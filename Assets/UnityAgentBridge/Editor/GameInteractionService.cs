using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Reflection;
using UnityEditor;
using UnityEngine;

namespace UnityAgentBridge.Editor
{
    [InitializeOnLoad]
    internal static class GameInteractionService
    {
        // The input driver is a DontSave object, so leaving Play Mode does not remove it; each session would leave one.
        static GameInteractionService()
        {
            EditorApplication.playModeStateChanged += state =>
            {
                if (state != PlayModeStateChange.ExitingPlayMode)
                    return;
                PendingDispatches.Clear();
                GameInputDriverBehaviour.ResetState();
                // The developer's keyboard and mouse work again in the editor after the game stops.
                try
                {
                    InputSystemGameInput.Reset();
                }
                catch (Exception exception)
                {
                    Debug.LogWarning("Unity Agent Bridge could not release its game input devices: " + exception.Message);
                }
            };
            EditorApplication.pauseStateChanged += state =>
            {
                if (state == PauseState.Unpaused)
                    PausedBy = string.Empty;
                else if (pausingFor != null)
                    PausedBy = pausingFor;
                else
                    PausedBy = DebugService.ErrorJustPaused() ? "error" : "editor";
            };
            UnityEngine.Rendering.RenderPipelineManager.endContextRendering += (context, cameras) =>
            {
                if (cameras.Exists(camera => camera.cameraType == CameraType.Game))
                    renderedFrame = Time.frameCount;
            };
            Camera.onPostRender += camera =>
            {
                if (camera.cameraType == CameraType.Game)
                    renderedFrame = Time.frameCount;
            };
        }

        // Who paused the game, as the developer tells it from the editor: the bridge's own command, the Console's Error
        // Pause on an error, or the Pause button and Debug.Break.
        internal static string PausedBy = string.Empty;
        private static string pausingFor;

        private static void PauseGame(string by)
        {
            pausingFor = by;
            try
            {
                EditorApplication.isPaused = true;
            }
            finally
            {
                pausingFor = null;
            }
        }

        // The Game View draws the game's last frame on its next repaint; a capture before that showed the previous frame.
        private static int renderedFrame = -1;
        private static double captureWaitStart = -1d;

        private static void WaitForRenderedFrame(EditorWindow gameView)
        {
            if (renderedFrame >= Time.frameCount)
            {
                captureWaitStart = -1d;
                return;
            }
            if (captureWaitStart < 0d)
                captureWaitStart = EditorApplication.timeSinceStartup;
            if (EditorApplication.timeSinceStartup - captureWaitStart > 1d)
            {
                captureWaitStart = -1d;
                return;
            }
            gameView.Repaint();
            throw new BridgeNotReadyException("Game View has not drawn the last frame yet.");
        }

        [Serializable]
        private sealed class GameViewState
        {
            public string state;
            public string message;
            public int renderWidth;
            public int renderHeight;
        }

        [Serializable]
        private sealed class GameFrame
        {
            public string screenshot;
            public int width;
            public int height;
            public float time;
        }

        private const BindingFlags InstanceMembers = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        private static readonly Type GameViewType = typeof(EditorWindow).Assembly.GetType("UnityEditor.GameView", true);
        private static readonly PropertyInfo TargetInView = GameViewType.GetProperty("targetInView", InstanceMembers);
        private static readonly PropertyInfo TargetDisplay = GameViewType.GetProperty("targetDisplay", InstanceMembers);
        private static readonly FieldInfo TargetDisplayField = GameViewType.GetField("m_TargetDisplay", InstanceMembers);
        private static readonly FieldInfo GameRenderTexture = GameViewType.GetField("m_RenderTexture", InstanceMembers);
        private static readonly Dictionary<string, long> PendingDispatches =
            new Dictionary<string, long>(StringComparer.Ordinal);

        internal static bool InputSettled
        {
            get { return !EditorApplication.isPlaying || GameInputDriverBehaviour.AllSettled; }
        }

        // A capture stops game time as soon as the game has taken its input, so waiting for the frame costs none.
        internal static bool ReadyToCapture()
        {
            if (!EditorApplication.isPlaying)
                return true;
            if (!InputSettled)
                return false;
            PauseGame("game_actions");
            return HasRenderedFrame() && ShadersReady();
        }

