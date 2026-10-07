using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using UnityEditor;
using UnityEngine;

namespace UnityAgentBridge.Editor
{
    internal sealed class BridgeServerWindow : EditorWindow
    {
        [Serializable]
        private sealed class ServerConfig
        {
            public string projectRoot;
            public int port;
            public string token;
            public int pid;
            public string model;
        }

        [Serializable]
        private sealed class ServerError
        {
            public string error;
            public string traceback;
        }

        [Serializable]
        private sealed class ServerControl
        {
            public string action;
        }

        [Serializable]
        private sealed class ActiveProject
        {
            public string projectRoot;
        }

        [Serializable]
        private sealed class McpState
        {
            public string state;
            public string projectRoot;
            public int pid;
        }

        private int startingProcessId;
        private string actionError;
        private string startupStatus;
        private Task<int> startupTask;
        private Task<string> pluginInstallTask;
        private string packageStatus;
        private MessageType packageMessageType = MessageType.None;
        private string pluginInstallStatus;
        private MessageType pluginInstallMessageType = MessageType.None;
        private bool pluginStatusOnly;
        private Vector2 scrollPosition;
        private int selectedTab;
        private ServerConfig displayedConfig;
        private ServerError displayedServerError;
        private bool displayedRunning;
        private bool displayedStarting;
        private string displayedMcpStatus = "Offline";
        private string displayedInputStatus = "—";
        private string displayedMcpError;
        private double nextStatusRefresh;

        private static readonly string[] Tabs = { "Server", "Build package" };
        private const string AutoRestartPendingKey = "UnityAgentBridge.AutoRestartPending";
        private const string DesiredRunningKey = "UnityAgentBridge.DesiredRunning";
        private const string WatchdogArmedKey = "UnityAgentBridge.WatchdogArmed";
        private const string InputSetupPendingKey = "UnityAgentBridge.InputSetupPending";
        private const string StartAfterInputSetupKey = "UnityAgentBridge.StartAfterInputSetup";
        private static bool autoRestartScheduled;
        private static bool autoRestartInProgress;
        private static int autoRestartProcessId;
        private static double autoRestartDeadline;
        private static Task<int> autoRestartStartTask;
        private static double nextWatchdogCheck;
        private static string lastAutoRestartError;

        [MenuItem("Tools/Unity Agent Bridge")]
        private static void Open()
        {
            var window = GetWindow<BridgeServerWindow>();
            window.titleContent = new GUIContent("Unity Agent Bridge");
            window.minSize = new Vector2(430f, 330f);
            window.Show();
        }

        [InitializeOnLoadMethod]
        private static void ResumePendingAutoRestart()
        {
            EditorApplication.update -= MonitorServer;
            EditorApplication.update += MonitorServer;
            var config = ReadConfig();
            if (config != null)
            {
                SetDesiredRunning(true);
                SessionState.SetBool(WatchdogArmedKey, true);
            }
            if (SessionState.GetBool(AutoRestartPendingKey, false) ||
                config != null && !ProcessExists(config.pid))
            {
                SetDesiredRunning(true);
                SessionState.SetBool(WatchdogArmedKey, true);
                SessionState.SetBool(AutoRestartPendingKey, true);
                ScheduleAutoRestart();
            }
        }

        private static void MonitorServer()
        {
            if (EditorApplication.timeSinceStartup < nextWatchdogCheck)
                return;
            nextWatchdogCheck = EditorApplication.timeSinceStartup + 1d;

            ProcessServerControls();

            if (!IsDesiredRunning())
                return;

            var config = ReadConfig();
            if (config != null && ProcessExists(config.pid))
            {
                SessionState.SetBool(WatchdogArmedKey, true);
                return;
            }

            if (!SessionState.GetBool(WatchdogArmedKey, false) ||
                autoRestartScheduled || autoRestartInProgress ||
                EditorApplication.isCompiling || EditorApplication.isUpdating ||
                !InputSystemSetupService.IsReady)
                return;

            SessionState.SetBool(AutoRestartPendingKey, true);
            ScheduleAutoRestart();
        }

        private static void ProcessServerControls()
        {
            if (!Directory.Exists(BridgePaths.RuntimeRoot))
                return;
            foreach (var path in Directory.GetFiles(BridgePaths.RuntimeRoot, "server-control-*.json"))
            {
                ServerControl control;
                try
                {
                    control = JsonUtility.FromJson<ServerControl>(File.ReadAllText(path, Encoding.UTF8));
                    File.Delete(path);
                }
                catch (IOException)
                {
                    continue;
                }

                if (control == null || control.action != "start" && control.action != "restart")
                    continue;

                SetDesiredRunning(true);
                SessionState.SetBool(WatchdogArmedKey, true);
                var config = ReadConfig();
                if (control.action == "start" && config != null && ProcessExists(config.pid))
                    continue;
                SessionState.SetBool(AutoRestartPendingKey, true);
                ScheduleAutoRestart();
            }
        }

