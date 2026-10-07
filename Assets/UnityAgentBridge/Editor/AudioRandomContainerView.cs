using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;

namespace UnityAgentBridge.Editor
{
    // Audio Random Container window: its Audio Clips list, where each clip is an AudioContainerElement sub-asset.
    internal sealed class AudioRandomContainerView : AssetView
    {
        private const string ClipsLabel = "Audio Clips";

        internal override bool Handles(string path, UnityEngine.Object asset)
        {
            return asset != null && asset.GetType().Name == "AudioRandomContainer";
        }

        internal override bool KeepsProperties { get { return true; } }

        // Clip paths as the list shows them; a clip with its own volume or unchecked is marked.
        internal override JsonText Describe(string path, UnityEngine.Object asset, string property)
        {
            var clips = Elements(asset).Select(element =>
            {
                var serialized = new SerializedObject(element);
                var label = AssetViews.ObjectLabel(serialized.FindProperty("m_AudioClip").objectReferenceValue) ?? "None";
                var volume = serialized.FindProperty("m_Volume").floatValue;
                if (Math.Abs(volume) > 0.001f)
                    label += " (" + AssetViews.Numbers(volume) + " dB)";
                if (!serialized.FindProperty("m_Enabled").boolValue)
                    label += " (off)";
                return label;
            }).ToArray();
            return new JsonText().Add(ClipsLabel, clips);
        }

        // "Audio Clips=[a.wav, b.wav]" replaces the list as dropping clips on it does; kept clips keep their volume.
        internal override PropertyValue[] Modify(string path, UnityEngine.Object asset, PropertyValue[] values, bool confirm, List<string> changes)
        {
            var rest = new List<PropertyValue>();
            foreach (var entry in values)
            {
                if (!AssetViews.KeyIs(entry.path, ClipsLabel, "Clips", "Elements"))
                {
                    rest.Add(entry);
                    continue;
                }
                var raw = entry.value == null ? "[]" : entry.value.Trim();
                var paths = (raw.StartsWith("[", StringComparison.Ordinal) ? ComponentService.JsonArrayElements(raw) : new List<string> { raw })
                    .Select(AssetViews.Text).ToList();
                var clips = paths.Select(clipPath =>
                {
                    var clip = AssetDatabase.LoadAssetAtPath<AudioClip>(clipPath);
                    if (clip == null)
                        throw new ArgumentException("AudioClip was not found: " + clipPath);
                    return clip;
                }).ToList();
                SetClips(asset, clips);
                changes.Add(ClipsLabel + " = " + AssetViews.Printable(paths));
            }
            return rest.ToArray();
        }

        private static List<UnityEngine.Object> Elements(UnityEngine.Object asset)
        {
            var list = new SerializedObject(asset).FindProperty("m_Elements");
            return Enumerable.Range(0, list.arraySize)
                .Select(index => list.GetArrayElementAtIndex(index).objectReferenceValue)
                .Where(element => element != null)
                .ToList();
        }

        private static void SetClips(UnityEngine.Object asset, List<AudioClip> clips)
        {
            var unused = Elements(asset);
            var elements = new List<UnityEngine.Object>();
            foreach (var clip in clips)
            {
                var element = unused.FirstOrDefault(item => new SerializedObject(item).FindProperty("m_AudioClip").objectReferenceValue == clip);
                if (element != null)
                    unused.Remove(element);
                else
                    element = CreateElement(asset, clip);
                elements.Add(element);
            }
            foreach (var element in unused)
                UnityEngine.Object.DestroyImmediate(element, true);
            var serialized = new SerializedObject(asset);
            var list = serialized.FindProperty("m_Elements");
            list.arraySize = elements.Count;
            for (var index = 0; index < elements.Count; index++)
                list.GetArrayElementAtIndex(index).objectReferenceValue = elements[index];
            serialized.ApplyModifiedPropertiesWithoutUndo();
            EditorUtility.SetDirty(asset);
            AssetDatabase.SaveAssets();
        }

        private static UnityEngine.Object CreateElement(UnityEngine.Object asset, AudioClip clip)
        {
            var type = asset.GetType().Assembly.GetType("UnityEngine.Audio.AudioContainerElement", true);
            var element = (UnityEngine.Object)Activator.CreateInstance(type, true);
            element.hideFlags = HideFlags.HideInHierarchy;
            var serialized = new SerializedObject(element);
            serialized.FindProperty("m_AudioClip").objectReferenceValue = clip;
            serialized.FindProperty("m_Enabled").boolValue = true;
            serialized.ApplyModifiedPropertiesWithoutUndo();
            AssetDatabase.AddObjectToAsset(element, asset);
            return element;
        }
    }
}
