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
    /// without reporting an error). The handler copies <see cref="BackendId"/> onto
    /// <c>timings.compiler</c>. <see cref="CompileMilliseconds"/> is the compiler call;
    /// <see cref="LoadMilliseconds"/> is assembly materialization and stays null when that step
    /// did not run.
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
            string? failureDetail,
            long? compileMilliseconds,
            long? loadMilliseconds)
        {
            BackendId = backendId ?? string.Empty;
            Assembly = assembly;
            IsInfrastructureFailure = isInfrastructureFailure;
            CompilerOptions = compilerOptions ?? string.Empty;
            Errors = errors ?? Array.Empty<string>();
            Output = output ?? Array.Empty<string>();
            FailureDetail = failureDetail;
            CompileMilliseconds = compileMilliseconds;
            LoadMilliseconds = loadMilliseconds;
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

        /// <summary>Compiler-call milliseconds. Null when this result was not timed.</summary>
        public long? CompileMilliseconds { get; }

        /// <summary>Assembly-materialization milliseconds. Null when that step did not run.</summary>
        public long? LoadMilliseconds { get; }

        public static CompileResult Success(
            string backendId,
            Assembly assembly,
            string compilerOptions,
            long? compileMilliseconds = null,
            long? loadMilliseconds = null)
        {
            if (assembly == null)
            {
                throw new ArgumentNullException(nameof(assembly));
            }

            return new CompileResult(
                backendId,
                assembly,
                false,
                compilerOptions,
                null,
                null,
                null,
                compileMilliseconds,
                loadMilliseconds);
        }

        public static CompileResult CompileError(
            string backendId,
            string compilerOptions,
            IReadOnlyList<string>? errors,
            IReadOnlyList<string>? output,
            long? compileMilliseconds = null,
            long? loadMilliseconds = null)
        {
            return new CompileResult(
                backendId,
                null,
                false,
                compilerOptions,
                errors,
                output,
                null,
                compileMilliseconds,
                loadMilliseconds);
        }

        public static CompileResult InfrastructureFailure(
            string backendId,
            string compilerOptions,
            IReadOnlyList<string>? errors,
            IReadOnlyList<string>? output,
            string? failureDetail,
            long? compileMilliseconds = null,
            long? loadMilliseconds = null)
        {
            return new CompileResult(
                backendId,
                null,
                true,
                compilerOptions,
                errors,
                output,
                failureDetail,
                compileMilliseconds,
                loadMilliseconds);
        }

        /// <summary>The <c>COMPILE_FAILED</c> message text. Never empty.</summary>
        public string FormatFailureMessage()
        {
            return CSharpLanguageVersion.FormatCompilerFailure(CompilerOptions, Errors, Output, FailureDetail);
        }
    }
}
