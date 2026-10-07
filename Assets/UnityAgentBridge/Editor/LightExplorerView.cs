using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;

namespace UnityAgentBridge.Editor
{
    // "Light Explorer" is Window > Rendering > Light Explorer: its tables for the open scenes, one row per object with
    // the window's main columns. Cells are the components' own fields, edited with component-modify.
    internal sealed class LightExplorerView : AssetView
    {
        internal const string WindowPath = "Light Explorer";
        private const BindingFlags Public = BindingFlags.Instance | BindingFlags.Public;

        internal static bool IsPath(string path)
        {
            return path != null && (path.Equals(WindowPath, StringComparison.OrdinalIgnoreCase) ||
                path.StartsWith(WindowPath + "/", StringComparison.OrdinalIgnoreCase));
        }

        internal override bool Handles(string path, UnityEngine.Object asset)
        {
            return IsPath(path);
        }

        private static string[] Tabs()
        {
            var tabs = new List<string> { "Lights" };
            if (VolumeType != null)
                tabs.Add("Volumes");
            tabs.Add("Reflection Probes");
            if (HdType("PlanarReflectionProbe") != null)
                tabs.Add("Planar Reflection Probes");
            return tabs.ToArray();
        }

        internal override JsonText Describe(string path, UnityEngine.Object asset, string property)
        {
            if (!string.IsNullOrEmpty(property))
                throw new ArgumentException("Light Explorer lists whole tables; read one object with object-info.");
            EditorPresentationService.ShowWindow("UnityEditor.LightingExplorerWindow");
            var tab = path.Length > WindowPath.Length ? path.Substring(WindowPath.Length + 1).Trim('/') : "Lights";
            var tabs = Tabs();
            var match = tabs.FirstOrDefault(item => AssetViews.KeyIs(tab, item));
            if (match == null)
                throw new ArgumentException("Light Explorer tab was not found: " + tab + ". Tabs: " + string.Join(", ", tabs));
            var result = new JsonText();
            if (path.Length == WindowPath.Length)
                result.Add("tabs", tabs);
            switch (match)
            {
                case "Lights": return result.Add("Lights", Rows<Light>(LightRow));
                case "Volumes": return result.Add("Volumes", Rows(VolumeType, VolumeRow));
                case "Planar Reflection Probes": return result.Add(match, Rows(HdType("PlanarReflectionProbe"), ProbeRow));
                default:
                    var hd = HdType("HDAdditionalReflectionData");
                    return result.Add(match, hd != null ? Rows(hd, ProbeRow) : Rows<ReflectionProbe>(ProbeRow));
            }
        }

        internal override PropertyValue[] Modify(string path, UnityEngine.Object asset, PropertyValue[] values, bool confirm, List<string> changes)
        {
            throw new InvalidOperationException("Light Explorer cells are component fields; change them with component-modify on the row's object.");
        }

        private static JsonText[] Rows<T>(Func<Component, JsonText> row) where T : Component
        {
            return Rows(typeof(T), row);
        }

        private static JsonText[] Rows(Type type, Func<Component, JsonText> row)
        {
            return UnityEngine.Object.FindObjectsByType(type, FindObjectsInactive.Include)
                .OfType<Component>()
                .Where(component => component.gameObject.scene.IsValid() && (component.hideFlags & HideFlags.DontSave) == 0)
                .Select(component => new { component, path = ScenePath.For(component.gameObject) })
                .OrderBy(item => item.path, StringComparer.Ordinal)
                .Select(item =>
                {
                    var result = new JsonText().Add("Name", item.path);
                    var behaviour = item.component as Behaviour;
                    var enabled = behaviour == null || behaviour.isActiveAndEnabled;
                    result.AddIf(!enabled, "Enabled", false);
                    foreach (var pair in row(item.component).Items)
                        result.Add(pair.Key, pair.Value);
                    return result;
                })
                .ToArray();
        }

