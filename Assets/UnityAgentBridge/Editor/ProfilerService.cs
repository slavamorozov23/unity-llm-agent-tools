using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using UnityEditor;
using UnityEditor.Profiling;
using UnityEditorInternal;
using UnityEngine;

namespace UnityAgentBridge.Editor
{
    internal static class ProfilerService
    {
        private const string GameRoot = "PlayerLoop";
        private const string EditorRoot = "EditorLoop";
        private const string FirstKey = "UnityAgentBridge.ProfilerFirst";
        private const string LastKey = "UnityAgentBridge.ProfilerLast";
        private const string RaisedKey = "UnityAgentBridge.ProfilerFrameCount";
        private const string EditModeKey = "UnityAgentBridge.ProfilerEditMode";
        private const int MaxFrameCount = 4000;
        // Preferences > Analysis > Profiler > Frame Count.
        private static readonly System.Reflection.PropertyInfo FrameCountSetting = typeof(ProfilerDriver).Assembly
            .GetType("UnityEditor.Profiling.ProfilerUserSettings")?.GetProperty("frameCount", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic);
        // The CPU Usage chart groups of the Profiler window; colours come from the matching Unity category.
        private static readonly string[] Groups = { "Rendering", "Scripts", "Physics", "Animation", "GarbageCollector", "VSync", "Global Illumination", "UI", "Others" };
        private static readonly string[] GroupColors = { "Render", "Scripts", "Physics", "Animation", "GC", "VSync", "Lighting", "GUI", "Other" };
        private static readonly string[] Counters = { "SetPass Calls Count", "Batches Count", "Triangles Count", "Vertices Count", "Total Used Memory", "GC Used Memory" };
        private static readonly string[] CounterNames = { "setPass", "batches", "tris", "verts", "usedMb", "gcUsedMb" };

        // editMode is the Profiler window's target dropdown: Edit Mode records the editor's own work under EditorLoop too.
        public static string Start(int frames, bool editMode = false)
        {
            if (!EditorApplication.isPlaying)
                throw new InvalidOperationException("Profiler capture needs Play Mode.");
            SessionState.SetInt(RaisedKey, RaiseFrameCount(frames));
            Application.runInBackground = true;
            // The Profiler window opens after recording: in front of the Game View it would stop the game rendering.
            GameInteractionService.GetGameView(false).Focus();
            ProfilerDriver.ClearAllFrames();
            ProfilerDriver.profileEditor = editMode;
            ProfilerDriver.enabled = true;
            SessionState.SetInt(FirstKey, ProfilerDriver.lastFrameIndex + 1);
            SessionState.SetInt(LastKey, -1);
            SessionState.SetBool(EditModeKey, editMode);
            EditorApplication.update -= RepaintGameView;
            EditorApplication.update += RepaintGameView;
            return "Profiler recording.";
        }

        // A longer capture than the Profiler keeps raises the Frame Count preference, as a developer would.
        private static int RaiseFrameCount(int frames)
        {
            // Recording stops a few frames late; the margin keeps the first frames in the buffer.
            frames = Math.Min(MaxFrameCount, (frames + 199) / 100 * 100);
            if (FrameCountSetting == null || frames <= (int)FrameCountSetting.GetValue(null))
                return 0;
            FrameCountSetting.SetValue(null, frames);
            typeof(ProfilerDriver).GetMethod("SetMaxFrameHistoryLength", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic)
                ?.Invoke(null, new object[] { frames });
            return frames;
        }

        // An unfocused editor renders the Game View only on demand; a capture needs every frame rendered.
        private static void RepaintGameView()
        {
            if (!EditorApplication.isPlaying || !ProfilerDriver.enabled)
            {
                // The game stopped before the capture was read: recording would go on through every later play session.
                ProfilerDriver.enabled = false;
                EditorApplication.update -= RepaintGameView;
                return;
            }
            if (!EditorApplication.isPaused)
                GameInteractionService.GetGameView(false).Repaint();
        }

