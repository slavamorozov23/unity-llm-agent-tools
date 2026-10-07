using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using UnityEditor;
using UnityEngine;

namespace UnityAgentBridge.Editor
{
    // Serialized ints that the Inspector shows as a popup: Particle System modes and pipeline lists such as Sky Type.
    // They read and write by the popup's names, like real enums.
    internal static class IntChoices
    {
        private static readonly Dictionary<string, Type> ParticleSystemFields = new Dictionary<string, Type>
        {
            { "moveWithTransform", typeof(ParticleSystemSimulationSpace) },
            { "scalingMode", typeof(ParticleSystemScalingMode) },
            { "cullingMode", typeof(ParticleSystemCullingMode) },
            { "stopAction", typeof(ParticleSystemStopAction) },
            { "ringBufferMode", typeof(ParticleSystemRingBufferMode) },
            { "emitterVelocityMode", typeof(ParticleSystemEmitterVelocityMode) },
            { "InitialModule.gravitySource", typeof(ParticleSystemGravitySource) },
            { "ShapeModule.type", typeof(ParticleSystemShapeType) },
            { "ShapeModule.placementMode", typeof(ParticleSystemMeshShapeType) },
            { "ShapeModule.m_TextureClipChannel", typeof(ParticleSystemShapeTextureChannel) },
            { "ShapeModule.radius.mode", typeof(ParticleSystemShapeMultiModeValue) },
            { "ShapeModule.arc.mode", typeof(ParticleSystemShapeMultiModeValue) },
            { "ShapeModule.m_MeshSpawn.mode", typeof(ParticleSystemShapeMultiModeValue) },
            { "InheritVelocityModule.m_Mode", typeof(ParticleSystemInheritVelocityMode) },
            { "ExternalForcesModule.influenceFilter", typeof(ParticleSystemGameObjectFilter) },
            { "NoiseModule.quality", typeof(ParticleSystemNoiseQuality) },
            { "CollisionModule.type", typeof(ParticleSystemCollisionType) },
            { "CollisionModule.collisionMode", typeof(ParticleSystemCollisionMode) },
            { "CollisionModule.quality", typeof(ParticleSystemCollisionQuality) },
            { "TriggerModule.inside", typeof(ParticleSystemOverlapAction) },
            { "TriggerModule.outside", typeof(ParticleSystemOverlapAction) },
            { "TriggerModule.enter", typeof(ParticleSystemOverlapAction) },
            { "TriggerModule.exit", typeof(ParticleSystemOverlapAction) },
            { "UVModule.mode", typeof(ParticleSystemAnimationMode) },
            { "UVModule.timeMode", typeof(ParticleSystemAnimationTimeMode) },
            { "UVModule.animationType", typeof(ParticleSystemAnimationType) },
            { "TrailModule.mode", typeof(ParticleSystemTrailMode) },
            { "TrailModule.textureMode", typeof(ParticleSystemTrailTextureMode) },
        };

        private static readonly Dictionary<string, Type> ParticleRendererFields = new Dictionary<string, Type>
        {
            { "m_RenderMode", typeof(ParticleSystemRenderMode) },
            { "m_RenderAlignment", typeof(ParticleSystemRenderSpace) },
            { "m_SortMode", typeof(ParticleSystemSortMode) },
            { "m_MeshDistribution", typeof(ParticleSystemMeshDistribution) },
        };

        internal static KeyValuePair<int, string>[] For(SerializedProperty property)
        {
            if (property.propertyType != SerializedPropertyType.Integer || property.serializedObject.targetObject == null)
                return null;
            var owner = property.serializedObject.targetObject.GetType();
            Type type;
            if (owner == typeof(ParticleSystem) && ParticleSystemFields.TryGetValue(property.propertyPath, out type) ||
                owner == typeof(ParticleSystemRenderer) && ParticleRendererFields.TryGetValue(property.propertyPath, out type))
                return EnumChoices(type);
            if (owner.FullName == "UnityEngine.Rendering.HighDefinition.VisualEnvironment")
            {
                if (property.propertyPath == "skyType.m_Value")
                    return PipelinePopup("skyClassNames", "skyUniqueIDs");
                if (property.propertyPath == "cloudType.m_Value")
                    return PipelinePopup("cloudClassNames", "cloudUniqueIDs");
            }
            type = property.depth == 0 ? SettingsEnum(owner, property.name) : null;
            return type == null ? null : EnumChoices(type);
        }

