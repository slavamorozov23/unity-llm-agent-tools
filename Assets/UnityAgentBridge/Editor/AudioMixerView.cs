using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using UnityEditor;
using UnityEngine;
using UnityEngine.Audio;

namespace UnityAgentBridge.Editor
{
    // Audio Mixer window: snapshots, the group tree with faders and effects, exposed parameters.
    // Values are read and written in the snapshot being edited, as in the window; units follow the window (dB, %, Hz).
    internal sealed class AudioMixerView : AssetView
    {
        private const BindingFlags Any = BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
        private static readonly Assembly EditorAssembly = typeof(UnityEditor.Editor).Assembly;

        internal override bool Handles(string path, UnityEngine.Object asset)
        {
            return asset != null && asset.GetType().FullName == "UnityEditor.Audio.AudioMixerController";
        }

        internal override JsonText Describe(string path, UnityEngine.Object asset, string property)
        {
            var mixer = (AudioMixer)asset;
            ShowWindow();
            if (!string.IsNullOrEmpty(property))
            {
                var group = FindGroup(mixer, property);
                return new JsonText().Add(GroupPath(mixer, group), GroupDetails(mixer, group));
            }
            var target = TargetSnapshot(mixer);
            var start = Get(mixer, "startSnapshot");
            var groups = new JsonText();
            foreach (var group in Groups(mixer))
                groups.Add(GroupPath(mixer, group), GroupSummary(mixer, group));
            var exposed = new JsonText();
            foreach (var parameter in ExposedParameters(mixer))
                exposed.Add(ExposedName(parameter), (string)Call(mixer, "ResolveExposedParameterPath", ExposedGuid(parameter), false));
            return new JsonText()
                .Add("Snapshots", Snapshots(mixer).Select(snapshot => SnapshotLabel(snapshot, snapshot == (UnityEngine.Object)start, snapshot == target)).ToArray())
                .Add("Groups", groups)
                .AddIf(exposed.Count > 0, "Exposed Parameters", exposed);
        }

        internal override string[] Actions(string path, UnityEngine.Object asset)
        {
            return new[] { "Add Group", "Remove Group", "Add Effect", "Remove Effect", "Add Snapshot", "Remove Snapshot", "Expose Parameter", "Unexpose Parameter" };
        }

        internal override string Execute(string path, UnityEngine.Object asset, string action, PropertyValue[] values)
        {
            var mixer = (AudioMixer)asset;
            action = AssetViews.Match(Actions(path, asset), action);
            ShowWindow();
            string result;
            switch (action)
            {
                case "Add Group":
                    var parent = FindGroup(mixer, AssetViews.Value(values, "parent", false) ?? "Master");
                    var group = Call(mixer, "CreateNewGroup", AssetViews.Value(values, "name"), true);
                    Call(mixer, "AddChildToParent", group, parent);
                    Call(mixer, "AddGroupToCurrentView", group);
                    result = "Group added: " + GroupPath(mixer, (AudioMixerGroup)group);
                    break;
                case "Remove Group":
                    var removed = FindGroup(mixer, AssetViews.Value(values, "name"));
                    if (removed == Master(mixer))
                        throw new InvalidOperationException("The Master group cannot be removed.");
                    var groups = Array.CreateInstance(removed.GetType(), 1);
                    groups.SetValue(removed, 0);
                    result = "Group removed: " + GroupPath(mixer, removed);
                    Call(mixer, "DeleteGroups", groups);
                    break;
                case "Add Effect":
                    result = AddEffect(mixer, FindGroup(mixer, AssetViews.Value(values, "group")), AssetViews.Value(values, "name"), AssetViews.Value(values, "index", false));
                    break;
                case "Remove Effect":
                    var owner = FindGroup(mixer, AssetViews.Value(values, "group"));
                    var effect = FindEffect(owner, AssetViews.Value(values, "name"));
                    if ((bool)Call(effect, "IsAttenuation"))
                        throw new InvalidOperationException("Attenuation is the group fader and cannot be removed.");
                    result = "Effect removed: " + EffectName(effect) + " from " + owner.name;
                    Call(mixer, "RemoveEffect", effect, owner);
                    break;
                case "Add Snapshot":
                    Call(mixer, "CloneNewSnapshotFromTarget", true);
                    var snapshot = TargetSnapshot(mixer);
                    snapshot.name = AssetViews.Value(values, "name");
                    result = "Snapshot added: " + snapshot.name + " (copy of the edited snapshot, now edited)";
                    break;
                case "Remove Snapshot":
                    if (Snapshots(mixer).Count() == 1)
                        throw new InvalidOperationException("The last snapshot cannot be removed.");
                    var doomed = FindSnapshot(mixer, AssetViews.Value(values, "name"));
                    result = "Snapshot removed: " + doomed.name;
                    Call(mixer, "RemoveSnapshot", doomed);
                    break;
                case "Expose Parameter":
                    result = Expose(mixer, AssetViews.Value(values, "parameter"), AssetViews.Value(values, "name", false));
                    break;
                default:
                    var name = AssetViews.Value(values, "name");
                    var exposed = ExposedParameters(mixer).FirstOrDefault(item => AssetViews.KeyIs(name, ExposedName(item)));
                    if (exposed == null)
                        throw new ArgumentException("Exposed parameter was not found: " + name);
                    Call(mixer, "RemoveExposedParameter", ExposedGuid(exposed));
                    result = "Parameter unexposed: " + ExposedName(exposed);
                    break;
            }
            Saved(mixer);
            return result;
        }