        private static JsonText LightRow(Component component)
        {
            var light = (Light)component;
            var row = new JsonText()
                .Add("Type", ObjectNames.NicifyVariableName(light.type.ToString()))
                .Add("Mode", light.lightmapBakeType.ToString());
            var color = "#" + ColorUtility.ToHtmlStringRGB(light.color);
            if (!light.useColorTemperature || color != "#FFFFFF")
                row.Add("Color", color);
            if (light.useColorTemperature)
                row.Add("Temperature", Math.Round(light.colorTemperature));
            row.Add("Intensity", Intensity(light));
            if (light.type != LightType.Directional)
                row.Add("Range", Math.Round(light.range, 3));
            return row.Add("Shadows", light.shadows != LightShadows.None);
        }

        // HDRP shows intensity in the light's unit; other pipelines have no units.
        private static object Intensity(Light light)
        {
            var utils = Type.GetType("UnityEngine.Rendering.LightUnitUtils, Unity.RenderPipelines.Core.Runtime");
            var pipeline = GraphicsSettings.currentRenderPipeline;
            if (utils == null || pipeline == null || pipeline.GetType().Name != "HDRenderPipelineAsset")
                return Math.Round(light.intensity, 4);
            var native = (LightUnit)utils.GetMethod("GetNativeLightUnit").Invoke(null, new object[] { light.type });
            var unit = light.lightUnit;
            if (!(bool)utils.GetMethod("IsLightUnitSupported").Invoke(null, new object[] { light.type, unit }))
                unit = native;
            var value = (float)utils.GetMethod("ConvertIntensity").Invoke(null, new object[] { light, light.intensity, native, unit });
            return Math.Round(value, 3).ToString("R", System.Globalization.CultureInfo.InvariantCulture) + " " + unit;
        }

        private static JsonText VolumeRow(Component volume)
        {
            var profile = (UnityEngine.Object)Member(volume, "sharedProfile");
            var row = new JsonText()
                .Add("Mode", (bool)Member(volume, "isGlobal") ? "Global" : "Local")
                .Add("Priority", Convert.ToDouble(Member(volume, "priority")))
                .Add("Profile", profile == null ? null : AssetViews.ObjectLabel(profile));
            var weight = Convert.ToDouble(Member(volume, "weight"));
            row.AddIf(weight != 1, "Weight", Math.Round(weight, 3));
            // The profile's active overrides stand for the window's Visual Environment, Sky, Fog and Volumetric columns.
            var components = profile == null ? null : Member(profile, "components") as System.Collections.IList;
            if (components != null)
                row.Add("Overrides", components.OfType<ScriptableObject>()
                    .Where(item => item != null && (bool)Member(item, "active"))
                    .Select(item => ObjectNames.NicifyVariableName(item.GetType().Name)).ToArray());
            return row;
        }

        private static JsonText ProbeRow(Component probe)
        {
            var row = new JsonText().Add("Mode", Convert.ToString(Member(probe, "mode")));
            var influence = Member(probe, "influenceVolume");
            if (influence != null && probe.GetType().Name != "PlanarReflectionProbe")
                row.Add("Shape", Convert.ToString(Member(influence, "shape")));
            var baked = Member(probe, "bakedTexture") as Texture;
            if (row.Items.First().Value as string == "Baked")
                row.Add("Baked Texture", baked == null ? null : AssetViews.ObjectLabel(baked));
            return row;
        }

        // Pipeline types are read by name: the bridge does not reference their packages.
        private static object Member(object target, string name)
        {
            var type = target.GetType();
            var property = type.GetProperty(name, Public);
            if (property != null)
                return property.GetValue(target);
            var field = type.GetField(name, Public);
            return field == null ? null : field.GetValue(target);
        }

        private static Type VolumeType
        {
            get { return Type.GetType("UnityEngine.Rendering.Volume, Unity.RenderPipelines.Core.Runtime"); }
        }

        private static Type HdType(string name)
        {
            var pipeline = GraphicsSettings.currentRenderPipeline;
            if (pipeline == null || pipeline.GetType().Name != "HDRenderPipelineAsset")
                return null;
            return Type.GetType("UnityEngine.Rendering.HighDefinition." + name + ", Unity.RenderPipelines.HighDefinition.Runtime");
        }
    }
}
