using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;

namespace UnityAgentBridge.Editor
{
    [InitializeOnLoad]
    internal static class AssetRefreshService
    {
        private const string RefreshedPrefix = "refreshed:";
        private static readonly TimeSpan CompileGrace = TimeSpan.FromSeconds(0.5);
        // The client waits up to 360 s; a request it never acknowledged or collected is left behind by a broken call.
        private static readonly TimeSpan Abandoned = TimeSpan.FromSeconds(400);
        private static double nextCheck;
        // Requests are known in memory, so an idle editor does not touch the disk every frame.
        private static readonly HashSet<string> Scheduled = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        private const string CompileAfterPlayKey = "UnityAgentBridge.CompileAfterPlay";

        static AssetRefreshService()
        {
            EditorApplication.playModeStateChanged += CompileWhenEdited;
            var directory = Path.Combine(BridgePaths.RuntimeRoot, "Refresh");
            if (Directory.Exists(directory))
                Scheduled.UnionWith(Directory.GetFiles(directory, "*.pending"));
            EditorApplication.update += ProcessScheduled;
        }

        // compile during Play Mode: Unity's "Recompile After Finished Playing", so the running game is left alone.
        public static string CompileAfterPlay()
        {
            SessionState.SetBool(CompileAfterPlayKey, true);
            return "Play Mode is running: scripts compile when it stops; compile after play stop reports the errors.";
        }

        private static void CompileWhenEdited(PlayModeStateChange change)
        {
            if (change != PlayModeStateChange.EnteredEditMode || !SessionState.GetBool(CompileAfterPlayKey, false))
                return;
            SessionState.EraseBool(CompileAfterPlayKey);
            AssetDatabase.Refresh();
        }

        public static string Schedule(string requestId)
        {
            var directory = Path.Combine(BridgePaths.RuntimeRoot, "Refresh");
            Directory.CreateDirectory(directory);
            var pending = Path.Combine(directory, requestId + ".pending");
            File.WriteAllText(pending, "scheduled");
            Scheduled.Add(pending);
            return pending;
        }

        private static void ProcessScheduled()
        {
            // Ten checks a second are enough for a refresh request; every editor frame cost the game frame rate.
            if (Scheduled.Count == 0 || EditorApplication.isCompiling || EditorApplication.isUpdating || EditorApplication.timeSinceStartup < nextCheck)
                return;
            nextCheck = EditorApplication.timeSinceStartup + 0.1;
            // The client deletes a request once it has read the result.
            Scheduled.RemoveWhere(pending => !File.Exists(pending));
            foreach (var pending in Scheduled.OrderBy(item => item, StringComparer.Ordinal).ToList())
            {
                if (DateTime.UtcNow - File.GetLastWriteTimeUtc(pending) > Abandoned)
                {
                    try
                    {
                        File.Delete(pending);
                        Scheduled.Remove(pending);
                    }
                    catch (IOException)
                    {
                    }
                    continue;
                }
                string state;
                try
                {
                    state = File.ReadAllText(pending);
                }
                catch (FileNotFoundException)
                {
                    continue;
                }
                if (state == "running")
                {
                    TryWriteState(pending, "complete");
                    continue;
                }
                // Refresh returns before Unity starts compiling changed scripts; wait until it had the chance
                // to start and finish, so compile reports this build's errors instead of the previous ones.
                if (state.StartsWith(RefreshedPrefix, StringComparison.Ordinal))
                {
                    long ticks;
                    if (!long.TryParse(state.Substring(RefreshedPrefix.Length), out ticks) ||
                        !CompilationPending() && DateTime.UtcNow.Ticks - ticks > CompileGrace.Ticks)
                        TryWriteState(pending, "complete");
                    continue;
                }
                if (state != "acknowledged")
                    continue;
                try
                {
                    if (!TryWriteState(pending, "running"))
                        continue;
                    AssetDatabase.Refresh(ImportAssetOptions.ForceSynchronousImport | ImportAssetOptions.ForceUpdate);
                    TryWriteState(pending, RefreshedPrefix + DateTime.UtcNow.Ticks);
                }
                catch (Exception exception)
                {
                    TryWriteState(pending, "error:" + exception.Message);
                    UnityEngine.Debug.LogException(exception);
                }
                break;
            }
        }

        internal static bool CompilationPending()
        {
            var compilation = typeof(UnityEditor.Editor).Assembly.GetType("UnityEditor.Scripting.ScriptCompilation.EditorCompilationInterface");
            var pending = compilation == null ? null : compilation.GetMethod("IsCompilationPending",
                System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic, null, Type.EmptyTypes, null);
            return pending != null && (bool)pending.Invoke(null, null);
        }

        private static bool TryWriteState(string path, string state)
        {
            try
            {
                File.WriteAllText(path, state);
                return true;
            }
            catch (FileNotFoundException)
            {
                return false;
            }
            catch (DirectoryNotFoundException)
            {
                return false;
            }
        }
    }
}