        public static string Prepare()
        {
            InputSystemSetupService.RequireReady();
            if (!EditorApplication.isPlayingOrWillChangePlaymode)
                DebugService.RequireCompiledScripts();
            if (EditorApplication.isPlayingOrWillChangePlaymode != EditorApplication.isPlaying)
                return JsonUtility.ToJson(new GameViewState { state = "starting" });
            if (!EditorApplication.isPlayingOrWillChangePlaymode)
            {
                GameInputDriverBehaviour.ResetState();
                PendingDispatches.Clear();
                InputSystemGameInput.Reset();
                ScenePersistenceService.SaveBeforeTransition();
                EditorApplication.EnterPlaymode();
                return JsonUtility.ToJson(new GameViewState { state = "starting" });
            }

            if (!EditorApplication.isPlaying)
                return JsonUtility.ToJson(new GameViewState { state = "starting" });

            try
            {
                // A Game View tab hidden behind another tab does not draw the game, so the batch brings it to the front.
                var covered = Array.Exists(Resources.FindObjectsOfTypeAll<EditorWindow>(), window => window.maximized && window.GetType() != GameViewType);
                var gameView = GetGameView(!covered);
                GameInputDriverBehaviour.Ensure();
                InputSystemGameInput.EnsureAvailable();
                RuntimeUiInputSetup.Ensure();
                Application.runInBackground = true;
                if (EditorApplication.isPaused)
                {
                    Unpause();
                    return JsonUtility.ToJson(new GameViewState { state = "layout" });
                }
                if (!GameInputDriverBehaviour.Ready)
                {
                    EditorApplication.QueuePlayerLoopUpdate();
                    return JsonUtility.ToJson(new GameViewState { state = "layout" });
                }
                gameView.Repaint();
                var target = GetTargetRect(gameView);
                var renderTexture = GetRenderTexture(gameView);
                if (target.width <= 0f || target.height <= 0f || renderTexture == null ||
                    renderTexture.width <= 0 || renderTexture.height <= 0)
                    return JsonUtility.ToJson(new GameViewState { state = "layout" });

                var cameraError = CameraError(gameView);
                if (!string.IsNullOrEmpty(cameraError))
                    return JsonUtility.ToJson(new GameViewState { state = "no-camera", message = cameraError });
                // Brought to the front just now, the view still holds the frame drawn before it was hidden.
                if (!covered && renderedFrame < Time.frameCount - 1)
                {
                    EditorApplication.QueuePlayerLoopUpdate();
                    return JsonUtility.ToJson(new GameViewState { state = "layout" });
                }
                return JsonUtility.ToJson(new GameViewState
                {
                    state = "ready",
                    renderWidth = renderTexture.width,
                    renderHeight = renderTexture.height
                });
            }
            catch
            {
                PauseGame("game_actions");
                throw;
            }
        }

        internal static bool CanCompleteDispatch(BridgeRequest request)
        {
            if (!EditorApplication.isPlaying)
                return true;

            long serial;
            if (!PendingDispatches.TryGetValue(request.id, out serial))
            {
                serial = Schedule(request);
                PendingDispatches.Add(request.id, serial);
                return false;
            }
            return GameInputDriverBehaviour.IsSettled(serial);
        }

        public static void CompleteDispatch(BridgeRequest request)
        {
            if (!EditorApplication.isPlaying)
                throw new InvalidOperationException("Game input requires a running game.");

            long serial;
            if (!PendingDispatches.TryGetValue(request.id, out serial))
                throw new InvalidOperationException("Game input was not scheduled.");
            PendingDispatches.Remove(request.id);
            GameInputDriverBehaviour.ThrowIfFailed(serial);
        }

