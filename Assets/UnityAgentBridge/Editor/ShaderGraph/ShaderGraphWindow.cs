using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using UnityEditor;
using UnityEditor.Experimental.GraphView;
using UnityEngine;
using UnityEngine.UIElements;

namespace UnityAgentBridge.Editor
{
    // The graph being edited is open maximized, as a developer would work on it; when the agent turns to anything
    // else the graph is saved and its window closed, so no "Unsaved Changes" dialog can block Unity later.
    [InitializeOnLoad]
    internal static class ShaderGraphWindow
    {
        private const string ActiveKey = "UnityAgentBridge.ShaderGraph.Active";
        private const string WindowType = "UnityEditor.ShaderGraph.Drawing.MaterialGraphEditWindow";

        // An open graph window rebuilds its view after a script reload (entering Play Mode too) without disposing the old
        // one, leaving its node previews (render textures and materials) behind each time. The view is disposed first;
        // the window builds it again on its next update.
        static ShaderGraphWindow()
        {
            AssemblyReloadEvents.beforeAssemblyReload += () =>
            {
                if (Sg.Unsupported != null)
                    return;
                foreach (var window in Resources.FindObjectsOfTypeAll<EditorWindow>().Where(item => item.GetType().FullName == WindowType))
                {
                    ReleaseMainPreview(window);
                    Sg.Set(window, "graphEditorView", null);
                }
            };
            DestroyOrphanPreviews();
        }

        // Right after a reload no graph view exists yet, so any preview still loaded was left behind (by an older reload
        // or a window closed on a deleted graph); its shader recompiles after every reload and logs errors of files long gone.
        private static void DestroyOrphanPreviews()
        {
            var shaders = Resources.FindObjectsOfTypeAll<Shader>()
                .Where(shader => (shader.name.Contains("/hidden/preview/") || shader.name.StartsWith("Shader Graphs/", StringComparison.Ordinal) && shader.name.EndsWith("/Master", StringComparison.Ordinal)) &&
                    (shader.hideFlags & HideFlags.DontSave) != 0 && string.IsNullOrEmpty(AssetDatabase.GetAssetPath(shader))).ToList();
            foreach (var material in Resources.FindObjectsOfTypeAll<Material>().Where(material => shaders.Contains(material.shader)).ToList())
                UnityEngine.Object.DestroyImmediate(material);
            foreach (var shader in shaders)
                UnityEngine.Object.DestroyImmediate(shader);
            foreach (var texture in Resources.FindObjectsOfTypeAll<RenderTexture>()
                .Where(texture => texture.width == 200 && texture.height == 200 && texture.depth == 16 && texture.name.Length == 0 &&
                    texture.hideFlags == HideFlags.HideAndDontSave).ToList())
            {
                texture.Release();
                UnityEngine.Object.DestroyImmediate(texture);
            }
        }

        // Shader Graph disposes node previews with the view but not the Main Preview: its shader, material and texture stay
        // loaded after every close, and the shader keeps compiling the closed graph (a deleted one logs its errors for good).
        private static void ReleaseMainPreview(EditorWindow window)
        {
            var view = Sg.Get(window, "graphEditorView");
            var data = view == null ? null : Sg.Get(Sg.Get(view, "previewManager"), "masterRenderData");
            if (data == null)
                return;
            var shaderData = Sg.Get(data, "shaderData");
            var material = shaderData == null ? null : Sg.Get(shaderData, "mat") as Material;
            var shader = shaderData == null ? null : Sg.Get(shaderData, "shader") as Shader;
            if (material != null)
                UnityEngine.Object.DestroyImmediate(material, true);
            if (shader != null)
            {
                ShaderUtil.ClearShaderMessages(shader);
                UnityEngine.Object.DestroyImmediate(shader, true);
            }
            var texture = Sg.Get(data, "renderTexture") as RenderTexture;
            if (texture != null)
            {
                texture.Release();
                UnityEngine.Object.DestroyImmediate(texture, true);
            }
            if (shaderData != null)
            {
                Sg.Set(shaderData, "mat", null);
                Sg.Set(shaderData, "shader", null);
            }
            Sg.Set(data, "renderTexture", null);
            Sg.Set(data, "texture", null);
        }

        // Commands that only look around (status, logs, eval) keep the graph open.
        private static readonly HashSet<string> Neutral = new HashSet<string>(StringComparer.Ordinal)
        {
            "get-status", "get-logs", "clear-logs", "refresh-assets", "eval-context", "eval-run", "list-menu-items", "search-assets"
        };

