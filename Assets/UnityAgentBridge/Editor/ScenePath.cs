using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace UnityAgentBridge.Editor
{
    internal static class ScenePath
    {
        public static string For(GameObject gameObject)
        {
            if (gameObject == null || !gameObject.scene.IsValid())
                throw new InvalidOperationException("Object does not belong to a loaded scene.");

            var segments = new Stack<string>();
            var current = gameObject.transform;
            while (current != null)
            {
                segments.Push(EncodeSegment(current.name, SameNameIndex(current)));
                current = current.parent;
            }

            return SceneSegment(gameObject.scene) + "/" + string.Join("/", segments.ToArray());
        }

        public static string ParentOf(GameObject gameObject)
        {
            return gameObject.transform.parent == null
                ? SceneSegment(gameObject.scene)
                : For(gameObject.transform.parent.gameObject);
        }

        public static GameObject ResolveObject(string path)
        {
            var segments = Segments(path);
            if (segments.Length == 0)
                throw new ArgumentException("Scene object path is required.", "path");

            GameObject found;
            string failure = null;
            var depth = -1;
            Scene scene;
            if (segments.Length > 1 && TryScene(segments[0], out scene))
            {
                string sceneFailure;
                int sceneDepth;
                if (TryWalk(scene, segments, 1, out found, out sceneFailure, out sceneDepth))
                    return found;
                failure = sceneFailure;
                depth = sceneDepth;
            }

            var matches = new List<GameObject>();
            foreach (var candidate in ContextScenes())
            {
                string candidateFailure;
                int candidateDepth;
                if (TryWalk(candidate, segments, 0, out found, out candidateFailure, out candidateDepth))
                    matches.Add(found);
                else if (candidateDepth > depth)
                {
                    failure = candidateFailure;
                    depth = candidateDepth;
                }
            }
            if (matches.Count == 1)
                return matches[0];
            if (matches.Count > 1)
                throw new InvalidOperationException("Scene path is ambiguous; prefix the scene: " +
                    string.Join(", ", matches.Select(For).ToArray()));
            if (segments.Length == 1 && TryScene(segments[0], out scene))
            {
                var roots = scene.GetRootGameObjects().Select(item => item.name).ToArray();
                throw new InvalidOperationException(Display(path) + " is a scene, not an object; its roots: " +
                    string.Join(", ", roots.Take(5).ToArray()) + (roots.Length > 5 ? ", … (" + roots.Length + ")" : string.Empty));
            }
            throw new InvalidOperationException("Scene path was not found: " + Display(path) + ". " + (failure ?? "No scene is loaded."));
        }

        public static Transform ResolveParent(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
                throw new ArgumentException("Destination path is required.", "path");
            var segments = Segments(path);
            Scene scene;
            if (segments.Length == 1 && TryScene(segments[0], out scene))
                return null;
            return ResolveObject(path).transform;
        }

        public static Scene ResolveDestinationScene(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
                throw new ArgumentException("Destination path is required.", "path");
            var segments = Segments(path);
            if (segments.Length == 0)
                throw new ArgumentException("Destination path must contain a scene or an object.", "path");
            Scene scene;
            if (segments.Length == 1 && TryScene(segments[0], out scene))
                return scene;
            return ResolveObject(path).scene;
        }

        public static string DestinationObjectName(string path, out int sameNameIndex)
        {
            var segments = Segments(path);
            if (segments.Length < 2)
                throw new ArgumentException("Destination path must contain a parent and an object.", "path");

            string name;
            int? index;
            DecodeSegment(segments[segments.Length - 1], out name, out index);
            sameNameIndex = index ?? -1;
            return name;
        }

        public static string DestinationParentPath(string path)
        {
            var segments = Segments(path);
            if (segments.Length < 2)
                throw new ArgumentException("Destination path must contain a parent and an object.", "path");
            return "/" + string.Join("/", segments.Take(segments.Length - 1).ToArray());
        }

        internal static IEnumerable<Scene> ContextScenes()
        {
            var prefabStage = PrefabStageUtility.GetCurrentPrefabStage();
            if (prefabStage != null)
            {
                yield return prefabStage.scene;
                yield break;
            }
            var active = SceneManager.GetActiveScene();
            if (active.IsValid() && active.isLoaded)
                yield return active;
            for (var index = 0; index < SceneManager.sceneCount; index++)
            {
                var scene = SceneManager.GetSceneAt(index);
                if (scene.isLoaded && scene != active)
                    yield return scene;
            }
        }

        private static string[] Segments(string path)
        {
            return (path ?? string.Empty).Split(new[] { '/' }, StringSplitOptions.RemoveEmptyEntries);
        }

        private static bool TryScene(string segment, out Scene scene)
        {
            string name;
            int? index;
            DecodeSegment(segment, out name, out index);
            var matches = LoadedScenesInOrder().Where(item => item.name == name).ToArray();
            scene = default(Scene);
            if (matches.Length == 0 || (index.HasValue && index.Value >= matches.Length))
                return false;
            if (!index.HasValue && matches.Length > 1)
                throw new InvalidOperationException("Loaded scene name is ambiguous; use its index: " + name + "[0]");
            scene = matches[index ?? 0];
            return true;
        }

        private static bool TryWalk(Scene scene, string[] segments, int start, out GameObject found, out string failure, out int depth)
        {
            found = null;
            failure = null;
            depth = 0;
            Transform current = null;
            IEnumerable<Transform> children = scene.GetRootGameObjects().Select(item => item.transform);
            for (var index = start; index < segments.Length; index++)
            {
                string name;
                int? sameNameIndex;
                DecodeSegment(segments[index], out name, out sameNameIndex);
                var siblings = children.ToArray();
                var matching = siblings.Where(item => item.name == name).ToArray();
                if (!sameNameIndex.HasValue && matching.Length > 1)
                    throw new InvalidOperationException("Scene path segment is ambiguous; use its index: " + name + "[0].." + name + "[" + (matching.Length - 1) + "]");
                var next = matching.ElementAtOrDefault(sameNameIndex ?? 0);
                if (next == null)
                {
                    var parent = current == null ? SceneSegment(scene) : For(current.gameObject);
                    failure = "Nearest: " + Display(parent) + (siblings.Length == 0 ? " (no children)" : "; children: " + Nearest(name, siblings));
                    depth = index - start;
                    return false;
                }
                current = next;
                children = Enumerable.Range(0, current.childCount).Select(current.GetChild);
            }
            found = current.gameObject;
            return true;
        }

        private static string Nearest(string name, Transform[] siblings)
        {
            var wanted = name.ToLowerInvariant();
            var names = siblings.Select(item => item.name).Distinct().OrderBy(item =>
            {
                var candidate = item.ToLowerInvariant();
                if (candidate == wanted) return 0;
                if (candidate.Contains(wanted) || wanted.Contains(candidate)) return 1 + Math.Abs(candidate.Length - wanted.Length) / 100f;
                return 2 + Distance(candidate, wanted) / 100f;
            }).ToArray();
            return string.Join(", ", names.Take(5).ToArray()) + (names.Length > 5 ? ", … (" + names.Length + ")" : string.Empty);
        }

        private static int Distance(string left, string right)
        {
            var previous = Enumerable.Range(0, right.Length + 1).ToArray();
            for (var i = 1; i <= left.Length; i++)
            {
                var current = new int[right.Length + 1];
                current[0] = i;
                for (var j = 1; j <= right.Length; j++)
                    current[j] = Math.Min(Math.Min(current[j - 1] + 1, previous[j] + 1), previous[j - 1] + (left[i - 1] == right[j - 1] ? 0 : 1));
                previous = current;
            }
            return previous[right.Length];
        }

        private static int SameNameIndex(Transform transform)
        {
            IEnumerable<Transform> siblings = transform.parent == null
                ? transform.gameObject.scene.GetRootGameObjects().Select(item => item.transform)
                : Enumerable.Range(0, transform.parent.childCount).Select(transform.parent.GetChild);
            var index = 0;
            var position = -1;
            foreach (var sibling in siblings)
            {
                if (sibling.name != transform.name)
                    continue;
                if (sibling == transform)
                    position = index;
                index++;
            }
            if (position < 0)
                throw new InvalidOperationException("Object is not present in its hierarchy.");
            return index > 1 ? position : -1;
        }

        private static string SceneSegment(Scene scene)
        {
            var same = LoadedScenesInOrder().Where(item => item.name == scene.name).ToList();
            var index = same.IndexOf(scene);
            // A prefab's contents loaded without Prefab Mode live in a preview scene; their paths are rewritten by the caller.
            if (index < 0 && PrefabStageUtility.GetCurrentPrefabStage() == null && !EditorSceneManager.IsPreviewScene(scene))
                throw new InvalidOperationException("Scene is not loaded.");
            return "/" + EncodeSegment(scene.name, same.Count > 1 ? index : -1);
        }

        private static IEnumerable<Scene> LoadedScenesInOrder()
        {
            var prefabStage = PrefabStageUtility.GetCurrentPrefabStage();
            if (prefabStage != null)
            {
                yield return prefabStage.scene;
                yield break;
            }
            for (var index = 0; index < SceneManager.sceneCount; index++)
            {
                var scene = SceneManager.GetSceneAt(index);
                if (scene.isLoaded)
                    yield return scene;
            }
        }

        private static string Display(string path)
        {
            return Uri.UnescapeDataString(path ?? string.Empty);
        }

        private static string EncodeSegment(string name, int sameNameIndex)
        {
            var builder = new StringBuilder(name.Length + 4);
            foreach (var character in name)
            {
                switch (character)
                {
                    case '%': builder.Append("%25"); break;
                    case '/': builder.Append("%2F"); break;
                    case '#': builder.Append("%23"); break;
                    case '[': builder.Append("%5B"); break;
                    case ']': builder.Append("%5D"); break;
                    default: builder.Append(character); break;
                }
            }
            if (sameNameIndex >= 0)
                builder.Append('[').Append(sameNameIndex).Append(']');
            return builder.ToString();
        }

        private static void DecodeSegment(string segment, out string name, out int? sameNameIndex)
        {
            var bracket = segment.LastIndexOf('[');
            if (bracket <= 0 || !segment.EndsWith("]", StringComparison.Ordinal))
            {
                name = Uri.UnescapeDataString(segment);
                sameNameIndex = null;
                return;
            }
            int parsed;
            name = Uri.UnescapeDataString(segment.Substring(0, bracket));
            if (!int.TryParse(segment.Substring(bracket + 1, segment.Length - bracket - 2), out parsed) || parsed < 0)
                throw new ArgumentException("Invalid same-name index in path segment: " + segment);
            sameNameIndex = parsed;
        }
    }
}
