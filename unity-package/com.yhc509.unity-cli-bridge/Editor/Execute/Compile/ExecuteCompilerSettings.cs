#nullable enable
using System;
using System.Collections.Generic;
using System.Globalization;
using UnityCli.Protocol;

namespace UnityCliBridge.Bridge.Editor
{
    /// <summary>Stable backend ids. Reported to the CLI as <c>timings.compiler</c>.</summary>
    internal static class CompileBackendIds
    {
        public const string RoslynHost = "roslyn-host";
        public const string CscShared = "csc-shared";
        public const string CodeDom = "codedom";
    }

    internal enum ExecuteCompilerMode
    {
        Auto,
        RoslynHost,
        CscShared,
        CodeDom,
    }

    /// <summary>When the Roslyn host is started (see <see cref="ExecuteCompilerSettings.PrewarmVariable"/>).</summary>
    internal enum RoslynHostPrewarmPolicy
    {
        /// <summary>Right after a domain reload if <c>execute</c> already ran this editor session; otherwise on the first <c>execute</c>.</summary>
        Auto,

        /// <summary>Right after every domain reload.</summary>
        Eager,

        /// <summary>On the first <c>execute</c> of each domain.</summary>
        Lazy,

        /// <summary>On the first <c>execute</c> of each domain, without the dummy prewarm compiles.</summary>
        Off,
    }

    /// <summary>
    /// <c>execute</c> compiler settings, read from the editor process environment once per domain.
    /// The package has no project-settings store, so environment variables are the only source:
    /// <code>
    /// UCB_EXECUTE_COMPILER            auto (default) | roslyn-host | csc-shared | codedom
    /// UCB_ROSLYN_PREWARM              auto (default) | eager | lazy | off
    /// UCB_ROSLYN_HOST_WAIT_MS         1500  wait for a starting host before falling back (0–60000)
    /// UCB_ROSLYN_COMPILE_TIMEOUT_MS   10000 per compile; the host is killed when exceeded (1000–600000)
    /// UCB_ROSLYN_IDLE_EXIT_MINUTES    0 = keep the host for the whole domain (0–1440)
    /// </code>
    /// <c>roslyn-host</c> uses the Roslyn host (chain roslyn-host → csc-shared → codedom),
    /// <c>csc-shared</c> starts at csc /shared (csc-shared → codedom), and <c>codedom</c> is the kill
    /// switch: mcs only, with no host, compiler server, thread, or status file. <c>auto</c> (also unset
    /// or invalid) is whatever <see cref="AutoUsesRoslynHost"/> says. Invalid values fall back to
    /// <c>auto</c> and are reported in <see cref="Warnings"/>.
    /// Unity-free so the parsing is unit-tested.
    /// </summary>
    internal sealed class ExecuteCompilerSettings
    {
        public const string CompilerVariable = "UCB_EXECUTE_COMPILER";
        public const string PrewarmVariable = "UCB_ROSLYN_PREWARM";
        public const string HostWaitVariable = "UCB_ROSLYN_HOST_WAIT_MS";
        public const string CompileTimeoutVariable = "UCB_ROSLYN_COMPILE_TIMEOUT_MS";
        public const string IdleExitVariable = "UCB_ROSLYN_IDLE_EXIT_MINUTES";

        public const int DefaultHostWaitMs = 1500;
        public const int DefaultCompileTimeoutMs = 10000;
        public const int DefaultIdleExitMinutes = 0;

        /// <summary>
        /// The default compiler: what <c>auto</c> (and an unset or invalid variable) selects. True is the
        /// <c>roslyn-host</c> chain, false is CodeDOM alone. The only line that decides the default.
        /// </summary>
        public static readonly bool AutoUsesRoslynHost = false;

        private ExecuteCompilerSettings(
            ExecuteCompilerMode mode,
            string rawCompiler,
            RoslynHostPrewarmPolicy prewarm,
            int hostWaitMs,
            int compileTimeoutMs,
            int idleExitMinutes,
            List<string> warnings)
        {
            Mode = mode;
            RawCompiler = rawCompiler;
            Prewarm = prewarm;
            HostWaitMs = hostWaitMs;
            CompileTimeoutMs = compileTimeoutMs;
            IdleExitMinutes = idleExitMinutes;
            Warnings = warnings;
        }

        public static readonly ExecuteCompilerSettings Default = Parse(_ => null);

        public ExecuteCompilerMode Mode { get; }

        /// <summary>The variable as given, trimmed; <c>auto</c> when unset.</summary>
        public string RawCompiler { get; }

        public RoslynHostPrewarmPolicy Prewarm { get; }

        public int HostWaitMs { get; }

        public int CompileTimeoutMs { get; }

        public int IdleExitMinutes { get; }

