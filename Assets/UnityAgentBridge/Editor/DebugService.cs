using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using UnityEditor;
using UnityEditor.Compilation;
using UnityEngine;

namespace UnityAgentBridge.Editor
{
    [InitializeOnLoad]
    internal static class DebugService
    {
        private static readonly object FileLock = new object();
        private static readonly string SessionLogPath;
        private static readonly string CompilationLogPath;
        private static readonly List<LogData> CurrentCompilationErrors = new List<LogData>();
        private static readonly string BusyPath = Path.Combine(BridgePaths.RuntimeRoot, "busy.txt");
        private const string PlayStartedKey = "UnityAgentBridge.PlayStarted";
        private const string CompileStartedKey = "UnityAgentBridge.CompileStarted";
        private const string RunInBackgroundKey = "UnityAgentBridge.RunInBackground";
        private static string playModeStatus;

        [Serializable]
        private sealed class StatusData
        {
            public bool paused;
            public double time;
            public int frame;
            public float timeScale;
            public bool lightmapping;
            public bool occlusion;
            public float progress;
            public int errors;
            public string lastError;
            public int compileErrors;
            public string pausedBy;
        }

        [Serializable]
        private sealed class MarksData
        {
            public string play;
            public string compile;
        }

        static DebugService()
        {
            BridgePaths.EnsureRuntimeDirectories();
            var process = Process.GetCurrentProcess();
            SessionLogPath = Path.Combine(
                BridgePaths.RuntimeRoot,
                "Logs-" + process.Id + "-" + process.StartTime.ToUniversalTime().Ticks + ".jsonl");
            CompilationLogPath = Path.Combine(BridgePaths.RuntimeRoot, "CurrentCompilationErrors.json");
            playModeStatus = EditorApplication.isPlaying ? "игра запущена" : "игра оффлайн";
            Application.logMessageReceivedThreaded += OnLog;
            EditorApplication.playModeStateChanged += OnPlayModeStateChanged;
            CompilationPipeline.compilationStarted += OnCompilationStarted;
            CompilationPipeline.assemblyCompilationFinished += OnAssemblyCompilationFinished;
            EditorApplication.update += ClearBusyWhenIdle;
        }

        private static void ClearBusyWhenIdle()
        {
            if (EditorApplication.isCompiling || EditorApplication.isUpdating || !File.Exists(BusyPath))
                return;
            try
            {
                File.Delete(BusyPath);
            }
            catch (IOException)
            {
            }
        }

        public static void RequireCompiledScripts()
        {
            if (!EditorUtility.scriptCompilationFailed)
                return;
            var first = GetCurrentCompilationErrors().Select(item => item.message).FirstOrDefault();
            throw new InvalidOperationException("Scripts have compile errors" + (first == null ? "." : ": " + first));
        }

        public static string StatusJson()
        {
            var playing = EditorApplication.isPlaying;
            return JsonUtility.ToJson(new StatusData
            {
                paused = playing && EditorApplication.isPaused,
                time = playing ? Math.Round(Time.timeAsDouble, 3) : 0d,
                frame = playing ? Time.frameCount : 0,
                timeScale = playing ? Time.timeScale : 1f,
                lightmapping = Lightmapping.isRunning,
                occlusion = StaticOcclusionCulling.isRunning,
                progress = Lightmapping.isRunning ? Mathf.Round(Lightmapping.buildProgress * 100f) : 0f,
                errors = ErrorCount(),
                lastError = lastError,
                compileErrors = GetCurrentCompilationErrors().Length,
                pausedBy = playing && EditorApplication.isPaused ? GameInteractionService.PausedBy : string.Empty
            });
        }

        public static string Marks()
        {
            return JsonUtility.ToJson(new MarksData
            {
                play = SessionState.GetString(PlayStartedKey, string.Empty),
                compile = SessionState.GetString(CompileStartedKey, string.Empty)
            });
        }

        private static long errorScanPosition;
        private static readonly HashSet<string> ErrorMessages = new HashSet<string>(StringComparer.Ordinal);
        private static string lastError = string.Empty;

