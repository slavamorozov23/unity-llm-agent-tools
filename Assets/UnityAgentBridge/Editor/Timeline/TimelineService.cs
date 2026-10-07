using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEditor.Timeline;
using UnityEngine;
using UnityEngine.Playables;
using UnityEngine.Timeline;

namespace UnityAgentBridge.Editor
{
    [InitializeOnLoad]
    internal static class TimelineService
    {
        private static readonly HashSet<string> HiddenFields = new HashSet<string>(StringComparer.Ordinal)
        {
            "m_Script", "m_Name", "m_Clips", "m_Children", "m_Parent", "m_Markers", "m_Version", "m_AnimClip",
            "m_Curves", "m_Time", "m_EditorClassIdentifier", "m_CustomPlayableFullTypename", "m_InfiniteClipOffsetPosition",
            "m_InfiniteClipOffsetEulerAngles", "m_InfiniteClipTimeOffset", "m_InfiniteClipPreExtrapolation",
            "m_InfiniteClipPostExtrapolation", "m_InfiniteClipRemoveOffset", "m_InfiniteClipApplyFootIK", "mInfiniteClipLoop"
        };

        static TimelineService()
        {
            CommandProcessor.Timeline = Execute;
        }

        private static string Execute(BridgeRequest request)
        {
            switch (request.command)
            {
                case "list-timelines":
                    return List();
                case "get-timeline":
                    return Describe(request);
                case "mutate-timeline-track":
                    return MutateTrack(request);
                case "mutate-timeline-clip":
                    return MutateClip(request);
                case "mutate-timeline-marker":
                    return MutateMarker(request);
                case "execute-component-action":
                    return Control(request);
                default:
                    throw new InvalidOperationException("Unknown Timeline command: " + request.command);
            }
        }

        private static string List()
        {
            var json = new StringBuilder("{\"directors\":[");
            var first = true;
            foreach (var scene in ScenePath.ContextScenes())
            {
                foreach (var director in scene.GetRootGameObjects().SelectMany(root => root.GetComponentsInChildren<PlayableDirector>(true)))
                {
                    if (!first)
                        json.Append(',');
                    first = false;
                    json.Append("{\"path\":");
                    Text(json, ScenePath.For(director.gameObject));
                    json.Append(",\"timeline\":");
                    Text(json, director.playableAsset == null ? null : AssetDatabase.GetAssetPath(director.playableAsset));
                    json.Append('}');
                }
            }
            return json.Append("]}").ToString();
        }

        private static string Describe(BridgeRequest request)
        {
            PlayableDirector director;
            var timeline = Source(request, out director);
            Present(director, timeline);
            return Info(timeline, director, null);
        }

        private static string MutateTrack(BridgeRequest request)
        {
            PlayableDirector director;
            var timeline = Source(request, out director);
            TrackAsset track;
            switch (request.action)
            {
                case "create":
                    if (string.IsNullOrWhiteSpace(request.componentType))
                        throw new ArgumentException("Track type is required: Animation, Audio, Activation, Signal, Control, Group, Playable or a Track class.", "type");
                    var parent = string.IsNullOrWhiteSpace(request.destinationPath) ? null : FindTrack(timeline, request.destinationPath);
                    if (parent != null && !(parent is GroupTrack))
                        throw new InvalidOperationException("Parent track must be a Group track: " + request.destinationPath);
                    track = timeline.CreateTrack(ResolveType<TrackAsset>(request.componentType, "Track"), parent, request.track);
                    try
                    {
                        ApplyTrack(track, request.values, director);
                    }
                    catch
                    {
                        timeline.DeleteTrack(track);
                        throw;
                    }
                    break;
                case "modify":
                    track = FindTrack(timeline, request.track);
                    Undo.RecordObject(track, "Unity Agent Bridge: Timeline Track");
                    ApplyTrack(track, request.values, director);
                    break;
                case "delete":
                    track = FindTrack(timeline, request.track);
                    if (!timeline.DeleteTrack(track))
                        throw new InvalidOperationException("Timeline did not delete track " + request.track);
                    Save(timeline);
                    Present(director, timeline);
                    return Info(timeline, director, null);
                default:
                    throw new ArgumentException("timeline-track action must be create, modify or delete.", "action");
            }
            Save(timeline);
            Present(director, timeline);
            return Info(timeline, director, track);
        }

