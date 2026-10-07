using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using UnityEditor;
using UnityEngine;

namespace UnityAgentBridge.Editor
{
    // TMP Font Asset Inspector: Generation Settings, Fallback Font Assets and the dynamic atlas buttons.
    // Glyph and character tables are summarized by count; static atlases are rebuilt in Font Asset Creator.
    internal sealed class FontAssetView : AssetView
    {
        private const BindingFlags Any = BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;

        internal override bool Handles(string path, UnityEngine.Object asset)
        {
            return asset != null && asset.GetType().FullName == "TMPro.TMP_FontAsset";
        }

        internal override JsonText Describe(string path, UnityEngine.Object asset, string property)
        {
            var faceInfo = Get(asset, "faceInfo");
            return new JsonText()
                .Add("Source Font File", AssetViews.ObjectLabel(Get(asset, "sourceFontFile") as UnityEngine.Object))
                .Add("Atlas Population Mode", ObjectNames.NicifyVariableName(Get(asset, "atlasPopulationMode").ToString()))
                .Add("Sampling Point Size", faceInfo == null ? null : Get(faceInfo, "pointSize"))
                .Add("Padding", Get(asset, "atlasPadding"))
                .Add("Atlas Resolution", Get(asset, "atlasWidth") + "x" + Get(asset, "atlasHeight"))
                .Add("Multi Atlas Textures", Get(asset, "isMultiAtlasTexturesEnabled"))
                .Add("Characters", ((ICollection)Get(asset, "characterTable")).Count)
                .Add("Glyphs", ((ICollection)Get(asset, "glyphTable")).Count)
                .Add("Fallback Font Assets", ((IEnumerable)Get(asset, "fallbackFontAssetTable") ?? new object[0]).Cast<UnityEngine.Object>().Select(AssetViews.ObjectLabel).ToArray());
        }

        internal override string[] Actions(string path, UnityEngine.Object asset)
        {
            return new[] { "Add Characters", "Clear Dynamic Data" };
        }

        internal override string Execute(string path, UnityEngine.Object asset, string action, PropertyValue[] values)
        {
            action = AssetViews.Match(Actions(path, asset), action);
            if (!AssetDatabase.IsOpenForEdit(asset))
                throw new InvalidOperationException("TMP font asset is not open for editing: " + path);
            string result;
            if (action == "Add Characters")
            {
                // Dynamic atlases grow on demand; a static atlas is regenerated in Window > TextMeshPro > Font Asset Creator.
                if (Get(asset, "atlasPopulationMode").ToString() == "Static")
                    throw new InvalidOperationException("Atlas Population Mode is Static; set it to Dynamic or rebuild the atlas in Font Asset Creator.");
                var characters = AssetViews.Value(values, "characters");
                var add = asset.GetType().GetMethods(Any).First(method => method.Name == "TryAddCharacters" &&
                    method.GetParameters().Length >= 2 && method.GetParameters()[0].ParameterType == typeof(string));
                var arguments = new object[add.GetParameters().Length];
                arguments[0] = characters;
                for (var index = 2; index < arguments.Length; index++)
                    arguments[index] = add.GetParameters()[index].DefaultValue;
                add.Invoke(asset, arguments);
                var missing = arguments[1] as string;
                result = "Characters: " + ((ICollection)Get(asset, "characterTable")).Count + (string.IsNullOrEmpty(missing) ? string.Empty : "; not in the source font: " + missing);
            }
            else
            {
                asset.GetType().GetMethod("ClearFontAssetData", BindingFlags.Instance | BindingFlags.Public, null, new[] { typeof(bool) }, null)
                    .Invoke(asset, new object[] { true });
                result = "Cleared dynamic data: " + path;
            }
            var events = Type.GetType("TMPro.TMPro_EventManager, Unity.TextMeshPro", false);
            var changed = events == null ? null : events.GetMethod("ON_FONT_PROPERTY_CHANGED", BindingFlags.Static | BindingFlags.Public, null,
                new[] { typeof(bool), typeof(UnityEngine.Object) }, null);
            if (changed != null)
                changed.Invoke(null, new object[] { true, asset });
            EditorUtility.SetDirty(asset);
            AssetDatabase.SaveAssets();
            return result;
        }

        internal override PropertyValue[] Modify(string path, UnityEngine.Object asset, PropertyValue[] values, bool confirm, List<string> changes)
        {
            var rest = new List<PropertyValue>();
            foreach (var entry in values)
            {
                if (AssetViews.KeyIs(entry.path, "Atlas Population Mode"))
                {
                    var property = asset.GetType().GetProperty("atlasPopulationMode", Any);
                    var mode = Enum.GetNames(property.PropertyType).FirstOrDefault(name => AssetViews.KeyIs(AssetViews.Text(entry.value), name));
                    if (mode == null)
                        throw new ArgumentException("Atlas Population Mode: " + string.Join(", ", Enum.GetNames(property.PropertyType)));
                    Undo.RecordObject(asset, "Atlas Population Mode");
                    property.SetValue(asset, Enum.Parse(property.PropertyType, mode), null);
                    changes.Add("Atlas Population Mode = " + ObjectNames.NicifyVariableName(mode));
                }
                else if (AssetViews.KeyIs(entry.path, "Fallback Font Assets"))
                {
                    // Comma-separated asset paths replace the list, like dragging fonts into it.
                    var table = (IList)Get(asset, "fallbackFontAssetTable");
                    var fonts = AssetViews.Text(entry.value).Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries).Select(item =>
                    {
                        var font = AssetDatabase.LoadMainAssetAtPath(item.Trim());
                        if (font == null || font.GetType() != asset.GetType())
                            throw new ArgumentException("TMP font asset was not found: " + item.Trim());
                        return font;
                    }).ToList();
                    Undo.RecordObject(asset, "Fallback Font Assets");
                    table.Clear();
                    foreach (var font in fonts)
                        table.Add(font);
                    changes.Add("Fallback Font Assets = [" + string.Join(", ", fonts.Select(AssetViews.ObjectLabel)) + "]");
                }
                else
                    rest.Add(entry);
            }
            EditorUtility.SetDirty(asset);
            AssetDatabase.SaveAssets();
            return rest.ToArray();
        }

        private static object Get(object target, string property)
        {
            var info = target.GetType().GetProperty(property, Any);
            return info == null ? null : info.GetValue(target, null);
        }
    }
}