        // frames <= 0 stops right away (game_actions profile).
        public static string Read(int frames, out bool pending)
        {
            var first = SessionState.GetInt(FirstKey, -1);
            if (first < 0 || SessionState.GetInt(LastKey, -1) >= 0 || !ProfilerDriver.enabled)
                throw new InvalidOperationException("Profiler capture was not started.");
            var recorded = ProfilerDriver.lastFrameIndex - first + 1;
            pending = frames > 0 && recorded < frames;
            if (pending)
                return Math.Max(0, recorded).ToString(CultureInfo.InvariantCulture);
            ProfilerDriver.enabled = false;
            EditorApplication.update -= RepaintGameView;
            SessionState.SetInt(LastKey, frames > 0 ? Math.Min(ProfilerDriver.lastFrameIndex, first + frames - 1) : ProfilerDriver.lastFrameIndex);
            return Summary();
        }

        // Edit Mode records the editor instead of the game: its frames are EditorLoop, without PlayerLoop.
        private static string Root
        {
            get { return SessionState.GetBool(EditModeKey, false) ? EditorRoot : GameRoot; }
        }

        private static string Summary()
        {
            var samples = Samples(null);
            if (samples.Count == 0)
                throw new InvalidOperationException("Profiler recorded no game frames.");
            var json = new StringBuilder("{");
            json.Append("\"frames\":").Append(samples.Count);
            var dropped = ProfilerDriver.firstFrameIndex - SessionState.GetInt(FirstKey, 0);
            if (dropped > 0)
                json.Append(",\"dropped\":").Append(dropped);
            var raised = SessionState.GetInt(RaisedKey, 0);
            if (raised > 0)
                json.Append(",\"frameCount\":").Append(raised);
            json.Append(",\"avgMs\":").Append(Number(samples.Average(item => item.gameMs)));
            json.Append(",\"maxMs\":").Append(Number(samples.Max(item => item.gameMs)));
            // The whole frame as the frame rate sees it, and the editor's share of it in Play Mode.
            var frameTimes = samples.Where(item => item.frameMs > 0).Select(item => (double)item.frameMs).ToArray();
            if (frameTimes.Length > 0)
                json.Append(",\"frameMs\":").Append(Number(frameTimes.Average()));
            var editorMs = samples.Average(item => item.editorMs);
            if (editorMs >= 0.05)
                json.Append(",\"editorMs\":").Append(Number(editorMs));
            json.Append(",\"gcKb\":").Append(Number(samples.Sum(item => item.gcBytes) / 1024f));
            json.Append(",\"categories\":{");
            var averages = Groups.Select((group, index) => new KeyValuePair<string, double>(group, samples.Average(item => item.categories[index])))
                .Where(item => item.Value >= 0.05).OrderByDescending(item => item.Value).ToArray();
            for (var index = 0; index < averages.Length; index++)
                Property(json, index, averages[index].Key, averages[index].Value);
            json.Append('}');
            var counters = new StringBuilder();
            for (var index = 0; index < Counters.Length; index++)
            {
                var values = samples.Where(item => item.counters[index] >= 0).Select(item => (double)item.counters[index]).ToArray();
                if (values.Length > 0)
                    Property(counters, counters.Length == 0 ? 0 : 1, CounterNames[index], index >= 4 ? values.Max() : Math.Round(values.Average()));
            }
            if (counters.Length > 0)
                json.Append(",\"counters\":{").Append(counters).Append('}');
            json.Append(",\"worst\":[");
            var worst = samples.OrderByDescending(item => item.gameMs).Take(5).ToArray();
            SelectFrame(worst[0].index, null, 0, null);
            for (var index = 0; index < worst.Length; index++)
            {
                var sample = worst[index];
                if (index > 0)
                    json.Append(',');
                json.Append("{\"frame\":").Append(sample.frame)
                    .Append(",\"ms\":").Append(Number(sample.gameMs))
                    .Append(",\"gcKb\":").Append(Number(sample.gcBytes / 1024f))
                    .Append(",\"top\":{");
                var top = TopMarkers(sample.index);
                for (var marker = 0; marker < top.Length; marker++)
                    Property(json, marker, top[marker].Key, top[marker].Value);
                json.Append("}}");
            }
            return json.Append("]}").ToString();
        }

