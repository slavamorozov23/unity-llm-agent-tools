using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using UnityEditor;
using UnityEngine;

namespace UnityAgentBridge.Editor
{
    // A view shows an asset the way its Inspector or window does: labels instead of serialized paths, buttons as actions.
    internal abstract class AssetView
    {
        // Called for "Project Settings/..." paths and for loaded assets; return false when the view does not apply.
        internal abstract bool Handles(string path, UnityEngine.Object asset);

        // Keys of the returned object are merged into asset-info.
        internal abstract JsonText Describe(string path, UnityEngine.Object asset, string property);

        // Keeps the generic serialized property list next to the view.
        internal virtual bool KeepsProperties { get { return false; } }

        // The asset opens in its own window (Shader Graph); pinging it in Project would cover that window.
        internal virtual bool OwnsWindow { get { return false; } }

        // Applies the values the view understands and returns the rest for the generic serialized path.
        internal virtual PropertyValue[] Modify(string path, UnityEngine.Object asset, PropertyValue[] values, bool confirm, List<string> changes)
        {
            return values;
        }

        internal virtual string[] Actions(string path, UnityEngine.Object asset)
        {
            return Array.Empty<string>();
        }

        internal virtual string Execute(string path, UnityEngine.Object asset, string action, PropertyValue[] values)
        {
            throw new InvalidOperationException("Unknown asset action: " + action);
        }

        // What the asset's own window creates inside it, for creation-templates --path.
        internal virtual CreationTemplateData[] Templates(string path, UnityEngine.Object asset)
        {
            return null;
        }
    }

    internal static class AssetViews
    {
        private static readonly List<AssetView> Views = new List<AssetView> { new ProjectSettingsView(), new OcclusionCullingView(), new LightingWindowView(), new LightExplorerView(), new AudioMixerView(), new SpriteAtlasView(), new FontAssetView(), new AudioRandomContainerView() };

        // Package-specific views (SRP, Shader Graph, Localization) live in optional assemblies and register on load.
        internal static void Register(AssetView view)
        {
            Views.RemoveAll(item => item.GetType() == view.GetType());
            Views.Add(view);
        }

        internal static AssetView Find(string path, UnityEngine.Object asset)
        {
            return Views.FirstOrDefault(view => view.Handles(path, asset));
        }

        internal static bool IsVirtualPath(string path)
        {
            return ProjectSettingsView.IsProjectSettingsPath(path) || OcclusionCullingView.IsPath(path) || LightingWindowView.IsPath(path) ||
                LightExplorerView.IsPath(path);
        }

        // Action ids are the Inspector button labels; "Add Override", "add-override" and "AddOverride" all match.
        internal static string Match(string[] actions, string action)
        {
            var normalized = Normalize(action);
            var match = actions.FirstOrDefault(item => Normalize(item) == normalized);
            if (match == null)
                throw new InvalidOperationException("Unknown asset action: " + action + ". Available: " + string.Join(", ", actions));
            return match;
        }

        internal static string ActionId(string label)
        {
            return System.Text.RegularExpressions.Regex.Replace(label.ToLowerInvariant(), "[^a-z0-9]+", "-").Trim('-');
        }

        internal static string Normalize(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
                return string.Empty;
            if (value.StartsWith("m_", StringComparison.OrdinalIgnoreCase))
                value = value.Substring(2);
            return new string(value.Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());
        }

        internal static bool KeyIs(string key, params string[] labels)
        {
            var normalized = Normalize(key);
            return labels.Any(label => Normalize(label) == normalized);
        }

        internal static string Value(PropertyValue[] values, string key, bool required = true)
        {
            var entry = (values ?? Array.Empty<PropertyValue>()).FirstOrDefault(item => KeyIs(item.path, key));
            if (entry == null)
            {
                if (required)
                    throw new ArgumentException("Action requires --set " + key + "=<value>.");
                return null;
            }
            return Text(entry.value);
        }

        // --set values arrive as JSON; strings are quoted.
        internal static string Text(string rawValue)
        {
            if (rawValue == null)
                return null;
            var trimmed = rawValue.Trim();
            if (trimmed.Length >= 2 && trimmed[0] == '"' && trimmed[trimmed.Length - 1] == '"')
                return JsonUtility.FromJson<StringBox>("{\"value\":" + trimmed + "}").value;
            return trimmed;
        }

        internal static bool Bool(string rawValue)
        {
            var text = Text(rawValue).ToLowerInvariant();
            if (text == "true" || text == "1" || text == "on" || text == "yes")
                return true;
            if (text == "false" || text == "0" || text == "off" || text == "no")
                return false;
            throw new FormatException("Expected true or false: " + rawValue);
        }

        internal static int Int(string rawValue)
        {
            return int.Parse(Text(rawValue), NumberStyles.Integer, CultureInfo.InvariantCulture);
        }