        private static string MutateClip(BridgeRequest request)
        {
            PlayableDirector director;
            var timeline = Source(request, out director);
            var track = FindTrack(timeline, request.track);
            TimelineClip clip;
            switch (request.action)
            {
                case "create":
                    Undo.RecordObject(track, "Unity Agent Bridge: Timeline Clip");
                    if (!string.IsNullOrWhiteSpace(request.clip))
                    {
                        var source = AssetDatabase.LoadMainAssetAtPath(request.clip);
                        if (source == null)
                            throw new InvalidOperationException("Clip asset was not found: " + request.clip);
                        if (track is AnimationTrack && source is AnimationClip)
                            clip = ((AnimationTrack)track).CreateClip((AnimationClip)source);
                        else if (track is AudioTrack && source is AudioClip)
                            clip = ((AudioTrack)track).CreateClip((AudioClip)source);
                        else
                            throw new InvalidOperationException("--asset takes an AnimationClip for Animation tracks or an AudioClip for Audio tracks.");
                    }
                    else
                        clip = track.CreateDefaultClip();
                    if (!string.IsNullOrWhiteSpace(request.name))
                        clip.displayName = request.name;
                    try
                    {
                        ApplyClip(clip, request.values, director);
                    }
                    catch
                    {
                        // A rejected --set leaves nothing behind, so repeating the command does not make a second clip.
                        timeline.DeleteClip(clip);
                        throw;
                    }
                    break;
                case "modify":
                    clip = FindClip(track, request.name);
                    Undo.RecordObject(track, "Unity Agent Bridge: Timeline Clip");
                    ApplyClip(clip, request.values, director);
                    break;
                case "delete":
                    clip = FindClip(track, request.name);
                    if (!timeline.DeleteClip(clip))
                        throw new InvalidOperationException("Timeline did not delete clip " + request.name);
                    break;
                default:
                    throw new ArgumentException("timeline-clip action must be create, modify or delete.", "action");
            }
            EditorUtility.SetDirty(track);
            Save(timeline);
            Present(director, timeline);
            return Info(timeline, director, track);
        }

        private static void ApplyClip(TimelineClip clip, PropertyValue[] values, PlayableDirector director)
        {
            var rest = new List<PropertyValue>();
            foreach (var value in values ?? Array.Empty<PropertyValue>())
            {
                switch (Key(value.path))
                {
                    case "start":
                        clip.start = Number(value);
                        break;
                    case "duration":
                        clip.duration = Number(value);
                        break;
                    case "clipin":
                        clip.clipIn = Number(value);
                        break;
                    case "easein":
                    case "easeinduration":
                        clip.easeInDuration = Number(value);
                        break;
                    case "easeout":
                    case "easeoutduration":
                        clip.easeOutDuration = Number(value);
                        break;
                    case "speed":
                    case "timescale":
                        clip.timeScale = Number(value);
                        break;
                    case "name":
                    case "displayname":
                        clip.displayName = Plain(value);
                        break;
                    // The clip Inspector's Pre-Extrapolate / Post-Extrapolate (the setters are internal to Timeline).
                    case "preextrapolation":
                    case "preextrapolate":
                    case "postextrapolation":
                    case "postextrapolate":
                        TimelineClip.ClipExtrapolation mode;
                        if (!Enum.TryParse(Plain(value), true, out mode))
                            throw new ArgumentException(value.path + ": " + string.Join(", ", Enum.GetNames(typeof(TimelineClip.ClipExtrapolation))), "values");
                        if (!clip.clipCaps.HasFlag(ClipCaps.Extrapolation))
                            throw new InvalidOperationException("Clip " + clip.displayName + " does not extrapolate.");
                        typeof(TimelineClip).GetProperty(Key(value.path).StartsWith("pre", StringComparison.Ordinal) ? "preExtrapolationMode" : "postExtrapolationMode")
                            .GetSetMethod(true).Invoke(clip, new object[] { mode });
                        break;
                    default:
                        rest.Add(value);
                        break;
                }
            }
            if (rest.Count > 0)
            {
                if (clip.asset == null)
                    throw new InvalidOperationException("Clip has no playable asset for: " + string.Join(", ", rest.Select(item => item.path).ToArray()));
                Undo.RecordObject(clip.asset, "Unity Agent Bridge: Timeline Clip");
                Apply(clip.asset, rest.ToArray(), director);
            }
        }

