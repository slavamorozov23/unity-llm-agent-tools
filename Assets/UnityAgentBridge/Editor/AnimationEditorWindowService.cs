using System;
using System.Linq;
using System.Reflection;
using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;
using UnityObject = UnityEngine.Object;

namespace UnityAgentBridge.Editor
{
    [InitializeOnLoad]
    internal static class AnimationEditorWindowService
    {
        private const double OpenDelaySeconds = 0.75d;
        private const double FocusRepeatDelaySeconds = 0.2d;
        private static string pendingAssetPath;
        private static string openingAnimatorPath;
        private static double openAfter;
        private static bool animatorWindowOpenedByBridge;
        private static int pendingAnimatorPanel;
        private static int pendingLayerIndex = -1;
        private static int pendingParameterIndex = -1;
        private static AnimatorStateMachine pendingStateMachine;
        private static UnityObject pendingGraphSelection;
        private static UnityObject pendingInspectorSelection;
        private static int pendingFocusPasses;
        private static int pendingFocusAttempts;
        private static double focusAfter;

        static AnimationEditorWindowService()
        {
            EditorApplication.update += ProcessPendingOpen;
            AssemblyReloadEvents.beforeAssemblyReload += BeforeAssemblyReload;
        }

        public static void Open(UnityObject asset)
        {
            if (asset == null)
                return;
            ClearAnimatorFocus();
            ScheduleOpen(asset);
        }

        public static void FocusLayers(AnimatorController controller, int layerIndex)
        {
            SetAnimatorFocus(0, layerIndex, -1, null, null, null);
            ScheduleOpen(controller);
        }

        public static void FocusParameters(AnimatorController controller, int parameterIndex)
        {
            SetAnimatorFocus(1, -1, parameterIndex, null, null, null);
            ScheduleOpen(controller);
        }

        public static void FocusGraph(
            AnimatorController controller,
            int layerIndex,
            AnimatorStateMachine stateMachine,
            UnityObject graphSelection,
            UnityObject inspectorSelection)
        {
            SetAnimatorFocus(0, layerIndex, -1, stateMachine, graphSelection, inspectorSelection);
            ScheduleOpen(controller);
        }

        private static void ScheduleOpen(UnityObject asset)
        {
            EditorPresentationService.ShowAsset(asset);
            pendingFocusPasses = 0;
            pendingAssetPath = AssetDatabase.GetAssetPath(asset);
            openAfter = EditorApplication.timeSinceStartup + OpenDelaySeconds;
        }

        internal static void PrepareForAssetMutation()
        {
            pendingAssetPath = null;
            openingAnimatorPath = null;
            pendingFocusPasses = 0;
            ClearAnimatorFocus();
            CloseAnimatorWindows();
            animatorWindowOpenedByBridge = false;
        }

        private static void ProcessPendingOpen()
        {
            if (pendingFocusPasses > 0 && !EditorApplication.isCompiling && !EditorApplication.isUpdating &&
                EditorApplication.timeSinceStartup >= focusAfter)
            {
                try
                {
                    ApplyAnimatorFocus();
                }
                catch (Exception exception)
                {
                    openingAnimatorPath = null;
                    pendingFocusPasses = 0;
                    ClearAnimatorFocus();
                    UnityEngine.Debug.LogWarning("Unity Agent Bridge could not focus the Animator window: " + exception.GetBaseException().Message);
                }
                return;
            }
            if (string.IsNullOrEmpty(pendingAssetPath) || EditorApplication.timeSinceStartup < openAfter ||
                EditorApplication.isCompiling || EditorApplication.isUpdating ||
                EditorApplication.isPlayingOrWillChangePlaymode)
                return;

            var path = pendingAssetPath;
            pendingAssetPath = null;
            var asset = AssetDatabase.LoadAssetAtPath<UnityObject>(path);
            if (asset == null)
                return;
            EditorPresentationService.ShowAsset(asset);
            if (asset is AnimationClip)
                EditorApplication.ExecuteMenuItem("Window/Animation/Animation");
            else if (asset is RuntimeAnimatorController)
            {
                openingAnimatorPath = path;
                AssetDatabase.OpenAsset(asset);
                animatorWindowOpenedByBridge = true;
                pendingFocusPasses = 2;
                focusAfter = EditorApplication.timeSinceStartup + FocusRepeatDelaySeconds;
            }
        }

        private static void BeforeAssemblyReload()
        {
            pendingAssetPath = null;
            openingAnimatorPath = null;
            pendingFocusPasses = 0;
            ClearAnimatorFocus();
            if (animatorWindowOpenedByBridge)
                CloseAnimatorWindows();
            animatorWindowOpenedByBridge = false;
        }

        private static void CloseAnimatorWindows()
        {
            var type = Type.GetType("UnityEditor.Graphs.AnimatorControllerTool, UnityEditor.Graphs", false);
            if (type == null)
                return;
            foreach (var value in Resources.FindObjectsOfTypeAll(type))
            {
                var window = value as EditorWindow;
                if (window != null)
                    window.Close();
            }
        }

        private static void SetAnimatorFocus(
            int panel,
            int layerIndex,
            int parameterIndex,
            AnimatorStateMachine stateMachine,
            UnityObject graphSelection,
            UnityObject inspectorSelection)
        {
            pendingAnimatorPanel = panel;
            pendingLayerIndex = layerIndex;
            pendingParameterIndex = parameterIndex;
            pendingStateMachine = stateMachine;
            pendingGraphSelection = graphSelection;
            pendingInspectorSelection = inspectorSelection;
        }

        private static void ClearAnimatorFocus()
        {
            pendingAnimatorPanel = 0;
            pendingLayerIndex = -1;
            pendingParameterIndex = -1;
            pendingStateMachine = null;
            pendingGraphSelection = null;
            pendingInspectorSelection = null;
            pendingFocusAttempts = 0;
        }

