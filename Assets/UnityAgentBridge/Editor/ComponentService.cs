using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace UnityAgentBridge.Editor
{
    internal static class ComponentService
    {
        [Serializable]
        private sealed class StringBox { public string value; }
        [Serializable]
        private sealed class VectorBox { public float x; public float y; public float z; public float w; }
        [Serializable]
        private sealed class ColorBox { public float r; public float g; public float b; public float a; }
        [Serializable]
        private sealed class RectBox { public float x; public float y; public float width; public float height; }
        [Serializable]
        private sealed class RectOffsetBox
        {
            public int left; public int right; public int top; public int bottom;
            public int m_Left; public int m_Right; public int m_Top; public int m_Bottom;
        }
        [Serializable]
        private sealed class BoundsBox { public VectorBox center; public VectorBox size; }
        [Serializable]
        private sealed class VectorIntBox { public int x; public int y; public int z; }
        [Serializable]
        private sealed class UnityEventCallBox
        {
            public string target;
            public string method;
            public string mode;
            public string state;
            public string objectArgument;
            public int intArgument;
            public float floatArgument;
            public string stringArgument;
            public bool boolArgument;
        }
        [Serializable]
        private sealed class UnityEventCallsBox { public UnityEventCallBox[] calls; }
        [Serializable]
        private sealed class AnimationCurveBox
        {
            public AnimationKeyBox[] keys;
            public AnimationKeyBox[] m_Curve;
            public int preWrapMode;
            public int postWrapMode;
            public int m_PreInfinity;
            public int m_PostInfinity;
        }
        [Serializable]
        private sealed class GradientColorKeyBox { public float time; public float r; public float g; public float b; }
        [Serializable]
        private sealed class GradientAlphaKeyBox { public float time; public float alpha; }
        [Serializable]
        private sealed class GradientBox
        {
            public GradientColorKeyBox[] colorKeys;
            public GradientAlphaKeyBox[] alphaKeys;
            public string mode;
        }
        [Serializable]
        private sealed class AnimationKeyBox
        {
            public float time;
            public float value;
            public float inTangent;
            public float outTangent;
            public float inSlope;
            public float outSlope;
            public float inWeight;
            public float outWeight;
            public int weightedMode;
        }

        public static string[] GetAllComponentTypeNames()
        {
            return TypeCache.GetTypesDerivedFrom<Component>()
                .Where(type => !type.IsAbstract && !type.IsGenericTypeDefinition)
                .Select(type => type.FullName)
                .Where(name => !string.IsNullOrEmpty(name))
                .OrderBy(name => name, StringComparer.Ordinal)
                .ToArray();
        }

        public static SceneObjectData Modify(BridgeRequest request, out string warning)
        {
            var gameObject = ScenePath.ResolveObject(request.path);
            var component = ResolveAttachedComponent(gameObject, request.componentType, request.componentIndex);
            EditorPresentationService.ShowComponent(component);
            if (!EditorApplication.isPlayingOrWillChangePlaymode)
                Undo.RecordObject(component, "Unity Agent Bridge: Modify Component");
            ApplyValues(component, request.values, out warning);
            if (EditorApplication.isPlaying)
            {
                Physics.SyncTransforms();
                Physics2D.SyncTransforms();
                if (component is Transform)
                    warning = Join(warning, MoveNotes(gameObject));
            }
            if (!EditorApplication.isPlayingOrWillChangePlaymode)
            {
                EditorUtility.SetDirty(component);
                if (PrefabUtility.IsPartOfPrefabInstance(component))
                    PrefabUtility.RecordPrefabInstancePropertyModifications(component);
            }
            return SceneService.Describe(gameObject, HierarchyDepth(gameObject.transform));
        }

        // What a moved object meets in Play Mode: renderers combined by static batching stay where they were, and the
        // triggers it now stands in fire on the next physics step (a teleport into a cutscene or level trigger).
        private static string MoveNotes(GameObject gameObject)
        {
            var notes = new List<string>();
            if (gameObject.GetComponentsInChildren<Renderer>().Any(renderer => renderer.isPartOfStaticBatch))
                notes.Add("Warning: its renderers are static-batched (Batching Static) and do not move in Play Mode.");
            var own = gameObject.GetComponentsInChildren<Collider>().Where(collider => collider.enabled && !collider.isTrigger).ToList();
            if (own.Count > 0)
            {
                var bounds = own[0].bounds;
                foreach (var collider in own.Skip(1))
                    bounds.Encapsulate(collider.bounds);
                var triggers = Physics.OverlapBox(bounds.center, bounds.extents, Quaternion.identity, Physics.AllLayers, QueryTriggerInteraction.Collide)
                    .Where(other => other.isTrigger && !other.transform.IsChildOf(gameObject.transform))
                    .Select(other => ScenePath.For(other.gameObject)).Distinct().Take(3).ToArray();
                if (triggers.Length > 0)
                    notes.Add("Inside trigger: " + string.Join(", ", triggers) + ".");
            }
            return string.Join(" ", notes.ToArray());
        }

        private static string Join(string first, string second)
        {
            return string.IsNullOrEmpty(first) ? second : string.IsNullOrEmpty(second) ? first : first + " " + second;
        }

        // In Play Mode a component is added as in the Inspector there: it runs at once and is gone when the game stops.
        public static SceneObjectData Add(BridgeRequest request, out string warning, out int componentIndex)
        {
            var gameObject = ScenePath.ResolveObject(request.path);
            var type = ResolveComponentType(request.componentType);
            EnsureUnityScript(type);
            var existingCount = gameObject.GetComponents(type).Length;
            if (existingCount > 0 && type.GetCustomAttributes(typeof(DisallowMultipleComponent), true).Length > 0)
            {
                EditorPresentationService.ShowComponent(gameObject.GetComponents(type)[0]);
                warning = "Warning: the object already has its single allowed component of type " + type.FullName + ".";
                componentIndex = 0;
                return SceneService.Describe(gameObject, HierarchyDepth(gameObject.transform));
            }
            Component component = null;
            string valueWarning = string.Empty;
            // Outside Play Mode only: switching a playing object off and on would run its OnDisable/OnEnable.
            var suspendObject = gameObject.activeSelf && gameObject.activeInHierarchy && !EditorApplication.isPlaying;
            if (suspendObject)
                gameObject.SetActive(false);
            try
            {
                component = Undo.AddComponent(gameObject, type);
                if (component == null)
                {
                    if (existingCount > 0)
                    {
                        warning = "Warning: Unity did not add another " + type.FullName + " because the object already has one.";
                        componentIndex = 0;
                        return SceneService.Describe(gameObject, HierarchyDepth(gameObject.transform));
                    }
                    throw new InvalidOperationException("Unity could not add component " + type.FullName + ".");
                }
            }
            finally
            {
                if (suspendObject && gameObject != null)
                    gameObject.SetActive(true);
            }
            // Values go in after the object is back on, as edits in the Inspector follow Add Component: components
            // that initialize themselves on enable (HDRP light units) would otherwise overwrite them.
            try
            {
                ApplyValues(component, request.values, out valueWarning);
            }
            catch (Exception error)
            {
                Undo.DestroyObjectImmediate(component);
                throw new InvalidOperationException("Component was not added: " + error.Message, error);
            }
            EditorPresentationService.ShowComponent(component);
            EditorUtility.SetDirty(component);
            if (PrefabUtility.IsPartOfPrefabInstance(component))
                PrefabUtility.RecordPrefabInstancePropertyModifications(component);
            var duplicateWarning = existingCount > 0
                ? "Warning: the object already had " + existingCount + " component(s) of type " + type.FullName + "."
                : string.Empty;
            warning = string.Join(" ", new[] { duplicateWarning, valueWarning }.Where(value => !string.IsNullOrEmpty(value)).ToArray());
            componentIndex = Array.IndexOf(gameObject.GetComponents(type), component);
            if (componentIndex < 0)
                throw new InvalidOperationException("Added component is missing from its GameObject.");
            return SceneService.Describe(gameObject, HierarchyDepth(gameObject.transform));
        }

        public static SceneObjectData Remove(BridgeRequest request)
        {
            var gameObject = ScenePath.ResolveObject(request.path);
            var component = ResolveAttachedComponent(gameObject, request.componentType, request.componentIndex);
            EditorPresentationService.ShowComponent(component);
            if (component is Transform)
                throw new InvalidOperationException("Transform cannot be removed from a GameObject.");
            // As the Inspector: a component others require stays, except SRP additional data (HDAdditionalLightData),
            // which the render pipeline's Remove Component takes along.
            var dependents = gameObject.GetComponents<Component>().Where(item => item != null && item != component && Requires(item.GetType(), component)).ToList();
            var blocking = dependents.Where(item => item.GetType().GetInterfaces().All(type => type.Name != "IAdditionalData")).ToList();
            if (blocking.Count > 0)
                throw new InvalidOperationException("Can't remove " + component.GetType().Name + " because " + string.Join(", ", blocking.Select(item => item.GetType().Name).ToArray()) + " depends on it.");
            foreach (var dependent in dependents)
                Undo.DestroyObjectImmediate(dependent);
            Undo.DestroyObjectImmediate(component);
            if (component != null)
                throw new InvalidOperationException("Unity did not remove " + component.GetType().Name + ".");
            return SceneService.Describe(gameObject, HierarchyDepth(gameObject.transform));
        }

        private static bool Requires(Type type, Component required)
        {
            // Another component of the same type still satisfies the requirement.
            if (required.gameObject.GetComponents(required.GetType()).Length > 1)
                return false;
            return type.GetCustomAttributes(typeof(RequireComponent), true).Cast<RequireComponent>()
                .SelectMany(item => new[] { item.m_Type0, item.m_Type1, item.m_Type2 })
                .Any(item => item != null && item.IsInstanceOfType(required));
        }

        public static CandidateData[] GetObjectPickerCandidates(BridgeRequest request)
        {
            var gameObject = ScenePath.ResolveObject(request.path);
            var component = ResolveAttachedComponent(gameObject, request.componentType, request.componentIndex);
            EditorPresentationService.ShowComponent(component);
            var expectedType = ObjectReferenceType(component, request.propertyPath);
            var limit = Mathf.Clamp(request.limit <= 0 ? 10 : request.limit, 1, 10);
            var candidates = new List<CandidateData>();

            foreach (var item in Resources.FindObjectsOfTypeAll(expectedType))
            {
                if (item == null || EditorUtility.IsPersistent(item))
                    continue;

                var owner = Owner(item);
                if (owner == null || !IsListedLoadedScene(owner.scene))
                    continue;

                var path = ScenePath.For(owner);
                var candidatePath = path;
                var label = item.name;
                var candidateComponent = item as Component;
                if (candidateComponent != null)
                {
                    var concreteType = candidateComponent.GetType();
                    var matchingComponents = owner.GetComponents(concreteType).Cast<Component>().ToArray();
                    var componentIndex = Array.IndexOf(matchingComponents, candidateComponent);
                    if (componentIndex < 0)
                        throw new InvalidOperationException("Object Picker component is missing from its GameObject.");
                    candidatePath = path + "#" + Uri.EscapeDataString(concreteType.FullName) + "[" + componentIndex + "]";
                    label += " (" + concreteType.Name + " #" + componentIndex + ")";
                }

                candidates.Add(new CandidateData
                {
                    label = label,
                    path = candidatePath,
                    type = item.GetType().FullName,
                    source = "scene"
                });
            }

            candidates.AddRange(AssetObjectPickerCandidates(expectedType));

            return candidates
                .GroupBy(item => item.source + "\n" + item.path + "\n" + item.type)
                .Select(group => group.First())
                .OrderBy(item => item.source == "scene" ? 0 : item.path.StartsWith("Assets/", StringComparison.Ordinal) ? 1 : 2)
                .ThenBy(item => item.label, StringComparer.OrdinalIgnoreCase)
                .ThenBy(item => item.path, StringComparer.Ordinal)
                .Take(limit)
                .ToArray();
        }

        private static Type ObjectReferenceType(Component component, string propertyPath)
        {
            var serializedObject = new SerializedObject(component);
            var property = FindProperty(serializedObject, propertyPath);
            if (property == null)
                throw new InvalidOperationException("Serialized property was not found: " + propertyPath);
            if (property.propertyType == SerializedPropertyType.ObjectReference)
                return ResolveObjectReferenceType(property.type);
            if (property.isArray && property.propertyType == SerializedPropertyType.Generic)
                return ResolveObjectReferenceType(property.arrayElementType);
            throw new InvalidOperationException("Property is not an Object Reference or an Object Reference list: " + propertyPath);
        }

        private static IEnumerable<CandidateData> AssetObjectPickerCandidates(Type expectedType)
        {
            var candidates = new List<CandidateData>();
            foreach (var guid in AssetDatabase.FindAssets("t:" + expectedType.Name))
            {
                var assetPath = AssetDatabase.GUIDToAssetPath(guid);
                foreach (var typeGroup in AssetDatabase.LoadAllAssetsAtPath(assetPath)
                    .Where(asset => asset != null && expectedType.IsAssignableFrom(asset.GetType()))
                    .GroupBy(asset => asset.GetType()))
                {
                    var typedAssets = typeGroup.ToArray();
                    for (var index = 0; index < typedAssets.Length; index++)
                    {
                        var asset = typedAssets[index];
                        candidates.Add(new CandidateData
                        {
                            label = asset.name,
                            path = assetPath + "#" + Uri.EscapeDataString(asset.GetType().FullName) + "[" + index + "]",
                            type = asset.GetType().FullName,
                            source = "asset"
                        });
                    }
                }
            }
            if (typeof(Component).IsAssignableFrom(expectedType))
            {
                foreach (var guid in AssetDatabase.FindAssets("t:Prefab"))
                {
                    var assetPath = AssetDatabase.GUIDToAssetPath(guid);
                    var root = AssetDatabase.LoadAssetAtPath<GameObject>(assetPath);
                    if (root == null)
                        continue;
                    foreach (var component in root.GetComponentsInChildren(expectedType, true).Cast<Component>())
                    {
                        var concreteType = component.GetType();
                        var components = component.gameObject.GetComponents(concreteType).Cast<Component>().ToArray();
                        var index = Array.IndexOf(components, component);
                        var childPath = PrefabChildIndexPath(root.transform, component.transform);
                        candidates.Add(new CandidateData
                        {
                            label = component.gameObject.name + " (" + concreteType.Name + " #" + index + ")",
                            path = assetPath + "#" + (childPath.Length == 0 ? string.Empty : childPath + "#") + Uri.EscapeDataString(concreteType.FullName) + "[" + index + "]",
                            type = concreteType.FullName,
                            source = "asset"
                        });
                    }
                }
            }
            return candidates
                .GroupBy(item => item.path + "\n" + item.type)
                .Select(group => group.First());
        }

        public static Type ResolveComponentType(string typeName)
        {
            if (string.IsNullOrWhiteSpace(typeName))
                throw new ArgumentException("Component type is required.", "typeName");

            var types = TypeCache.GetTypesDerivedFrom<Component>()
                .Where(type => !type.IsAbstract && (type.FullName == typeName || type.Name == typeName))
                .ToArray();
            if (types.Length == 0)
                throw new InvalidOperationException("Component type was not found: " + typeName);
            if (types.Length > 1)
                throw new InvalidOperationException("Component type name is ambiguous; use the full name: " + string.Join(", ", types.Select(type => type.FullName).ToArray()));
            return types[0];
        }

        internal static Component ResolveAttachedComponent(GameObject gameObject, string typeName, int componentIndex)
        {
            var type = ResolveComponentType(typeName);
            var components = gameObject.GetComponents(type).Cast<Component>().ToArray();
            if (components.Length == 0)
                throw new InvalidOperationException("Object has no component of type " + type.FullName + "; its components: " +
                    string.Join(", ", gameObject.GetComponents<Component>().Where(item => item != null).Select(item => item.GetType().Name)) + ".");
            if (componentIndex < 0 && components.Length > 1)
                throw new InvalidOperationException("Object has " + components.Length + " components of type " + type.FullName + "; provide componentIndex.");
            var index = componentIndex < 0 ? 0 : componentIndex;
            if (index >= components.Length)
                throw new InvalidOperationException("componentIndex is outside the available range 0.." + (components.Length - 1) + ".");
            return components[index];
        }

        internal static bool ApplyValues(Component component, PropertyValue[] values, out string warning)
        {
            warning = string.Empty;
            if (values == null || values.Length == 0)
                return false;

            var serializedObject = new SerializedObject(component);
            var expectedValues = new Dictionary<string, string>();
            var errors = new List<string>();
            Vector3? expectedPosition = null;
            Vector3? requestedLocalEuler = null;
            Vector3? requestedWorldEuler = null;
            var originalPosition = component.transform.position;
            var originalRotation = component.transform.rotation;
            foreach (var entry in values.OrderBy(entry => entry != null && entry.path != null && entry.path.EndsWith(".Array.size", StringComparison.Ordinal) ? 0 : 1))
            {
                try
                {
                    if (entry == null || string.IsNullOrWhiteSpace(entry.path))
                        throw new InvalidOperationException("Every component value requires a serialized property path.");
                    var transform = component as Transform;
                    if (transform != null && string.Equals(entry.path, "position", StringComparison.Ordinal))
                    {
                        var position = ParseVector(entry.value, transform.position);
                        transform.position = new Vector3(position.x, position.y, position.z);
                        expectedPosition = transform.position;
                        continue;
                    }
                    bool localEuler;
                    string eulerAxis;
                    if (transform != null && TryEulerPath(entry.path, out localEuler, out eulerAxis))
                    {
                        var current = localEuler
                            ? requestedLocalEuler ?? transform.localEulerAngles
                            : requestedWorldEuler ?? transform.eulerAngles;
                        var changedEuler = ApplyEulerValue(current, eulerAxis, entry.value);
                        if (localEuler)
                            requestedLocalEuler = changedEuler;
                        else
                            requestedWorldEuler = changedEuler;
                        continue;
                    }
                    if (FrameSettingsText.TrySetCamera(serializedObject, entry.path, entry.value))
                        continue;
                    var property = FindProperty(serializedObject, entry.path);
                    if (property == null)
                        throw new InvalidOperationException("Serialized property was not found: " + entry.path +
                            (transform != null ? "; Transform takes position|localPosition, eulerAngles|localEulerAngles (x/y/z), localScale." : string.Empty));
                    if (entry.remove)
                        RemovePersistentCall(property, entry.value);
                    else
                        SetProperty(property, entry.value, entry.append);
                    expectedValues[property.propertyPath] = ComparableValue(property);
                }
                catch (Exception error)
                {
                    errors.Add((entry == null || string.IsNullOrWhiteSpace(entry.path) ? "<empty>" : entry.path) + ": " + error.Message);
                }
            }
            if (errors.Count > 0)
            {
                serializedObject.Update();
                component.transform.position = originalPosition;
                throw new InvalidOperationException(string.Join("; ", errors.ToArray()));
            }
            var changed = serializedObject.ApplyModifiedProperties();
            if (requestedLocalEuler.HasValue)
                component.transform.localEulerAngles = requestedLocalEuler.Value;
            if (requestedWorldEuler.HasValue)
                component.transform.eulerAngles = requestedWorldEuler.Value;
            var expectedRotation = component.transform.rotation;
            serializedObject.Update();
            foreach (var expected in expectedValues)
            {
                var actual = serializedObject.FindProperty(expected.Key);
                if (actual == null || !RetainedValueMatches(actual, expected.Value))
                {
                    var hint = component is RectTransform && expected.Key == "m_LocalPosition"
                        ? " RectTransform layout may drive this field; use m_AnchoredPosition."
                        : string.Empty;
                    throw new InvalidOperationException("Unity did not retain the serialized value: " + expected.Key + "." + hint);
                }
            }
            if (expectedPosition.HasValue && component.transform.position != expectedPosition.Value)
                throw new InvalidOperationException("Unity did not retain the Transform world position.");
            if ((requestedLocalEuler.HasValue || requestedWorldEuler.HasValue) && Quaternion.Angle(component.transform.rotation, expectedRotation) > 0.01f)
                throw new InvalidOperationException("Unity did not retain the Transform Euler rotation.");
            ScenePersistenceService.MarkDirty(component.gameObject.scene);
            EditorPresentationService.RevealProperties(component, expectedValues.Keys);
            return changed || component.transform.position != originalPosition || component.transform.rotation != originalRotation;
        }

        private static bool TryEulerPath(string path, out bool local, out string axis)
        {
            axis = string.Empty;
            local = false;
            if (string.IsNullOrWhiteSpace(path))
                return false;
            var normalized = path.Trim();
            const string localName = "localEulerAngles";
            const string worldName = "eulerAngles";
            if (normalized.StartsWith(localName, StringComparison.OrdinalIgnoreCase))
            {
                local = true;
                axis = normalized.Length == localName.Length ? string.Empty : normalized.Substring(localName.Length).TrimStart('.').ToLowerInvariant();
                return axis.Length == 0 || axis == "x" || axis == "y" || axis == "z";
            }
            if (!normalized.StartsWith(worldName, StringComparison.OrdinalIgnoreCase))
                return false;
            axis = normalized.Length == worldName.Length ? string.Empty : normalized.Substring(worldName.Length).TrimStart('.').ToLowerInvariant();
            return axis.Length == 0 || axis == "x" || axis == "y" || axis == "z";
        }

        private static Vector3 ApplyEulerValue(Vector3 current, string axis, string rawValue)
        {
            if (axis == "x" || axis == "y" || axis == "z")
            {
                var value = float.Parse(rawValue, NumberStyles.Float, CultureInfo.InvariantCulture);
                if (axis == "x") current.x = value;
                else if (axis == "y") current.y = value;
                else current.z = value;
                return current;
            }
            if (!rawValue.TrimStart().StartsWith("{", StringComparison.Ordinal))
            {
                var angles = ParseVector(rawValue);
                return new Vector3(angles.x, angles.y, angles.z);
            }
            if (!HasField(rawValue, "x") && !HasField(rawValue, "y") && !HasField(rawValue, "z"))
                throw new FormatException("Euler angle value must contain x, y or z.");
            var valueBox = JsonUtility.FromJson<VectorBox>(rawValue);
            if (HasField(rawValue, "x")) current.x = valueBox.x;
            if (HasField(rawValue, "y")) current.y = valueBox.y;
            if (HasField(rawValue, "z")) current.z = valueBox.z;
            return current;
        }

        private static bool RetainedValueMatches(SerializedProperty actual, string expected)
        {
            var current = ComparableValue(actual);
            if (string.Equals(current, expected, StringComparison.Ordinal))
                return true;
            switch (actual.propertyType)
            {
                case SerializedPropertyType.Float:
                case SerializedPropertyType.Color:
                case SerializedPropertyType.Vector2:
                case SerializedPropertyType.Vector3:
                case SerializedPropertyType.Vector4:
                case SerializedPropertyType.Rect:
                case SerializedPropertyType.Bounds:
                case SerializedPropertyType.Quaternion:
                    var left = ParseComparableNumbers(current);
                    var right = ParseComparableNumbers(expected);
                    if (left.Length != right.Length)
                        return false;
                    if (actual.propertyType == SerializedPropertyType.Quaternion && left.Length == 4)
                        return NumbersClose(left, right, 1f) || NumbersClose(left, right, -1f);
                    return NumbersClose(left, right, 1f);
                default:
                    return false;
            }
        }

        private static float[] ParseComparableNumbers(string value)
        {
            return (value ?? string.Empty).Split('|')
                .Select(item => float.Parse(item, NumberStyles.Float, CultureInfo.InvariantCulture))
                .ToArray();
        }

        private static bool NumbersClose(float[] left, float[] right, float sign)
        {
            for (var index = 0; index < left.Length; index++)
            {
                var target = right[index] * sign;
                if (Mathf.Abs(left[index] - target) > 0.0001f * Mathf.Max(1f, Mathf.Abs(target)))
                    return false;
            }
            return true;
        }

        internal static string ComparableValue(SerializedProperty property)
        {
            switch (property.propertyType)
            {
                case SerializedPropertyType.Integer:
                    if (property.numericType == SerializedPropertyNumericType.UInt64)
                        return property.ulongValue.ToString(CultureInfo.InvariantCulture);
                    if (property.numericType == SerializedPropertyNumericType.Int64)
                        return property.longValue.ToString(CultureInfo.InvariantCulture);
                    if (property.numericType == SerializedPropertyNumericType.UInt32)
                        return property.uintValue.ToString(CultureInfo.InvariantCulture);
                    return property.intValue.ToString(CultureInfo.InvariantCulture);
                case SerializedPropertyType.LayerMask:
                case SerializedPropertyType.Character:
                case SerializedPropertyType.ArraySize:
                    return property.intValue.ToString(CultureInfo.InvariantCulture);
                case SerializedPropertyType.Boolean:
                    return property.boolValue ? "true" : "false";
                case SerializedPropertyType.Float:
                    return property.doubleValue.ToString("R", CultureInfo.InvariantCulture);
                case SerializedPropertyType.String:
                    return property.stringValue ?? string.Empty;
                case SerializedPropertyType.Color:
                    return Vector(property.colorValue.r, property.colorValue.g, property.colorValue.b, property.colorValue.a);
                case SerializedPropertyType.ObjectReference:
                    return property.objectReferenceValue == null ? "null" : UnityObjectIdentity.TransientId(property.objectReferenceValue).ToString();
                case SerializedPropertyType.Enum:
                    return property.enumValueIndex.ToString(CultureInfo.InvariantCulture);
                case SerializedPropertyType.Vector2:
                    return Vector(property.vector2Value.x, property.vector2Value.y);
                case SerializedPropertyType.Vector3:
                    return Vector(property.vector3Value.x, property.vector3Value.y, property.vector3Value.z);
                case SerializedPropertyType.Vector4:
                    return Vector(property.vector4Value.x, property.vector4Value.y, property.vector4Value.z, property.vector4Value.w);
                case SerializedPropertyType.Vector2Int:
                    return property.vector2IntValue.x.ToString(CultureInfo.InvariantCulture) + "|" +
                        property.vector2IntValue.y.ToString(CultureInfo.InvariantCulture);
                case SerializedPropertyType.Vector3Int:
                    return property.vector3IntValue.x.ToString(CultureInfo.InvariantCulture) + "|" +
                        property.vector3IntValue.y.ToString(CultureInfo.InvariantCulture) + "|" +
                        property.vector3IntValue.z.ToString(CultureInfo.InvariantCulture);
                case SerializedPropertyType.Rect:
                    return Vector(property.rectValue.x, property.rectValue.y, property.rectValue.width, property.rectValue.height);
                case SerializedPropertyType.Bounds:
                    var bounds = property.boundsValue;
                    return Vector(bounds.center.x, bounds.center.y, bounds.center.z, bounds.size.x, bounds.size.y, bounds.size.z);
                case SerializedPropertyType.Quaternion:
                    var quaternion = property.quaternionValue;
                    return Vector(quaternion.x, quaternion.y, quaternion.z, quaternion.w);
                case SerializedPropertyType.AnimationCurve:
                    return AnimationCurveSignature(property.animationCurveValue);
                case SerializedPropertyType.Gradient:
                    return GradientSignature(property.gradientValue);
                case SerializedPropertyType.ExposedReference:
                case SerializedPropertyType.Generic:
                    if (IsExposedReference(property))
                        return property.FindPropertyRelative("exposedName").stringValue + "|" + ComparableValue(property.FindPropertyRelative("defaultValue"));
                    if (property.type == "RectOffset")
                        return string.Join("|", new[] { "m_Left", "m_Right", "m_Top", "m_Bottom" }
                            .Select(name => property.FindPropertyRelative(name).intValue.ToString(CultureInfo.InvariantCulture)).ToArray());
                    SerializedProperty persistentCalls;
                    if (TryPersistentCalls(property, out persistentCalls))
                        return PersistentCallsSignature(persistentCalls);
                    if (property.type == "PersistentCall")
                        return PersistentCallSignature(property);
                    if (property.FindPropertyRelative("minMaxState") != null || property.FindPropertyRelative("enabled") != null)
                        return string.Join("|", ParticleValueParts(property));
                    if (property.isArray)
                        return property.arraySize + "[" + string.Join(";", Enumerable.Range(0, property.arraySize)
                            .Select(index => ComparableValue(property.GetArrayElementAtIndex(index))).ToArray()) + "]";
                    if (property.hasVisibleChildren)
                        return "{" + string.Join(";", AssetViews.ChildProperties(property).Select(ComparableValue).ToArray()) + "}";
                    throw new NotSupportedException("Serialized Generic property is not supported: " + property.type);
                default:
                    throw new NotSupportedException("Serialized property cannot be verified by the component agent: " + property.propertyType);
            }
        }

        internal static SerializedProperty FindProperty(SerializedObject serializedObject, string path)
        {
            if (serializedObject.targetObject is AudioSource &&
                new[] { "clip", "audioClip", "m_AudioClip", "m_audioClip", "m_Resource" }
                    .Any(name => string.Equals(name, path, StringComparison.OrdinalIgnoreCase)))
            {
                var audioClip = serializedObject.FindProperty("m_Resource") ?? serializedObject.FindProperty("m_audioClip");
                if (audioClip != null)
                    return audioClip;
            }
            var property = serializedObject.FindProperty(path);
            if (property != null || string.IsNullOrEmpty(path))
                return property;
            var array = path.IndexOf(".Array.", StringComparison.Ordinal);
            if (array > 0)
            {
                // "Materials.Array.data[0]": the list is named as elsewhere, the element path stays as Unity writes it.
                var list = FindProperty(serializedObject, path.Substring(0, array));
                return list == null ? null : list.FindPropertyRelative(path.Substring(array + 1));
            }
            if (path.IndexOf('.') >= 0)
            {
                var segments = path.Split('.');
                property = serializedObject.FindProperty(segments[0]) ??
                    serializedObject.FindProperty(SerializedFieldName(segments[0]));
                for (var index = 1; property != null && index < segments.Length; index++)
                    property = property.FindPropertyRelative(segments[index]) ??
                        property.FindPropertyRelative(SerializedFieldName(segments[index]));
                if (property != null)
                    return property;
            }
            if (path.IndexOf('.') < 0 && !path.StartsWith("m_", StringComparison.Ordinal))
            {
                var serializedName = SerializedFieldName(path);
                property = serializedObject.FindProperty(serializedName);
                if (property != null)
                    return property;
            }
            if (path.IndexOf('.') >= 0)
                return null;
            var iterator = serializedObject.GetIterator();
            var enterChildren = true;
            var normalized = NormalizePropertyName(path);
            SerializedProperty normalizedMatch = null;
            while (iterator.Next(enterChildren))
            {
                enterChildren = false;
                if (string.Equals(iterator.displayName, path, StringComparison.OrdinalIgnoreCase))
                    return iterator.Copy();
                if (NormalizePropertyName(iterator.displayName) != normalized &&
                    NormalizePropertyName(iterator.propertyPath) != normalized &&
                    NormalizePropertyName(PropertyLeaf(iterator.propertyPath)) != normalized)
                    continue;
                if (normalizedMatch != null)
                    throw new InvalidOperationException("Serialized property name is ambiguous: " + path);
                normalizedMatch = iterator.Copy();
            }
            return normalizedMatch;
        }

        private static string PropertyLeaf(string path)
        {
            var separator = string.IsNullOrEmpty(path) ? -1 : path.LastIndexOf('.');
            return separator < 0 ? path : path.Substring(separator + 1);
        }

        private static string NormalizePropertyName(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
                return string.Empty;
            if (value.StartsWith("m_", StringComparison.OrdinalIgnoreCase))
                value = value.Substring(2);
            return new string(value.Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());
        }

        private static string SerializedFieldName(string name)
        {
            return string.IsNullOrEmpty(name) || name.StartsWith("m_", StringComparison.Ordinal)
                ? name
                : "m_" + char.ToUpperInvariant(name[0]) + name.Substring(1);
        }

        private static string Vector(params float[] values)
        {
            return string.Join("|", values.Select(value => value.ToString("R", CultureInfo.InvariantCulture)).ToArray());
        }

        internal static void EnsureEditMode()
        {
            if (EditorApplication.isPlayingOrWillChangePlaymode)
                throw new InvalidOperationException("Component changes are not allowed in Play Mode because Unity discards them when Play Mode stops.");
        }

        // Script fields the Inspector does not draw ([HideInInspector], obsolete data, m_EditorClassIdentifier);
        // they stay writable by path. Built-in components keep everything: their editors draw hidden fields too.
        // Serialized values; a Transform shows Rotation as the Inspector does, under the name --set takes.
        internal static string ValuesJson(Component component)
        {
            var json = EditorJsonUtility.ToJson(component, false);
            var transform = component as Transform;
            var body = json.IndexOf('{', 1);
            if (body < 0)
                return json;
            if (transform != null)
                return json.Insert(body + 1, "\"localEulerAngles\":" + JsonUtility.ToJson(TransformUtils.GetInspectorRotation(transform)) + ",");
            // An HDRP camera's Custom Frame Settings as the overridden checkboxes, under the key --set takes.
            var serialized = component is MonoBehaviour ? new SerializedObject(component) : null;
            if (serialized != null && FrameSettingsText.IsCamera(serialized))
                return json.Insert(body + 1, "\"" + FrameSettingsText.CameraKey + "\":\"" + FrameSettingsText.CameraOverrides(serialized) + "\",");
            return json;
        }

        internal static string[] HiddenFields(Component component)
        {
            if (component is Transform)
                return new[] { "m_LocalRotation", "m_LocalEulerAnglesHint" };
            if (!(component is MonoBehaviour))
                return Array.Empty<string>();
            var serializedObject = new SerializedObject(component);
            if (FrameSettingsText.IsCamera(serializedObject))
                return HiddenScriptFields(component, serializedObject).Concat(FrameSettingsText.CameraFields).ToArray();
            return HiddenScriptFields(component, serializedObject);
        }

        private static string[] HiddenScriptFields(Component component, SerializedObject serializedObject)
        {
            var visible = new HashSet<string>(StringComparer.Ordinal);
            var iterator = serializedObject.GetIterator();
            for (var enter = true; iterator.NextVisible(enter); enter = false)
                visible.Add(iterator.name);
            var hidden = new List<string>();
            iterator = serializedObject.GetIterator();
            for (var enter = true; iterator.Next(enter); enter = false)
                if (!visible.Contains(iterator.name) && iterator.name != "m_Enabled" || IsObsoleteField(component.GetType(), iterator.name))
                    hidden.Add(iterator.name);
            return hidden.ToArray();
        }

        // Serialized data kept only for migration ([Obsolete]) is not part of what the component shows.
        private static bool IsObsoleteField(Type type, string name)
        {
            for (; type != null && type != typeof(MonoBehaviour); type = type.BaseType)
            {
                var field = type.GetField(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly);
                if (field != null)
                    return field.IsDefined(typeof(ObsoleteAttribute), false);
            }
            return false;
        }

        internal static SerializedPropertyData[] DescribeReferences(Component component)
        {
            var result = new List<SerializedPropertyData>();
            var serializedObject = new SerializedObject(component);
            var iterator = serializedObject.GetIterator();
            var enterChildren = true;
            while (iterator.NextVisible(enterChildren))
            {
                enterChildren = iterator.hasVisibleChildren;
                if (iterator.propertyType != SerializedPropertyType.ObjectReference || iterator.propertyPath == "m_Script")
                    continue;
                string referencePath;
                if (!TryObjectReferencePath(iterator.objectReferenceValue, out referencePath))
                    continue;
                result.Add(new SerializedPropertyData
                {
                    path = iterator.propertyPath,
                    type = iterator.propertyType.ToString(),
                    value = referencePath,
                    writable = true
                });
            }
            return result.ToArray();
        }

        private static bool TryObjectReferencePath(UnityEngine.Object reference, out string path)
        {
            if (reference == null)
            {
                path = string.Empty;
                return true;
            }
            var assetPath = AssetDatabase.GetAssetPath(reference);
            if (!string.IsNullOrEmpty(assetPath))
            {
                var persistentComponent = reference as Component;
                if (persistentComponent != null && assetPath.EndsWith(".prefab", StringComparison.OrdinalIgnoreCase))
                {
                    var root = persistentComponent.transform.root;
                    var persistentType = persistentComponent.GetType();
                    var components = persistentComponent.gameObject.GetComponents(persistentType).Cast<Component>().ToArray();
                    var persistentIndex = Array.IndexOf(components, persistentComponent);
                    var childPath = PrefabChildIndexPath(root, persistentComponent.transform);
                    path = assetPath + "#" + (childPath.Length == 0 ? string.Empty : childPath + "#") + Uri.EscapeDataString(persistentType.FullName) + "[" + persistentIndex + "]";
                    return true;
                }
                if (!AssetDatabase.IsMainAsset(reference))
                {
                    var referenceType = reference.GetType();
                    var subAssets = AssetDatabase.LoadAllAssetsAtPath(assetPath)
                        .Where(item => item != null && item.GetType() == referenceType)
                        .ToArray();
                    var subAssetIndex = Array.IndexOf(subAssets, reference);
                    if (subAssetIndex < 0)
                        throw new InvalidOperationException("Referenced sub-asset is missing from its asset file.");
                    // Built-in assets read by name, as the picker shows them (Library/unity default resources#Sphere).
                    var builtin = !assetPath.StartsWith("Assets/", StringComparison.Ordinal) && !assetPath.StartsWith("Packages/", StringComparison.Ordinal);
                    path = builtin && subAssets.Count(item => item.name == reference.name) == 1
                        ? assetPath + "#" + reference.name
                        : assetPath + "#" + Uri.EscapeDataString(referenceType.FullName) + "[" + subAssetIndex + "]";
                    return true;
                }
                path = assetPath;
                return true;
            }
            var owner = Owner(reference);
            if (owner == null || !IsListedLoadedScene(owner.scene))
            {
                path = string.Empty;
                return false;
            }
            path = ScenePath.For(owner);
            var component = reference as Component;
            if (component == null)
                return true;
            var concreteType = component.GetType();
            var matching = owner.GetComponents(concreteType).Cast<Component>().ToArray();
            var index = Array.IndexOf(matching, component);
            if (index < 0)
                throw new InvalidOperationException("Referenced component is missing from its GameObject.");
            path += "#" + Uri.EscapeDataString(concreteType.FullName) + "[" + index + "]";
            return true;
        }

        internal static void SetProperty(SerializedProperty property, string rawValue, bool append = false, IExposedPropertyTable resolver = null)
        {
            switch (property.propertyType)
            {
                case SerializedPropertyType.Gradient:
                    property.gradientValue = CurveText.Is(PlainText(rawValue)) ? CurveText.Gradient(PlainText(rawValue)) : ParseGradient(rawValue);
                    return;
                case SerializedPropertyType.Integer:
                    if (IntChoices.TrySet(property, rawValue.TrimStart().StartsWith("\"", StringComparison.Ordinal) ? ParseJsonString(rawValue) : rawValue.Trim()))
                        return;
                    if (property.numericType == SerializedPropertyNumericType.UInt64)
                        property.ulongValue = ulong.Parse(rawValue, NumberStyles.Integer, CultureInfo.InvariantCulture);
                    else if (property.numericType == SerializedPropertyNumericType.Int64)
                        property.longValue = long.Parse(rawValue, NumberStyles.Integer, CultureInfo.InvariantCulture);
                    else if (property.numericType == SerializedPropertyNumericType.UInt32)
                        property.uintValue = uint.Parse(rawValue, NumberStyles.Integer, CultureInfo.InvariantCulture);
                    else
                        property.intValue = int.Parse(rawValue, NumberStyles.Integer, CultureInfo.InvariantCulture);
                    return;
                case SerializedPropertyType.LayerMask:
                case SerializedPropertyType.Character:
                case SerializedPropertyType.ArraySize:
                    property.intValue = int.Parse(rawValue, NumberStyles.Integer, CultureInfo.InvariantCulture);
                    return;
                case SerializedPropertyType.Boolean:
                    property.boolValue = rawValue == "1" ? true : rawValue == "0" ? false : bool.Parse(rawValue);
                    return;
                case SerializedPropertyType.Float:
                    property.doubleValue = double.Parse(rawValue, NumberStyles.Float, CultureInfo.InvariantCulture);
                    return;
                case SerializedPropertyType.String:
                    property.stringValue = ParseJsonString(rawValue);
                    return;
                case SerializedPropertyType.Color:
                    var color = ParseColor(rawValue);
                    property.colorValue = new Color(color.r, color.g, color.b, color.a);
                    return;
                case SerializedPropertyType.ObjectReference:
                    property.objectReferenceValue = ResolveObjectReference(property.type, ParseJsonString(rawValue));
                    return;
                case SerializedPropertyType.Enum:
                    SetEnum(property, rawValue);
                    return;
                case SerializedPropertyType.Vector2:
                    var vector2 = ParseVector(rawValue, property.vector2Value);
                    property.vector2Value = new Vector2(vector2.x, vector2.y);
                    return;
                case SerializedPropertyType.Vector3:
                    var vector3 = ParseVector(rawValue, property.vector3Value);
                    property.vector3Value = new Vector3(vector3.x, vector3.y, vector3.z);
                    return;
                case SerializedPropertyType.Vector4:
                    var vector4 = ParseVector(rawValue, property.vector4Value);
                    property.vector4Value = new Vector4(vector4.x, vector4.y, vector4.z, vector4.w);
                    return;
                case SerializedPropertyType.Vector2Int:
                    if (rawValue == null || !HasField(rawValue, "x") || !HasField(rawValue, "y"))
                        throw new FormatException("Vector2Int value must contain x and y fields.");
                    var vector2Int = JsonUtility.FromJson<VectorIntBox>(rawValue);
                    property.vector2IntValue = new Vector2Int(vector2Int.x, vector2Int.y);
                    return;
                case SerializedPropertyType.Vector3Int:
                    if (rawValue == null || !HasField(rawValue, "x") || !HasField(rawValue, "y") || !HasField(rawValue, "z"))
                        throw new FormatException("Vector3Int value must contain x, y, and z fields.");
                    var vector3Int = JsonUtility.FromJson<VectorIntBox>(rawValue);
                    property.vector3IntValue = new Vector3Int(vector3Int.x, vector3Int.y, vector3Int.z);
                    return;
                case SerializedPropertyType.Rect:
                    var rect = JsonUtility.FromJson<RectBox>(rawValue);
                    property.rectValue = new Rect(rect.x, rect.y, rect.width, rect.height);
                    return;
                case SerializedPropertyType.Bounds:
                    var bounds = JsonUtility.FromJson<BoundsBox>(rawValue);
                    if (bounds == null || bounds.center == null || bounds.size == null)
                        throw new FormatException("Bounds value must contain center and size objects.");
                    property.boundsValue = new Bounds(
                        new Vector3(bounds.center.x, bounds.center.y, bounds.center.z),
                        new Vector3(bounds.size.x, bounds.size.y, bounds.size.z));
                    return;
                case SerializedPropertyType.Quaternion:
                    var quaternion = ParseVector(rawValue, new Vector4(property.quaternionValue.x, property.quaternionValue.y, property.quaternionValue.z, property.quaternionValue.w));
                    property.quaternionValue = new Quaternion(quaternion.x, quaternion.y, quaternion.z, quaternion.w);
                    return;
                case SerializedPropertyType.ExposedReference:
                case SerializedPropertyType.Generic:
                    if (IsExposedReference(property))
                    {
                        SetExposedReference(property, ParseJsonString(rawValue), resolver ?? DefaultResolver(property));
                        return;
                    }
                    if (property.type == "RectOffset")
                    {
                        if (rawValue == null || !HasEitherField(rawValue, "left", "m_Left") ||
                            !HasEitherField(rawValue, "right", "m_Right") || !HasEitherField(rawValue, "top", "m_Top") ||
                            !HasEitherField(rawValue, "bottom", "m_Bottom"))
                            throw new FormatException("RectOffset value must contain left/right/top/bottom or m_Left/m_Right/m_Top/m_Bottom fields.");
                        var offset = JsonUtility.FromJson<RectOffsetBox>(rawValue);
                        property.FindPropertyRelative("m_Left").intValue = HasField(rawValue, "left") ? offset.left : offset.m_Left;
                        property.FindPropertyRelative("m_Right").intValue = HasField(rawValue, "right") ? offset.right : offset.m_Right;
                        property.FindPropertyRelative("m_Top").intValue = HasField(rawValue, "top") ? offset.top : offset.m_Top;
                        property.FindPropertyRelative("m_Bottom").intValue = HasField(rawValue, "bottom") ? offset.bottom : offset.m_Bottom;
                        return;
                    }
                    SerializedProperty calls;
                    if (TryPersistentCalls(property, out calls))
                    {
                        if (append)
                            AppendPersistentCalls(calls, rawValue);
                        else
                            SetPersistentCalls(calls, rawValue);
                        return;
                    }
                    if (property.type == "PersistentCall")
                    {
                        SetPersistentCall(property, ParseUnityEventCall(rawValue));
                        return;
                    }
                    if (TrySetParticleValue(property, rawValue))
                        return;
                    // A list is filled at once, as dropping several assets on it in the Inspector: [a, b, c].
                    if (property.isArray && rawValue != null && rawValue.TrimStart().StartsWith("[", StringComparison.Ordinal))
                    {
                        var elements = JsonArrayElements(rawValue);
                        property.arraySize = elements.Count;
                        for (var index = 0; index < elements.Count; index++)
                            SetProperty(property.GetArrayElementAtIndex(index), elements[index], false, resolver);
                        return;
                    }
                    // A serialized class or struct takes its fields by name, as object-info shows it: {"solid": "a.mat"}.
                    if (property.hasChildren && rawValue != null && rawValue.TrimStart().StartsWith("{", StringComparison.Ordinal))
                    {
                        foreach (var field in JsonTopLevel(rawValue, '{', '}'))
                        {
                            var colon = JsonKeyEnd(field);
                            var name = ParseJsonString(field.Substring(0, colon).Trim());
                            var child = property.FindPropertyRelative(name) ?? property.FindPropertyRelative("m_" + char.ToUpperInvariant(name[0]) + name.Substring(1));
                            if (child == null)
                                throw new ArgumentException(property.type + " has no field " + name + ".");
                            SetProperty(child, field.Substring(colon + 1).Trim(), false, resolver);
                        }
                        return;
                    }
                    throw new NotSupportedException("Serialized Generic property is not supported: " + property.type);
                case SerializedPropertyType.AnimationCurve:
                    property.animationCurveValue = CurveText.Is(PlainText(rawValue)) ? CurveText.Curve(PlainText(rawValue)) : ParseAnimationCurve(rawValue);
                    return;
                default:
                    throw new NotSupportedException("Serialized property type is not supported by the component agent: " + property.propertyType);
            }
        }

        // Top-level elements of a JSON array, each kept as JSON text.
        internal static List<string> JsonArrayElements(string json)
        {
            return JsonTopLevel(json, '[', ']');
        }

        // Index of the colon after a JSON object key.
        private static int JsonKeyEnd(string field)
        {
            var quoted = false;
            for (var index = 0; index < field.Length; index++)
            {
                var character = field[index];
                if (quoted && character == '\\')
                    index++;
                else if (character == '"')
                    quoted = !quoted;
                else if (!quoted && character == ':')
                    return index;
            }
            throw new FormatException("Expected \"field\": value in " + field);
        }

        private static List<string> JsonTopLevel(string json, char open, char close)
        {
            var elements = new List<string>();
            var text = json.Trim();
            if (text.Length < 2 || text[0] != open || text[text.Length - 1] != close)
                throw new FormatException(open == '[' ? "List value must be a JSON array." : "Value must be a JSON object.");
            var depth = 0;
            var start = 1;
            var quoted = false;
            for (var index = 1; index < text.Length - 1; index++)
            {
                var character = text[index];
                if (quoted)
                {
                    if (character == '\\')
                        index++;
                    else if (character == '"')
                        quoted = false;
                    continue;
                }
                if (character == '"')
                    quoted = true;
                else if (character == '[' || character == '{')
                    depth++;
                else if (character == ']' || character == '}')
                    depth--;
                else if (character == ',' && depth == 0)
                {
                    elements.Add(text.Substring(start, index - start).Trim());
                    start = index + 1;
                }
            }
            var last = text.Substring(start, text.Length - 1 - start).Trim();
            if (last.Length > 0)
                elements.Add(last);
            return elements;
        }

        private static string PlainText(string rawValue)
        {
            return (rawValue.TrimStart().StartsWith("\"", StringComparison.Ordinal) ? ParseJsonString(rawValue) : rawValue).Trim();
        }

        // As object-info shows them: a Particle System module is true/false, a MinMaxCurve "0.5", "0.2..0.8",
        // a curve "0:0 1:1 *2" or two curves "… .. …", a MinMaxGradient "r,g,b,a", "r,g,b,a..r,g,b,a",
        // a gradient "0:#FF0000 1:#0000FF / 0:1 1:0" or two gradients "… .. …".
        private static bool TrySetParticleValue(SerializedProperty property, string rawValue)
        {
            var text = PlainText(rawValue);
            var state = property.FindPropertyRelative("minMaxState");
            var bounds = text.Split(new[] { ".." }, StringSplitOptions.None);
            if (state != null && property.FindPropertyRelative("scalar") != null && CurveText.Is(text))
            {
                var star = text.LastIndexOf('*');
                var multiplier = star < 0 ? 1f : float.Parse(text.Substring(star + 1), NumberStyles.Float, CultureInfo.InvariantCulture);
                var curves = (star < 0 ? text : text.Substring(0, star)).Split(new[] { " .. " }, StringSplitOptions.None);
                if (curves.Length > 2)
                    throw new FormatException("Use a curve or two curves separated by \" .. \": " + text);
                state.intValue = curves.Length == 1 ? 1 : 2;
                property.FindPropertyRelative("scalar").floatValue = multiplier;
                property.FindPropertyRelative("minCurve").animationCurveValue = CurveText.Curve(curves[0]);
                property.FindPropertyRelative("maxCurve").animationCurveValue = CurveText.Curve(curves[curves.Length - 1]);
                return true;
            }
            if (state != null && property.FindPropertyRelative("maxColor") != null && CurveText.Is(text))
            {
                var gradients = text.Split(new[] { " .. " }, StringSplitOptions.None);
                if (gradients.Length > 2)
                    throw new FormatException("Use a gradient or two gradients separated by \" .. \": " + text);
                state.intValue = gradients.Length == 1 ? 1 : 3;
                property.FindPropertyRelative("minGradient").gradientValue = CurveText.Gradient(gradients[0]);
                property.FindPropertyRelative("maxGradient").gradientValue = CurveText.Gradient(gradients[gradients.Length - 1]);
                return true;
            }
            if (state != null && property.FindPropertyRelative("scalar") != null)
            {
                var numbers = bounds.Select(item => float.Parse(item, NumberStyles.Float, CultureInfo.InvariantCulture)).ToArray();
                if (numbers.Length > 2)
                    throw new FormatException("Use a number or min..max: " + text);
                state.intValue = numbers.Length == 1 ? 0 : 3;
                property.FindPropertyRelative("minScalar").floatValue = numbers[0];
                property.FindPropertyRelative("scalar").floatValue = numbers[numbers.Length - 1];
                return true;
            }
            if (state != null && property.FindPropertyRelative("maxColor") != null)
            {
                if (bounds.Length > 2)
                    throw new FormatException("Use r,g,b,a or r,g,b,a..r,g,b,a: " + text);
                var colors = bounds.Select(item => ParseColor("\"" + item.Trim() + "\"")).Select(item => new Color(item.r, item.g, item.b, item.a)).ToArray();
                state.intValue = colors.Length == 1 ? 0 : 2;
                property.FindPropertyRelative("minColor").colorValue = colors[0];
                property.FindPropertyRelative("maxColor").colorValue = colors[colors.Length - 1];
                return true;
            }
            var enabled = property.FindPropertyRelative("enabled");
            if (enabled != null && enabled.propertyType == SerializedPropertyType.Boolean && (text == "true" || text == "false"))
            {
                enabled.boolValue = text == "true";
                return true;
            }
            return false;
        }

        private static IEnumerable<string> ParticleValueParts(SerializedProperty property)
        {
            foreach (var name in new[] { "minMaxState", "enabled" })
            {
                var part = property.FindPropertyRelative(name);
                if (part != null)
                    yield return ComparableValue(part);
            }
            foreach (var name in new[] { "minScalar", "scalar", "minColor", "maxColor" })
            {
                var part = property.FindPropertyRelative(name);
                if (part != null)
                    yield return ComparableValue(part);
            }
        }

        private static void SetEnum(SerializedProperty property, string rawValue)
        {
            int numericValue;
            if (int.TryParse(rawValue, NumberStyles.Integer, CultureInfo.InvariantCulture, out numericValue))
                throw new InvalidOperationException("Enum value must use a name. Available values: " + string.Join(", ", property.enumNames));

            var name = ParseJsonString(rawValue);
            var normalized = NormalizePropertyName(name);
            var match = Enumerable.Range(0, property.enumNames.Length)
                .Where(candidate =>
                    string.Equals(property.enumNames[candidate], name, StringComparison.OrdinalIgnoreCase) ||
                    NormalizePropertyName(property.enumNames[candidate]) == normalized ||
                    candidate < property.enumDisplayNames.Length &&
                    (string.Equals(property.enumDisplayNames[candidate], name, StringComparison.OrdinalIgnoreCase) ||
                     NormalizePropertyName(property.enumDisplayNames[candidate]) == normalized))
                .Select(candidate => (int?)candidate)
                .FirstOrDefault();
            if (!match.HasValue)
                throw new InvalidOperationException("Enum value was not found. Available values: " + string.Join(", ", property.enumDisplayNames.Length == property.enumNames.Length ? property.enumDisplayNames : property.enumNames));
            property.enumValueIndex = match.Value;
        }

        private static bool TryPersistentCalls(SerializedProperty property, out SerializedProperty calls)
        {
            if (property.isArray && string.Equals(property.arrayElementType, "PersistentCall", StringComparison.Ordinal))
            {
                calls = property;
                return true;
            }
            var persistent = property.FindPropertyRelative("m_PersistentCalls");
            calls = persistent == null ? null : persistent.FindPropertyRelative("m_Calls");
            return calls != null && calls.isArray;
        }

        private static void SetPersistentCalls(SerializedProperty calls, string rawValue)
        {
            if (string.IsNullOrWhiteSpace(rawValue))
                throw new FormatException("UnityEvent value must be a JSON array of calls.");
            var json = rawValue.Trim();
            if (json.StartsWith("{", StringComparison.Ordinal))
                json = "[" + json + "]";
            if (!json.StartsWith("[", StringComparison.Ordinal))
                throw new FormatException("UnityEvent value must be a JSON array of calls.");
            ValidateUnityEventFields(json);
            var box = JsonUtility.FromJson<UnityEventCallsBox>("{\"calls\":" + json + "}");
            var values = box == null || box.calls == null ? new UnityEventCallBox[0] : box.calls;
            calls.arraySize = values.Length;
            for (var index = 0; index < values.Length; index++)
                SetPersistentCall(calls.GetArrayElementAtIndex(index), values[index]);
        }

        private static void AppendPersistentCalls(SerializedProperty calls, string rawValue)
        {
            var value = ParseUnityEventCall(rawValue);
            var index = calls.arraySize;
            calls.InsertArrayElementAtIndex(index);
            SetPersistentCall(calls.GetArrayElementAtIndex(index), value);
        }

        private static void RemovePersistentCall(SerializedProperty property, string rawIndex)
        {
            SerializedProperty calls;
            if (!TryPersistentCalls(property, out calls))
                throw new InvalidOperationException("Remove is available only for a UnityEvent property.");
            int index;
            if (!int.TryParse(rawIndex, NumberStyles.Integer, CultureInfo.InvariantCulture, out index)
                || index < 0 || index >= calls.arraySize)
                throw new ArgumentOutOfRangeException("rawIndex", "UnityEvent listener index must be inside 0.." + (calls.arraySize - 1) + ".");
            calls.DeleteArrayElementAtIndex(index);
        }

        internal static SerializedPropertyData[] DescribeEnums(Component component)
        {
            var result = new List<SerializedPropertyData>();
            var serialized = new SerializedObject(component);
            var iterator = serialized.GetIterator();
            while (iterator.Next(true))
            {
                var choice = IntChoices.Name(iterator);
                if (choice != null)
                    result.Add(new SerializedPropertyData { path = iterator.propertyPath, type = "Enum", value = choice, writable = true });
                if (iterator.propertyType != SerializedPropertyType.Enum)
                    continue;
                var index = iterator.enumValueIndex;
                result.Add(new SerializedPropertyData
                {
                    path = iterator.propertyPath,
                    type = "Enum",
                    value = index >= 0 && index < iterator.enumNames.Length
                        ? iterator.enumNames[index]
                        : index.ToString(CultureInfo.InvariantCulture),
                    writable = true
                });
            }
            return result.ToArray();
        }

        private static UnityEventCallBox ParseUnityEventCall(string rawValue)
        {
            if (string.IsNullOrWhiteSpace(rawValue) || !rawValue.TrimStart().StartsWith("{", StringComparison.Ordinal))
                throw new FormatException("UnityEvent call must be a JSON object.");
            ValidateUnityEventFields(rawValue);
            var value = JsonUtility.FromJson<UnityEventCallBox>(rawValue);
            if (value == null)
                throw new FormatException("UnityEvent call is invalid.");
            return value;
        }

        private static void ValidateUnityEventFields(string json)
        {
            var allowed = new HashSet<string>(StringComparer.Ordinal)
            {
                "target", "method", "mode", "state", "objectArgument", "intArgument",
                "floatArgument", "stringArgument", "boolArgument"
            };
            var unknown = Regex.Matches(json, "\\\"([^\\\"]+)\\\"\\s*:")
                .Cast<Match>()
                .Select(match => match.Groups[1].Value)
                .FirstOrDefault(name => !allowed.Contains(name));
            if (!string.IsNullOrEmpty(unknown))
                throw new FormatException("Unknown UnityEvent field '" + unknown + "'.");
        }

        private static void SetPersistentCall(SerializedProperty property, UnityEventCallBox value)
        {
            if (value == null || string.IsNullOrWhiteSpace(value.target) || string.IsNullOrWhiteSpace(value.method))
                throw new FormatException("UnityEvent call requires target and method.");
            var mode = UnityEventMode(value.mode);
            var targetProperty = RequiredRelative(property, "m_Target");
            var target = ResolveObjectReference(targetProperty.type, value.target);
            MethodInfo targetMethod = null;
            var gameObject = target as GameObject;
            if (gameObject != null)
            {
                var gameObjectMethods = UnityEventMethods(gameObject.GetType(), value.method, mode);
                if (gameObjectMethods.Length == 1)
                    targetMethod = gameObjectMethods[0];
                var matches = targetMethod != null ? Array.Empty<Component>() : gameObject.GetComponents<Component>()
                    .Where(item => item != null && UnityEventMethods(item.GetType(), value.method, mode).Length > 0)
                    .ToArray();
                if (targetMethod == null && matches.Length == 1)
                {
                    target = matches[0];
                    var methods = UnityEventMethods(target.GetType(), value.method, mode);
                    if (methods.Length != 1)
                        throw new InvalidOperationException("UnityEvent target method is ambiguous for mode " + (value.mode ?? "Void") + ".");
                    targetMethod = methods[0];
                }
                else if (targetMethod == null && matches.Length == 0)
                    throw new InvalidOperationException("UnityEvent target has no compatible method " + value.method + " for mode " + (value.mode ?? "Void") + ".");
                else if (targetMethod == null)
                    throw new InvalidOperationException("UnityEvent target method is ambiguous; use an exact component reference from object-info.");
            }
            else
            {
                var methods = UnityEventMethods(target.GetType(), value.method, mode);
                if (methods.Length == 0)
                    throw new InvalidOperationException("UnityEvent target has no compatible method " + value.method + " for mode " + (value.mode ?? "Void") + ".");
                if (methods.Length > 1)
                    throw new InvalidOperationException("UnityEvent target method is ambiguous for mode " + (value.mode ?? "Void") + ".");
                targetMethod = methods[0];
            }
            targetProperty.objectReferenceValue = target;
            SetStringIfPresent(property, "m_TargetAssemblyTypeName", target.GetType().AssemblyQualifiedName);
            RequiredRelative(property, "m_MethodName").stringValue = value.method;
            RequiredRelative(property, "m_Mode").intValue = mode;
            RequiredRelative(property, "m_CallState").intValue = UnityEventState(value.state);
            var arguments = RequiredRelative(property, "m_Arguments");
            RequiredRelative(arguments, "m_ObjectArgument").objectReferenceValue = null;
            SetStringIfPresent(arguments, "m_ObjectArgumentAssemblyTypeName", typeof(UnityEngine.Object).AssemblyQualifiedName);
            RequiredRelative(arguments, "m_IntArgument").intValue = value.intArgument;
            RequiredRelative(arguments, "m_FloatArgument").floatValue = value.floatArgument;
            RequiredRelative(arguments, "m_StringArgument").stringValue = value.stringArgument ?? string.Empty;
            RequiredRelative(arguments, "m_BoolArgument").boolValue = value.boolArgument;
            if (mode == 2)
                SetStringIfPresent(arguments, "m_ObjectArgumentAssemblyTypeName", targetMethod.GetParameters()[0].ParameterType.AssemblyQualifiedName);
            if (mode == 2 && !string.IsNullOrWhiteSpace(value.objectArgument))
            {
                var objectArgument = RequiredRelative(arguments, "m_ObjectArgument");
                objectArgument.objectReferenceValue = ResolveObjectReference(objectArgument.type, value.objectArgument);
            }
        }

        private static MethodInfo[] UnityEventMethods(Type targetType, string methodName, int mode)
        {
            return targetType.GetMethods(BindingFlags.Instance | BindingFlags.Public)
                .Where(method => method.Name == methodName && UnityEventParametersMatch(method.GetParameters(), mode))
                .ToArray();
        }

        private static bool UnityEventParametersMatch(ParameterInfo[] parameters, int mode)
        {
            if (mode == 0)
                return true;
            if (mode == 1)
                return parameters.Length == 0;
            if (parameters.Length != 1)
                return false;
            switch (mode)
            {
                case 2: return typeof(UnityEngine.Object).IsAssignableFrom(parameters[0].ParameterType);
                case 3: return parameters[0].ParameterType == typeof(int);
                case 4: return parameters[0].ParameterType == typeof(float);
                case 5: return parameters[0].ParameterType == typeof(string);
                case 6: return parameters[0].ParameterType == typeof(bool);
                default: return false;
            }
        }

        private static SerializedProperty RequiredRelative(SerializedProperty property, string name)
        {
            var value = property.FindPropertyRelative(name);
            if (value == null)
                throw new InvalidOperationException("UnityEvent field is missing: " + name);
            return value;
        }

        private static void SetStringIfPresent(SerializedProperty property, string name, string value)
        {
            var target = property.FindPropertyRelative(name);
            if (target != null)
                target.stringValue = value ?? string.Empty;
        }

        private static int UnityEventMode(string value)
        {
            switch ((value ?? "Void").Trim().ToLowerInvariant())
            {
                case "eventdefined": return 0;
                case "void": return 1;
                case "object": return 2;
                case "int": return 3;
                case "float": return 4;
                case "string": return 5;
                case "bool": return 6;
                default: throw new ArgumentException("UnityEvent mode must be EventDefined, Void, Object, Int, Float, String, or Bool.", "value");
            }
        }

        private static int UnityEventState(string value)
        {
            switch ((value ?? "RuntimeOnly").Trim().ToLowerInvariant())
            {
                case "off": return 0;
                case "editorandruntime": return 1;
                case "runtimeonly": return 2;
                default: throw new ArgumentException("UnityEvent state must be Off, EditorAndRuntime, or RuntimeOnly.", "value");
            }
        }

        private static string PersistentCallsSignature(SerializedProperty calls)
        {
            var result = new string[calls.arraySize];
            for (var index = 0; index < calls.arraySize; index++)
                result[index] = PersistentCallSignature(calls.GetArrayElementAtIndex(index));
            return string.Join(";", result);
        }

        private static string PersistentCallSignature(SerializedProperty call)
        {
            var target = RequiredRelative(call, "m_Target").objectReferenceValue;
            var arguments = RequiredRelative(call, "m_Arguments");
            var objectArgument = RequiredRelative(arguments, "m_ObjectArgument").objectReferenceValue;
            return string.Join("|", new[]
            {
                target == null ? "0" : UnityObjectIdentity.TransientId(target).ToString(),
                RequiredRelative(call, "m_MethodName").stringValue ?? string.Empty,
                RequiredRelative(call, "m_Mode").intValue.ToString(CultureInfo.InvariantCulture),
                RequiredRelative(call, "m_CallState").intValue.ToString(CultureInfo.InvariantCulture),
                objectArgument == null ? "0" : UnityObjectIdentity.TransientId(objectArgument).ToString(),
                RequiredRelative(arguments, "m_IntArgument").intValue.ToString(CultureInfo.InvariantCulture),
                RequiredRelative(arguments, "m_FloatArgument").floatValue.ToString("R", CultureInfo.InvariantCulture),
                RequiredRelative(arguments, "m_StringArgument").stringValue ?? string.Empty,
                RequiredRelative(arguments, "m_BoolArgument").boolValue ? "1" : "0"
            });
        }

        // Fields a JSON object leaves out keep their current values, as when one field is edited in the Inspector.
        private static VectorBox ParseVector(string rawValue, Vector4 current = default(Vector4))
        {
            if (!string.IsNullOrWhiteSpace(rawValue) && !rawValue.TrimStart().StartsWith("{", StringComparison.Ordinal))
            {
                var parts = ParseJsonString(rawValue).Split(',');
                var numbers = new float[4];
                for (var index = 0; index < parts.Length; index++)
                    if (parts.Length > 4 || !float.TryParse(parts[index].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out numbers[index]))
                        throw new FormatException("Vector value must be x,y[,z[,w]] or a JSON object with x/y/z/w fields.");
                return new VectorBox { x = numbers[0], y = numbers[1], z = numbers[2], w = numbers[3] };
            }
            var result = new VectorBox { x = current.x, y = current.y, z = current.z, w = current.w };
            try
            {
                JsonUtility.FromJsonOverwrite(rawValue, result);
            }
            catch (ArgumentException)
            {
                throw new FormatException("Vector value must be x,y[,z[,w]] or a JSON object with x/y/z/w fields.");
            }
            return result;
        }

        private static AnimationCurve ParseAnimationCurve(string rawValue)
        {
            if (string.IsNullOrWhiteSpace(rawValue))
                throw new FormatException("AnimationCurve value must contain a JSON array of keys.");
            var json = rawValue.Trim();
            if (json.StartsWith("[", StringComparison.Ordinal))
                json = "{\"keys\":" + json + "}";
            var box = JsonUtility.FromJson<AnimationCurveBox>(json);
            var serializedKeys = box == null ? null : box.keys ?? box.m_Curve;
            if (serializedKeys == null)
                throw new FormatException("AnimationCurve value must be a key array or an object containing keys.");
            var usesTangentNames = HasField(json, "inTangent") || HasField(json, "outTangent");
            var usesWeights = HasField(json, "inWeight") || HasField(json, "outWeight");
            var usesWeightedMode = HasField(json, "weightedMode");
            var keys = serializedKeys.Select(value =>
            {
                var inTangent = usesTangentNames ? value.inTangent : value.inSlope;
                var outTangent = usesTangentNames ? value.outTangent : value.outSlope;
                var key = usesWeights
                    ? new Keyframe(value.time, value.value, inTangent, outTangent, value.inWeight, value.outWeight)
                    : new Keyframe(value.time, value.value, inTangent, outTangent);
                if (usesWeightedMode)
                    key.weightedMode = (WeightedMode)value.weightedMode;
                return key;
            }).ToArray();
            var curve = new AnimationCurve(keys);
            if (HasEitherField(json, "preWrapMode", "m_PreInfinity"))
                curve.preWrapMode = (WrapMode)(HasField(json, "preWrapMode") ? box.preWrapMode : box.m_PreInfinity);
            if (HasEitherField(json, "postWrapMode", "m_PostInfinity"))
                curve.postWrapMode = (WrapMode)(HasField(json, "postWrapMode") ? box.postWrapMode : box.m_PostInfinity);
            return curve;
        }

        private static string AnimationCurveSignature(AnimationCurve curve)
        {
            if (curve == null)
                return "null";
            var keys = curve.keys.Select(key => string.Join("|", new[]
            {
                key.time.ToString("R", CultureInfo.InvariantCulture),
                key.value.ToString("R", CultureInfo.InvariantCulture),
                key.inTangent.ToString("R", CultureInfo.InvariantCulture),
                key.outTangent.ToString("R", CultureInfo.InvariantCulture),
                key.inWeight.ToString("R", CultureInfo.InvariantCulture),
                key.outWeight.ToString("R", CultureInfo.InvariantCulture),
                ((int)key.weightedMode).ToString(CultureInfo.InvariantCulture)
            })).ToArray();
            return ((int)curve.preWrapMode).ToString(CultureInfo.InvariantCulture) + ":" +
                ((int)curve.postWrapMode).ToString(CultureInfo.InvariantCulture) + ":" + string.Join(";", keys);
        }

        private static void EnsureUnityScript(Type type)
        {
            if (!typeof(MonoBehaviour).IsAssignableFrom(type))
                return;
            var registered = MonoImporter.GetAllRuntimeMonoScripts()
                .Any(script => script != null && script.GetClass() == type);
            if (!registered)
                throw new InvalidOperationException(
                    "Unity has no MonoScript for " + type.FullName + "; place this MonoBehaviour in " + type.Name + ".cs.");
        }

        internal static bool IsExposedReference(SerializedProperty property)
        {
            return (property.propertyType == SerializedPropertyType.ExposedReference || property.propertyType == SerializedPropertyType.Generic) &&
                property.FindPropertyRelative("exposedName") != null && property.FindPropertyRelative("defaultValue") != null;
        }

        private static IExposedPropertyTable DefaultResolver(SerializedProperty property)
        {
            var component = property.serializedObject.targetObject as Component;
            return component == null ? null : component.GetComponent<UnityEngine.Playables.PlayableDirector>();
        }

        private static void SetExposedReference(SerializedProperty property, string reference, IExposedPropertyTable resolver)
        {
            var defaultValue = property.FindPropertyRelative("defaultValue");
            var target = string.IsNullOrEmpty(reference) || reference == "null"
                ? null
                : ResolveObjectReference("PPtr<$" + ExposedType(property).Name + ">", reference);
            if (resolver == null)
            {
                defaultValue.objectReferenceValue = target;
                return;
            }
            var exposedName = property.FindPropertyRelative("exposedName");
            if (string.IsNullOrEmpty(exposedName.stringValue))
                exposedName.stringValue = Guid.NewGuid().ToString();
            var resolverObject = resolver as UnityEngine.Object;
            if (resolverObject != null && !EditorApplication.isPlayingOrWillChangePlaymode)
                Undo.RecordObject(resolverObject, "Unity Agent Bridge: Exposed Reference");
            if (target == null)
                resolver.ClearReferenceValue(exposedName.stringValue);
            else
                resolver.SetReferenceValue(exposedName.stringValue, target);
            if (resolverObject != null)
                EditorUtility.SetDirty(resolverObject);
        }

        private static Type ExposedType(SerializedProperty property)
        {
            var type = property.serializedObject.targetObject.GetType();
            foreach (var segment in property.propertyPath.Split('.'))
            {
                FieldInfo field = null;
                for (var current = type; current != null && field == null; current = current.BaseType)
                    field = current.GetField(segment, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly);
                if (field == null)
                    return typeof(UnityEngine.Object);
                type = field.FieldType;
            }
            return type.IsGenericType ? type.GetGenericArguments()[0] : typeof(UnityEngine.Object);
        }

        private static Gradient ParseGradient(string rawValue)
        {
            if (rawValue == null || !HasField(rawValue, "colorKeys") || !HasField(rawValue, "alphaKeys"))
                throw new FormatException("Gradient value must be {\"colorKeys\":[{\"time\",\"r\",\"g\",\"b\"}],\"alphaKeys\":[{\"time\",\"alpha\"}],\"mode\"?}.");
            var box = JsonUtility.FromJson<GradientBox>(rawValue);
            var gradient = new Gradient();
            if (!string.IsNullOrEmpty(box.mode))
                gradient.mode = (GradientMode)Enum.Parse(typeof(GradientMode), box.mode, true);
            gradient.SetKeys(
                box.colorKeys.Select(key => new GradientColorKey(new Color(key.r, key.g, key.b), key.time)).ToArray(),
                box.alphaKeys.Select(key => new GradientAlphaKey(key.alpha, key.time)).ToArray());
            return gradient;
        }

        private static string GradientSignature(Gradient gradient)
        {
            return gradient.mode + ":" +
                string.Join(";", gradient.colorKeys.Select(key => Vector(key.time, key.color.r, key.color.g, key.color.b)).ToArray()) + ":" +
                string.Join(";", gradient.alphaKeys.Select(key => Vector(key.time, key.alpha)).ToArray());
        }

        private static ColorBox ParseColor(string rawValue)
        {
            if (!string.IsNullOrWhiteSpace(rawValue) && !rawValue.TrimStart().StartsWith("{", StringComparison.Ordinal))
            {
                var text = ParseJsonString(rawValue);
                var parts = text.Split(',').Select(value => value.Trim()).ToArray();
                float r;
                float g;
                float b;
                float a = 1f;
                if ((parts.Length == 3 || parts.Length == 4)
                    && float.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out r)
                    && float.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out g)
                    && float.TryParse(parts[2], NumberStyles.Float, CultureInfo.InvariantCulture, out b)
                    && (parts.Length == 3 || float.TryParse(parts[3], NumberStyles.Float, CultureInfo.InvariantCulture, out a)))
                    return new ColorBox { r = r, g = g, b = b, a = a };
            }
            if (rawValue == null
                || !Regex.IsMatch(rawValue, "\\\"r\\\"\\s*:")
                || !Regex.IsMatch(rawValue, "\\\"g\\\"\\s*:")
                || !Regex.IsMatch(rawValue, "\\\"b\\\"\\s*:")
                || !Regex.IsMatch(rawValue, "\\\"a\\\"\\s*:"))
                throw new FormatException("Color value must be r,g,b[,a] or a JSON object with r/g/b/a fields.");
            var result = JsonUtility.FromJson<ColorBox>(rawValue);
            if (result == null)
                throw new FormatException("Color value must be r,g,b[,a] or a JSON object with r/g/b/a fields.");
            return result;
        }

        private static bool HasField(string json, string name)
        {
            return Regex.IsMatch(json, "\\\"" + Regex.Escape(name) + "\\\"\\s*:");
        }

        private static bool HasEitherField(string json, string first, string second)
        {
            return HasField(json, first) || HasField(json, second);
        }

        private static string ParseJsonString(string rawValue)
        {
            if (rawValue == null)
                throw new ArgumentNullException("rawValue");
            var box = JsonUtility.FromJson<StringBox>("{\"value\":" + rawValue + "}");
            return box.value;
        }

        internal static Type ResolveObjectReferenceType(string serializedPropertyType)
        {
            if (!serializedPropertyType.StartsWith("PPtr<", StringComparison.Ordinal))
                serializedPropertyType = "PPtr<$" + serializedPropertyType + ">";
            const string prefix = "PPtr<";
            if (!serializedPropertyType.StartsWith(prefix, StringComparison.Ordinal) || !serializedPropertyType.EndsWith(">", StringComparison.Ordinal))
                throw new InvalidOperationException("Unknown Object Reference property type: " + serializedPropertyType);
            var typeName = serializedPropertyType.Substring(prefix.Length, serializedPropertyType.Length - prefix.Length - 1).TrimStart('$');
            var engineType = typeof(UnityEngine.Object).Assembly.GetType("UnityEngine." + typeName, false);
            if (engineType != null && typeof(UnityEngine.Object).IsAssignableFrom(engineType))
                return engineType;
            var matches = TypeCache.GetTypesDerivedFrom<UnityEngine.Object>()
                .Concat(new[] { typeof(GameObject), typeof(Component), typeof(UnityEngine.Object) })
                .Where(type => type.Name == typeName || type.FullName == typeName)
                .Distinct()
                .ToArray();
            if (matches.Length == 1)
                return matches[0];
            throw new InvalidOperationException("Object Reference type is missing or ambiguous: " + typeName);
        }

        internal static UnityEngine.Object ResolveObjectReference(string serializedPropertyType, string reference)
        {
            if (string.IsNullOrEmpty(reference))
                return null;
            var expectedType = ResolveObjectReferenceType(serializedPropertyType);
            // Built-in assets (the picker's Sphere mesh, Default-Material) live in Library/ and Resources/ files.
            if (reference.StartsWith("Assets/", StringComparison.Ordinal) || reference.StartsWith("Packages/", StringComparison.Ordinal) ||
                reference.StartsWith("Library/unity ", StringComparison.Ordinal) || reference.StartsWith("Resources/unity_builtin_extra", StringComparison.Ordinal))
            {
                var assetSelectorSeparator = reference.IndexOf('#');
                var assetPath = Uri.UnescapeDataString(assetSelectorSeparator < 0 ? reference : reference.Substring(0, assetSelectorSeparator));
                if (assetSelectorSeparator >= 0 && assetPath.EndsWith(".prefab", StringComparison.OrdinalIgnoreCase))
                    return ResolvePrefabComponentReference(assetPath, reference.Substring(assetSelectorSeparator + 1), expectedType);
                UnityEngine.Object asset;
                if (assetSelectorSeparator >= 0)
                {
                    var selector = reference.Substring(assetSelectorSeparator + 1);
                    var bracket = selector.LastIndexOf('[');
                    int assetIndex;
                    if (!selector.EndsWith("]", StringComparison.Ordinal))
                    {
                        // path#Name, as the picker lists sub-assets by name.
                        var named = AssetDatabase.LoadAllAssetsAtPath(assetPath)
                            .Where(item => item != null && expectedType.IsAssignableFrom(item.GetType()) && item.name == Uri.UnescapeDataString(selector))
                            .ToArray();
                        if (named.Length > 1)
                            throw new InvalidOperationException("Several " + expectedType.Name + " assets are named " + selector + " in " + assetPath + "; use the reference from object-info.");
                        if (named.Length == 0)
                            throw new InvalidOperationException("Compatible asset reference was not found: " + reference);
                        return named[0];
                    }
                    if (bracket <= 0
                        || !int.TryParse(selector.Substring(bracket + 1, selector.Length - bracket - 2), out assetIndex)
                        || assetIndex < 0)
                        throw new ArgumentException("Asset Object Picker reference is invalid.", "reference");
                    var typeName = Uri.UnescapeDataString(selector.Substring(0, bracket));
                    var matches = AssetDatabase.LoadAllAssetsAtPath(assetPath)
                        .Where(item => item != null && expectedType.IsAssignableFrom(item.GetType())
                            && (item.GetType().FullName == typeName || item.GetType().Name == typeName))
                        .ToArray();
                    asset = assetIndex < matches.Length ? matches[assetIndex] : null;
                }
                else
                {
                    asset = AssetDatabase.LoadAssetAtPath(assetPath, expectedType);
                }
                if (asset == null)
                    throw new InvalidOperationException("Compatible asset reference was not found: " + assetPath);
                return asset;
            }

            var selectorSeparator = reference.LastIndexOf('#');
            var objectPath = selectorSeparator < 0 ? reference : reference.Substring(0, selectorSeparator);
            var gameObject = ScenePath.ResolveObject(objectPath);
            if (selectorSeparator >= 0)
            {
                var selector = reference.Substring(selectorSeparator + 1);
                var bracket = selector.LastIndexOf('[');
                var hasIndex = bracket > 0 && selector.EndsWith("]", StringComparison.Ordinal);
                var componentIndex = -1;
                if (hasIndex && (!int.TryParse(selector.Substring(bracket + 1, selector.Length - bracket - 2), out componentIndex) || componentIndex < 0))
                    throw new ArgumentException("Object Picker component index is invalid.", "reference");
                var componentTypeName = Uri.UnescapeDataString(hasIndex ? selector.Substring(0, bracket) : selector);
                var componentType = ResolveComponentType(componentTypeName);
                if (expectedType != typeof(UnityEngine.Object) && !expectedType.IsAssignableFrom(componentType))
                    throw new InvalidOperationException("Object Picker reference is not compatible with " + expectedType.FullName + ".");
                var exactComponents = gameObject.GetComponents(componentType).Cast<Component>().ToArray();
                if (!hasIndex && exactComponents.Length != 1)
                    throw new InvalidOperationException("Object Picker component reference is ambiguous; use an index.");
                if (!hasIndex)
                    return exactComponents[0];
                if (componentIndex >= exactComponents.Length)
                    throw new InvalidOperationException("Object Picker component index is outside the available range.");
                return exactComponents[componentIndex];
            }

            if (expectedType == typeof(GameObject) || expectedType == typeof(UnityEngine.Object))
                return gameObject;
            if (typeof(Component).IsAssignableFrom(expectedType))
            {
                var components = gameObject.GetComponents(expectedType).Cast<Component>().ToArray();
                if (components.Length == 0)
                    throw new InvalidOperationException("Referenced scene object lacks component " + expectedType.FullName + ".");
                if (components.Length > 1)
                    throw new InvalidOperationException("Referenced scene object has multiple compatible components; use the exact reference returned by Object Picker.");
                return components[0];
            }
            throw new InvalidOperationException("Scene object is not compatible with reference type " + expectedType.FullName + ".");
        }

        private static UnityEngine.Object ResolvePrefabComponentReference(string assetPath, string selector, Type expectedType)
        {
            var root = AssetDatabase.LoadAssetAtPath<GameObject>(assetPath);
            if (root == null)
                throw new InvalidOperationException("Prefab was not found: " + assetPath);
            var parts = selector.Split('#');
            if (parts.Length < 1 || parts.Length > 2)
                throw new ArgumentException("Prefab component reference is invalid.", "selector");
            var target = root.transform;
            if (parts.Length == 2 && parts[0].Length > 0)
            {
                foreach (var segment in parts[0].Split('/'))
                {
                    int childIndex;
                    if (!int.TryParse(segment, NumberStyles.None, CultureInfo.InvariantCulture, out childIndex) || childIndex < 0 || childIndex >= target.childCount)
                        throw new InvalidOperationException("Prefab component child path is invalid: " + parts[0]);
                    target = target.GetChild(childIndex);
                }
            }
            var componentSelector = parts[parts.Length - 1];
            var bracket = componentSelector.LastIndexOf('[');
            var hasIndex = bracket > 0 && componentSelector.EndsWith("]", StringComparison.Ordinal);
            var componentIndex = -1;
            if (hasIndex && (!int.TryParse(componentSelector.Substring(bracket + 1, componentSelector.Length - bracket - 2), out componentIndex) || componentIndex < 0))
                throw new ArgumentException("Prefab component index is invalid.", "selector");
            var componentType = ResolveComponentType(Uri.UnescapeDataString(hasIndex ? componentSelector.Substring(0, bracket) : componentSelector));
            if (!expectedType.IsAssignableFrom(componentType))
                throw new InvalidOperationException("Prefab component is not compatible with " + expectedType.FullName + ".");
            var components = target.GetComponents(componentType).Cast<Component>().ToArray();
            if (!hasIndex && components.Length != 1)
                throw new InvalidOperationException("Prefab component reference is ambiguous; use an index.");
            if (!hasIndex)
                return components[0];
            if (componentIndex >= components.Length)
                throw new InvalidOperationException("Prefab component index is outside the available range.");
            return components[componentIndex];
        }

        private static string PrefabChildIndexPath(Transform root, Transform target)
        {
            if (target == root)
                return string.Empty;
            var indices = new Stack<int>();
            for (var current = target; current != null && current != root; current = current.parent)
                indices.Push(current.GetSiblingIndex());
            return string.Join("/", indices.Select(index => index.ToString(CultureInfo.InvariantCulture)).ToArray());
        }

        private static GameObject Owner(UnityEngine.Object item)
        {
            var gameObject = item as GameObject;
            if (gameObject != null)
                return gameObject;
            var component = item as Component;
            return component == null ? null : component.gameObject;
        }

        private static bool IsListedLoadedScene(UnityEngine.SceneManagement.Scene target)
        {
            if (!target.IsValid() || !target.isLoaded)
                return false;
            return ScenePath.ContextScenes().Any(scene => scene == target);
        }

        private static int HierarchyDepth(Transform transform)
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