        public IReadOnlyList<string> Warnings { get; }

        /// <summary>True for <c>roslyn-host</c>, and for <c>auto</c> when <see cref="AutoUsesRoslynHost"/>.</summary>
        public bool UsesRoslynHost
        {
            get { return Mode == ExecuteCompilerMode.RoslynHost || (Mode == ExecuteCompilerMode.Auto && AutoUsesRoslynHost); }
        }

        /// <summary>True when the chain has the csc /shared step: the host's fallback (<see cref="UsesRoslynHost"/>) or <c>csc-shared</c>.</summary>
        public bool UsesCscShared
        {
            get { return UsesRoslynHost || Mode == ExecuteCompilerMode.CscShared; }
        }

        /// <summary>
        /// The invalid <c>UCB_EXECUTE_COMPILER</c> value <c>auto</c> replaced (reported as
        /// <c>timings.compilerFallbackReason</c> on every compile); null when the value was valid.
        /// </summary>
        public string? InvalidSettingReason
        {
            get
            {
                foreach (string warning in Warnings)
                {
                    if (warning.StartsWith(CompilerVariable, StringComparison.Ordinal))
                    {
                        return "invalid-setting: " + warning;
                    }
                }

                return null;
            }
        }

        public string PrewarmName
        {
            get { return Prewarm.ToString().ToLowerInvariant(); }
        }

        public static ExecuteCompilerSettings FromEnvironment()
        {
            return Parse(Environment.GetEnvironmentVariable);
        }

        public static ExecuteCompilerSettings Parse(Func<string, string?> lookup)
        {
            if (lookup == null)
            {
                throw new ArgumentNullException(nameof(lookup));
            }

            var warnings = new List<string>();
            string rawCompiler = (lookup(CompilerVariable) ?? string.Empty).Trim();
            ExecuteCompilerMode mode;
            switch (rawCompiler.ToLowerInvariant())
            {
                case "":
                case "auto":
                    mode = ExecuteCompilerMode.Auto;
                    break;
                case "roslyn-host":
                    mode = ExecuteCompilerMode.RoslynHost;
                    break;
                case "csc-shared":
                    mode = ExecuteCompilerMode.CscShared;
                    break;
                case "codedom":
                    mode = ExecuteCompilerMode.CodeDom;
                    break;
                default:
                    mode = ExecuteCompilerMode.Auto;
                    warnings.Add(CompilerVariable + "='" + rawCompiler + "' is not auto, roslyn-host, csc-shared, or codedom; using auto.");
                    break;
            }

            string rawPrewarm = (lookup(PrewarmVariable) ?? string.Empty).Trim();
            RoslynHostPrewarmPolicy prewarm;
            switch (rawPrewarm.ToLowerInvariant())
            {
                case "":
                case "auto":
                    prewarm = RoslynHostPrewarmPolicy.Auto;
                    break;
                case "eager":
                    prewarm = RoslynHostPrewarmPolicy.Eager;
                    break;
                case "lazy":
                    prewarm = RoslynHostPrewarmPolicy.Lazy;
                    break;
                case "off":
                    prewarm = RoslynHostPrewarmPolicy.Off;
                    break;
                default:
                    prewarm = RoslynHostPrewarmPolicy.Auto;
                    warnings.Add(PrewarmVariable + "='" + rawPrewarm + "' is not auto, eager, lazy, or off; using auto.");
                    break;
            }

            int hostWaitMs = ReadInt(lookup, HostWaitVariable, DefaultHostWaitMs, 0, 60000, warnings);
            int compileTimeoutMs = ReadInt(lookup, CompileTimeoutVariable, DefaultCompileTimeoutMs, 1000, 600000, warnings);
            int idleExitMinutes = ReadInt(lookup, IdleExitVariable, DefaultIdleExitMinutes, 0, 1440, warnings);
            return new ExecuteCompilerSettings(
                mode,
                rawCompiler.Length == 0 ? "auto" : rawCompiler,
                prewarm,
                hostWaitMs,
                compileTimeoutMs,
                idleExitMinutes,
                warnings);
        }

        private static int ReadInt(Func<string, string?> lookup, string name, int fallback, int min, int max, List<string> warnings)
        {
            string raw = (lookup(name) ?? string.Empty).Trim();
            if (raw.Length == 0)
            {
                return fallback;
            }

            if (!int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out int value) || value < min || value > max)
            {
                warnings.Add(name + "='" + raw + "' is not an integer from " + min + " to " + max + "; using " + fallback + ".");
                return fallback;
            }