        private static long Schedule(BridgeRequest request)
        {
            if (!EditorApplication.isPlaying)
                throw new InvalidOperationException("Game input requires a running game.");

            if (EditorApplication.isPaused)
                Unpause();

            var gameView = GetGameView(false);
            GameInputDriverBehaviour.Ensure();
            RuntimeUiInputSetup.Ensure();
            var frameWidth = PositiveInt(request, "frameWidth");
            var frameHeight = PositiveInt(request, "frameHeight");
            var action = request.action ?? string.Empty;
            Action input;
            switch (action)
            {
                case "click":
                    input = Click(gameView, Point(request, gameView, frameWidth, frameHeight), Button(request), 1);
                    break;
                case "double-click":
                    input = Click(gameView, Point(request, gameView, frameWidth, frameHeight), Button(request), 2);
                    break;
                case "hover":
                    input = MouseInput(gameView, EventType.MouseMove, Point(request, gameView, frameWidth, frameHeight), 0, 0, Vector2.zero);
                    break;
                case "mouse-down":
                    input = MouseInput(gameView, EventType.MouseDown, Point(request, gameView, frameWidth, frameHeight), Button(request), ClickCount(request), Vector2.zero);
                    break;
                case "mouse-drag":
                    input = MouseInput(gameView, EventType.MouseDrag, Point(request, gameView, frameWidth, frameHeight), Button(request), 0, Vector2.zero);
                    break;
                case "mouse-up":
                    input = MouseInput(gameView, EventType.MouseUp, Point(request, gameView, frameWidth, frameHeight), Button(request), ClickCount(request), Vector2.zero);
                    break;
                case "scroll":
                    input = MouseInput(
                        gameView,
                        EventType.ScrollWheel,
                        Point(request, gameView, frameWidth, frameHeight),
                        0,
                        0,
                        new Vector2(Number(request, "deltaX"), Number(request, "deltaY")));
                    break;
                case "key-down":
                    input = KeyInput(EventType.KeyDown, request.name, true);
                    break;
                case "key-up":
                    input = KeyInput(EventType.KeyUp, request.name, false);
                    break;
                case "type-text":
                    input = TextInput(request.name);
                    break;
                default:
                    throw new ArgumentException("Unknown game input action: " + action, "action");
            }
            gameView.Repaint();
            return GameInputDriverBehaviour.Enqueue(input);
        }

        public static string PauseAndCapture()
        {
            return Capture(null, true);
        }

        public static string Capture(string name, bool pause)
        {
            if (!EditorApplication.isPlaying)
                throw new InvalidOperationException("The game is not running.");

            var gameView = GetGameView(false);
            RequireCamera(gameView);
            // Reading and encoding the full frame stalls the editor; paused, the stall is not counted as game time.
            // game_actions resumes the game for its next action, keeping held keys.
            PauseGame("game_actions");
            WaitForRenderedFrame(gameView);
            if (pause)
                InputSystemGameInput.ReleaseControl();
            var texture = ReadGameViewTexture();
            try
            {
                var directory = Path.Combine(BridgePaths.RuntimeRoot, "Screenshots");
                Directory.CreateDirectory(directory);
                var path = Path.Combine(directory, string.IsNullOrEmpty(name) ? "game.png" : Path.GetFileName(name) + ".png");
                var temporary = path + ".tmp";
                File.WriteAllBytes(temporary, texture.EncodeToPNG());
                if (File.Exists(path))
                    File.Delete(path);
                File.Move(temporary, path);
                return JsonUtility.ToJson(new GameFrame
                {
                    screenshot = path,
                    width = texture.width,
                    height = texture.height,
                    time = Time.time
                });
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(texture);
            }
        }

        public static string Pause()
        {
            if (!EditorApplication.isPlaying)
                return "Game is offline.";
            InputSystemGameInput.ReleaseControl();
            PauseGame("play pause");
            return "Game paused.";
        }

        public static string Resume()
        {
            if (!EditorApplication.isPlaying)
                throw new InvalidOperationException("The game is not running.");
            Application.runInBackground = true;
            Unpause();
            return "Game resumed.";
        }

        private static float pausedMaximumDeltaTime = -1f;
        private static int resumedFrame;

        // Unity counts the paused time into the next frame, up to Maximum Allowed Timestep (0.33 s by default); after
        // the agent's pause the game goes on as if it had not stopped. The setting is back two frames later.
        private static void Unpause()
        {
            if (EditorApplication.isPaused && pausedMaximumDeltaTime < 0f)
            {
                pausedMaximumDeltaTime = Time.maximumDeltaTime;
                Time.maximumDeltaTime = Time.fixedDeltaTime;
                resumedFrame = Time.frameCount;
                EditorApplication.update += RestoreMaximumDeltaTime;
            }
            EditorApplication.isPaused = false;
            EditorApplication.QueuePlayerLoopUpdate();
        }