        internal static void RestartAfterAssetChangeIfRunning()
        {
            var config = ReadConfig();
            if (config == null || !ProcessExists(config.pid))
                return;
            SetDesiredRunning(true);
            SessionState.SetBool(WatchdogArmedKey, true);
            SessionState.SetBool(AutoRestartPendingKey, true);
            ScheduleAutoRestart();
        }

        private static void ScheduleAutoRestart()
        {
            if (autoRestartScheduled || autoRestartInProgress)
                return;
            autoRestartScheduled = true;
            EditorApplication.update += BeginAutoRestartOnUpdate;
        }

        private static void BeginAutoRestartOnUpdate()
        {
            EditorApplication.update -= BeginAutoRestartOnUpdate;
            BeginAutoRestart();
        }

        private static void BeginAutoRestart()
        {
            autoRestartScheduled = false;
            if (!SessionState.GetBool(AutoRestartPendingKey, false))
                return;
            if (EditorApplication.isCompiling || EditorApplication.isUpdating)
            {
                ScheduleAutoRestart();
                return;
            }

            var config = ReadConfig();
            if (config == null || !ProcessExists(config.pid))
            {
                SessionState.SetBool(AutoRestartPendingKey, false);
                autoRestartInProgress = true;
                StartAutoRestartServer();
                return;
            }

            SessionState.SetBool(AutoRestartPendingKey, false);
            try
            {
                RequestShutdown(config);
                autoRestartInProgress = true;
                autoRestartProcessId = config.pid;
                autoRestartDeadline = EditorApplication.timeSinceStartup + 10d;
                EditorApplication.update += WaitForAutoRestartStop;
            }
            catch (Exception exception)
            {
                UnityEngine.Debug.LogError("Unity Agent Bridge auto-restart failed: " + exception.Message);
            }
        }

        private static void WaitForAutoRestartStop()
        {
            if (ProcessExists(autoRestartProcessId) && EditorApplication.timeSinceStartup < autoRestartDeadline)
                return;

            EditorApplication.update -= WaitForAutoRestartStop;
            if (ProcessExists(autoRestartProcessId))
            {
                FinishAutoRestartWithError("The previous server did not stop within 10 seconds.");
                return;
            }

            StartAutoRestartServer();
        }

        private static void StartAutoRestartServer()
        {
            if (!IsDesiredRunning())
            {
                autoRestartInProgress = false;
                return;
            }
            if (!InputSystemSetupService.IsReady)
            {
                FinishAutoRestartWithError("Game input setup is required.");
                return;
            }
            try
            {
                var assetRoot = FindAssetRoot();
                EditorLayoutService.ApplyOnce(assetRoot);
                var pythonRoot = Path.Combine(assetRoot, "Python~");
                var serverScript = Path.Combine(pythonRoot, "server.py");
                if (!File.Exists(serverScript))
                    throw new FileNotFoundException("Bridge server script was not found.", serverScript);

                BridgePaths.EnsureRuntimeDirectories();
                DeleteIfExists(ServerErrorPath());
                DeleteIfExists(ServerConfigPath());
                var modelCache = Path.Combine(BridgePaths.RuntimeRoot, "models");
                var port = FreeLoopbackPort();
                var token = Guid.NewGuid().ToString("N");
                autoRestartStartTask = Task.Run(() =>
                {
                    var executable = BridgeRuntimeInstaller.Ensure(assetRoot, null);
                    return StartServerProcess(executable, pythonRoot, serverScript, modelCache, port, token);
                });
                EditorApplication.update += WaitForAutoRestartPrepared;
            }
            catch (Exception exception)
            {
                FinishAutoRestartWithError(exception.GetType().Name + ": " + exception.Message);
            }
        }

        private static void WaitForAutoRestartPrepared()
        {
            if (autoRestartStartTask == null || !autoRestartStartTask.IsCompleted)
                return;
            EditorApplication.update -= WaitForAutoRestartPrepared;
            try
            {
                autoRestartProcessId = autoRestartStartTask.GetAwaiter().GetResult();
                EditorApplication.update += WaitForAutoRestartStart;
            }
            catch (Exception exception)
            {
                FinishAutoRestartWithError(exception.GetType().Name + ": " + exception.Message);
            }
            finally
            {
                autoRestartStartTask = null;
            }
        }