        private static string MutateMarker(BridgeRequest request)
        {
            PlayableDirector director;
            var timeline = Source(request, out director);
            TrackAsset track;
            if (string.IsNullOrWhiteSpace(request.track))
            {
                if (timeline.markerTrack == null && request.action == "create")
                    timeline.CreateMarkerTrack();
                track = timeline.markerTrack;
                if (track == null)
                    throw new InvalidOperationException("Timeline has no Markers track.");
            }
            else
                track = FindTrack(timeline, request.track);
            var markers = track.GetMarkers().ToArray();
            var values = request.values ?? Array.Empty<PropertyValue>();
            var time = values.FirstOrDefault(value => Key(value.path) == "time");
            var rest = values.Where(value => Key(value.path) != "time").ToArray();
            IMarker marker;
            switch (request.action)
            {
                case "create":
                    if (time == null)
                        throw new ArgumentException("Marker create needs --set time=SECONDS.", "values");
                    Undo.RecordObject(track, "Unity Agent Bridge: Timeline Marker");
                    marker = track.CreateMarker(ResolveType<Marker>(string.IsNullOrWhiteSpace(request.componentType) ? "SignalEmitter" : request.componentType, "Emitter"), Number(time));
                    break;
                case "modify":
                    marker = Marker(markers, request.siblingIndex);
                    if (time != null)
                    {
                        Undo.RecordObject((UnityEngine.Object)marker, "Unity Agent Bridge: Timeline Marker");
                        marker.time = Number(time);
                    }
                    break;
                case "delete":
                    if (!track.DeleteMarker(Marker(markers, request.siblingIndex)))
                        throw new InvalidOperationException("Timeline did not delete the marker.");
                    EditorUtility.SetDirty(track);
                    Save(timeline);
                    Present(director, timeline);
                    return Info(timeline, director, track);
                default:
                    throw new ArgumentException("timeline-marker action must be create, modify or delete.", "action");
            }
            var markerObject = marker as UnityEngine.Object;
            if (rest.Length > 0)
            {
                try
                {
                    if (markerObject == null)
                        throw new InvalidOperationException("Marker has no serialized fields.");
                    Apply(markerObject, rest, director);
                }
                catch
                {
                    if (request.action == "create")
                        track.DeleteMarker(marker);
                    throw;
                }
            }
            if (markerObject != null)
                EditorUtility.SetDirty(markerObject);
            EditorUtility.SetDirty(track);
            Save(timeline);
            Present(director, timeline);
            return Info(timeline, director, track);
        }

        private static IMarker Marker(IMarker[] markers, int index)
        {
            if (index < 0 || index >= markers.Length)
                throw new ArgumentOutOfRangeException("marker", "Marker index must be inside 0.." + (markers.Length - 1) + ".");
            return markers[index];
        }

        // The binding lives on the director, but Timeline shows it in the track header.
        private static void ApplyTrack(TrackAsset track, PropertyValue[] values, PlayableDirector director)
        {
            values = values ?? Array.Empty<PropertyValue>();
            var binding = values.FirstOrDefault(value => Key(value.path) == "binding");
            Apply(track, values.Where(value => value != binding).ToArray(), director);
            if (binding == null)
                return;
            if (director == null)
                throw new InvalidOperationException("binding needs --path with the PlayableDirector object.");
            Undo.RecordObject(director, "Unity Agent Bridge: Timeline Binding");
            var target = Plain(binding);
            if (string.IsNullOrWhiteSpace(target) || target == "null")
                director.ClearGenericBinding(track);
            else
                director.SetGenericBinding(track, BindingTarget(track, target));
            EditorUtility.SetDirty(director);
            ScenePersistenceService.MarkDirty(director.gameObject.scene);
        }

