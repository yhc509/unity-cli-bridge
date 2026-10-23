#nullable enable
using System;
using System.Collections.Generic;

namespace UnityCliBridge.Bridge.Editor
{
    /// <summary>
    /// Runs an <c>execute</c> compile down a backend chain that always ends with the fallback
    /// (CodeDOM). Only an infrastructure failure moves on to the next backend; a user compile error
    /// is returned as-is, because recompiling with a different compiler would only produce a
    /// different (and for C# 9 code, misleading) error. A backend that throws is treated as an
    /// infrastructure failure, except the last one, whose exception surfaces as before.
    /// </summary>
    internal sealed class CompileBackendSelector
    {
        private readonly ICompileBackend[] _chain;
        private readonly string? _configurationReason;

        public CompileBackendSelector(ICompileBackend fallback)
            : this(fallback, Array.Empty<ICompileBackend>(), null)
        {
        }

        /// <param name="preferred">Tried in order before <paramref name="fallback"/>.</param>
        /// <param name="configurationReason">
        /// Why the chain does not start where the settings asked (for example the Roslyn host could
        /// not be set up). Reported as the fallback reason, followed by any skipped backend's reason.
        /// </param>
        public CompileBackendSelector(ICompileBackend fallback, IReadOnlyList<ICompileBackend> preferred, string? configurationReason)
        {
            if (fallback == null)
            {
                throw new ArgumentNullException(nameof(fallback));
            }

            var chain = new List<ICompileBackend>();
            if (preferred != null)
            {
                foreach (ICompileBackend backend in preferred)
                {
                    if (backend != null)
                    {
                        chain.Add(backend);
                    }
                }
            }

            chain.Add(fallback);
            _chain = chain.ToArray();
            _configurationReason = string.IsNullOrWhiteSpace(configurationReason) ? null : configurationReason;
        }

        public IReadOnlyList<ICompileBackend> Chain
        {
            get { return _chain; }
        }

        /// <summary>The backend a compile starts with.</summary>
        public ICompileBackend Select()
        {
            return _chain[0];
        }

        public CompileOutcome Compile(CompileRequest request)
        {
            string? reason = _configurationReason;
            string? fellBackFrom = null;
            for (int index = 0; index < _chain.Length; index++)
            {
                ICompileBackend backend = _chain[index];
                bool last = index == _chain.Length - 1;
                CompileResult result;
                if (last)
                {
                    result = backend.TryCompile(request);
                }
                else
                {
                    try
                    {
                        result = backend.TryCompile(request);
                    }
                    catch (Exception ex)
                    {
                        string thrown = "backend-exception: " + ex.GetType().Name + ": " + ex.Message;
                        reason = reason == null ? thrown : reason + "; " + thrown;
                        fellBackFrom = backend.Id;
                        continue;
                    }
                }

                if (last || !result.IsInfrastructureFailure)
                {
                    return new CompileOutcome(result, backend.Id, reason, fellBackFrom);
                }

                // Keep every skipped backend's reason (host, then csc-shared) rather than only the last.
                string described = DescribeInfrastructureFailure(result);
                reason = reason == null ? described : reason + "; " + described;
                fellBackFrom = backend.Id;
            }

            throw new InvalidOperationException("The compile backend chain is empty.");
        }

        private static string DescribeInfrastructureFailure(CompileResult result)
        {
            if (!string.IsNullOrWhiteSpace(result.FailureDetail))
            {
                return result.FailureDetail!.Trim();
            }

            if (result.Errors.Count > 0)
            {
                return result.Errors[0];
            }

            return result.BackendId + " failed without a reason";
        }
    }

    /// <summary>A <see cref="CompileResult"/> plus which backend produced it and why earlier ones were skipped.</summary>
    internal sealed class CompileOutcome
    {
        public const string McsFallbackNotePrefix = "compiled with mcs fallback (C# 7.x); Roslyn host unavailable: ";

        public CompileOutcome(CompileResult result, string compiler, string? fallbackReason, string? fellBackFrom)
        {
            Result = result ?? throw new ArgumentNullException(nameof(result));
            Compiler = compiler ?? string.Empty;
            FallbackReason = fallbackReason;
            FellBackFrom = fellBackFrom;
        }

        public CompileResult Result { get; }

        /// <summary>Id of the backend that produced <see cref="Result"/> (<c>timings.compiler</c>).</summary>
        public string Compiler { get; }

        /// <summary>Why the chain did not stop at the first backend (<c>timings.compilerFallbackReason</c>); null when it did.</summary>
        public string? FallbackReason { get; }

        /// <summary>The backend that failed last before <see cref="Compiler"/>; null without a fallback.</summary>
        public string? FellBackFrom { get; }

        /// <summary>
        /// The <c>COMPILE_FAILED</c> text. When a Roslyn backend (host or csc-shared) was skipped and
        /// mcs then rejected the code, one line says so: the snippet may be valid C# 9 that mcs (C# 7.x)
        /// cannot parse.
        /// </summary>
        public string FormatFailureMessage()
        {
            string message = Result.FormatFailureMessage();
            if ((string.Equals(FellBackFrom, CompileBackendIds.RoslynHost, StringComparison.Ordinal)
                    || string.Equals(FellBackFrom, CompileBackendIds.CscShared, StringComparison.Ordinal))
                && string.Equals(Compiler, CompileBackendIds.CodeDom, StringComparison.Ordinal)
                && !Result.IsInfrastructureFailure)
            {
                message += "\n" + McsFallbackNotePrefix + (FallbackReason ?? "unknown");
            }

            return message;
        }
    }
}
