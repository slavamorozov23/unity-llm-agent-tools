using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.ExceptionServices;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace UnityAgentBridge.Editor
{
    internal static class PrefabService
    {
        private static readonly HashSet<string> NonCreationMenuItems = new HashSet<string>(StringComparer.Ordinal)
        {
            "GameObject/Align View to Selected",
            "GameObject/Align With View",
            "GameObject/Center On Children",
            "GameObject/Clear Parent",
            "GameObject/Make Parent",
            "GameObject/Move To View",
            "GameObject/Set as first sibling",
            "GameObject/Set as last sibling",
            "GameObject/Toggle Active State"
        };

        private static readonly MethodInfo SavePrefabStage = typeof(PrefabStage).GetMethod(
            "SavePrefab",
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
            null,
            Type.EmptyTypes,
            null);

        public static string[] ListCreationTemplates()
        {
            var unsupported = typeof(EditorApplication).Assembly.GetType("UnityEditor.Unsupported", true);
            var method = unsupported.GetMethod(
                "GetSubmenus",
                BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic,
                null,
                new[] { typeof(string) },
                null);
            if (method == null)
                throw new MissingMethodException("Unity does not expose the GameObject creation menu.");
            var paths = method.Invoke(null, new object[] { "GameObject" }) as string[];
            if (paths == null)
                throw new InvalidOperationException("Unity returned no GameObject creation menu.");
            return paths
                .Where(path => !string.IsNullOrWhiteSpace(path) && path.StartsWith("GameObject/", StringComparison.Ordinal))
                .Where(path => !NonCreationMenuItems.Contains(path))
                .Distinct(StringComparer.Ordinal)
                .OrderBy(path => path, StringComparer.Ordinal)
                .ToArray();
        }

        public static SceneObjectData CreateEmpty(string parentPath, string name, string templateName, out string message)
        {
            if (string.IsNullOrWhiteSpace(name) && string.IsNullOrWhiteSpace(templateName))
                throw new ArgumentException("Object name or creation template is required.", "name");
            var scene = ScenePath.ResolveDestinationScene(parentPath);
            var parent = ScenePath.ResolveParent(parentPath);
            GameObject gameObject;
            HashSet<GameObject> before = null;
            if (string.IsNullOrWhiteSpace(templateName))
            {
                gameObject = new GameObject(name);
                Undo.RegisterCreatedObjectUndo(gameObject, "Unity Agent Bridge: Create Object");
            }
            else
            {
                var requested = templateName.StartsWith("GameObject/", StringComparison.Ordinal)
                    ? templateName
                    : "GameObject/" + templateName.TrimStart('/');
                var template = ListCreationTemplates().SingleOrDefault(path =>
                    string.Equals(path, requested, StringComparison.OrdinalIgnoreCase));
                if (template == null)
                    throw new InvalidOperationException("GameObject creation template was not found: " + templateName);
                before = new HashSet<GameObject>(Resources.FindObjectsOfTypeAll<GameObject>());
                var activeScene = SceneManager.GetActiveScene();
                var undoGroup = Undo.GetCurrentGroup();
                Undo.IncrementCurrentGroup();
                undoGroup = Undo.GetCurrentGroup();
                Undo.SetCurrentGroupName("Unity Agent Bridge: Create " + template);
                try
                {
                    if (scene.IsValid() && scene.isLoaded)
                        SceneManager.SetActiveScene(scene);
                    Selection.activeGameObject = parent == null ? null : parent.gameObject;
                    if (!EditorApplication.ExecuteMenuItem(template))
                        throw new InvalidOperationException("Unity could not execute GameObject creation template: " + template);
                    gameObject = Selection.activeGameObject;
                    if (gameObject == null || before.Contains(gameObject) || !gameObject.scene.IsValid())
                        gameObject = Resources.FindObjectsOfTypeAll<GameObject>()
                            .FirstOrDefault(candidate => candidate != null && candidate.scene.IsValid() && !before.Contains(candidate));
                    if (gameObject == null)
                        throw new InvalidOperationException("The selected GameObject menu item did not create an object immediately: " + template);
                    Undo.CollapseUndoOperations(undoGroup);
                }
                catch
                {
                    Undo.RevertAllDownToGroup(undoGroup);
                    throw;
                }
                finally
                {
                    if (activeScene.IsValid() && activeScene.isLoaded)
                        SceneManager.SetActiveScene(activeScene);
                }
                if (!string.IsNullOrWhiteSpace(name))
                    gameObject.name = name;
            }
            if (parent != null)
            {
                // As Create Empty Child: the new object keeps its local transform (no rotation, scale 1) under the parent.
                Undo.SetTransformParent(gameObject.transform, parent, false, "Unity Agent Bridge: Parent Object");
                var rectTransform = gameObject.transform as RectTransform;
                if (rectTransform != null)
                    rectTransform.anchoredPosition = Vector2.zero;
                else
                    gameObject.transform.localPosition = Vector3.zero;
            }
            else
            {
                var root = gameObject.transform.root.gameObject;
                if (root.scene != scene)
                    SceneManager.MoveGameObjectToScene(root, scene);
            }
            ScenePersistenceService.MarkDirty(gameObject.scene);
            // Menu items add what the object needs to work, such as the EventSystem of a Canvas.
            var created = gameObject;
            var companions = before == null ? new string[0] : Resources.FindObjectsOfTypeAll<GameObject>()
                .Where(candidate => candidate != null && candidate.scene.IsValid() && !before.Contains(candidate) &&
                    !candidate.transform.IsChildOf(created.transform) &&
                    (candidate.transform.parent == null || before.Contains(candidate.transform.parent.gameObject)))
                .Select(ScenePath.For).ToArray();
            message = companions.Length == 0 ? null : "Also created: " + string.Join(", ", companions);
            EditorPresentationService.ShowSceneObject(gameObject);
            return SceneService.GetObjectInfo(ScenePath.For(gameObject));
        }

        public static PrefabData Save(string objectPath, string prefabNameOrPath)
        {
            EnsureStandardFolder();
            var currentStage = PrefabStageUtility.GetCurrentPrefabStage();
            var gameObject = string.IsNullOrWhiteSpace(objectPath)
                ? currentStage == null ? null : currentStage.prefabContentsRoot
                : ScenePath.ResolveObject(objectPath);
            if (gameObject == null)
                throw new ArgumentException("Prefab save requires --path outside Prefab Mode.", "objectPath");
            var instanceRoot = PrefabUtility.GetOutermostPrefabInstanceRoot(gameObject);
            var sourcePath = PrefabUtility.GetPrefabAssetPathOfNearestInstanceRoot(gameObject);
            var stage = PrefabStageUtility.GetCurrentPrefabStage();
            if (stage != null && stage.prefabContentsRoot == gameObject)
                sourcePath = stage.assetPath;
            var assetPath = ResolveSavePath(prefabNameOrPath, gameObject.name, sourcePath);
            EnsureNoExternalSceneReferences(gameObject);
            EnsureAssetFolder(Path.GetDirectoryName(assetPath).Replace('\\', '/'));
            var existing = AssetDatabase.LoadAssetAtPath<GameObject>(assetPath);
            if (stage != null && stage.prefabContentsRoot == gameObject)
            {
                if (PrefabUtility.SaveAsPrefabAsset(gameObject, assetPath) == null)
                    throw new InvalidOperationException("Unity could not save prefab: " + assetPath);
            }
            else if (existing != null && instanceRoot == gameObject && string.Equals(sourcePath, assetPath, StringComparison.Ordinal))
            {
                PrefabUtility.ApplyPrefabInstance(gameObject, InteractionMode.AutomatedAction);
            }
            else if (PrefabUtility.SaveAsPrefabAssetAndConnect(gameObject, assetPath, InteractionMode.AutomatedAction) == null)
            {
                throw new InvalidOperationException("Unity could not save prefab: " + assetPath);
            }
            SynchronizeAsset(assetPath);
            EditorPresentationService.ShowAssetPath(assetPath);
            return DescribeAsset(assetPath, ScenePath.For(gameObject));
        }

        public static SceneObjectData Apply(string objectPath, string componentType, int componentIndex, string propertyPath, out string warning)
        {
            warning = string.Empty;
            var selected = ScenePath.ResolveObject(objectPath);
            var selectedPath = ScenePath.For(selected);
            var root = PrefabUtility.GetOutermostPrefabInstanceRoot(selected);
            if (root == null)
                throw new InvalidOperationException("Object is not part of a prefab instance: " + objectPath);
            var assetPath = PrefabUtility.GetPrefabAssetPathOfNearestInstanceRoot(selected);
            if (string.IsNullOrWhiteSpace(assetPath))
                throw new InvalidOperationException("Prefab source could not be resolved: " + objectPath);

            if (string.IsNullOrWhiteSpace(componentType))
            {
                if (!string.IsNullOrWhiteSpace(propertyPath))
                    throw new ArgumentException("Applying a property requires componentType.", "propertyPath");
                warning = SkippedReferenceWarning(ApplyPrefabSkippingExternalReferences(root));
            }
            else
            {
                var component = ComponentService.ResolveAttachedComponent(selected, componentType, componentIndex);
                if (string.IsNullOrWhiteSpace(propertyPath))
                {
                    if (PrefabUtility.IsAddedComponentOverride(component))
                        warning = SkippedReferenceWarning(ApplyAddedComponent(component, root, assetPath));
                    else
                        warning = SkippedReferenceWarning(ApplyComponentProperties(component, root, assetPath));
                }
                else
                {
                    var serializedObject = new SerializedObject(component);
                    var property = serializedObject.FindProperty(propertyPath);
                    if (property == null)
                        throw new InvalidOperationException("Serialized property was not found: " + propertyPath);
                    EnsureNoExternalSceneReferences(property, root);
                    PrefabUtility.ApplyPropertyOverride(property, assetPath, InteractionMode.AutomatedAction);
                }
            }

            SynchronizeAsset(assetPath);
            ScenePersistenceService.MarkDirty(selected.scene);
            return SceneService.GetObjectInfo(selectedPath);
        }

        private static System.Collections.Generic.List<string> ApplyComponentProperties(Component component, GameObject root, string assetPath)
        {
            var serializedObject = new SerializedObject(component);
            var iterator = serializedObject.GetIterator();
            var paths = new System.Collections.Generic.List<string>();
            var skipped = new System.Collections.Generic.List<string>();
            if (iterator.Next(true))
            {
                do
                {
                    if (!iterator.prefabOverride || iterator.propertyPath == "m_Script" || iterator.propertyPath == "m_Father")
                        continue;
                    if (iterator.propertyType == SerializedPropertyType.ObjectReference && !CanReferenceBeSaved(iterator, root))
                    {
                        skipped.Add(ReferenceLabel(component, iterator));
                        continue;
                    }
                    if (!iterator.hasChildren || iterator.propertyType == SerializedPropertyType.ObjectReference)
                        paths.Add(iterator.propertyPath);
                }
                while (iterator.Next(true));
            }
            foreach (var path in paths.Distinct(StringComparer.Ordinal))
            {
                var property = new SerializedObject(component).FindProperty(path);
                if (property != null && property.prefabOverride)
                    PrefabUtility.ApplyPropertyOverride(property, assetPath, InteractionMode.AutomatedAction);
            }
            return skipped;
        }

        private sealed class ExternalReferenceOverride
        {
            public Component component;
            public string propertyPath;
            public UnityEngine.Object value;
            public string label;
        }

        private static System.Collections.Generic.List<string> ApplyPrefabSkippingExternalReferences(GameObject root)
        {
            var overrides = new System.Collections.Generic.List<ExternalReferenceOverride>();
            foreach (var component in root.GetComponentsInChildren<Component>(true).Where(item => item != null))
            {
                var serializedObject = new SerializedObject(component);
                var sourceComponent = PrefabUtility.GetCorrespondingObjectFromSource(component) as Component;
                var sourceObject = sourceComponent == null ? null : new SerializedObject(sourceComponent);
                var iterator = serializedObject.GetIterator();
                var changed = false;
                if (iterator.Next(true))
                {
                    do
                    {
                        if (iterator.propertyType != SerializedPropertyType.ObjectReference || CanReferenceBeSaved(iterator, root))
                            continue;
                        overrides.Add(new ExternalReferenceOverride
                        {
                            component = component,
                            propertyPath = iterator.propertyPath,
                            value = iterator.objectReferenceValue,
                            label = ReferenceLabel(component, iterator),
                        });
                        var sourceProperty = sourceObject == null ? null : sourceObject.FindProperty(iterator.propertyPath);
                        iterator.objectReferenceValue = sourceProperty == null ? null : sourceProperty.objectReferenceValue;
                        changed = true;
                    }
                    while (iterator.Next(true));
                }
                if (changed)
                    serializedObject.ApplyModifiedPropertiesWithoutUndo();
            }

            PrefabUtility.ApplyPrefabInstance(root, InteractionMode.AutomatedAction);
            foreach (var group in overrides.Where(item => item.component != null).GroupBy(item => item.component))
            {
                var serializedObject = new SerializedObject(group.Key);
                foreach (var entry in group)
                {
                    var property = serializedObject.FindProperty(entry.propertyPath);
                    if (property != null)
                        property.objectReferenceValue = entry.value;
                }
                serializedObject.ApplyModifiedPropertiesWithoutUndo();
                PrefabUtility.RecordPrefabInstancePropertyModifications(group.Key);
            }
            return overrides.Select(item => item.label).ToList();
        }

        private static System.Collections.Generic.List<string> ApplyAddedComponent(Component component, GameObject root, string assetPath)
        {
            var serializedObject = new SerializedObject(component);
            var iterator = serializedObject.GetIterator();
            var external = new System.Collections.Generic.List<System.Collections.Generic.KeyValuePair<string, UnityEngine.Object>>();
            var labels = new System.Collections.Generic.List<string>();
            if (iterator.Next(true))
            {
                do
                {
                    if (iterator.propertyType != SerializedPropertyType.ObjectReference || CanReferenceBeSaved(iterator, root))
                        continue;
                    external.Add(new System.Collections.Generic.KeyValuePair<string, UnityEngine.Object>(iterator.propertyPath, iterator.objectReferenceValue));
                    labels.Add(ReferenceLabel(component, iterator));
                    iterator.objectReferenceValue = null;
                }
                while (iterator.Next(true));
            }
            serializedObject.ApplyModifiedPropertiesWithoutUndo();
            PrefabUtility.ApplyAddedComponent(component, assetPath, InteractionMode.AutomatedAction);
            if (component != null && external.Count > 0)
            {
                serializedObject = new SerializedObject(component);
                foreach (var entry in external)
                {
                    var property = serializedObject.FindProperty(entry.Key);
                    if (property != null)
                        property.objectReferenceValue = entry.Value;
                }
                serializedObject.ApplyModifiedPropertiesWithoutUndo();
                PrefabUtility.RecordPrefabInstancePropertyModifications(component);
            }
            return labels;
        }

        // "/Scene/Door#Animator.m_Avatar → /Scene/Rig": which override stays on the instance.
        private static string ReferenceLabel(Component component, SerializedProperty property)
        {
            var target = property.objectReferenceValue;
            var owner = target is Component ? ((Component)target).gameObject : target as GameObject;
            return ScenePath.For(component.gameObject) + "#" + component.GetType().Name + "." + property.propertyPath +
                (owner != null && owner.scene.IsValid() ? " → " + ScenePath.For(owner) : string.Empty);
        }

        private static string SkippedReferenceWarning(System.Collections.Generic.List<string> skipped)
        {
            return skipped.Count == 0 ? string.Empty : "Skipped external scene references (kept as instance overrides): " + string.Join(", ", skipped);
        }

        public static SceneObjectData Instantiate(string prefabNameOrPath, string parentPath)
        {
            var prefab = ResolvePrefab(prefabNameOrPath);
            var scene = ScenePath.ResolveDestinationScene(parentPath);
            var parent = ScenePath.ResolveParent(parentPath);
            var instance = (GameObject)PrefabUtility.InstantiatePrefab(prefab, scene);
            if (parent != null)
                instance.transform.SetParent(parent, false);
            Undo.RegisterCreatedObjectUndo(instance, "Unity Agent Bridge: Instantiate Prefab");
            ScenePersistenceService.MarkDirty(instance.scene);
            return SceneService.GetObjectInfo(ScenePath.For(instance));
        }

        public static SceneObjectData Revert(string objectPath, string componentType = null, int componentIndex = -1, string propertyPath = null)
        {
            var selected = ScenePath.ResolveObject(objectPath);
            var root = PrefabUtility.GetOutermostPrefabInstanceRoot(selected);
            if (root == null)
                throw new InvalidOperationException("Object is not part of a prefab instance: " + objectPath);
            var sourceRoot = PrefabUtility.GetCorrespondingObjectFromSource(root);
            if (sourceRoot == null)
                throw new InvalidOperationException("Prefab source could not be resolved: " + objectPath);
            if (string.IsNullOrWhiteSpace(componentType))
            {
                if (!string.IsNullOrWhiteSpace(propertyPath))
                    throw new ArgumentException("Reverting a property requires componentType.", "propertyPath");
                PrefabUtility.RevertPrefabInstance(root, InteractionMode.AutomatedAction);
            }
            else
            {
                RevertComponent(selected, componentType, componentIndex, propertyPath);
            }
            ScenePersistenceService.MarkDirty(root.scene);
            return SceneService.GetObjectInfo(ScenePath.For(root));
        }

        private static void RevertComponent(GameObject selected, string componentType, int componentIndex, string propertyPath)
        {
            var type = ComponentService.ResolveComponentType(componentType);
            var components = selected.GetComponents(type).Cast<Component>().ToArray();
            var index = componentIndex < 0 ? 0 : componentIndex;
            if (index < components.Length)
            {
                var component = components[index];
                if (!string.IsNullOrWhiteSpace(propertyPath))
                {
                    var serializedObject = new SerializedObject(component);
                    var property = serializedObject.FindProperty(propertyPath);
                    if (property == null)
                        throw new InvalidOperationException("Serialized property was not found: " + propertyPath);
                    PrefabUtility.RevertPropertyOverride(property, InteractionMode.AutomatedAction);
                }
                else if (PrefabUtility.IsAddedComponentOverride(component))
                {
                    PrefabUtility.RevertAddedComponent(component, InteractionMode.AutomatedAction);
                }
                else
                {
                    PrefabUtility.RevertObjectOverride(component, InteractionMode.AutomatedAction);
                }
                return;
            }

            if (!string.IsNullOrWhiteSpace(propertyPath))
                throw new InvalidOperationException("A removed component cannot revert one property; revert the component.");
            var sourceObject = PrefabUtility.GetCorrespondingObjectFromSource(selected);
            if (sourceObject == null)
                throw new InvalidOperationException("Prefab source object could not be resolved: " + ScenePath.For(selected));
            var sourceComponents = sourceObject.GetComponents(type).Cast<Component>().ToArray();
            if (index >= sourceComponents.Length)
                throw new InvalidOperationException("Prefab source has no component at index " + index + " for " + type.FullName + ".");
            PrefabUtility.RevertRemovedComponent(selected, sourceComponents[index], InteractionMode.AutomatedAction);
        }

        public static PrefabData Open(string prefabNameOrPath)
        {
            ComponentService.EnsureEditMode();
            ScenePersistenceService.SaveBeforeTransition();
            var prefab = ResolvePrefab(prefabNameOrPath);
            var assetPath = AssetDatabase.GetAssetPath(prefab);
            EditorPresentationService.ShowAsset(prefab);
            var stage = PrefabStageUtility.OpenPrefab(assetPath);
            if (stage == null || stage.prefabContentsRoot == null)
                throw new InvalidOperationException("Unity could not open prefab: " + assetPath);
            EditorPresentationService.ShowSceneObject(stage.prefabContentsRoot);
            return DescribeAsset(assetPath, ScenePath.For(stage.prefabContentsRoot));
        }

        public static PrefabData Close()
        {
            ComponentService.EnsureEditMode();
            var stage = PrefabStageUtility.GetCurrentPrefabStage();
            if (stage == null || stage.prefabContentsRoot == null)
                throw new InvalidOperationException("Prefab Mode is not open.");
            var assetPath = stage.assetPath;
            var rootPath = ScenePath.For(stage.prefabContentsRoot);
            SaveStage(stage);
            SynchronizeAsset(assetPath);
            StageUtility.GoToMainStage();
            EditorPresentationService.ShowAssetPath(assetPath);
            return DescribeAsset(assetPath, rootPath);
        }

        internal static PrefabData DescribeAsset(string assetPath, string rootPath = "")
        {
            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(assetPath);
            if (prefab == null)
                throw new InvalidOperationException("Prefab asset was not found: " + assetPath);
            var source = PrefabUtility.GetCorrespondingObjectFromSource(prefab);
            var basePrefabPath = source == null ? string.Empty : AssetDatabase.GetAssetPath(source);
            if (string.Equals(basePrefabPath, assetPath, StringComparison.Ordinal))
                basePrefabPath = string.Empty;
            return new PrefabData
            {
                name = prefab.name,
                assetPath = assetPath,
                rootPath = rootPath ?? string.Empty,
                basePrefabPath = basePrefabPath ?? string.Empty
            };
        }

        public static void SaveCurrentStage()
        {
            var stage = PrefabStageUtility.GetCurrentPrefabStage();
            if (stage != null)
                SaveStage(stage);
        }

        private static void SaveStage(PrefabStage stage)
        {
            if (SavePrefabStage == null)
                throw new MissingMethodException(typeof(PrefabStage).FullName, "SavePrefab");
            var missing = stage.prefabContentsRoot
                .GetComponentsInChildren<Transform>(true)
                .Where(item => GameObjectUtility.GetMonoBehavioursWithMissingScriptCount(item.gameObject) > 0)
                .Select(item => ScenePath.For(item.gameObject))
                .Take(4)
                .ToArray();
            if (missing.Length > 0)
                throw new InvalidOperationException(
                    "Prefab cannot be saved because it contains a missing script on: " + string.Join(", ", missing) + ".");
            try
            {
                SavePrefabStage.Invoke(stage, null);
            }
            catch (TargetInvocationException error)
            {
                if (error.InnerException == null)
                    throw;
                ExceptionDispatchInfo.Capture(error.InnerException).Throw();
                throw;
            }
        }

        private static void EnsureNoExternalSceneReferences(GameObject root)
        {
            foreach (var component in root.GetComponentsInChildren<Component>(true).Where(item => item != null))
                EnsureNoExternalSceneReferences(component, root);
        }

        private static void EnsureNoExternalSceneReferences(Component component, GameObject root)
        {
            var serializedObject = new SerializedObject(component);
            var property = serializedObject.GetIterator();
            if (!property.Next(true))
                return;
            do
            {
                EnsureReferenceCanBeSaved(property, root);
            }
            while (property.Next(true));
        }

        private static void EnsureNoExternalSceneReferences(SerializedProperty property, GameObject root)
        {
            var current = property.Copy();
            var end = property.GetEndProperty();
            do
            {
                EnsureReferenceCanBeSaved(current, root);
            }
            while (current.Next(true) && !SerializedProperty.EqualContents(current, end));
        }

        private static void EnsureReferenceCanBeSaved(SerializedProperty property, GameObject root)
        {
            if (CanReferenceBeSaved(property, root))
                return;
            var value = property.objectReferenceValue;
            var owner = value as GameObject;
            var component = value as Component;
            if (owner == null && component != null)
                owner = component.gameObject;
            throw new InvalidOperationException(
                "Prefab cannot store scene reference " + property.propertyPath + " -> " + ScenePath.For(owner) + ".");
        }

        private static bool CanReferenceBeSaved(SerializedProperty property, GameObject root)
        {
            if (property.propertyType != SerializedPropertyType.ObjectReference)
                return true;
            if (property.propertyPath == "m_Father" && property.serializedObject.targetObject == root.transform)
                return true;
            var value = property.objectReferenceValue;
            var owner = value as GameObject;
            var component = value as Component;
            if (owner == null && component != null)
                owner = component.gameObject;
            if (owner == null || !owner.scene.IsValid() || owner == root || owner.transform.IsChildOf(root.transform))
                return true;
            return false;
        }

        private static void SynchronizeAsset(string assetPath)
        {
            AssetDatabase.SaveAssets();
            AssetDatabase.ImportAsset(assetPath, ImportAssetOptions.ForceSynchronousImport | ImportAssetOptions.ForceUpdate);
        }

        private static GameObject ResolvePrefab(string nameOrPath)
        {
            if (string.IsNullOrWhiteSpace(nameOrPath))
                throw new ArgumentException("Prefab name or path is required.", "nameOrPath");
            if (nameOrPath.StartsWith("Assets/", StringComparison.Ordinal) || nameOrPath.IndexOf('/') >= 0 || nameOrPath.IndexOf('\\') >= 0)
            {
                var requestedPath = nameOrPath.Trim().Replace('\\', '/');
                var assetPath = requestedPath.StartsWith("Assets/", StringComparison.Ordinal) && Path.HasExtension(requestedPath)
                    ? requestedPath
                    : NormalizePrefabPath(requestedPath);
                var direct = AssetDatabase.LoadAssetAtPath<GameObject>(assetPath);
                if (direct == null)
                    throw new InvalidOperationException("GameObject asset was not found in Assets: " + assetPath);
                EditorPresentationService.ShowAsset(direct);
                return direct;
            }
            var matches = ObjectService.ListPrefabs().Where(item => string.Equals(item.name, nameOrPath, StringComparison.OrdinalIgnoreCase)).ToArray();
            if (matches.Length != 1)
                throw new InvalidOperationException(matches.Length == 0 ? "Prefab was not found: " + nameOrPath : "Prefab name is ambiguous: " + nameOrPath);
            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(matches[0].assetPath);
            EditorPresentationService.ShowAsset(prefab);
            return prefab;
        }

        private static string ResolveSavePath(string nameOrPath, string objectName, string sourcePath)
        {
            if (!string.IsNullOrWhiteSpace(nameOrPath))
                return NormalizePrefabPath(nameOrPath);
            if (!string.IsNullOrEmpty(sourcePath) && sourcePath.StartsWith("Assets/", StringComparison.Ordinal))
                return sourcePath;
            return NormalizePrefabPath(objectName);
        }

        private static string NormalizePrefabPath(string nameOrPath)
        {
            if (string.IsNullOrWhiteSpace(nameOrPath))
                throw new ArgumentException("Prefab name or path is required.", "nameOrPath");
            var normalized = nameOrPath.Trim().Replace('\\', '/');
            if (!normalized.StartsWith("Assets/", StringComparison.Ordinal))
                normalized = BridgePaths.StandardPrefabFolder + "/" + normalized.TrimStart('/');
            if (!normalized.EndsWith(".prefab", StringComparison.OrdinalIgnoreCase))
                normalized += ".prefab";
            if (!normalized.StartsWith("Assets/", StringComparison.Ordinal)
                || normalized.IndexOf("/../", StringComparison.Ordinal) >= 0
                || normalized.EndsWith("/..", StringComparison.Ordinal)
                || normalized.IndexOf("/./", StringComparison.Ordinal) >= 0)
                throw new InvalidOperationException("Prefab path must be inside Assets: " + normalized);
            var fileName = Path.GetFileNameWithoutExtension(normalized);
            if (string.IsNullOrWhiteSpace(fileName))
                throw new InvalidOperationException("Prefab path must contain a file name: " + normalized);
            return normalized;
        }

        private static void EnsureAssetFolder(string assetPath)
        {
            if (AssetDatabase.IsValidFolder(assetPath))
                return;
            var parent = Path.GetDirectoryName(assetPath).Replace('\\', '/');
            if (string.IsNullOrEmpty(parent) || (!string.Equals(parent, "Assets", StringComparison.Ordinal) && !parent.StartsWith("Assets/", StringComparison.Ordinal)))
                throw new InvalidOperationException("Prefab folder must be inside Assets: " + assetPath);
            EnsureAssetFolder(parent);
            if (string.IsNullOrEmpty(AssetDatabase.CreateFolder(parent, Path.GetFileName(assetPath))))
                throw new InvalidOperationException("Unity could not create prefab folder: " + assetPath);
        }

        private static void EnsureStandardFolder()
        {
            if (!AssetDatabase.IsValidFolder("Assets/UnityAgentBridge"))
                throw new DirectoryNotFoundException("UnityAgentBridge asset folder was not found.");
            if (!AssetDatabase.IsValidFolder(BridgePaths.StandardPrefabFolder))
                AssetDatabase.CreateFolder("Assets/UnityAgentBridge", "Prefabs");
        }
    }
}
