using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Text;
using UnityEditor;
using UnityEngine;

namespace UnityAgentBridge.Editor
{
    internal static class RuntimeValueService
    {
        private const BindingFlags Members = BindingFlags.Instance | BindingFlags.Public | BindingFlags.IgnoreCase;
        private const BindingFlags NamedMembers = BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.IgnoreCase;
        private static readonly HashSet<string> SideEffectMembers = new HashSet<string>(StringComparer.Ordinal)
        {
            "material", "materials", "mesh"
        };

        public static string Describe(Component component, string propertyPaths)
        {
            var requested = (propertyPaths ?? string.Empty).Split(',')
                .Select(item => item.Trim())
                .Where(item => item.Length > 0)
                .ToArray();
            var builder = new StringBuilder("{");
            if (requested.Length == 0)
            {
                var first = true;
                foreach (var member in ListedMembers(component.GetType()))
                {
                    object value;
                    if (!TryRead(component, member, out value) || !IsSimple(value))
                        continue;
                    if (!first)
                        builder.Append(',');
                    first = false;
                    AppendString(builder, member.Name);
                    builder.Append(':');
                    AppendValue(builder, value, 1);
                }
            }
            else
            {
                for (var index = 0; index < requested.Length; index++)
                {
                    if (index > 0)
                        builder.Append(',');
                    AppendString(builder, requested[index]);
                    builder.Append(':');
                    AppendValue(builder, Read(component, requested[index]), 0);
                }
            }
            return builder.Append('}').ToString();
        }

        private static object Read(object target, string path)
        {
            object current = target;
            foreach (var segment in path.Split('.'))
            {
                if (current == null)
                    throw new InvalidOperationException("Runtime property is null before " + segment + ": " + path);
                var member = FindMember(current.GetType(), segment);
                if (member == null)
                    throw new InvalidOperationException("Runtime property was not found on " + current.GetType().Name + ": " + segment +
                        ". Available: " + string.Join(", ", ListedMembers(current.GetType()).Select(item => item.Name).Take(40).ToArray()));
                var property = member as PropertyInfo;
                var isStatic = property != null ? property.GetGetMethod(true).IsStatic : ((FieldInfo)member).IsStatic;
                var owner = isStatic ? null : current;
                current = property != null ? property.GetValue(owner, null) : ((FieldInfo)member).GetValue(owner);
            }
            return current;
        }

        // A member named in --property may also be private or static, as a debugger watch reads it; the listing stays public.
        private static MemberInfo FindMember(Type type, string name)
        {
            foreach (var flags in new[] { Members, NamedMembers })
                for (var current = type; current != null; current = current.BaseType)
                {
                    var property = current.GetProperty(name, flags | BindingFlags.DeclaredOnly);
                    if (property != null && property.CanRead && property.GetIndexParameters().Length == 0)
                        return property;
                    var field = current.GetField(name, flags | BindingFlags.DeclaredOnly);
                    if (field != null)
                        return field;
                }
            return null;
        }

        private static IEnumerable<MemberInfo> ListedMembers(Type type)
        {
            var stop = typeof(Component).IsAssignableFrom(type) ? typeof(Component) : typeof(object);
            var members = new List<MemberInfo>();
            for (var current = type; current != null && current != stop && current != typeof(UnityEngine.Object); current = current.BaseType)
            {
                members.AddRange(current.GetProperties(BindingFlags.Instance | BindingFlags.Public | BindingFlags.DeclaredOnly)
                    .Where(item => item.CanRead && item.GetIndexParameters().Length == 0));
                members.AddRange(current.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.DeclaredOnly));
            }
            return members
                .Where(item => !item.IsDefined(typeof(ObsoleteAttribute), true) && !SideEffectMembers.Contains(item.Name))
                .GroupBy(item => item.Name)
                .Select(group => group.First())
                .OrderBy(item => item.Name, StringComparer.Ordinal);
        }

        private static bool TryRead(object target, MemberInfo member, out object value)
        {
            try
            {
                value = member is PropertyInfo ? ((PropertyInfo)member).GetValue(target, null) : ((FieldInfo)member).GetValue(target);
                return true;
            }
            catch
            {
                value = null;
                return false;
            }
        }

        private static bool IsSimple(object value)
        {
            if (value == null)
                return false;
            var type = value.GetType();
            return type.IsPrimitive || type.IsEnum || value is string || value is decimal ||
                value is Vector2 || value is Vector3 || value is Vector4 || value is Quaternion || value is Color ||
                value is Vector2Int || value is Vector3Int || value is Rect || value is Bounds ||
                value is UnityEngine.Object;
        }