        // Snapshot=<name> picks the edited snapshot, Start Snapshot=<name>, Exposed.<name>=<new name>,
        // <Group>.Volume|Pitch|Mute|Solo|Bypass Effects, <Group>.<Effect>.<Parameter>|Bypass|Target.
        internal override PropertyValue[] Modify(string path, UnityEngine.Object asset, PropertyValue[] values, bool confirm, List<string> changes)
        {
            var mixer = (AudioMixer)asset;
            ShowWindow();
            foreach (var entry in values)
            {
                var text = AssetViews.Text(entry.value);
                var parts = entry.path.Split('.');
                if (parts.Length == 1 && AssetViews.KeyIs(parts[0], "Snapshot"))
                {
                    Set(mixer, "TargetSnapshot", FindSnapshot(mixer, text));
                    changes.Add("Snapshot = " + TargetSnapshot(mixer).name);
                    continue;
                }
                if (parts.Length == 1 && AssetViews.KeyIs(parts[0], "Start Snapshot"))
                {
                    Undo.RecordObject(mixer, "Set Start Snapshot");
                    Set(mixer, "startSnapshot", FindSnapshot(mixer, text));
                    changes.Add("Start Snapshot = " + text);
                    continue;
                }
                if (parts.Length == 2 && AssetViews.KeyIs(parts[0], "Exposed", "Exposed Parameters"))
                {
                    changes.Add(RenameExposed(mixer, parts[1], text));
                    continue;
                }
                if (parts.Length < 2)
                    throw new ArgumentException("Use <Group>.<Field>, <Group>.<Effect>.<Parameter>, Snapshot, Start Snapshot or Exposed.<name>: " + entry.path);
                var group = FindGroup(mixer, parts[0]);
                changes.Add(parts.Length == 2 ? SetGroupField(mixer, group, parts[1], text) : SetEffectField(mixer, group, parts[1], parts[2], text));
            }
            Saved(mixer);
            return Array.Empty<PropertyValue>();
        }

        // ---------- groups ----------

        private static AudioMixerGroup Master(AudioMixer mixer)
        {
            return (AudioMixerGroup)Get(mixer, "masterGroup");
        }

        private static IEnumerable<AudioMixerGroup> Children(AudioMixerGroup group)
        {
            return ((IEnumerable)Get(group, "children")).Cast<AudioMixerGroup>();
        }

        private static IEnumerable<AudioMixerGroup> Groups(AudioMixer mixer)
        {
            var stack = new Stack<AudioMixerGroup>();
            stack.Push(Master(mixer));
            while (stack.Count > 0)
            {
                var group = stack.Pop();
                yield return group;
                foreach (var child in Children(group).Reverse())
                    stack.Push(child);
            }
        }

        private static string GroupPath(AudioMixer mixer, AudioMixerGroup group)
        {
            var names = new List<string> { group.name };
            for (var parent = Parent(mixer, group); parent != null; parent = Parent(mixer, parent))
                names.Insert(0, parent.name);
            return string.Join("/", names);
        }

        private static AudioMixerGroup Parent(AudioMixer mixer, AudioMixerGroup group)
        {
            return Groups(mixer).FirstOrDefault(candidate => Children(candidate).Contains(group));
        }