        public static string Frames(string marker)
        {
            var samples = Samples(string.IsNullOrWhiteSpace(marker) ? null : marker.Trim());
            if (samples.Count == 0)
                throw new InvalidOperationException("Profiler capture has no game frames.");
            var json = new StringBuilder("{\"categories\":[");
            var colors = GroupColorValues(samples[0].index);
            for (var index = 0; index < Groups.Length; index++)
            {
                if (index > 0)
                    json.Append(',');
                json.Append("{\"name\":");
                Text(json, Groups[index]);
                json.Append(",\"color\":\"#").Append(colors[index].r.ToString("x2")).Append(colors[index].g.ToString("x2")).Append(colors[index].b.ToString("x2")).Append("\"}");
            }
            json.Append("],\"counters\":[");
            json.Append(string.Join(",", CounterNames.Select(name => "\"" + name + "\"")));
            json.Append("],\"frames\":[");
            for (var index = 0; index < samples.Count; index++)
            {
                var sample = samples[index];
                if (index > 0)
                    json.Append(',');
                json.Append('[').Append(sample.frame).Append(',').Append(Number(sample.gameMs)).Append(",[");
                json.Append(string.Join(",", sample.categories.Select(value => Number(value))));
                json.Append("],").Append(Number(sample.gcBytes / 1024f)).Append(',').Append(Number(sample.markerMs)).Append(",[");
                json.Append(string.Join(",", sample.counters.Select(value => Number(value))));
                json.Append("]]");
            }
            return json.Append("]}").ToString();
        }

        public static string Hierarchy(string frameSpec, string thread, string query, string path, string sort, int limit)
        {
            var samples = Samples(null);
            if (samples.Count == 0)
                throw new InvalidOperationException("Profiler capture has no game frames.");
            FrameSample[] frames;
            var spec = string.IsNullOrWhiteSpace(frameSpec) ? "worst" : frameSpec.Trim().ToLowerInvariant();
            if (spec == "all")
                frames = samples.ToArray();
            else if (spec == "worst")
                frames = new[] { samples.OrderByDescending(item => item.gameMs).First() };
            else
            {
                int relative;
                if (!int.TryParse(spec, NumberStyles.Integer, CultureInfo.InvariantCulture, out relative))
                    throw new ArgumentException("--frame must be a frame number, worst or all.", "frame");
                var match = samples.FirstOrDefault(item => item.frame == relative);
                if (match == null)
                    throw new ArgumentOutOfRangeException("frame", "Frame " + relative + " is not in the capture (0.." + samples[samples.Count - 1].frame + ").");
                frames = new[] { match };
            }

            var threadIndex = ThreadIndex(frames[0].index, thread);
            var column = SortColumn(sort);
            var rows = new Dictionary<string, Row>(StringComparer.Ordinal);
            var needle = string.IsNullOrWhiteSpace(query) ? null : query.Trim();
            var scope = string.IsNullOrWhiteSpace(path) ? null : path.Trim().Trim('/');
            string threadName = null;
            foreach (var frame in frames)
            {
                using (var view = ProfilerDriver.GetHierarchyFrameDataView(frame.index, threadIndex,
                    HierarchyFrameDataView.ViewModes.MergeSamplesWithTheSameName, HierarchyFrameDataView.columnTotalTime, false))
                {
                    if (!view.valid)
                        continue;
                    threadName = view.threadName;
                    var parent = scope == null ? view.GetRootItemID() : Find(view, scope);
                    if (parent < 0)
                        continue;
                    var items = new List<int>();
                    if (needle == null)
                        view.GetItemChildren(parent, items);
                    else
                        Search(view, parent, needle, items);
                    foreach (var item in items)
                    {
                        var key = view.GetItemPath(item);
                        Row row;
                        if (!rows.TryGetValue(key, out row))
                            rows[key] = row = new Row { path = ShortPath(view, item), frame = frame.index };
                        row.total += view.GetItemColumnDataAsFloat(item, HierarchyFrameDataView.columnTotalTime);
                        row.self += view.GetItemColumnDataAsFloat(item, HierarchyFrameDataView.columnSelfTime);
                        row.calls += view.GetItemColumnDataAsFloat(item, HierarchyFrameDataView.columnCalls);
                        row.gc += view.GetItemColumnDataAsFloat(item, HierarchyFrameDataView.columnGcMemory);
                        row.children |= view.HasItemChildren(item);
                        Objects(view, item, row.objects);
                    }
                }
            }
            if (scope != null && rows.Count == 0 && needle == null && !frames.Any(frame => HasPath(frame.index, threadIndex, scope)))
                throw new InvalidOperationException("Profiler item was not found: " + scope);

            var count = frames.Length;
            var ordered = rows.Values.OrderByDescending(row => column(row)).Take(Math.Max(1, limit)).ToArray();
            if (ordered.Length > 0)
                SelectFrame(ordered[0].frame, needle, threadIndex, ordered[0].path);
            else
                SelectFrame(frames[0].index, needle, threadIndex, null);

            var json = new StringBuilder("{\"frame\":");
            if (frames.Length == 1)
                json.Append(frames[0].frame);
            else
                json.Append("\"all\"");
            if (threadIndex != 0)
            {
                json.Append(",\"thread\":");
                Text(json, threadName);
            }
            json.Append(",\"items\":[");
            for (var index = 0; index < ordered.Length; index++)
            {
                var row = ordered[index];
                if (index > 0)
                    json.Append(',');
                json.Append("{\"path\":");
                Text(json, row.path);
                if (row.total > 0.005f * count)
                    json.Append(",\"total\":").Append(Number(row.total / count));
                if (row.self > 0.005f * count)
                    json.Append(",\"self\":").Append(Number(row.self / count));
                json.Append(",\"calls\":").Append(Number(row.calls / count));
                if (row.gc > 0)
                    json.Append(",\"gcKb\":").Append(Number(row.gc / count / 1024f));
                if (row.children && needle == null)
                    json.Append(",\"children\":true");
                if (row.objects.Count > 0)
                {
                    json.Append(",\"objects\":[");
                    var objects = row.objects.Take(3).ToArray();
                    for (var item = 0; item < objects.Length; item++)
                    {
                        if (item > 0)
                            json.Append(',');
                        Text(json, objects[item]);
                    }
                    json.Append(']');
                    if (row.objects.Count > 3)
                        json.Append(",\"objectCount\":").Append(row.objects.Count);
                }
                json.Append('}');
            }
            return json.Append("]}").ToString();
        }