        private static void RestoreMaximumDeltaTime()
        {
            if (EditorApplication.isPlaying && !EditorApplication.isPaused && Time.frameCount <= resumedFrame + 1)
                return;
            EditorApplication.update -= RestoreMaximumDeltaTime;
            Time.maximumDeltaTime = pausedMaximumDeltaTime;
            pausedMaximumDeltaTime = -1f;
        }

        public static string Step()
        {
            if (!EditorApplication.isPlaying)
                throw new InvalidOperationException("The game is not running.");
            InputSystemGameInput.ReleaseControl();
            PauseGame("play step");
            EditorApplication.Step();
            return Time.frameCount.ToString(CultureInfo.InvariantCulture);
        }

        private static float scaledFrom = float.NaN;
        private static float scaledTo = float.NaN;

        // The wait's own scale comes off only if nothing else changed Time.timeScale meanwhile: the Recorder holds it at 0
        // until its first frame and then puts back what it found, and the game may set its own.
        public static string RestoreTimeScale()
        {
            if (EditorApplication.isPlaying && !float.IsNaN(scaledFrom) && Time.timeScale == scaledTo)
                Time.timeScale = scaledFrom;
            scaledFrom = scaledTo = float.NaN;
            return "Game time scale restored.";
        }

        public static string MultiplyTimeScale(string rawValue)
        {
            if (!EditorApplication.isPlaying)
                throw new InvalidOperationException("Game time scale requires Play Mode.");
            float multiplier;
            if (!float.TryParse(rawValue, NumberStyles.Float, CultureInfo.InvariantCulture, out multiplier) ||
                float.IsNaN(multiplier) || float.IsInfinity(multiplier) || multiplier <= 0f)
                throw new ArgumentException("Game time scale multiplier must be positive.", "rawValue");
            var previous = Time.timeScale;
            Time.timeScale = Mathf.Min(100f, previous * multiplier);
            scaledFrom = previous;
            scaledTo = Time.timeScale;
            return previous.ToString("R", CultureInfo.InvariantCulture);
        }

        private static double pauseTarget = double.NaN;

        // game_actions wait: the game stops on the first frame at the target game time instead of when the agent's
        // next poll arrives, so a following screenshot shows exactly that moment.
        public static string PauseAt(string rawValue)
        {
            if (!EditorApplication.isPlaying)
                throw new InvalidOperationException("Game time requires Play Mode.");
            pauseTarget = double.Parse(rawValue, NumberStyles.Float, CultureInfo.InvariantCulture);
            EditorApplication.update -= PauseOnCondition;
            EditorApplication.update -= PauseOnTarget;
            EditorApplication.update += PauseOnTarget;
            return "Pause scheduled.";
        }

        private static void PauseOnTarget()
        {
            if (EditorApplication.isPlaying && Time.timeAsDouble < pauseTarget)
                return;
            EditorApplication.update -= PauseOnTarget;
            if (EditorApplication.isPlaying)
                PauseGame("game_actions");
            pauseTarget = double.NaN;
        }

        [Serializable]
        private sealed class WaitCondition
        {
            public string path;
            public string state;
            public double seconds;
            // Game time stopped (a pause menu): the limit counts real time.
            public bool realTime;
            [NonSerialized] public double target;
        }

        private static WaitCondition waitCondition;
        private static string waitResult = string.Empty;

        // game_actions wait with an object: Playwright's locator.waitFor. The game stops on the first frame the object
        // is in that state, or at the time limit.
        public static string PauseWhen(string json)
        {
            if (!EditorApplication.isPlaying)
                throw new InvalidOperationException("Game time requires Play Mode.");
            var condition = JsonUtility.FromJson<WaitCondition>(json);
            if (condition == null || string.IsNullOrWhiteSpace(condition.path))
                throw new ArgumentException("wait needs the object's path.");
            if (Array.IndexOf(new[] { "visible", "hidden", "attached", "detached" }, condition.state) < 0)
                throw new ArgumentException("wait state must be visible, hidden, attached or detached.");
            condition.target = Now(condition) + condition.seconds;
            waitCondition = condition;
            waitResult = "running";
            EditorApplication.update -= PauseOnTarget;
            EditorApplication.update -= PauseOnCondition;
            EditorApplication.update += PauseOnCondition;
            return "Pause scheduled.";
        }

