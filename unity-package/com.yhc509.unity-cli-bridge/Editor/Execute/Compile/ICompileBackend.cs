#nullable enable

namespace UnityCliBridge.Bridge.Editor
{
    /// <summary>
    /// Compiles the wrapped <c>execute</c> source into a loaded assembly. A backend reports user
    /// code errors and infrastructure failures (the compiler could not run or produce output)
    /// through <see cref="CompileResult"/> rather than throwing, so a selector can tell the two
    /// apart. Unexpected exceptions still propagate and surface as <c>EXECUTE_FAILED</c>.
    /// </summary>
    internal interface ICompileBackend
    {
        /// <summary>Stable identifier, e.g. <c>codedom</c>. <c>ExecuteCodeHandler</c> copies it to <c>timings.compiler</c>.</summary>
        string Id { get; }

        CompileResult TryCompile(CompileRequest request);
    }
}
