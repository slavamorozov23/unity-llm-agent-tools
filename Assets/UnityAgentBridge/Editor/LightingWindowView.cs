using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.SceneManagement;

namespace UnityAgentBridge.Editor
{
    // "Lighting" is Window > Rendering > Lighting: the Scene tab by its labels with Generate Lighting, Cancel and
    // Clear Baked Data, and "Lighting/Baked Lightmaps". A .lighting asset reads with the same labels.
    internal sealed class LightingWindowView : AssetView
    {
        internal const string WindowPath = "Lighting";
        private const string LightmapsTab = "Baked Lightmaps";

        private sealed class Field
        {
            public string label;
            public Func<LightingSettings, bool> shown;
            public Func<LightingSettings, object> get;
            public Action<LightingSettings, string> set;
        }

        private static readonly Field[] Fields =
        {
            Toggle("Realtime Global Illumination", s => SupportedRenderingFeatures.active.enlighten, s => s.realtimeGI, (s, v) => s.realtimeGI = v),
            Toggle("Realtime Environment Lighting", Realtime, s => s.realtimeEnvironmentLighting, (s, v) => s.realtimeEnvironmentLighting = v),
            Number("Indirect Resolution", Realtime, s => s.indirectResolution, (s, v) => s.indirectResolution = v),
            Toggle("Baked Global Illumination", s => true, s => s.bakedGI, (s, v) => s.bakedGI = v),
            Choice("Lighting Mode", Baked, s => s.mixedBakeMode, (s, v) => s.mixedBakeMode = v, () => LightingModes),
            Choice("Lightmapper", Baked, s => s.lightmapper, (s, v) => s.lightmapper = v,
                () => new[] { LightingSettings.Lightmapper.ProgressiveCPU, LightingSettings.Lightmapper.ProgressiveGPU }),
            Toggle("Importance Sampling", Baked, s => s.environmentImportanceSampling, (s, v) => s.environmentImportanceSampling = v),
            Integer("Direct Samples", Baked, s => s.directSampleCount, (s, v) => s.directSampleCount = v),
            Integer("Indirect Samples", Baked, s => s.indirectSampleCount, (s, v) => s.indirectSampleCount = v),
            Integer("Environment Samples", Baked, s => s.environmentSampleCount, (s, v) => s.environmentSampleCount = v),
            Number("Light Probe Sample Multiplier", Baked, s => s.lightProbeSampleCountMultiplier, (s, v) => s.lightProbeSampleCountMultiplier = v),
            Integer("Min Bounces", Baked, s => s.minBounces, (s, v) => s.minBounces = v),
            Integer("Max Bounces", Baked, s => s.maxBounces, (s, v) => s.maxBounces = v),
            Choice("Filtering", Baked, s => s.filteringMode, (s, v) => s.filteringMode = v),
            Number("Lightmap Resolution", Baked, s => s.lightmapResolution, (s, v) => s.lightmapResolution = v),
            Integer("Lightmap Padding", Baked, s => s.lightmapPadding, (s, v) => s.lightmapPadding = v),
            Integer("Max Lightmap Size", Baked, s => s.lightmapMaxSize, (s, v) => s.lightmapMaxSize = v),
            Choice("Lightmap Compression", Baked, s => s.lightmapCompression, (s, v) => s.lightmapCompression = v),
            Toggle("Ambient Occlusion", Baked, s => s.ao, (s, v) => s.ao = v),
            Number("Max Distance", s => s.bakedGI && s.ao, s => s.aoMaxDistance, (s, v) => s.aoMaxDistance = v),
            Number("Indirect Contribution", s => s.bakedGI && s.ao, s => s.aoExponentIndirect, (s, v) => s.aoExponentIndirect = v),
            Number("Direct Contribution", s => s.bakedGI && s.ao, s => s.aoExponentDirect, (s, v) => s.aoExponentDirect = v),
            Choice("Directional Mode", AnyGI, s => s.directionalityMode, (s, v) => s.directionalityMode = v,
                () => new[] { LightmapsMode.NonDirectional, LightmapsMode.CombinedDirectional }),
            Number("Albedo Boost", AnyGI, s => s.albedoBoost, (s, v) => s.albedoBoost = v),
            Number("Indirect Intensity", AnyGI, s => s.indirectScale, (s, v) => s.indirectScale = v),
            new Field
            {
                label = "Lightmap Parameters",
                shown = AnyGI,
                get = s =>
                {
                    var value = Parameters(s).objectReferenceValue;
                    // Unity's own presets read by name, as in the window.
                    return value == null ? "Default-Medium" : AssetDatabase.GetAssetPath(value).StartsWith("Assets/") ? AssetViews.ObjectLabel(value) : value.name;
                },
                set = (s, v) =>
                {
                    var property = Parameters(s);
                    property.objectReferenceValue = v.StartsWith("Assets/") ? Load<LightmapParameters>(v) : BuiltinParameters(v);
                    property.serializedObject.ApplyModifiedPropertiesWithoutUndo();
                }
            },
            Toggle("Auto Generate", s => true, s => s.autoGenerate, (s, v) => s.autoGenerate = v)
        };

