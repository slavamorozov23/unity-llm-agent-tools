using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using UnityEditor;
using UnityEngine;

namespace UnityAgentBridge.Editor
{
    internal static class AnimationClipService
    {
        [Serializable]
        private sealed class ClipList { public ClipData[] clips = Array.Empty<ClipData>(); }

        [Serializable]
        private sealed class ClipData
        {
            public string name;
            public string path;
            public float frameRate;
            public float length;
        }

        [Serializable]
        private sealed class ClipIdentity
        {
            public string name;
            public string path;
        }

        [Serializable]
        private sealed class PropertyList { public AnimationPropertyData[] properties = Array.Empty<AnimationPropertyData>(); }

        [Serializable]
        private sealed class AnimationPropertyData
        {
            public string id;
            public string objectPath;
            public string componentType;
            public string property;
            public string kind;
        }

        [Serializable]
        private sealed class AnimationTableData
        {
            public string name;
            public string path;
            public float frameRate;
            public int[] frames = Array.Empty<int>();
            public AnimationRowData[] rows = Array.Empty<AnimationRowData>();
            public string[] events = Array.Empty<string>();
        }

        [Serializable]
        private sealed class EventKeyList
        {
            public EventKey[] keys = Array.Empty<EventKey>();
        }

        // One Animation Event as --key gives it: frame|time, function and at most one parameter.
        [Serializable]
        private sealed class EventKey
        {
            public int frame;
            public bool hasFrame;
            public float time;
            public bool hasTime;
            public string function;
            public float @float;
            public int @int;
            public string @string;
            public string @object;
        }

        [Serializable]
        private sealed class AnimationRowData
        {
            public string id;
            public string objectPath;
            public string componentType;
            public string property;
            public string kind;
            public AnimationCellData[] cells = Array.Empty<AnimationCellData>();
        }

        [Serializable]
        private sealed class AnimationCellData
        {
            public bool hasKey;
            public int frame;
            public float time;
            public float value;
            public float inTangent;
            public float outTangent;
            public float inWeight;
            public float outWeight;
            public int weightedMode;
            public string reference;
        }

        [Serializable]
        private sealed class KeyList { public AnimationKeyData[] keys = Array.Empty<AnimationKeyData>(); }

        [Serializable]
        private sealed class AnimationKeyData
        {
            public int frame;
            public float time;
            public bool hasTime;
            public float value;
            public bool hasValue;
            public float inTangent;
            public float outTangent;
            public bool hasInTangent;
            public bool hasOutTangent;
            public float inWeight;
            public float outWeight;
            public bool hasInWeight;
            public bool hasOutWeight;
            public int weightedMode;
            public bool hasWeightedMode;
            public string reference;
        }

        [Serializable]
        private sealed class ClipSettingData
        {
            public string name;
            public string path;
            public string parameter;
            public string type;
            public string value;
        }

        private static readonly Dictionary<string, string> SettingAliases = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            { "looppose", "loopBlend" },
            { "loopblend", "loopBlend" }
        };

        public static string ListClips(string path)
        {
            var normalized = string.IsNullOrWhiteSpace(path) ? string.Empty : path.Replace('\\', '/');
            if (normalized.EndsWith(".anim", StringComparison.OrdinalIgnoreCase))
            {
                var clip = ResolveClip(normalized);
                return JsonUtility.ToJson(new ClipList { clips = new[] { DescribeClip(clip) } });
            }
            return JsonUtility.ToJson(new ClipList { clips = AllClips(SearchFolder(path)).Select(DescribeClip).ToArray() });
        }

