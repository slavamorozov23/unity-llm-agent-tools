using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEditor.U2D;
using UnityEngine;
using UnityEngine.U2D;

namespace UnityAgentBridge.Editor
{
    // Sprite Atlas Inspector: type, packing and texture settings, Objects for Packing and the Pack Preview button.
    // V1 atlases (.spriteatlas) store settings in the asset, V2 (.spriteatlasv2) in the importer and SpriteAtlasAsset.
    internal sealed class SpriteAtlasView : AssetView
    {
        private sealed class Field
        {
            internal string label;
            internal Func<Settings, object> get;
            internal Action<Settings, string> set;
        }

        private sealed class Settings
        {
            internal SpriteAtlasPackingSettings packing;
            internal SpriteAtlasTextureSettings texture;
            internal bool includeInBuild;
        }

        private static readonly Field[] Fields =
        {
            New("Include In Build", item => item.includeInBuild, (item, value) => item.includeInBuild = AssetViews.Bool(value)),
            New("Allow Rotation", item => item.packing.enableRotation, (item, value) => item.packing.enableRotation = AssetViews.Bool(value)),
            New("Tight Packing", item => item.packing.enableTightPacking, (item, value) => item.packing.enableTightPacking = AssetViews.Bool(value)),
            New("Alpha Dilation", item => item.packing.enableAlphaDilation, (item, value) => item.packing.enableAlphaDilation = AssetViews.Bool(value)),
            New("Padding", item => item.packing.padding, (item, value) => item.packing.padding = AssetViews.Int(value)),
            New("Read/Write", item => item.texture.readable, (item, value) => item.texture.readable = AssetViews.Bool(value)),
            New("Generate Mip Maps", item => item.texture.generateMipMaps, (item, value) => item.texture.generateMipMaps = AssetViews.Bool(value)),
            New("sRGB", item => item.texture.sRGB, (item, value) => item.texture.sRGB = AssetViews.Bool(value)),
            New("Filter Mode", item => item.texture.filterMode.ToString(), (item, value) => item.texture.filterMode = (FilterMode)Enum.Parse(typeof(FilterMode), AssetViews.Text(value), true))
        };

        private static Field New(string label, Func<Settings, object> get, Action<Settings, string> set)
        {
            return new Field { label = label, get = get, set = set };
        }

        internal override bool Handles(string path, UnityEngine.Object asset)
        {
            return asset is SpriteAtlas;
        }

        internal override JsonText Describe(string path, UnityEngine.Object asset, string property)
        {
            var atlas = (SpriteAtlas)asset;
            var settings = Read(path, atlas);
            var result = new JsonText().Add("Type", atlas.isVariant ? "Variant" : "Master");
            if (atlas.isVariant)
                result.Add("Master Atlas", AssetViews.ObjectLabel(Master(path, atlas)));
            foreach (var field in Fields)
                result.Add(field.label, field.get(settings));
            return result
                .Add("Objects for Packing", atlas.GetPackables().Select(AssetViews.ObjectLabel).ToArray())
                .Add("Packed Sprites", atlas.spriteCount);
        }

        internal override string[] Actions(string path, UnityEngine.Object asset)
        {
            return new[] { "Add Packable", "Remove Packable", "Pack Preview" };
        }

        internal override string Execute(string path, UnityEngine.Object asset, string action, PropertyValue[] values)
        {
            var atlas = (SpriteAtlas)asset;
            action = AssetViews.Match(Actions(path, asset), action);
            if (action == "Pack Preview")
            {
                SpriteAtlasUtility.PackAtlases(new[] { atlas }, EditorUserBuildSettings.activeBuildTarget);
                return "Packed " + atlas.spriteCount + " sprites";
            }
            // A folder, texture or sprite, like dragging it into Objects for Packing.
            var objectPath = AssetViews.Value(values, "object");
            var packable = AssetDatabase.LoadMainAssetAtPath(objectPath);
            if (packable == null)
                throw new ArgumentException("Asset was not found: " + objectPath);
            var packables = new[] { packable };
            var add = action == "Add Packable";
            if (add == atlas.GetPackables().Contains(packable))
                throw new InvalidOperationException(objectPath + (add ? " is already packed." : " is not in Objects for Packing."));
            if (IsV2(path))
            {
                var source = SpriteAtlasAsset.Load(path);
                if (add)
                    source.Add(packables);
                else
                    source.Remove(packables);
                SpriteAtlasAsset.Save(source, path);
                AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceSynchronousImport);
            }
            else
            {
                Undo.RecordObject(atlas, action);
                if (add)
                    atlas.Add(packables);
                else
                    atlas.Remove(packables);
                EditorUtility.SetDirty(atlas);
                AssetDatabase.SaveAssets();
            }
            return (add ? "Added to Objects for Packing: " : "Removed from Objects for Packing: ") + objectPath;
        }