        // The window's names for these enum values.
        private static readonly Dictionary<string, string> Names = new Dictionary<string, string>
        {
            { "IndirectOnly", "Baked Indirect" }, { "ProgressiveCPU", "Progressive CPU" }, { "ProgressiveGPU", "Progressive GPU" },
            { "LowQuality", "Low Quality" },
            { "NormalQuality", "Normal Quality" }, { "HighQuality", "High Quality" }
        };

        private static bool Realtime(LightingSettings settings) { return SupportedRenderingFeatures.active.enlighten && settings.realtimeGI; }
        private static bool Baked(LightingSettings settings) { return settings.bakedGI; }
        private static bool AnyGI(LightingSettings settings) { return settings.bakedGI || Realtime(settings); }

        private static MixedLightingMode[] LightingModes
        {
            get
            {
                var supported = SupportedRenderingFeatures.active.mixedLightingModes;
                return new[] { MixedLightingMode.IndirectOnly, MixedLightingMode.Subtractive, MixedLightingMode.Shadowmask }
                    .Where(mode => (supported & (SupportedRenderingFeatures.LightmapMixedBakeModes)(1 << (int)mode)) != 0).ToArray();
            }
        }

        internal static bool IsPath(string path)
        {
            return string.Equals(path, WindowPath, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(path, WindowPath + "/" + LightmapsTab, StringComparison.OrdinalIgnoreCase);
        }

        internal override bool Handles(string path, UnityEngine.Object asset)
        {
            return IsPath(path) || asset is LightingSettings;
        }

        internal override JsonText Describe(string path, UnityEngine.Object asset, string property)
        {
            if (asset == null)
                Open();
            if (path.EndsWith(LightmapsTab, StringComparison.OrdinalIgnoreCase))
                return Lightmaps();
            var settings = asset as LightingSettings ?? SceneSettings();
            var result = new JsonText();
            if (asset == null)
                result.Add("Lighting Settings", settings == null ? null : AssetViews.ObjectLabel(settings));
            // Without an asset the window shows the defaults Unity bakes with.
            var shown = settings ?? new LightingSettings();
            try
            {
                foreach (var field in Fields.Where(item => item.shown(shown)))
                    result.Add(field.label, field.get(shown));
            }
            finally
            {
                if (settings == null)
                    UnityEngine.Object.DestroyImmediate(shown);
            }
            if (string.IsNullOrEmpty(property))
                return result;
            var pair = result.Items.FirstOrDefault(item => AssetViews.KeyIs(property, item.Key));
            if (pair.Key == null)
                throw new ArgumentException("Lighting setting was not found: " + property + "; asset-info without --property lists them.");
            return new JsonText().Add(pair.Key, pair.Value);
        }

        internal override PropertyValue[] Modify(string path, UnityEngine.Object asset, PropertyValue[] values, bool confirm, List<string> changes)
        {
            ComponentService.EnsureEditMode();
            if (path.EndsWith(LightmapsTab, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Baked Lightmaps only shows the bake result.");
            if (asset == null)
                Open();
            foreach (var entry in values)
            {
                var text = AssetViews.Text(entry.value);
                if (asset == null && AssetViews.KeyIs(entry.path, "Lighting Settings"))
                {
                    Lightmapping.lightingSettings = string.IsNullOrEmpty(text) || text == "None" ? null : Load<LightingSettings>(text);
                    ScenePersistenceService.MarkDirty(SceneManager.GetActiveScene());
                    changes.Add("Lighting Settings = " + (AssetViews.ObjectLabel(SceneSettings()) ?? "None"));
                    continue;
                }
                var field = Fields.FirstOrDefault(item => AssetViews.KeyIs(entry.path, item.label));
                if (field == null)
                    throw new ArgumentException("Lighting setting was not found: " + entry.path + "; asset-info lists them.");
                var settings = asset as LightingSettings ?? SceneSettings();
                if (settings == null)
                    throw new InvalidOperationException("The scene has no Lighting Settings asset; run the New action or set Lighting Settings.");
                Undo.RecordObject(settings, "Change " + field.label);
                field.set(settings, text);
                EditorUtility.SetDirty(settings);
                changes.Add(field.label + " = " + AssetViews.Printable(field.get(settings)));
            }
            AssetDatabase.SaveAssets();
            return Array.Empty<PropertyValue>();
        }

        internal override string[] Actions(string path, UnityEngine.Object asset)
        {
            if (asset != null || path.EndsWith(LightmapsTab, StringComparison.OrdinalIgnoreCase))
                return Array.Empty<string>();
            return Lightmapping.isRunning ? new[] { "Cancel", "New" } : new[] { "Generate Lighting", "Clear Baked Data", "New" };
        }

        internal override string Execute(string path, UnityEngine.Object asset, string action, PropertyValue[] values)
        {
            switch (AssetViews.Normalize(action))
            {
                case "generatelighting":
                case "bake":
                    return LightingService.Execute("bake");
                case "cancel":
                    return LightingService.Execute("cancel");
                case "clearbakeddata":
                case "clear":
                    return LightingService.Execute("clear");
                case "new":
                    return CreateSettings();
                default:
                    throw new InvalidOperationException("Lighting actions: generate-lighting, cancel, clear-baked-data, new.");
            }
        }

        // The window's New button: a default Lighting Settings asset next to the scene, assigned to it.
        private static string CreateSettings()
        {
            ComponentService.EnsureEditMode();
            Open();
            var scene = SceneManager.GetActiveScene();
            var folder = string.IsNullOrEmpty(scene.path) ? "Assets" : Path.GetDirectoryName(scene.path).Replace('\\', '/');
            var path = AssetDatabase.GenerateUniqueAssetPath(folder + "/New Lighting Settings.lighting");
            var settings = new LightingSettings { name = Path.GetFileNameWithoutExtension(path) };
            AssetDatabase.CreateAsset(settings, path);
            Lightmapping.lightingSettings = settings;
            EditorSceneManager.MarkSceneDirty(scene);
            return "Created " + path + " for " + scene.name + ".";
        }

        private static JsonText Lightmaps()
        {
            var data = Lightmapping.lightingDataAsset;
            var maps = LightmapSettings.lightmaps.Select(map => map.lightmapColor)
                .Where(texture => texture != null).Select(texture => texture.name + " " + texture.width + "x" + texture.height).ToArray();
            return new JsonText()
                // Before the first bake Unity reports a built-in placeholder.
                .Add("Lighting Data", data == null || !AssetDatabase.GetAssetPath(data).StartsWith("Assets/") ? null : AssetViews.ObjectLabel(data))
                .Add("Lightmaps", maps);
        }

        private static LightmapParameters BuiltinParameters(string name)
        {
            var match = AssetDatabase.LoadAllAssetsAtPath("Resources/unity_builtin_extra").OfType<LightmapParameters>()
                .FirstOrDefault(item => AssetViews.KeyIs(name, item.name));
            if (match == null)
                throw new ArgumentException("Lightmap Parameters must be an asset path or a built-in preset such as Default-Medium.");
            return match;
        }

        // LightingSettings has no API for it.
        private static SerializedProperty Parameters(LightingSettings settings)
        {
            return new SerializedObject(settings).FindProperty("m_LightmapParameters");
        }

        private static LightingSettings SceneSettings()
        {
            LightingSettings settings;
            return Lightmapping.TryGetLightingSettings(out settings) ? settings : null;
        }

        private static void Open()
        {
            EditorPresentationService.ShowWindow("UnityEditor.LightingWindow");
        }

        private static T Load<T>(string path) where T : UnityEngine.Object
        {
            var asset = AssetDatabase.LoadAssetAtPath<T>(path);
            if (asset == null)
                throw new ArgumentException(typeof(T).Name + " asset was not found: " + path);
            return asset;
        }

        private static Field Toggle(string label, Func<LightingSettings, bool> shown, Func<LightingSettings, bool> get, Action<LightingSettings, bool> set)
        {
            return new Field { label = label, shown = shown, get = s => get(s), set = (s, v) => set(s, AssetViews.Bool(v)) };
        }

        private static Field Integer(string label, Func<LightingSettings, bool> shown, Func<LightingSettings, int> get, Action<LightingSettings, int> set)
        {
            return new Field { label = label, shown = shown, get = s => get(s), set = (s, v) => set(s, AssetViews.Int(v)) };
        }

        private static Field Number(string label, Func<LightingSettings, bool> shown, Func<LightingSettings, float> get, Action<LightingSettings, float> set)
        {
            return new Field
            {
                label = label,
                shown = shown,
                get = s => Math.Round(get(s), 4),
                set = (s, v) => set(s, float.Parse(v, NumberStyles.Float, CultureInfo.InvariantCulture))
            };
        }

        private static Field Choice<T>(string label, Func<LightingSettings, bool> shown, Func<LightingSettings, T> get, Action<LightingSettings, T> set,
            Func<T[]> choices = null) where T : struct, Enum
        {
            return new Field
            {
                label = label,
                shown = shown,
                get = s => Name(get(s)),
                set = (s, v) =>
                {
                    var options = choices == null ? (T[])Enum.GetValues(typeof(T)) : choices();
                    var match = options.Where(option => AssetViews.KeyIs(v, Name(option), option.ToString())).ToArray();
                    if (match.Length == 0)
                        throw new ArgumentException(label + " must be one of: " + string.Join(", ", options.Select(option => Name(option))));
                    set(s, match[0]);
                }
            };
        }

        private static string Name<T>(T value) where T : struct, Enum
        {
            // LightmapsMode keeps obsolete aliases of its values.
            if (value is LightmapsMode)
                return Convert.ToInt32(value) == 0 ? "Non-Directional" : "Directional";
            string name;
            return Names.TryGetValue(value.ToString(), out name) ? name : ObjectNames.NicifyVariableName(value.ToString());
        }
    }
}
