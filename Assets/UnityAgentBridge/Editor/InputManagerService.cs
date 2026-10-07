using System;
using System.Globalization;
using System.IO;
using UnityEditor;
using UnityEditorInternal;
using UnityEngine;

namespace UnityAgentBridge.Editor
{
    internal static class InputManagerService
    {
        private const string AssetPath = "ProjectSettings/InputManager.asset";

        public static InputAxisData[] List()
        {
            UnityEngine.Object[] assets;
            SerializedObject serialized;
            SerializedProperty axes;
            Load(out assets, out serialized, out axes);
            try
            {
                var result = new InputAxisData[axes.arraySize];
                for (var index = 0; index < axes.arraySize; index++)
                    result[index] = Read(axes.GetArrayElementAtIndex(index));
                return result;
            }
            finally
            {
                Destroy(assets);
            }
        }

        public static InputAxisData Create(string name, PropertyValue[] values)
        {
            if (string.IsNullOrWhiteSpace(name))
                throw new ArgumentException("Axis name is required.", "name");

            UnityEngine.Object[] assets;
            SerializedObject serialized;
            SerializedProperty axes;
            Load(out assets, out serialized, out axes);
            try
            {
                axes.InsertArrayElementAtIndex(axes.arraySize);
                var axis = axes.GetArrayElementAtIndex(axes.arraySize - 1);
                Reset(axis, name.Trim());
                foreach (var value in values ?? Array.Empty<PropertyValue>())
                    Set(axis, value);
                Save(assets, serialized);
                return Read(axis);
            }
            finally
            {
                Destroy(assets);
            }
        }

        public static int Delete(string name)
        {
            if (string.IsNullOrWhiteSpace(name))
                throw new ArgumentException("Axis name is required.", "name");

            UnityEngine.Object[] assets;
            SerializedObject serialized;
            SerializedProperty axes;
            Load(out assets, out serialized, out axes);
            try
            {
                var count = 0;
                for (var index = axes.arraySize - 1; index >= 0; index--)
                {
                    var axis = axes.GetArrayElementAtIndex(index);
                    if (!string.Equals(Field(axis, "m_Name").stringValue, name, StringComparison.Ordinal))
                        continue;
                    axes.DeleteArrayElementAtIndex(index);
                    count++;
                }
                if (count == 0)
                    throw new InvalidOperationException("Axis was not found: " + name);
                Save(assets, serialized);
                return count;
            }
            finally
            {
                Destroy(assets);
            }
        }

        private static void Load(out UnityEngine.Object[] assets, out SerializedObject serialized, out SerializedProperty axes)
        {
            var path = Path.GetFullPath(AssetPath);
            assets = InternalEditorUtility.LoadSerializedFileAndForget(path);
            if (assets == null || assets.Length == 0)
                throw new InvalidOperationException("InputManager.asset could not be loaded.");
            serialized = new SerializedObject(assets[0]);
            axes = serialized.FindProperty("m_Axes");
            if (axes == null || !axes.isArray)
            {
                Destroy(assets);
                throw new InvalidOperationException("InputManager Axes were not found.");
            }
        }

        private static void Save(UnityEngine.Object[] assets, SerializedObject serialized)
        {
            serialized.ApplyModifiedPropertiesWithoutUndo();
            InternalEditorUtility.SaveToSerializedFileAndForget(assets, Path.GetFullPath(AssetPath), true);
        }

        private static void Destroy(UnityEngine.Object[] assets)
        {
            if (assets == null)
                return;
            foreach (var asset in assets)
                if (asset != null)
                    UnityEngine.Object.DestroyImmediate(asset);
        }

        private static void Reset(SerializedProperty axis, string name)
        {
            String(axis, "m_Name", name);
            String(axis, "descriptiveName", string.Empty);
            String(axis, "descriptiveNegativeName", string.Empty);
            String(axis, "negativeButton", string.Empty);
            String(axis, "positiveButton", string.Empty);
            String(axis, "altNegativeButton", string.Empty);
            String(axis, "altPositiveButton", string.Empty);
            Float(axis, "gravity", 0f);
            Float(axis, "dead", 0.001f);
            Float(axis, "sensitivity", 1f);
            Bool(axis, "snap", false);
            Bool(axis, "invert", false);
            Int(axis, "type", 0);
            Int(axis, "axis", 0);
            Int(axis, "joyNum", 0);
        }

