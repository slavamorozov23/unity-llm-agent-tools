using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEngine;

namespace UnityAgentBridge.Editor
{
    // game_actions frames: while a batch runs, small copies of the Game View stay on the GPU; at the end only the frames
    // picked evenly over its real time are read back, so recording barely slows the game. Like slow motion footage, a wait
    // with a low timeScale takes more real time and so gets more of the frames; a high timeScale skims.
    [InitializeOnLoad]
    internal static class GameClipRecorder
    {
        // A script reload forgets the list but not the GPU copies, so they are released first.
        static GameClipRecorder()
        {
            AssemblyReloadEvents.beforeAssemblyReload += () =>
            {
                EditorApplication.update -= Record;
                Discard();
            };
        }

        [Serializable]
        private sealed class Clip
        {
            public string[] screenshots;
            public float[] times;
        }

        private const int Width = 384;
        private const int Limit = 256;
        private const float FirstStep = 1f / 30f;
        private sealed class Frame
        {
            internal float time;
            internal float realTime;
            internal RenderTexture texture;
        }

        private static readonly List<Frame> Frames = new List<Frame>();
        private static float step;
        private static float next;

        internal static string Start()
        {
            EditorApplication.update -= Record;
            Discard();
            step = FirstStep;
            next = float.MinValue;
            EditorApplication.update += Record;
            return "Recording.";
        }

        internal static string Stop(int count)
        {
            EditorApplication.update -= Record;
            try
            {
                var picked = Pick(count);
                var directory = Path.Combine(BridgePaths.RuntimeRoot, "Screenshots");
                Directory.CreateDirectory(directory);
                foreach (var old in Directory.GetFiles(directory, "clip-*.png"))
                    File.Delete(old);
                var paths = new List<string>();
                for (var index = 0; index < picked.Count; index++)
                {
                    var path = Path.Combine(directory, "clip-" + (index + 1) + ".png");
                    var texture = GameInteractionService.ReadTexture(picked[index].texture);
                    try
                    {
                        File.WriteAllBytes(path, texture.EncodeToPNG());
                    }
                    finally
                    {
                        UnityEngine.Object.DestroyImmediate(texture);
                    }
                    paths.Add(path);
                }
                return JsonUtility.ToJson(new Clip { screenshots = paths.ToArray(), times = picked.Select(item => item.time).ToArray() });
            }
            finally
            {
                Discard();
            }
        }

        private static void Record()
        {
            if (!EditorApplication.isPlaying)
            {
                EditorApplication.update -= Record;
                Discard();
                return;
            }
            if (EditorApplication.isPaused || Time.unscaledTime < next)
                return;
            var source = GameInteractionService.GameViewTexture();
            if (source == null || source.width <= 0 || source.height <= 0)
                return;
            var descriptor = source.descriptor;
            descriptor.width = Width;
            descriptor.height = Mathf.Max(1, Mathf.RoundToInt(Width * (float)source.height / source.width));
            descriptor.depthBufferBits = 0;
            descriptor.msaaSamples = 1;
            descriptor.useMipMap = false;
            var copy = new RenderTexture(descriptor) { hideFlags = HideFlags.HideAndDontSave };
            Graphics.Blit(source, copy);
            Frames.Add(new Frame { time = Time.time, realTime = Time.unscaledTime, texture = copy });
            next = Time.unscaledTime + step;
            if (Frames.Count < Limit)
                return;
            // A long batch keeps every other frame and records half as often, so its frames stay evenly spaced.
            for (var index = Frames.Count - 1; index > 0; index -= 2)
            {
                Frames[index].texture.Release();
                UnityEngine.Object.DestroyImmediate(Frames[index].texture);
                Frames.RemoveAt(index);
            }
            step *= 2f;
        }

        private static List<Frame> Pick(int count)
        {
            if (Frames.Count == 0)
                throw new InvalidOperationException("The game rendered no frames while the actions ran.");
            var first = Frames[0].realTime;
            var last = Frames[Frames.Count - 1].realTime;
            var picked = new List<Frame>();
            for (var index = 0; index < count; index++)
            {
                var target = count == 1 ? last : first + (last - first) * index / (count - 1);
                var nearest = Frames.OrderBy(item => Mathf.Abs(item.realTime - target)).First();
                if (!picked.Contains(nearest))
                    picked.Add(nearest);
            }
            return picked;
        }

        private static void Discard()
        {
            foreach (var frame in Frames)
            {
                if (frame.texture == null)
                    continue;
                frame.texture.Release();
                UnityEngine.Object.DestroyImmediate(frame.texture);
            }
            Frames.Clear();
        }
    }
}