        private static void ApplyAnimatorFocus()
        {
            var controller = AssetDatabase.LoadAssetAtPath<AnimatorController>(openingAnimatorPath);
            if (controller == null)
            {
                openingAnimatorPath = null;
                pendingFocusPasses = 0;
                ClearAnimatorFocus();
                return;
            }
            var windowType = Type.GetType("UnityEditor.Graphs.AnimatorControllerTool, UnityEditor.Graphs", true);
            var controllerProperty = RequiredProperty(windowType, "animatorController");
            var window = Resources.FindObjectsOfTypeAll(windowType)
                .OfType<EditorWindow>()
                .FirstOrDefault(candidate => (UnityObject)controllerProperty.GetValue(candidate, null) == controller);
            if (window == null)
            {
                pendingFocusAttempts++;
                if (pendingFocusAttempts >= 20)
                    throw new InvalidOperationException("Unity did not open the requested Animator Controller.");
                focusAfter = EditorApplication.timeSinceStartup + FocusRepeatDelaySeconds;
                return;
            }

            RequiredProperty(windowType, "currentEditor").SetValue(window, pendingAnimatorPanel, null);
            if (pendingLayerIndex >= 0)
                RequiredProperty(windowType, "selectedLayerIndex").SetValue(window, pendingLayerIndex, null);
            if (pendingStateMachine != null)
            {
                RequiredMethod(windowType, "GoToBreadCrumbTarget", typeof(UnityObject))
                    .Invoke(window, new object[] { pendingStateMachine });
                RequiredMethod(windowType, "RebuildGraph", typeof(bool)).Invoke(window, new object[] { true });
            }
            if (pendingParameterIndex >= 0)
                SelectParameter(windowType, window, pendingParameterIndex);
            window.Focus();
            RequiredMethod(windowType, "RepaintImmediately").Invoke(window, null);
            if (pendingGraphSelection != null && !SelectGraphObject(windowType, window, pendingGraphSelection))
            {
                pendingFocusAttempts++;
                if (pendingFocusAttempts >= 20)
                {
                    openingAnimatorPath = null;
                    ClearAnimatorFocus();
                    throw new InvalidOperationException("Unity Animator graph did not become ready for selection.");
                }
                focusAfter = EditorApplication.timeSinceStartup + FocusRepeatDelaySeconds;
                return;
            }
            if (pendingInspectorSelection != null)
                EditorPresentationService.ShowInspectorObject(pendingInspectorSelection);
            pendingFocusPasses--;
            if (pendingFocusPasses > 0)
            {
                focusAfter = EditorApplication.timeSinceStartup + FocusRepeatDelaySeconds;
                return;
            }
            openingAnimatorPath = null;
            ClearAnimatorFocus();
        }

        private static void SelectParameter(Type windowType, EditorWindow window, int index)
        {
            var parameterEditor = RequiredField(windowType, "m_ParameterEditor").GetValue(window);
            var list = RequiredField(parameterEditor.GetType(), "m_ParameterList").GetValue(parameterEditor);
            RequiredProperty(list.GetType(), "index").SetValue(list, index, null);
        }

        private static bool SelectGraphObject(Type windowType, EditorWindow window, UnityObject target)
        {
            var graph = RequiredField(windowType, "stateMachineGraph").GetValue(window);
            var graphGui = RequiredField(windowType, "stateMachineGraphGUI").GetValue(window);
            if (graph == null || graphGui == null)
                return false;
            var graphProperty = RequiredProperty(graphGui.GetType(), "graph");
            var selectionField = RequiredFieldInHierarchy(graphGui.GetType(), "selection");
            if (graphProperty.GetValue(graphGui, null) != graph || selectionField.GetValue(graphGui) == null)
                return false;
            var findNode = graph.GetType().GetMethod(
                "FindNode",
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
                null,
                new[] { target.GetType() },
                null);
            if (findNode == null)
                throw new MissingMethodException(graph.GetType().FullName, "FindNode(" + target.GetType().Name + ")");
            var node = findNode.Invoke(graph, new object[] { target });
            if (node == null)
                throw new InvalidOperationException("Animator graph does not contain the requested block: " + target.name);
            RequiredMethod(graphGui.GetType(), "ClearSelection").Invoke(graphGui, null);
            var selectNode = graphGui.GetType().GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                .Single(method => method.Name == "SelectNode" && method.GetParameters().Length == 1);
            try
            {
                selectNode.Invoke(graphGui, new[] { node });
            }
            catch (TargetInvocationException exception) when (exception.InnerException is NullReferenceException)
            {
                return false;
            }
            RequiredMethod(windowType, "FrameSelection").Invoke(window, null);
            return true;
        }

        private static FieldInfo RequiredFieldInHierarchy(Type type, string name)
        {
            for (var current = type; current != null; current = current.BaseType)
            {
                var field = current.GetField(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                if (field != null)
                    return field;
            }
            throw new MissingFieldException(type.FullName, name);
        }

        private static FieldInfo RequiredField(Type type, string name)
        {
            var field = type.GetField(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            if (field == null)
                throw new MissingFieldException(type.FullName, name);
            return field;
        }

        private static PropertyInfo RequiredProperty(Type type, string name)
        {
            var property = type.GetProperty(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            if (property == null)
                throw new MissingMemberException(type.FullName, name);
            return property;
        }

        private static MethodInfo RequiredMethod(Type type, string name, params Type[] parameters)
        {
            var method = type.GetMethod(
                name,
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
                null,
                parameters,
                null);
            if (method == null)
                throw new MissingMethodException(type.FullName, name);
            return method;
        }
    }
}
