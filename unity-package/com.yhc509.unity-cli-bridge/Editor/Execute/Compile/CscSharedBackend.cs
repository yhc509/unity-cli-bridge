#nullable enable
using System;
using System.Reflection;
using UnityCli.Protocol;

namespace UnityCliBridge.Bridge.Editor
{
    /// <summary>
    /// Compiles through <c>csc /shared</c> (the bundled csc talking to a compiler server on this
    /// project's pipe), C# 9. The step between the Roslyn host and CodeDOM: used when the host has an
    /// infrastructure failure (the default chain), and first under <c>UCB_EXECUTE_COMPILER=csc-shared</c>. Inputs, polyfill, and diagnostic text are the host's
    /// (<see cref="RoslynHostBackend"/>), so a snippet compiles and fails the same way on both. A
    /// missing toolchain, a csc that cannot run or times out, output without an assembly, or an image
    /// Mono cannot load is an infrastructure failure; csc errors located in the source are the user's.
    /// </summary>
    internal sealed class CscSharedBackend : ICompileBackend
    {
        public const string BackendId = CompileBackendIds.CscShared;

        private readonly CscSharedServer _server;

        public CscSharedBackend(CscSharedServer server)
        {
            _server = server ?? throw new ArgumentNullException(nameof(server));
        }

        public string Id
        {
            get { return BackendId; }
        }

        public CscSharedServer Server
        {
            get { return _server; }
        }

        public CompileResult TryCompile(CompileRequest request)
        {
            var defines = RoslynHostBackend.BuildDefines(request);
            string compilerOptions = RoslynHostBackend.BuildCompilerOptions(request, defines);
            RoslynHostCompileRequest compile = RoslynHostBackend.BuildHostRequest(request, defines);
            CscSharedRun run = _server.Compile(compile, request.ReferencePaths);
            if (run.InfrastructureFailure != null)
            {
                return CompileResult.InfrastructureFailure(BackendId, compilerOptions, null, null, run.InfrastructureFailure);
            }

            if (run.Assembly == null)
            {
                return CompileResult.CompileError(BackendId, compilerOptions, RoslynHostBackend.FormatFailureDiagnostics(run.Diagnostics), null);
            }

            Assembly assembly;
            try
            {
                assembly = Assembly.Load(run.Assembly);
            }
            catch (Exception ex)
            {
                return CompileResult.InfrastructureFailure(
                    BackendId,
                    compilerOptions,
                    null,
                    null,
                    CscSharedFailureCodes.LoadFailed + ": " + ex.GetType().Name + ": " + ex.Message);
            }

            return CompileResult.Success(BackendId, assembly, compilerOptions);
        }
    }

    /// <summary>
    /// The host's step in the default chain when csc-shared follows it. Once the host serves a compile
    /// (an assembly or the user's own compile error), a csc /shared server this domain started while
    /// the host was building, starting, or down is no longer needed: it is stopped on a background
    /// thread (<see cref="CscSharedServer.RetireInBackground"/>) so it does not sit idle next to the
    /// host until the editor quits. If the host fails again later, the next csc-shared compile starts
    /// a new server. An infrastructure failure of the host leaves the server alone.
    /// </summary>
    internal sealed class RetiresCscSharedBackend : ICompileBackend
    {
        public const string RetireReason = "host-ready";

        private readonly ICompileBackend _host;
        private readonly CscSharedServer _server;

        public RetiresCscSharedBackend(ICompileBackend host, CscSharedServer server)
        {
            _host = host ?? throw new ArgumentNullException(nameof(host));
            _server = server ?? throw new ArgumentNullException(nameof(server));
        }

        public string Id
        {
            get { return _host.Id; }
        }

        public CompileResult TryCompile(CompileRequest request)
        {
            CompileResult result = _host.TryCompile(request);
            if (!result.IsInfrastructureFailure)
            {
                _server.RetireInBackground(RetireReason);
            }

            return result;
        }
    }
}
