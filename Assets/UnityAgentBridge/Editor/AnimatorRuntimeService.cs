using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;

namespace UnityAgentBridge.Editor
{
    internal static class AnimatorRuntimeService
    {
        [Serializable]
        private sealed class RuntimeData
        {
            public string path;
            public bool enabled;
            public bool activeInHierarchy;
            public string state;
            public float speed;
            public LayerData[] layers = Array.Empty<LayerData>();
            public ParameterData[] parameters = Array.Empty<ParameterData>();
        }

        [Serializable]
        private sealed class LayerData
        {
            public int index;
            public string name;
            public float weight;
            public StateData current;
            public bool inTransition;
            public StateData next;
            public float transitionTime;
        }

        [Serializable]
        private sealed class StateData
        {
            public string name;
            public string path;
            public float normalizedTime;
            public float length;
            public float speed;
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

        public static string Control(BridgeRequest request)
        {
            EnsurePlayMode();
            var animator = AnimatorControllerService.ResolveAnimator(request.path);
            ApplyParameters(animator, request.values);
            if (!string.IsNullOrWhiteSpace(request.state))
            {
                var layerIndex = ResolveLayerIndex(animator, request.layer);
                var statePath = ResolveStatePath(animator, request.state, layerIndex);
                animator.Play(statePath, layerIndex, 0f);
                animator.Update(0f);
            }
            return JsonUtility.ToJson(Describe(animator));
        }

        public static string GetState(string path)
        {
            EnsurePlayMode();
            return JsonUtility.ToJson(Describe(AnimatorControllerService.ResolveAnimator(path)));
        }

        private static RuntimeData Describe(Animator animator)
        {
            var stateNames = StateNames(animator);
            var layers = new LayerData[animator.layerCount];
            for (var index = 0; index < layers.Length; index++)
            {
                var current = animator.GetCurrentAnimatorStateInfo(index);
                var transition = animator.IsInTransition(index);
                layers[index] = new LayerData
                {
                    index = index,
                    name = animator.GetLayerName(index),
                    weight = animator.GetLayerWeight(index),
                    current = DescribeState(current, stateNames),
                    inTransition = transition,
                    next = transition ? DescribeState(animator.GetNextAnimatorStateInfo(index), stateNames) : null,
                    transitionTime = transition ? animator.GetAnimatorTransitionInfo(index).normalizedTime : 0f
                };
            }

            return new RuntimeData
            {
                path = ScenePath.For(animator.gameObject),
                enabled = animator.enabled,
                activeInHierarchy = animator.gameObject.activeInHierarchy,
                state = !animator.gameObject.activeInHierarchy ? "inactive-object" : !animator.enabled ? "disabled-component" : "ready",
                speed = animator.speed,
                layers = layers,
                parameters = animator.parameters.Select(parameter => DescribeParameter(animator, parameter)).ToArray()
            };
        }

        private static StateData DescribeState(AnimatorStateInfo state, IReadOnlyDictionary<int, string> names)
        {
            string path;
            if (!names.TryGetValue(state.fullPathHash, out path))
                path = state.fullPathHash.ToString(CultureInfo.InvariantCulture);
            return new StateData
            {
                name = path.Contains(".") ? path.Substring(path.LastIndexOf('.') + 1) : path,
                path = path,
                normalizedTime = state.normalizedTime,
                length = state.length,
                speed = state.speed
            };
        }

        private static ParameterData DescribeParameter(Animator animator, AnimatorControllerParameter parameter)
        {
            var result = new ParameterData { name = parameter.name, type = parameter.type.ToString() };
            switch (parameter.type)
            {
                case AnimatorControllerParameterType.Float: result.floatValue = animator.GetFloat(parameter.nameHash); break;
                case AnimatorControllerParameterType.Int: result.intValue = animator.GetInteger(parameter.nameHash); break;
                case AnimatorControllerParameterType.Bool:
                case AnimatorControllerParameterType.Trigger: result.boolValue = animator.GetBool(parameter.nameHash); break;
            }
            return result;
        }

        private static Dictionary<int, string> StateNames(Animator animator)
        {
            var controller = AnimatorControllerService.ResolveController(null, ScenePath.For(animator.gameObject));
            return AnimatorControllerService.EnumerateStates(controller)
                .GroupBy(item => Animator.StringToHash(item.fullPath))
                .ToDictionary(group => group.Key, group => group.First().fullPath);
        }

        private static int ResolveLayerIndex(Animator animator, string layer)
        {
            if (string.IsNullOrWhiteSpace(layer))
                return 0;
            int numeric;
            if (int.TryParse(layer, NumberStyles.Integer, CultureInfo.InvariantCulture, out numeric))
            {
                if (numeric < 0 || numeric >= animator.layerCount)
                    throw new InvalidOperationException("Animator Layer index is outside the available range.");
                return numeric;
            }
            var matches = Enumerable.Range(0, animator.layerCount).Where(index => string.Equals(animator.GetLayerName(index), layer, StringComparison.Ordinal)).ToArray();
            if (matches.Length != 1)
                throw new InvalidOperationException(matches.Length == 0 ? "Animator Layer was not found: " + layer : "Animator Layer name is ambiguous: " + layer);
            return matches[0];
        }

        private static string ResolveStatePath(Animator animator, string state, int layerIndex)
        {
            var controller = AnimatorControllerService.ResolveController(null, ScenePath.For(animator.gameObject));
            var layerName = animator.GetLayerName(layerIndex);
            var matches = AnimatorControllerService.EnumerateStates(controller)
                .Where(item => string.Equals(item.layer, layerName, StringComparison.Ordinal))
                .Where(item => string.Equals(item.fullPath, state, StringComparison.Ordinal) || string.Equals(item.state.name, state, StringComparison.Ordinal))
                .Select(item => item.fullPath)
                .Distinct(StringComparer.Ordinal)
                .ToArray();
            if (matches.Length != 1)
                throw new InvalidOperationException(matches.Length == 0 ? "Animator State was not found: " + state : "Animator State name is ambiguous; use its full path.");
            return matches[0];
        }

        private static void ApplyParameters(Animator animator, PropertyValue[] values)
        {
            var parameters = animator.parameters.ToDictionary(parameter => parameter.name, StringComparer.Ordinal);
            foreach (var entry in values ?? Array.Empty<PropertyValue>())
            {
                AnimatorControllerParameter parameter;
                if (!parameters.TryGetValue(entry.path, out parameter))
                    throw new InvalidOperationException("Animator Parameter was not found: " + entry.path);
                switch (parameter.type)
                {
                    case AnimatorControllerParameterType.Float:
                        animator.SetFloat(parameter.nameHash, Float(entry.value, entry.path));
                        break;
                    case AnimatorControllerParameterType.Int:
                        animator.SetInteger(parameter.nameHash, Int(entry.value, entry.path));
                        break;
                    case AnimatorControllerParameterType.Bool:
                        animator.SetBool(parameter.nameHash, Bool(entry.value, entry.path));
                        break;
                    case AnimatorControllerParameterType.Trigger:
                        if (Bool(entry.value, entry.path)) animator.SetTrigger(parameter.nameHash); else animator.ResetTrigger(parameter.nameHash);
                        break;
                    default:
                        throw new InvalidOperationException("Unsupported Animator Parameter type: " + parameter.type);
                }
            }
        }

        private static void EnsurePlayMode()
        {
            if (!EditorApplication.isPlaying)
                throw new InvalidOperationException("Animator runtime commands require a running game.");
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
    }
}
