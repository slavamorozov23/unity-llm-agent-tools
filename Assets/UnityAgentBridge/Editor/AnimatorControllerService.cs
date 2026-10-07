using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.Animations;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace UnityAgentBridge.Editor
{
    internal static class AnimatorControllerService
    {
        [Serializable]
        private sealed class AnimatorList { public AnimatorData[] animators = Array.Empty<AnimatorData>(); }

        [Serializable]
        private sealed class AnimatorData
        {
            public string path;
            public bool enabled;
            public string controller;
            public string avatar;
        }

        [Serializable]
        private sealed class ControllerData
        {
            public string name;
            public string path;
            public LayerData[] layers = Array.Empty<LayerData>();
            public ParameterData[] parameters = Array.Empty<ParameterData>();
        }

        [Serializable]
        private sealed class ControllerIdentity
        {
            public string name;
            public string path;
        }

        [Serializable]
        private sealed class LayerData
        {
            public int index;
            public string name;
            public float weight;
            public string avatarMask;
            public string blendingMode;
            public bool iKPass;
            public int syncedLayerIndex;
            public bool syncedLayerAffectsTiming;
            public StateMachineData stateMachine;
        }

        [Serializable]
        private sealed class StateMachineData
        {
            public string name;
            public string path;
            public string defaultState;
            public StateData[] states = Array.Empty<StateData>();
            public StateMachineData[] stateMachines = Array.Empty<StateMachineData>();
            public TransitionData[] anyStateTransitions = Array.Empty<TransitionData>();
            public TransitionData[] entryTransitions = Array.Empty<TransitionData>();
            public TransitionData[] stateMachineTransitions = Array.Empty<TransitionData>();
        }

        [Serializable]
        private sealed class StateData
        {
            public string name;
            public string path;
            public float speed;
            public string speedParameter;
            public string timeParameter;
            public bool mirror;
            public string mirrorParameter;
            public float cycleOffset;
            public string cycleOffsetParameter;
            public string tag;
            public bool writeDefaultValues;
            public MotionData motion;
            public TransitionData[] transitions = Array.Empty<TransitionData>();
        }

        [Serializable]
        private sealed class MotionData
        {
            public string kind;
            public string name;
            public string path;
            public string blendType;
            public string blendParameter;
            public string blendParameterY;
            public bool useAutomaticThresholds;
            public float minThreshold;
            public float maxThreshold;
            public ChildMotionData[] children = Array.Empty<ChildMotionData>();
        }

        [Serializable]
        private sealed class MotionStateData
        {
            public string layer;
            public string state;
            public string path;
            public MotionData motion;
        }

        [Serializable]
        private sealed class MotionStateCollection { public MotionStateData[] states = Array.Empty<MotionStateData>(); }

        [Serializable]
        private sealed class ChildMotionData
        {
            public string path;
            public string parent;
            public string kind;
            public string name;
            public string assetPath;
            public string blendType;
            public string blendParameter;
            public string blendParameterY;
            public bool useAutomaticThresholds;
            public float minThreshold;
            public float maxThreshold;
            public float threshold;
            public Vector2 position;
            public float timeScale;
            public float cycleOffset;
            public string directBlendParameter;
            public bool mirror;
        }

        [Serializable]
        private sealed class TransitionData
        {
            public int index;
            public string from;
            public string to;
            public bool isExit;
            public bool hasExitTime;
            public float exitTime;
            public float duration;
            public float offset;
            public bool hasFixedDuration;
            public string interruptionSource;
            public bool orderedInterruption;
            public bool canTransitionToSelf;
            public bool mute;
            public bool solo;
            public ConditionData[] conditions = Array.Empty<ConditionData>();
        }

        [Serializable]
        private sealed class ConditionData
        {
            public string mode;
            public string parameter;
            public float threshold;
        }

        [Serializable]
        private sealed class ParameterData
        {
            public string name;
            public string type;
            public float floatValue;
            public int intValue;
            public bool boolValue;
        }

        [Serializable]
        private sealed class ConditionList { public ConditionData[] conditions = Array.Empty<ConditionData>(); }

        [Serializable]
        private sealed class BlendTreeSettings
        {
            public string blendType;
            public string blendParameter;
            public string blendParameterY;
            public bool useAutomaticThresholds;
            public float minThreshold;
            public float maxThreshold;
            public BlendTreeChildInput[] children = Array.Empty<BlendTreeChildInput>();
        }

        [Serializable]
        private sealed class BlendTreeChildInput
        {
            public string motion;
            public float threshold;
            public float x;
            public float y;
            public float timeScale = 1f;
            public float cycleOffset;
            public string directBlendParameter;
            public bool mirror;
        }

        [Serializable]
        private sealed class StringValue { public string value; }

        public static string ListAnimators()
        {
            var result = new List<AnimatorData>();
            foreach (var scene in ScenePath.ContextScenes())
                foreach (var root in scene.GetRootGameObjects())
                    result.AddRange(root.GetComponentsInChildren<Animator>(true).Select(DescribeAnimator));
            return JsonUtility.ToJson(new AnimatorList
            {
                animators = result.OrderBy(item => item.path, StringComparer.Ordinal).ToArray()
            });
        }

        public static string GetAnimator(string scenePath)
        {
            return JsonUtility.ToJson(DescribeAnimator(ResolveAnimator(scenePath)));
        }

        public static string MutateAnimator(BridgeRequest request)
        {
            EnsureEditMode();
            var gameObject = ScenePath.ResolveObject(request.path);
            var action = (request.action ?? string.Empty).ToLowerInvariant();
            if (action == "create")
            {
                var animator = gameObject.GetComponent<Animator>();
                if (animator == null)
                    animator = Undo.AddComponent<Animator>(gameObject);
                if (!string.IsNullOrWhiteSpace(request.controller))
                {
                    Undo.RecordObject(animator, "Unity Agent Bridge: Assign Animator Controller");
                    animator.runtimeAnimatorController = ResolveRuntimeController(request.controller);
                }
                MarkSceneObject(animator);
                EditorPresentationService.ShowComponent(animator);
                return JsonUtility.ToJson(DescribeAnimator(animator));
            }
            if (action == "delete")
            {
                var animator = ResolveAnimator(request.path);
                var result = DescribeAnimator(animator);
                Undo.DestroyObjectImmediate(animator);
                ScenePersistenceService.MarkDirty(gameObject.scene);
                return JsonUtility.ToJson(result);
            }
            throw new ArgumentException("Animator action must be create or delete.", "action");
        }

        public static string AssignController(BridgeRequest request)
        {
            EnsureEditMode();
            var animator = ResolveAnimator(request.path);
            Undo.RecordObject(animator, "Unity Agent Bridge: Assign Animator Controller");
            var action = (request.action ?? string.Empty).ToLowerInvariant();
            if (action == "detach" || action == "delete")
                animator.runtimeAnimatorController = null;
            else if (action == "assign" || action == "modify" || action == "set")
                animator.runtimeAnimatorController = ResolveRuntimeController(request.controller);
            else
                throw new ArgumentException("Animator Controller action must be assign or detach.", "action");
            MarkSceneObject(animator);
            return JsonUtility.ToJson(DescribeAnimator(animator));
        }

        public static string GetController(BridgeRequest request)
        {
            var controller = ResolveController(request.controller, request.path);
            AnimationEditorWindowService.Open(controller);
            return JsonUtility.ToJson(DescribeController(controller));
        }

        public static string GetMotions(BridgeRequest request)
        {
            var controller = ResolveController(request.controller, request.path);
            AnimationEditorWindowService.Open(controller);
            var motions = new List<MotionStateData>();
            foreach (var layer in controller.layers)
                CollectMotions(layer.stateMachine, layer.name, string.Empty, motions);
            return JsonUtility.ToJson(new MotionStateCollection { states = motions.ToArray() });
        }

        public static string MutateLayer(BridgeRequest request)
        {
            EnsureEditMode();
            var controller = ResolveController(request.controller, null);
            var action = (request.action ?? string.Empty).ToLowerInvariant();
            var layers = controller.layers.ToList();
            var focusIndex = -1;
            if (action == "create")
            {
                EnsureUnique(layers.Select(item => item.name), request.layer, "Layer");
                controller.AddLayer(request.layer);
                layers = controller.layers.ToList();
                ApplyLayerValues(layers[layers.Count - 1], request.values);
                controller.layers = layers.ToArray();
                focusIndex = layers.Count - 1;
            }
            else
            {
                var index = UniqueIndex(layers.Select(item => item.name), request.layer, "Layer");
                if (action == "delete")
                {
                    if (layers.Count == 1)
                        throw new InvalidOperationException("Animator Controller must keep at least one Layer.");
                    controller.RemoveLayer(index);
                    focusIndex = Math.Min(index, controller.layers.Length - 1);
                }
                else if (action == "modify")
                {
                    ApplyLayerValues(layers[index], request.values);
                    controller.layers = layers.ToArray();
                    focusIndex = index;
                }
                else
                    throw new ArgumentException("Layer action must be create, modify, or delete.", "action");
            }
            SaveController(controller);
            AnimationEditorWindowService.FocusLayers(controller, focusIndex);
            return JsonUtility.ToJson(DescribeControllerIdentity(controller));
        }

        public static string MutateParameter(BridgeRequest request)
        {
            EnsureEditMode();
            var controller = ResolveController(request.controller, null);
            var action = (request.action ?? string.Empty).ToLowerInvariant();
            var focusIndex = -1;
            if (action == "create")
            {
                EnsureUnique(controller.parameters.Select(item => item.name), request.name, "Parameter");
                var parameter = new AnimatorControllerParameter
                {
                    name = request.name,
                    type = ParseEnum<AnimatorControllerParameterType>(request.parameterType, "parameterType")
                };
                SetParameterDefault(parameter, request.value);
                controller.AddParameter(parameter);
                focusIndex = controller.parameters.Length - 1;
            }
            else
            {
                var parameters = controller.parameters;
                var index = UniqueIndex(parameters.Select(item => item.name), request.name, "Parameter");
                if (action == "delete")
                {
                    controller.RemoveParameter(index);
                    focusIndex = Math.Min(index, controller.parameters.Length - 1);
                }
                else if (action == "modify")
                {
                    if (!string.IsNullOrWhiteSpace(request.destinationPath))
                        parameters[index].name = request.destinationPath;
                    if (!string.IsNullOrWhiteSpace(request.parameterType))
                        parameters[index].type = ParseEnum<AnimatorControllerParameterType>(request.parameterType, "parameterType");
                    if (request.value != null)
                        SetParameterDefault(parameters[index], request.value);
                    controller.parameters = parameters;
                    focusIndex = index;
                }
                else
                    throw new ArgumentException("Parameter action must be create, modify, or delete.", "action");
            }
            SaveController(controller);
            AnimationEditorWindowService.FocusParameters(controller, focusIndex);
            return JsonUtility.ToJson(DescribeControllerIdentity(controller));
        }

        public static string MutateStateMachine(BridgeRequest request)
        {
            EnsureEditMode();
            var controller = ResolveController(request.controller, null);
            var layer = ResolveLayer(controller, request.layer);
            var layerIndex = LayerIndex(controller, request.layer);
            var parent = ResolveStateMachine(layer.stateMachine, request.objectPath);
            var action = (request.action ?? string.Empty).ToLowerInvariant();
            AnimatorStateMachine focusMachine;
            if (action == "create")
            {
                EnsureUnique(parent.stateMachines.Select(item => item.stateMachine.name), request.stateMachine, "State Machine");
                focusMachine = parent.AddStateMachine(request.stateMachine);
            }
            else
            {
                var child = ResolveDirectStateMachine(parent, request.stateMachine);
                if (action == "delete")
                {
                    parent.RemoveStateMachine(child);
                    focusMachine = parent;
                }
                else if (action == "modify")
                {
                    child.name = Required(request.name, "name");
                    focusMachine = child;
                }
                else
                    throw new ArgumentException("State Machine action must be create, modify, or delete.", "action");
            }
            SaveController(controller);
            AnimationEditorWindowService.FocusGraph(controller, layerIndex, parent,
                focusMachine == parent ? null : focusMachine, focusMachine);
            return JsonUtility.ToJson(DescribeControllerIdentity(controller));
        }

        public static string MutateState(BridgeRequest request)
        {
            EnsureEditMode();
            var controller = ResolveController(request.controller, null);
            var layer = ResolveLayer(controller, request.layer);
            var layerIndex = LayerIndex(controller, request.layer);
            var machine = ResolveStateMachine(layer.stateMachine, request.stateMachine);
            var action = (request.action ?? string.Empty).ToLowerInvariant();
            AnimatorState focusState = null;
            if (action == "create")
            {
                EnsureUnique(machine.states.Select(item => item.state.name), request.state, "State");
                var state = machine.AddState(request.state);
                if (!string.IsNullOrWhiteSpace(request.motion))
                    state.motion = ResolveMotion(controller, request.motion);
                ApplyStateValues(state, request.values);
                focusState = state;
            }
            else
            {
                var state = ResolveDirectState(machine, request.state);
                if (action == "delete")
                    machine.RemoveState(state);
                else if (action == "modify")
                {
                    if (!string.IsNullOrWhiteSpace(request.motion))
                        state.motion = ResolveMotion(controller, request.motion);
                    ApplyStateValues(state, request.values);
                    focusState = state;
                }
                else
                    throw new ArgumentException("State action must be create, modify, or delete.", "action");
            }
            SaveController(controller);
            AnimationEditorWindowService.FocusGraph(controller, layerIndex, machine, focusState, focusState);
            return JsonUtility.ToJson(DescribeControllerIdentity(controller));
        }

        public static string AssignStateMotion(BridgeRequest request)
        {
            EnsureEditMode();
            var controller = ResolveController(request.controller, null);
            var layer = ResolveLayer(controller, request.layer);
            var layerIndex = LayerIndex(controller, request.layer);
            var machine = ResolveStateMachine(layer.stateMachine, request.stateMachine);
            var state = ResolveDirectState(machine, request.state);
            var action = (request.action ?? string.Empty).ToLowerInvariant();
            if (action == "detach" || action == "delete")
                state.motion = null;
            else if (action == "assign" || action == "modify" || action == "set")
                state.motion = ResolveMotion(controller, request.motion);
            else
                throw new ArgumentException("State motion action must be assign or detach.", "action");
            SaveController(controller);
            AnimationEditorWindowService.FocusGraph(controller, layerIndex, machine, state, state);
            return JsonUtility.ToJson(DescribeControllerIdentity(controller));
        }

        public static string MutateTransition(BridgeRequest request)
        {
            EnsureEditMode();
            var controller = ResolveController(request.controller, null);
            var layer = ResolveLayer(controller, request.layer);
            var layerIndex = LayerIndex(controller, request.layer);
            var machine = ResolveStateMachine(layer.stateMachine, request.stateMachine);
            var action = (request.action ?? string.Empty).ToLowerInvariant();
            AnimatorStateTransition transition;
            var exits = string.Equals(request.toState, "Exit", StringComparison.OrdinalIgnoreCase);
            var destinationMachine = exits ? null : StateMachineForStateReference(layer.stateMachine, request.toState, machine);
            var destinationState = exits ? null : ResolveStateReference(layer.stateMachine, request.toState, machine);
            var sourceMachine = IsAnyState(request.fromState) ? machine : StateMachineForStateReference(layer.stateMachine, request.fromState, machine);
            var sourceState = IsAnyState(request.fromState) ? null : ResolveStateReference(layer.stateMachine, request.fromState, machine);
            if (action == "create")
            {
                if (IsAnyState(request.fromState))
                {
                    if (destinationState == null)
                        throw new InvalidOperationException("Any State cannot transition to Exit.");
                    transition = machine.AddAnyStateTransition(destinationState);
                }
                else
                {
                    transition = destinationState == null ? sourceState.AddExitTransition() : sourceState.AddTransition(destinationState);
                }
            }
            else
            {
                transition = ResolveTransition(layer.stateMachine, machine, request.fromState, request.toState, request.componentIndex);
                if (action == "delete")
                {
                    if (IsAnyState(request.fromState))
                        machine.RemoveAnyStateTransition(transition);
                    else
                        sourceState.RemoveTransition(transition);
                    SaveController(controller);
                    var deleteFocus = destinationState ?? sourceState;
                    AnimationEditorWindowService.FocusGraph(controller, layerIndex,
                        destinationMachine ?? sourceMachine, deleteFocus, deleteFocus);
                    return JsonUtility.ToJson(DescribeControllerIdentity(controller));
                }
                if (action != "modify")
                    throw new ArgumentException("Transition action must be create, modify, or delete.", "action");
            }
            try
            {
                ApplyTransitionValues(transition, request.values);
                ApplyConditions(transition, request.json);
            }
            catch
            {
                if (action == "create")
                    RemoveTransition(machine, sourceState, request.fromState, transition);
                throw;
            }
            SaveController(controller);
            var focusState = destinationState ?? sourceState;
            AnimationEditorWindowService.FocusGraph(controller, layerIndex, destinationMachine ?? sourceMachine, focusState,
                action == "create" ? (UnityEngine.Object)focusState : transition);
            return JsonUtility.ToJson(DescribeControllerIdentity(controller));
        }

        public static string MutateBlendTree(BridgeRequest request)
        {
            EnsureEditMode();
            var controller = ResolveController(request.controller, null);
            var layer = ResolveLayer(controller, request.layer);
            var layerIndex = LayerIndex(controller, request.layer);
            var machine = ResolveStateMachine(layer.stateMachine, request.stateMachine);
            var state = ResolveDirectState(machine, request.state);
            var action = (request.action ?? string.Empty).ToLowerInvariant();
            BlendTree tree;
            if (action == "create")
            {
                if (state.motion != null)
                    throw new InvalidOperationException("State already has a motion; detach it before creating a BlendTree.");
                tree = new BlendTree { name = request.name };
                AssetDatabase.AddObjectToAsset(tree, controller);
                state.motion = tree;
            }
            else
            {
                tree = state.motion as BlendTree;
                if (tree == null || (!string.IsNullOrWhiteSpace(request.name) && !string.Equals(tree.name, request.name, StringComparison.Ordinal)))
                    throw new InvalidOperationException("State does not use the requested BlendTree.");
                if (action == "delete")
                {
                    state.motion = null;
                    UnityEngine.Object.DestroyImmediate(tree, true);
                    SaveController(controller);
                    AnimationEditorWindowService.FocusGraph(controller, layerIndex, machine, state, state);
                    return JsonUtility.ToJson(DescribeControllerIdentity(controller));
                }
                if (action != "modify")
                    throw new ArgumentException("BlendTree action must be create, modify, or delete.", "action");
            }
            ApplyBlendTree(controller, tree, request.json);
            EditorUtility.SetDirty(tree);
            SaveController(controller);
            AnimationEditorWindowService.FocusGraph(controller, layerIndex, machine, state, state);
            return JsonUtility.ToJson(DescribeControllerIdentity(controller));
        }

        internal static Animator ResolveAnimator(string scenePath)
        {
            var gameObject = ScenePath.ResolveObject(scenePath);
            var animator = gameObject.GetComponent<Animator>();
            if (animator == null)
                throw new InvalidOperationException("Object has no Animator: " + scenePath);
            EditorPresentationService.ShowComponent(animator);
            return animator;
        }

        internal static AnimatorController ResolveController(string controllerPath, string animatorPath)
        {
            if (!string.IsNullOrWhiteSpace(controllerPath))
            {
                var direct = AssetDatabase.LoadAssetAtPath<AnimatorController>(controllerPath);
                if (direct == null)
                    throw new InvalidOperationException("Animator Controller was not found: " + controllerPath);
                return direct;
            }
            var runtime = ResolveAnimator(animatorPath).runtimeAnimatorController;
            var overrideController = runtime as AnimatorOverrideController;
            if (overrideController != null)
                runtime = overrideController.runtimeAnimatorController;
            var controller = runtime as AnimatorController;
            if (controller == null)
                throw new InvalidOperationException("Animator has no editable Animator Controller.");
            return controller;
        }

        internal static IEnumerable<(string layer, string fullPath, AnimatorState state)> EnumerateStates(AnimatorController controller)
        {
            foreach (var layer in controller.layers)
                foreach (var value in EnumerateStates(layer.stateMachine, layer.name, string.Empty))
                    yield return (layer.name, value.fullPath, value.state);
        }

        private static IEnumerable<(string fullPath, AnimatorState state)> EnumerateStates(AnimatorStateMachine machine, string layer, string machinePath)
        {
            var prefix = string.IsNullOrEmpty(machinePath) ? layer : layer + "." + machinePath.Replace('/', '.');
            foreach (var child in machine.states)
                yield return (prefix + "." + child.state.name, child.state);
            foreach (var child in machine.stateMachines)
            {
                var childPath = string.IsNullOrEmpty(machinePath) ? child.stateMachine.name : machinePath + "/" + child.stateMachine.name;
                foreach (var value in EnumerateStates(child.stateMachine, layer, childPath))
                    yield return value;
            }
        }

        private static AnimatorData DescribeAnimator(Animator animator)
        {
            return new AnimatorData
            {
                path = ScenePath.For(animator.gameObject),
                enabled = animator.enabled,
                controller = animator.runtimeAnimatorController == null ? string.Empty : AssetDatabase.GetAssetPath(animator.runtimeAnimatorController),
                avatar = animator.avatar == null ? string.Empty : AssetDatabase.GetAssetPath(animator.avatar)
            };
        }

        private static ControllerData DescribeController(AnimatorController controller)
        {
            return new ControllerData
            {
                name = controller.name,
                path = AssetDatabase.GetAssetPath(controller),
                layers = controller.layers.Select((layer, index) => new LayerData
                {
                    index = index,
                    name = layer.name,
                    weight = layer.defaultWeight,
                    avatarMask = layer.avatarMask == null ? string.Empty : AssetDatabase.GetAssetPath(layer.avatarMask),
                    blendingMode = layer.blendingMode.ToString(),
                    iKPass = layer.iKPass,
                    syncedLayerIndex = layer.syncedLayerIndex,
                    syncedLayerAffectsTiming = layer.syncedLayerAffectsTiming,
                    stateMachine = DescribeStateMachine(layer.stateMachine, layer.name, string.Empty)
                }).ToArray(),
                parameters = controller.parameters.Select(parameter => new ParameterData
                {
                    name = parameter.name,
                    type = parameter.type.ToString(),
                    floatValue = parameter.defaultFloat,
                    intValue = parameter.defaultInt,
                    boolValue = parameter.defaultBool
                }).ToArray()
            };
        }

        private static ControllerIdentity DescribeControllerIdentity(AnimatorController controller)
        {
            return new ControllerIdentity { name = controller.name, path = AssetDatabase.GetAssetPath(controller) };
        }

        private static StateMachineData DescribeStateMachine(AnimatorStateMachine machine, string layer, string parentPath)
        {
            var path = string.IsNullOrEmpty(parentPath) ? machine.name : parentPath + "/" + machine.name;
            return new StateMachineData
            {
                name = machine.name,
                path = path,
                defaultState = machine.defaultState == null ? string.Empty : machine.defaultState.name,
                states = machine.states.Select(child => DescribeState(child.state, path + "/" + child.state.name)).ToArray(),
                stateMachines = machine.stateMachines.Select(child => DescribeStateMachine(child.stateMachine, layer, path)).ToArray(),
                anyStateTransitions = machine.anyStateTransitions.Select((item, index) => DescribeTransition(item, index, "Any State")).ToArray(),
                entryTransitions = machine.entryTransitions.Select((item, index) => DescribeTransition(item, index, "Entry")).ToArray(),
                stateMachineTransitions = machine.stateMachines.SelectMany(child => machine.GetStateMachineTransitions(child.stateMachine)
                    .Select((item, index) => DescribeTransition(item, index, child.stateMachine.name))).ToArray()
            };
        }

        private static StateData DescribeState(AnimatorState state, string path)
        {
            return new StateData
            {
                name = state.name,
                path = path,
                speed = state.speed,
                speedParameter = state.speedParameter,
                timeParameter = state.timeParameter,
                mirror = state.mirror,
                mirrorParameter = state.mirrorParameter,
                cycleOffset = state.cycleOffset,
                cycleOffsetParameter = state.cycleOffsetParameter,
                tag = state.tag,
                writeDefaultValues = state.writeDefaultValues,
                motion = DescribeMotion(state.motion),
                transitions = state.transitions.Select((item, index) => DescribeTransition(item, index, state.name)).ToArray()
            };
        }

        private static MotionData DescribeMotion(Motion motion)
        {
            if (motion == null)
                return null;
            var tree = motion as BlendTree;
            if (tree == null)
                return new MotionData { kind = "AnimationClip", name = motion.name, path = AssetDatabase.GetAssetPath(motion) };
            var result = new MotionData
            {
                kind = "BlendTree",
                name = tree.name,
                path = AssetDatabase.GetAssetPath(tree),
                blendType = tree.blendType.ToString(),
                blendParameter = tree.blendParameter,
                blendParameterY = tree.blendParameterY,
                useAutomaticThresholds = tree.useAutomaticThresholds,
                minThreshold = tree.minThreshold,
                maxThreshold = tree.maxThreshold
            };
            var children = new List<ChildMotionData>();
            AddMotionChildren(tree, string.Empty, children);
            result.children = children.ToArray();
            return result;
        }

        private static void AddMotionChildren(BlendTree tree, string parent, ICollection<ChildMotionData> result)
        {
            var children = tree.children;
            for (var index = 0; index < children.Length; index++)
            {
                var child = children[index];
                var childPath = string.IsNullOrEmpty(parent) ? index.ToString(CultureInfo.InvariantCulture) : parent + "/" + index;
                var childTree = child.motion as BlendTree;
                result.Add(new ChildMotionData
                {
                    path = childPath,
                    parent = parent,
                    kind = child.motion == null ? "None" : childTree == null ? "AnimationClip" : "BlendTree",
                    name = child.motion == null ? string.Empty : child.motion.name,
                    assetPath = child.motion == null ? string.Empty : AssetDatabase.GetAssetPath(child.motion),
                    blendType = childTree == null ? string.Empty : childTree.blendType.ToString(),
                    blendParameter = childTree == null ? string.Empty : childTree.blendParameter,
                    blendParameterY = childTree == null ? string.Empty : childTree.blendParameterY,
                    useAutomaticThresholds = childTree != null && childTree.useAutomaticThresholds,
                    minThreshold = childTree == null ? 0f : childTree.minThreshold,
                    maxThreshold = childTree == null ? 0f : childTree.maxThreshold,
                    threshold = child.threshold,
                    position = child.position,
                    timeScale = child.timeScale,
                    cycleOffset = child.cycleOffset,
                    directBlendParameter = child.directBlendParameter,
                    mirror = child.mirror
                });
                if (childTree != null)
                    AddMotionChildren(childTree, childPath, result);
            }
        }

        private static TransitionData DescribeTransition(AnimatorStateTransition transition, int index, string from)
        {
            return new TransitionData
            {
                index = index,
                from = from,
                to = transition.isExit ? "Exit" : transition.destinationState != null ? transition.destinationState.name : transition.destinationStateMachine != null ? transition.destinationStateMachine.name : string.Empty,
                isExit = transition.isExit,
                hasExitTime = transition.hasExitTime,
                exitTime = transition.exitTime,
                duration = transition.duration,
                offset = transition.offset,
                hasFixedDuration = transition.hasFixedDuration,
                interruptionSource = transition.interruptionSource.ToString(),
                orderedInterruption = transition.orderedInterruption,
                canTransitionToSelf = transition.canTransitionToSelf,
                mute = transition.mute,
                solo = transition.solo,
                conditions = transition.conditions.Select(DescribeCondition).ToArray()
            };
        }

        private static TransitionData DescribeTransition(AnimatorTransition transition, int index, string from)
        {
            return new TransitionData
            {
                index = index,
                from = from,
                to = transition.destinationState != null ? transition.destinationState.name : transition.destinationStateMachine != null ? transition.destinationStateMachine.name : string.Empty,
                mute = transition.mute,
                solo = transition.solo,
                conditions = transition.conditions.Select(DescribeCondition).ToArray()
            };
        }

        private static ConditionData DescribeCondition(AnimatorCondition condition)
        {
            return new ConditionData { mode = condition.mode.ToString(), parameter = condition.parameter, threshold = condition.threshold };
        }

        private static void CollectMotions(AnimatorStateMachine machine, string layer, string parentPath, ICollection<MotionStateData> result)
        {
            var path = string.IsNullOrEmpty(parentPath) ? layer : parentPath;
            foreach (var state in machine.states)
                result.Add(new MotionStateData
                {
                    layer = layer,
                    state = state.state.name,
                    path = path + "/" + state.state.name,
                    motion = DescribeMotion(state.state.motion) ?? new MotionData { kind = "None" }
                });
            foreach (var child in machine.stateMachines)
                CollectMotions(child.stateMachine, layer, path + "/" + child.stateMachine.name, result);
        }

        private static RuntimeAnimatorController ResolveRuntimeController(string path)
        {
            var controller = AssetDatabase.LoadAssetAtPath<RuntimeAnimatorController>(path);
            if (controller == null)
                throw new InvalidOperationException("Runtime Animator Controller was not found: " + path);
            return controller;
        }

        private static AnimatorControllerLayer ResolveLayer(AnimatorController controller, string name)
        {
            var layers = controller.layers;
            return layers[LayerIndex(controller, name)];
        }

        private static int LayerIndex(AnimatorController controller, string name)
        {
            int index;
            if (int.TryParse(name, out index))
            {
                if (index < 0 || index >= controller.layers.Length)
                    throw new ArgumentOutOfRangeException("name", "Layer index must be between 0 and " + (controller.layers.Length - 1) + ".");
                return index;
            }
            return UniqueIndex(controller.layers.Select(item => item.name), name, "Layer");
        }

        private static void RemoveTransition(AnimatorStateMachine machine, AnimatorState sourceState, string fromState, AnimatorStateTransition transition)
        {
            if (IsAnyState(fromState))
                machine.RemoveAnyStateTransition(transition);
            else
                sourceState.RemoveTransition(transition);
        }

        private static AnimatorStateMachine ResolveStateMachine(AnimatorStateMachine root, string path)
        {
            if (string.IsNullOrWhiteSpace(path) || string.Equals(path, root.name, StringComparison.Ordinal))
                return root;
            var current = root;
            var segments = path.Split(new[] { '/' }, StringSplitOptions.RemoveEmptyEntries);
            var start = segments.Length > 0 && string.Equals(segments[0], root.name, StringComparison.Ordinal) ? 1 : 0;
            for (var index = start; index < segments.Length; index++)
                current = ResolveDirectStateMachine(current, segments[index]);
            return current;
        }

        private static AnimatorStateMachine ResolveDirectStateMachine(AnimatorStateMachine parent, string name)
        {
            var matches = parent.stateMachines.Where(item => string.Equals(item.stateMachine.name, name, StringComparison.Ordinal)).Select(item => item.stateMachine).ToArray();
            if (matches.Length != 1)
                throw new InvalidOperationException(matches.Length == 0 ? "State Machine was not found: " + name : "State Machine name is ambiguous: " + name);
            return matches[0];
        }

        private static AnimatorState ResolveDirectState(AnimatorStateMachine machine, string name)
        {
            var matches = machine.states.Where(item => string.Equals(item.state.name, name, StringComparison.Ordinal)).Select(item => item.state).ToArray();
            if (matches.Length != 1)
                throw new InvalidOperationException(matches.Length == 0 ? "State was not found: " + name : "State name is ambiguous: " + name);
            return matches[0];
        }

        private static AnimatorState ResolveStateReference(AnimatorStateMachine root, string reference, AnimatorStateMachine defaultMachine)
        {
            if (string.IsNullOrWhiteSpace(reference))
                throw new ArgumentException("State reference is required.", "reference");
            var slash = reference.LastIndexOf('/');
            return slash < 0
                ? ResolveDirectState(defaultMachine, reference)
                : ResolveDirectState(ResolveStateMachine(root, reference.Substring(0, slash)), reference.Substring(slash + 1));
        }

        private static AnimatorStateMachine StateMachineForStateReference(
            AnimatorStateMachine root,
            string reference,
            AnimatorStateMachine defaultMachine)
        {
            var slash = reference.LastIndexOf('/');
            return slash < 0 ? defaultMachine : ResolveStateMachine(root, reference.Substring(0, slash));
        }

        private static AnimatorStateTransition ResolveTransition(AnimatorStateMachine root, AnimatorStateMachine machine, string from, string to, int index)
        {
            AnimatorStateTransition[] transitions;
            if (IsAnyState(from))
                transitions = machine.anyStateTransitions;
            else
                transitions = ResolveStateReference(root, from, machine).transitions;
            var matches = transitions.Where(item => string.Equals(item.isExit ? "Exit" : item.destinationState != null ? item.destinationState.name : item.destinationStateMachine != null ? item.destinationStateMachine.name : string.Empty, to, StringComparison.Ordinal)).ToArray();
            if (matches.Length == 0)
                throw new InvalidOperationException("Transition was not found: " + from + " -> " + to);
            if (index < 0 && matches.Length > 1)
                throw new InvalidOperationException("Multiple transitions match; provide transition index returned by the controller graph.");
            var selected = index < 0 ? 0 : index;
            if (selected >= matches.Length)
                throw new InvalidOperationException("Transition index is outside the available range.");
            return matches[selected];
        }

        private static Motion ResolveMotion(AnimatorController controller, string pathOrName)
        {
            if (string.IsNullOrWhiteSpace(pathOrName))
                return null;
            var clip = AssetDatabase.LoadAssetAtPath<AnimationClip>(pathOrName);
            if (clip != null)
                return clip;
            var trees = AssetDatabase.LoadAllAssetsAtPath(AssetDatabase.GetAssetPath(controller)).OfType<BlendTree>()
                .Where(item => string.Equals(item.name, pathOrName, StringComparison.Ordinal)).ToArray();
            if (trees.Length != 1)
                throw new InvalidOperationException(trees.Length == 0 ? "Animation or BlendTree was not found: " + pathOrName : "BlendTree name is ambiguous: " + pathOrName);
            return trees[0];
        }

        private static void ApplyLayerValues(AnimatorControllerLayer layer, PropertyValue[] values)
        {
            foreach (var entry in values ?? Array.Empty<PropertyValue>())
            {
                switch (entry.path)
                {
                    case "name": layer.name = String(entry.value, entry.path); break;
                    case "defaultWeight": layer.defaultWeight = Float(entry.value, entry.path); break;
                    case "avatarMask":
                        var avatarMask = String(entry.value, entry.path);
                        layer.avatarMask = string.IsNullOrWhiteSpace(avatarMask) ? null : AssetDatabase.LoadAssetAtPath<AvatarMask>(avatarMask);
                        break;
                    case "blendingMode": layer.blendingMode = ParseEnum<AnimatorLayerBlendingMode>(String(entry.value, entry.path), entry.path); break;
                    case "iKPass": layer.iKPass = Bool(entry.value, entry.path); break;
                    case "syncedLayerIndex": layer.syncedLayerIndex = Int(entry.value, entry.path); break;
                    case "syncedLayerAffectsTiming": layer.syncedLayerAffectsTiming = Bool(entry.value, entry.path); break;
                    default: throw new InvalidOperationException("Unsupported Layer parameter: " + entry.path);
                }
            }
        }

        private static void ApplyStateValues(AnimatorState state, PropertyValue[] values)
        {
            foreach (var entry in values ?? Array.Empty<PropertyValue>())
            {
                switch (entry.path)
                {
                    case "name": state.name = String(entry.value, entry.path); break;
                    case "speed": state.speed = Float(entry.value, entry.path); break;
                    case "speedParameter": state.speedParameter = String(entry.value, entry.path); state.speedParameterActive = !string.IsNullOrEmpty(state.speedParameter); break;
                    case "timeParameter": state.timeParameter = String(entry.value, entry.path); state.timeParameterActive = !string.IsNullOrEmpty(state.timeParameter); break;
                    case "mirror": state.mirror = Bool(entry.value, entry.path); break;
                    case "mirrorParameter": state.mirrorParameter = String(entry.value, entry.path); state.mirrorParameterActive = !string.IsNullOrEmpty(state.mirrorParameter); break;
                    case "cycleOffset": state.cycleOffset = Float(entry.value, entry.path); break;
                    case "cycleOffsetParameter": state.cycleOffsetParameter = String(entry.value, entry.path); state.cycleOffsetParameterActive = !string.IsNullOrEmpty(state.cycleOffsetParameter); break;
                    case "tag": state.tag = String(entry.value, entry.path); break;
                    case "writeDefaultValues": state.writeDefaultValues = Bool(entry.value, entry.path); break;
                    case "iKOnFeet": state.iKOnFeet = Bool(entry.value, entry.path); break;
                    default: throw new InvalidOperationException("Unsupported State parameter: " + entry.path);
                }
            }
        }

        private static void ApplyTransitionValues(AnimatorStateTransition transition, PropertyValue[] values)
        {
            foreach (var entry in values ?? Array.Empty<PropertyValue>())
            {
                switch (entry.path)
                {
                    case "hasExitTime": transition.hasExitTime = Bool(entry.value, entry.path); break;
                    case "exitTime": transition.exitTime = Float(entry.value, entry.path); break;
                    case "duration": transition.duration = Float(entry.value, entry.path); break;
                    case "offset": transition.offset = Float(entry.value, entry.path); break;
                    case "hasFixedDuration": transition.hasFixedDuration = Bool(entry.value, entry.path); break;
                    case "interruptionSource": transition.interruptionSource = ParseEnum<TransitionInterruptionSource>(String(entry.value, entry.path), entry.path); break;
                    case "orderedInterruption": transition.orderedInterruption = Bool(entry.value, entry.path); break;
                    case "canTransitionToSelf": transition.canTransitionToSelf = Bool(entry.value, entry.path); break;
                    case "mute": transition.mute = Bool(entry.value, entry.path); break;
                    case "solo": transition.solo = Bool(entry.value, entry.path); break;
                    default: throw new InvalidOperationException("Unsupported Transition parameter: " + entry.path);
                }
            }
        }

        private static void ApplyConditions(AnimatorStateTransition transition, string json)
        {
            if (string.IsNullOrWhiteSpace(json))
                return;
            var list = JsonUtility.FromJson<ConditionList>(json);
            if (list == null || list.conditions == null)
                throw new ArgumentException("Transition conditions are invalid.", "json");
            foreach (var condition in transition.conditions)
                transition.RemoveCondition(condition);
            foreach (var condition in list.conditions)
                transition.AddCondition(ParseEnum<AnimatorConditionMode>(condition.mode, "condition.mode"), condition.threshold, condition.parameter);
        }

        private static void ApplyBlendTree(AnimatorController controller, BlendTree tree, string json)
        {
            if (string.IsNullOrWhiteSpace(json))
                return;
            var settings = JsonUtility.FromJson<BlendTreeSettings>(json);
            if (settings == null)
                throw new ArgumentException("BlendTree settings are invalid.", "json");
            if (!string.IsNullOrWhiteSpace(settings.blendType)) tree.blendType = ParseEnum<BlendTreeType>(settings.blendType, "blendType");
            tree.blendParameter = settings.blendParameter ?? string.Empty;
            tree.blendParameterY = settings.blendParameterY ?? string.Empty;
            tree.useAutomaticThresholds = settings.useAutomaticThresholds;
            tree.minThreshold = settings.minThreshold;
            tree.maxThreshold = settings.maxThreshold;
            if (settings.children != null)
            {
                tree.children = Array.Empty<ChildMotion>();
                foreach (var input in settings.children)
                {
                    tree.AddChild(ResolveMotion(controller, input.motion));
                    var children = tree.children;
                    var index = children.Length - 1;
                    var child = children[index];
                    child.threshold = input.threshold;
                    child.position = new Vector2(input.x, input.y);
                    child.timeScale = input.timeScale;
                    child.cycleOffset = input.cycleOffset;
                    child.directBlendParameter = input.directBlendParameter;
                    child.mirror = input.mirror;
                    children[index] = child;
                    tree.children = children;
                }
            }
        }

        private static void SetParameterDefault(AnimatorControllerParameter parameter, string value)
        {
            if (string.IsNullOrWhiteSpace(value))
                return;
            switch (parameter.type)
            {
                case AnimatorControllerParameterType.Float: parameter.defaultFloat = Float(value, "value"); break;
                case AnimatorControllerParameterType.Int: parameter.defaultInt = Int(value, "value"); break;
                case AnimatorControllerParameterType.Bool: parameter.defaultBool = Bool(value, "value"); break;
                case AnimatorControllerParameterType.Trigger: break;
                default: throw new InvalidOperationException("Unsupported Animator Parameter type: " + parameter.type);
            }
        }

        private static bool IsAnyState(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
                return false;
            var normalized = value.Replace(" ", string.Empty).Replace("-", string.Empty).Replace("_", string.Empty);
            return string.Equals(normalized, "AnyState", StringComparison.OrdinalIgnoreCase);
        }

        private static void MarkSceneObject(Animator animator)
        {
            EditorUtility.SetDirty(animator);
            if (PrefabUtility.IsPartOfPrefabInstance(animator))
                PrefabUtility.RecordPrefabInstancePropertyModifications(animator);
            ScenePersistenceService.MarkDirty(animator.gameObject.scene);
        }

        private static void SaveController(AnimatorController controller)
        {
            EditorUtility.SetDirty(controller);
            AssetDatabase.SaveAssets();
            AnimationEditorWindowService.Open(controller);
        }

        private static void EnsureEditMode()
        {
            if (EditorApplication.isPlayingOrWillChangePlaymode)
                throw new InvalidOperationException("Animator asset editing is available only in Edit Mode.");
            AnimationEditorWindowService.PrepareForAssetMutation();
        }

        private static void EnsureUnique(IEnumerable<string> values, string value, string label)
        {
            Required(value, label);
            if (values.Any(item => string.Equals(item, value, StringComparison.Ordinal)))
                throw new InvalidOperationException(label + " already exists: " + value);
        }

        private static int UniqueIndex(IEnumerable<string> values, string value, string label)
        {
            var matches = values.Select((item, index) => new { item, index }).Where(entry => string.Equals(entry.item, value, StringComparison.Ordinal)).ToArray();
            if (matches.Length != 1)
                throw new InvalidOperationException(matches.Length == 0 ? label + " was not found: " + value : label + " name is ambiguous: " + value);
            return matches[0].index;
        }

        private static string Required(string value, string name)
        {
            if (string.IsNullOrWhiteSpace(value))
                throw new ArgumentException(name + " is required.", name);
            return value;
        }

        private static T ParseEnum<T>(string value, string name) where T : struct
        {
            T result;
            if (!Enum.TryParse(value, true, out result))
                throw new ArgumentException(name + " is invalid: " + value, name);
            return result;
        }

        private static float Float(string value, string name)
        {
            float result;
            if (!float.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out result) || float.IsNaN(result) || float.IsInfinity(result))
                throw new ArgumentException(name + " must be a finite number.", name);
            return result;
        }

        private static int Int(string value, string name)
        {
            int result;
            if (!int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out result))
                throw new ArgumentException(name + " must be an integer.", name);
            return result;
        }

        private static bool Bool(string value, string name)
        {
            bool result;
            if (!bool.TryParse(value, out result))
                throw new ArgumentException(name + " must be true or false.", name);
            return result;
        }

        private static string String(string value, string name)
        {
            try
            {
                var parsed = JsonUtility.FromJson<StringValue>("{\"value\":" + value + "}");
                if (parsed == null || parsed.value == null)
                    throw new FormatException();
                return parsed.value;
            }
            catch (Exception)
            {
                throw new ArgumentException(name + " must be a JSON string.", name);
            }
        }
    }
}