        private static void WaitForAutoRestartStart()
        {
            var config = ReadConfig();
            if (config != null && ProcessExists(config.pid))
            {
                EditorApplication.update -= WaitForAutoRestartStart;
                autoRestartProcessId = config.pid;
                autoRestartInProgress = false;
                lastAutoRestartError = null;
                SessionState.SetBool(WatchdogArmedKey, true);
                if (SessionState.GetBool(AutoRestartPendingKey, false))
                    ScheduleAutoRestart();
                return;
            }
            if (ProcessExists(autoRestartProcessId))
                return;

            EditorApplication.update -= WaitForAutoRestartStart;
            var serverError = ReadServerError();
            FinishAutoRestartWithError(serverError != null && !string.IsNullOrWhiteSpace(serverError.error)
                ? serverError.error
                : "The new server exited before becoming ready.");
        }

        private static void FinishAutoRestartWithError(string message)
        {
            autoRestartInProgress = false;
            nextWatchdogCheck = EditorApplication.timeSinceStartup + 5d;
            if (!string.Equals(lastAutoRestartError, message, StringComparison.Ordinal))
            {
                lastAutoRestartError = message;
                UnityEngine.Debug.LogError("Unity Agent Bridge auto-restart failed: " + message);
            }
        }

        private void OnInspectorUpdate()
        {
            CompleteInputSystemSetup();
            CompleteStartup();
            CompletePluginInstall();
            RefreshDisplayedStatus();
            Repaint();
        }

        private void OnEnable()
        {
            nextStatusRefresh = 0d;
            RefreshDisplayedStatus();
        }

        private void RefreshDisplayedStatus()
        {
            if (EditorApplication.timeSinceStartup < nextStatusRefresh)
                return;
            nextStatusRefresh = EditorApplication.timeSinceStartup + 0.5d;
            displayedConfig = ReadConfig();
            displayedRunning = displayedConfig != null && ProcessExists(displayedConfig.pid);
            displayedStarting = startupTask != null || !displayedRunning && startingProcessId > 0 && ProcessExists(startingProcessId);
            displayedServerError = ReadServerError();
            displayedMcpStatus = McpStatus(displayedRunning);
            displayedInputStatus = InputSystemSetupService.Status;
            displayedMcpError = displayedRunning ? ReadMcpError() : string.Empty;
        }

        private void OnGUI()
        {
            selectedTab = GUILayout.Toolbar(selectedTab, Tabs);
            scrollPosition = EditorGUILayout.BeginScrollView(scrollPosition);
            if (selectedTab == 0)
                DrawServerTab();
            else
                DrawPackageTab();
            EditorGUILayout.EndScrollView();
        }

        private void DrawServerTab()
        {
            EditorGUILayout.Space(10f);
            EditorGUILayout.LabelField("Unity Agent Bridge", EditorStyles.boldLabel);
            EditorGUILayout.Space(4f);

            var config = displayedConfig;
            var running = displayedRunning;
            var starting = displayedStarting;
            var serverError = displayedServerError;

            EditorGUILayout.LabelField("Server", running ? "Running" : starting ? startupStatus : "Stopped");
            EditorGUILayout.LabelField("NLP model", running ? config.model : starting ? "Preparing" : "Offline");
            EditorGUILayout.LabelField("Endpoint", running ? "127.0.0.1:" + config.port : "—");
            EditorGUILayout.LabelField("Process", running ? config.pid.ToString() : starting ? startingProcessId.ToString() : "—");
            EditorGUILayout.LabelField("Game MCP", displayedMcpStatus);
            EditorGUILayout.LabelField("Game input", displayedInputStatus);

            EditorGUILayout.Space(10f);
            using (new EditorGUILayout.HorizontalScope())
            {
                using (new EditorGUI.DisabledScope(running || starting))
                {
                    if (GUILayout.Button("Start Server", GUILayout.Height(32f)))
                        StartServer();
                }

                using (new EditorGUI.DisabledScope(!running))
                {
                    if (GUILayout.Button("Stop Server", GUILayout.Height(32f)))
                        StopServer(config);
                }
            }

            EditorGUILayout.Space(10f);
            EditorGUILayout.HelpBox(
                "On the first start, the server installs its local Python runtime, libraries, and NLP model in this project's Library folder. Later starts reuse them.",
                MessageType.Info);

            var visibleError = !string.IsNullOrWhiteSpace(actionError)
                ? actionError
                : serverError != null ? serverError.error : displayedMcpError;
            EditorGUILayout.HelpBox(
                string.IsNullOrWhiteSpace(visibleError) ? " " : visibleError,
                string.IsNullOrWhiteSpace(visibleError) ? MessageType.None : MessageType.Error);
        }