        public static string WaitStatus()
        {
            return waitResult;
        }

        private static void PauseOnCondition()
        {
            if (!EditorApplication.isPlaying)
            {
                EditorApplication.update -= PauseOnCondition;
                waitResult = "timeout: the game stopped";
                return;
            }
            if (EditorApplication.isPaused)
                return;
            string why;
            var met = StateMet(waitCondition, out why);
            if (!met && Now(waitCondition) < waitCondition.target)
                return;
            EditorApplication.update -= PauseOnCondition;
            waitResult = met ? "met" : "timeout: " + why;
            PauseGame("game_actions");
        }

        private static double Now(WaitCondition condition)
        {
            return condition.realTime ? EditorApplication.timeSinceStartup : Time.timeAsDouble;
        }

        private static bool StateMet(WaitCondition condition, out string why)
        {
            GameObject target = null;
            try
            {
                target = ScenePath.ResolveObject(condition.path);
            }
            catch (Exception)
            {
            }
            var problem = target == null ? "no object at this path" : VisibilityProblem(target);
            switch (condition.state)
            {
                case "attached":
                    why = problem == "no object at this path" ? problem : null;
                    return target != null;
                case "detached":
                    why = "the object is still there";
                    return target == null;
                case "visible":
                    why = problem;
                    return problem == null;
                default:
                    why = "it is still visible";
                    return problem != null;
            }
        }

        // Visible as a player sees it: active, and some renderer inside a game camera's view or some UI graphic drawn.
        private static string VisibilityProblem(GameObject target)
        {
            if (!target.activeInHierarchy)
                return "inactive";
            var renderers = target.GetComponentsInChildren<Renderer>(false);
            var graphics = Array.FindAll(target.GetComponentsInChildren<Behaviour>(false), IsGraphic);
            if (renderers.Length == 0 && graphics.Length == 0)
                return null;
            var cameras = Array.FindAll(Camera.allCameras, camera => camera.cameraType == CameraType.Game && camera.targetTexture == null);
            foreach (var renderer in renderers)
            {
                if (!renderer.enabled)
                    continue;
                if (cameras.Length == 0)
                    return null;
                foreach (var camera in cameras)
                    if ((camera.cullingMask & (1 << renderer.gameObject.layer)) != 0 &&
                        GeometryUtility.TestPlanesAABB(GeometryUtility.CalculateFrustumPlanes(camera), renderer.bounds))
                        return null;
            }
            foreach (var graphic in graphics)
            {
                var canvasRenderer = graphic.GetComponent<CanvasRenderer>();
                var canvas = graphic.GetComponentInParent<Canvas>();
                if (graphic.isActiveAndEnabled && canvasRenderer != null && !canvasRenderer.cull &&
                    canvasRenderer.GetInheritedAlpha() > 0.01f && canvas != null && canvas.isActiveAndEnabled)
                    return null;
            }
            return renderers.Length > 0 ? "outside the camera view or its renderers are off" : "its UI is transparent or off";
        }

        private static bool IsGraphic(Behaviour behaviour)
        {
            for (var type = behaviour.GetType(); type != null; type = type.BaseType)
                if (type.FullName == "UnityEngine.UI.Graphic")
                    return true;
            return false;
        }

        public static string GameTime()
        {
            if (!EditorApplication.isPlaying)
                throw new InvalidOperationException("Game time requires Play Mode.");
            return Time.timeAsDouble.ToString("R", CultureInfo.InvariantCulture);
        }

        internal static EditorWindow GetGameView(bool focus)
        {
            return EditorWindow.GetWindow(GameViewType, false, "Game", focus);
        }

