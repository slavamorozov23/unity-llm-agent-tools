using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace UnityAgentBridge.Editor
{
    // "Occlusion Culling" is Window > Rendering > Occlusion Culling: the Bake tab's parameters by their labels and its
    // Bake, Clear and Cancel buttons, for the open scenes.
    internal sealed class OcclusionCullingView : AssetView
    {
        internal const string WindowPath = "Occlusion Culling";
        private static readonly string[] Labels = { "Smallest Occluder", "Smallest Hole", "Backface Threshold" };

        internal static bool IsPath(string path)
        {
            return string.Equals(path, WindowPath, StringComparison.OrdinalIgnoreCase);
        }

        internal override bool Handles(string path, UnityEngine.Object asset)
        {
            return IsPath(path);
        }

        internal override JsonText Describe(string path, UnityEngine.Object asset, string property)
        {
            Open();
            var data = StaticOcclusionCulling.umbraDataSize;
            var result = new JsonText()
                .Add("Smallest Occluder", Round(StaticOcclusionCulling.smallestOccluder))
                .Add("Smallest Hole", Round(StaticOcclusionCulling.smallestHole))
                .Add("Backface Threshold", Round(StaticOcclusionCulling.backfaceThreshold))
                .Add("Baked Data", StaticOcclusionCulling.isRunning ? "baking" : data > 0 ? Size(data) : "none");
            if (string.IsNullOrEmpty(property))
                return result;
            var label = Labels.Concat(new[] { "Baked Data" }).FirstOrDefault(item => AssetViews.KeyIs(property, item));
            if (label == null)
                throw new ArgumentException("Occlusion Culling setting was not found: " + property + "; asset-info without --property lists them.");
            return new JsonText().Add(label, result.Items.First(item => item.Key == label).Value);
        }

        internal override PropertyValue[] Modify(string path, UnityEngine.Object asset, PropertyValue[] values, bool confirm, List<string> changes)
        {
            ComponentService.EnsureEditMode();
            Open();
            foreach (var entry in values)
            {
                var label = Labels.FirstOrDefault(item => AssetViews.KeyIs(entry.path, item));
                if (label == null)
                    throw new ArgumentException("Occlusion Culling setting was not found: " + entry.path + ". Settings: " + string.Join(", ", Labels));
                var value = float.Parse(AssetViews.Text(entry.value), NumberStyles.Float, CultureInfo.InvariantCulture);
                // The window's own limits.
                switch (label)
                {
                    case "Smallest Occluder":
                        StaticOcclusionCulling.smallestOccluder = Mathf.Max(0f, value);
                        break;
                    case "Smallest Hole":
                        StaticOcclusionCulling.smallestHole = Mathf.Max(0f, value);
                        break;
                    default:
                        StaticOcclusionCulling.backfaceThreshold = Mathf.Clamp(value, 5f, 100f);
                        break;
                }
                changes.Add(label + " = " + Describe(path, asset, label).Items.First().Value);
            }
            for (var index = 0; index < SceneManager.sceneCount; index++)
                ScenePersistenceService.MarkDirty(SceneManager.GetSceneAt(index));
            return Array.Empty<PropertyValue>();
        }

        internal override string[] Actions(string path, UnityEngine.Object asset)
        {
            return StaticOcclusionCulling.isRunning ? new[] { "Cancel" } : new[] { "Bake", "Clear" };
        }

        internal override string Execute(string path, UnityEngine.Object asset, string action, PropertyValue[] values)
        {
            ComponentService.EnsureEditMode();
            Open();
            switch (AssetViews.Normalize(action))
            {
                case "bake":
                    if (StaticOcclusionCulling.isRunning)
                        return "Occlusion culling is already baking.";
                    // The window bakes an empty result without a word when nothing is Occluder Static.
                    if (!UnityEngine.Object.FindObjectsByType<Renderer>(FindObjectsInactive.Exclude).Any(renderer =>
                            (GameObjectUtility.GetStaticEditorFlags(renderer.gameObject) & StaticEditorFlags.OccluderStatic) != 0))
                        throw new InvalidOperationException("No renderer in the open scenes is Occluder Static; set it with object-static.");
                    if (!StaticOcclusionCulling.GenerateInBackground())
                        throw new InvalidOperationException("Unity could not start the occlusion culling bake.");
                    EditorApplication.update -= SaveWhenBaked;
                    EditorApplication.update += SaveWhenBaked;
                    return "Occlusion culling bake started.";
                case "clear":
                    StaticOcclusionCulling.Clear();
                    EditorSceneManager.SaveOpenScenes();
                    return "Occlusion culling data cleared.";
                case "cancel":
                    if (!StaticOcclusionCulling.isRunning)
                        return "Occlusion culling is not baking.";
                    StaticOcclusionCulling.Cancel();
                    return "Occlusion culling bake cancelled.";
                default:
                    throw new InvalidOperationException("Occlusion Culling actions: bake, clear, cancel.");
            }
        }

        // The baked data is referenced by the scene, as after the window's Bake the developer saves it.
        private static void SaveWhenBaked()
        {
            if (StaticOcclusionCulling.isRunning)
                return;
            EditorApplication.update -= SaveWhenBaked;
            ScenePersistenceService.SaveOpenSceneChangesNow();
        }

        private static void Open()
        {
            EditorPresentationService.ShowWindow("UnityEditor.OcclusionCullingWindow");
        }

        private static float Round(float value)
        {
            return (float)Math.Round(value, 4);
        }

        private static string Size(int bytes)
        {
            return bytes >= 1 << 20 ? (bytes / (float)(1 << 20)).ToString("0.#", CultureInfo.InvariantCulture) + " MB"
                : bytes >= 1 << 10 ? (bytes / 1024f).ToString("0.#", CultureInfo.InvariantCulture) + " KB" : bytes + " B";
        }
    }
}