        private static UnityEngine.Object BindingTarget(TrackAsset track, string path)
        {
            if (string.IsNullOrWhiteSpace(path))
                throw new ArgumentException("Binding object path is required.", "binding");
            var type = track.outputs.Select(output => output.outputTargetType).FirstOrDefault(item => item != null);
            if (type == null)
                throw new InvalidOperationException("Track " + track.name + " has no binding.");
            var separator = path.LastIndexOf('#');
            var gameObject = ScenePath.ResolveObject(separator < 0 ? path : path.Substring(0, separator));
            if (type == typeof(GameObject) || type == typeof(UnityEngine.Object))
                return gameObject;
            var component = gameObject.GetComponent(type);
            if (component == null)
                throw new InvalidOperationException(ScenePath.For(gameObject) + " has no " + type.Name + " for track " + track.name + ".");
            return component;
        }

        private static PlayableDirector resumeDirector;

        private static void PlayOnResume(PauseState state)
        {
            if (state != PauseState.Unpaused)
                return;
            EditorApplication.pauseStateChanged -= PlayOnResume;
            if (resumeDirector != null && EditorApplication.isPlaying)
                resumeDirector.Play();
            resumeDirector = null;
        }

        private static string Control(BridgeRequest request)
        {
            var gameObject = ScenePath.ResolveObject(request.path);
            var director = (PlayableDirector)ComponentService.ResolveAttachedComponent(gameObject, request.componentType, request.componentIndex);
            var timeValue = (request.values ?? Array.Empty<PropertyValue>()).FirstOrDefault(value => Key(value.path) == "time");
            if ((request.values ?? Array.Empty<PropertyValue>()).Any(value => Key(value.path) != "time"))
                throw new ArgumentException("PlayableDirector actions accept only --set time=SECONDS.", "values");
            double? time = timeValue == null ? (double?)null : Number(timeValue);
            var window = Present(director, director.playableAsset as TimelineAsset);
            if (EditorApplication.isPlaying)
            {
                if (time.HasValue)
                    director.time = time.Value;
                resumeDirector = null;
                switch ((request.action ?? string.Empty).ToLowerInvariant())
                {
                    case "play":
                        director.Play();
                        // Unity ignores Play while the game is paused (as it is between game_actions); it starts on resume.
                        if (EditorApplication.isPaused && director.state != PlayState.Playing)
                        {
                            resumeDirector = director;
                            EditorApplication.pauseStateChanged -= PlayOnResume;
                            EditorApplication.pauseStateChanged += PlayOnResume;
                            return "PlayableDirector plays when the game resumes, t=" + Seconds(director.time) + "/" + Seconds(director.duration) + ".";
                        }
                        break;
                    case "pause":
                        director.Pause();
                        break;
                    case "stop":
                        director.Stop();
                        break;
                    case "evaluate":
                        director.Evaluate();
                        break;
                    default:
                        throw new InvalidOperationException("PlayableDirector actions: play, pause, stop, evaluate.");
                }
                return "PlayableDirector " + director.state.ToString().ToLowerInvariant() + ", t=" + Seconds(director.time) + "/" + Seconds(director.duration) + ".";
            }

            var controls = window.playbackControls;
            switch ((request.action ?? string.Empty).ToLowerInvariant())
            {
                case "play":
                    if (time.HasValue)
                        controls.SetCurrentTime(time.Value);
                    controls.Play();
                    break;
                case "pause":
                    controls.Pause();
                    if (time.HasValue)
                        controls.SetCurrentTime(time.Value);
                    break;
                case "stop":
                    controls.Pause();
                    controls.GoToFirstFrame();
                    break;
                case "evaluate":
                    controls.SetCurrentTime(time ?? controls.GetCurrentTime());
                    break;
                default:
                    throw new InvalidOperationException("PlayableDirector actions: play, pause, stop, evaluate.");
            }
            window.Repaint();
            return "Timeline preview t=" + Seconds(controls.GetCurrentTime()) + "/" + Seconds(director.duration) + ".";
        }

        private static TimelineAsset Source(BridgeRequest request, out PlayableDirector director)
        {
            director = null;
            if (!string.IsNullOrWhiteSpace(request.path))
            {
                director = Director(request.path);
                var directed = director.playableAsset as TimelineAsset;
                if (directed == null)
                    throw new InvalidOperationException("PlayableDirector has no Timeline asset: " + request.path);
                if (!string.IsNullOrWhiteSpace(request.timeline) && Asset(request.timeline) != directed)
                    throw new InvalidOperationException("--timeline differs from the PlayableDirector asset.");
                return directed;
            }
            var timeline = Asset(request.timeline);
            director = DirectorFor(timeline);
            return timeline;
        }

