#nullable enable
using UnityEngine;

namespace UnityCliBridge.Bridge
{
    /// <summary>
    /// Implement on a MonoBehaviour to expose a non-UI world object as a QA tap target,
    /// discoverable by `qa world-dump` and tappable by `qa tap --target`.
    /// </summary>
    public interface IQaTappable
    {
        /// <summary>Label shown in qa world-dump output.</summary>
        string QaLabel { get; }

        /// <summary>World-space anchor used to compute the tap point. Null falls back to the object's transform.</summary>
        Transform? QaAnchor { get; }

        /// <summary>
        /// Performs the tap action directly. Return true when handled; return false to let the bridge
        /// simulate a real Input System tap at the anchor's screen position instead.
        /// </summary>
        bool TryQaTap();
    }

    /// <summary>
    /// Implement on a MonoBehaviour to receive `qa key` in projects that use the legacy Input Manager,
    /// where UnityEngine.Input cannot be fed simulated keyboard state. Projects on the Input System
    /// receive real simulated key events instead and never call this.
    /// </summary>
    public interface IQaKeyHandler
    {
        /// <summary>Performs the action bound to <paramref name="key"/>. Return true when handled.</summary>
        bool TryQaKey(KeyCode key);
    }
}