        public static string GetTable(string clip)
        {
            var asset = ResolveClip(clip);
            AnimationEditorWindowService.Open(asset);
            var floatBindings = AnimationUtility.GetCurveBindings(asset);
            var objectBindings = AnimationUtility.GetObjectReferenceCurveBindings(asset);
            var frameRate = asset.frameRate <= 0f ? 60f : asset.frameRate;
            var frames = new SortedSet<int>();
            foreach (var binding in floatBindings)
                foreach (var key in AnimationUtility.GetEditorCurve(asset, binding).keys)
                    frames.Add(Frame(key.time, frameRate));
            foreach (var binding in objectBindings)
                foreach (var key in AnimationUtility.GetObjectReferenceCurve(asset, binding))
                    frames.Add(Frame(key.time, frameRate));

            var columns = frames.ToArray();
            var rows = new List<AnimationRowData>();
            foreach (var binding in floatBindings.OrderBy(BindingOrder, StringComparer.Ordinal))
            {
                var keys = AnimationUtility.GetEditorCurve(asset, binding).keys.ToDictionary(key => Frame(key.time, frameRate));
                rows.Add(Row(binding, "float", columns, frame =>
                {
                    Keyframe key;
                    if (!keys.TryGetValue(frame, out key))
                        return new AnimationCellData { frame = frame };
                    return new AnimationCellData
                    {
                        hasKey = true,
                        frame = frame,
                        time = key.time,
                        value = key.value,
                        inTangent = key.inTangent,
                        outTangent = key.outTangent,
                        inWeight = key.inWeight,
                        outWeight = key.outWeight,
                        weightedMode = (int)key.weightedMode
                    };
                }));
            }
            foreach (var binding in objectBindings.OrderBy(BindingOrder, StringComparer.Ordinal))
            {
                var keys = AnimationUtility.GetObjectReferenceCurve(asset, binding).ToDictionary(key => Frame(key.time, frameRate));
                rows.Add(Row(binding, "object", columns, frame =>
                {
                    ObjectReferenceKeyframe key;
                    if (!keys.TryGetValue(frame, out key))
                        return new AnimationCellData { frame = frame };
                    return new AnimationCellData
                    {
                        hasKey = true,
                        frame = frame,
                        time = key.time,
                        reference = key.value == null ? string.Empty : AssetDatabase.GetAssetPath(key.value)
                    };
                }));
            }

            return JsonUtility.ToJson(new AnimationTableData
            {
                name = asset.name,
                path = AssetDatabase.GetAssetPath(asset),
                frameRate = frameRate,
                frames = columns,
                rows = rows.ToArray(),
                events = AnimationUtility.GetAnimationEvents(asset).Select(item => EventText(item, frameRate)).ToArray()
            });
        }

        // The Animation window's event row: "12 Open(\"door\")".
        private static string EventText(AnimationEvent item, float frameRate)
        {
            var parameter = item.objectReferenceParameter != null ? AssetDatabase.GetAssetPath(item.objectReferenceParameter)
                : !string.IsNullOrEmpty(item.stringParameter) ? "\"" + item.stringParameter + "\""
                : Math.Abs(item.floatParameter) > 0f ? item.floatParameter.ToString("R", CultureInfo.InvariantCulture)
                : item.intParameter != 0 ? item.intParameter.ToString(CultureInfo.InvariantCulture) : string.Empty;
            return Frame(item.time, frameRate) + " " + item.functionName + (parameter.Length > 0 ? "(" + parameter + ")" : string.Empty);
        }

        // Events edited like keys: create adds, modify replaces all, delete removes those at the given frames (all without keys).
        public static string MutateEvents(BridgeRequest request)
        {
            EnsureEditMode();
            var clip = ResolveClip(request.clip);
            var frameRate = clip.frameRate <= 0f ? 60f : clip.frameRate;
            var keys = (JsonUtility.FromJson<EventKeyList>(request.json ?? "{}") ?? new EventKeyList()).keys ?? Array.Empty<EventKey>();
            var events = AnimationUtility.GetAnimationEvents(clip).ToList();
            var action = (request.action ?? string.Empty).ToLowerInvariant();
            Func<EventKey, float> timeOf = key =>
            {
                if (!key.hasFrame && !key.hasTime)
                    throw new ArgumentException("An event key needs frame or time.");
                return key.hasTime ? key.time : key.frame / frameRate;
            };
            switch (action)
            {
                case "create":
                case "modify":
                    if (action == "modify")
                        events.Clear();
                    foreach (var key in keys)
                    {
                        if (string.IsNullOrWhiteSpace(key.function))
                            throw new ArgumentException("An event key needs function=<method>.");
                        UnityEngine.Object reference = null;
                        if (!string.IsNullOrEmpty(key.@object))
                        {
                            reference = AssetDatabase.LoadMainAssetAtPath(key.@object);
                            if (reference == null)
                                throw new InvalidOperationException("Event object parameter was not found: " + key.@object);
                        }
                        events.Add(new AnimationEvent
                        {
                            time = timeOf(key),
                            functionName = key.function,
                            floatParameter = key.@float,
                            intParameter = key.@int,
                            stringParameter = key.@string ?? string.Empty,
                            objectReferenceParameter = reference
                        });
                    }
                    break;
                case "delete":
                    if (keys.Length == 0)
                        events.Clear();
                    foreach (var key in keys)
                    {
                        var frame = Frame(timeOf(key), frameRate);
                        if (events.RemoveAll(item => Frame(item.time, frameRate) == frame &&
                                (string.IsNullOrEmpty(key.function) || item.functionName == key.function)) == 0)
                            throw new InvalidOperationException("No event at frame " + frame + ". Events: " +
                                string.Join(", ", AnimationUtility.GetAnimationEvents(clip).Select(item => EventText(item, frameRate))));
                    }
                    break;
                default:
                    throw new ArgumentException("Animation event action must be create, modify, or delete.", "action");
            }
            AnimationUtility.SetAnimationEvents(clip, events.OrderBy(item => item.time).ToArray());
            EditorUtility.SetDirty(clip);
            AssetDatabase.SaveAssets();
            AnimationEditorWindowService.Open(clip);
            return JsonUtility.ToJson(new EventTextList { events = AnimationUtility.GetAnimationEvents(clip).Select(item => EventText(item, frameRate)).ToArray() });
        }