        private static PlayableDirector Director(string path)
        {
            var gameObject = ScenePath.ResolveObject(path);
            var director = gameObject.GetComponent<PlayableDirector>();
            if (director == null)
                throw new InvalidOperationException("Object has no PlayableDirector: " + ScenePath.For(gameObject));
            return director;
        }

        private static TimelineAsset Asset(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
                throw new ArgumentException("Provide --path DIRECTOR or --timeline Assets/…/Name.playable.", "timeline");
            var timeline = AssetDatabase.LoadAssetAtPath<TimelineAsset>(path.Replace('\\', '/'));
            if (timeline == null)
                throw new InvalidOperationException("Timeline asset was not found: " + path);
            return timeline;
        }

        private static PlayableDirector DirectorFor(TimelineAsset timeline)
        {
            var directors = ScenePath.ContextScenes()
                .SelectMany(scene => scene.GetRootGameObjects())
                .SelectMany(root => root.GetComponentsInChildren<PlayableDirector>(true))
                .Where(director => director.playableAsset == timeline)
                .ToArray();
            return directors.Length == 1 ? directors[0] : null;
        }

        private static IEnumerable<TrackAsset> Tracks(TimelineAsset timeline)
        {
            if (timeline.markerTrack != null)
                yield return timeline.markerTrack;
            var pending = new Stack<TrackAsset>(timeline.GetRootTracks().Where(track => track != timeline.markerTrack).Reverse());
            while (pending.Count > 0)
            {
                var track = pending.Pop();
                yield return track;
                foreach (var child in track.GetChildTracks().Reverse())
                    pending.Push(child);
            }
        }

        private static TrackAsset FindTrack(TimelineAsset timeline, string label)
        {
            if (string.IsNullOrWhiteSpace(label))
                throw new ArgumentException("--track is required.", "track");
            var tracks = Tracks(timeline).ToArray();
            var match = tracks.FirstOrDefault(track => Label(track, tracks) == label) ??
                tracks.FirstOrDefault(track => string.Equals(Label(track, tracks), label, StringComparison.OrdinalIgnoreCase));
            if (match == null)
                throw new InvalidOperationException("Track was not found: " + label + ". Tracks: " +
                    string.Join(", ", tracks.Select(track => Label(track, tracks)).ToArray()));
            return match;
        }

        private static TimelineClip FindClip(TrackAsset track, string label)
        {
            var clips = track.GetClips().ToArray();
            int index;
            if (int.TryParse(label, NumberStyles.Integer, CultureInfo.InvariantCulture, out index) && index >= 0 && index < clips.Length)
                return clips[index];
            var match = clips.FirstOrDefault(clip => Label(clip, clips) == label) ??
                clips.FirstOrDefault(clip => string.Equals(Label(clip, clips), label, StringComparison.OrdinalIgnoreCase));
            if (match == null)
                throw new InvalidOperationException("Clip was not found on " + track.name + ": " + label + ". Clips: " +
                    string.Join(", ", clips.Select(clip => Label(clip, clips)).ToArray()));
            return match;
        }

        private static string Label(TrackAsset track, TrackAsset[] tracks)
        {
            var same = tracks.Where(item => item.name == track.name).ToList();
            return same.Count > 1 ? track.name + "[" + same.IndexOf(track) + "]" : track.name;
        }

        private static string Label(TimelineClip clip, TimelineClip[] clips)
        {
            var same = clips.Where(item => item.displayName == clip.displayName).ToList();
            return same.Count > 1 ? clip.displayName + "[" + same.IndexOf(clip) + "]" : clip.displayName;
        }

