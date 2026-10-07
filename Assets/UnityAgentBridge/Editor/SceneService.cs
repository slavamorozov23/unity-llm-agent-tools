using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace UnityAgentBridge.Editor
{
    internal static class SceneService
    {
        [Serializable]
        private sealed class RuntimeTransformData
        {
            public Vector3 position;
            public Vector3 localPosition;
            public Vector3 localEulerAngles;
            public Vector3 localScale;
            public Vector3 lossyScale;
        }

        [Serializable]
        private sealed class RuntimeRectTransformData
        {
            public Vector3 position;
            public Vector3 localPosition;
            public Vector2 anchoredPosition;
            public Vector2 sizeDelta;
            public Rect rect;
            public Vector2 offsetMin;
            public Vector2 offsetMax;
            public Vector2 anchorMin;
            public Vector2 anchorMax;
            public Vector2 pivot;
            public Vector3 localScale;
            public Vector3 lossyScale;
        }

        public static SceneObjectData[] GetTree()
        {
            var result = new List<SceneObjectData>();
            var prefabStage = PrefabStageUtility.GetCurrentPrefabStage();
            if (prefabStage != null)
            {
                AddHierarchy(prefabStage.prefabContentsRoot, 0, result);
                return result.ToArray();
            }
            for (var sceneIndex = 0; sceneIndex < SceneManager.sceneCount; sceneIndex++)
            {
                var scene = SceneManager.GetSceneAt(sceneIndex);
                if (!scene.isLoaded)
                    continue;

                foreach (var root in scene.GetRootGameObjects())
                    AddHierarchy(root, 0, result);
            }

            return result.ToArray();
        }

        public static SceneObjectData GetObjectInfo(string path)
        {
            var gameObject = ScenePath.ResolveObject(path);
            EditorPresentationService.ShowSceneObject(gameObject);
            return Describe(gameObject, Depth(gameObject.transform));
        }

        public static SceneObjectData GetObjectInfo(BridgeRequest request)
        {
            var gameObject = ScenePath.ResolveObject(request.path);
            if (request.runtime)
            {
                if (string.IsNullOrWhiteSpace(request.componentType))
                    throw new ArgumentException("Runtime object values require componentType.", "componentType");
                var component = ComponentService.ResolveAttachedComponent(
                    gameObject, request.componentType, request.componentIndex);
                EditorPresentationService.ShowComponent(component);
                var result = Describe(gameObject, Depth(gameObject.transform));
                result.components = new[] { DescribeRuntimeComponent(component, request.propertyPath) };
                return result;
            }
            if (string.IsNullOrWhiteSpace(request.componentType))
                EditorPresentationService.ShowSceneObject(gameObject);
            else
                // Several matching components are all returned; the Inspector scrolls to the first.
                EditorPresentationService.ShowComponent(ComponentService.ResolveAttachedComponent(
                    gameObject, request.componentType, Math.Max(0, request.componentIndex)));
            return Describe(gameObject, Depth(gameObject.transform));
        }

        private static ComponentData DescribeRuntimeComponent(Component component, string propertyPaths)
        {
            string json;
            var rectTransform = component as RectTransform;
            if (!string.IsNullOrWhiteSpace(propertyPaths) || !(component is Transform))
                json = RuntimeValueService.Describe(component, propertyPaths);
            else if (rectTransform != null)
            {
                json = JsonUtility.ToJson(new RuntimeRectTransformData
                {
                    position = rectTransform.position,
                    localPosition = rectTransform.localPosition,
                    anchoredPosition = rectTransform.anchoredPosition,
                    sizeDelta = rectTransform.sizeDelta,
                    rect = rectTransform.rect,
                    offsetMin = rectTransform.offsetMin,
                    offsetMax = rectTransform.offsetMax,
                    anchorMin = rectTransform.anchorMin,
                    anchorMax = rectTransform.anchorMax,
                    pivot = rectTransform.pivot,
                    localScale = rectTransform.localScale,
                    lossyScale = rectTransform.lossyScale
                });
            }
            else
            {
                var transform = (Transform)component;
                json = JsonUtility.ToJson(new RuntimeTransformData
                {
                    position = transform.position,
                    localPosition = transform.localPosition,
                    localEulerAngles = transform.localEulerAngles,
                    localScale = transform.localScale,
                    lossyScale = transform.lossyScale
                });
            }
            return new ComponentData
            {
                type = component.GetType().FullName,
                assemblyQualifiedType = string.Empty,
                json = json,
                references = Array.Empty<SerializedPropertyData>(),
                enums = Array.Empty<SerializedPropertyData>(),
                warnings = Array.Empty<InspectorWarningData>(),
                actions = Array.Empty<InspectorActionData>()
            };
        }

        public static SceneAssetData[] ListSceneAssets()
        {
            if (AssetDatabase.IsValidFolder("Assets/Scenes"))
                EditorPresentationService.ShowAssetFolder("Assets/Scenes");
            return AssetDatabase.FindAssets("t:Scene", new[] { "Assets" })
                .Select(AssetDatabase.GUIDToAssetPath)
                .OrderBy(path => path, StringComparer.Ordinal)
                .Select(path => new SceneAssetData
                {
                    name = Path.GetFileNameWithoutExtension(path),
                    assetPath = path
                })
                .ToArray();
        }

        public static SceneAssetData OpenScene(string nameOrPath)
        {
            if (string.IsNullOrWhiteSpace(nameOrPath))
                throw new ArgumentException("Scene name or path is required.", "nameOrPath");
            var matches = ListSceneAssets()
                .Where(item => string.Equals(item.assetPath, nameOrPath, StringComparison.OrdinalIgnoreCase)
                    || string.Equals(item.name, nameOrPath, StringComparison.OrdinalIgnoreCase))
                .ToArray();
            if (matches.Length != 1)
                throw new InvalidOperationException(matches.Length == 0 ? "Scene was not found in Assets: " + nameOrPath : "Scene name is ambiguous: " + nameOrPath);
            EditorPresentationService.ShowAssetPath(matches[0].assetPath);
            ScenePersistenceService.SaveBeforeTransition();
            if (PrefabStageUtility.GetCurrentPrefabStage() != null)
                StageUtility.GoToMainStage();
            EditorSceneManager.OpenScene(matches[0].assetPath, OpenSceneMode.Single);
            return matches[0];
        }

        public static SceneObjectData Describe(GameObject gameObject, int depth)
        {
            return Describe(gameObject, depth, true);
        }

        private static SceneObjectData Describe(GameObject gameObject, int depth, bool includeValues)
        {
            var componentData = DescribeComponents(gameObject, includeValues);
            var prefabStage = PrefabStageUtility.GetCurrentPrefabStage();
            var instanceRoot = PrefabUtility.GetOutermostPrefabInstanceRoot(gameObject);
            var prefabAssetPath = PrefabUtility.GetPrefabAssetPathOfNearestInstanceRoot(gameObject);
            if (prefabStage != null && (gameObject == prefabStage.prefabContentsRoot || gameObject.transform.IsChildOf(prefabStage.prefabContentsRoot.transform)))
                prefabAssetPath = prefabStage.assetPath;

            return new SceneObjectData
            {
                path = ScenePath.For(gameObject),
                parentPath = ScenePath.ParentOf(gameObject),
                name = gameObject.name,
                scene = gameObject.scene.name,
                depth = depth,
                activeSelf = gameObject.activeSelf,
                activeInHierarchy = gameObject.activeInHierarchy,
                visual = HasRenderableGeometry(gameObject),
                tag = gameObject.tag,
                layer = gameObject.layer,
                staticFlags = ObjectService.StaticText(GameObjectUtility.GetStaticEditorFlags(gameObject)),
                worldPosition = gameObject.transform.position,
                prefabAssetPath = prefabAssetPath,
                prefabInstanceRootPath = instanceRoot == null ? string.Empty : ScenePath.For(instanceRoot),
                components = componentData
            };
        }

        // Objects whose own renderer or collider bounds come within radius of a point, nearest first; for object-find --near.
        internal static string Near(string point, string radius)
        {
            var numbers = point.Split(',').Select(item => float.Parse(item.Trim(), System.Globalization.CultureInfo.InvariantCulture)).ToArray();
            if (numbers.Length != 3)
                throw new ArgumentException("--near takes x,y,z.", "query");
            var center = new Vector3(numbers[0], numbers[1], numbers[2]);
            var limit = float.Parse(radius, System.Globalization.CultureInfo.InvariantCulture);
            var found = new List<KeyValuePair<float, string>>();
            var prefabStage = PrefabStageUtility.GetCurrentPrefabStage();
            var roots = prefabStage != null
                ? new[] { prefabStage.prefabContentsRoot }
                : Enumerable.Range(0, SceneManager.sceneCount).Select(SceneManager.GetSceneAt).Where(scene => scene.isLoaded).SelectMany(scene => scene.GetRootGameObjects());
            // Hidden objects (inactive, or renderer switched off by the game) count too, by their geometry, and say so.
            foreach (var root in roots.SelectMany(item => item.GetComponentsInChildren<Transform>(true)).Select(item => item.gameObject))
            {
                var renderer = root.GetComponent<Renderer>();
                var collider = root.GetComponent<Collider>();
                if (renderer == null && collider == null)
                    continue;
                var bounds = renderer != null ? renderer.bounds : collider.bounds;
                if (bounds.size == Vector3.zero)
                {
                    var filter = root.GetComponent<MeshFilter>();
                    var mesh = filter != null ? filter.sharedMesh : collider is MeshCollider ? ((MeshCollider)collider).sharedMesh : null;
                    if (mesh != null)
                        bounds = WorldBounds(mesh.bounds, root.transform);
                    else if (collider is BoxCollider)
                        bounds = WorldBounds(new Bounds(((BoxCollider)collider).center, ((BoxCollider)collider).size), root.transform);
                }
                var distance = Mathf.Sqrt(bounds.SqrDistance(center));
                if (distance > limit)
                    continue;
                var size = bounds.size;
                var hidden = !root.activeInHierarchy || (renderer != null ? !renderer.enabled : !collider.enabled);
                found.Add(new KeyValuePair<float, string>(distance + size.magnitude * 1e-4f, new JsonText()
                    .Add("path", ScenePath.For(root))
                    .Add("distance", Math.Round(distance, 2))
                    .Add("size", Round(size.x) + "," + Round(size.y) + "," + Round(size.z))
                    .AddIf(hidden, "hidden", true).ToString()));
            }
            return "{\"objects\":[" + string.Join(",", found.OrderBy(item => item.Key).Select(item => item.Value).ToArray()) + "]}";
        }

        private static Bounds WorldBounds(Bounds local, Transform transform)
        {
            var world = new Bounds(transform.TransformPoint(local.center), Vector3.zero);
            for (var corner = 0; corner < 8; corner++)
                world.Encapsulate(transform.TransformPoint(local.center + Vector3.Scale(local.extents, new Vector3((corner & 1) == 0 ? -1 : 1, (corner & 2) == 0 ? -1 : 1, (corner & 4) == 0 ? -1 : 1))));
            return world;
        }

        private static string Round(float value)
        {
            return Math.Round(value, 2).ToString(System.Globalization.CultureInfo.InvariantCulture);
        }

        private static bool HasRenderableGeometry(GameObject gameObject)
        {
            if (gameObject.GetComponentsInChildren<Renderer>(false).Any(renderer => renderer.enabled))
                return true;
            if (gameObject.GetComponentsInChildren<CanvasRenderer>(false).Any(renderer => renderer.gameObject.activeInHierarchy))
                return true;
            return gameObject.GetComponentsInChildren<Terrain>(false).Any(terrain => terrain.enabled);
        }

        internal static ComponentData[] DescribeComponents(GameObject gameObject, bool includeValues)
        {
            var components = gameObject.GetComponents<Component>();
            var componentData = new ComponentData[components.Length];
            for (var i = 0; i < components.Length; i++)
            {
                var component = components[i];
                if (component == null)
                {
                    componentData[i] = new ComponentData
                    {
                        type = "Missing Script",
                        assemblyQualifiedType = string.Empty,
                        json = string.Empty,
                        references = Array.Empty<SerializedPropertyData>(),
                        enums = Array.Empty<SerializedPropertyData>(),
                        warnings = Array.Empty<InspectorWarningData>(),
                        actions = Array.Empty<InspectorActionData>()
                    };
                    continue;
                }

                var type = component.GetType();
                componentData[i] = new ComponentData
                {
                    type = type.FullName,
                    assemblyQualifiedType = includeValues ? type.AssemblyQualifiedName : string.Empty,
                    json = includeValues ? ComponentService.ValuesJson(component) : string.Empty,
                    hidden = includeValues ? ComponentService.HiddenFields(component) : Array.Empty<string>(),
                    references = includeValues ? ComponentService.DescribeReferences(component) : Array.Empty<SerializedPropertyData>(),
                    enums = includeValues ? ComponentService.DescribeEnums(component) : Array.Empty<SerializedPropertyData>(),
                    warnings = includeValues ? InspectorDiagnosticService.Warnings(component) : Array.Empty<InspectorWarningData>(),
                    actions = includeValues ? InspectorDiagnosticService.Actions(component) : Array.Empty<InspectorActionData>()
                };
            }

            return componentData;
        }

        // A prefab asset's hierarchy read without opening Prefab Mode; paths are the ones prefab-open gives.
        public static SceneObjectData[] GetPrefabTree(string assetPath)
        {
            if (AssetDatabase.LoadAssetAtPath<GameObject>(assetPath) == null)
                throw new ArgumentException("Prefab was not found: " + assetPath, "path");
            var root = PrefabUtility.LoadPrefabContents(assetPath);
            try
            {
                var result = new List<SceneObjectData>();
                AddHierarchy(root, 0, result);
                var stage = "/" + Path.GetFileNameWithoutExtension(assetPath);
                foreach (var item in result)
                {
                    var separator = item.path.IndexOf('/', 1);
                    item.path = stage + (separator < 0 ? "/" + item.name : item.path.Substring(separator));
                }
                return result.ToArray();
            }
            finally
            {
                PrefabUtility.UnloadPrefabContents(root);
            }
        }

        private static void AddHierarchy(GameObject gameObject, int depth, ICollection<SceneObjectData> result)
        {
            result.Add(Describe(gameObject, depth, false));
            for (var i = 0; i < gameObject.transform.childCount; i++)
                AddHierarchy(gameObject.transform.GetChild(i).gameObject, depth + 1, result);
        }

        private static int Depth(Transform transform)
        {
            var depth = 0;
            while (transform.parent != null)
            {
                depth++;
                transform = transform.parent;
            }

            return depth;
        }
    }
}