        // A group by name or by its path from Master ("Master/Music/Voice").
        private static AudioMixerGroup FindGroup(AudioMixer mixer, string key)
        {
            var matches = Groups(mixer).Where(group => AssetViews.KeyIs(key, group.name, GroupPath(mixer, group))).ToList();
            if (matches.Count != 1)
                throw new ArgumentException((matches.Count == 0 ? "Group was not found: " : "Group name is ambiguous, use its path: ") + key +
                    ". Groups: " + string.Join(", ", Groups(mixer).Select(group => GroupPath(mixer, group))));
            return matches[0];
        }

        private static string GroupSummary(AudioMixer mixer, AudioMixerGroup group)
        {
            var flags = new[] { "mute", "solo", "bypassEffects" }.Where(flag => (bool)Get(group, flag))
                .Select(flag => flag == "bypassEffects" ? "bypass" : flag);
            return Number(Volume(mixer, group)) + " dB" + string.Concat(flags.Select(flag => ", " + flag)) +
                " | " + string.Join(", ", Effects(group).Select(EffectName).ToArray());
        }

        private static JsonText GroupDetails(AudioMixer mixer, AudioMixerGroup group)
        {
            var snapshot = TargetSnapshot(mixer);
            var result = new JsonText()
                .Add("Snapshot", snapshot.name)
                .Add("Volume", Number(Volume(mixer, group)) + " dB")
                .Add("Pitch", Number((float)Call(group, "GetValueForPitch", mixer, snapshot) * 100f) + " %")
                .Add("Mute", Get(group, "mute"))
                .Add("Solo", Get(group, "solo"))
                .Add("Bypass Effects", Get(group, "bypassEffects"));
            foreach (var effect in Effects(group))
            {
                if ((bool)Call(effect, "IsAttenuation"))
                    continue;
                var fields = new JsonText().AddIf((bool)Get(effect, "bypass"), "Bypass", true);
                if ((bool)Call(effect, "IsSend"))
                    fields.Add("Target", SendTargetName(mixer, (UnityEngine.Object)Get(effect, "sendTarget")))
                        .Add("Send Level", Number((float)Call(effect, "GetValueForMixLevel", mixer, TargetSnapshot(mixer))) + " dB");
                foreach (var parameter in EffectParameters(effect))
                    fields.Add(parameter.name, ParameterDisplay(mixer, effect, parameter));
                result.Add(UniqueKey(result, EffectName(effect)), fields);
            }
            return result;
        }

        private static float Volume(AudioMixer mixer, AudioMixerGroup group)
        {
            return (float)Call(group, "GetValueForVolume", mixer, TargetSnapshot(mixer));
        }

        private static string SetGroupField(AudioMixer mixer, AudioMixerGroup group, string field, string value)
        {
            var snapshot = TargetSnapshot(mixer);
            if (AssetViews.KeyIs(field, "Volume"))
            {
                Undo.RecordObject(snapshot, "Change Volume Fader");
                Call(group, "SetValueForVolume", mixer, snapshot, Float(value));
                return group.name + ".Volume = " + Number(Volume(mixer, group)) + " dB";
            }
            if (AssetViews.KeyIs(field, "Pitch"))
            {
                Undo.RecordObject(snapshot, "Change Pitch");
                Call(group, "SetValueForPitch", mixer, snapshot, Float(value) / 100f);
                return group.name + ".Pitch = " + Number((float)Call(group, "GetValueForPitch", mixer, snapshot) * 100f) + " %";
            }
            var flag = AssetViews.KeyIs(field, "Mute") ? "mute" : AssetViews.KeyIs(field, "Solo") ? "solo" : AssetViews.KeyIs(field, "Bypass Effects", "Bypass") ? "bypassEffects" : null;
            if (flag == null)
                throw new ArgumentException(group.name + " has Volume, Pitch, Mute, Solo, Bypass Effects and effects " +
                    string.Join(", ", Effects(group).Select(EffectName)) + "; got " + field);
            Undo.RecordObject(group, "Change " + field);
            Set(group, flag, AssetViews.Bool(value));
            Call(mixer, flag == "bypassEffects" ? "UpdateBypass" : "UpdateMuteSolo");
            return group.name + "." + field + " = " + AssetViews.Printable(Get(group, flag));
        }

        // ---------- effects ----------

