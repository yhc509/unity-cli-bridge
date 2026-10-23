#nullable enable
using System;
using System.Collections.Generic;
using System.Reflection;
using System.Text;
using UnityCli.Protocol;

namespace UnityCliBridge.Bridge.Editor
{
    /// <summary>
    /// Compiles through the resident Roslyn host (bundled .NET runtime + bundled Roslyn), C# 9.
    /// Used only when <c>UCB_EXECUTE_COMPILER=roslyn-host</c>. Anything that is not the user's own
    /// compile error (no toolchain, host not ready in time, crash, timeout, bad frame, foreign
    /// runtime, an emitted image Mono cannot load) is an infrastructure failure, so the selector
    /// falls back to CodeDOM. Diagnostics keep the CodeDOM text shape (<c>Line N: CSxxxx: text</c>,
    /// errors only, warnings only when a failed compile has no errors).
    /// </summary>
    internal sealed class RoslynHostBackend : ICompileBackend
    {
        public const string BackendId = CompileBackendIds.RoslynHost;
        public const string LanguageVersion = "9";
        public const string FilePath = "execute.cs";

        /// <summary>
        /// Lets <c>init</c> accessors and records compile against Mono's mscorlib, which lacks the type.
        /// Internal, so it cannot clash with another assembly's copy (CS0436 is suppressed), and hidden
        /// from line mapping so user line numbers are unchanged.
        /// </summary>
        public const string IsExternalInitPolyfill =
            "\n#line hidden\n"
            + "namespace System.Runtime.CompilerServices\n"
            + "{\n"
            + "    internal static class IsExternalInit\n"
            + "    {\n"
            + "    }\n"
            + "}\n";

        public static readonly string[] AlwaysSuppressedWarnings = { "CS0436" };

        private readonly RoslynHostSupervisor _supervisor;

        public RoslynHostBackend(RoslynHostSupervisor supervisor)
        {
            _supervisor = supervisor ?? throw new ArgumentNullException(nameof(supervisor));
        }

        public string Id
        {
            get { return BackendId; }
        }

        public CompileResult TryCompile(CompileRequest request)
        {
            List<string> defines = BuildDefines(request);
            string compilerOptions = BuildCompilerOptions(request, defines);
            IRoslynHostInstance? host = _supervisor.AcquireReady(_supervisor.Settings.HostWaitMs, out string reason);
            if (host == null)
            {
                return CompileResult.InfrastructureFailure(BackendId, compilerOptions, null, null, reason);
            }

            RoslynHostCompileRequest hostRequest = BuildHostRequest(request, defines);
            RoslynHostCompileResult result;
            try
            {
                result = _supervisor.Compile(host, hostRequest, request.ReferencePaths);
            }
            catch (RoslynHostException ex)
            {
                return CompileResult.InfrastructureFailure(BackendId, compilerOptions, null, null, ex.Reason);
            }

            if (!string.IsNullOrEmpty(result.Error))
            {
                return CompileResult.InfrastructureFailure(
                    BackendId,
                    compilerOptions,
                    null,
                    null,
                    RoslynHostFailureCodes.HostError + ": " + result.Error);
            }

            if (!result.Ok || result.Assembly == null)
            {
                return CompileResult.CompileError(BackendId, compilerOptions, FormatFailureDiagnostics(result.Diagnostics), null);
            }

            Assembly assembly;
            try
            {
                assembly = Assembly.Load(result.Assembly);
            }
            catch (Exception ex)
            {
                return CompileResult.InfrastructureFailure(
                    BackendId,
                    compilerOptions,
                    null,
                    null,
                    RoslynHostFailureCodes.LoadFailed + ": " + ex.GetType().Name + ": " + ex.Message);
            }

            return CompileResult.Success(BackendId, assembly, compilerOptions);
        }

        /// <summary>UNITY_ASSERTIONS + active scripting defines + the project's rsp <c>-define</c>s, validated like the CodeDOM path.</summary>
        public static List<string> BuildDefines(CompileRequest request)
        {
            var combined = new List<string>(request.ScriptingDefines);
            combined.AddRange(request.ProjectOptions.Defines);
            string option = ExecuteCompilerDefines.BuildDefineOption(combined);
            const string Prefix = "/define:";
            string list = option.StartsWith(Prefix, StringComparison.Ordinal) ? option.Substring(Prefix.Length) : option;
            return new List<string>(list.Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries));
        }

        /// <summary>The options shown in a <c>COMPILE_FAILED</c> message, shaped like the CodeDOM ones.</summary>
        public static string BuildCompilerOptions(CompileRequest request, IReadOnlyList<string> defines)
        {
            var builder = new StringBuilder();
            builder.Append("/langversion:").Append(LanguageVersion);
            builder.Append(" /define:").Append(string.Join(";", defines));
            if (request.ProjectOptions.AllowUnsafe)
            {
                builder.Append(" /unsafe");
            }

            return builder.ToString();
        }

        public static RoslynHostCompileRequest BuildHostRequest(CompileRequest request, List<string> defines)
        {
            var noWarn = new List<string>(AlwaysSuppressedWarnings);
            noWarn.AddRange(request.ProjectOptions.NoWarn);
            return new RoslynHostCompileRequest
            {
                Source = request.Source + IsExternalInitPolyfill,
                Defines = defines,
                LanguageVersion = LanguageVersion,
                AllowUnsafe = request.ProjectOptions.AllowUnsafe,
                NoWarn = noWarn,
                AssemblyName = "ucb_execute_" + Guid.NewGuid().ToString("N"),
                FilePath = FilePath,
            };
        }

        /// <summary>Errors only; warnings only when a failed compile reported no error (same rule as CodeDOM).</summary>
        public static string[] FormatFailureDiagnostics(IReadOnlyList<RoslynHostDiagnostic> diagnostics)
        {
            string[] errors = Format(diagnostics, includeWarnings: false);
            return errors.Length > 0 ? errors : Format(diagnostics, includeWarnings: true);
        }

        private static string[] Format(IReadOnlyList<RoslynHostDiagnostic> diagnostics, bool includeWarnings)
        {
            var lines = new List<string>();
            foreach (RoslynHostDiagnostic diagnostic in diagnostics)
            {
                if (!diagnostic.IsError && !includeWarnings)
                {
                    continue;
                }

                var line = new StringBuilder();
                if (diagnostic.Line > 0)
                {
                    line.Append("Line ").Append(diagnostic.Line).Append(": ");
                }

                if (!string.IsNullOrEmpty(diagnostic.Id))
                {
                    line.Append(diagnostic.Id).Append(": ");
                }

                if (!string.IsNullOrEmpty(diagnostic.Message))
                {
                    line.Append(diagnostic.Message);
                }

                if (line.Length > 0)
                {
                    lines.Add(line.ToString());
                }
            }

            return lines.ToArray();
        }
    }
}