        private void DrawPackageTab()
        {
            EditorGUILayout.Space(10f);
            EditorGUILayout.LabelField("Build Unity package", EditorStyles.boldLabel);
            if (GUILayout.Button("Build .unitypackage", GUILayout.Height(32f)))
                StartPackaging();
            EditorGUILayout.HelpBox(string.IsNullOrWhiteSpace(packageStatus) ? " " : packageStatus, packageMessageType);

            EditorGUILayout.Space(16f);
            EditorGUILayout.LabelField("Agent plugins", EditorStyles.boldLabel);
            using (new EditorGUI.DisabledScope(pluginInstallTask != null))
            {
                if (GUILayout.Button(pluginInstallTask == null ? "Build and install Codex + Claude" : "Installing...", GUILayout.Height(32f)))
                    StartPluginInstall();
                if (GUILayout.Button("Check versions", GUILayout.Height(24f)))
                    StartPluginStatus();
            }
            EditorGUILayout.HelpBox(
                string.IsNullOrWhiteSpace(pluginInstallStatus) ? " " : pluginInstallStatus,
                pluginInstallMessageType);
        }

        private void StartPackaging()
        {
            packageStatus = "Building UnityAgentBridge.unitypackage...";
            packageMessageType = MessageType.Info;
            try
            {
                var result = BridgePackageExporter.Pack(AssetRoot());
                packageStatus = "Created: " + result.path + "\nSize: " + FormatBytes(result.bytes) + "\nSHA-256: " + result.sha256;
                packageMessageType = MessageType.Info;
            }
            catch (Exception exception)
            {
                packageStatus = exception.GetType().Name + ": " + exception.Message;
                packageMessageType = MessageType.Error;
            }
        }

        private void StartPluginInstall()
        {
            pluginStatusOnly = false;
            pluginInstallStatus = "Building and installing plugins...";
            pluginInstallMessageType = MessageType.Info;
            var assetRoot = AssetRoot();
            pluginInstallTask = Task.Run(() => InstallPlugins(assetRoot));
        }

        private void StartPluginStatus()
        {
            pluginStatusOnly = true;
            pluginInstallStatus = "Checking versions...";
            pluginInstallMessageType = MessageType.Info;
            var assetRoot = AssetRoot();
            pluginInstallTask = Task.Run(() => InstallPlugins(assetRoot, "--status"));
        }

        private static string InstallPlugins(string assetRoot, string extraArguments = null)
        {
            var installer = Path.Combine(assetRoot, "install_plugins.py");
            if (!File.Exists(installer))
                throw new FileNotFoundException("Plugin installer was not found.", installer);

            var python = BridgeRuntimeInstaller.Ensure(assetRoot, null);
            var info = new ProcessStartInfo
            {
                FileName = python,
                Arguments = "\"" + installer + "\" --asset-root \"" + assetRoot + "\"" +
                    (string.IsNullOrWhiteSpace(extraArguments) ? string.Empty : " " + extraArguments),
                WorkingDirectory = Path.GetDirectoryName(installer),
                UseShellExecute = false,
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };

            using (var process = Process.Start(info))
            {
                if (process == null)
                    throw new InvalidOperationException("Unable to start plugin installer.");
                var output = process.StandardOutput.ReadToEnd();
                var error = process.StandardError.ReadToEnd();
                process.WaitForExit();
                if (process.ExitCode != 0)
                    throw new InvalidOperationException(string.IsNullOrWhiteSpace(error) ? "Plugin installer failed with exit code " + process.ExitCode + "." : error.Trim());
                return output.Trim();
            }
        }

        private void CompletePluginInstall()
        {
            if (pluginInstallTask == null || !pluginInstallTask.IsCompleted)
                return;

            try
            {
                pluginInstallStatus = pluginInstallTask.GetAwaiter().GetResult() +
                    (pluginStatusOnly ? string.Empty : "\nOpen new chats to load the new versions.");
                pluginInstallMessageType = MessageType.Info;
            }
            catch (Exception exception)
            {
                pluginInstallStatus = exception.GetType().Name + ": " + exception.Message;
                pluginInstallMessageType = MessageType.Error;
            }
            finally
            {
                pluginInstallTask = null;
            }
        }

        private static string FormatBytes(long bytes)
        {
            const double kilobyte = 1024d;
            const double megabyte = 1024d * 1024d;
            if (bytes < kilobyte)
                return bytes + " B";
            if (bytes < megabyte)
                return (bytes / kilobyte).ToString("0.0") + " KB";
            return (bytes / megabyte).ToString("0.0") + " MB";
        }

