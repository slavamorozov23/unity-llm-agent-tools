using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using UnityEditor;

namespace UnityAgentBridge.Editor
{
    // HDRP Frame Settings (Graphics > HDRP defaults, a camera's Custom Frame Settings): the checkboxes of the
    // Frame Settings foldouts by their labels instead of the serialized bit fields.
    internal static class FrameSettingsText
    {
        internal const string CameraKey = "frameSettings";
        private const string CameraFrame = "m_RenderingPathCustomFrameSettings";
        private const string CameraMask = "renderingPathCustomFrameSettingsOverrideMask";

        internal static IEnumerable<FieldInfo> Fields()
        {
            var type = AppDomain.CurrentDomain.GetAssemblies()
                .Select(assembly => assembly.GetType("UnityEngine.Rendering.HighDefinition.FrameSettingsField", false))
                .FirstOrDefault(item => item != null);
            if (type == null)
                return Enumerable.Empty<FieldInfo>();
            return type.GetFields(BindingFlags.Public | BindingFlags.Static).Where(field =>
            {
                var bit = Bit(field);
                var attribute = Attribute(field);
                return bit >= 0 && bit < 128 && field.GetCustomAttributes(typeof(ObsoleteAttribute), false).Length == 0 &&
                    attribute != null && Convert.ToString(attribute.GetType().GetField("type").GetValue(attribute)) == "BoolAsCheckbox";
            });
        }

        private static int Bit(FieldInfo field)
        {
            return Convert.ToInt32(field.GetValue(null), CultureInfo.InvariantCulture);
        }

        private static object Attribute(FieldInfo field)
        {
            return field.GetCustomAttributes(true).FirstOrDefault(item => item.GetType().Name == "FrameSettingsFieldAttribute");
        }

        // Labels repeated in several foldouts ("Transparents" under SSR and refraction) fall back to the field name.
        private static Dictionary<FieldInfo, string> labels;

        internal static string Label(FieldInfo field)
        {
            if (labels == null)
            {
                var fields = Fields().ToList();
                var names = fields.Select(DisplayedName).ToList();
                labels = fields.Select((item, index) => new { item, label = names.Count(name => name == names[index]) > 1 ? ObjectNames.NicifyVariableName(item.Name) : names[index] })
                    .ToDictionary(pair => pair.item, pair => pair.label);
            }
            string label;
            return labels.TryGetValue(field, out label) ? label : DisplayedName(field);
        }

        private static string DisplayedName(FieldInfo field)
        {
            var attribute = Attribute(field);
            var label = attribute.GetType().GetField("displayedName").GetValue(attribute) as string;
            return string.IsNullOrEmpty(label) ? ObjectNames.NicifyVariableName(field.Name) : label;
        }

        // "Screen Space Reflection" or the field name (SSR).
        internal static FieldInfo Find(string name)
        {
            var field = Fields().FirstOrDefault(candidate => AssetViews.KeyIs(name, Label(candidate), candidate.Name));
            if (field == null)
                throw new ArgumentException("Frame setting was not found: " + name + ". Names: " + string.Join(", ", Fields().Select(Label).ToArray()));
            return field;
        }

        // bits is a FrameSettings (bitDatas) or a FrameSettingsOverrideMask (mask).
        internal static bool Get(SerializedProperty bits, FieldInfo field)
        {
            var data = Data(bits, Bit(field));
            return data != null && (data.ulongValue & (1UL << (Bit(field) % 64))) != 0;
        }

        internal static void Set(SerializedProperty bits, FieldInfo field, bool on)
        {
            var data = Data(bits, Bit(field));
            var mask = 1UL << (Bit(field) % 64);
            data.ulongValue = on ? data.ulongValue | mask : data.ulongValue & ~mask;
        }

        private static SerializedProperty Data(SerializedProperty bits, int bit)
        {
            var field = bit < 64 ? "data1" : "data2";
            return bits.FindPropertyRelative("bitDatas." + field) ?? bits.FindPropertyRelative("mask." + field);
        }

        internal static string Off(SerializedProperty frame)
        {
            var off = Fields().Where(field => !Get(frame, field)).Select(Label).ToArray();
            return off.Length == 0 ? "all on" : "off: " + string.Join(", ", off);
        }

        // A camera's Custom Frame Settings: only the overridden checkboxes, the rest follow the HDRP defaults.
        internal static bool IsCamera(SerializedObject serialized)
        {
            return serialized.FindProperty(CameraFrame) != null && serialized.FindProperty(CameraMask) != null;
        }

        internal static string[] CameraFields
        {
            get { return new[] { CameraFrame, CameraMask }; }
        }

        internal static string CameraOverrides(SerializedObject serialized)
        {
            var frame = serialized.FindProperty(CameraFrame);
            var mask = serialized.FindProperty(CameraMask);
            var overrides = Fields().Where(field => Get(mask, field))
                .Select(field => Label(field) + (Get(frame, field) ? " on" : " off")).ToArray();
            return overrides.Length == 0 ? "no overrides" : "overrides: " + string.Join(", ", overrides);
        }

        // frameSettings.<label>=true|false overrides the checkbox, =default returns it to the HDRP defaults.
        internal static bool TrySetCamera(SerializedObject serialized, string path, string rawValue)
        {
            var prefix = CameraKey + ".";
            if (!path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) || !IsCamera(serialized))
                return false;
            var field = Find(path.Substring(prefix.Length));
            var value = AssetViews.Text(rawValue);
            var reset = string.Equals(value, "default", StringComparison.OrdinalIgnoreCase);
            Set(serialized.FindProperty(CameraMask), field, !reset);
            if (!reset)
                Set(serialized.FindProperty(CameraFrame), field, AssetViews.Bool(value));
            return true;
        }
    }
}