        internal override PropertyValue[] Modify(string path, UnityEngine.Object asset, PropertyValue[] values, bool confirm, List<string> changes)
        {
            var atlas = (SpriteAtlas)asset;
            var settings = Read(path, atlas);
            var rest = new List<PropertyValue>();
            var changed = false;
            foreach (var entry in values)
            {
                if (AssetViews.KeyIs(entry.path, "Master Atlas"))
                {
                    // Choosing a master makes the atlas a Variant, as the Type dropdown plus the Master Atlas field do.
                    var master = AssetDatabase.LoadAssetAtPath<SpriteAtlas>(AssetViews.Text(entry.value));
                    if (master == null)
                        throw new ArgumentException("Sprite Atlas was not found: " + entry.value);
                    SetMaster(path, atlas, master);
                    changes.Add("Master Atlas = " + AssetDatabase.GetAssetPath(master) + " (Type = Variant)");
                    continue;
                }
                var field = Fields.FirstOrDefault(item => AssetViews.KeyIs(entry.path, item.label));
                if (field == null)
                {
                    rest.Add(entry);
                    continue;
                }
                field.set(settings, entry.value);
                changes.Add(field.label + " = " + AssetViews.Printable(field.get(settings)));
                changed = true;
            }
            if (changed)
                Write(path, atlas, settings);
            return rest.ToArray();
        }

        private static bool IsV2(string path)
        {
            return path.EndsWith(".spriteatlasv2", StringComparison.OrdinalIgnoreCase);
        }

        private static Settings Read(string path, SpriteAtlas atlas)
        {
            if (IsV2(path))
            {
                var importer = (SpriteAtlasImporter)AssetImporter.GetAtPath(path);
                return new Settings { packing = importer.packingSettings, texture = importer.textureSettings, includeInBuild = importer.includeInBuild };
            }
            return new Settings { packing = atlas.GetPackingSettings(), texture = atlas.GetTextureSettings(), includeInBuild = new SerializedObject(atlas).FindProperty("m_EditorData.bindAsDefault").boolValue };
        }

        private static void Write(string path, SpriteAtlas atlas, Settings settings)
        {
            if (IsV2(path))
            {
                var importer = (SpriteAtlasImporter)AssetImporter.GetAtPath(path);
                importer.packingSettings = settings.packing;
                importer.textureSettings = settings.texture;
                importer.includeInBuild = settings.includeInBuild;
                importer.SaveAndReimport();
                return;
            }
            Undo.RecordObject(atlas, "Modify Sprite Atlas");
            atlas.SetPackingSettings(settings.packing);
            atlas.SetTextureSettings(settings.texture);
            atlas.SetIncludeInBuild(settings.includeInBuild);
            EditorUtility.SetDirty(atlas);
            AssetDatabase.SaveAssets();
        }

        private static SpriteAtlas Master(string path, SpriteAtlas atlas)
        {
            var master = new SerializedObject(atlas).FindProperty("m_MasterAtlas");
            return master == null ? null : master.objectReferenceValue as SpriteAtlas;
        }

        private static void SetMaster(string path, SpriteAtlas atlas, SpriteAtlas master)
        {
            if (IsV2(path))
            {
                var source = SpriteAtlasAsset.Load(path);
                source.SetIsVariant(true);
                source.SetMasterAtlas(master);
                SpriteAtlasAsset.Save(source, path);
                AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceSynchronousImport);
                return;
            }
            Undo.RecordObject(atlas, "Set Master Atlas");
            atlas.SetIsVariant(true);
            atlas.SetMasterAtlas(master);
            EditorUtility.SetDirty(atlas);
            AssetDatabase.SaveAssets();
        }
    }
}
