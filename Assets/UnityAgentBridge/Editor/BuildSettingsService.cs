using System;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEngine;

namespace UnityAgentBridge.Editor
{
    internal static class BuildSettingsService
    {
        [Serializable]
        private sealed class BuildSceneData
        {
            public int index;
            public string path;
            public bool enabled;
        }

        [Serializable]
        private sealed class BuildSceneList
        {
            public BuildSceneData[] scenes;
        }

        public static string List()
        {
            return JsonUtility.ToJson(new BuildSceneList
            {
                scenes = EditorBuildSettings.scenes.Select((scene, index) => new BuildSceneData
                {
                    index = index,
                    path = scene.path,
                    enabled = scene.enabled
                }).ToArray()
            });
        }

        public static string Mutate(string action, string path, int index)
        {
            action = (action ?? string.Empty).ToLowerInvariant();
            path = NormalizeScenePath(path);
            var scenes = EditorBuildSettings.scenes.ToList();
            var current = scenes.FindIndex(scene => string.Equals(scene.path, path, StringComparison.OrdinalIgnoreCase));
            switch (action)
            {
                case "add":
                    var added = current >= 0 ? scenes[current] : new EditorBuildSettingsScene(path, true);
                    if (current >= 0)
                        scenes.RemoveAt(current);
                    scenes.Insert(ClampIndex(index, scenes.Count), added);
                    break;
                case "remove":
                    RequireExisting(current, path);
                    scenes.RemoveAt(current);
                    break;
                case "enable":
                case "disable":
                    RequireExisting(current, path);
                    scenes[current] = new EditorBuildSettingsScene(path, action == "enable");
                    break;
                case "move":
                    RequireExisting(current, path);
                    var moved = scenes[current];
                    scenes.RemoveAt(current);
                    scenes.Insert(ClampIndex(index, scenes.Count), moved);
                    break;
                default:
                    throw new ArgumentException("Build scene action must be add, remove, enable, disable or move.", "action");
            }
            EditorBuildSettings.scenes = scenes.ToArray();
            return List();
        }

        private static string NormalizeScenePath(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
                throw new ArgumentException("Scene asset path is required.", "path");
            path = path.Replace('\\', '/');
            if (!path.StartsWith("Assets/", StringComparison.OrdinalIgnoreCase) ||
                !path.EndsWith(".unity", StringComparison.OrdinalIgnoreCase) ||
                AssetDatabase.LoadAssetAtPath<SceneAsset>(path) == null)
                throw new FileNotFoundException("Unity scene asset was not found.", path);
            return path;
        }

        private static int ClampIndex(int index, int count)
        {
            return index < 0 ? count : Mathf.Clamp(index, 0, count);
        }

        private static void RequireExisting(int index, string path)
        {
            if (index < 0)
                throw new InvalidOperationException("Scene is not in Build Settings: " + path);
        }
    }
}
