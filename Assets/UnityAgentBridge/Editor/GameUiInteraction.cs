using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using UnityEditor;
using UnityEngine;

namespace UnityAgentBridge.Editor
{
    internal static class InputSystemGameInput
    {
        private const BindingFlags PublicStatic = BindingFlags.Public | BindingFlags.Static;
        private const BindingFlags PublicInstance = BindingFlags.Public | BindingFlags.Instance;
        private const string VirtualKeyboardName = "UnityAgentBridgeKeyboard";
        private const string VirtualMouseName = "UnityAgentBridgeMouse";
        private static readonly HashSet<string> HeldKeys = new HashSet<string>(StringComparer.Ordinal);
        private static readonly HashSet<int> PairedPlayerInputs = new HashSet<int>();
        private static Type inputSystemType;
        private static Type inputDeviceType;
        private static Type keyboardType;
        private static Type mouseType;
        private static Type keyType;
        private static Type keyboardStateType;
        private static Type mouseStateType;
        private static Type mouseButtonType;
        private static MethodInfo queueStateEvent;
        private static MethodInfo queueTextEvent;
        private static MethodInfo enableDevice;
        private static MethodInfo disableDevice;
        private static MethodInfo addDevice;
        private static MethodInfo removeDevice;
        private static MethodInfo makeCurrent;
        private static PropertyInfo deviceAdded;
        private static PropertyInfo playerInputs;
        private static PropertyInfo playerInputUser;
        private static MethodInfo pairWithDevice;
        private static Type pairingOptionsType;
        private static object keyboard;
        private static object mouse;
        private static object physicalKeyboard;
        private static object physicalMouse;
        private static Vector2 mousePosition;
        private static int heldMouseButtons;
        private static bool physicalDevicesSuspended;

        internal static void EnsureAvailable()
        {
            // Every key press lands here; the full check reads packages and ProjectSettings and costs ~400 KB of GC.
            if (keyboard != null && mouse != null && (bool)deviceAdded.GetValue(keyboard, null) && (bool)deviceAdded.GetValue(mouse, null))
            {
                CapturePhysicalDevices();
                // Input System disables devices while the editor is unfocused.
                enableDevice.Invoke(null, new[] { keyboard });
                enableDevice.Invoke(null, new[] { mouse });
                SuspendPhysicalDevices();
                PairWithPlayerInputs();
                return;
            }
            InputSystemSetupService.RequireReady();
            if (inputSystemType == null)
                ResolveApi();

            CapturePhysicalDevices();
            keyboard = FindDevice(VirtualKeyboardName) ?? AddVirtualDevice("Keyboard", VirtualKeyboardName);
            mouse = FindDevice(VirtualMouseName) ?? AddVirtualDevice("Mouse", VirtualMouseName);

            enableDevice.Invoke(null, new[] { keyboard });
            enableDevice.Invoke(null, new[] { mouse });
            SuspendPhysicalDevices();
            PairWithPlayerInputs();
        }

        internal static void Reset()
        {
            if (inputSystemType != null)
            {
                CapturePhysicalDevices();
                RemoveVirtualDevice(keyboard);
                RemoveVirtualDevice(mouse);
                ResumePhysicalDevices();
                RestorePhysicalCurrent();
            }
            HeldKeys.Clear();
            PairedPlayerInputs.Clear();
            heldMouseButtons = 0;
            mousePosition = Vector2.zero;
            keyboard = null;
            mouse = null;
            RuntimeUiInputSetup.Reset();
        }

        internal static void ReleaseControl()
        {
            ResumePhysicalDevices();
            RestorePhysicalCurrent();
        }

        internal static void SetKey(string name, bool pressed)
        {
            EnsureAvailable();
            var key = KeyName(name);
            if (pressed)
                HeldKeys.Add(key);
            else
                HeldKeys.Remove(key);
            QueueKeyboardState();
        }

        internal static void TypeText(string text)
        {
            if (string.IsNullOrEmpty(text))
                throw new ArgumentException("Text is required.", "text");
            EnsureAvailable();
            foreach (var character in text)
                QueueText(character);
        }

