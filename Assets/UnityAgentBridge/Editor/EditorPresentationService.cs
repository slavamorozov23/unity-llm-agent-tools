using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using UnityEditor;
using UnityEditorInternal;
using UnityEngine;
using UnityEngine.UIElements;

namespace UnityAgentBridge.Editor
{
    internal static class UnityObjectIdentity
    {
        internal static Type TransientIdType
        {
            get
            {
#if UNITY_6000_0_OR_NEWER
                return typeof(EntityId);
#else
                return typeof(int);
#endif
            }
        }

        internal static object TransientId(UnityEngine.Object target)
        {
            if (target == null)
                throw new ArgumentNullException("target");
#if UNITY_6000_0_OR_NEWER
            return target.GetEntityId();
#else
            return target.GetInstanceID();
#endif
        }

        internal static int TransientHash(UnityEngine.Object target)
        {
            return TransientId(target).GetHashCode();
        }
    }

    internal static class EditorPresentationService
    {
        private const BindingFlags InstanceMethod = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        private const BindingFlags StaticMethod = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;

        public static void ShowSceneObject(GameObject target)
        {
            if (target == null)
                throw new ArgumentNullException("target");

            var hierarchyType = RequireEditorType("UnityEditor.SceneHierarchyWindow");
            var hierarchy = ShowWindow(hierarchyType.FullName, "UnityEditor.ProjectBrowser", "UnityEditor.InspectorWindow");
            if (hierarchy == null)
            {
                // A maximized window covers the layout; opening Hierarchy or Scene would float a copy over it.
                Selection.activeGameObject = target;
                return;
            }
            var setExpanded = RequireMethod(
                hierarchyType, "SetExpanded", InstanceMethod, UnityObjectIdentity.TransientIdType, typeof(bool));
            var ancestors = new Stack<Transform>();
            for (var current = target.transform.parent; current != null; current = current.parent)
                ancestors.Push(current);
            while (ancestors.Count > 0)
                setExpanded.Invoke(hierarchy, new object[] { UnityObjectIdentity.TransientId(ancestors.Pop().gameObject), true });

            Selection.activeGameObject = target;
            RequireMethod(hierarchyType, "FrameObject", InstanceMethod, UnityObjectIdentity.TransientIdType, typeof(bool))
                .Invoke(hierarchy, new object[] { UnityObjectIdentity.TransientId(target), true });
            hierarchy.Repaint();

            // In Play Mode the Scene tab stays where it is: brought over a docked Game View, it would stop the game rendering.
            var sceneView = SceneView.lastActiveSceneView;
            if (EditorApplication.isPlaying && sceneView == null)
                return;
            if (!EditorApplication.isPlaying)
            {
                sceneView = sceneView ?? EditorWindow.GetWindow<SceneView>();
                sceneView.Show();
                sceneView.Focus();
            }
            var bounds = FocusBounds(target);
            var largest = Mathf.Max(bounds.size.x, Mathf.Max(bounds.size.y, bounds.size.z));
            bounds.Expand(Mathf.Max(4f, largest * 0.5f));
            sceneView.Frame(bounds, true);
            sceneView.Repaint();
        }

        private static Bounds FocusBounds(GameObject target)
        {
            Bounds bounds;
            for (var current = target.transform; current != null; current = current.parent)
                if (TryOwnBounds(current.gameObject, out bounds))
                    return bounds;
            if (TryDescendantBounds(target, out bounds))
                return bounds;
            return new Bounds(target.transform.position, Vector3.one * 4f);
        }

        private static bool TryOwnBounds(GameObject target, out Bounds bounds)
        {
            var found = false;
            bounds = default(Bounds);
            var rectTransform = target.transform as RectTransform;
            if (rectTransform != null && rectTransform.rect.width > 0.01f && rectTransform.rect.height > 0.01f)
            {
                var corners = new Vector3[4];
                rectTransform.GetWorldCorners(corners);
                bounds = new Bounds(corners[0], Vector3.zero);
                for (var index = 1; index < corners.Length; index++)
                    bounds.Encapsulate(corners[index]);
                found = Meaningful(bounds);
            }
            foreach (var renderer in target.GetComponents<Renderer>())
                Encapsulate(renderer.bounds, ref bounds, ref found);
            foreach (var collider in target.GetComponents<Collider>())
                Encapsulate(collider.bounds, ref bounds, ref found);
            foreach (var collider in target.GetComponents<Collider2D>())
                Encapsulate(collider.bounds, ref bounds, ref found);
            return found;
        }

        private static bool TryDescendantBounds(GameObject target, out Bounds bounds)
        {
            var found = false;
            bounds = default(Bounds);
            foreach (var renderer in target.GetComponentsInChildren<Renderer>(true))
                Encapsulate(renderer.bounds, ref bounds, ref found);
            if (found)
                return true;
            foreach (var rectTransform in target.GetComponentsInChildren<RectTransform>(true))
            {
                if (rectTransform.rect.width <= 0.01f || rectTransform.rect.height <= 0.01f)
                    continue;
                var corners = new Vector3[4];
                rectTransform.GetWorldCorners(corners);
                var candidate = new Bounds(corners[0], Vector3.zero);
                for (var index = 1; index < corners.Length; index++)
                    candidate.Encapsulate(corners[index]);
                Encapsulate(candidate, ref bounds, ref found);
            }
            return found;
        }

