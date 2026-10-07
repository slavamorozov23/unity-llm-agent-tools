using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;

namespace UnityAgentBridge.Editor
{
    // Shader Graph keeps its editor model internal; members are reached by name and fail with the member that moved.
    internal static class Sg
    {
        private const BindingFlags All = BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly;
        private static Assembly editorAssembly;

        internal static Assembly EditorAssembly
        {
            get
            {
                if (editorAssembly == null)
                    editorAssembly = AppDomain.CurrentDomain.GetAssemblies().First(assembly => assembly.GetName().Name == "Unity.ShaderGraph.Editor");
                return editorAssembly;
            }
        }

        // Members the view relies on; checked once so an updated package fails with one clear message instead of midway.
        private static readonly string[] Required =
        {
            "UnityEditor.ShaderGraph.Drawing.MaterialGraphEditWindow:graphObject,graphEditorView,selectedGuid,SaveAsset,Update,GraphHasChangedSinceLastSerialization",
            "UnityEditor.ShaderGraph.GraphData:AddNode,Connect,RemoveEdge,RemoveElements,ValidateGraph,CreateGroup,SetGroup,AddBlock,AddStickyNote,messageManager,activeTargets,blockFieldDescriptors",
            "UnityEditor.ShaderGraph.Drawing.GraphEditorView:graphView,previewManager,blackboardController,m_SearchWindowProvider",
            "UnityEditor.ShaderGraph.Drawing.PreviewManager:RenderPreviews,GetPreviewRenderData,masterRenderData,m_PreviewsCompiling,m_PreviewsNeedsRecompile",
            "UnityEditor.ShaderGraph.Drawing.SearchWindowProvider:GenerateNodeEntries,currentNodeEntries",
            "UnityEditor.ShaderGraph.Drawing.SearcherProvider:CopyNodeForGraph",
            "UnityEditor.ShaderGraph.AbstractMaterialNode:drawState,objectId,GetSlotReference,Dirty,previewExpanded",
            "UnityEditor.ShaderGraph.TargetPropertyGUIContext:AddProperty",
            "UnityEditor.ShaderGraph.NewGraphFromTemplateAction:CreateAndRenameGraphFromTemplate",
        };

        private static string unsupported;

        // Null when this Shader Graph version has everything the view uses.
        internal static string Unsupported
        {
            get
            {
                if (unsupported != null)
                    return unsupported.Length == 0 ? null : unsupported;
                var missing = new List<string>();
                foreach (var entry in Required)
                {
                    var parts = entry.Split(':');
                    var type = EditorAssembly.GetType(parts[0], false);
                    if (type == null)
                    {
                        missing.Add(parts[0]);
                        continue;
                    }
                    foreach (var member in parts[1].Split(','))
                        if (!HasMember(type, member))
                            missing.Add(type.Name + "." + member);
                }
                var package = UnityEditor.PackageManager.PackageInfo.FindForAssembly(EditorAssembly);
                unsupported = missing.Count == 0 ? string.Empty
                    : "Shader Graph " + (package == null ? "?" : package.version) + " is not supported by Unity Agent Bridge (checked with 17.4); missing: " + string.Join(", ", missing);
                return unsupported.Length == 0 ? null : unsupported;
            }
        }

        private static bool HasMember(Type start, string name)
        {
            for (var type = start; type != null; type = type.BaseType)
                if (type.GetMember(name, All).Length > 0)
                    return true;
            return false;
        }

        internal static Type Type(string fullName)
        {
            var type = EditorAssembly.GetType(fullName, false);
            if (type == null)
                throw new MissingMemberException("Shader Graph type is missing: " + fullName);
            return type;
        }

        internal static object Get(object target, string name)
        {
            for (var type = target.GetType(); type != null; type = type.BaseType)
            {
                var property = type.GetProperty(name, All);
                if (property != null && property.GetIndexParameters().Length == 0 && property.GetGetMethod(true) != null)
                    return property.GetValue(target, null);
                var field = type.GetField(name, All);
                if (field != null)
                    return field.GetValue(target);
            }
            throw new MissingMemberException(target.GetType().Name, name);
        }

        internal static bool Has(object target, string name)
        {
            for (var type = target.GetType(); type != null; type = type.BaseType)
                if (type.GetProperty(name, All) != null || type.GetField(name, All) != null)
                    return true;
            return false;
        }

        internal static void Set(object target, string name, object value)
        {
            for (var type = target.GetType(); type != null; type = type.BaseType)
            {
                var property = type.GetProperty(name, All);
                if (property != null && property.GetSetMethod(true) != null)
                {
                    property.SetValue(target, value, null);
                    return;
                }
                var field = type.GetField(name, All);
                if (field != null)
                {
                    field.SetValue(target, value);
                    return;
                }
            }
            throw new MissingMemberException(target.GetType().Name, name);
        }

        internal static object Call(object target, string name, params object[] arguments)
        {
            return Invoke(target.GetType(), target, name, null, arguments);
        }

        internal static object CallGeneric(object target, string name, Type typeArgument, params object[] arguments)
        {
            return Invoke(target.GetType(), target, name, typeArgument, arguments);
        }

        internal static object CallStatic(Type type, string name, params object[] arguments)
        {
            return Invoke(type, null, name, null, arguments);
        }

        private static object Invoke(Type start, object target, string name, Type typeArgument, object[] arguments)
        {
            for (var type = start; type != null; type = type.BaseType)
            {
                foreach (var method in type.GetMethods(All).Where(item => item.Name == name && item.IsGenericMethodDefinition == (typeArgument != null)))
                {
                    var parameters = method.GetParameters();
                    if (parameters.Length != arguments.Length)
                        continue;
                    var candidate = typeArgument == null ? method : method.MakeGenericMethod(typeArgument);
                    parameters = candidate.GetParameters();
                    var fits = true;
                    for (var index = 0; index < parameters.Length && fits; index++)
                        fits = arguments[index] == null ? !parameters[index].ParameterType.IsValueType || Nullable.GetUnderlyingType(parameters[index].ParameterType) != null
                            : parameters[index].ParameterType.IsInstanceOfType(arguments[index]);
                    if (fits)
                        return candidate.Invoke(target, arguments);
                }
            }
            throw new MissingMethodException(start.Name, name);
        }

        internal static IEnumerable<object> Items(object enumerable)
        {
            return enumerable == null ? Enumerable.Empty<object>() : ((IEnumerable)enumerable).Cast<object>();
        }

        // Typed array for internal signatures such as RemoveElements(AbstractMaterialNode[], ...).
        internal static Array Array(string elementType, IEnumerable<object> items)
        {
            var list = items.ToList();
            var array = System.Array.CreateInstance(Type(elementType), list.Count);
            for (var index = 0; index < list.Count; index++)
                array.SetValue(list[index], index);
            return array;
        }

        internal static object Enum(string enumType, string value)
        {
            return System.Enum.Parse(Type(enumType), value);
        }
    }
}