        private void StartServer()
        {
            actionError = string.Empty;
            if (!InputSystemSetupService.IsReady)
            {
                if (!EditorUtility.DisplayDialog(
                    "Unity Agent Bridge",
                    "Game control requires Unity Input System. Install it and enable Both input backends? Unity will recompile scripts.",
                    "Install and configure",
                    "Cancel"))
                    return;

                SessionState.SetBool(InputSetupPendingKey, true);
                SessionState.SetBool(StartAfterInputSetupKey, true);
                SetDesiredRunning(true);
                CompleteInputSystemSetup();
                return;
            }

            SetDesiredRunning(true);
            StartServerCore();
        }

        private void StartServerCore()
        {
            actionError = string.Empty;
            try
            {
                InputSystemSetupService.RequireReady();
            }
            catch (Exception exception)
            {
                actionError = exception.Message;
                return;
            }

            EditorLayoutService.ApplyOnce(AssetRoot());
            var pythonRoot = Path.Combine(AssetRoot(), "Python~");
            var serverScript = Path.Combine(pythonRoot, "server.py");
            var modelCache = Path.Combine(BridgePaths.RuntimeRoot, "models");

            if (!File.Exists(serverScript))
            {
                actionError = "Bridge server script is missing: " + serverScript;
                return;
            }
            BridgePaths.EnsureRuntimeDirectories();
            DeleteIfExists(ServerErrorPath());
            var stale = ReadConfig();
            if (stale != null && !ProcessExists(stale.pid))
                DeleteIfExists(ServerConfigPath());

            var port = FreeLoopbackPort();
            var token = Guid.NewGuid().ToString("N");
            startupStatus = "Preparing local runtime";
            var assetRoot = AssetRoot();
            startupTask = Task.Run(() =>
            {
                var executable = BridgeRuntimeInstaller.Ensure(assetRoot, value => startupStatus = value);
                startupStatus = "Downloading or loading NLP model";
                return StartServerProcess(executable, pythonRoot, serverScript, modelCache, port, token);
            });
        }

        private void CompleteInputSystemSetup()
        {
            if (!SessionState.GetBool(InputSetupPendingKey, false) ||
                EditorApplication.isCompiling || EditorApplication.isUpdating)
                return;

            try
            {
                if (!InputSystemSetupService.PollSetup())
                {
                    return;
                }

                SessionState.SetBool(InputSetupPendingKey, false);
                if (SessionState.GetBool(StartAfterInputSetupKey, false))
                {
                    SessionState.SetBool(StartAfterInputSetupKey, false);
                    StartServerCore();
                }
            }
            catch (Exception exception)
            {
                SessionState.SetBool(InputSetupPendingKey, false);
                SessionState.SetBool(StartAfterInputSetupKey, false);
                actionError = exception.GetType().Name + ": " + exception.Message;
            }
        }

        private static int StartServerProcess(string executable, string pythonRoot, string serverScript, string modelCache, int port, string token)
        {
            var info = new ProcessStartInfo
            {
                FileName = executable,
                Arguments =
                    "\"" + serverScript + "\"" +
                    " --project \"" + BridgePaths.ProjectRoot + "\"" +
                    " --port " + port +
                    " --token " + token +
                    " --model-cache \"" + modelCache + "\"",
                WorkingDirectory = pythonRoot,
                UseShellExecute = false,
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden
            };

            var process = Process.Start(info);
            if (process == null)
                throw new InvalidOperationException("Process.Start returned no process.");
            return process.Id;
        }

        private void CompleteStartup()
        {
            if (startupTask == null || !startupTask.IsCompleted)
                return;
            try
            {
                startingProcessId = startupTask.GetAwaiter().GetResult();
            }
            catch (Exception exception)
            {
                actionError = exception.GetType().Name + ": " + exception.Message;
                startingProcessId = 0;
            }
            finally
            {
                startupTask = null;
            }
        }

        private void StopServer(ServerConfig config)
        {
            actionError = string.Empty;
            SetDesiredRunning(false);
            SessionState.SetBool(WatchdogArmedKey, false);
            SessionState.SetBool(AutoRestartPendingKey, false);
            try
            {
                RequestShutdown(config);
                startingProcessId = 0;
            }
            catch (Exception exception)
            {
                actionError = exception.GetType().Name + ": " + exception.Message;
            }
        }

        private static void RequestShutdown(ServerConfig config)
        {
            var request = (HttpWebRequest)WebRequest.Create("http://127.0.0.1:" + config.port + "/shutdown");
            request.Method = "POST";
            request.ContentLength = 0;
            request.Timeout = 5000;
            request.Headers.Add("X-Unity-Agent-Token", config.token);
            using (var response = (HttpWebResponse)request.GetResponse())
            {
                if (response.StatusCode != HttpStatusCode.OK)
                    throw new InvalidOperationException("Server returned HTTP " + (int)response.StatusCode + ".");
            }
        }

