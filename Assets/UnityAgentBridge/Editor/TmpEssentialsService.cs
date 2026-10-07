using System;
using System.Linq;
using System.Reflection;
using UnityEditor;
using UnityEngine;

namespace UnityAgentBridge.Editor
{
    // The first TMP use opens the "TMP Importer" window; after an agent command the bridge presses
    // Import TMP Essentials itself and closes the window, as the developer would.
    [InitializeOnLoad]
    internal static class TmpEssentialsService
    {
        private const string ImporterWindow = "TMPro.TMP_PackageResourceImporterWindow";
        private static double watchUntil;

        static TmpEssentialsService()
        {
            EditorApplication.update += Check;
        }

        internal static void WatchAfterCommand()
        {
            watchUntil = EditorApplication.timeSinceStartup + 3.0;
        }

        internal static bool Imported
        {
            get { return AssetDatabase.LoadMainAssetAtPath("Assets/TextMesh Pro/Resources/TMP Settings.asset") != null; }
        }

        // ImportPackage finishes on later editor updates; one request per session keeps it from being queued again.
        internal static void Import(bool essentials)
        {
            var key = "UnityAgentBridge.TmpImport." + (essentials ? "Essentials" : "Extras");
            CloseImporterWindows();
            if (SessionState.GetBool(key, false))
                return;
            SessionState.SetBool(key, true);
            var importer = AppDomain.CurrentDomain.GetAssemblies().Select(assembly => assembly.GetType("TMPro.TMP_PackageResourceImporter", false))
                .FirstOrDefault(type => type != null);
            if (importer == null)
                throw new InvalidOperationException("TextMesh Pro is not installed (com.unity.ugui).");
            importer.GetMethod("ImportResources", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)
                .Invoke(null, new object[] { essentials, !essentials, false });
        }

        private static void Check()
        {
            if (watchUntil <= 0 || EditorApplication.timeSinceStartup > watchUntil)
                return;
            var windows = ImporterWindows();
            if (windows.Length == 0)
                return;
            watchUntil = 0;
            try
            {
                if (Imported)
                    CloseImporterWindows();
                else
                    Import(true);
            }
            catch (Exception exception)
            {
                Debug.LogWarning("Unity Agent Bridge could not import TMP Essential Resources: " + exception.Message);
            }
        }

        private static EditorWindow[] ImporterWindows()
        {
            return Resources.FindObjectsOfTypeAll<EditorWindow>().Where(window => window.GetType().FullName == ImporterWindow).ToArray();
        }

        private static void CloseImporterWindows()
        {
            foreach (var window in ImporterWindows())
                window.Close();
        }
    }
}
