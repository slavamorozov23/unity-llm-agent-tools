using System;
using System.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEditorInternal;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace UnityAgentBridge.Editor
{
    internal static class ObjectService
    {
        public static void Delete(string path)
        {
            var gameObject = ScenePath.ResolveObject(path);
            EditorPresentationService.ShowSceneObject(gameObject);
            var scene = gameObject.scene;
            Undo.DestroyObjectImmediate(gameObject);
            ScenePersistenceService.MarkDirty(scene);
        }

        public static SceneObjectData Duplicate(string path, string name)
        {
            var source = ScenePath.ResolveObject(path);
            var copy = UnityEngine.Object.Instantiate(source, source.transform.parent);
            copy.name = string.IsNullOrWhiteSpace(name) ? source.name : name.Trim();
            if (source.transform.parent == null && copy.scene != source.scene)
                SceneManager.MoveGameObjectToScene(copy, source.scene);
            Undo.RegisterCreatedObjectUndo(copy, "Unity Agent Bridge: Duplicate Object");
            ScenePersistenceService.MarkDirty(copy.scene);
            EditorPresentationService.ShowSceneObject(copy);
            return SceneService.GetObjectInfo(ScenePath.For(copy));
        }

        public static PrefabData[] ListPrefabs(string folderPath = null)
        {
            var folder = string.IsNullOrWhiteSpace(folderPath) ? BridgePaths.StandardPrefabFolder : folderPath.Trim().Replace('\\', '/').TrimEnd('/');
            if (!string.Equals(folder, "Assets", StringComparison.Ordinal) && !folder.StartsWith("Assets/", StringComparison.Ordinal))
                throw new InvalidOperationException("Prefab folder must be inside Assets: " + folder);
            if (!AssetDatabase.IsValidFolder(folder))
                return Array.Empty<PrefabData>();
            EditorPresentationService.ShowAssetFolder(folder);

            return AssetDatabase.FindAssets("t:Prefab", new[] { folder })
                .Select(AssetDatabase.GUIDToAssetPath)
                .OrderBy(path => path, StringComparer.Ordinal)
                .Select(path => PrefabService.DescribeAsset(path))
                .ToArray();
        }

        public static SceneObjectData Move(string path, string destinationPath, int siblingIndex = -1)
        {
            var gameObject = ScenePath.ResolveObject(path);
            var destinationScene = ScenePath.ResolveDestinationScene(destinationPath);
            var newParent = ScenePath.ResolveParent(destinationPath);
            if (newParent != null && (newParent == gameObject.transform || newParent.IsChildOf(gameObject.transform)))
                throw new InvalidOperationException("An object cannot be moved below itself or one of its descendants.");

            Undo.SetTransformParent(gameObject.transform, newParent, "Unity Agent Bridge: Move Object");
            if (newParent == null && gameObject.scene != destinationScene)
                SceneManager.MoveGameObjectToScene(gameObject, destinationScene);
            if (siblingIndex >= 0)
            {
                var siblingCount = newParent == null
                    ? gameObject.scene.rootCount
                    : newParent.childCount;
                if (siblingIndex >= siblingCount)
                    throw new ArgumentOutOfRangeException("siblingIndex", "Sibling index must be between 0 and " + (siblingCount - 1) + ".");
                gameObject.transform.SetSiblingIndex(siblingIndex);
            }
            ScenePersistenceService.MarkDirty(gameObject.scene);
            return SceneService.GetObjectInfo(ScenePath.For(gameObject));
        }

        public static SceneObjectData Rename(string path, string destinationPath)
        {
            var gameObject = ScenePath.ResolveObject(path);
            var nameOnly = !string.IsNullOrWhiteSpace(destinationPath) && destinationPath.IndexOf('/') < 0;
            int requestedIndex = -1;
            var newName = nameOnly
                ? destinationPath
                : ScenePath.DestinationObjectName(destinationPath, out requestedIndex);
            if (string.IsNullOrWhiteSpace(newName))
                throw new ArgumentException("New object name cannot be empty.", "newName");

            if (!nameOnly)
            {
                var destinationParentPath = ScenePath.DestinationParentPath(destinationPath);
                var destinationParent = ScenePath.ResolveParent(destinationParentPath);
                var destinationScene = ScenePath.ResolveDestinationScene(destinationParentPath);
                if (destinationParent != gameObject.transform.parent || destinationScene != gameObject.scene)
                    throw new ArgumentException("Rename destination must keep the current parent. Use object-move separately.", "destinationPath");
            }

            System.Collections.Generic.IEnumerable<Transform> siblings;
            if (gameObject.transform.parent == null)
                siblings = gameObject.scene.GetRootGameObjects().Select(item => item.transform);
            else
                siblings = Enumerable.Range(0, gameObject.transform.parent.childCount).Select(gameObject.transform.parent.GetChild);
            var resultingIndex = siblings.TakeWhile(item => item != gameObject.transform).Count(item => item.name == newName);
            if (!nameOnly && requestedIndex >= 0 && resultingIndex != requestedIndex)
                throw new ArgumentException("Destination path has the wrong same-name index. Expected [" + resultingIndex + "].", "destinationPath");

            Undo.RecordObject(gameObject, "Unity Agent Bridge: Rename Object");
            gameObject.name = newName;
            EditorUtility.SetDirty(gameObject);
            if (PrefabUtility.IsPartOfPrefabInstance(gameObject))
                PrefabUtility.RecordPrefabInstancePropertyModifications(gameObject);
            ScenePersistenceService.MarkDirty(gameObject.scene);
            if (gameObject.name != newName)
                throw new InvalidOperationException("Unity did not retain the new object name.");
            return SceneService.GetObjectInfo(ScenePath.For(gameObject));
        }

        public static SceneObjectData SetActive(string path, bool active)
        {
            var gameObject = ScenePath.ResolveObject(path);
            if (!EditorApplication.isPlayingOrWillChangePlaymode)
                Undo.RecordObject(gameObject, "Unity Agent Bridge: Set Active");
            gameObject.SetActive(active);
            if (!EditorApplication.isPlayingOrWillChangePlaymode)
            {
                EditorUtility.SetDirty(gameObject);
                if (PrefabUtility.IsPartOfPrefabInstance(gameObject))
                    PrefabUtility.RecordPrefabInstancePropertyModifications(gameObject);
                ScenePersistenceService.MarkDirty(gameObject.scene);
            }
            EditorPresentationService.ShowSceneObject(gameObject);
            return SceneService.GetObjectInfo(ScenePath.For(gameObject));
        }

        public static string SetTag(string path, string tag)
        {
            if (string.IsNullOrWhiteSpace(tag) || tag.Any(char.IsControl))
                throw new ArgumentException("Tag must be a non-empty name without control characters.", "tag");

            if (path.StartsWith("Assets/", StringComparison.Ordinal))
            {
                if (!path.EndsWith(".prefab", StringComparison.OrdinalIgnoreCase) ||
                    AssetDatabase.LoadAssetAtPath<GameObject>(path) == null)
                    throw new InvalidOperationException("Prefab root was not found in Assets: " + path);

                var root = PrefabUtility.LoadPrefabContents(path);
                try
                {
                    EnsureTagExists(tag);
                    root.tag = tag;
                    if (PrefabUtility.SaveAsPrefabAsset(root, path) == null)
                        throw new InvalidOperationException("Unity could not save the prefab tag: " + path);
                }
                finally
                {
                    PrefabUtility.UnloadPrefabContents(root);
                }
                EditorPresentationService.ShowAssetPath(path);
                return tag;
            }

            var gameObject = ScenePath.ResolveObject(path);
            EnsureTagExists(tag);
            Undo.RecordObject(gameObject, "Unity Agent Bridge: Set Tag");
            gameObject.tag = tag;
            EditorUtility.SetDirty(gameObject);
            if (PrefabUtility.IsPartOfPrefabInstance(gameObject))
                PrefabUtility.RecordPrefabInstancePropertyModifications(gameObject);
            ScenePersistenceService.MarkDirty(gameObject.scene);
            EditorPresentationService.ShowSceneObject(gameObject);
            return gameObject.tag;
        }

        // Layer and Static in the GameObject header; children=true is the "Yes, change children" answer.
        public static string SetLayer(string path, string layer, bool children)
        {
            var index = LayerIndex(layer);
            var count = EditHeader(path, children, "Set Layer", item => item.layer = index);
            return index + " " + LayerMask.LayerToName(index) + ChildrenNote(count);
        }

        public static string SetStatic(string path, string value, bool children)
        {
            var flags = StaticFlags(value);
            var count = EditHeader(path, children, "Set Static", item => GameObjectUtility.SetStaticEditorFlags(item, flags));
            var text = StaticText(flags);
            return (text.Length == 0 ? "Nothing" : text) + ChildrenNote(count);
        }

        internal static string StaticText(StaticEditorFlags flags)
        {
            if (flags == 0)
                return string.Empty;
            var known = StaticFlagNames();
            if (known.All(name => (flags & Flag(name)) != 0))
                return "Everything";
            return string.Join(", ", known.Where(name => (flags & Flag(name)) != 0).Select(ObjectNames.NicifyVariableName).ToArray());
        }

        // The Static dropdown's entries: current names only (Lightmap Static is the old name of Contribute GI).
        private static string[] StaticFlagNames()
        {
            return Enum.GetNames(typeof(StaticEditorFlags))
                .Where(name => !typeof(StaticEditorFlags).GetField(name).IsDefined(typeof(ObsoleteAttribute), false))
                .ToArray();
        }

        private static StaticEditorFlags Flag(string name)
        {
            return (StaticEditorFlags)Enum.Parse(typeof(StaticEditorFlags), name);
        }

        private static StaticEditorFlags StaticFlags(string value)
        {
            var text = (value ?? string.Empty).Trim();
            if (text.Equals("true", StringComparison.OrdinalIgnoreCase) || text.Equals("Everything", StringComparison.OrdinalIgnoreCase))
                return StaticFlagNames().Aggregate((StaticEditorFlags)0, (all, name) => all | Flag(name));
            if (text.Length == 0 || text.Equals("false", StringComparison.OrdinalIgnoreCase) || text.Equals("Nothing", StringComparison.OrdinalIgnoreCase))
                return 0;
            StaticEditorFlags flags = 0;
            foreach (var part in text.Split(new[] { ',', '|' }, StringSplitOptions.RemoveEmptyEntries))
            {
                var name = AssetViews.Normalize(part);
                var match = StaticFlagNames().FirstOrDefault(item => AssetViews.Normalize(item) == name || AssetViews.Normalize(item) == name + "static");
                if (match == null)
                    throw new ArgumentException("Unknown Static flag: " + part.Trim() + ". Use true, false or " +
                        string.Join(", ", StaticFlagNames().Select(ObjectNames.NicifyVariableName).ToArray()) + ".");
                flags |= Flag(match);
            }
            return flags;
        }

        private static string ChildrenNote(int count)
        {
            return count > 1 ? " (" + count + " objects)" : string.Empty;
        }

        // Applies an edit to the object (or prefab asset root) and optionally its children; returns how many changed.
        private static int EditHeader(string path, bool children, string undoName, Action<GameObject> edit)
        {
            if (path.StartsWith("Assets/", StringComparison.Ordinal))
            {
                if (!path.EndsWith(".prefab", StringComparison.OrdinalIgnoreCase) || AssetDatabase.LoadAssetAtPath<GameObject>(path) == null)
                    throw new InvalidOperationException("Prefab root was not found in Assets: " + path);
                var root = PrefabUtility.LoadPrefabContents(path);
                try
                {
                    var targets = Targets(root, children);
                    targets.ForEach(edit);
                    if (PrefabUtility.SaveAsPrefabAsset(root, path) == null)
                        throw new InvalidOperationException("Unity could not save the prefab: " + path);
                    EditorPresentationService.ShowAssetPath(path);
                    return targets.Count;
                }
                finally
                {
                    PrefabUtility.UnloadPrefabContents(root);
                }
            }
            var gameObject = ScenePath.ResolveObject(path);
            var objects = Targets(gameObject, children);
            Undo.RecordObjects(objects.Cast<UnityEngine.Object>().ToArray(), "Unity Agent Bridge: " + undoName);
            foreach (var item in objects)
            {
                edit(item);
                EditorUtility.SetDirty(item);
                if (PrefabUtility.IsPartOfPrefabInstance(item))
                    PrefabUtility.RecordPrefabInstancePropertyModifications(item);
            }
            ScenePersistenceService.MarkDirty(gameObject.scene);
            EditorPresentationService.ShowSceneObject(gameObject);
            return objects.Count;
        }

        private static System.Collections.Generic.List<GameObject> Targets(GameObject root, bool children)
        {
            return children
                ? root.GetComponentsInChildren<Transform>(true).Select(item => item.gameObject).ToList()
                : new System.Collections.Generic.List<GameObject> { root };
        }

        // A layer by name or index; a new name takes the first empty user layer, as tags are created on first use.
        private static int LayerIndex(string layer)
        {
            var text = (layer ?? string.Empty).Trim();
            int index;
            if (int.TryParse(text, out index))
            {
                if (index < 0 || index > 31 || string.IsNullOrEmpty(LayerMask.LayerToName(index)))
                    throw new ArgumentException("Layer " + text + " is not defined.", "layer");
                return index;
            }
            if (text.Length == 0 || text.Any(char.IsControl))
                throw new ArgumentException("Layer must be a name or an index 0-31.", "layer");
            index = LayerMask.NameToLayer(text);
            if (index >= 0)
                return index;
            RequireSavableSettings("Layer " + text);
            var tagManager = new SerializedObject(AssetDatabase.LoadAllAssetsAtPath("ProjectSettings/TagManager.asset")[0]);
            var layers = tagManager.FindProperty("layers");
            for (index = 8; index < layers.arraySize; index++)
            {
                var slot = layers.GetArrayElementAtIndex(index);
                if (!string.IsNullOrEmpty(slot.stringValue))
                    continue;
                slot.stringValue = text;
                tagManager.ApplyModifiedPropertiesWithoutUndo();
                AssetDatabase.SaveAssets();
                return index;
            }
            throw new InvalidOperationException("All user layers are taken; free one in Project Settings/Tags and Layers.");
        }

        // A new tag or layer goes to Project Settings, which Unity neither saves nor keeps in Play Mode.
        private static void RequireSavableSettings(string what)
        {
            if (EditorApplication.isPlayingOrWillChangePlaymode)
                throw new InvalidOperationException(what + " does not exist, and Project Settings are not saved in Play Mode. Run play stop first.");
        }

        private static void EnsureTagExists(string tag)
        {
            if (InternalEditorUtility.tags.Contains(tag))
                return;
            RequireSavableSettings("Tag " + tag);
            var assets = AssetDatabase.LoadAllAssetsAtPath("ProjectSettings/TagManager.asset");
            if (assets == null || assets.Length == 0)
                throw new InvalidOperationException("Unity TagManager asset was not found.");
            var tagManager = new SerializedObject(assets[0]);
            var tags = tagManager.FindProperty("tags");
            if (tags == null || !tags.isArray)
                throw new InvalidOperationException("Unity TagManager does not expose the tags array.");
            tags.InsertArrayElementAtIndex(tags.arraySize);
            tags.GetArrayElementAtIndex(tags.arraySize - 1).stringValue = tag;
            if (!tagManager.ApplyModifiedPropertiesWithoutUndo())
                throw new InvalidOperationException("Unity did not create the tag: " + tag);
            AssetDatabase.SaveAssets();
        }
    }
}