        private static bool IsDesiredRunning()
        {
            return SessionState.GetBool(DesiredRunningKey, false) || File.Exists(DesiredRunningPath());
        }

        private static void SetDesiredRunning(bool value)
        {
            SessionState.SetBool(DesiredRunningKey, value);
            var path = DesiredRunningPath();
            if (value)
            {
                BridgePaths.EnsureRuntimeDirectories();
                File.WriteAllText(path, "1", Encoding.UTF8);
            }
            else
            {
                DeleteIfExists(path);
            }
        }

        private string AssetRoot()
        {
            var script = MonoScript.FromScriptableObject(this);
            var scriptAssetPath = AssetDatabase.GetAssetPath(script);
            if (string.IsNullOrEmpty(scriptAssetPath))
                throw new InvalidOperationException("Unable to locate BridgeServerWindow.cs in the AssetDatabase.");
            var editorDirectory = Path.GetDirectoryName(scriptAssetPath);
            var assetDirectory = Path.GetDirectoryName(editorDirectory);
            return Path.GetFullPath(Path.Combine(BridgePaths.ProjectRoot, assetDirectory));
        }

        internal static string FindAssetRoot()
        {
            var probe = CreateInstance<BridgeServerWindow>();
            try
            {
                return probe.AssetRoot();
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(probe);
            }
        }

        private static ServerConfig ReadConfig()
        {
            var path = ServerConfigPath();
            try
            {
                if (!File.Exists(path))
                    return null;
                return JsonUtility.FromJson<ServerConfig>(File.ReadAllText(path, Encoding.UTF8));
            }
            catch (IOException)
            {
                return null;
            }
        }

        private static ServerError ReadServerError()
        {
            var path = ServerErrorPath();
            try
            {
                if (!File.Exists(path))
                    return null;
                return JsonUtility.FromJson<ServerError>(File.ReadAllText(path, Encoding.UTF8));
            }
            catch (IOException)
            {
                return null;
            }
        }

        private static bool ProcessExists(int processId)
        {
            if (processId <= 0)
                return false;
            try
            {
                using (var process = Process.GetProcessById(processId))
                    return !process.HasExited;
            }
            catch (ArgumentException)
            {
                return false;
            }
        }

        private static int FreeLoopbackPort()
        {
            var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            try
            {
                return ((IPEndPoint)listener.LocalEndpoint).Port;
            }
            finally
            {
                listener.Stop();
            }
        }

        private static string ServerConfigPath()
        {
            return Path.Combine(BridgePaths.RuntimeRoot, "server.json");
        }

        private static string ServerErrorPath()
        {
            return Path.Combine(BridgePaths.RuntimeRoot, "server-error.json");
        }

        private static string DesiredRunningPath()
        {
            return Path.Combine(BridgePaths.RuntimeRoot, "desired-running");
        }

        private static string DiscoveryPath(string name)
        {
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "UnityAgentBridge", name);
        }

        private static string McpStatus(bool running)
        {
            if (!running)
                return "Offline";
            if (!string.IsNullOrWhiteSpace(ReadMcpError()))
                return "Error";
            try
            {
                var path = DiscoveryPath("active-project.json");
                if (!File.Exists(path))
                    return "Restart server";
                var active = JsonUtility.FromJson<ActiveProject>(File.ReadAllText(path, Encoding.UTF8));
                if (active == null || string.IsNullOrWhiteSpace(active.projectRoot))
                    return "Error";
                return Path.GetFullPath(active.projectRoot).Equals(BridgePaths.ProjectRoot, StringComparison.OrdinalIgnoreCase)
                    ? ReadMcpState()
                    : "Other project";
            }
            catch (IOException)
            {
                return "Error";
            }
        }

        private static string ReadMcpState()
        {
            var states = new[] { "codex", "claude" }
                .Select(ReadMcpClientState)
                .Where(value => value != "Not connected")
                .ToArray();
            if (states.Length == 0)
                return "Not connected";
            if (states.Contains("Connected"))
                return "Connected";
            if (states.Contains("Connecting"))
                return "Connecting";
            if (states.Contains("Other project"))
                return "Other project";
            return states[0];
        }