        internal static void SetMouse(
            EventType type,
            Vector2 position,
            int button,
            int clickCount,
            Vector2 scrollDelta)
        {
            EnsureAvailable();
            var movement = position - mousePosition;
            mousePosition = position;
            if (type == EventType.MouseDown)
                heldMouseButtons |= 1 << button;
            else if (type == EventType.MouseUp)
                heldMouseButtons &= ~(1 << button);

            var state = Activator.CreateInstance(mouseStateType);
            mouseStateType.GetField("position", PublicInstance).SetValue(state, mousePosition);
            mouseStateType.GetField("delta", PublicInstance).SetValue(state, movement);
            mouseStateType.GetField("scroll", PublicInstance).SetValue(
                state,
                type == EventType.ScrollWheel ? new Vector2(-scrollDelta.x, -scrollDelta.y) : Vector2.zero);
            mouseStateType.GetField("clickCount", PublicInstance).SetValue(state, (ushort)Math.Max(0, clickCount));

            var withButton = mouseStateType.GetMethod("WithButton", PublicInstance);
            for (var index = 0; index < 3; index++)
            {
                if ((heldMouseButtons & (1 << index)) == 0)
                    continue;
                state = withButton.Invoke(state, new[] { Enum.ToObject(mouseButtonType, index), (object)true });
            }
            QueueState(mouse, state, mouseStateType);
        }

        private static void QueueKeyboardState()
        {
            var keys = Array.CreateInstance(keyType, HeldKeys.Count);
            var index = 0;
            foreach (var name in HeldKeys.OrderBy(value => value, StringComparer.Ordinal))
                keys.SetValue(Enum.Parse(keyType, name), index++);
            var constructor = keyboardStateType.GetConstructor(new[] { keyType.MakeArrayType() });
            if (constructor == null)
                throw new MissingMethodException(keyboardStateType.FullName, ".ctor(Key[])");
            QueueState(keyboard, constructor.Invoke(new object[] { keys }), keyboardStateType);
        }

        private static void QueueState(object device, object state, Type stateType)
        {
            makeCurrent.Invoke(device, null);
            queueStateEvent.MakeGenericMethod(stateType).Invoke(null, new[] { device, state, (object)(-1d) });
        }

        private static void QueueText(char character)
        {
            makeCurrent.Invoke(keyboard, null);
            queueTextEvent.Invoke(null, new[] { keyboard, (object)character, -1d });
        }

        private static void ResolveApi()
        {
            inputSystemType = RequiredType("UnityEngine.InputSystem.InputSystem");
            inputDeviceType = RequiredType("UnityEngine.InputSystem.InputDevice");
            keyboardType = RequiredType("UnityEngine.InputSystem.Keyboard");
            mouseType = RequiredType("UnityEngine.InputSystem.Mouse");
            keyType = RequiredType("UnityEngine.InputSystem.Key");
            keyboardStateType = RequiredType("UnityEngine.InputSystem.LowLevel.KeyboardState");
            mouseStateType = RequiredType("UnityEngine.InputSystem.LowLevel.MouseState");
            mouseButtonType = RequiredType("UnityEngine.InputSystem.LowLevel.MouseButton");
            queueStateEvent = inputSystemType.GetMethods(PublicStatic).FirstOrDefault(method =>
                method.Name == "QueueStateEvent" && method.IsGenericMethodDefinition &&
                method.GetGenericArguments().Length == 1 && method.GetParameters().Length == 3);
            queueTextEvent = inputSystemType.GetMethods(PublicStatic).FirstOrDefault(method =>
                method.Name == "QueueTextEvent" && method.GetParameters().Length == 3);
            enableDevice = inputSystemType.GetMethods(PublicStatic).FirstOrDefault(method =>
                method.Name == "EnableDevice" && method.GetParameters().Length == 1);
            disableDevice = inputSystemType.GetMethods(PublicStatic).FirstOrDefault(method =>
                method.Name == "DisableDevice" && method.GetParameters().Length == 2);
            addDevice = inputSystemType.GetMethods(PublicStatic).FirstOrDefault(method =>
            {
                if (method.Name != "AddDevice" || method.IsGenericMethod || method.GetParameters().Length != 3)
                    return false;
                var parameters = method.GetParameters();
                return parameters[0].ParameterType == typeof(string) && parameters[1].ParameterType == typeof(string);
            });
            removeDevice = inputSystemType.GetMethods(PublicStatic).FirstOrDefault(method =>
                method.Name == "RemoveDevice" && method.GetParameters().Length == 1);
            makeCurrent = inputDeviceType.GetMethod("MakeCurrent", PublicInstance);
            deviceAdded = inputDeviceType.GetProperty("added", PublicInstance);
            if (deviceAdded == null || queueStateEvent == null || queueTextEvent == null || enableDevice == null || disableDevice == null ||
                addDevice == null || removeDevice == null || makeCurrent == null)
                throw new MissingMethodException("Installed Unity Input System does not expose the required input event API.");
        }