        internal static EditorWindow Open(string path)
        {
            if (Sg.Unsupported != null)
                throw new NotSupportedException(Sg.Unsupported);
            var window = Find(path);
            if (window == null)
            {
                AssetDatabase.OpenAsset(AssetDatabase.LoadMainAssetAtPath(path));
                window = Find(path);
            }
            if (window == null)
                throw new InvalidOperationException("Shader Graph window did not open: " + path);
            var active = SessionState.GetString(ActiveKey, string.Empty);
            if (active != path)
            {
                if (active.Length > 0)
                    Leave();
                SessionState.SetString(ActiveKey, path);
            }
            Sync(window);
            // The plugin keeps assets saved: whatever the window recalculated on load is written now, not left as "*".
            Save(window);
            if (!window.maximized)
            {
                window.Focus();
                window.maximized = true;
            }
            return window;
        }

        internal static EditorWindow Find(string path)
        {
            var guid = AssetDatabase.AssetPathToGUID(path);
            return Resources.FindObjectsOfTypeAll(Sg.Type(WindowType)).Cast<EditorWindow>()
                .FirstOrDefault(window => (string)Sg.Get(window, "selectedGuid") == guid);
        }

        internal static void OnCommand(BridgeRequest request)
        {
            var active = SessionState.GetString(ActiveKey, string.Empty);
            if (active.Length == 0 || Neutral.Contains(request.command))
                return;
            var path = request.path ?? string.Empty;
            var separator = path.IndexOf('#');
            if ((separator < 0 ? path : path.Substring(0, separator)) == active)
                return;
            Leave();
        }

        internal static void Leave()
        {
            var path = SessionState.GetString(ActiveKey, string.Empty);
            SessionState.EraseString(ActiveKey);
            var window = path.Length == 0 ? null : Find(path);
            if (window == null)
                return;
            Save(window);
            // A Custom Function reloaded from its changed .hlsl can still mark the window; closing it then asks to save.
            if (window.hasUnsavedChanges)
                Sg.Call(window, "SaveAsset");
            ReleaseMainPreview(window);
            window.maximized = false;
            window.Close();
        }

        internal static void Save(EditorWindow window)
        {
            // The window finishes a change (stack, material hash) in its next Update; saving before that leaves it dirty.
            // hasUnsavedChanges follows only changes that mark the graph dirty, so moved nodes are compared with the saved file.
            Sync(window);
            if ((bool)Sg.Call(window, "GraphHasChangedSinceLastSerialization"))
                Sg.Call(window, "SaveAsset");
        }

        // Changes not saved yet are dropped and the graph is read from its file again, as the window's "Discard Changes And
        // Reload" does; a graph rebuilt from Undo would be saved with what the window drops on rebuilding (inactive blocks).
        internal static void Reload(EditorWindow window)
        {
            Sg.Set(window, "graphObject", null);
            Sync(window);
        }

        // Node views write their laid-out size into the graph a few frames after a change; that is saved too.
        internal static void SaveAfterLayout(EditorWindow window)
        {
            EditorPresentationService.NextUpdate(() => EditorPresentationService.NextUpdate(() => EditorPresentationService.NextUpdate(() =>
            {
                if (window != null && Sg.Get(window, "graphObject") != null)
                    Save(window);
            })));
        }

        // The window applies graph changes to its views in Update; running it now keeps the views in step with the data.
        internal static void Sync(EditorWindow window)
        {
            Sg.Call(window, "Update");
        }

        internal static object GraphObject(EditorWindow window)
        {
            return Sg.Get(window, "graphObject");
        }

        internal static object Graph(EditorWindow window)
        {
            return Sg.Get(GraphObject(window), "graph");
        }

        internal static object EditorView(EditorWindow window)
        {
            var view = Sg.Get(window, "graphEditorView");
            if (view == null)
                throw new InvalidOperationException("Shader Graph view is not ready.");
            return view;
        }

        internal static GraphView GraphView(EditorWindow window)
        {
            return (GraphView)Sg.Get(EditorView(window), "graphView");
        }

