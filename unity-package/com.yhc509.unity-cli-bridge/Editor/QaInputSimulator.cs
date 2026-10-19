#nullable enable
using System;
using System.Diagnostics;
using System.Reflection;
using UnityCli.Protocol;
using UnityEditor;
using UnityEngine;
#if ENABLE_INPUT_SYSTEM
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.LowLevel;
#endif

#if ENABLE_INPUT_SYSTEM
namespace UnityCliBridge.Bridge.Editor
{
    /// <summary>
    /// Simulates QA input through Unity Input System devices.
    /// </summary>
    internal sealed class QaInputSimulator
    {
        private const float PressedValue = 1f;
        private const float ReleasedValue = 0f;
        private const int DefaultFrameDurationMs = 16;

        private QaInputSimulator()
        {
        }

        /// <summary>
        /// Queues a key press and release in the current frame.
        /// </summary>
        public static void SimulateKey(string keyName)
        {
            if (QaLegacyInputSimulator.IsLegacyOnly)
            {
                QaLegacyInputSimulator.SimulateKey(keyName);
                return;
            }

            Keyboard keyboard = RequireKeyboard();
            if (!Enum.TryParse(keyName, true, out Key key))
            {
                throw new CommandFailureException("QA_INVALID_KEY", $"Unknown key name: {keyName}. Use Input System Key enum values.", false, null);
            }

            QueueKeyboardState(keyboard, key, true);
            QueueKeyboardState(keyboard, key, false);
        }

        /// <summary>
        /// Creates a swipe operation that advances one screen-space input step per editor update.
        /// </summary>
        public static IQaSwipeOperation BeginSwipe(Vector2 fromScreenPosition, Vector2 toScreenPosition, int durationMs)
        {
            return BeginSwipe(fromScreenPosition, toScreenPosition, durationMs, string.Empty);
        }

        public static IQaSwipeOperation BeginSwipe(Vector2 fromScreenPosition, Vector2 toScreenPosition, int durationMs, string? button)
        {
            if (QaLegacyInputSimulator.IsLegacyOnly)
            {
                return QaLegacyInputSimulator.BeginSwipe(fromScreenPosition, toScreenPosition, durationMs, button);
            }

            int normalizedDurationMs = durationMs > 0 ? durationMs : ProtocolConstants.DefaultQaSwipeDurationMs;
            int totalSteps = Mathf.Max(1, Mathf.CeilToInt(normalizedDurationMs / (float)DefaultFrameDurationMs));
            bool isRightButton = IsRightButton(button);

            Touchscreen? touchscreen = Touchscreen.current;
            if (!isRightButton && touchscreen != null)
            {
                return new SwipeOperation(fromScreenPosition, toScreenPosition, totalSteps, touchscreen);
            }

            Mouse? mouse = Mouse.current;
            if (mouse != null)
            {
                return new SwipeOperation(fromScreenPosition, toScreenPosition, totalSteps, mouse, isRightButton);
            }

            throw new CommandFailureException("QA_NO_POINTER_DEVICE", "No pointer device found in Input System.", false, null);
        }

        /// <summary>
        /// Queues a pointer press and release at the given screen position in the current frame.
        /// Mirrors SimulateKey's same-frame press/release; reaches Input System polling that reads
        /// Mouse/Touchscreen state. Prefers Touchscreen, then Mouse.
        /// </summary>
        public static void SimulateTap(Vector2 screenPosition)
        {
            SimulateTap(screenPosition, string.Empty);
        }

        public static void SimulateTap(Vector2 screenPosition, string? button)
        {
            if (QaLegacyInputSimulator.IsLegacyOnly)
            {
                QaLegacyInputSimulator.SimulateTap(screenPosition, button);
                return;
            }

            bool isRightButton = IsRightButton(button);
            Touchscreen? touchscreen = Touchscreen.current;
            if (!isRightButton && touchscreen != null)
            {
                QueueTouchState(touchscreen, screenPosition, UnityEngine.InputSystem.TouchPhase.Began);
                QueueTouchState(touchscreen, screenPosition, UnityEngine.InputSystem.TouchPhase.Ended);
                return;
            }

            Mouse? mouse = Mouse.current;
            if (mouse != null)
            {
                QueueMouseState(mouse, screenPosition, true, isRightButton);
                QueueMouseState(mouse, screenPosition, false, isRightButton);
                return;
            }

            throw new CommandFailureException("QA_NO_POINTER_DEVICE", "No pointer device found in Input System.", false, null);
        }