        private static Rect GetTargetRect(EditorWindow gameView)
        {
            if (TargetInView == null)
                throw new MissingMemberException("This Unity version does not expose the Game View target rectangle.");
            return (Rect)TargetInView.GetValue(gameView, null);
        }

        private static RenderTexture GetRenderTexture(EditorWindow gameView)
        {
            if (GameRenderTexture == null)
                throw new MissingMemberException("This Unity version does not expose the Game View render texture.");
            return GameRenderTexture.GetValue(gameView) as RenderTexture;
        }

        private static int shadersReadyFrame = -1;
        private static double shadersReadyTime;

        // Shaders still compiling draw as cyan placeholders; a capture waits for them and a frame drawn after.
        internal static bool ShadersReady()
        {
            if (ShaderUtil.anythingCompiling)
            {
                shadersReadyFrame = Time.frameCount + 2;
                shadersReadyTime = EditorApplication.timeSinceStartup + 0.5d;
                return false;
            }
            return Time.frameCount >= shadersReadyFrame || EditorApplication.timeSinceStartup >= shadersReadyTime;
        }

        internal static bool HasRenderedFrame()
        {
            if (!EditorApplication.isPlaying)
                return false;
            var gameView = GetGameView(false);
            Application.runInBackground = true;
            // Paused for a capture, the Game View already holds the frame; a player loop update would step the game.
            if (!EditorApplication.isPaused)
                EditorApplication.QueuePlayerLoopUpdate();
            gameView.Repaint();
            var target = GetTargetRect(gameView);
            var renderTexture = GetRenderTexture(gameView);
            return target.width > 0f && target.height > 0f && renderTexture != null &&
                renderTexture.width > 0 && renderTexture.height > 0;
        }

        private static void RequireCamera(EditorWindow gameView)
        {
            var error = CameraError(gameView);
            if (!string.IsNullOrEmpty(error))
                throw new InvalidOperationException(error);
        }

        private static string CameraError(EditorWindow gameView)
        {
            var display = GetTargetDisplay(gameView);
            foreach (var camera in Resources.FindObjectsOfTypeAll<Camera>())
            {
                if (camera != null && camera.gameObject.scene.IsValid() && camera.gameObject.scene.isLoaded &&
                    camera.isActiveAndEnabled && camera.targetTexture == null &&
                    camera.targetDisplay == display)
                    return null;
            }
            return "Unity Game View Display " + (display + 1) + " has no active camera rendering.";
        }

        private static int GetTargetDisplay(EditorWindow gameView)
        {
            object value = null;
            if (TargetDisplay != null)
                value = TargetDisplay.GetValue(gameView, null);
            else if (TargetDisplayField != null)
                value = TargetDisplayField.GetValue(gameView);
            return value == null ? 0 : Convert.ToInt32(value, CultureInfo.InvariantCulture);
        }

        private static Vector2 Point(BridgeRequest request, EditorWindow gameView, int frameWidth, int frameHeight)
        {
            var x = Number(request, "x");
            var y = Number(request, "y");
            if (x < 0f || x >= frameWidth || y < 0f || y >= frameHeight)
                throw new ArgumentOutOfRangeException("values", "Game input point is outside the last screenshot.");
            var renderTexture = GetRenderTexture(gameView);
            if (renderTexture == null || renderTexture.width <= 0 || renderTexture.height <= 0)
                throw new BridgeNotReadyException("Unity Game View has no rendered frame yet.");
            return new Vector2(
                (x + 0.5f) * renderTexture.width / frameWidth,
                (y + 0.5f) * renderTexture.height / frameHeight);
        }

        private static int Button(BridgeRequest request)
        {
            var value = Text(request, "button");
            if (value == "left") return 0;
            if (value == "right") return 1;
            if (value == "middle") return 2;
            throw new ArgumentException("Mouse button must be left, right, or middle.", "values");
        }

        private static int ClickCount(BridgeRequest request)
        {
            foreach (var entry in request.values ?? Array.Empty<PropertyValue>())
            {
                int value;
                if (string.Equals(entry.path, "clickCount", StringComparison.Ordinal) &&
                    int.TryParse(entry.value, NumberStyles.Integer, CultureInfo.InvariantCulture, out value) && value > 0)
                    return value;
            }
            return 1;
        }

