#nullable enable
using System;
using System.Collections.Generic;
using System.Reflection;
using UnityCli.Protocol;

namespace UnityCliBridge.Bridge.Editor
{
    /// <summary>
    /// Outcome of one <see cref="ICompileBackend.TryCompile"/> call. Exactly one of these holds:
    /// <see cref="Succeeded"/> (an assembly is loaded), a user code error, or an infrastructure
    /// failure (<see cref="IsInfrastructureFailure"/>: the compiler ran but produced no assembly
    /// without reporting an error). The backend id stays internal and is not sent on the wire.
    /// </summary>
    internal sealed class CompileResult
    {
        private CompileResult(
            string backendId,
            Assembly? assembly,
            bool isInfrastructureFailure,
            string compilerOptions,
            IReadOnlyList<string>? errors,
            IReadOnlyList<string>? output,
            string? failureDetail)
        {
            BackendId = backendId ?? string.Empty;
            Assembly = assembly;
            IsInfrastructureFailure = isInfrastructureFailure;
            CompilerOptions = compilerOptions ?? string.Empty;
            Errors = errors ?? Array.Empty<string>();
            Output = output ?? Array.Empty<string>();
            FailureDetail = failureDetail;
        }

        public string BackendId { get; }

        public Assembly? Assembly { get; }

        public bool Succeeded
        {
            get { return Assembly != null; }
        }

        public bool IsInfrastructureFailure { get; }

        /// <summary>Effective compiler options, shown in the failure message.</summary>
        public string CompilerOptions { get; }

        /// <summary>Formatted diagnostic lines (<c>Line N: CSxxxx: text</c>).</summary>
        public IReadOnlyList<string> Errors { get; }

        /// <summary>Raw compiler output, used when <see cref="Errors"/> is empty.</summary>
        public IReadOnlyList<string> Output { get; }

        /// <summary>Last-resort detail, used when both <see cref="Errors"/> and <see cref="Output"/> are empty.</summary>
        public string? FailureDetail { get; }

        public static CompileResult Success(string backendId, Assembly assembly, string compilerOptions)
        {
            if (assembly == null)
            {
                throw new ArgumentNullException(nameof(assembly));
            }

            return new CompileResult(backendId, assembly, false, compilerOptions, null, null, null);
        }

        public static CompileResult CompileError(
            string backendId,
            string compilerOptions,
            IReadOnlyList<string>? errors,
            IReadOnlyList<string>? output)
        {
            return new CompileResult(backendId, null, false, compilerOptions, errors, output, null);
        }

        public static CompileResult InfrastructureFailure(
            string backendId,
            string compilerOptions,
            IReadOnlyList<string>? errors,
            IReadOnlyList<string>? output,
            string? failureDetail)
        {
            return new CompileResult(backendId, null, true, compilerOptions, errors, output, failureDetail);
        }

        /// <summary>The <c>COMPILE_FAILED</c> message text. Never empty.</summary>
        public string FormatFailureMessage()
        {
            return CSharpLanguageVersion.FormatCompilerFailure(CompilerOptions, Errors, Output, FailureDetail);
        }
    }
}