            return value;
        }
    }

    /// <summary>
    /// Which backends one domain's chain gets, decided once on its first compile. A Roslyn setting
    /// whose editor has no bundled .NET runtime or Roslyn (an unknown install layout, for example on
    /// Linux) goes straight to CodeDOM: the host and csc /shared would both fail for that same reason,
    /// so neither is tried and nothing waits. Unity-free so the decision is unit-tested.
    /// </summary>
    internal sealed class CompileChainPlan
    {
        /// <summary>Same text as the host's toolchain failure code.</summary>
        public const string ToolchainMissingCode = "toolchain-missing";

        public const string HostStartFailedCode = "start-failed";

        public const string CscSharedUnavailableCode = "csc-shared-unavailable";

        private CompileChainPlan(bool useRoslynHost, bool useCscShared, string? reason, string? skippedBackend)
        {
            UseRoslynHost = useRoslynHost;
            UseCscShared = useCscShared;
            Reason = reason;
            SkippedBackend = skippedBackend;
        }

        public bool UseRoslynHost { get; }

        public bool UseCscShared { get; }

        /// <summary>The selector's configuration reason (<c>timings.compilerFallbackReason</c>); null when there is none.</summary>
        public string? Reason { get; }

        /// <summary>
        /// The Roslyn backend the settings start with that this plan leaves out (so an mcs error
        /// after it carries the fallback note); null when the chain starts where the settings asked.
        /// </summary>
        public string? SkippedBackend { get; }

        /// <param name="hostSetupError">Null when the host is set up; otherwise why not. Ignored unless the settings use the host.</param>
        /// <param name="cscSharedSetupError">Null when csc-shared is set up; otherwise why not. Ignored unless the settings use it.</param>
        /// <param name="toolchainMissing">Why the bundled toolchain cannot run Roslyn, or null; only called for a Roslyn setting.</param>
        public static CompileChainPlan Create(
            ExecuteCompilerSettings settings,
            string? hostSetupError,
            string? cscSharedSetupError,
            Func<string?> toolchainMissing)
        {
            if (settings == null)
            {
                throw new ArgumentNullException(nameof(settings));
            }

            string? warning = settings.InvalidSettingReason;
            if (!settings.UsesRoslynHost && !settings.UsesCscShared)
            {
                return new CompileChainPlan(false, false, warning, null);
            }

            string? missing;
            try
            {
                missing = toolchainMissing != null ? toolchainMissing() : null;
            }
            catch (Exception ex)
            {
                missing = ex.GetType().Name + ": " + ex.Message;
            }

            if (!string.IsNullOrWhiteSpace(missing))
            {
                return new CompileChainPlan(
                    false,
                    false,
                    Join(warning, ToolchainMissingCode + ": " + missing!.Trim()),
                    settings.UsesRoslynHost ? CompileBackendIds.RoslynHost : CompileBackendIds.CscShared);
            }

            bool useHost = settings.UsesRoslynHost && hostSetupError == null;
            bool useCscShared = settings.UsesCscShared && cscSharedSetupError == null;
            string? reason = null;
            string? skipped = null;
            if (settings.UsesRoslynHost && hostSetupError != null)
            {
                reason = HostStartFailedCode + ": " + (hostSetupError.Length > 0 ? hostSetupError : "host setup failed");
                skipped = CompileBackendIds.RoslynHost;
            }
            else if (!settings.UsesRoslynHost && settings.UsesCscShared && cscSharedSetupError != null)
            {
                reason = CscSharedUnavailableCode + ": " + (cscSharedSetupError.Length > 0 ? cscSharedSetupError : "csc-shared setup failed");
                skipped = CompileBackendIds.CscShared;
            }

            return new CompileChainPlan(useHost, useCscShared, Join(warning, reason), skipped);
        }

        /// <summary>Null when <paramref name="report"/> has the bundled runtime and Roslyn; otherwise what is missing.</summary>
        public static string? DescribeMissingToolchain(BundledToolchainReport? report)
        {
            if (report == null)
            {
                return "the bundled toolchain was not probed";
            }

            BundledToolComponent? runtime = report.Runtime;
            if (runtime == null || !runtime.Found || string.IsNullOrEmpty(runtime.Path))
            {
                return "bundled .NET runtime not found: " + (runtime?.Error ?? report.Error ?? "unknown");
            }

            BundledToolComponent? roslyn = report.Roslyn;
            if (roslyn == null || !roslyn.Found || string.IsNullOrEmpty(roslyn.Path))
            {
                return "bundled Roslyn not found: " + (roslyn?.Error ?? report.Error ?? "unknown");
            }

            return null;
        }

        private static string? Join(string? first, string? second)
        {
            if (string.IsNullOrEmpty(first))
            {
                return string.IsNullOrEmpty(second) ? null : second;
            }

            return string.IsNullOrEmpty(second) ? first : first + "; " + second;
        }
    }
}