        private static object AddVirtualDevice(string layout, string name)
        {
            var device = addDevice.Invoke(null, new object[] { layout, name, null });
            if (device == null)
                throw new InvalidOperationException("Unity Input System did not create virtual device " + name + ".");
            return device;
        }

        private static object FindDevice(string name)
        {
            var devices = inputSystemType.GetProperty("devices", PublicStatic).GetValue(null, null) as IEnumerable;
            if (devices == null)
                throw new InvalidOperationException("Unity Input System device list is unavailable.");
            var nameProperty = inputDeviceType.GetProperty("name", PublicInstance);
            foreach (var device in devices)
                if (string.Equals((string)nameProperty.GetValue(device, null), name, StringComparison.Ordinal))
                    return device;
            return null;
        }

        private static void CapturePhysicalDevices()
        {
            var currentKeyboard = keyboardType.GetProperty("current", PublicStatic).GetValue(null, null);
            var currentMouse = mouseType.GetProperty("current", PublicStatic).GetValue(null, null);
            // Leaving Play Mode removes the virtual devices, but current still names them until another device is used.
            if (IsPhysical(currentKeyboard))
                physicalKeyboard = currentKeyboard;
            if (IsPhysical(currentMouse))
                physicalMouse = currentMouse;
            if (!IsAdded(physicalKeyboard))
                physicalKeyboard = null;
            if (!IsAdded(physicalMouse))
                physicalMouse = null;
        }

        private static bool IsAdded(object device)
        {
            return device != null && (bool)deviceAdded.GetValue(device, null);
        }

        private static bool IsPhysical(object device)
        {
            if (!IsAdded(device))
                return false;
            var name = (string)inputDeviceType.GetProperty("name", PublicInstance).GetValue(device, null);
            return name != VirtualKeyboardName && name != VirtualMouseName;
        }

        private static void RestorePhysicalCurrent()
        {
            if (IsAdded(physicalKeyboard))
                makeCurrent.Invoke(physicalKeyboard, null);
            if (IsAdded(physicalMouse))
                makeCurrent.Invoke(physicalMouse, null);
        }

        private static void SuspendPhysicalDevices()
        {
            if (physicalDevicesSuspended)
                return;
            if (physicalKeyboard != null)
                disableDevice.Invoke(null, new[] { physicalKeyboard, (object)false });
            if (physicalMouse != null)
                disableDevice.Invoke(null, new[] { physicalMouse, (object)false });
            physicalDevicesSuspended = true;
        }

        private static void ResumePhysicalDevices()
        {
            if (!physicalDevicesSuspended)
                return;
            if (IsAdded(physicalKeyboard))
                enableDevice.Invoke(null, new[] { physicalKeyboard });
            if (IsAdded(physicalMouse))
                enableDevice.Invoke(null, new[] { physicalMouse });
            physicalDevicesSuspended = false;
        }