        private sealed class FrameSample
        {
            public int frame;
            public int index;
            public float gameMs;
            public float editorMs;
            public float frameMs;
            public float gcBytes;
            public float markerMs;
            public float[] categories;
            public float[] counters;
        }

        private sealed class Row
        {
            public string path;
            public int frame;
            public float total;
            public float self;
            public float calls;
            public float gc;
            public bool children;
            public readonly HashSet<string> objects = new HashSet<string>(StringComparer.Ordinal);
        }

        // Frames of the last capture that ran the game loop (paused frames only run the editor).
        private static List<FrameSample> Samples(string marker)
        {
            var first = SessionState.GetInt(FirstKey, -1);
            var last = SessionState.GetInt(LastKey, -1);
            if (first < 0 || last < 0)
                throw new InvalidOperationException("No profiler capture: run profiler or game_actions with profile first.");
            var samples = new List<FrameSample>();
            int[] groups = null;
            for (var frame = Math.Max(first, ProfilerDriver.firstFrameIndex); frame <= Math.Min(last, ProfilerDriver.lastFrameIndex); frame++)
            {
                using (var view = View(frame))
                {
                    if (!view.valid)
                        continue;
                    var game = Child(view, view.GetRootItemID(), Root);
                    if (game < 0)
                        continue;
                    if (groups == null)
                        groups = GroupMap(view);
                    var editor = Root == GameRoot ? Child(view, view.GetRootItemID(), EditorRoot) : -1;
                    var sample = new FrameSample
                    {
                        frame = frame - first,
                        index = frame,
                        gameMs = view.GetItemColumnDataAsFloat(game, HierarchyFrameDataView.columnTotalTime),
                        editorMs = editor < 0 ? 0 : view.GetItemColumnDataAsFloat(editor, HierarchyFrameDataView.columnTotalTime),
                        frameMs = FrameTime(frame),
                        gcBytes = view.GetItemColumnDataAsFloat(game, HierarchyFrameDataView.columnGcMemory),
                        categories = new float[Groups.Length],
                        counters = CounterValues(frame)
                    };
                    Walk(view, game, marker, sample, groups);
                    samples.Add(sample);
                }
            }
            return samples;
        }

