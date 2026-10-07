using System;
using System.Linq;
using System.Reflection;
using UnityEditor;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace UnityAgentBridge.Editor
{
    internal static class LightingService
    {
        public static string Execute(string action)
        {
            ComponentService.EnsureEditMode();
            EditorPresentationService.ShowWindow("UnityEditor.LightingWindow");
            switch (action)
            {
                case "bake":
                    if (Lightmapping.isRunning)
                        return "Lighting is already baking.";
                    RequireProbeVolume();
                    if (!Lightmapping.BakeAsync())
                        throw new InvalidOperationException("Unity could not start the lighting bake.");
                    Lightmapping.bakeCompleted -= SaveBakedScenes;
                    Lightmapping.bakeCompleted += SaveBakedScenes;
                    return "Lighting bake started.";
                case "cancel":
                    if (!Lightmapping.isRunning)
                        return "Lighting is not baking.";
                    Lightmapping.Cancel();
                    return "Lighting bake cancelled.";
                case "clear":
                    Lightmapping.Clear();
                    Lightmapping.ClearLightingDataAsset();
                    return "Baked lighting cleared.";
                default:
                    throw new ArgumentException("Lighting action must be bake, cancel or clear.", "action");
            }
        }

        private static void SaveBakedScenes()
        {
            Lightmapping.bakeCompleted -= SaveBakedScenes;
            ScenePersistenceService.SaveOpenSceneChangesNow();
        }

        // Unity asks about a missing Adaptive Probe Volume in a modal dialog that blocks the editor.
        private static void RequireProbeVolume()
        {
            var referenceType = Type.GetType("UnityEngine.Rendering.ProbeReferenceVolume, Unity.RenderPipelines.Core.Runtime");
            var volumeType = Type.GetType("UnityEngine.Rendering.ProbeVolume, Unity.RenderPipelines.Core.Runtime");
            var reference = referenceType?.GetProperty("instance", BindingFlags.Static | BindingFlags.Public)?.GetValue(null);
            if (reference == null || volumeType == null)
                return;
            const BindingFlags flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
            if (!(referenceType.GetProperty("isInitialized", flags)?.GetValue(reference) is bool initialized && initialized) ||
                !(referenceType.GetProperty("enabledBySRP", flags)?.GetValue(reference) is bool enabled && enabled))
                return;
            var scene = SceneManager.GetActiveScene();
            if (UnityEngine.Object.FindObjectsByType(volumeType, FindObjectsInactive.Exclude)
                .OfType<Behaviour>().Any(volume => volume.isActiveAndEnabled && volume.gameObject.scene == scene))
                return;
            throw new InvalidOperationException("Adaptive Probe Volumes are enabled for this project, but none exist in the active scene. Add an Adaptive Probe Volume before baking.");
        }
    }
}