        // Inspector-style value: enum display names, vectors as x,y,z, references as asset paths.
        internal static object Display(SerializedProperty property)
        {
            switch (property.propertyType)
            {
                case SerializedPropertyType.Boolean: return property.boolValue;
                case SerializedPropertyType.Integer:
                    return (object)IntChoices.Name(property) ?? property.longValue;
                case SerializedPropertyType.LayerMask:
                case SerializedPropertyType.ArraySize:
                    return property.longValue;
                case SerializedPropertyType.Float: return Math.Round(property.doubleValue, 6);
                case SerializedPropertyType.String: return property.stringValue;
                case SerializedPropertyType.Enum:
                    return property.enumValueIndex >= 0 && property.enumValueIndex < property.enumDisplayNames.Length
                        ? property.enumDisplayNames[property.enumValueIndex]
                        : property.intValue.ToString(CultureInfo.InvariantCulture);
                case SerializedPropertyType.ObjectReference:
                    return ObjectLabel(property.objectReferenceValue);
                case SerializedPropertyType.Color:
                    var color = property.colorValue;
                    return Numbers(color.r, color.g, color.b, color.a);
                case SerializedPropertyType.Vector2: return Numbers(property.vector2Value.x, property.vector2Value.y);
                case SerializedPropertyType.Vector3: return Numbers(property.vector3Value.x, property.vector3Value.y, property.vector3Value.z);
                case SerializedPropertyType.Vector4: return Numbers(property.vector4Value.x, property.vector4Value.y, property.vector4Value.z, property.vector4Value.w);
                case SerializedPropertyType.Vector2Int: return property.vector2IntValue.x + "," + property.vector2IntValue.y;
                case SerializedPropertyType.Vector3Int: return property.vector3IntValue.x + "," + property.vector3IntValue.y + "," + property.vector3IntValue.z;
                case SerializedPropertyType.Rect: return Numbers(property.rectValue.x, property.rectValue.y, property.rectValue.width, property.rectValue.height);
                case SerializedPropertyType.ManagedReference:
                    return property.managedReferenceValue == null ? null : ObjectNames.NicifyVariableName(property.managedReferenceValue.GetType().Name);
                default:
                    if (property.isArray && property.propertyType != SerializedPropertyType.String)
                    {
                        if (property.arraySize == 0)
                            return new string[0];
                        var first = property.GetArrayElementAtIndex(0);
                        if (property.arraySize <= 10 && (first.propertyType == SerializedPropertyType.String || first.propertyType == SerializedPropertyType.ObjectReference))
                            return Enumerable.Range(0, property.arraySize).Select(index => Display(property.GetArrayElementAtIndex(index))).ToArray();
                        return "[" + property.arraySize + "]";
                    }
                    if (!property.hasVisibleChildren)
                        return property.type;
                    // A wrapper around one list (custom pass orders and the like) reads as that list.
                    var children = ChildProperties(property).Take(4).ToList();
                    var lists = children.Where(child => child.isArray && child.propertyType != SerializedPropertyType.String).ToList();
                    return children.Count == 1 ? Display(children[0]) : lists.Count == 1 ? Display(lists[0]) : "{…}";
            }
        }

        // One-line form of Display for "changes".
        internal static string Printable(object value)
        {
            if (value == null)
                return "None";
            if (value is string)
                return (string)value;
            if (value is bool)
                return (bool)value ? "true" : "false";
            var list = value as System.Collections.IEnumerable;
            if (list != null)
                return "[" + string.Join(", ", list.Cast<object>().Select(Printable).ToArray()) + "]";
            return Convert.ToString(value, CultureInfo.InvariantCulture);
        }

        internal static string ObjectLabel(UnityEngine.Object value)
        {
            if (value == null)
                return null;
            var path = AssetDatabase.GetAssetPath(value);
            if (string.IsNullOrEmpty(path))
                return value.name;
            return AssetDatabase.IsMainAsset(value) ? path : path + "#" + value.name;
        }

        internal static string Numbers(params float[] values)
        {
            return string.Join(",", values.Select(value => Math.Round(value, 5).ToString("R", CultureInfo.InvariantCulture)).ToArray());
        }

        // Serialized names that the Inspector shows under another label.
        private static readonly Dictionary<string, string> Labels = new Dictionary<string, string>
        {
            { "m_CustomRenderPipeline", "Default Render Pipeline" },
            { "customRenderPipeline", "Render Pipeline Asset" }
        };

        internal static string Label(SerializedProperty property)
        {
            string label;
            return Labels.TryGetValue(property.name, out label) ? label : property.displayName;
        }

        // Top-level visible fields by display name; nested structs are listed by their own children with --property.
        internal static JsonText Fields(SerializedObject serialized, Func<SerializedProperty, bool> include = null)
        {
            var result = new JsonText();
            var iterator = serialized.GetIterator();
            var enter = true;
            while (iterator.NextVisible(enter))
            {
                enter = false;
                if (iterator.propertyPath == "m_Script" || include != null && !include(iterator))
                    continue;
                result.Add(Label(iterator), Display(iterator));
            }
            return result;
        }

        internal static JsonText Children(SerializedProperty property)
        {
            var result = new JsonText();
            foreach (var child in ChildProperties(property))
                result.Add(Label(child), Display(child));
            return result;
        }

        internal static IEnumerable<SerializedProperty> ChildProperties(SerializedProperty property)
        {
            var current = property.Copy();
            var end = property.GetEndProperty();
            var enter = true;
            while (current.NextVisible(enter) && !SerializedProperty.EqualContents(current, end))
            {
                enter = false;
                yield return current.Copy();
            }
        }

        // Finds a field by its Inspector label or serialized name, the same keys asset-info prints.
        internal static SerializedProperty FindByLabel(SerializedObject serialized, string key)
        {
            var iterator = serialized.GetIterator();
            var enter = true;
            while (iterator.NextVisible(enter))
            {
                enter = false;
                if (Normalize(Label(iterator)) == Normalize(key))
                    return iterator.Copy();
            }
            return ComponentService.FindProperty(serialized, key);
        }

        [Serializable]
        private sealed class StringBox { public string value; }
    }
}