        private static string ReadMcpClientState(string client)
        {
            var directory = Path.GetDirectoryName(DiscoveryPath("placeholder"));
            var paths = Directory.Exists(directory)
                ? Directory.GetFiles(directory, "mcp-state-" + client + "-*.json")
                : Array.Empty<string>();
            var states = paths.Select(ReadLiveMcpState).Where(value => value != null).ToArray();
            if (states.Length == 0)
                return "Not connected";
            if (states.All(value => !Path.GetFullPath(value.projectRoot).Equals(BridgePaths.ProjectRoot, StringComparison.OrdinalIgnoreCase)))
                return "Other project";
            if (states.Any(value => value.state == "ready" && Path.GetFullPath(value.projectRoot).Equals(BridgePaths.ProjectRoot, StringComparison.OrdinalIgnoreCase)))
                return "Connected";
            if (states.Any(value => value.state == "started" && Path.GetFullPath(value.projectRoot).Equals(BridgePaths.ProjectRoot, StringComparison.OrdinalIgnoreCase)))
                return "Connecting";
            return "Error";
        }

        private static McpState ReadLiveMcpState(string path)
        {
            try
            {
                var value = JsonUtility.FromJson<McpState>(File.ReadAllText(path, Encoding.UTF8));
                if (value == null || string.IsNullOrWhiteSpace(value.state) || string.IsNullOrWhiteSpace(value.projectRoot)
                    || value.pid <= 0 || !ProcessExists(value.pid))
                {
                    DeleteIfExists(path);
                    return null;
                }
                return value;
            }
            catch (IOException)
            {
                return null;
            }
        }

        private static string ReadMcpError()
        {
            try
            {
                return string.Join("\n", new[] { "codex", "claude" }
                    .Select(client => new { client, path = DiscoveryPath("mcp-error-" + client + ".txt") })
                    .Where(item => File.Exists(item.path))
                    .Select(item => item.client + ": " + File.ReadAllText(item.path, Encoding.UTF8).Trim())
                    .Where(value => !value.EndsWith(": ", StringComparison.Ordinal))
                    .ToArray());
            }
            catch (IOException exception)
            {
                return exception.Message;
            }
        }