        // Distinct errors, as the Console counts them with Collapse: an error repeated every frame is one problem.
        // Status is polled several times a second, so only lines added since the last call are read.
        private static int ErrorCount()
        {
            lock (FileLock)
            {
                if (!File.Exists(SessionLogPath))
                {
                    TruncateLog(false);
                    return 0;
                }
                using (var stream = new FileStream(SessionLogPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                {
                    var length = stream.Length;
                    if (length < errorScanPosition)
                        TruncateLog(false);
                    stream.Seek(errorScanPosition, SeekOrigin.Begin);
                    using (var reader = new StreamReader(stream))
                    {
                        string line;
                        while ((line = reader.ReadLine()) != null)
                            if (line.Contains("\"type\":\"Error\"") || line.Contains("\"type\":\"Exception\"") || line.Contains("\"type\":\"Assert\""))
                            {
                                var entry = JsonUtility.FromJson<LogData>(line);
                                ErrorMessages.Add(entry.message);
                                lastError = ErrorLine(entry);
                            }
                    }
                    errorScanPosition = length;
                }
                return ErrorMessages.Count;
            }
        }

        // What the editor's status bar shows for the latest error: its first line, plus where it came from.
        private static string ErrorLine(LogData entry)
        {
            var message = (entry.message ?? string.Empty).Split('\n')[0].Trim();
            if (message.Length > 160)
                message = message.Substring(0, 160) + "…";
            var frame = (entry.stackTrace ?? string.Empty).Split('\n').Select(item => item.Trim()).FirstOrDefault(item => item.Length > 0 && !item.StartsWith("UnityEngine.Debug", StringComparison.Ordinal));
            if (string.IsNullOrEmpty(frame))
                return message;
            var call = frame.Split(' ')[0].Split(':')[0];
            var parts = call.Split('.');
            return message + " (" + string.Join(".", parts.Skip(Math.Max(0, parts.Length - 2)).ToArray()) + ")";
        }

        private static long lastErrorTicks;

        // The Console's Error Pause stops the game in the frame an error is logged.
        internal static bool ErrorJustPaused()
        {
            var flags = typeof(EditorApplication).Assembly.GetType("UnityEditor.LogEntries", false)?.GetProperty("consoleFlags", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
            var flagType = typeof(EditorApplication).Assembly.GetType("UnityEditor.ConsoleWindow+ConsoleFlags", false);
            if (flags == null || flagType == null || !Enum.IsDefined(flagType, "ErrorPause") ||
                ((int)flags.GetValue(null) & (int)Enum.Parse(flagType, "ErrorPause")) == 0)
                return false;
            return DateTime.UtcNow.Ticks - System.Threading.Interlocked.Read(ref lastErrorTicks) < TimeSpan.TicksPerSecond;
        }

        private static void TruncateLog(bool file = true)
        {
            if (file)
                File.WriteAllText(SessionLogPath, string.Empty);
            errorScanPosition = 0;
            ErrorMessages.Clear();
            lastError = string.Empty;
        }

        // The Console window's Clear on Play toggle; the bridge log clears with it.
        private static bool ConsoleClearsOnPlay()
        {
            var editor = typeof(EditorApplication).Assembly;
            var flags = editor.GetType("UnityEditor.LogEntries", false)?.GetProperty("consoleFlags", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
            var flagType = editor.GetType("UnityEditor.ConsoleWindow+ConsoleFlags", false);
            if (flags == null || flagType == null || !Enum.IsDefined(flagType, "ClearOnPlay"))
                return true;
            return ((int)flags.GetValue(null) & (int)Enum.Parse(flagType, "ClearOnPlay")) != 0;
        }

        public static void ClearConsole()
        {
            var entries = typeof(EditorApplication).Assembly.GetType("UnityEditor.LogEntries", false);
            var clear = entries == null ? null : entries.GetMethod("Clear", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
            if (clear == null)
                throw new MissingMethodException("Unity does not expose Console clearing.");
            clear.Invoke(null, null);
        }

        public static LogData[] GetLast(int count)
        {
            count = Mathf.Clamp(count, 1, 2000);
            lock (FileLock)
            {
                if (!File.Exists(SessionLogPath))
                    return Array.Empty<LogData>();
                // A queue keeps only the last lines instead of the whole session log.
                var last = new Queue<string>(count);
                foreach (var line in File.ReadLines(SessionLogPath))
                {
                    if (string.IsNullOrWhiteSpace(line))
                        continue;
                    if (last.Count == count)
                        last.Dequeue();
                    last.Enqueue(line);
                }
                return last.Select(JsonUtility.FromJson<LogData>)
                    .Where(entry => entry != null)
                    .ToArray();
            }
        }

        public static void Clear()
        {
            lock (FileLock)
            {
                TruncateLog();
                CurrentCompilationErrors.Clear();
                SaveCompilationErrors();
            }
        }

        public static LogData[] GetCurrentCompilationErrors()
        {
            lock (FileLock)
            {
                if (!File.Exists(CompilationLogPath))
                    return Array.Empty<LogData>();
                var data = JsonUtility.FromJson<LogFileData>(File.ReadAllText(CompilationLogPath));
                return data == null || data.entries == null ? Array.Empty<LogData>() : data.entries;
            }
        }

        private static void OnCompilationStarted(object context)
        {
            SessionState.SetString(CompileStartedKey, DateTime.UtcNow.ToString("O"));
            try
            {
                File.WriteAllText(BusyPath, "compiling");
            }
            catch (IOException)
            {
            }
            lock (FileLock)
            {
                CurrentCompilationErrors.Clear();
                TruncateLog();
                SaveCompilationErrors();
            }
        }

        private static void OnAssemblyCompilationFinished(string assemblyPath, CompilerMessage[] messages)
        {
            lock (FileLock)
            {
                foreach (var message in messages.Where(item => item.type == CompilerMessageType.Error))
                {
                    CurrentCompilationErrors.Add(new LogData
                    {
                        timestampUtc = DateTime.UtcNow.ToString("O"),
                        type = "Error",
                        message = message.message,
                        stackTrace = message.file + ":" + message.line + ":" + message.column
                    });
                }
                if (CurrentCompilationErrors.Count > 20)
                    CurrentCompilationErrors.RemoveRange(0, CurrentCompilationErrors.Count - 20);
                SaveCompilationErrors();
            }
        }

        private static void SaveCompilationErrors()
        {
            File.WriteAllText(
                CompilationLogPath,
                JsonUtility.ToJson(new LogFileData { entries = CurrentCompilationErrors.ToArray() }, false));
        }

        public static string Status()
        {
            return playModeStatus;
        }

        private static void OnPlayModeStateChanged(PlayModeStateChange state)
        {
            switch (state)
            {
                case PlayModeStateChange.ExitingEditMode:
                    playModeStatus = "игра запускается";
                    if (ConsoleClearsOnPlay())
                        lock (FileLock)
                            TruncateLog();
                    break;
                case PlayModeStateChange.EnteredPlayMode:
                    SessionState.SetString(PlayStartedKey, DateTime.UtcNow.ToString("O"));
                    // Unity is in the background while the agent plays; the project setting would freeze the game.
                    if (SessionState.GetBool(RunInBackgroundKey, false))
                        Application.runInBackground = true;
                    SessionState.EraseBool(RunInBackgroundKey);
                    playModeStatus = "игра запущена";
                    break;
                case PlayModeStateChange.ExitingPlayMode:
                    playModeStatus = "игра останавливается";
                    break;
                case PlayModeStateChange.EnteredEditMode:
                    playModeStatus = "игра оффлайн";
                    break;
            }
        }

        public static string SetPlayMode(string action, out bool transitionRequested)
        {
            transitionRequested = false;
            if (string.Equals(action, "запустить", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(action, "start", StringComparison.OrdinalIgnoreCase))
            {
                if (EditorApplication.isPlayingOrWillChangePlaymode)
                {
                    SessionState.SetBool(RunInBackgroundKey, true);
                    if (EditorApplication.isPlaying)
                        Application.runInBackground = true;
                    return "Game is already running.";
                }
                RequireCompiledScripts();
                ScenePersistenceService.SaveBeforeTransition();
                SessionState.SetBool(RunInBackgroundKey, true);
                EditorApplication.EnterPlaymode();
                transitionRequested = true;
                return "Play Mode start requested.";
            }

            if (string.Equals(action, "остановить", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(action, "stop", StringComparison.OrdinalIgnoreCase))
            {
                if (!EditorApplication.isPlayingOrWillChangePlaymode)
                    return "Game is already stopped.";
                EditorApplication.ExitPlaymode();
                transitionRequested = true;
                return "Play Mode stop requested.";
            }

            throw new ArgumentException("Play Mode action must be 'запустить'/'start' or 'остановить'/'stop'.", "action");
        }

        private static void OnLog(string condition, string stackTrace, LogType type)
        {
            var entry = new LogData
            {
                timestampUtc = DateTime.UtcNow.ToString("O"),
                type = type.ToString(),
                message = condition,
                stackTrace = stackTrace
            };
            if (type == LogType.Error || type == LogType.Exception || type == LogType.Assert)
                System.Threading.Interlocked.Exchange(ref lastErrorTicks, DateTime.UtcNow.Ticks);

            try
            {
                lock (FileLock)
                    File.AppendAllText(SessionLogPath, JsonUtility.ToJson(entry, false) + Environment.NewLine);
            }
            catch (Exception exception)
            {
                Console.Error.WriteLine(exception);
            }
        }
    }
}