        private static void Encapsulate(Bounds candidate, ref Bounds bounds, ref bool found)
        {
            if (!Meaningful(candidate))
                return;
            if (found)
                bounds.Encapsulate(candidate);
            else
            {
                bounds = candidate;
                found = true;
            }
        }

        private static bool Meaningful(Bounds bounds)
        {
            var size = bounds.size;
            return IsFinite(bounds.center.x) && IsFinite(bounds.center.y) && IsFinite(bounds.center.z) &&
                IsFinite(size.x) && IsFinite(size.y) && IsFinite(size.z) && size.sqrMagnitude > 0.0001f;
        }

        private static bool IsFinite(float value)
        {
            return !float.IsNaN(value) && !float.IsInfinity(value);
        }

        public static void ShowComponentOwner(GameObject target)
        {
            ShowSceneObject(target);
            var inspector = ShowWindow("UnityEditor.InspectorWindow", "UnityEditor.SceneHierarchyWindow");
            if (inspector != null)
                inspector.Repaint();
        }

        public static void ShowComponent(Component component)
        {
            if (component == null)
                throw new ArgumentNullException("component");

            ShowSceneObject(component.gameObject);
            InternalEditorUtility.SetIsInspectorExpanded(component, true);
            var inspector = ShowWindow("UnityEditor.InspectorWindow", "UnityEditor.SceneHierarchyWindow");
            if (inspector == null)
                return;
            inspector.Repaint();
            NextUpdate(() => ScrollToComponent(inspector, component));
        }

        // The Inspector keeps its own foldout state: a changed nested field or list is unfolded there, as the developer
        // opens it to edit. Done on the next update, when the Inspector has built editors for the shown object.
        public static void RevealProperties(UnityEngine.Object target, IEnumerable<string> propertyPaths)
        {
            var paths = propertyPaths.Where(path => !string.IsNullOrEmpty(path)).ToArray();
            if (target == null || paths.Length == 0)
                return;
            NextUpdate(() =>
            {
                var revealed = false;
                foreach (var editor in ActiveEditorTracker.sharedTracker.activeEditors)
                {
                    if (editor == null || editor.target != target)
                        continue;
                    foreach (var path in paths)
                        foreach (var foldout in FoldoutPaths(path))
                        {
                            var property = editor.serializedObject.FindProperty(foldout);
                            if (property != null && property.hasVisibleChildren && !property.isExpanded)
                            {
                                property.isExpanded = true;
                                revealed = true;
                            }
                        }
                }
                if (revealed)
                    InternalEditorUtility.RepaintAllViews();
            });
        }

        // "a.b.Array.data[2].c" unfolds "a", "a.b", "a.b.Array.data[2]" and the field itself when it is a list or struct.
        private static IEnumerable<string> FoldoutPaths(string path)
        {
            var segments = path.Split('.');
            var current = string.Empty;
            for (var index = 0; index < segments.Length; index++)
            {
                current = current.Length == 0 ? segments[index] : current + "." + segments[index];
                if (segments[index] == "Array")
                    continue;
                yield return current;
            }
        }

        // A window missing from the layout opens as a tab next to dockNextTo instead of floating.
        public static EditorWindow ShowWindow(string typeName, params string[] dockNextTo)
        {
            var type = RequireEditorType(typeName);
            // Docked windows are hidden behind a maximized one; GetWindow would open a second, floating one on top.
            var maximized = Resources.FindObjectsOfTypeAll<EditorWindow>().FirstOrDefault(window => window.maximized);
            if (maximized != null && maximized.GetType() != type)
                return null;
            var neighbours = Resources.FindObjectsOfTypeAll(type).Length > 0 ? new Type[0] : dockNextTo
                .Select(name => AppDomain.CurrentDomain.GetAssemblies().Select(assembly => assembly.GetType(name, false)).FirstOrDefault(item => item != null))
                .Where(item => item != null && Resources.FindObjectsOfTypeAll(item).Length > 0)
                .ToArray();
            var window = neighbours.Length == 0
                ? EditorWindow.GetWindow(type)
                : (EditorWindow)typeof(EditorWindow).GetMethods(BindingFlags.Public | BindingFlags.Static)
                    .First(method => method.Name == "GetWindow" && method.IsGenericMethodDefinition &&
                        method.GetParameters().Select(parameter => parameter.ParameterType).SequenceEqual(new[] { typeof(string), typeof(bool), typeof(Type[]) }))
                    .MakeGenericMethod(type)
                    .Invoke(null, new object[] { null, true, neighbours });
            window.Show();
            window.Focus();
            window.Repaint();
            return window;
        }

