using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;

namespace UnityAgentBridge.Editor
{
    // Volume Profile Inspector: overrides with their checked parameters, Add Override / Remove Override buttons.
    // "Bloom.Intensity=0.3" sets the value and checks its override box, "Bloom.Intensity.Override=false" unchecks it.
    [InitializeOnLoad]
    internal sealed class VolumeProfileView : AssetView
    {
        private const BindingFlags AnyInstance = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

        static VolumeProfileView()
        {
            AssetViews.Register(new VolumeProfileView());
        }

        internal override bool Handles(string path, UnityEngine.Object asset)
        {
            return asset is VolumeProfile;
        }

        internal override JsonText Describe(string path, UnityEngine.Object asset, string property)
        {
            var overrides = new JsonText();
            foreach (var component in Components((VolumeProfile)asset))
            {
                if (!string.IsNullOrEmpty(property) && !AssetViews.KeyIs(property, Label(component), component.GetType().Name))
                    continue;
                overrides.Add(Label(component), Parameters(component, !string.IsNullOrEmpty(property)));
            }
            if (!string.IsNullOrEmpty(property) && overrides.Count == 0)
                throw new ArgumentException("Override was not found: " + property + ". Overrides: " + string.Join(", ", Components((VolumeProfile)asset).Select(Label)));
            return new JsonText().Add("Overrides", overrides);
        }

        internal override string[] Actions(string path, UnityEngine.Object asset)
        {
            return new[] { "Add Override", "Remove Override" };
        }

        internal override string Execute(string path, UnityEngine.Object asset, string action, PropertyValue[] values)
        {
            var profile = (VolumeProfile)asset;
            action = AssetViews.Match(Actions(path, asset), action);
            var name = AssetViews.Value(values, "name");
            if (action == "Remove Override")
                return RemoveOverride(profile, name);

            var type = OverrideType(profile, name);
            if (profile.Has(type))
                throw new InvalidOperationException("The profile already has " + name + ".");
            var component = (VolumeComponent)ScriptableObject.CreateInstance(type);
            component.hideFlags = HideFlags.HideInInspector | HideFlags.HideInHierarchy;
            component.name = type.Name;
            Undo.RegisterCreatedObjectUndo(component, "Add Volume Override");
            AssetDatabase.AddObjectToAsset(component, profile);
            var serialized = new SerializedObject(profile);
            var list = serialized.FindProperty("components");
            list.arraySize++;
            list.GetArrayElementAtIndex(list.arraySize - 1).objectReferenceValue = component;
            serialized.ApplyModifiedProperties();
            EditorUtility.SetDirty(profile);

            // The rest of --set fills the new override: --set name=Bloom Intensity=0.3
            var changes = new List<string>();
            var rest = values.Where(entry => !AssetViews.KeyIs(entry.path, "name"))
                .Select(entry => new PropertyValue { path = Label(component) + "." + entry.path, value = entry.value }).ToArray();
            if (rest.Length > 0)
                Modify(path, asset, rest, false, changes);
            AssetDatabase.SaveAssets();
            return "Override added: " + Label(component) + (changes.Count == 0 ? string.Empty : "; " + string.Join(", ", changes));
        }

        internal override PropertyValue[] Modify(string path, UnityEngine.Object asset, PropertyValue[] values, bool confirm, List<string> changes)
        {
            var profile = (VolumeProfile)asset;
            var rest = new List<PropertyValue>();
            foreach (var entry in values)
            {
                var parts = entry.path.Split('.');
                var component = Components(profile).FirstOrDefault(item => AssetViews.KeyIs(parts[0], Label(item), item.GetType().Name));
                if (parts.Length < 2)
                {
                    rest.Add(entry);
                    continue;
                }
                if (component == null)
                    throw new ArgumentException("Override was not found: " + parts[0] + ". Overrides: " + string.Join(", ", Components(profile).Select(Label)));
                var serialized = new SerializedObject(component);
                if (parts.Length == 2 && AssetViews.KeyIs(parts[1], "Active"))
                {
                    serialized.FindProperty("active").boolValue = AssetViews.Bool(entry.value);
                    serialized.ApplyModifiedProperties();
                    changes.Add(Label(component) + ".Active = " + AssetViews.Printable(component.active));
                    continue;
                }
                var parameter = ParameterProperties(serialized).FirstOrDefault(item => AssetViews.KeyIs(parts[1], AssetViews.Label(item), item.name));
                if (parameter == null)
                    throw new ArgumentException(Label(component) + " has no parameter " + parts[1] + ". Parameters: " +
                        string.Join(", ", ParameterProperties(serialized).Select(AssetViews.Label)));
                var state = parameter.FindPropertyRelative("m_OverrideState");
                var value = parameter.FindPropertyRelative("m_Value");
                if (parts.Length == 3 && AssetViews.KeyIs(parts[2], "Override"))
                    state.boolValue = AssetViews.Bool(entry.value);
                else if (parts.Length == 2)
                {
                    ComponentService.SetProperty(value, entry.value);
                    state.boolValue = true;
                }
                else
                    throw new ArgumentException("Use <Override>.<Parameter>=<value> or <Override>.<Parameter>.Override=true|false: " + entry.path);
                serialized.ApplyModifiedProperties();
                Clamp(component, parameter.name);
                serialized.Update();
                changes.Add(Label(component) + "." + AssetViews.Label(parameter) + " = " +
                    (state.boolValue ? AssetViews.Printable(AssetViews.Display(value)) : "not overridden"));
                EditorUtility.SetDirty(component);
            }
            AssetDatabase.SaveAssets();
            return rest.ToArray();
        }