        private static IEnumerable<UnityEngine.Object> Effects(AudioMixerGroup group)
        {
            return ((IEnumerable)Get(group, "effects")).Cast<UnityEngine.Object>();
        }

        private static string EffectName(UnityEngine.Object effect)
        {
            return (string)Get(effect, "effectName");
        }

        private static UnityEngine.Object FindEffect(AudioMixerGroup group, string name)
        {
            var effect = Effects(group).FirstOrDefault(item => AssetViews.KeyIs(name, EffectName(item)));
            if (effect == null)
                throw new ArgumentException(group.name + " has no effect " + name + ". Effects: " + string.Join(", ", Effects(group).Select(EffectName)));
            return effect;
        }

        private sealed class EffectParameter
        {
            internal string name;
            internal string units;
            internal float displayScale;
        }

        private static IEnumerable<EffectParameter> EffectParameters(UnityEngine.Object effect)
        {
            var definitions = (IEnumerable)CallStatic("MixerEffectDefinitions", "GetEffectParameters", EffectName(effect));
            foreach (var definition in definitions ?? new object[0])
            {
                var type = definition.GetType();
                var scale = Convert.ToSingle(type.GetField("displayScale", Any).GetValue(definition), CultureInfo.InvariantCulture);
                yield return new EffectParameter
                {
                    name = (string)type.GetField("name", Any).GetValue(definition),
                    units = (string)type.GetField("units", Any).GetValue(definition),
                    displayScale = scale == 0f ? 1f : scale
                };
            }
        }

        private static string ParameterDisplay(AudioMixer mixer, UnityEngine.Object effect, EffectParameter parameter)
        {
            var value = (float)Call(effect, "GetValueForParameter", mixer, TargetSnapshot(mixer), parameter.name) * parameter.displayScale;
            return Number(value) + (string.IsNullOrEmpty(parameter.units) ? string.Empty : " " + parameter.units);
        }

        private static string SetEffectField(AudioMixer mixer, AudioMixerGroup group, string effectName, string field, string value)
        {
            var effect = FindEffect(group, effectName);
            var label = group.name + "." + EffectName(effect) + "." + field;
            if (AssetViews.KeyIs(field, "Bypass"))
            {
                Undo.RecordObject(effect, "Bypass Effect");
                Set(effect, "bypass", AssetViews.Bool(value));
                Call(mixer, "UpdateBypass");
                return label + " = " + AssetViews.Printable(Get(effect, "bypass"));
            }
            if (AssetViews.KeyIs(field, "Target") && (bool)Call(effect, "IsSend"))
            {
                // A send targets the Receive or Duck Volume effect of another group, written as asset-info shows it: "World (Duck Volume)".
                string receiverName = null;
                var open = value.LastIndexOf(" (", StringComparison.Ordinal);
                if (open > 0 && value.EndsWith(")", StringComparison.Ordinal))
                {
                    receiverName = value.Substring(open + 2, value.Length - open - 3);
                    value = value.Substring(0, open);
                }
                var targetGroup = FindGroup(mixer, value);
                var receiver = Effects(targetGroup).FirstOrDefault(item => ((bool)Call(item, "IsReceive") || (bool)Call(item, "IsDuckVolume")) &&
                    (receiverName == null || AssetViews.KeyIs(receiverName, EffectName(item))));
                if (receiver == null)
                    throw new InvalidOperationException(targetGroup.name + " has no Receive or Duck Volume effect to send to.");
                Undo.RecordObject(effect, "Change Send Target");
                Set(effect, "sendTarget", receiver);
                return label + " = " + SendTargetName(mixer, receiver);
            }
            if (AssetViews.KeyIs(field, "Send Level") && (bool)Call(effect, "IsSend"))
            {
                Undo.RecordObject(TargetSnapshot(mixer), "Change Send Level");
                Call(effect, "SetValueForMixLevel", mixer, TargetSnapshot(mixer), Float(value));
                // The mix level reads back only after the mixer updates; asset-info shows it then.
                return label + " = " + Number(Float(value)) + " dB";
            }
            var parameter = EffectParameters(effect).FirstOrDefault(item => AssetViews.KeyIs(field, item.name));
            if (parameter == null)
                throw new ArgumentException(EffectName(effect) + " parameters: " + string.Join(", ", EffectParameters(effect).Select(item => item.name)) + ", Bypass");
            Undo.RecordObject(TargetSnapshot(mixer), "Change Effect Parameter");
            Call(effect, "SetValueForParameter", mixer, TargetSnapshot(mixer), parameter.name, Float(value) / parameter.displayScale);
            return label + " = " + ParameterDisplay(mixer, effect, parameter);
        }