        private static void RemoveVirtualDevice(object device)
        {
            if (IsAdded(device))
                removeDevice.Invoke(null, new[] { device });
        }

        private static void PairWithPlayerInputs()
        {
            if (pairWithDevice == null)
            {
                var playerInputType = Type.GetType("UnityEngine.InputSystem.PlayerInput, Unity.InputSystem", false);
                var inputUserType = Type.GetType("UnityEngine.InputSystem.Users.InputUser, Unity.InputSystem", false);
                pairingOptionsType = Type.GetType("UnityEngine.InputSystem.Users.InputUserPairingOptions, Unity.InputSystem", false);
                if (playerInputType == null || inputUserType == null || pairingOptionsType == null)
                    throw new InvalidOperationException("Unity Input System PlayerInput API has not finished loading.");
                playerInputs = playerInputType.GetProperty("all", PublicStatic);
                playerInputUser = playerInputType.GetProperty("user", PublicInstance);
                pairWithDevice = inputUserType.GetMethods(PublicStatic).FirstOrDefault(method =>
                    method.Name == "PerformPairingWithDevice" && method.GetParameters().Length == 3);
                if (playerInputs == null || playerInputUser == null || pairWithDevice == null)
                    throw new MissingMethodException("Installed Unity Input System does not expose PlayerInput device pairing.");
            }

            var all = playerInputs.GetValue(null, null) as IEnumerable;
            if (all == null)
                return;
            foreach (var value in all)
            {
                var playerInput = value as UnityEngine.Object;
                if (playerInput == null || !PairedPlayerInputs.Add(UnityObjectIdentity.TransientHash(playerInput)))
                    continue;
                var user = playerInputUser.GetValue(value, null);
                var options = Enum.ToObject(pairingOptionsType, 0);
                pairWithDevice.Invoke(null, new[] { keyboard, user, options });
                pairWithDevice.Invoke(null, new[] { mouse, user, options });
            }
        }

        private static Type RequiredType(string name)
        {
            var type = Type.GetType(name + ", Unity.InputSystem", false);
            if (type == null)
                throw new InvalidOperationException("Unity Input System has not finished loading: " + name);
            return type;
        }

        private static string KeyName(string name)
        {
            if (string.IsNullOrWhiteSpace(name))
                throw new ArgumentException("Key is required.", "name");
            var value = name.Trim().ToUpperInvariant();
            if (value.Length == 1 && value[0] >= 'A' && value[0] <= 'Z')
                return value;
            if (value.Length == 1 && value[0] >= '0' && value[0] <= '9')
                return "Digit" + value;
            int function;
            if (value.StartsWith("F", StringComparison.Ordinal) &&
                int.TryParse(value.Substring(1), out function) && function >= 1 && function <= 15)
                return "F" + function;
            switch (value)
            {
                case "CTRL": case "CONTROL": return "LeftCtrl";
                case "SHIFT": return "LeftShift";
                case "ALT": return "LeftAlt";
                case "WIN": case "LWIN": return "LeftMeta";
                case "RWIN": return "RightMeta";
                case "ENTER": case "RETURN": return "Enter";
                case "ESC": case "ESCAPE": return "Escape";
                case "SPACE": return "Space";
                case "TAB": return "Tab";
                case "BACKSPACE": return "Backspace";
                case "DELETE": return "Delete";
                case "INSERT": return "Insert";
                case "HOME": return "Home";
                case "END": return "End";
                case "PAGEUP": return "PageUp";
                case "PAGEDOWN": return "PageDown";
                case "LEFT": return "LeftArrow";
                case "RIGHT": return "RightArrow";
                case "UP": return "UpArrow";
                case "DOWN": return "DownArrow";
                default: throw new ArgumentException("Unsupported key: " + name, "name");
            }
        }
    }

    internal static class RuntimeUiInputSetup
    {
        private static readonly HashSet<int> ConfiguredObjects = new HashSet<int>();

