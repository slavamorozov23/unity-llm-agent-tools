using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace UnityAgentBridge.Editor
{
    internal static class InspectorDiagnosticService
    {
        private const string LegacyInputModule = "UnityEngine.EventSystems.StandaloneInputModule";
        private const string InputSystemUiModule = "UnityEngine.InputSystem.UI.InputSystemUIInputModule, Unity.InputSystem";
        private const string ReplaceInputModule = "replace-with-input-system-ui-module";
        private static readonly string[] ParticleActions = { "play", "pause", "restart", "stop" };
        private static readonly string[] DirectorActions = { "play", "pause", "stop", "evaluate" };
        private const string NavMeshSurfaceType = "Unity.AI.Navigation.NavMeshSurface";
        private static readonly string[] NavMeshActions = { "bake", "clear" };

        internal static InspectorWarningData[] Warnings(Component component)
        {
            if (!IsLegacyInputModule(component) || !InputSystemSetupService.IsReady)
                return Array.Empty<InspectorWarningData>();

            var oldInputDisabled = PackageService.ActiveInputHandler == 1;
            return new[]
            {
                new InspectorWarningData
                {
                    severity = oldInputDisabled ? "error" : "info",
                    message = oldInputDisabled
                        ? "StandaloneInputModule uses the old InputManager, but the old InputManager is disabled. It will not work."
                        : "StandaloneInputModule uses the old InputManager while the new Input System is enabled."
                }
            };
        }

        internal static InspectorActionData[] Actions(Component component)
        {
            return BuiltInActions(component)
                .Concat(ContextMenus(component).Select(item => new InspectorActionData { id = item.Key, label = item.Key }))
                .ToArray();
        }

        private static InspectorActionData[] BuiltInActions(Component component)
        {
            if (component is ParticleSystem)
                return ParticleActions.Select(Action).ToArray();
            if (component is UnityEngine.Playables.PlayableDirector && CommandProcessor.Timeline != null)
                return DirectorActions.Select(Action).ToArray();
            if (IsNavMeshSurface(component))
                return NavMeshActions.Select(Action).ToArray();
            if (IsBakedHDProbe(component))
                return new[] { Action("bake") };
            if (!IsLegacyInputModule(component) || !InputSystemSetupService.IsReady)
                return Array.Empty<InspectorActionData>();

            return new[]
            {
                new InspectorActionData
                {
                    id = ReplaceInputModule,
                    label = "Replace with InputSystemUIInputModule"
                }
            };
        }

        private static InspectorActionData Action(string id)
        {
            return new InspectorActionData { id = id, label = char.ToUpperInvariant(id[0]) + id.Substring(1) };
        }

        internal static string Execute(BridgeRequest request)
        {
            var owner = ScenePath.ResolveObject(request.path);
            var target = ComponentService.ResolveAttachedComponent(owner, request.componentType, request.componentIndex);
            var particles = target as ParticleSystem;
            if (particles != null)
                return ExecuteParticles(particles, request.action);
            if (target is UnityEngine.Playables.PlayableDirector)
            {
                if (CommandProcessor.Timeline == null)
                    throw new InvalidOperationException("PlayableDirector actions need the Timeline package.");
                return CommandProcessor.Timeline(request);
            }
            if (IsNavMeshSurface(target))
                return ExecuteNavMesh(target, request.action);
            if (request.action == "bake" && IsBakedHDProbe(target))
                return BakeHDProbe(target);
            var menu = ContextMenus(target).FirstOrDefault(item => item.Key == request.action);
            if (menu.Value != null)
                return ExecuteContextMenu(target, menu.Key, menu.Value);
            ComponentService.EnsureEditMode();
            if (!string.Equals(request.action, ReplaceInputModule, StringComparison.Ordinal))
                throw new InvalidOperationException("Unknown Inspector action: " + request.action);

            var gameObject = owner;
            EditorPresentationService.ShowComponentOwner(gameObject);
            var component = target;
            if (!IsLegacyInputModule(component))
                throw new InvalidOperationException("The action belongs to StandaloneInputModule, not " + component.GetType().FullName + ".");
            InputSystemSetupService.RequireReady();

            var replacementType = Type.GetType(InputSystemUiModule, false);
            if (replacementType == null || !typeof(Component).IsAssignableFrom(replacementType))
                throw new InvalidOperationException("InputSystemUIInputModule is unavailable after Input System setup.");

            Undo.IncrementCurrentGroup();
            var group = Undo.GetCurrentGroup();
            Undo.SetCurrentGroupName("Unity Agent Bridge: Replace Input Module");
            try
            {
                Undo.DestroyObjectImmediate(component);
                var replacement = gameObject.GetComponent(replacementType) ?? Undo.AddComponent(gameObject, replacementType);
                if (replacement == null)
                    throw new InvalidOperationException("Unity did not create InputSystemUIInputModule.");
                EditorUtility.SetDirty(gameObject);
                if (gameObject.scene.IsValid())
                    ScenePersistenceService.MarkDirty(gameObject.scene);
                Undo.CollapseUndoOperations(group);
            }
            catch
            {
                Undo.RevertAllDownToGroup(group);
                throw;
            }

            return "Replaced StandaloneInputModule with InputSystemUIInputModule on " + ScenePath.For(gameObject) + ".";
        }

        private const BindingFlags DeclaredMethods = BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly;

        // The script's own items of the component's ⋮ menu: [ContextMenu] methods, base classes included.
        private static List<KeyValuePair<string, MethodInfo>> ContextMenus(Component component)
        {
            var items = new List<KeyValuePair<string, MethodInfo>>();
            if (!(component is MonoBehaviour))
                return items;
            for (var type = component.GetType(); type != null && type != typeof(MonoBehaviour); type = type.BaseType)
                foreach (var method in type.GetMethods(DeclaredMethods))
                    foreach (ContextMenu attribute in method.GetCustomAttributes(typeof(ContextMenu), false))
                        if (!attribute.validate && method.GetParameters().Length == 0 && items.All(item => item.Key != attribute.menuItem))
                            items.Add(new KeyValuePair<string, MethodInfo>(attribute.menuItem, method));
            return items;
        }

        private static string ExecuteContextMenu(Component component, string item, MethodInfo method)
        {
            EditorPresentationService.ShowComponent(component);
            // A validate function greys the item out in the menu.
            for (var type = component.GetType(); type != null && type != typeof(MonoBehaviour); type = type.BaseType)
                foreach (var validate in type.GetMethods(DeclaredMethods))
                    if (validate.ReturnType == typeof(bool) && validate.GetParameters().Length == 0 &&
                        validate.GetCustomAttributes(typeof(ContextMenu), false).Cast<ContextMenu>().Any(attribute => attribute.validate && attribute.menuItem == item) &&
                        !(bool)validate.Invoke(validate.IsStatic ? null : component, null))
                        throw new InvalidOperationException(item + " is disabled in the component menu now.");
            if (!EditorApplication.isPlaying)
                Undo.RecordObject(component, item);
            try
            {
                method.Invoke(method.IsStatic ? null : component, null);
            }
            catch (TargetInvocationException exception)
            {
                throw exception.InnerException ?? exception;
            }
            if (!EditorApplication.isPlaying)
            {
                EditorUtility.SetDirty(component);
                if (component.gameObject.scene.IsValid())
                    ScenePersistenceService.MarkDirty(component.gameObject.scene);
            }
            return "Ran " + item + ".";
        }

        // An HDRP reflection or planar probe in Baked mode has the Inspector's Bake button.
        private static bool IsBakedHDProbe(Component component)
        {
            if (component == null)
                return false;
            for (var type = component.GetType(); type != null; type = type.BaseType)
                if (type.FullName == "UnityEngine.Rendering.HighDefinition.HDProbe")
                    return Convert.ToString(type.GetProperty("mode").GetValue(component)) == "Baked";
            return false;
        }

        // The Bake button: HDBakedReflectionSystem.BakeProbes for this probe; it bakes only with the scenes saved.
        private static string BakeHDProbe(Component probe)
        {
            ComponentService.EnsureEditMode();
            EditorPresentationService.ShowComponent(probe);
            if (string.IsNullOrEmpty(probe.gameObject.scene.path))
                throw new InvalidOperationException("Save the scene before baking its reflection probe.");
            ScenePersistenceService.SaveOpenSceneChangesNow();
            var system = Type.GetType("UnityEditor.Rendering.HighDefinition.HDBakedReflectionSystem, Unity.RenderPipelines.HighDefinition.Editor");
            var bake = system == null ? null : system.GetMethod("BakeProbes", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
            if (bake == null)
                throw new InvalidOperationException("This HDRP version has no reflection probe bake entry point.");
            var probes = Array.CreateInstance(FindHDProbeType(probe.GetType()), 1);
            probes.SetValue(probe, 0);
            if (!(bool)bake.Invoke(null, new object[] { probes }))
                throw new InvalidOperationException("HDRP did not bake the probe; the Console has the reason.");
            ScenePersistenceService.SaveOpenSceneChangesNow();
            var texture = probe.GetType().GetProperty("bakedTexture").GetValue(probe) as Texture;
            return texture == null ? "Probe baked." : "Baked " + AssetDatabase.GetAssetPath(texture) + ".";
        }

        private static Type FindHDProbeType(Type type)
        {
            while (type.FullName != "UnityEngine.Rendering.HighDefinition.HDProbe")
                type = type.BaseType;
            return type;
        }

        private static bool IsNavMeshSurface(Component component)
        {
            return component != null && component.GetType().FullName == NavMeshSurfaceType;
        }

        // The Inspector's Bake saves the data as <scene folder>/<scene>/NavMesh-<object>.asset; Clear removes it.
        private static string ExecuteNavMesh(Component surface, string action)
        {
            ComponentService.EnsureEditMode();
            EditorPresentationService.ShowComponent(surface);
            var type = surface.GetType();
            var dataProperty = type.GetProperty("navMeshData");
            var old = dataProperty.GetValue(surface) as UnityEngine.AI.NavMeshData;
            var oldPath = old == null ? string.Empty : AssetDatabase.GetAssetPath(old);
            Undo.RecordObject(surface, "Unity Agent Bridge: NavMesh " + action);
            string result;
            switch (action)
            {
                case "bake":
                    var scenePath = surface.gameObject.scene.path;
                    if (string.IsNullOrEmpty(scenePath))
                        throw new InvalidOperationException("Save the scene before baking its NavMesh.");
                    var folder = scenePath.Substring(0, scenePath.Length - ".unity".Length);
                    if (!AssetDatabase.IsValidFolder(folder))
                        AssetDatabase.CreateFolder(System.IO.Path.GetDirectoryName(folder).Replace('\\', '/'), System.IO.Path.GetFileName(folder));
                    type.GetMethod("BuildNavMesh", Type.EmptyTypes).Invoke(surface, null);
                    var data = (UnityEngine.AI.NavMeshData)dataProperty.GetValue(surface);
                    var path = folder + "/NavMesh-" + surface.name + ".asset";
                    if (!string.IsNullOrEmpty(oldPath) && oldPath != path && oldPath.StartsWith(folder + "/NavMesh-", StringComparison.Ordinal))
                        AssetDatabase.DeleteAsset(oldPath);
                    AssetDatabase.CreateAsset(data, path);
                    var triangles = UnityEngine.AI.NavMesh.CalculateTriangulation().indices.Length / 3;
                    result = "Baked " + path + " (" + triangles + " triangles).";
                    break;
                case "clear":
                    type.GetMethod("RemoveData", Type.EmptyTypes).Invoke(surface, null);
                    dataProperty.SetValue(surface, null);
                    if (!string.IsNullOrEmpty(oldPath))
                        AssetDatabase.DeleteAsset(oldPath);
                    result = "NavMesh cleared.";
                    break;
                default:
                    throw new InvalidOperationException("NavMeshSurface actions: " + string.Join(", ", NavMeshActions) + ".");
            }
            EditorUtility.SetDirty(surface);
            ScenePersistenceService.MarkDirty(surface.gameObject.scene);
            return result;
        }

        private static string ExecuteParticles(ParticleSystem particles, string action)
        {
            EditorPresentationService.ShowComponent(particles);
            switch (action)
            {
                case "play":
                    particles.Play(true);
                    break;
                case "pause":
                    particles.Pause(true);
                    break;
                case "restart":
                    particles.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);
                    particles.Play(true);
                    break;
                case "stop":
                    particles.Stop(true, ParticleSystemStopBehavior.StopEmitting);
                    break;
                default:
                    throw new InvalidOperationException("ParticleSystem actions: " + string.Join(", ", ParticleActions) + ".");
            }
            return "ParticleSystem " + (particles.isPlaying ? "playing" : particles.isPaused ? "paused" : "stopped") +
                ", t=" + particles.time.ToString("0.##", CultureInfo.InvariantCulture) + ", particles=" + particles.particleCount + ".";
        }

        private static bool IsLegacyInputModule(Component component)
        {
            return component != null && string.Equals(component.GetType().FullName, LegacyInputModule, StringComparison.Ordinal);
        }
    }
}