        // Selects the nodes or group and frames them, like pressing F after clicking them.
        internal static void Frame(EditorWindow window, IEnumerable<object> nodes, object group = null)
        {
            Sync(window);
            var graphView = GraphView(window);
            // A new group opens its title for typing, as after Group Selection by hand; the name was given with the command.
            var focused = graphView.panel == null ? null : graphView.panel.focusController.focusedElement as VisualElement;
            if (focused != null && graphView.Contains(focused))
                focused.Blur();
            var targets = new HashSet<object>(nodes);
            graphView.ClearSelection();
            if (group != null)
                targets.Add(group);
            // Node views carry their node, the master stacks their context; groups and sticky notes carry their data as userData.
            foreach (var element in graphView.graphElements.ToList())
                if (targets.Contains(element is Node && Sg.Has(element, "node") ? Sg.Get(element, "node") : Sg.Has(element, "m_ContextData") ? Sg.Get(element, "m_ContextData") : element.userData))
                    graphView.AddToSelection(element);
            if (graphView.selection.Count == 0)
                return;
            FrameWhenLaidOut(graphView);
        }

        // New and moved nodes, a group resizing to its members and the window being maximized all settle over a few
        // frames; the view is framed once the selection's bounds stop changing, so the whole selection fits.
        private static EditorApplication.CallbackFunction pendingFrame;

        private static void FrameWhenLaidOut(GraphView graphView)
        {
            // A newer framing replaces one still waiting.
            if (pendingFrame != null)
                EditorApplication.update -= pendingFrame;
            // Framed at once, so back-to-back commands each end on their result even while Unity is in the background.
            if (graphView.panel != null)
                Sg.Call(graphView.panel, "ValidateLayout");
            var previous = FrameSelection(graphView);
            var frames = 0;
            EditorApplication.CallbackFunction tick = null;
            tick = () =>
            {
                frames++;
                if (graphView.panel == null || graphView.selection.Count == 0)
                {
                    EditorApplication.update -= tick;
                    return;
                }
                var bounds = SelectionBounds(graphView);
                var settled = bounds != null && frames >= 3 && bounds == previous;
                previous = bounds;
                if (!settled && frames < 60)
                    return;
                EditorApplication.update -= tick;
                FrameSelection(graphView);
            };
            pendingFrame = tick;
            EditorApplication.update += tick;
        }

        private static Rect? SelectionBounds(GraphView graphView)
        {
            var rects = graphView.selection.OfType<GraphElement>().Select(element => element.GetPosition()).ToList();
            if (rects.Count == 0 || graphView.layout.width <= 0f || rects.Any(rect => float.IsNaN(rect.width) || rect.width <= 0f))
                return null;
            return rects.Aggregate((a, b) => Rect.MinMaxRect(Mathf.Min(a.xMin, b.xMin), Mathf.Min(a.yMin, b.yMin), Mathf.Max(a.xMax, b.xMax), Mathf.Max(a.yMax, b.yMax)));
        }

        private static Rect? FrameSelection(GraphView graphView)
        {
            var bounds = SelectionBounds(graphView);
            if (bounds == null)
                return null;
            Vector3 translation;
            Vector3 scaling;
            UnityEditor.Experimental.GraphView.GraphView.CalculateFrameTransform(bounds.Value, graphView.layout, 40, out translation, out scaling);
            graphView.UpdateViewTransform(translation, scaling);
            return bounds;
        }

        // Scrolls the Blackboard to the property and unfolds its category, as clicking it would.
        internal static void ShowInBlackboard(EditorWindow window, object input)
        {
            Sync(window);
            var controller = Sg.Get(EditorView(window), "blackboardController");
            var blackboard = controller == null ? null : Sg.Get(controller, "blackboard") as VisualElement;
            if (blackboard == null)
                return;
            var field = blackboard.Query<GraphElement>().ToList().FirstOrDefault(element =>
                element.GetType().Name == "SGBlackboardField" && Sg.Get(Sg.Get(element, "ViewModel"), "model") == input);
            if (field == null)
                return;
            for (var parent = field.parent; parent != null; parent = parent.parent)
                if (parent.GetType().Name == "SGBlackboardCategory")
                    Sg.Call(parent, "TryDoFoldout", true);
            var graphView = GraphView(window);
            graphView.ClearSelection();
            graphView.AddToSelection(field);
            EditorPresentationService.NextUpdate(() => EditorPresentationService.NextUpdate(() =>
            {
                var scroll = field.GetFirstAncestorOfType<ScrollView>();
                if (scroll != null && field.panel != null)
                    scroll.ScrollTo(field);
            }));
        }