        private static void Walk(HierarchyFrameDataView view, int root, string marker, FrameSample sample, int[] groups)
        {
            var pending = new Stack<KeyValuePair<int, bool>>();
            var children = new List<int>();
            pending.Push(new KeyValuePair<int, bool>(root, false));
            while (pending.Count > 0)
            {
                var entry = pending.Pop();
                var item = entry.Key;
                var category = view.GetItemCategoryIndex(item);
                sample.categories[category < groups.Length ? groups[category] : Groups.Length - 1] +=
                    view.GetItemColumnDataAsFloat(item, HierarchyFrameDataView.columnSelfTime);
                var inside = entry.Value;
                if (!inside && marker != null && view.GetItemName(item).IndexOf(marker, StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    sample.markerMs += view.GetItemColumnDataAsFloat(item, HierarchyFrameDataView.columnTotalTime);
                    inside = true;
                }
                children.Clear();
                view.GetItemChildren(item, children);
                foreach (var child in children)
                    pending.Push(new KeyValuePair<int, bool>(child, inside));
            }
        }

        private static int[] GroupMap(FrameDataView view)
        {
            var categories = new List<ProfilerCategoryInfo>();
            view.GetAllCategories(categories);
            var map = Enumerable.Repeat(Groups.Length - 1, categories.Count == 0 ? 0 : categories.Max(item => item.id) + 1).ToArray();
            foreach (var category in categories)
            {
                switch ((category.name ?? string.Empty).ToLowerInvariant())
                {
                    case "render": map[category.id] = 0; break;
                    case "scripts": map[category.id] = 1; break;
                    case "physics": case "physics2d": map[category.id] = 2; break;
                    case "animation": map[category.id] = 3; break;
                    case "gc": map[category.id] = 4; break;
                    case "vsync": map[category.id] = 5; break;
                    case "lighting": map[category.id] = 6; break;
                    case "gui": case "ui": case "uidetails": map[category.id] = 7; break;
                }
            }
            return map;
        }

        private static Color32[] GroupColorValues(int frame)
        {
            using (var view = View(frame))
            {
                var categories = new List<ProfilerCategoryInfo>();
                view.GetAllCategories(categories);
                return GroupColors.Select(name => categories.Where(item => string.Equals(item.name, name, StringComparison.OrdinalIgnoreCase))
                    .Select(item => item.color).DefaultIfEmpty(new Color32(128, 128, 128, 255)).First()).ToArray();
            }
        }

        // Frame Timing's CPU Total Frame Time: from one frame's start to the next, in milliseconds; 0 when not recorded.
        private static float FrameTime(int frame)
        {
            using (var raw = ProfilerDriver.GetRawFrameDataView(frame, 0))
            {
                var id = raw.valid ? raw.GetMarkerId("CPU Total Frame Time") : -1;
                return id >= 0 && raw.HasCounterValue(id) ? raw.GetCounterValueAsLong(id) / 1e6f : 0f;
            }
        }

        // -1 marks a counter this capture did not record.
        private static float[] CounterValues(int frame)
        {
            var values = Enumerable.Repeat(-1f, Counters.Length).ToArray();
            using (var raw = ProfilerDriver.GetRawFrameDataView(frame, 0))
            {
                if (!raw.valid)
                    return values;
                for (var index = 0; index < Counters.Length; index++)
                {
                    var id = raw.GetMarkerId(Counters[index]);
                    if (id < 0 || !raw.HasCounterValue(id))
                        continue;
                    var value = raw.GetCounterValueAsLong(id);
                    values[index] = index >= 4 ? value / (1024f * 1024f) : value;
                }
            }
            return values;
        }

        private static int ThreadIndex(int frame, string thread)
        {
            if (string.IsNullOrWhiteSpace(thread) || thread.Trim().Equals("main", StringComparison.OrdinalIgnoreCase))
                return 0;
            var names = new List<string>();
            for (var index = 0; ; index++)
            {
                using (var raw = ProfilerDriver.GetRawFrameDataView(frame, index))
                {
                    if (!raw.valid)
                        break;
                    if (raw.threadName.IndexOf(thread.Trim(), StringComparison.OrdinalIgnoreCase) >= 0)
                        return index;
                    names.Add(raw.threadName);
                }
            }
            throw new InvalidOperationException("Profiler thread was not found: " + thread + ". Threads: " + string.Join(", ", names.Distinct().Take(12)));
        }

        private static Func<Row, float> SortColumn(string sort)
        {
            switch (string.IsNullOrWhiteSpace(sort) ? "total" : sort.Trim().ToLowerInvariant())
            {
                case "total":
                    return row => row.total;
                case "self":
                    return row => row.self;
                case "gc":
                    return row => row.gc;
                case "calls":
                    return row => row.calls;
                default:
                    throw new ArgumentException("--sort must be total, self, gc or calls.", "sort");
            }
        }

        private static int Find(HierarchyFrameDataView view, string path)
        {
            var item = view.GetRootItemID();
            var children = new List<int>();
            foreach (var segment in path.Split('/'))
            {
                children.Clear();
                view.GetItemChildren(item, children);
                var next = -1;
                foreach (var child in children)
                    if (view.GetItemName(child) == segment || Marker(view.GetItemName(child)) == segment)
                    {
                        next = child;
                        break;
                    }
                if (next < 0)
                    return -1;
                item = next;
            }
            return item;
        }

        // Paths use the short marker names of the summary ("RenderPipelineManager.DoRenderLoop_Internal", not
        // "…dll!UnityEngine.Rendering::RenderPipelineManager.DoRenderLoop_Internal() [Invoke]"); --path takes both.
        private static string ShortPath(HierarchyFrameDataView view, int item)
        {
            var ancestors = new List<int>();
            view.GetItemAncestors(item, ancestors);
            var root = view.GetRootItemID();
            return string.Join("/", ancestors.Where(id => id != root).OrderBy(view.GetItemDepth).Append(item)
                .Select(id => Marker(view.GetItemName(id))));
        }

        private static bool HasPath(int frame, int thread, string path)
        {
            using (var view = ProfilerDriver.GetHierarchyFrameDataView(frame, thread,
                HierarchyFrameDataView.ViewModes.MergeSamplesWithTheSameName, HierarchyFrameDataView.columnTotalTime, false))
                return view.valid && Find(view, path) >= 0;
        }

        private static void Search(HierarchyFrameDataView view, int root, string needle, List<int> found)
        {
            var pending = new Stack<int>();
            var children = new List<int>();
            pending.Push(root);
            while (pending.Count > 0)
            {
                var item = pending.Pop();
                if (item != root && view.GetItemName(item).IndexOf(needle, StringComparison.OrdinalIgnoreCase) >= 0)
                    found.Add(item);
                children.Clear();
                view.GetItemChildren(item, children);
                foreach (var child in children)
                    pending.Push(child);
            }
        }

        // Samples of MonoBehaviour callbacks carry their object, like the Profiler's selection details.
        private static void Objects(HierarchyFrameDataView view, int item, HashSet<string> objects)
        {
            var ids = new List<EntityId>();
            view.GetItemMergedSamplesEntityId(item, ids);
            foreach (var id in ids.Distinct())
            {
                var value = EditorUtility.EntityIdToObject(id);
                var component = value as Component;
                var gameObject = component != null ? component.gameObject : value as GameObject;
                if (gameObject == null)
                    continue;
                // DontDestroyOnLoad objects and those of scenes unloaded since the capture have no path the other commands could use.
                try
                {
                    objects.Add(ScenePath.For(gameObject) + (component != null ? "#" + component.GetType().Name : string.Empty));
                }
                catch (InvalidOperationException)
                {
                }
            }
        }

        private static void SelectFrame(int frame, string search, int thread, string path)
        {
            var window = EditorPresentationService.ShowWindow("UnityEditor.ProfilerWindow") as ProfilerWindow;
            if (window == null)
                return;
            window.selectedFrameIndex = frame;
            var controller = window.GetFrameTimeViewSampleSelectionController(ProfilerWindow.cpuModuleIdentifier);
            if (controller == null)
                return;
            controller.focusedThreadIndex = thread;
            controller.sampleNameSearchFilter = search ?? string.Empty;
            if (path == null)
                return;
            using (var view = ProfilerDriver.GetHierarchyFrameDataView(frame, thread,
                HierarchyFrameDataView.ViewModes.MergeSamplesWithTheSameName, HierarchyFrameDataView.columnTotalTime, false))
            {
                // Item ids exist only after their parents are expanded in this view.
                var item = view.valid ? Find(view, path) : -1;
                if (item < 0)
                    return;
                var raw = new List<int>();
                view.GetItemRawFrameDataViewIndices(item, raw);
                if (raw.Count == 0)
                    return;
                controller.SetSelection(new ProfilerTimeSampleSelection(frame, view.threadGroupName, view.threadName, view.threadId, raw[0], view.GetItemName(item)));
            }
        }

        private static HierarchyFrameDataView View(int frame)
        {
            return ProfilerDriver.GetHierarchyFrameDataView(frame, 0,
                HierarchyFrameDataView.ViewModes.MergeSamplesWithTheSameName, HierarchyFrameDataView.columnTotalTime, false);
        }

        private static int Child(HierarchyFrameDataView view, int parent, string name)
        {
            var children = new List<int>();
            view.GetItemChildren(parent, children);
            foreach (var child in children)
                if (view.GetItemName(child) == name)
                    return child;
            return -1;
        }

        private static KeyValuePair<string, float>[] TopMarkers(int frame)
        {
            var totals = new Dictionary<string, float>(StringComparer.Ordinal);
            using (var view = View(frame))
            {
                var game = Child(view, view.GetRootItemID(), Root);
                if (game < 0)
                    return Array.Empty<KeyValuePair<string, float>>();
                var pending = new Stack<int>();
                var children = new List<int>();
                pending.Push(game);
                while (pending.Count > 0)
                {
                    var item = pending.Pop();
                    var name = Marker(view.GetItemName(item));
                    float current;
                    totals.TryGetValue(name, out current);
                    totals[name] = current + view.GetItemColumnDataAsFloat(item, HierarchyFrameDataView.columnSelfTime);
                    children.Clear();
                    view.GetItemChildren(item, children);
                    foreach (var child in children)
                        pending.Push(child);
                }
            }
            return totals.OrderByDescending(item => item.Value).Take(3).ToArray();
        }

        private static string Marker(string name)
        {
            var library = name.IndexOf(".dll!", StringComparison.Ordinal);
            if (library >= 0)
                name = name.Substring(library + 5);
            var bracket = name.IndexOf(" [", StringComparison.Ordinal);
            if (bracket > 0)
                name = name.Substring(0, bracket);
            name = name.Replace("()", string.Empty);
            var scope = name.LastIndexOf("::", StringComparison.Ordinal);
            if (scope >= 0)
                name = name.Substring(scope + 2);
            var parts = name.Split('.');
            return parts.Length > 2 ? parts[parts.Length - 2] + "." + parts[parts.Length - 1] : name;
        }

        private static void Property(StringBuilder json, int index, string name, double value)
        {
            if (index > 0)
                json.Append(',');
            Text(json, name);
            json.Append(':').Append(Number(value));
        }

        private static void Text(StringBuilder json, string value)
        {
            if (value == null)
            {
                json.Append("null");
                return;
            }
            json.Append('"').Append(value.Replace("\\", "\\\\").Replace("\"", "\\\"")).Append('"');
        }

        private static string Number(double value)
        {
            return Math.Round(value, 2).ToString("R", CultureInfo.InvariantCulture);
        }
    }
}