        private static bool IsRightButton(string? button)
        {
            return string.Equals(button, "right", StringComparison.OrdinalIgnoreCase);
        }

        private static Keyboard RequireKeyboard()
        {
            Keyboard? keyboard = Keyboard.current;
            if (keyboard == null)
            {
                throw new CommandFailureException("QA_NO_KEYBOARD", "No keyboard device found in Input System.", false, null);
            }

            return keyboard;
        }

        private static void QueueKeyboardState(Keyboard keyboard, Key key, bool isPressed)
        {
            InputEventPtr eventPtr;
            using (StateEvent.From(keyboard, out eventPtr))
            {
                keyboard[key].WriteValueIntoEvent(isPressed ? PressedValue : ReleasedValue, eventPtr);
                InputSystem.QueueEvent(eventPtr);
            }
        }

        private static void QueueMouseState(Mouse mouse, Vector2 screenPosition, bool isPressed, bool isRightButton)
        {
            InputEventPtr eventPtr;
            using (StateEvent.From(mouse, out eventPtr))
            {
                mouse.position.WriteValueIntoEvent(screenPosition, eventPtr);
                if (isRightButton)
                {
                    mouse.rightButton.WriteValueIntoEvent(isPressed ? PressedValue : ReleasedValue, eventPtr);
                }
                else
                {
                    mouse.leftButton.WriteValueIntoEvent(isPressed ? PressedValue : ReleasedValue, eventPtr);
                }

                InputSystem.QueueEvent(eventPtr);
            }
        }

        private static void QueueTouchState(Touchscreen touchscreen, Vector2 position, UnityEngine.InputSystem.TouchPhase phase)
        {
            InputSystem.QueueStateEvent(touchscreen, new TouchState
            {
                touchId = 1,
                position = position,
                phase = phase,
            });
        }

        internal sealed class SwipeOperation : IQaSwipeOperation
        {
            private readonly Vector2 _from;
            private readonly Vector2 _to;
            private readonly int _totalSteps;
            private readonly Touchscreen? _touchscreen;
            private readonly Mouse? _mouse;
            private readonly bool _isRightButton;
            private Vector2 _lastPosition;
            private int _currentStep;
            private bool _isAborted;

            public SwipeOperation(Vector2 from, Vector2 to, int totalSteps, Touchscreen touchscreen)
            {
                _from = from;
                _to = to;
                _totalSteps = totalSteps;
                _touchscreen = touchscreen;
                _lastPosition = from;
            }

            public SwipeOperation(Vector2 from, Vector2 to, int totalSteps, Mouse mouse, bool isRightButton)
            {
                _from = from;
                _to = to;
                _totalSteps = totalSteps;
                _mouse = mouse;
                _isRightButton = isRightButton;
                _lastPosition = from;
            }

            public bool Advance()
            {
                if (_isAborted)
                {
                    return true;
                }

                float t = _totalSteps > 0 ? (float)_currentStep / _totalSteps : 1f;
                Vector2 position = Vector2.Lerp(_from, _to, t);
                _lastPosition = position;

                if (_touchscreen != null)
                {
                    QueueTouchState(_touchscreen, position, GetTouchPhase());
                }
                else if (_mouse != null)
                {
                    QueueMouseState(_mouse, position, _currentStep < _totalSteps, _isRightButton);
                }
                else
                {
                    throw new InvalidOperationException("Swipe operation has no active pointer device.");
                }

                bool isCompleted = _currentStep >= _totalSteps;
                _currentStep++;
                return isCompleted;
            }

            public void Abort()
            {
                if (_isAborted)
                {
                    return;
                }

                _isAborted = true;

                if (_touchscreen != null)
                {
                    QueueTouchState(_touchscreen, _lastPosition, UnityEngine.InputSystem.TouchPhase.Canceled);
                    return;
                }

                if (_mouse != null)
                {
                    QueueMouseState(_mouse, _lastPosition, false, _isRightButton);
                }
            }