        [Serializable]
        private sealed class EventTextList
        {
            public string[] events = Array.Empty<string>();
        }

        public static string GetAvailableProperties(string scenePath, bool children = false)
        {
            if (scenePath.Replace('\\', '/').EndsWith(".anim", StringComparison.OrdinalIgnoreCase))
            {
                var clip = ResolveClip(scenePath);
                AnimationEditorWindowService.Open(clip);
                var clipProperties = AnimationUtility.GetCurveBindings(clip)
                    .Select(binding => DescribeBinding(binding, "float"))
                    .Concat(AnimationUtility.GetObjectReferenceCurveBindings(clip)
                        .Select(binding => DescribeBinding(binding, "object")))
                    .OrderBy(item => item.objectPath, StringComparer.Ordinal)
                    .ThenBy(item => item.componentType, StringComparer.Ordinal)
                    .ThenBy(item => item.property, StringComparer.Ordinal)
                    .ToArray();
                return JsonUtility.ToJson(new PropertyList { properties = clipProperties });
            }
            var target = ScenePath.ResolveObject(scenePath);
            EditorPresentationService.ShowComponentOwner(target);
            var animator = target.GetComponentInParent<Animator>(true);
            var root = animator == null ? target : animator.gameObject;
            // A search covers the objects below too, as the Animation window's Add Property list does.
            var objects = children ? target.GetComponentsInChildren<Transform>(true) : new[] { target.transform };
            var properties = objects
                .SelectMany(item => AnimationUtility.GetAnimatableBindings(item.gameObject, root)
                    .Select(binding => DescribeBinding(binding, binding.isPPtrCurve ? "object" : "float"))
                    .Concat(EulerRotationProperties(item, root.transform)))
                .GroupBy(item => item.id, StringComparer.Ordinal)
                .Select(group => group.First())
                .OrderBy(item => item.objectPath, StringComparer.Ordinal)
                .ThenBy(item => item.componentType, StringComparer.Ordinal)
                .ThenBy(item => item.property, StringComparer.Ordinal)
                .ToArray();
            return JsonUtility.ToJson(new PropertyList { properties = properties });
        }

        private static IEnumerable<AnimationPropertyData> EulerRotationProperties(Transform target, Transform root)
        {
            var path = AnimationUtility.CalculateTransformPath(target, root);
            return new[] { "x", "y", "z" }.Select(axis => DescribeBinding(
                EditorCurveBinding.FloatCurve(path, typeof(Transform), "localEulerAnglesRaw." + axis),
                "float"));
        }

        public static string CreateClip(string name, string path)
        {
            EnsureEditMode();
            var assetPath = NewAssetPath(name, path, ".anim", "Assets/Animations");
            if (AssetDatabase.LoadAssetAtPath<UnityEngine.Object>(assetPath) != null)
                throw new InvalidOperationException("Animation clip already exists: " + assetPath);
            var clip = new AnimationClip { name = Path.GetFileNameWithoutExtension(assetPath), frameRate = 60f };
            AssetDatabase.CreateAsset(clip, assetPath);
            AssetDatabase.SaveAssets();
            AnimationEditorWindowService.Open(clip);
            return JsonUtility.ToJson(DescribeClip(clip));
        }

