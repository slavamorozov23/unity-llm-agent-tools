using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using UnityEditor;
using UnityEngine;

namespace UnityAgentBridge.Editor
{
    // eval: an agent-written C# snippet, compiled by the bridge server with Unity's own Roslyn.
    internal static class EvalService
    {
        private const int MaxResultLength = 4000;

        [Serializable]
        private sealed class ContextData
        {
            public string dotnet;
            public string compiler;
            public string[] references;
        }

        public static string Context()
        {
            var data = EditorApplication.applicationContentsPath;
            var references = AppDomain.CurrentDomain.GetAssemblies()
                .Where(assembly => !assembly.IsDynamic && !string.IsNullOrEmpty(assembly.Location) && File.Exists(assembly.Location))
                .GroupBy(assembly => assembly.GetName().Name, StringComparer.OrdinalIgnoreCase)
                .Select(group => group.OrderByDescending(assembly => assembly.GetName().Version).First().Location)
                .ToArray();
            return JsonUtility.ToJson(new ContextData
            {
                dotnet = Path.Combine(data, "NetCoreRuntime", "dotnet.exe"),
                compiler = Path.Combine(data, "DotNetSdkRoslyn", "csc.dll"),
                references = references
            });
        }

        // Mono never unloads an assembly, so a snippet run again reuses the one already loaded.
        private static readonly Dictionary<string, Assembly> Loaded = new Dictionary<string, Assembly>(StringComparer.Ordinal);

        // An exception thrown by the snippet: its type, message and the frames down to the snippet line.
        internal sealed class EvalFailure : Exception
        {
            internal EvalFailure(Exception inner) : base(Describe(inner), inner)
            {
            }

            private static string Describe(Exception error)
            {
                var frames = (error.StackTrace ?? string.Empty).Split('\n').Select(line => line.Trim()).Where(line => line.Length > 0).ToList();
                var snippet = frames.FindIndex(line => line.Contains("UnityAgentBridgeEval.Run"));
                if (snippet >= 0)
                    frames = frames.Take(snippet + 1).ToList();
                var shown = frames.Skip(Math.Max(0, frames.Count - 4)).Select(line =>
                {
                    var snippetLine = System.Text.RegularExpressions.Regex.Match(line, @" in (?:.*[\\/])?eval:(\d+)");
                    return snippetLine.Success ? "eval:" + snippetLine.Groups[1].Value :System.Text.RegularExpressions.Regex.Replace(line, @" \[0x[0-9a-f]+\]| \(at <[0-9a-f]+>:0\)| in <[0-9a-f]+>:0", string.Empty);
                });
                return error.GetType().Name + ": " + error.Message + " | " + string.Join(" | ", shown.ToArray());
            }
        }

        public static string Run(string assemblyPath)
        {
            var bytes = File.ReadAllBytes(assemblyPath);
            string hash;
            using (var sha = System.Security.Cryptography.SHA256.Create())
                hash = Convert.ToBase64String(sha.ComputeHash(bytes));
            Assembly assembly;
            if (!Loaded.TryGetValue(hash, out assembly))
            {
                // The pdb gives exceptions their snippet line (#line "eval").
                var symbols = Path.ChangeExtension(assemblyPath, ".pdb");
                Loaded[hash] = assembly = File.Exists(symbols) ? Assembly.Load(bytes, File.ReadAllBytes(symbols)) : Assembly.Load(bytes);
            }
            var method = assembly.GetType("UnityAgentBridgeEval", true).GetMethod("Run", BindingFlags.Public | BindingFlags.Static);
            Undo.IncrementCurrentGroup();
            var group = Undo.GetCurrentGroup();
            Undo.SetCurrentGroupName("Unity Agent Bridge: eval");
            object value;
            try
            {
                value = method.Invoke(null, null);
            }
            catch (TargetInvocationException error)
            {
                throw new EvalFailure(error.InnerException ?? error);
            }
            finally
            {
                Undo.CollapseUndoOperations(group);
            }
            var text = Format(value, true);
            return text.Length > MaxResultLength ? text.Substring(0, MaxResultLength) + "…" : text;
        }

        private static string Format(object value, bool top)
        {
            switch (value)
            {
                case null:
                    return "null";
                case string text:
                    return text;
                case GameObject gameObject:
                    return Reference(gameObject);
                case Component component:
                    return Reference(component.gameObject) + "#" + component.GetType().Name;
                case UnityEngine.Object asset:
                    return AssetDatabase.Contains(asset) ? AssetDatabase.GetAssetPath(asset) : asset.name;
                case IFormattable formattable:
                    return formattable.ToString(null, CultureInfo.InvariantCulture);
                case IEnumerable items when top:
                    var list = items.Cast<object>().Take(51).Select(item => Format(item, false)).ToList();
                    return "[" + string.Join(", ", list.Take(50)) + (list.Count > 50 ? ", …" : string.Empty) + "]";
                default:
                    return value.ToString();
            }
        }

        private static string Reference(GameObject gameObject)
        {
            return gameObject.scene.IsValid() ? ScenePath.For(gameObject) : AssetDatabase.GetAssetPath(gameObject);
        }
    }
}
