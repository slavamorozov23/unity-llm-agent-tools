using System;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditorInternal;
using UnityEngine;

namespace UnityAgentBridge.Editor
{
    // The windows the agent opens (Profiler, Timeline, Search, Lighting…) are tabs of this layout, so they
    // open in place instead of floating over the editor. Applied once per editor session when the bridge starts.
    [InitializeOnLoad]
    internal static class EditorLayoutService
    {
        private const string LayoutName = "AllInOne-PluginLayout";
        private const string AppliedKey = "UnityAgentBridge.LayoutApplied";

        // HDRP brings an open Wizard to the front after every script reload (GetWindow in HDWizard.InitializeEntryList),
        // over the window being worked in, a maximized Shader Graph too. The Wizard closes first (it also takes focus
        // itself, so focus says nothing about the user); Window > Rendering > HDRP Wizard opens it again.
        static EditorLayoutService()
        {
            AssemblyReloadEvents.beforeAssemblyReload += () =>
            {
                foreach (var window in Resources.FindObjectsOfTypeAll<EditorWindow>().Where(item => item.GetType().FullName == "UnityEditor.Rendering.HighDefinition.HDWizard"))
                    window.Close();
            };
        }

        internal static void ApplyOnce(string assetRoot)
        {
            if (SessionState.GetBool(AppliedKey, false))
                return;
            SessionState.SetBool(AppliedKey, true);
            var folder = Path.Combine(InternalEditorUtility.unityPreferencesFolder, "Layouts", "default");
            var layout = Path.Combine(folder, LayoutName + ".wlt");
            try
            {
                // Import into Window > Layouts; a copy the user already has (and may have changed) wins.
                var bundled = Path.Combine(assetRoot, "Layout~", LayoutName + ".wlt");
                if (!File.Exists(layout) && File.Exists(bundled))
                {
                    Directory.CreateDirectory(folder);
                    File.Copy(bundled, layout);
                }
            }
            catch (Exception exception)
            {
                UnityEngine.Debug.LogWarning("Unity Agent Bridge could not import its window layout: " + exception.Message);
                return;
            }
            if (!File.Exists(layout))
                return;
            EditorPresentationService.NextUpdate(() =>
            {
                if (!EditorApplication.isPlayingOrWillChangePlaymode)
                    EditorUtility.LoadWindowLayout(layout);
            });
        }
    }
}