        private static Action Click(EditorWindow gameView, Vector2 point, int button, int count)
        {
            return delegate
            {
                for (var click = 1; click <= count; click++)
                {
                    SendMouse(gameView, EventType.MouseDown, point, button, click, Vector2.zero);
                    SendMouse(gameView, EventType.MouseUp, point, button, click, Vector2.zero);
                }
            };
        }

        private static Action MouseInput(EditorWindow gameView, EventType type, Vector2 point, int button, int clickCount, Vector2 delta)
        {
            return delegate { SendMouse(gameView, type, point, button, clickCount, delta); };
        }

        private static void SendMouse(EditorWindow gameView, EventType type, Vector2 point, int button, int clickCount, Vector2 delta)
        {
            var renderTexture = GetRenderTexture(gameView);
            if (renderTexture == null || renderTexture.width <= 0 || renderTexture.height <= 0)
                throw new BridgeNotReadyException("Unity Game View has no rendered frame yet.");
            var screenPoint = new Vector2(point.x, renderTexture.height - point.y);
            InputSystemGameInput.SetMouse(type, screenPoint, button, clickCount, delta);
        }

        private static Action KeyInput(EventType type, string name, bool pressed)
        {
            return delegate { InputSystemGameInput.SetKey(name, pressed); };
        }

        private static Action TextInput(string text)
        {
            if (string.IsNullOrEmpty(text))
                throw new ArgumentException("Text is required.", "name");
            return delegate { InputSystemGameInput.TypeText(text); };
        }

        internal static RenderTexture GameViewTexture()
        {
            return GetRenderTexture(GetGameView(false));
        }

        private static Texture2D ReadGameViewTexture()
        {
            var renderTexture = GameViewTexture();
            if (renderTexture == null || renderTexture.width <= 0 || renderTexture.height <= 0)
                throw new BridgeNotReadyException("Unity Game View has no rendered frame yet.");
            return ReadTexture(renderTexture);
        }

        internal static Texture2D ReadTexture(RenderTexture renderTexture)
        {
            var previous = RenderTexture.active;
            var texture = new Texture2D(renderTexture.width, renderTexture.height, TextureFormat.RGB24, false);
            try
            {
                RenderTexture.active = renderTexture;
                texture.ReadPixels(new Rect(0f, 0f, renderTexture.width, renderTexture.height), 0, 0, false);
                texture.Apply(false, false);
                FlipVertically(texture);
                return texture;
            }
            catch
            {
                UnityEngine.Object.DestroyImmediate(texture);
                throw;
            }
            finally
            {
                RenderTexture.active = previous;
            }
        }

        private static void FlipVertically(Texture2D texture)
        {
            var pixels = texture.GetPixels32();
            var width = texture.width;
            var height = texture.height;
            for (var y = 0; y < height / 2; y++)
            {
                var opposite = height - y - 1;
                for (var x = 0; x < width; x++)
                {
                    var top = y * width + x;
                    var bottom = opposite * width + x;
                    var swap = pixels[top];
                    pixels[top] = pixels[bottom];
                    pixels[bottom] = swap;
                }
            }
            texture.SetPixels32(pixels);
            texture.Apply(false, false);
        }


        private static int PositiveInt(BridgeRequest request, string name)
        {
            int value;
            if (!int.TryParse(Text(request, name), NumberStyles.Integer, CultureInfo.InvariantCulture, out value) || value <= 0)
                throw new ArgumentException(name + " must be a positive integer.", "values");
            return value;
        }

        private static float Number(BridgeRequest request, string name)
        {
            float value;
            if (!float.TryParse(Text(request, name), NumberStyles.Float, CultureInfo.InvariantCulture, out value) || float.IsNaN(value) || float.IsInfinity(value))
                throw new ArgumentException(name + " must be a finite number.", "values");
            return value;
        }

        private static string Text(BridgeRequest request, string name)
        {
            foreach (var value in request.values ?? Array.Empty<PropertyValue>())
                if (string.Equals(value.path, name, StringComparison.Ordinal))
                    return value.value;
            throw new ArgumentException("Missing game input value: " + name, "values");
        }
    }
}