        // Settings assets (Editor, Player, Quality…) store their popups as ints; the static C# property of the same name has the enum.
        private static Type SettingsEnum(Type owner, string name)
        {
            var key = AssetViews.Normalize(name);
            var match = owner.GetProperties(BindingFlags.Public | BindingFlags.Static)
                .FirstOrDefault(item => item.PropertyType.IsEnum && AssetViews.Normalize(item.Name) == key && !item.IsDefined(typeof(ObsoleteAttribute), false));
            return match == null ? null : match.PropertyType;
        }

        internal static string Name(SerializedProperty property)
        {
            var choices = For(property);
            if (choices == null)
                return null;
            var value = property.intValue;
            var match = choices.Where(item => item.Key == value).ToList();
            return match.Count > 0 ? match[0].Value : value.ToString(System.Globalization.CultureInfo.InvariantCulture);
        }

        internal static bool TrySet(SerializedProperty property, string name)
        {
            var choices = For(property);
            if (choices == null)
                return false;
            var match = choices.Where(item => AssetViews.KeyIs(name, item.Value)).ToList();
            if (match.Count == 0)
                throw new InvalidOperationException("Enum value was not found. Available values: " + string.Join(", ", choices.Select(item => item.Value)));
            property.intValue = match[0].Key;
            return true;
        }

        private static KeyValuePair<int, string>[] EnumChoices(Type type)
        {
            var choices = type.GetFields(BindingFlags.Public | BindingFlags.Static)
                .Where(field => !field.IsDefined(typeof(ObsoleteAttribute), false))
                .Select(field => new KeyValuePair<int, string>(Convert.ToInt32(field.GetValue(null)), ObjectNames.NicifyVariableName(field.Name)))
                .ToList();
            // Flags (Enter Play Mode Options) also read as their combinations: "Disable Domain Reload, Disable Scene Reload".
            var bits = choices.Where(item => item.Key > 0 && (item.Key & (item.Key - 1)) == 0).ToList();
            if (type.IsDefined(typeof(FlagsAttribute), false) && bits.Count <= 5)
                for (var mask = 1; mask < 1 << bits.Count; mask++)
                {
                    var set = bits.Where((item, index) => (mask & (1 << index)) != 0).ToList();
                    var value = set.Aggregate(0, (sum, item) => sum | item.Key);
                    if (set.Count > 1 && choices.All(item => item.Key != value))
                        choices.Add(new KeyValuePair<int, string>(value, string.Join(", ", set.Select(item => item.Value))));
                }
            return choices.ToArray();
        }

        // The Volume Inspector's own popup lists (Sky Type, Cloud Type): registered sky classes by their unique id.
        private static KeyValuePair<int, string>[] PipelinePopup(string namesProperty, string idsProperty)
        {
            var editor = AppDomain.CurrentDomain.GetAssemblies()
                .Select(assembly => assembly.GetType("UnityEditor.Rendering.HighDefinition.VisualEnvironmentEditor", false))
                .FirstOrDefault(item => item != null);
            if (editor == null)
                return null;
            const BindingFlags flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static;
            var names = editor.GetProperty(namesProperty, flags)?.GetValue(null) as List<GUIContent>;
            var ids = editor.GetProperty(idsProperty, flags)?.GetValue(null) as List<int>;
            if (names == null || ids == null)
                return null;
            return ids.Select((id, index) => new KeyValuePair<int, string>(id, names[index].text.Trim())).ToArray();
        }
    }
}
