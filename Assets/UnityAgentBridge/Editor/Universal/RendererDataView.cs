using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering.Universal;

namespace UnityAgentBridge.Editor
{
    // URP Renderer Inspector: the renderer's fields plus the Renderer Features list, its Add button and context menu.
    // A feature's settings are edited as the sub-asset "Assets/Renderer.asset#<feature name>".
    [InitializeOnLoad]
    internal sealed class RendererDataView : AssetView
    {
        static RendererDataView()
        {
            AssetViews.Register(new RendererDataView());
        }

        internal override bool Handles(string path, UnityEngine.Object asset)
        {
            return asset is ScriptableRendererData;
        }

        internal override bool KeepsProperties { get { return true; } }

        internal override JsonText Describe(string path, UnityEngine.Object asset, string property)
        {
            var data = (ScriptableRendererData)asset;
            return new JsonText().Add("Renderer Features", data.rendererFeatures
                .Select(feature => feature == null ? "Missing" : feature.name + (feature.isActive ? string.Empty : " (off)")).ToArray());
        }

        internal override string[] Actions(string path, UnityEngine.Object asset)
        {
            return new[] { "Add Renderer Feature", "Remove Renderer Feature", "Move Up", "Move Down" };
        }

        internal override string Execute(string path, UnityEngine.Object asset, string action, PropertyValue[] values)
        {
            var data = (ScriptableRendererData)asset;
            action = AssetViews.Match(Actions(path, asset), action);
            var name = AssetViews.Value(values, "name");
            var serialized = new SerializedObject(data);
            var features = serialized.FindProperty("m_RendererFeatures");
            var map = serialized.FindProperty("m_RendererFeatureMap");
            if (action == "Add Renderer Feature")
            {
                var type = FeatureType(data, name);
                var component = ScriptableObject.CreateInstance(type);
                component.name = type.Name;
                Undo.RegisterCreatedObjectUndo(component, "Add Renderer Feature");
                AssetDatabase.AddObjectToAsset(component, data);
                string guid;
                long localId;
                AssetDatabase.TryGetGUIDAndLocalFileIdentifier(component, out guid, out localId);
                features.arraySize++;
                features.GetArrayElementAtIndex(features.arraySize - 1).objectReferenceValue = component;
                map.arraySize++;
                map.GetArrayElementAtIndex(map.arraySize - 1).longValue = localId;
                return Saved(serialized, data, "Renderer feature added: " + component.name);
            }

            var index = FeatureIndex(data, name);
            switch (action)
            {
                case "Remove Renderer Feature":
                    var feature = data.rendererFeatures[index];
                    features.GetArrayElementAtIndex(index).objectReferenceValue = null;
                    features.DeleteArrayElementAtIndex(index);
                    map.DeleteArrayElementAtIndex(index);
                    serialized.ApplyModifiedProperties();
                    if (feature != null)
                    {
                        Undo.DestroyObjectImmediate(feature);
                        feature.Dispose();
                    }
                    return Saved(serialized, data, "Renderer feature removed: " + name);
                default:
                    var target = action == "Move Up" ? index - 1 : index + 1;
                    if (target < 0 || target >= features.arraySize)
                        throw new InvalidOperationException(name + " is already " + (target < 0 ? "first." : "last."));
                    features.MoveArrayElement(index, target);
                    map.MoveArrayElement(index, target);
                    return Saved(serialized, data, "Renderer features: " + string.Join(", ", Enumerable.Range(0, features.arraySize)
                        .Select(item => features.GetArrayElementAtIndex(item).objectReferenceValue)
                        .Select(item => item == null ? "Missing" : item.name)));
            }
        }

        private static string Saved(SerializedObject serialized, ScriptableRendererData data, string message)
        {
            serialized.ApplyModifiedProperties();
            data.SetDirty();
            EditorUtility.SetDirty(data);
            AssetDatabase.SaveAssets();
            return message;
        }

        private static int FeatureIndex(ScriptableRendererData data, string name)
        {
            var index = data.rendererFeatures.FindIndex(feature => feature != null && AssetViews.KeyIs(name, feature.name));
            if (index < 0)
                throw new ArgumentException("Renderer feature was not found: " + name + ". Features: " +
                    string.Join(", ", data.rendererFeatures.Where(feature => feature != null).Select(feature => feature.name)));
            return index;
        }

        // The "Add Renderer Feature" list: supported on this renderer and not already added when single-instance.
        private static Type FeatureType(ScriptableRendererData data, string name)
        {
            var duplicate = typeof(ScriptableRendererData).GetMethod("DuplicateFeatureCheck", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            var available = TypeCache.GetTypesDerivedFrom<ScriptableRendererFeature>()
                .Where(type => !type.IsAbstract && Supported(type, data.GetType()) &&
                    (duplicate == null || !(bool)duplicate.Invoke(data, new object[] { type })))
                .ToList();
            var match = available.Where(type => AssetViews.KeyIs(name, MenuName(type), type.Name)).ToList();
            if (match.Count != 1)
                throw new ArgumentException((match.Count == 0 ? "Renderer feature type was not found: " : "Renderer feature type is ambiguous: ") + name +
                    ". Available: " + string.Join(", ", available.Select(MenuName).OrderBy(item => item)));
            return match[0];
        }

        private static bool Supported(Type feature, Type renderer)
        {
            var attribute = feature.GetCustomAttribute<SupportedOnRendererAttribute>();
            return attribute == null || attribute.rendererTypes.Contains(renderer);
        }

        private static string MenuName(Type type)
        {
            var single = type.GetCustomAttribute<DisallowMultipleRendererFeature>();
            var name = single != null && single.customTitle != null ? single.customTitle : ObjectNames.NicifyVariableName(type.Name);
            return type.Namespace != null && type.Namespace.Contains("Experimental") ? name + " (Experimental)" : name;
        }
    }
}