        private static void Set(SerializedProperty axis, PropertyValue value)
        {
            if (value == null || string.IsNullOrWhiteSpace(value.path))
                throw new ArgumentException("Axis property name is required.", "values");
            switch (value.path)
            {
                case "descriptiveName": case "descriptiveNegativeName": case "negativeButton":
                case "positiveButton": case "altNegativeButton": case "altPositiveButton":
                    String(axis, value.path, JsonString(value.value)); break;
                case "gravity": case "dead": case "sensitivity":
                    Float(axis, value.path, JsonFloat(value.value)); break;
                case "snap": case "invert":
                    Bool(axis, value.path, JsonBool(value.value)); break;
                case "type":
                    Int(axis, value.path, Range(JsonInt(value.value), 0, 2, "type")); break;
                case "axis":
                    Int(axis, value.path, Range(JsonInt(value.value), 0, 27, "axis")); break;
                case "joyNum":
                    Int(axis, value.path, Range(JsonInt(value.value), 0, 11, "joyNum")); break;
                default:
                    throw new ArgumentException("Unsupported axis property: " + value.path, "values");
            }
        }

        private static InputAxisData Read(SerializedProperty axis)
        {
            return new InputAxisData
            {
                name = Field(axis, "m_Name").stringValue,
                descriptiveName = Field(axis, "descriptiveName").stringValue,
                descriptiveNegativeName = Field(axis, "descriptiveNegativeName").stringValue,
                negativeButton = Field(axis, "negativeButton").stringValue,
                positiveButton = Field(axis, "positiveButton").stringValue,
                altNegativeButton = Field(axis, "altNegativeButton").stringValue,
                altPositiveButton = Field(axis, "altPositiveButton").stringValue,
                gravity = Field(axis, "gravity").floatValue,
                dead = Field(axis, "dead").floatValue,
                sensitivity = Field(axis, "sensitivity").floatValue,
                snap = Field(axis, "snap").boolValue,
                invert = Field(axis, "invert").boolValue,
                type = Field(axis, "type").intValue,
                axis = Field(axis, "axis").intValue,
                joyNum = Field(axis, "joyNum").intValue
            };
        }

        private static SerializedProperty Field(SerializedProperty axis, string name)
        {
            var field = axis.FindPropertyRelative(name);
            if (field == null)
                throw new MissingMemberException("InputManager axis", name);
            return field;
        }

        private static void String(SerializedProperty axis, string name, string value) { Field(axis, name).stringValue = value; }
        private static void Float(SerializedProperty axis, string name, float value) { Field(axis, name).floatValue = value; }
        private static void Bool(SerializedProperty axis, string name, bool value) { Field(axis, name).boolValue = value; }
        private static void Int(SerializedProperty axis, string name, int value) { Field(axis, name).intValue = value; }

        [Serializable] private sealed class StringBox { public string value; }
        [Serializable] private sealed class FloatBox { public float value; }
        [Serializable] private sealed class BoolBox { public bool value; }
        [Serializable] private sealed class IntBox { public int value; }

        private static string JsonString(string raw) { return JsonUtility.FromJson<StringBox>("{\"value\":" + raw + "}").value; }
        private static float JsonFloat(string raw)
        {
            var value = JsonUtility.FromJson<FloatBox>("{\"value\":" + raw + "}").value;
            if (float.IsNaN(value) || float.IsInfinity(value)) throw new ArgumentException("Axis number must be finite.");
            return value;
        }
        private static bool JsonBool(string raw) { return JsonUtility.FromJson<BoolBox>("{\"value\":" + raw + "}").value; }
        private static int JsonInt(string raw) { return JsonUtility.FromJson<IntBox>("{\"value\":" + raw + "}").value; }
        private static int Range(int value, int minimum, int maximum, string name)
        {
            if (value < minimum || value > maximum)
                throw new ArgumentOutOfRangeException(name, value, name + " must be between " + minimum.ToString(CultureInfo.InvariantCulture) + " and " + maximum.ToString(CultureInfo.InvariantCulture) + ".");
            return value;
        }
    }
}