        private static string SendTargetName(AudioMixer mixer, UnityEngine.Object receiver)
        {
            if (receiver == null)
                return "None";
            var group = Groups(mixer).FirstOrDefault(candidate => Effects(candidate).Contains(receiver));
            return group == null ? EffectName(receiver) : group.name + " (" + EffectName(receiver) + ")";
        }

        private static string AddEffect(AudioMixer mixer, AudioMixerGroup group, string name, string index)
        {
            var available = ((IEnumerable)CallStatic("MixerEffectDefinitions", "GetEffectList")).Cast<string>().ToArray();
            var effectName = available.FirstOrDefault(item => AssetViews.KeyIs(name, item));
            if (effectName == null)
                throw new ArgumentException("Effect was not found: " + name + ". Effects: " + string.Join(", ", available));
            var effectType = EditorAssembly.GetType("UnityEditor.Audio.AudioMixerEffectController", true);
            var position = index == null ? Effects(group).Count() : Mathf.Clamp(AssetViews.Int(index), 0, Effects(group).Count());
            Undo.RecordObject(group, "Add effect");
            var effect = (UnityEngine.Object)Activator.CreateInstance(effectType, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, null, new object[] { effectName }, null);
            Call(group, "InsertEffect", effect, position);
            AssetDatabase.AddObjectToAsset(effect, mixer);
            Call(effect, "PreallocateGUIDs");
            return "Effect added: " + effectName + " to " + group.name + " at " + position;
        }

        // ---------- snapshots ----------

        private static IEnumerable<AudioMixerSnapshot> Snapshots(AudioMixer mixer)
        {
            return ((IEnumerable)Get(mixer, "snapshots")).Cast<AudioMixerSnapshot>();
        }

        private static string SnapshotLabel(AudioMixerSnapshot snapshot, bool start, bool editing)
        {
            var marks = new[] { start ? "start" : null, editing ? "editing" : null }.Where(mark => mark != null).ToArray();
            return marks.Length == 0 ? snapshot.name : snapshot.name + " (" + string.Join(", ", marks) + ")";
        }

        private static AudioMixerSnapshot TargetSnapshot(AudioMixer mixer)
        {
            return (AudioMixerSnapshot)Get(mixer, "TargetSnapshot");
        }

        private static AudioMixerSnapshot FindSnapshot(AudioMixer mixer, string name)
        {
            var snapshot = Snapshots(mixer).FirstOrDefault(item => AssetViews.KeyIs(name, item.name));
            if (snapshot == null)
                throw new ArgumentException("Snapshot was not found: " + name + ". Snapshots: " + string.Join(", ", Snapshots(mixer).Select(item => item.name)));
            return snapshot;
        }

        // ---------- exposed parameters ----------

        private static IEnumerable<object> ExposedParameters(AudioMixer mixer)
        {
            return ((IEnumerable)Get(mixer, "exposedParameters")).Cast<object>();
        }

        private static string ExposedName(object parameter)
        {
            return (string)parameter.GetType().GetField("name").GetValue(parameter);
        }

        private static object ExposedGuid(object parameter)
        {
            return parameter.GetType().GetField("guid").GetValue(parameter);
        }