        internal static void Ensure()
        {
            var legacyType = Type.GetType("UnityEngine.EventSystems.StandaloneInputModule, UnityEngine.UI", false);
            var inputSystemUiType = Type.GetType("UnityEngine.InputSystem.UI.InputSystemUIInputModule, Unity.InputSystem", false);
            if (legacyType == null || inputSystemUiType == null)
                return;

#if UNITY_6000_0_OR_NEWER
            var legacyModules = UnityEngine.Object.FindObjectsByType(legacyType, FindObjectsInactive.Include);
#else
            var legacyModules = UnityEngine.Object.FindObjectsOfType(legacyType, true);
#endif
            foreach (var value in legacyModules)
            {
                var legacy = value as Behaviour;
                if (legacy == null || !legacy.gameObject.scene.IsValid())
                    continue;
                legacy.enabled = false;
                var id = UnityObjectIdentity.TransientHash(legacy.gameObject);
                if (!ConfiguredObjects.Add(id) || legacy.gameObject.GetComponent(inputSystemUiType) != null)
                    continue;
                var module = legacy.gameObject.AddComponent(inputSystemUiType);
                module.hideFlags = HideFlags.DontSaveInEditor;
            }
        }

        internal static void Reset()
        {
            ConfiguredObjects.Clear();
        }
    }

    internal sealed class GameInputDriverBehaviour : MonoBehaviour
    {
        private sealed class PendingInput
        {
            internal long serial;
            internal Action action;
        }

        private static readonly Queue<PendingInput> Queue = new Queue<PendingInput>();
        private static readonly Dictionary<long, Exception> Failures = new Dictionary<long, Exception>();
        private static GameInputDriverBehaviour instance;
        private static long nextSerial;
        private static long lastCompletedSerial;
        private static int lastCompletedFrame = -1;
        private static int lastUpdateFrame = -1;

        internal static bool Ready
        {
            get { return instance != null && lastUpdateFrame >= 0; }
        }

        internal static bool AllSettled
        {
            get
            {
                return Queue.Count == 0 &&
                    (lastCompletedFrame < 0 || Time.frameCount > lastCompletedFrame);
            }
        }

        internal static void Ensure()
        {
            if (instance != null)
                return;
            var owner = new GameObject("Unity Agent Bridge Input")
            {
                hideFlags = HideFlags.HideAndDontSave
            };
            DontDestroyOnLoad(owner);
            instance = owner.AddComponent<GameInputDriverBehaviour>();
        }

        internal static long Enqueue(Action action)
        {
            if (action == null)
                throw new ArgumentNullException("action");
            Ensure();
            var serial = ++nextSerial;
            Queue.Enqueue(new PendingInput { serial = serial, action = action });
            EditorApplication.QueuePlayerLoopUpdate();
            return serial;
        }

        internal static bool IsSettled(long serial)
        {
            if (Failures.ContainsKey(serial))
                return true;
            return lastCompletedSerial >= serial && Time.frameCount > lastCompletedFrame;
        }

        internal static void ThrowIfFailed(long serial)
        {
            Exception error;
            if (!Failures.TryGetValue(serial, out error))
                return;
            Failures.Remove(serial);
            throw error;
        }

        internal static void ResetState()
        {
            Queue.Clear();
            Failures.Clear();
            nextSerial = 0;
            lastCompletedSerial = 0;
            lastCompletedFrame = -1;
            lastUpdateFrame = -1;
            if (instance != null)
                DestroyImmediate(instance.gameObject);
            instance = null;
        }

        private void Update()
        {
            lastUpdateFrame = Time.frameCount;
            if (EditorApplication.isPaused || Queue.Count == 0)
                return;
            var input = Queue.Dequeue();
            try
            {
                input.action();
            }
            catch (Exception error)
            {
                Failures[input.serial] = error;
            }
            lastCompletedSerial = input.serial;
            lastCompletedFrame = Time.frameCount;
        }

        private void OnDestroy()
        {
            if (ReferenceEquals(instance, this))
                instance = null;
        }
    }
}