        private static IEnumerable<VolumeComponent> Components(VolumeProfile profile)
        {
            return profile.components.Where(component => component != null);
        }

        private static string Label(VolumeComponent component)
        {
            return string.IsNullOrEmpty(component.displayName) ? ObjectNames.NicifyVariableName(component.GetType().Name) : component.displayName;
        }

        // Checked parameters only, like the override boxes in the Inspector; with --property the unchecked ones too.
        private static JsonText Parameters(VolumeComponent component, bool all)
        {
            var result = new JsonText();
            if (all || !component.active)
                result.Add("Active", component.active);
            var off = new JsonText();
            foreach (var parameter in ParameterProperties(new SerializedObject(component)))
            {
                var value = AssetViews.Display(parameter.FindPropertyRelative("m_Value"));
                if (parameter.FindPropertyRelative("m_OverrideState").boolValue)
                    result.Add(AssetViews.Label(parameter), value);
                else if (all)
                    off.Add(AssetViews.Label(parameter), value);
            }
            if (off.Count > 0)
                result.Add("Not Overridden", off);
            return result;
        }

        private static IEnumerable<SerializedProperty> ParameterProperties(SerializedObject serialized)
        {
            var iterator = serialized.GetIterator();
            var enter = true;
            while (iterator.NextVisible(enter))
            {
                enter = false;
                if (iterator.FindPropertyRelative("m_OverrideState") != null && iterator.FindPropertyRelative("m_Value") != null)
                    yield return iterator.Copy();
            }
        }

        // The parameter's own setter applies its min/max, as the Inspector slider does.
        private static void Clamp(VolumeComponent component, string fieldName)
        {
            FieldInfo field = null;
            for (var type = component.GetType(); field == null && type != null; type = type.BaseType)
                field = type.GetField(fieldName, AnyInstance);
            var parameter = field == null ? null : field.GetValue(component) as VolumeParameter;
            var value = parameter == null ? null : parameter.GetType().GetProperty("value", BindingFlags.Instance | BindingFlags.Public);
            if (value != null && value.CanWrite)
                value.SetValue(parameter, value.GetValue(parameter));
        }

        // The Add Override list of the profile's pipeline (by its current overrides), otherwise of the active pipeline.
        private static Type OverrideType(VolumeProfile profile, string name)
        {
            var available = AvailableOverrides(profile);
            var match = available.Where(item => AssetViews.KeyIs(name, item.Key, item.Key.Substring(item.Key.LastIndexOf('/') + 1), item.Value.Name)).ToList();
            if (match.Count != 1)
                throw new ArgumentException((match.Count == 0 ? "Override was not found: " : "Override name is ambiguous, use its menu path: ") + name +
                    ". Available: " + string.Join(", ", (match.Count == 0 ? available : match).Select(item => item.Key)));
            return match[0].Value;
        }

        // Same filter as VolumeManager's display list, which needs a rendered frame before it is initialized.
        private static List<KeyValuePair<string, Type>> AvailableOverrides(VolumeProfile profile)
        {
            var pipeline = ProfilePipeline(profile);
            return TypeCache.GetTypesDerivedFrom<VolumeComponent>()
                .Where(type => !type.IsAbstract && SupportedOnRenderPipelineAttribute.IsTypeSupportedOnRenderPipeline(type, pipeline) &&
                    !type.IsDefined(typeof(ObsoleteAttribute), false) && !type.IsDefined(typeof(HideInInspector), false))
                .Select(type =>
                {
                    var menu = type.GetCustomAttribute<VolumeComponentMenu>(false);
                    return new KeyValuePair<string, Type>(menu != null && !string.IsNullOrEmpty(menu.menu) ? menu.menu : ObjectNames.NicifyVariableName(type.Name), type);
                })
                .OrderBy(item => item.Key)
                .ToList();
        }

        private static Type ProfilePipeline(VolumeProfile profile)
        {
            var space = Components(profile).Select(component => component.GetType().Namespace ?? string.Empty)
                .FirstOrDefault(name => name.EndsWith(".Universal", StringComparison.Ordinal) || name.EndsWith(".HighDefinition", StringComparison.Ordinal));
            var assetType = space == null ? null
                : space.EndsWith(".Universal", StringComparison.Ordinal) ? "UnityEngine.Rendering.Universal.UniversalRenderPipelineAsset"
                : "UnityEngine.Rendering.HighDefinition.HDRenderPipelineAsset";
            var type = assetType == null ? null : AppDomain.CurrentDomain.GetAssemblies().Select(assembly => assembly.GetType(assetType, false)).FirstOrDefault(item => item != null);
            return type ?? GraphicsSettings.currentRenderPipelineAssetType;
        }

        private static string RemoveOverride(VolumeProfile profile, string name)
        {
            var serialized = new SerializedObject(profile);
            var list = serialized.FindProperty("components");
            for (var index = 0; index < list.arraySize; index++)
            {
                var component = list.GetArrayElementAtIndex(index).objectReferenceValue as VolumeComponent;
                if (component == null || !AssetViews.KeyIs(name, Label(component), component.GetType().Name))
                    continue;
                var label = Label(component);
                list.GetArrayElementAtIndex(index).objectReferenceValue = null;
                list.DeleteArrayElementAtIndex(index);
                serialized.ApplyModifiedProperties();
                Undo.DestroyObjectImmediate(component);
                EditorUtility.SetDirty(profile);
                AssetDatabase.SaveAssets();
                return "Override removed: " + label;
            }
            throw new ArgumentException("Override was not found: " + name + ". Overrides: " + string.Join(", ", Components(profile).Select(Label)));
        }
    }
}