        // Preview shaders compile asynchronously; their errors reach the node badges only when compiled.
        // False when the previews were still compiling at the deadline: their errors may belong to the previous graph.
        internal static bool WaitForPreviews(EditorWindow window, double seconds)
        {
            var manager = Sg.Get(EditorView(window), "previewManager");
            var until = EditorApplication.timeSinceStartup + seconds;
            var settled = false;
            while (true)
            {
                RenderPreviews(window, manager);
                var compiling = Sg.Items(Sg.Get(manager, "m_PreviewsCompiling")).Count();
                var pending = Sg.Items(Sg.Get(manager, "m_PreviewsNeedsRecompile")).Count(preview =>
                {
                    var node = Sg.Get(Sg.Get(preview, "shaderData"), "node");
                    return node == null || (bool)Sg.Get(node, "hasPreview") && (bool)Sg.Get(node, "previewExpanded");
                });
                settled = compiling == 0 && pending == 0;
                if (settled || EditorApplication.timeSinceStartup > until)
                    break;
                Thread.Sleep(30);
            }
            Sync(window);
            return settled;
        }

        // The graph compiles two previews at a time, so the requested ones are waited for by their own images.
        internal static void WaitForPreviews(EditorWindow window, IEnumerable<object> nodes, double seconds)
        {
            var manager = Sg.Get(EditorView(window), "previewManager");
            var previews = nodes.Select(node => Sg.Call(manager, "GetPreviewRenderData", node)).Where(item => item != null).ToList();
            var until = EditorApplication.timeSinceStartup + seconds;
            while (EditorApplication.timeSinceStartup < until)
            {
                RenderPreviews(window, manager);
                var compiling = Sg.Items(Sg.Get(manager, "m_PreviewsCompiling")).ToList();
                var waiting = Sg.Items(Sg.Get(manager, "m_PreviewsNeedsRecompile")).ToList();
                if (previews.All(preview => !compiling.Contains(preview) && !waiting.Contains(preview) && Sg.Get(preview, "texture") != null))
                    break;
                Thread.Sleep(30);
            }
            Sync(window);
        }

        // Async compiles finish only while the editor loop runs, so the queued passes are finished here.
        private static void RenderPreviews(EditorWindow window, object manager)
        {
            Sg.Call(manager, "RenderPreviews", window, true);
            foreach (var preview in Sg.Items(Sg.Get(manager, "m_PreviewsCompiling")).ToList())
            {
                var material = Sg.Get(Sg.Get(preview, "shaderData"), "mat") as Material;
                for (var pass = 0; material != null && pass < material.passCount; pass++)
                    if (!ShaderUtil.IsPassCompiled(material, pass))
                        ShaderUtil.CompilePass(material, pass, true);
            }
        }

        // The toolbar's Main Preview toggle, for a template that hides it.
        internal static void ShowMainPreview(EditorWindow window)
        {
            var view = EditorView(window);
            var settings = Sg.Get(view, "m_UserViewSettings");
            if ((bool)Sg.Get(settings, "isPreviewVisible"))
                return;
            Sg.Set(settings, "isPreviewVisible", true);
            Sg.Call(view, "UserViewSettingsChangeCheck", Sg.Get(Sg.Get(view, "m_ColorManager"), "activeIndex"));
            Sync(window);
        }

        // Each node preview as a PNG, the same image the node shows.
        internal static string CapturePreview(EditorWindow window, object node, int index)
        {
            var manager = Sg.Get(EditorView(window), "previewManager");
            var data = Sg.Call(manager, "GetPreviewRenderData", node);
            var texture = data == null ? null : Sg.Get(data, "texture") as Texture;
            if (texture == null)
                return null;
            const int size = 256;
            var target = RenderTexture.GetTemporary(size, size, 0, RenderTextureFormat.ARGB32, RenderTextureReadWrite.sRGB);
            var previous = RenderTexture.active;
            var image = new Texture2D(size, size, TextureFormat.RGBA32, false);
            try
            {
                Graphics.Blit(texture, target);
                RenderTexture.active = target;
                image.ReadPixels(new Rect(0, 0, size, size), 0, 0);
                image.Apply();
                var directory = Path.Combine(BridgePaths.RuntimeRoot, "Screenshots");
                Directory.CreateDirectory(directory);
                var file = Path.Combine(directory, "shadergraph-" + index + ".png");
                File.WriteAllBytes(file, image.EncodeToPNG());
                return file;
            }
            finally
            {
                RenderTexture.active = previous;
                RenderTexture.ReleaseTemporary(target);
                UnityEngine.Object.DestroyImmediate(image);
            }
        }
    }
}