            private UnityEngine.InputSystem.TouchPhase GetTouchPhase()
            {
                if (_currentStep == 0)
                {
                    return UnityEngine.InputSystem.TouchPhase.Began;
                }

                if (_currentStep >= _totalSteps)
                {
                    return UnityEngine.InputSystem.TouchPhase.Ended;
                }

                return UnityEngine.InputSystem.TouchPhase.Moved;
            }
        }
    }
}
#else
namespace UnityCliBridge.Bridge.Editor
{
    /// <summary>
    /// Without the Input System package every QA input goes through the legacy Input Manager simulator.
    /// </summary>
    internal static class QaInputSimulator
    {
        public static void SimulateKey(string keyName) => QaLegacyInputSimulator.SimulateKey(keyName);

        public static IQaSwipeOperation BeginSwipe(Vector2 fromScreenPosition, Vector2 toScreenPosition, int durationMs)
            => QaLegacyInputSimulator.BeginSwipe(fromScreenPosition, toScreenPosition, durationMs, string.Empty);

        public static IQaSwipeOperation BeginSwipe(Vector2 fromScreenPosition, Vector2 toScreenPosition, int durationMs, string? button)
            => QaLegacyInputSimulator.BeginSwipe(fromScreenPosition, toScreenPosition, durationMs, button);

        public static void SimulateTap(Vector2 screenPosition) => QaLegacyInputSimulator.SimulateTap(screenPosition, string.Empty);

        public static void SimulateTap(Vector2 screenPosition, string? button) => QaLegacyInputSimulator.SimulateTap(screenPosition, button);
    }
}
#endif

namespace UnityCliBridge.Bridge.Editor
{
    /// <summary>A multi-frame pointer gesture advanced once per editor update.</summary>
    internal interface IQaSwipeOperation
    {
        /// <summary>Advances one step; returns true once the gesture has finished.</summary>
        bool Advance();

        void Abort();
    }

    /// <summary>
    /// QA input for projects on the legacy Input Manager (Active Input Handling = Input Manager (Old)).
    /// Pointer input uses UnityEngine.Input's touch simulation, which Unity also surfaces as left-mouse
    /// input (Input.GetMouseButtonDown(0), Input.mousePosition, Input.anyKeyDown) and Input.touches.
    /// UnityEngine.Input has no API to inject keyboard state, so keys are delivered to <see cref="IQaKeyHandler"/>
    /// components instead.
    /// </summary>
    internal static class QaLegacyInputSimulator
    {
        private const int FingerId = 7;
        private const int DefaultFrameDurationMs = 16;
        private const int TapHoldFrames = 2;
        private const long TapMaxHoldMs = 1000;

        private static MethodInfo? _simulateTouch;
        private static PropertyInfo? _simulateTouchEnabled;
        private static bool _apiResolved;
        private static EditorApplication.CallbackFunction? _pendingTapRelease;
#if ENABLE_INPUT_SYSTEM
        private static bool? _legacyOnly;
#endif

        /// <summary>True when only the legacy Input Manager is active, so Input System events would be ignored.</summary>
        public static bool IsLegacyOnly
        {
            get
            {
#if ENABLE_INPUT_SYSTEM
                _legacyOnly ??= ReadActiveInputHandler() == 0;
                return _legacyOnly.Value;
#else
                return true;
#endif
            }
        }

#if ENABLE_INPUT_SYSTEM
        private static int ReadActiveInputHandler()
        {
            try
            {
                UnityEngine.Object[] settings = AssetDatabase.LoadAllAssetsAtPath("ProjectSettings/ProjectSettings.asset");
                if (settings.Length > 0)
                {
                    SerializedProperty? property = new SerializedObject(settings[0]).FindProperty("activeInputHandler");
                    if (property != null)
                    {
                        return property.intValue;
                    }
                }
            }
            catch (Exception)
            {
                // Unknown layout: assume the Input System is active, matching the compile-time define.
            }

            return 1;
        }
#endif

