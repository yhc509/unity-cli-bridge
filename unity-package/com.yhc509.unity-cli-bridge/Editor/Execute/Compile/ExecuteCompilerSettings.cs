#nullable enable
using System;
using System.Collections.Generic;
using System.Globalization;

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
    /// Until the default changes, only <c>roslyn-host</c> uses the host; <c>auto</c>, unset, and
    /// <c>codedom</c> compile exactly as before. <c>csc-shared</c> is not implemented yet and falls
    /// back to CodeDOM. Invalid values fall back to the default and are reported in <see cref="Warnings"/>.
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

        /// <summary>True only for the explicit opt-in. <c>auto</c> stays on CodeDOM until the default changes.</summary>
        public bool UsesRoslynHost
        {
            get { return Mode == ExecuteCompilerMode.RoslynHost; }
        }

        /// <summary>
        /// Why a setting that asked for something else compiles with CodeDOM (reported as
        /// <c>timings.compilerFallbackReason</c>); null when CodeDOM is what was asked for.
        /// </summary>
        public string? CodeDomReason
        {
            get
            {
                if (Mode == ExecuteCompilerMode.CscShared)
                {
                    return "csc-shared-unavailable: the csc-shared backend is not available in this version";
                }

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
}