        // "Music.Volume", "Music.Pitch" or "Music.<Effect>.<Parameter>", as the "Expose ... to script" context menu.
        private static string Expose(AudioMixer mixer, string key, string name)
        {
            var parts = key.Split('.');
            if (parts.Length < 2)
                throw new ArgumentException("parameter is <Group>.Volume, <Group>.Pitch or <Group>.<Effect>.<Parameter>.");
            var group = FindGroup(mixer, parts[0]);
            object path;
            if (parts.Length == 2)
            {
                var guid = AssetViews.KeyIs(parts[1], "Volume") ? Call(group, "GetGUIDForVolume")
                    : AssetViews.KeyIs(parts[1], "Pitch") ? Call(group, "GetGUIDForPitch")
                    : throw new ArgumentException("Group parameters are Volume and Pitch: " + key);
                path = Activator.CreateInstance(EditorAssembly.GetType("UnityEditor.Audio.AudioGroupParameterPath", true), group, guid);
            }
            else
            {
                var effect = FindEffect(group, parts[1]);
                var parameter = EffectParameters(effect).FirstOrDefault(item => AssetViews.KeyIs(parts[2], item.name));
                if (parameter == null)
                    throw new ArgumentException(EffectName(effect) + " parameters: " + string.Join(", ", EffectParameters(effect).Select(item => item.name)));
                path = Activator.CreateInstance(EditorAssembly.GetType("UnityEditor.Audio.AudioEffectParameterPath", true), group, effect, Call(effect, "GetGUIDForParameter", parameter.name));
            }
            var before = ExposedParameters(mixer).Select(ExposedName).ToList();
            Undo.RecordObject(mixer, "Expose Mixer Parameter");
            Call(mixer, "AddExposedParameter", path);
            var added = ExposedParameters(mixer).Select(ExposedName).FirstOrDefault(item => !before.Contains(item));
            if (added == null)
                throw new InvalidOperationException("Already exposed: " + key);
            return "Parameter exposed: " + (string.IsNullOrEmpty(name) ? added : RenameExposed(mixer, added, name).Split('=')[1].Trim());
        }

        private static string RenameExposed(AudioMixer mixer, string name, string newName)
        {
            var parameters = (Array)Get(mixer, "exposedParameters");
            for (var index = 0; index < parameters.Length; index++)
            {
                var parameter = parameters.GetValue(index);
                if (!AssetViews.KeyIs(name, ExposedName(parameter)))
                    continue;
                if (ExposedParameters(mixer).Any(item => ExposedName(item) == newName))
                    throw new InvalidOperationException("Exposed parameter already exists: " + newName);
                Undo.RecordObject(mixer, "Rename Exposed Parameter");
                parameter.GetType().GetField("name").SetValue(parameter, newName);
                parameters.SetValue(parameter, index);
                Set(mixer, "exposedParameters", parameters);
                Call(mixer, "OnChangedExposedParameter");
                return "Exposed." + name + " = " + newName;
            }
            throw new ArgumentException("Exposed parameter was not found: " + name);
        }

        // ---------- shared ----------

        private static void ShowWindow()
        {
            EditorPresentationService.NextUpdate(() => EditorPresentationService.ShowWindow("UnityEditor.AudioMixerWindow", "UnityEditor.AnimationWindow", "UnityEditor.ConsoleWindow"));
        }

        private static void Saved(AudioMixer mixer)
        {
            EditorUtility.SetDirty(mixer);
            foreach (var snapshot in Snapshots(mixer))
                EditorUtility.SetDirty(snapshot);
            foreach (var group in Groups(mixer))
                EditorUtility.SetDirty(group);
            AssetDatabase.SaveAssets();
        }

        private static string UniqueKey(JsonText json, string key)
        {
            var result = key;
            for (var index = 2; json.Contains(result); index++)
                result = key + " " + index;
            return result;
        }

        private static float Float(string value)
        {
            return float.Parse(value.Replace("dB", string.Empty).Replace("%", string.Empty).Trim(), NumberStyles.Float, CultureInfo.InvariantCulture);
        }

        private static string Number(float value)
        {
            return Math.Round(value, 2).ToString("0.##", CultureInfo.InvariantCulture);
        }

        private static object Get(object target, string property)
        {
            return target.GetType().GetProperty(property, Any).GetValue(target, null);
        }

        private static void Set(object target, string property, object value)
        {
            target.GetType().GetProperty(property, Any).SetValue(target, value, null);
        }

        private static object Call(object target, string method, params object[] arguments)
        {
            var info = target.GetType().GetMethods(Any).FirstOrDefault(candidate => candidate.Name == method && candidate.GetParameters().Length == arguments.Length);
            if (info == null)
                throw new MissingMethodException(target.GetType().FullName, method);
            return info.Invoke(target, arguments);
        }

        private static object CallStatic(string type, string method, params object[] arguments)
        {
            var owner = EditorAssembly.GetType("UnityEditor.Audio." + type, true);
            var info = owner.GetMethods(Any).FirstOrDefault(candidate => candidate.Name == method && candidate.GetParameters().Length == arguments.Length);
            if (info == null)
                throw new MissingMethodException(owner.FullName, method);
            return info.Invoke(null, arguments);
        }
    }
}
