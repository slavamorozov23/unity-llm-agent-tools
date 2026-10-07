using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using UnityEditor;
using UnityEngine;
using UnityEngine.Audio;
using UnityEngine.Playables;

namespace UnityAgentBridge.Editor
{
    // game_actions sounds: the agent cannot hear the game, so a batch lists the sounds that started while it ran — an
    // AudioSource that began or restarted playing, a Timeline audio clip the playhead entered.
    [InitializeOnLoad]
    internal static class GameSoundLog
    {
        static GameSoundLog()
        {
            AssemblyReloadEvents.beforeAssemblyReload += () => EditorApplication.update -= Sample;
        }

        [Serializable]
        private sealed class Result
        {
            public string[] sounds;
        }

        private sealed class Heard
        {
            internal string name;
            internal string source;
            internal int count;
            internal float time;
        }

        private const int Shown = 12;
        private static readonly Dictionary<int, KeyValuePair<bool, int>> Sources = new Dictionary<int, KeyValuePair<bool, int>>();
        private static readonly HashSet<string> ActiveClips = new HashSet<string>();
        private static readonly HashSet<AudioSource> TimelineSources = new HashSet<AudioSource>();
        private static readonly List<Heard> HeardSounds = new List<Heard>();
        private static AudioSource[] cachedSources = Array.Empty<AudioSource>();
        private static double nextScan;
        private static bool primed;

        internal static string Start()
        {
            EditorApplication.update -= Sample;
            Sources.Clear();
            ActiveClips.Clear();
            HeardSounds.Clear();
            nextScan = 0d;
            primed = false;
            EditorApplication.update += Sample;
            // Sounds already playing when the batch starts are not reported as started.
            Sample();
            primed = true;
            return "Listening.";
        }

        internal static string Stop()
        {
            EditorApplication.update -= Sample;
            Sample();
            var lines = HeardSounds.Take(Shown).Select(item => item.name + (item.count > 1 ? " ×" + item.count : string.Empty) +
                " t=" + item.time.ToString("0.##", CultureInfo.InvariantCulture) + " (" + item.source + ")").ToList();
            if (HeardSounds.Count > Shown)
                lines.Add("+" + (HeardSounds.Count - Shown) + " more");
            HeardSounds.Clear();
            return JsonUtility.ToJson(new Result { sounds = lines.ToArray() });
        }

        private static void Sample()
        {
            if (!EditorApplication.isPlaying)
            {
                EditorApplication.update -= Sample;
                return;
            }
            if (EditorApplication.isPaused && primed)
                return;
            if (EditorApplication.timeSinceStartup >= nextScan)
            {
                cachedSources = UnityEngine.Object.FindObjectsByType<AudioSource>(FindObjectsInactive.Exclude, FindObjectsSortMode.None);
                nextScan = EditorApplication.timeSinceStartup + 0.5d;
            }
            SampleTimelines();
            foreach (var source in cachedSources)
            {
                // A source a Timeline plays through is reported by its clips.
                if (source == null || TimelineSources.Contains(source))
                    continue;
                var id = source.GetInstanceID();
                var playing = source.isPlaying;
                var samples = playing ? source.timeSamples : 0;
                KeyValuePair<bool, int> previous;
                var known = Sources.TryGetValue(id, out previous);
                // Play() on a source still playing starts it over: its position jumps back (a loop wrapping is not a start).
                if (primed && playing && (!known || !previous.Key || samples < previous.Value && !source.loop))
                    Record(source.clip != null ? source.clip.name : source.resource != null ? source.resource.name : "AudioSource", source.gameObject);
                Sources[id] = new KeyValuePair<bool, int>(playing, samples);
            }
        }

        // Timeline plays its audio clips through the director's graph, not through AudioSource.Play: a clip is heard
        // while its AudioClipPlayable is playing.
        private static void SampleTimelines()
        {
            var active = new HashSet<string>();
            TimelineSources.Clear();
            foreach (var director in UnityEngine.Object.FindObjectsByType<PlayableDirector>(FindObjectsInactive.Exclude, FindObjectsSortMode.None))
            {
                if (director.state != PlayState.Playing || !director.playableGraph.IsValid())
                    continue;
                var graph = director.playableGraph;
                for (var index = 0; index < graph.GetOutputCount(); index++)
                {
                    var output = graph.GetOutput(index);
                    if (!output.IsPlayableOutputOfType<AudioPlayableOutput>())
                        continue;
                    var target = ((AudioPlayableOutput)output).GetTarget();
                    if (target != null)
                        TimelineSources.Add(target);
                    var root = output.GetSourcePlayable();
                    var port = output.GetSourceOutputPort();
                    if (!root.IsValid() || port < 0 || port >= root.GetInputCount() || root.GetInputWeight(port) <= 0f)
                        continue;
                    Walk(root.GetInput(port), director.GetInstanceID() + "/" + index, target, active);
                }
            }
            ActiveClips.Clear();
            ActiveClips.UnionWith(active);
        }

        private static void Walk(Playable playable, string key, AudioSource target, HashSet<string> active)
        {
            if (!playable.IsValid())
                return;
            if (playable.GetPlayableType() == typeof(AudioClipPlayable))
            {
                if (playable.GetPlayState() != PlayState.Playing)
                    return;
                active.Add(key);
                var clip = ((AudioClipPlayable)playable).GetClip();
                if (primed && !ActiveClips.Contains(key) && clip != null)
                    Record(clip.name, target != null ? target.gameObject : null);
                return;
            }
            for (var index = 0; index < playable.GetInputCount(); index++)
                Walk(playable.GetInput(index), key + "/" + index, target, active);
        }

        private static void Record(string name, GameObject owner)
        {
            var source = owner == null ? "Timeline" : ScenePath.For(owner);
            var scene = source.IndexOf('/', 1);
            if (owner != null && scene > 0)
                source = source.Substring(scene + 1);
            var heard = HeardSounds.FirstOrDefault(item => item.name == name && item.source == source);
            if (heard == null)
                HeardSounds.Add(new Heard { name = name, source = source, count = 1, time = Time.time });
            else
                heard.count++;
        }
    }
}