        private static void DeleteIfExists(string path)
        {
            if (File.Exists(path))
                File.Delete(path);
        }
    }

    internal sealed class BridgeAssetChangePostprocessor : AssetPostprocessor
    {
        private const string EditorRoot = "Assets/UnityAgentBridge/Editor/";
        private const string PythonRoot = "Assets/UnityAgentBridge/Python~/";

        private static void OnPostprocessAllAssets(
            string[] importedAssets,
            string[] deletedAssets,
            string[] movedAssets,
            string[] movedFromAssetPaths)
        {
            if (ContainsRuntimeChange(importedAssets)
                || ContainsRuntimeChange(deletedAssets)
                || ContainsRuntimeChange(movedAssets)
                || ContainsRuntimeChange(movedFromAssetPaths))
            {
                BridgeServerWindow.RestartAfterAssetChangeIfRunning();
            }
        }

        private static bool ContainsRuntimeChange(string[] paths)
        {
            if (paths == null)
                return false;
            foreach (var path in paths)
            {
                if (string.IsNullOrWhiteSpace(path) || path.EndsWith(".meta", StringComparison.OrdinalIgnoreCase))
                    continue;
                if (path.StartsWith(EditorRoot, StringComparison.OrdinalIgnoreCase)
                    || path.StartsWith(PythonRoot, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }
            return false;
        }
    }

    [InitializeOnLoad]
    internal static class BridgeRuntimeChangeWatcher
    {
        private static FileSystemWatcher watcher;
        private static int changeQueued;
        private static int runtimeRestartQueued;
        private static int editorRefreshQueued;
        private static int playStopQueued;
        private static long lastChangeTicks;

        static BridgeRuntimeChangeWatcher()
        {
            EditorApplication.update += ProcessQueuedChange;
            AssemblyReloadEvents.beforeAssemblyReload += Dispose;
            // Watching the whole Assets tree takes ~0.4 s to set up; off the main thread it no longer delays every script reload.
            var assetRoot = Path.Combine(BridgePaths.ProjectRoot, "Assets");
            System.Threading.Tasks.Task.Run(() => Start(assetRoot));
        }

        private static readonly object WatcherLock = new object();
        private static bool disposed;

        private static void Start(string assetRoot)
        {
            try
            {
                var created = new FileSystemWatcher(assetRoot)
                {
                    IncludeSubdirectories = true,
                    NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.Size
                };
                created.Changed += OnChanged;
                created.Created += OnChanged;
                created.Deleted += OnChanged;
                created.Renamed += OnRenamed;
                lock (WatcherLock)
                {
                    if (disposed)
                    {
                        created.Dispose();
                        return;
                    }
                    watcher = created;
                    created.EnableRaisingEvents = true;
                }
            }
            catch (Exception exception)
            {
                UnityEngine.Debug.LogError("Unity Agent Bridge change watcher failed: " + exception.Message);
            }
        }

        private static void OnChanged(object sender, FileSystemEventArgs arguments)
        {
            QueueIfRuntimeFile(arguments.FullPath);
        }

        private static void OnRenamed(object sender, RenamedEventArgs arguments)
        {
            QueueIfRuntimeFile(arguments.OldFullPath);
            QueueIfRuntimeFile(arguments.FullPath);
        }

        private static void QueueIfRuntimeFile(string path)
        {
            if (!string.IsNullOrWhiteSpace(path)
                && path.Replace('\\', '/').IndexOf("/__UnityAgentBridgeExport/", StringComparison.OrdinalIgnoreCase) >= 0)
                return;
            bool editorFile;
            var runtimeFile = IsRuntimeFile(path, out editorFile);
            var projectScript = IsProjectScript(path);
            if (!runtimeFile && !projectScript)
                return;
            if (runtimeFile)
                Interlocked.Exchange(ref runtimeRestartQueued, 1);
            if (editorFile || projectScript)
            {
                Interlocked.Exchange(ref editorRefreshQueued, 1);
                Interlocked.Exchange(ref playStopQueued, 1);
            }
            Interlocked.Exchange(ref lastChangeTicks, DateTime.UtcNow.Ticks);
            Interlocked.Exchange(ref changeQueued, 1);
        }

        private static bool IsProjectScript(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
                return false;
            var extension = Path.GetExtension(path);
            return extension.Equals(".cs", StringComparison.OrdinalIgnoreCase)
                || extension.Equals(".asmdef", StringComparison.OrdinalIgnoreCase);
        }

        private static bool IsRuntimeFile(string path, out bool editorFile)
        {
            editorFile = false;
            if (string.IsNullOrWhiteSpace(path))
                return false;
            var normalized = path.Replace('\\', '/');
            if (normalized.IndexOf("/__pycache__/", StringComparison.OrdinalIgnoreCase) >= 0)
                return false;
            var inEditor = normalized.IndexOf("/UnityAgentBridge/Editor/", StringComparison.OrdinalIgnoreCase) >= 0;
            var inPython = normalized.IndexOf("/UnityAgentBridge/Python~/", StringComparison.OrdinalIgnoreCase) >= 0;
            var pluginManifest = normalized.EndsWith("/UnityAgentBridge/CodexPlugin~/unity-agent-bridge/codex-plugin/plugin.json", StringComparison.OrdinalIgnoreCase);
            if (!inEditor && !inPython && !pluginManifest)
                return false;
            var extension = Path.GetExtension(normalized);
            editorFile = inEditor && (extension.Equals(".cs", StringComparison.OrdinalIgnoreCase)
                || extension.Equals(".asmdef", StringComparison.OrdinalIgnoreCase));
            return editorFile
                || pluginManifest
                || extension.Equals(".asmdef", StringComparison.OrdinalIgnoreCase)
                || extension.Equals(".py", StringComparison.OrdinalIgnoreCase)
                || normalized.EndsWith("/requirements.txt", StringComparison.OrdinalIgnoreCase);
        }

        private static void ProcessQueuedChange()
        {
            if (Interlocked.CompareExchange(ref changeQueued, 0, 0) == 0)
                return;
            var elapsed = new TimeSpan(DateTime.UtcNow.Ticks - Interlocked.Read(ref lastChangeTicks));
            if (elapsed.TotalMilliseconds < 500d)
                return;
            var playStopState = Interlocked.CompareExchange(ref playStopQueued, 0, 0);
            if (playStopState != 0 && (EditorApplication.isPlaying || EditorApplication.isPlayingOrWillChangePlaymode))
            {
                if (playStopState == 1 && Interlocked.CompareExchange(ref playStopQueued, 2, 1) == 1)
                    EditorApplication.ExitPlaymode();
                return;
            }
            Interlocked.Exchange(ref changeQueued, 0);
            Interlocked.Exchange(ref playStopQueued, 0);
            if (Interlocked.Exchange(ref runtimeRestartQueued, 0) != 0)
                BridgeServerWindow.RestartAfterAssetChangeIfRunning();
            if (Interlocked.Exchange(ref editorRefreshQueued, 0) != 0)
                AssetDatabase.Refresh(ImportAssetOptions.ForceUpdate);
        }

        private static void Dispose()
        {
            EditorApplication.update -= ProcessQueuedChange;
            lock (WatcherLock)
            {
                disposed = true;
                if (watcher == null)
                    return;
                watcher.EnableRaisingEvents = false;
                watcher.Dispose();
                watcher = null;
            }
        }
    }
}