        public static string DeleteClip(string nameOrPath)
        {
            EnsureEditMode();
            var clip = ResolveClip(nameOrPath);
            var path = AssetDatabase.GetAssetPath(clip);
            var name = clip.name;
            EditorPresentationService.ShowAsset(clip);
            if (!AssetDatabase.DeleteAsset(path))
                throw new InvalidOperationException("Unity could not delete animation clip: " + path);
            return JsonUtility.ToJson(new ClipIdentity { name = name, path = path });
        }

        public static string MutateProperty(BridgeRequest request)
        {
            EnsureEditMode();
            var clip = ResolveClip(request.clip);
            var parsed = ParsePropertyId(request.propertyPath);
            var binding = parsed.kind == "object"
                ? EditorCurveBinding.PPtrCurve(request.objectPath ?? string.Empty, parsed.type, parsed.property)
                : EditorCurveBinding.FloatCurve(request.objectPath ?? string.Empty, parsed.type, parsed.property);
            var keys = ParseKeys(request.json);
            switch ((request.action ?? string.Empty).ToLowerInvariant())
            {
                case "create":
                    CreateKeys(clip, binding, parsed.kind, keys);
                    break;
                case "modify":
                    ModifyKeys(clip, binding, parsed.kind, keys);
                    break;
                case "delete":
                    DeleteKeys(clip, binding, parsed.kind, keys);
                    break;
                default:
                    throw new ArgumentException("Animation property action must be create, modify, or delete.", "action");
            }
            VerifyKeyMutation(clip, binding, parsed.kind, request.action, keys);
            EditorUtility.SetDirty(clip);
            AssetDatabase.SaveAssets();
            AnimationEditorWindowService.Open(clip);
            return JsonUtility.ToJson(DescribeClip(clip));
        }

        public static string ClipSetting(BridgeRequest request)
        {
            var clip = ResolveClip(request.clip);
            var settings = AnimationUtility.GetAnimationClipSettings(clip);
            var member = ResolveSetting(settings, request.propertyPath);
            var action = (request.action ?? string.Empty).ToLowerInvariant();
            if (action == "set" || action == "modify")
            {
                EnsureEditMode();
                SetSettingValue(member, settings, ParseSettingValue(SettingType(member), request.value));
                AnimationUtility.SetAnimationClipSettings(clip, settings);
                EditorUtility.SetDirty(clip);
                AssetDatabase.SaveAssets();
            }
            else if (action != "get")
            {
                throw new ArgumentException("Animation clip setting action must be get or set.", "action");
            }
            AnimationEditorWindowService.Open(clip);
            return JsonUtility.ToJson(new ClipSettingData
            {
                name = clip.name,
                path = AssetDatabase.GetAssetPath(clip),
                parameter = DisplaySettingName(member.Name),
                type = SettingTypeName(SettingType(member)),
                value = FormatSettingValue(GetSettingValue(member, settings))
            });
        }

