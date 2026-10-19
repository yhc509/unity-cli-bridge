#nullable enable
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Reflection;
using UnityEditor;

namespace UnityCliBridge.Bridge.Editor
{
    /// <summary>
    /// Keeps the editor ticking at full rate while deferred bridge work is in flight.
    ///
    /// An idle editor runs <c>EditorApplication.update</c> at roughly 6 ticks/second, and every
    /// deferred command in the bridge — test runs, package operations, profiler sampling,
    /// recording, qa run-sequence — advances one step per tick. Forcing the internal
    /// <c>EditorApplication.SignalTick()</c> schedules the next tick immediately, which measured
    /// ~1.55x faster on Unity 6000.3.10f1 / macOS (60 ticks: 9.4–10.4s → 6.0–6.1s), focused and
    /// unfocused alike.
    ///
    /// Activation is automatic and scoped: deferred flows subscribe their poll through
    /// <see cref="Add"/> instead of <c>EditorApplication.update</c> directly, and the pump runs
    /// only while at least one of them is subscribed. Because the subscriber set holds the very
    /// delegates that are on <c>EditorApplication.update</c>, the pump cannot outlive the work —
    /// a leaked pump lease would mean a leaked poll subscription, which the flows already cannot
    /// afford. <see cref="Remove"/> is idempotent, matching the defensive double-unsubscribes the
    /// poll bodies already do.
    ///
    /// <c>SignalTick</c> is an internal API. If reflection cannot bind it the pump degrades to a
    /// no-op with a single warning: deferred work still completes, just at the throttled rate.
    /// Statics do not survive a domain reload, but neither do the poll subscriptions — flows that
    /// restore themselves after a reload re-subscribe through here and re-arm the pump.
    /// </summary>
    internal static class EditorTickPump
    {
        /// <summary>
        /// Minimum milliseconds between forced ticks (~60Hz). 0 would signal on every update.
        /// Measured on Unity 6000.3.10f1 / macOS the editor update loop settles at ~100ms per tick
        /// under the pump either way, so the throttle costs nothing there and caps the forced rate
        /// on setups whose loop can run faster.
        /// </summary>
        internal const int DefaultIntervalMs = 16;

        private static readonly HashSet<EditorApplication.CallbackFunction> _subscribers =
            new HashSet<EditorApplication.CallbackFunction>();
        private static readonly Stopwatch _sinceLastSignal = new Stopwatch();
        private static Action? _signalTick;
        private static bool _hasResolvedSignalTick;
        private static bool _isPumping;

        /// <summary>Minimum spacing between forced ticks; see <see cref="DefaultIntervalMs"/>.</summary>
        internal static int IntervalMs { get; set; } = DefaultIntervalMs;

        /// <summary>True while the pump is subscribed to the editor update loop.</summary>
        internal static bool IsPumping
        {
            get { return _isPumping; }
        }

        /// <summary>Deferred polls currently holding the pump open.</summary>
        internal static int SubscriberCount
        {
            get { return _subscribers.Count; }
        }

        /// <summary>
        /// Subscribe a deferred poll to the editor update loop and arm the pump.
        /// </summary>
        internal static void Add(EditorApplication.CallbackFunction? callback)
        {
            if (callback == null)
            {
                return;
            }

            EditorApplication.update += callback;
            if (_subscribers.Add(callback))
            {
                SyncPumpState();
            }
        }

        /// <summary>
        /// Unsubscribe a deferred poll and disarm the pump once nothing is left. Safe to call for
        /// a callback that was never added, or twice for the same one.
        /// </summary>
        internal static void Remove(EditorApplication.CallbackFunction? callback)
        {
            if (callback == null)
            {
                return;
            }

            EditorApplication.update -= callback;
            if (_subscribers.Remove(callback))
            {
                SyncPumpState();
            }
        }

        private static void SyncPumpState()
        {
            bool shouldPump = _subscribers.Count > 0 && ResolveSignalTick() != null;
            if (shouldPump == _isPumping)
            {
                return;
            }

            _isPumping = shouldPump;
            if (shouldPump)
            {
                _sinceLastSignal.Restart();
                EditorApplication.update += Pump;
                return;
            }

            _sinceLastSignal.Stop();
            EditorApplication.update -= Pump;
        }

        private static void Pump()
        {
            Action? signalTick = _signalTick;
            if (signalTick == null)
            {
                return;
            }

            if (IntervalMs > 0 && _sinceLastSignal.ElapsedMilliseconds < IntervalMs)
            {
                return;
            }

            _sinceLastSignal.Restart();
            try
            {
                signalTick();
            }
            catch (Exception exception)
            {
                // Losing the pump only costs speed, so drop it rather than log once per tick.
                _signalTick = null;
                SyncPumpState();
                UnityEngine.Debug.LogWarning(
                    "Unity CLI bridge editor tick pump stopped (EditorApplication.SignalTick call failed): " + exception.Message);
            }
        }

        private static Action? ResolveSignalTick()
        {
            if (_hasResolvedSignalTick)
            {
                return _signalTick;
            }

            _hasResolvedSignalTick = true;
            try
            {
                MethodInfo? method = typeof(EditorApplication).GetMethod(
                    "SignalTick",
                    BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public);
                if (method == null)
                {
                    UnityEngine.Debug.LogWarning(
                        "Unity CLI bridge editor tick pump disabled: could not find EditorApplication.SignalTick. " +
                        "Deferred commands may run slowly in an unfocused editor.");
                    return null;
                }

                _signalTick = (Action)Delegate.CreateDelegate(typeof(Action), method);
            }
            catch (Exception exception)
            {
                UnityEngine.Debug.LogWarning(
                    "Unity CLI bridge editor tick pump disabled (EditorApplication.SignalTick bind failed): "
                    + exception.Message);
                _signalTick = null;
            }

            return _signalTick;
        }
    }
}