        public static void SimulateKey(string keyName)
        {
            KeyCode keyCode = ParseKeyCode(keyName);
            int handled = 0;
#pragma warning disable CS0618 // FindObjectsOfType is obsolete in Unity 6 but is the 2021.3-compatible API.
            foreach (MonoBehaviour behaviour in UnityEngine.Object.FindObjectsOfType<MonoBehaviour>())
#pragma warning restore CS0618
            {
                if (behaviour is IQaKeyHandler handler && behaviour.isActiveAndEnabled && handler.TryQaKey(keyCode))
                {
                    handled++;
                }
            }

            if (handled == 0)
            {
                throw new CommandFailureException(
                    "QA_LEGACY_KEY_UNSUPPORTED",
                    $"qa key {keyName}: this project uses the legacy Input Manager, and UnityEngine.Input cannot be fed keyboard state. "
                    + "Use qa tap instead (it fires Input.GetMouseButtonDown(0), Input.anyKeyDown and Input.touches), "
                    + "implement UnityCliBridge.Bridge.IQaKeyHandler on a MonoBehaviour, or install com.unity.inputsystem.",
                    false,
                    null);
            }
        }

        internal static KeyCode ParseKeyCode(string keyName)
        {
            string name = keyName.Trim();
            switch (name.ToLowerInvariant())
            {
                case "enter":
                    return KeyCode.Return;
                case "leftctrl":
                    return KeyCode.LeftControl;
                case "rightctrl":
                    return KeyCode.RightControl;
                case "leftmeta":
                case "leftcommand":
                    return KeyCode.LeftCommand;
                case "rightmeta":
                case "rightcommand":
                    return KeyCode.RightCommand;
            }

            if (name.Length == 6 && name.StartsWith("Digit", StringComparison.OrdinalIgnoreCase) && char.IsDigit(name[5]))
            {
                return KeyCode.Alpha0 + (name[5] - '0');
            }

            if (name.Length == 1 && char.IsDigit(name[0]))
            {
                return KeyCode.Alpha0 + (name[0] - '0');
            }

            if (Enum.TryParse(name, true, out KeyCode keyCode))
            {
                return keyCode;
            }

            throw new CommandFailureException("QA_INVALID_KEY", $"Unknown key name: {keyName}. Use KeyCode or Input System Key names.", false, null);
        }

        public static void SimulateTap(Vector2 screenPosition, string? button)
        {
            RequireLeftButton(button, "qa tap");
            ReleasePendingTap();
            SendTouch(screenPosition, UnityEngine.TouchPhase.Began, Vector2.zero);

            int pressFrame = Time.frameCount;
            Stopwatch held = Stopwatch.StartNew();
            EditorApplication.CallbackFunction? release = null;
            release = () =>
            {
                if (EditorApplication.isPlaying && Time.frameCount < pressFrame + TapHoldFrames && held.ElapsedMilliseconds < TapMaxHoldMs)
                {
                    return;
                }

                EditorTickPump.Remove(release);
                if (ReferenceEquals(_pendingTapRelease, release))
                {
                    _pendingTapRelease = null;
                }

                if (EditorApplication.isPlaying)
                {
                    SendTouch(screenPosition, UnityEngine.TouchPhase.Ended, Vector2.zero);
                }
            };
            _pendingTapRelease = release;
            EditorTickPump.Add(release);
        }

        public static IQaSwipeOperation BeginSwipe(Vector2 fromScreenPosition, Vector2 toScreenPosition, int durationMs, string? button)
        {
            RequireLeftButton(button, "qa swipe");
            ReleasePendingTap();
            int normalizedDurationMs = durationMs > 0 ? durationMs : ProtocolConstants.DefaultQaSwipeDurationMs;
            int totalSteps = Mathf.Max(1, Mathf.CeilToInt(normalizedDurationMs / (float)DefaultFrameDurationMs));
            return new LegacySwipeOperation(fromScreenPosition, toScreenPosition, totalSteps);
        }