        public static void ShowInspectorObject(UnityEngine.Object target)
        {
            if (target == null)
                throw new ArgumentNullException("target");
            ActiveEditorTracker.sharedTracker.isLocked = false;
            Selection.activeObject = target;
            var inspector = ShowWindow("UnityEditor.InspectorWindow", "UnityEditor.SceneHierarchyWindow");
            if (inspector != null)
                inspector.Repaint();
        }

        public static void ShowAsset(UnityEngine.Object asset)
        {
            if (asset == null)
                throw new ArgumentNullException("asset");
            var assetPath = AssetDatabase.GetAssetPath(asset);
            if (string.IsNullOrEmpty(assetPath))
                throw new InvalidOperationException("Object is not an asset: " + asset.name);
            ShowAssetPath(assetPath, asset);
        }

        public static void ShowAssetPath(string assetPath)
        {
            if (string.IsNullOrWhiteSpace(assetPath))
                throw new ArgumentException("Asset path is required.", "assetPath");
            var asset = AssetDatabase.LoadMainAssetAtPath(assetPath);
            if (asset == null && !AssetDatabase.IsValidFolder(assetPath))
                throw new FileNotFoundException("Asset was not found: " + assetPath);
            ShowAssetPath(assetPath, asset);
        }

        public static void ShowAssetFolder(string folderPath)
        {
            if (!AssetDatabase.IsValidFolder(folderPath))
                throw new DirectoryNotFoundException("Asset folder was not found: " + folderPath);
            ShowProjectBrowser(folderPath, null);
        }

        private static void ShowAssetPath(string assetPath, UnityEngine.Object asset)
        {
            var folderPath = AssetDatabase.IsValidFolder(assetPath)
                ? assetPath
                : Path.GetDirectoryName(assetPath).Replace('\\', '/');
            ShowProjectBrowser(folderPath, asset);
        }

        private static void ShowProjectBrowser(string folderPath, UnityEngine.Object asset)
        {
            // Docked windows are hidden behind a maximized one; GetWindow would open a second, floating Project on top.
            if (Resources.FindObjectsOfTypeAll<EditorWindow>().Any(window => window.maximized))
                return;
            var browserType = RequireEditorType("UnityEditor.ProjectBrowser");
            var browser = EditorWindow.GetWindow(browserType);
            browser.Show();
            browser.Focus();
            if (asset != null)
            {
                Selection.activeObject = asset;
                EditorGUIUtility.PingObject(asset);
            }
            else
            {
                var folder = AssetDatabase.LoadAssetAtPath<DefaultAsset>(folderPath);
                if (folder != null)
                    Selection.activeObject = folder;
            }

            NextUpdate(delegate
            {
                if (browser == null)
                    return;
                var folderId = RequireMethod(browserType, "GetFolderInstanceID", StaticMethod, typeof(string))
                    .Invoke(null, new object[] { folderPath });
                RequireMethod(browserType, "ShowFolderContents", InstanceMethod, UnityObjectIdentity.TransientIdType, typeof(bool))
                    .Invoke(browser, new object[] { folderId, true });
                if (asset != null)
                    RequireMethod(browserType, "FrameObject", InstanceMethod, UnityObjectIdentity.TransientIdType, typeof(bool))
                        .Invoke(browser, new object[] { UnityObjectIdentity.TransientId(asset), true });
                browser.Focus();
                browser.Repaint();
            });
        }

        // delayCall waits for editor focus; update keeps running while Unity is in the background.
        internal static void NextUpdate(EditorApplication.CallbackFunction action)
        {
            EditorApplication.CallbackFunction run = null;
            run = () =>
            {
                EditorApplication.update -= run;
                action();
            };
            EditorApplication.update += run;
        }

        private static void ScrollToComponent(EditorWindow inspector, Component component)
        {
            if (inspector == null || component == null)
                return;
            var inspectorElementType = RequireEditorType("UnityEditor.UIElements.InspectorElement");
            var editorProperty = inspectorElementType.GetProperty("editor", InstanceMethod);
            if (editorProperty == null)
                throw new MissingMemberException(inspectorElementType.FullName, "editor");
            var scrollView = inspector.rootVisualElement.Query<ScrollView>().First();
            foreach (var element in inspector.rootVisualElement.Query<VisualElement>().ToList())
            {
                if (!inspectorElementType.IsInstanceOfType(element))
                    continue;
                var editor = editorProperty.GetValue(element, null) as UnityEditor.Editor;
                if (editor == null || !editor.targets.Contains(component))
                    continue;
                scrollView.ScrollTo(element);
                inspector.Focus();
                inspector.Repaint();
                return;
            }
        }

        private static Type RequireEditorType(string fullName)
        {
            var type = AppDomain.CurrentDomain.GetAssemblies()
                .Select(assembly => assembly.GetType(fullName, false))
                .FirstOrDefault(candidate => candidate != null);
            if (type == null)
                throw new MissingMemberException("Unity editor window type is unavailable: " + fullName);
            return type;
        }

        private static MethodInfo RequireMethod(Type type, string name, BindingFlags flags, params Type[] parameters)
        {
            var method = type.GetMethod(name, flags, null, parameters, null);
            if (method == null)
                throw new MissingMethodException(type.FullName, name);
            return method;
        }
    }
}