        private static Type ResolveType<T>(string name, string suffix)
        {
            var wanted = name.Trim();
            var types = TypeCache.GetTypesDerivedFrom<T>().Where(type => !type.IsAbstract).ToArray();
            var match = types.Where(type => type.FullName == wanted || type.Name == wanted).ToArray();
            if (match.Length == 0)
                match = types.Where(type => string.Equals(type.Name, wanted, StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(type.Name, wanted + suffix, StringComparison.OrdinalIgnoreCase)).ToArray();
            if (match.Length != 1)
                throw new InvalidOperationException((match.Length == 0 ? "Type was not found: " : "Type is ambiguous: ") + name + ". Available: " +
                    string.Join(", ", types.Select(type => type.Name).OrderBy(item => item).ToArray()));
            return match[0];
        }

        private static void Apply(UnityEngine.Object target, PropertyValue[] values, PlayableDirector director)
        {
            if (values == null || values.Length == 0)
                return;
            var serialized = new SerializedObject(target);
            foreach (var value in values)
            {
                if (Key(value.path) == "name")
                {
                    target.name = Plain(value);
                    continue;
                }
                var property = ComponentService.FindProperty(serialized, value.path) ?? StructField(serialized, value.path);
                if (property == null)
                    throw new InvalidOperationException(target.GetType().Name + " has no field " + value.path + ". Fields: " + string.Join(", ", Fields(serialized)) + ".");
                ComponentService.SetProperty(property, value.value, false, director);
            }
            serialized.ApplyModifiedProperties();
            EditorUtility.SetDirty(target);
        }

        private static bool IsStruct(SerializedProperty property)
        {
            return property.propertyType == SerializedPropertyType.Generic && !property.isArray && !ComponentService.IsExposedReference(property);
        }

        // Fields as timeline-info names them: struct fields by their own name.
        private static IEnumerable<string> Fields(SerializedObject serialized)
        {
            var iterator = serialized.GetIterator();
            var enter = true;
            while (iterator.NextVisible(enter))
            {
                enter = IsStruct(iterator);
                if (!enter && !HiddenFields.Contains(iterator.name))
                    yield return Display(iterator.name);
            }
        }

        private static SerializedProperty StructField(SerializedObject serialized, string path)
        {
            var iterator = serialized.GetIterator();
            var enter = true;
            while (iterator.NextVisible(enter))
            {
                enter = IsStruct(iterator);
                if (!enter && iterator.depth > 0 && Key(iterator.name) == Key(path))
                    return iterator.Copy();
            }
            return null;
        }

        private static void Save(TimelineAsset timeline)
        {
            EditorUtility.SetDirty(timeline);
            AssetDatabase.SaveAssetIfDirty(timeline);
        }

        private static TimelineEditorWindow Present(PlayableDirector director, TimelineAsset timeline)
        {
            if (director != null)
                EditorPresentationService.ShowComponent(director);
            else if (timeline != null)
                EditorPresentationService.ShowAsset(timeline);
            var window = TimelineEditor.GetOrCreateWindow();
            if (director != null && director.playableAsset is TimelineAsset)
                window.SetTimeline(director);
            else if (timeline != null)
                window.SetTimeline(timeline);
            window.Show();
            window.Focus();
            TimelineEditor.Refresh(RefreshReason.ContentsAddedOrRemoved | RefreshReason.ContentsModified);
            return window;
        }

        private static string Info(TimelineAsset timeline, PlayableDirector director, TrackAsset only)
        {
            var json = new StringBuilder("{\"timeline\":");
            Text(json, AssetDatabase.GetAssetPath(timeline));
            if (director != null)
            {
                json.Append(",\"director\":");
                Text(json, ScenePath.For(director.gameObject));
            }
            json.Append(",\"duration\":").Append(Seconds(timeline.duration));
            json.Append(",\"frameRate\":").Append(timeline.editorSettings.frameRate.ToString("R", CultureInfo.InvariantCulture));
            if (director != null && EditorApplication.isPlaying)
                json.Append(",\"state\":").Append(Quoted(director.state.ToString())).Append(",\"time\":").Append(Seconds(director.time));
            var tracks = Tracks(timeline).ToArray();
            if (only != null)
            {
                json.Append(",\"track\":");
                Track(json, only, tracks, director);
            }
            else
                json.Append(",\"tracks\":").Append(TracksJson(timeline, director));
            return json.Append('}').ToString();
        }

        internal static string TracksJson(TimelineAsset timeline, PlayableDirector director)
        {
            var tracks = Tracks(timeline).ToArray();
            var json = new StringBuilder("[");
            for (var index = 0; index < tracks.Length; index++)
            {
                if (index > 0)
                    json.Append(',');
                Track(json, tracks[index], tracks, director);
            }
            return json.Append(']').ToString();
        }

        private static void Track(StringBuilder json, TrackAsset track, TrackAsset[] tracks, PlayableDirector director)
        {
            json.Append("{\"name\":");
            Text(json, Label(track, tracks));
            json.Append(",\"type\":");
            Text(json, track.GetType().Name);
            var group = track.parent as GroupTrack;
            if (group != null)
            {
                json.Append(",\"group\":");
                Text(json, Label(group, tracks));
            }
            if (director != null)
            {
                var binding = director.GetGenericBinding(track);
                if (binding != null)
                {
                    json.Append(",\"binding\":");
                    Text(json, Reference(binding));
                }
            }
            Values(json, track, director);
            var clips = track.GetClips().ToArray();
            if (clips.Length > 0)
            {
                json.Append(",\"clips\":[");
                for (var index = 0; index < clips.Length; index++)
                {
                    var clip = clips[index];
                    if (index > 0)
                        json.Append(',');
                    json.Append("{\"name\":");
                    Text(json, Label(clip, clips));
                    json.Append(",\"start\":").Append(Seconds(clip.start)).Append(",\"duration\":").Append(Seconds(clip.duration));
                    if (clip.clipIn > 0) json.Append(",\"clipIn\":").Append(Seconds(clip.clipIn));
                    if (clip.easeInDuration > 0) json.Append(",\"easeIn\":").Append(Seconds(clip.easeInDuration));
                    if (clip.easeOutDuration > 0) json.Append(",\"easeOut\":").Append(Seconds(clip.easeOutDuration));
                    if (Math.Abs(clip.timeScale - 1d) > 1e-6) json.Append(",\"speed\":").Append(Seconds(clip.timeScale));
                    if (clip.preExtrapolationMode != TimelineClip.ClipExtrapolation.None) json.Append(",\"preExtrapolation\":").Append(Quoted(clip.preExtrapolationMode.ToString()));
                    if (clip.postExtrapolationMode != TimelineClip.ClipExtrapolation.None) json.Append(",\"postExtrapolation\":").Append(Quoted(clip.postExtrapolationMode.ToString()));
                    if (clip.asset != null)
                        Values(json, clip.asset, director);
                    json.Append('}');
                }
                json.Append(']');
            }
            var markers = track.GetMarkers().ToArray();
            if (markers.Length > 0)
            {
                json.Append(",\"markers\":[");
                for (var index = 0; index < markers.Length; index++)
                {
                    if (index > 0)
                        json.Append(',');
                    json.Append("{\"index\":").Append(index).Append(",\"type\":");
                    Text(json, markers[index].GetType().Name);
                    json.Append(",\"time\":").Append(Seconds(markers[index].time));
                    var markerObject = markers[index] as UnityEngine.Object;
                    if (markerObject != null)
                        Values(json, markerObject, director);
                    json.Append('}');
                }
                json.Append(']');
            }
            json.Append('}');
        }

        private static void Values(StringBuilder json, UnityEngine.Object target, PlayableDirector director)
        {
            var defaults = ScriptableObject.CreateInstance(target.GetType());
            try
            {
                var actual = new SerializedObject(target);
                var reference = new SerializedObject(defaults);
                var iterator = actual.GetIterator();
                var enter = true;
                var first = true;
                while (iterator.NextVisible(enter))
                {
                    enter = false;
                    if (HiddenFields.Contains(iterator.name))
                        continue;
                    // The clip Inspector draws a struct's fields as its own rows (Audio clip: m_ClipProperties.volume → volume).
                    if (IsStruct(iterator))
                    {
                        enter = true;
                        continue;
                    }
                    var baseline = reference.FindProperty(iterator.propertyPath);
                    if (baseline != null && SerializedProperty.DataEquals(iterator, baseline))
                        continue;
                    var value = Value(iterator, director);
                    if (value == null)
                        continue;
                    json.Append(first ? ",\"values\":{" : ",");
                    first = false;
                    Text(json, Display(iterator.name));
                    json.Append(':').Append(value);
                }
                if (!first)
                    json.Append('}');
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(defaults);
            }
        }

        private static string Value(SerializedProperty property, PlayableDirector director)
        {
            switch (property.propertyType)
            {
                case SerializedPropertyType.Boolean:
                    return property.boolValue ? "true" : "false";
                case SerializedPropertyType.Integer:
                    return property.longValue.ToString(CultureInfo.InvariantCulture);
                case SerializedPropertyType.Float:
                    return Seconds(property.doubleValue);
                case SerializedPropertyType.String:
                    return Quoted(property.stringValue);
                case SerializedPropertyType.Enum:
                    return property.enumValueIndex >= 0 && property.enumValueIndex < property.enumNames.Length
                        ? Quoted(property.enumNames[property.enumValueIndex])
                        : property.intValue.ToString(CultureInfo.InvariantCulture);
                case SerializedPropertyType.ObjectReference:
                    return Quoted(Reference(property.objectReferenceValue));
                case SerializedPropertyType.Vector2:
                case SerializedPropertyType.Vector3:
                case SerializedPropertyType.Vector4:
                case SerializedPropertyType.Color:
                case SerializedPropertyType.Quaternion:
                    return JsonUtility.ToJson(Boxed(property));
                case SerializedPropertyType.ExposedReference:
                case SerializedPropertyType.Generic:
                    if (!ComponentService.IsExposedReference(property))
                        return null;
                    var name = property.FindPropertyRelative("exposedName").stringValue;
                    UnityEngine.Object resolved = null;
                    bool valid;
                    if (director != null && !string.IsNullOrEmpty(name))
                        resolved = director.GetReferenceValue(name, out valid);
                    if (resolved == null)
                        resolved = property.FindPropertyRelative("defaultValue").objectReferenceValue;
                    return Quoted(Reference(resolved));
                default:
                    return null;
            }
        }

        private static object Boxed(SerializedProperty property)
        {
            switch (property.propertyType)
            {
                case SerializedPropertyType.Vector2: return property.vector2Value;
                case SerializedPropertyType.Vector3: return property.vector3Value;
                case SerializedPropertyType.Vector4: return property.vector4Value;
                case SerializedPropertyType.Color: return property.colorValue;
                default: return property.quaternionValue;
            }
        }

        private static string Reference(UnityEngine.Object value)
        {
            if (value == null)
                return null;
            var assetPath = AssetDatabase.GetAssetPath(value);
            if (!string.IsNullOrEmpty(assetPath))
                return AssetDatabase.IsMainAsset(value) ? assetPath : assetPath + "#" + value.name;
            var component = value as Component;
            var gameObject = component != null ? component.gameObject : value as GameObject;
            if (gameObject == null || !gameObject.scene.IsValid())
                return value.name;
            return ScenePath.For(gameObject) + (component != null ? "#" + component.GetType().Name : string.Empty);
        }

        private static string Display(string name)
        {
            if (name.StartsWith("m_", StringComparison.Ordinal) && name.Length > 2)
                return char.ToLowerInvariant(name[2]) + name.Substring(3);
            return name;
        }

        private static string Key(string path)
        {
            return (path ?? string.Empty).Replace("m_", string.Empty).Replace("_", string.Empty).ToLowerInvariant();
        }

        private static double Number(PropertyValue value)
        {
            return double.Parse(Plain(value), NumberStyles.Float, CultureInfo.InvariantCulture);
        }

        private static string Plain(PropertyValue value)
        {
            var text = (value.value ?? string.Empty).Trim();
            return text.Length >= 2 && text[0] == '"' && text[text.Length - 1] == '"' ? text.Substring(1, text.Length - 2) : text;
        }

        private static string Seconds(double value)
        {
            return Math.Round(value, 4).ToString("R", CultureInfo.InvariantCulture);
        }

        private static string Quoted(string value)
        {
            var json = new StringBuilder();
            Text(json, value);
            return json.ToString();
        }

        private static void Text(StringBuilder json, string value)
        {
            if (value == null)
            {
                json.Append("null");
                return;
            }
            json.Append('"');
            foreach (var character in value)
            {
                if (character == '"' || character == '\\')
                    json.Append('\\').Append(character);
                else if (character < ' ')
                    json.Append("\\u").Append(((int)character).ToString("x4"));
                else
                    json.Append(character);
            }
            json.Append('"');
        }
    }
}
