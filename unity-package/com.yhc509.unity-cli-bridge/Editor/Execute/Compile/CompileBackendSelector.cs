#nullable enable
using System;

namespace UnityCliBridge.Bridge.Editor
{
    /// <summary>
    /// Picks the backend for an <c>execute</c> compile. The fallback backend is the last resort
    /// of every choice; today it is the only one, so <see cref="Select"/> always returns it.
    /// </summary>
    internal sealed class CompileBackendSelector
    {
        private readonly ICompileBackend _fallback;

        public CompileBackendSelector(ICompileBackend fallback)
        {
            _fallback = fallback ?? throw new ArgumentNullException(nameof(fallback));
        }

        public ICompileBackend Select()
        {
            return _fallback;
        }
    }
}