        internal static AnimationClip ResolveClip(string nameOrPath)
        {
            if (string.IsNullOrWhiteSpace(nameOrPath))
                throw new ArgumentException("Animation clip name or path is required.", "nameOrPath");
            var normalized = nameOrPath.Replace('\\', '/');
            if (normalized.StartsWith("Assets/", StringComparison.OrdinalIgnoreCase))
            {
                var exact = AssetDatabase.LoadAssetAtPath<AnimationClip>(normalized);
                if (exact == null || !normalized.EndsWith(".anim", StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("Animation clip was not found: " + nameOrPath);
                return exact;
            }
            var matches = AllClips("Assets/Animations").Where(clip =>
            {
                var path = AssetDatabase.GetAssetPath(clip);
                return string.Equals(path, nameOrPath, StringComparison.OrdinalIgnoreCase)
                    || string.Equals(clip.name, Path.GetFileNameWithoutExtension(nameOrPath), StringComparison.OrdinalIgnoreCase);
            }).ToArray();
            if (matches.Length != 1)
                throw new InvalidOperationException(matches.Length == 0
                    ? "Animation clip was not found in Assets/Animations: " + nameOrPath
                    : "Animation clip name is ambiguous: " + nameOrPath);
            return matches[0];
        }

        internal static string NewAssetPath(string name, string path, string extension, string defaultFolder)
        {
            if (string.IsNullOrWhiteSpace(name))
                throw new ArgumentException("Asset name is required.", "name");
            var folder = string.IsNullOrWhiteSpace(path) ? defaultFolder : path.Replace('\\', '/').TrimEnd('/');
            if (Path.HasExtension(folder))
                folder = Path.GetDirectoryName(folder).Replace('\\', '/');
            if (!folder.StartsWith("Assets", StringComparison.Ordinal) || folder.Contains(".."))
                throw new InvalidOperationException("Animation asset path must stay inside Assets.");
            EnsureFolder(folder);
            return folder + "/" + Path.GetFileNameWithoutExtension(name) + extension;
        }

        private static IEnumerable<AnimationClip> AllClips(string folder)
        {
            if (!AssetDatabase.IsValidFolder(folder))
                return Enumerable.Empty<AnimationClip>();
            return AssetDatabase.FindAssets("t:AnimationClip", new[] { folder })
                .Select(AssetDatabase.GUIDToAssetPath)
                .Where(path => path.EndsWith(".anim", StringComparison.OrdinalIgnoreCase))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(path => path, StringComparer.Ordinal)
                .Select(AssetDatabase.LoadAssetAtPath<AnimationClip>)
                .Where(clip => clip != null);
        }

        private static string SearchFolder(string path)
        {
            var folder = string.IsNullOrWhiteSpace(path) ? "Assets/Animations" : path.Replace('\\', '/').TrimEnd('/');
            if (!folder.StartsWith("Assets", StringComparison.Ordinal) || folder.Contains("..") || Path.HasExtension(folder))
                throw new InvalidOperationException("Animation search path must be a folder inside Assets.");
            return folder;
        }

        private static ClipData DescribeClip(AnimationClip clip)
        {
            return new ClipData { name = clip.name, path = AssetDatabase.GetAssetPath(clip), frameRate = clip.frameRate, length = clip.length };
        }

        private static AnimationRowData Row(EditorCurveBinding binding, string kind, int[] frames, Func<int, AnimationCellData> cell)
        {
            return new AnimationRowData
            {
                id = PropertyId(binding, kind),
                objectPath = binding.path,
                componentType = binding.type.FullName,
                property = binding.propertyName,
                kind = kind,
                cells = frames.Select(cell).ToArray()
            };
        }

        private static AnimationPropertyData DescribeBinding(EditorCurveBinding binding, string kind)
        {
            return new AnimationPropertyData
            {
                id = PropertyId(binding, kind),
                objectPath = binding.path,
                componentType = binding.type.FullName,
                property = binding.propertyName,
                kind = kind
            };
        }

        private static string PropertyId(EditorCurveBinding binding, string kind)
        {
            return kind + "::" + binding.type.FullName + "::" + binding.propertyName;
        }

        private static string BindingOrder(EditorCurveBinding binding)
        {
            return binding.path + "\n" + binding.type.FullName + "\n" + binding.propertyName;
        }

        private static int Frame(float time, float frameRate)
        {
            return Mathf.RoundToInt(time * frameRate);
        }

        private static (string kind, Type type, string property) ParsePropertyId(string id)
        {
            if (string.IsNullOrWhiteSpace(id))
                throw new ArgumentException("Animation Property id is required.", "propertyPath");
            var parts = id.Split(new[] { "::" }, 3, StringSplitOptions.None);
            if (parts.Length != 3 ||
                (parts[0] != "float" && parts[0] != "object" && parts[0] != "bool" &&
                 parts[0] != "int" && parts[0] != "enum" && parts[0] != "discrete"))
            {
                var separator = id.IndexOf('/');
                if (separator <= 0 || separator == id.Length - 1)
                    throw new ArgumentException("Property must be TYPE/PROPERTY or the id returned by animation-table.", "propertyPath");
                parts = new[] { "float", id.Substring(0, separator), id.Substring(separator + 1) };
            }
            var type = AppDomain.CurrentDomain.GetAssemblies()
                .SelectMany(assembly =>
                {
                    try { return assembly.GetTypes(); }
                    catch (ReflectionTypeLoadException error) { return error.Types.Where(value => value != null); }
                })
                .Where(value => value != null &&
                    (string.Equals(value.FullName, parts[1], StringComparison.Ordinal) ||
                     string.Equals(value.Name, parts[1], StringComparison.Ordinal)))
                .OrderByDescending(value => string.Equals(value.FullName, parts[1], StringComparison.Ordinal))
                .ThenByDescending(value => value.Namespace == "UnityEngine" || value.Namespace != null && value.Namespace.StartsWith("UnityEngine.", StringComparison.Ordinal))
                .FirstOrDefault();
            if (type == null)
                throw new InvalidOperationException("Animation Property component type was not found: " + parts[1]);
            return (parts[0] == "object" ? "object" : "float", type, parts[2]);
        }

        private static AnimationKeyData[] ParseKeys(string json)
        {
            if (string.IsNullOrWhiteSpace(json))
                return Array.Empty<AnimationKeyData>();
            var value = JsonUtility.FromJson<KeyList>(json);
            if (value == null || value.keys == null)
                throw new ArgumentException("Animation keys are invalid.", "json");
            return value.keys;
        }

        private static void CreateKeys(AnimationClip clip, EditorCurveBinding binding, string kind, AnimationKeyData[] keys)
        {
            if (keys.Length == 0)
                throw new ArgumentException("Creating a Property or key requires keys.", "json");
            if (kind == "object")
            {
                var existing = AnimationUtility.GetObjectReferenceCurve(clip, binding) ?? Array.Empty<ObjectReferenceKeyframe>();
                EnsureNoDuplicateTimes(existing.Select(key => key.time), keys, clip.frameRate);
                AnimationUtility.SetObjectReferenceCurve(clip, binding, existing.Concat(keys.Select(key => ObjectKey(key, clip.frameRate))).OrderBy(key => key.time).ToArray());
                return;
            }
            var curve = AnimationUtility.GetEditorCurve(clip, binding) ?? new AnimationCurve();
            EnsureNoDuplicateTimes(curve.keys.Select(key => key.time), keys, clip.frameRate);
            foreach (var key in keys)
            {
                RequireFloatValue(key);
                var index = curve.AddKey(FloatKey(key, clip.frameRate));
                ApplyTangentModes(curve, index, key);
            }
            AnimationUtility.SetEditorCurve(clip, binding, curve);
        }

        private static void ModifyKeys(AnimationClip clip, EditorCurveBinding binding, string kind, AnimationKeyData[] keys)
        {
            if (keys.Length == 0)
                throw new ArgumentException("Modifying keys requires keys.", "json");
            if (kind == "object")
            {
                var existing = AnimationUtility.GetObjectReferenceCurve(clip, binding);
                if (existing == null)
                    throw new InvalidOperationException("Animation Property does not exist.");
                AnimationUtility.SetObjectReferenceCurve(
                    clip,
                    binding,
                    keys.Select(key => ObjectKey(key, clip.frameRate)).OrderBy(key => key.time).ToArray());
                return;
            }
            var curve = AnimationUtility.GetEditorCurve(clip, binding);
            if (curve == null)
                throw new InvalidOperationException("Animation Property does not exist.");
            foreach (var key in keys)
                RequireFloatValue(key);
            var ordered = keys.OrderBy(key => KeyTime(key, clip.frameRate)).ToArray();
            var replacement = new AnimationCurve(ordered.Select(key => FloatKey(key, clip.frameRate)).ToArray());
            for (var index = 0; index < ordered.Length; index++)
                ApplyTangentModes(replacement, index, ordered[index]);
            AnimationUtility.SetEditorCurve(
                clip,
                binding,
                replacement);
        }

        private static void DeleteKeys(AnimationClip clip, EditorCurveBinding binding, string kind, AnimationKeyData[] keys)
        {
            if (kind == "object")
            {
                var existing = AnimationUtility.GetObjectReferenceCurve(clip, binding);
                if (existing == null)
                    throw new InvalidOperationException("Animation Property does not exist.");
                if (keys.Length == 0)
                {
                    AnimationUtility.SetObjectReferenceCurve(clip, binding, null);
                    return;
                }
                var frames = new HashSet<int>(keys.Select(key => Frame(KeyTime(key, clip.frameRate), clip.frameRate)));
                AnimationUtility.SetObjectReferenceCurve(clip, binding, existing.Where(key => !frames.Contains(Frame(key.time, clip.frameRate))).ToArray());
                return;
            }
            var curve = AnimationUtility.GetEditorCurve(clip, binding);
            if (curve == null)
                throw new InvalidOperationException("Animation Property does not exist.");
            if (keys.Length == 0)
            {
                AnimationUtility.SetEditorCurve(clip, binding, null);
                return;
            }
            var deletedFrames = new HashSet<int>(keys.Select(key => Frame(KeyTime(key, clip.frameRate), clip.frameRate)));
            AnimationUtility.SetEditorCurve(clip, binding, new AnimationCurve(curve.keys.Where(key => !deletedFrames.Contains(Frame(key.time, clip.frameRate))).ToArray()));
        }

        private static void VerifyKeyMutation(
            AnimationClip clip,
            EditorCurveBinding binding,
            string kind,
            string action,
            AnimationKeyData[] requested)
        {
            var deleted = string.Equals(action, "delete", StringComparison.OrdinalIgnoreCase);
            var replaced = string.Equals(action, "modify", StringComparison.OrdinalIgnoreCase);
            var requestedFrames = new HashSet<int>(requested.Select(key => Frame(KeyTime(key, clip.frameRate), clip.frameRate)));
            if (kind == "object")
            {
                var actual = AnimationUtility.GetObjectReferenceCurve(clip, binding) ?? Array.Empty<ObjectReferenceKeyframe>();
                var actualFrames = new HashSet<int>(actual.Select(key => Frame(key.time, clip.frameRate)));
                var valid = deleted
                    ? requested.Length == 0 ? actual.Length == 0 : requestedFrames.All(frame => !actualFrames.Contains(frame))
                    : requestedFrames.All(actualFrames.Contains) && (!replaced || actual.Length == requested.Length);
                if (!valid)
                    throw new InvalidOperationException("Unity did not apply the requested object-reference animation keys.");
                return;
            }

            var curve = AnimationUtility.GetEditorCurve(clip, binding);
            var actualKeys = curve == null ? Array.Empty<Keyframe>() : curve.keys;
            var byFrame = actualKeys.GroupBy(key => Frame(key.time, clip.frameRate)).ToDictionary(group => group.Key, group => group.Last());
            if (deleted)
            {
                var valid = requested.Length == 0 ? actualKeys.Length == 0 : requestedFrames.All(frame => !byFrame.ContainsKey(frame));
                if (!valid)
                    throw new InvalidOperationException("Unity did not delete the requested animation keys.");
                return;
            }
            foreach (var expected in requested)
            {
                Keyframe actual;
                var frame = Frame(KeyTime(expected, clip.frameRate), clip.frameRate);
                if (!byFrame.TryGetValue(frame, out actual) || !Mathf.Approximately(actual.value, expected.value))
                    throw new InvalidOperationException("Unity did not apply the animation key at frame " + frame + ".");
            }
            if (replaced && actualKeys.Length != requested.Length)
                throw new InvalidOperationException("Unity left obsolete keys in the modified animation Property.");
        }

        private static void EnsureNoDuplicateTimes(IEnumerable<float> existingTimes, AnimationKeyData[] keys, float frameRate)
        {
            var frames = new HashSet<int>(existingTimes.Select(time => Frame(time, frameRate)));
            foreach (var key in keys)
                if (!frames.Add(Frame(KeyTime(key, frameRate), frameRate)))
                    throw new InvalidOperationException("Animation key already exists at frame " + Frame(KeyTime(key, frameRate), frameRate) + ".");
        }

        private static Keyframe FloatKey(AnimationKeyData key, float frameRate)
        {
            var result = new Keyframe(
                KeyTime(key, frameRate),
                key.value,
                key.inTangent,
                key.outTangent,
                key.hasInWeight ? key.inWeight : 0f,
                key.hasOutWeight ? key.outWeight : 0f)
            {
                weightedMode = key.hasWeightedMode ? (WeightedMode)key.weightedMode : WeightedMode.None
            };
            return result;
        }

        private static void RequireFloatValue(AnimationKeyData key)
        {
            if (!key.hasValue)
                throw new ArgumentException("A float animation key requires value.", "json");
        }

        private static void ApplyTangentModes(AnimationCurve curve, int index, AnimationKeyData key)
        {
            if (!key.hasInTangent)
                AnimationUtility.SetKeyLeftTangentMode(curve, index, AnimationUtility.TangentMode.ClampedAuto);
            if (!key.hasOutTangent)
                AnimationUtility.SetKeyRightTangentMode(curve, index, AnimationUtility.TangentMode.ClampedAuto);
        }

        private static ObjectReferenceKeyframe ObjectKey(AnimationKeyData key, float frameRate)
        {
            var reference = string.IsNullOrWhiteSpace(key.reference) ? null : AssetDatabase.LoadAssetAtPath<UnityEngine.Object>(key.reference);
            if (!string.IsNullOrWhiteSpace(key.reference) && reference == null)
                throw new InvalidOperationException("Animation key asset was not found: " + key.reference);
            return new ObjectReferenceKeyframe { time = KeyTime(key, frameRate), value = reference };
        }

        private static float KeyTime(AnimationKeyData key, float frameRate)
        {
            return key.hasTime ? key.time : key.frame / (frameRate <= 0f ? 60f : frameRate);
        }

        private static void EnsureFolder(string folder)
        {
            var current = "Assets";
            foreach (var segment in folder.Split('/').Skip(1))
            {
                var next = current + "/" + segment;
                if (!AssetDatabase.IsValidFolder(next))
                    AssetDatabase.CreateFolder(current, segment);
                current = next;
            }
        }

        private static MemberInfo ResolveSetting(object settings, string requested)
        {
            if (string.IsNullOrWhiteSpace(requested))
                throw new ArgumentException("Animation clip setting name is required.", "propertyPath");
            var normalized = NormalizeSettingName(requested);
            string alias;
            if (SettingAliases.TryGetValue(normalized, out alias))
                normalized = NormalizeSettingName(alias);
            var flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
            var members = settings.GetType().GetProperties(flags)
                .Where(property => property.CanRead && property.CanWrite)
                .Cast<MemberInfo>()
                .Concat(settings.GetType().GetFields(flags).Where(field => !field.IsInitOnly).Cast<MemberInfo>());
            var matches = members
                .Where(member => NormalizeSettingName(member.Name) == normalized)
                .ToArray();
            if (matches.Length != 1)
            {
                var available = members.Select(member => DisplaySettingName(member.Name)).Distinct(StringComparer.Ordinal);
                throw new InvalidOperationException("Animation clip setting was not found: " + requested + ". Available: " + string.Join(", ", available));
            }
            return matches[0];
        }

        private static Type SettingType(MemberInfo member)
        {
            var property = member as PropertyInfo;
            if (property != null) return property.PropertyType;
            var field = member as FieldInfo;
            if (field != null) return field.FieldType;
            throw new InvalidOperationException("Unsupported animation clip setting member: " + member.Name);
        }

        private static object GetSettingValue(MemberInfo member, object settings)
        {
            var property = member as PropertyInfo;
            if (property != null) return property.GetValue(settings, null);
            return ((FieldInfo)member).GetValue(settings);
        }

        private static void SetSettingValue(MemberInfo member, object settings, object value)
        {
            var property = member as PropertyInfo;
            if (property != null) property.SetValue(settings, value, null);
            else ((FieldInfo)member).SetValue(settings, value);
        }

        private static object ParseSettingValue(Type type, string value)
        {
            if (type == typeof(bool))
            {
                bool parsed;
                if (bool.TryParse(value, out parsed)) return parsed;
            }
            else if (type == typeof(float))
            {
                float parsed;
                if (float.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out parsed) && !float.IsNaN(parsed) && !float.IsInfinity(parsed)) return parsed;
            }
            else if (typeof(UnityEngine.Object).IsAssignableFrom(type))
            {
                if (string.IsNullOrWhiteSpace(value)) return null;
                var asset = AssetDatabase.LoadAssetAtPath(value, type);
                if (asset != null) return asset;
            }
            throw new ArgumentException("Value is invalid for animation clip setting type " + type.Name + ".", "value");
        }

        private static string FormatSettingValue(object value)
        {
            if (value == null) return string.Empty;
            var asset = value as UnityEngine.Object;
            if (asset != null) return AssetDatabase.GetAssetPath(asset);
            var formattable = value as IFormattable;
            return formattable == null ? value.ToString() : formattable.ToString(null, CultureInfo.InvariantCulture);
        }

        private static string SettingTypeName(Type type)
        {
            if (type == typeof(bool)) return "Bool";
            if (type == typeof(float)) return "Float";
            if (typeof(UnityEngine.Object).IsAssignableFrom(type)) return "Object";
            return type.Name;
        }

        private static string NormalizeSettingName(string value)
        {
            return new string(value.Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());
        }

        private static string DisplaySettingName(string value)
        {
            if (string.Equals(value, "loopBlend", StringComparison.Ordinal)) return "Loop Pose";
            var characters = new List<char>();
            foreach (var character in value)
            {
                if (characters.Count > 0 && char.IsUpper(character)) characters.Add(' ');
                characters.Add(character);
            }
            return CultureInfo.InvariantCulture.TextInfo.ToTitleCase(new string(characters.ToArray()));
        }

        private static void EnsureEditMode()
        {
            if (EditorApplication.isPlayingOrWillChangePlaymode)
                throw new InvalidOperationException("Animation clip editing is available only in Edit Mode.");
            AnimationEditorWindowService.PrepareForAssetMutation();
        }
    }
}