        private static void AppendValue(StringBuilder builder, object value, int depth)
        {
            if (value == null || (value is UnityEngine.Object && (UnityEngine.Object)value == null))
            {
                builder.Append("null");
                return;
            }
            if (value is bool)
            {
                builder.Append((bool)value ? "true" : "false");
                return;
            }
            if (value is string || value is char || value.GetType().IsEnum)
            {
                AppendString(builder, value.ToString());
                return;
            }
            if (value is float || value is double)
            {
                var number = Convert.ToDouble(value, CultureInfo.InvariantCulture);
                if (double.IsNaN(number) || double.IsInfinity(number))
                    AppendString(builder, number.ToString(CultureInfo.InvariantCulture));
                else
                    builder.Append(Math.Round(number, 4).ToString("R", CultureInfo.InvariantCulture));
                return;
            }
            if (value.GetType().IsPrimitive || value is decimal)
            {
                builder.Append(Convert.ToString(value, CultureInfo.InvariantCulture));
                return;
            }
            var unityObject = value as UnityEngine.Object;
            if (unityObject != null)
            {
                AppendString(builder, ObjectLabel(unityObject));
                return;
            }
            // JsonUtility writes Bounds as {} (its fields are not serialized).
            if (value is Bounds)
            {
                var bounds = (Bounds)value;
                builder.Append("{\"center\":").Append(JsonUtility.ToJson(bounds.center))
                    .Append(",\"size\":").Append(JsonUtility.ToJson(bounds.size))
                    .Append(",\"min\":").Append(JsonUtility.ToJson(bounds.min))
                    .Append(",\"max\":").Append(JsonUtility.ToJson(bounds.max)).Append('}');
                return;
            }
            if (value is Rect)
            {
                var rect = (Rect)value;
                builder.Append("{\"x\":").Append(rect.x.ToString("R", CultureInfo.InvariantCulture))
                    .Append(",\"y\":").Append(rect.y.ToString("R", CultureInfo.InvariantCulture))
                    .Append(",\"width\":").Append(rect.width.ToString("R", CultureInfo.InvariantCulture))
                    .Append(",\"height\":").Append(rect.height.ToString("R", CultureInfo.InvariantCulture)).Append('}');
                return;
            }
            if (value is Vector2 || value is Vector3 || value is Vector4 || value is Quaternion || value is Color ||
                value is Vector2Int || value is Vector3Int)
            {
                builder.Append(JsonUtility.ToJson(value));
                return;
            }
            var list = value as IEnumerable;
            if (list != null)
            {
                var items = list.Cast<object>().Take(21).ToArray();
                builder.Append('[');
                for (var index = 0; index < Math.Min(items.Length, 20); index++)
                {
                    if (index > 0)
                        builder.Append(',');
                    if (depth < 2)
                        AppendValue(builder, items[index], depth + 1);
                    else
                        AppendString(builder, items[index] == null ? "null" : items[index].ToString());
                }
                if (items.Length > 20)
                    builder.Append(",\"…\"");
                builder.Append(']');
                return;
            }
            if (depth > 0)
            {
                AppendString(builder, value.ToString());
                return;
            }
            builder.Append('{');
            var first = true;
            foreach (var member in ListedMembers(value.GetType()))
            {
                object memberValue;
                if (!TryRead(value, member, out memberValue) || !IsSimple(memberValue))
                    continue;
                if (!first)
                    builder.Append(',');
                first = false;
                AppendString(builder, member.Name);
                builder.Append(':');
                AppendValue(builder, memberValue, depth + 1);
            }
            builder.Append('}');
        }

        private static string ObjectLabel(UnityEngine.Object value)
        {
            var assetPath = AssetDatabase.GetAssetPath(value);
            if (!string.IsNullOrEmpty(assetPath))
                return AssetDatabase.IsMainAsset(value) ? assetPath : assetPath + "#" + value.name;
            var gameObject = value as GameObject ?? (value is Component ? ((Component)value).gameObject : null);
            if (gameObject != null && gameObject.scene.IsValid())
                return ScenePath.For(gameObject) + (value is Component ? "#" + value.GetType().Name : string.Empty);
            return value.name;
        }

        private static void AppendString(StringBuilder builder, string value)
        {
            builder.Append('"');
            foreach (var character in value)
            {
                switch (character)
                {
                    case '"': builder.Append("\\\""); break;
                    case '\\': builder.Append("\\\\"); break;
                    case '\n': builder.Append("\\n"); break;
                    case '\r': builder.Append("\\r"); break;
                    case '\t': builder.Append("\\t"); break;
                    default:
                        if (character < ' ')
                            builder.Append("\\u").Append(((int)character).ToString("x4"));
                        else
                            builder.Append(character);
                        break;
                }
            }
            builder.Append('"');
        }
    }
}