        private static void ReleasePendingTap()
        {
            EditorApplication.CallbackFunction? pending = _pendingTapRelease;
            if (pending == null)
            {
                return;
            }

            _pendingTapRelease = null;
            EditorTickPump.Remove(pending);
            if (EditorApplication.isPlaying)
            {
                SendTouch(Vector2.zero, UnityEngine.TouchPhase.Canceled, Vector2.zero);
            }
        }

        private static void RequireLeftButton(string? button, string commandName)
        {
            if (string.Equals(button, "right", StringComparison.OrdinalIgnoreCase))
            {
                throw new CommandFailureException(
                    "QA_LEGACY_BUTTON_UNSUPPORTED",
                    $"{commandName} --button right: the legacy Input Manager can only simulate touch / left-mouse input. Install com.unity.inputsystem for right-button input.",
                    false,
                    null);
            }
        }

        private static void SendTouch(Vector2 position, UnityEngine.TouchPhase phase, Vector2 delta)
        {
            ResolveApi();
            if (_simulateTouch == null)
            {
                throw new CommandFailureException(
                    "QA_LEGACY_INPUT_UNAVAILABLE",
                    "This Unity version does not expose legacy Input touch simulation. Install com.unity.inputsystem to use qa pointer input.",
                    false,
                    null);
            }

            _simulateTouchEnabled?.SetValue(null, true);
            var touch = new UnityEngine.Touch
            {
                fingerId = FingerId,
                position = position,
                rawPosition = position,
                deltaPosition = delta,
                phase = phase,
                tapCount = 1,
                pressure = 1f,
                maximumPossiblePressure = 1f,
                type = UnityEngine.TouchType.Direct,
            };

            try
            {
                _simulateTouch.Invoke(null, new object[] { touch });
            }
            catch (TargetInvocationException exception) when (exception.InnerException != null)
            {
                throw new CommandFailureException(
                    "QA_LEGACY_INPUT_UNAVAILABLE",
                    "Legacy Input touch simulation failed: " + exception.InnerException.Message,
                    false,
                    null);
            }
        }

        private static void ResolveApi()
        {
            if (_apiResolved)
            {
                return;
            }

            _apiResolved = true;
            const BindingFlags flags = BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public;
            _simulateTouch = typeof(UnityEngine.Input).GetMethod("SimulateTouch", flags, null, new[] { typeof(UnityEngine.Touch) }, null);
            _simulateTouchEnabled = typeof(UnityEngine.Input).GetProperty("simulateTouchEnabled", flags);
        }

        private sealed class LegacySwipeOperation : IQaSwipeOperation
        {
            private readonly Vector2 _from;
            private readonly Vector2 _to;
            private readonly int _totalSteps;
            private Vector2 _lastPosition;
            private int _currentStep;
            private int _lastFrame = -1;
            private bool _isFinished;

            public LegacySwipeOperation(Vector2 from, Vector2 to, int totalSteps)
            {
                _from = from;
                _to = to;
                _totalSteps = totalSteps;
                _lastPosition = from;
            }

            public bool Advance()
            {
                if (_isFinished)
                {
                    return true;
                }

                // Legacy Input samples once per player frame; send at most one touch phase per frame.
                if (Time.frameCount == _lastFrame)
                {
                    return false;
                }

                _lastFrame = Time.frameCount;
                float t = _totalSteps > 0 ? (float)_currentStep / _totalSteps : 1f;
                Vector2 position = Vector2.Lerp(_from, _to, t);
                UnityEngine.TouchPhase phase = _currentStep == 0
                    ? UnityEngine.TouchPhase.Began
                    : _currentStep >= _totalSteps ? UnityEngine.TouchPhase.Ended : UnityEngine.TouchPhase.Moved;
                SendTouch(position, phase, position - _lastPosition);
                _lastPosition = position;

                _isFinished = _currentStep >= _totalSteps;
                _currentStep++;
                return _isFinished;
            }

            public void Abort()
            {
                if (_isFinished)
                {
                    return;
                }

                _isFinished = true;
                if (_currentStep > 0 && EditorApplication.isPlaying)
                {
                    SendTouch(_lastPosition, UnityEngine.TouchPhase.Canceled, Vector2.zero);
                }
            }
        }
    }
}
