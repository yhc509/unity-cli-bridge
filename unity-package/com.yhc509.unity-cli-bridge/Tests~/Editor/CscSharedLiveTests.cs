#nullable enable
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using NUnit.Framework;
using UnityCli.Protocol;
using UnityEditor;
using UnityEngine;

namespace UnityCliBridge.Bridge.Editor.Tests
{
    /// <summary>
    /// The real csc /shared path: this editor's bundled csc and compiler server on its bundled .NET,
    /// driven through <see cref="ExecuteCodeHandler"/> with a csc-shared → codedom selector. Uses its
    /// own folder (<c>Library/com.yhc509.unity-cli-bridge/csc-shared-tests</c>) and pipe so it never
    /// touches an opted-in editor's own server.
    /// </summary>
    [Category("CscSharedLive")]
    public sealed class CscSharedLiveTests
    {
        private CscSharedServer? _server;
        private CompileBackendSelector? _previous;

        [OneTimeSetUp]
        public void SetUp()
        {
            BundledToolchainReport toolchain = BundledToolchainLocator.Locate(EditorApplication.applicationContentsPath);
            Assume.That(CscSharedToolchain.FromReport(toolchain, out string missing), Is.Not.Null, missing);

            string projectRoot = ProtocolConstants.GetCanonicalPath(Path.Combine(Application.dataPath, ".."));
            string testHash = ProtocolConstants.ComputeProjectHash(projectRoot + "/csc-shared-tests");
            _server = new CscSharedServer(new CscSharedServerOptions
            {
                ContentsPath = EditorApplication.applicationContentsPath,
                ProjectHash = testHash,
                UnityVersion = Application.unityVersion,
                EditorPid = Process.GetCurrentProcess().Id,
                Directory = Path.Combine(Path.Combine(Path.Combine(projectRoot, "Library"), "com.yhc509.unity-cli-bridge"), "csc-shared-tests"),
                ConfiguredCompiler = "csc-shared",
                CompileTimeoutMs = 60000,
            });
            _previous = ExecuteCodeHandler.SwapSelectorForTests(new CompileBackendSelector(
                new CodeDomBackend(),
                new ICompileBackend[] { new CscSharedBackend(_server) },
                null));
        }

        [OneTimeTearDown]
        public void TearDown()
        {
            if (_previous != null)
            {
                ExecuteCodeHandler.SwapSelectorForTests(_previous);
            }

            if (_server != null)
            {
                _server.Shutdown("test-teardown");
                Assert.That(File.Exists(_server.RecordPath), Is.False);
                Assert.That(SpinUntil(() => Servers(_server.PipeName).Count == 0, 2000), Is.True, "server still running on " + _server.PipeName);
            }
        }

        private static ExecuteCodePayload Execute(string code)
        {
            var handler = new ExecuteCodeHandler();
            string args = ProtocolJson.Serialize(new ExecuteCodeArgs { code = code, force = true });
            return ProtocolJson.Deserialize<ExecuteCodePayload>(handler.Handle(ProtocolConstants.CommandExecuteCode, args));
        }

        private static List<int> Servers(string pipe)
        {
            return RoslynHostProcessInspector.FindByCommandLine("VBCSCompiler")
                .Where(p => CscSharedPipe.IsServerCommandLine(p.Value, pipe))
                .Select(p => p.Key)
                .ToList();
        }

        private static bool SpinUntil(Func<bool> condition, int timeoutMs)
        {
            var watch = Stopwatch.StartNew();
            while (watch.ElapsedMilliseconds < timeoutMs)
            {
                if (condition())
                {
                    return true;
                }

                Thread.Sleep(50);
            }

            return condition();
        }

        [Test]
        public void Miss_ThenHit_UseCscShared()
        {
            string code = "__pucResult = UnityEngine.Mathf.Max(40, 2) + 2; // csc " + Guid.NewGuid().ToString("N");

            ExecuteCodePayload miss = Execute(code);
            ExecuteCodePayload hit = Execute(code);

            Assert.That(miss.result, Is.EqualTo("42"));
            Assert.That(miss.cacheHit, Is.False);
            Assert.That(miss.timings!.compiler, Is.EqualTo("csc-shared"), miss.timings.compilerFallbackReason);
            Assert.That(miss.timings.compilerFallbackReason, Is.Empty);
            Assert.That(hit.cacheHit, Is.True);
            Assert.That(hit.timings!.compiler, Is.EqualTo("csc-shared"));
        }

        [Test]
        public void Server_RunsOnTheBundledRuntimeOnOurPipe()
        {
            Execute("__pucResult = 1; // server " + Guid.NewGuid().ToString("N"));

            Assert.That(SpinUntil(() => Servers(_server!.PipeName).Count == 1, 5000), Is.True);
            int pid = Servers(_server!.PipeName)[0];
            string commandLine = RoslynHostProcessInspector.ReadCommandLine(pid) ?? string.Empty;
            Assert.That(commandLine, Does.StartWith(Path.GetFullPath(EditorApplication.applicationContentsPath)));
            Assert.That(commandLine, Does.Contain("VBCSCompiler.dll"));
            Assert.That(CscSharedServerRecord.TryRead(_server.RecordPath)!.Pipe, Is.EqualTo(_server.PipeName));
        }

        [Test]
        public void CSharp9_InMethodFeatures_RunThroughExecute()
        {
            string code = string.Join("\n", new[]
            {
                "System.Collections.Generic.List<int> list = new();",
                "list.Add(5);",
                "object o = list.Count;",
                "string kind = o is int n and > 0 ? \"pos\" : \"other\";",
                "string s = null; s ??= \"set\";",
                "if (o is not null) { list.Add(Twice(3)); }",
                "static int Twice(int v) => v * 2;",
                "Func<int, int, int> first = (a, _) => a;",
                "__pucResult = kind + \":\" + string.Join(\",\", list) + \":\" + s + \":\" + first(7, 8); // " + Guid.NewGuid().ToString("N"),
            });

            ExecuteCodePayload payload = Execute(code);

            Assert.That(payload.timings!.compiler, Is.EqualTo("csc-shared"), payload.timings.compilerFallbackReason);
            Assert.That(payload.result, Is.EqualTo("\"pos:5,6:set:7\""));
        }

        [Test]
        public void CSharp9_RecordInitWith_CompileWithThePolyfillAndRunOnMono()
        {
            string source = string.Join("\n", new[]
            {
                "public record UcbCscPoint { public int X { get; init; } public int Y { get; init; } }",
                "public sealed record UcbCscPair(string Name, int Count);",
                "public static class UcbCscRecords",
                "{",
                "    public static string Run()",
                "    {",
                "        var p = new UcbCscPoint { X = 1, Y = 2 };",
                "        var q = p with { Y = 5 };",
                "        var pair = new UcbCscPair(\"a\", 1) with { Count = 3 };",
                "        return q.X + \",\" + q.Y + \":\" + (p == new UcbCscPoint { X = 1, Y = 2 }) + \":\" + pair.Name + pair.Count;",
                "    }",
                "}",
            });

            CompileResult result = new CscSharedBackend(_server!).TryCompile(CompileRequestBuilder.Build(source));

            Assert.That(result.Succeeded, Is.True, result.FormatFailureMessage());
            object value = result.Assembly!.GetType("UcbCscRecords")!.GetMethod("Run")!.Invoke(null, null);
            Assert.That(value, Is.EqualTo("1,5:True:a3"));
        }

        [Test]
        public void CompileError_KeepsTheHostFormatAndUserLineAndDoesNotFallBack()
        {
            var ex = Assert.Throws<CommandFailureException>(() => Execute("int ok = 1;\nint x = \"nope\"; // " + Guid.NewGuid().ToString("N")));

            Assert.That(ex!.ErrorCode, Is.EqualTo("COMPILE_FAILED"));
            Assert.That(ex.Message, Does.StartWith("Code compilation failed (/langversion:9 /define:UNITY_ASSERTIONS"));
            Assert.That(ex.Message, Does.Contain("\nLine 2: CS0029: Cannot implicitly convert type 'string' to 'int'"));
            Assert.That(ex.Message, Does.Not.Contain("mcs fallback"));
        }

        [Test]
        public void HostInfrastructureFailure_IsServedByCscShared()
        {
            var selector = new CompileBackendSelector(
                new CodeDomBackend(),
                new ICompileBackend[] { new FailingBackend(), new CscSharedBackend(_server!) },
                null);
            CompileBackendSelector previous = ExecuteCodeHandler.SwapSelectorForTests(selector);
            try
            {
                ExecuteCodePayload payload = Execute("__pucResult = 6 * 7; // chain " + Guid.NewGuid().ToString("N"));

                Assert.That(payload.result, Is.EqualTo("42"));
                Assert.That(payload.timings!.compiler, Is.EqualTo("csc-shared"));
                Assert.That(payload.timings.compilerFallbackReason, Is.EqualTo("host-crashed: test"));
            }
            finally
            {
                ExecuteCodeHandler.SwapSelectorForTests(previous);
            }
        }

        [Test]
        public void HostServingAgain_RetiresOurServer_AndTheNextFallbackStartsANewOne()
        {
            var host = new SwitchableHostBackend();
            var selector = new CompileBackendSelector(
                new CodeDomBackend(),
                new ICompileBackend[] { new RetiresCscSharedBackend(host, _server!), new CscSharedBackend(_server!) },
                null);

            CompileOutcome building = selector.Compile(CompileRequestBuilder.Build(Wrapped("__pucResult = 1; // building " + Guid.NewGuid().ToString("N"))));
            Assert.That(building.Compiler, Is.EqualTo("csc-shared"), building.FallbackReason);
            Assert.That(SpinUntil(() => _server!.Snapshot().ServerPid > 0 && Servers(_server.PipeName).Count == 1, 10000), Is.True, "no server on " + _server!.PipeName);
            int first = _server.Snapshot().ServerPid;

            host.Ready = true;
            var watch = Stopwatch.StartNew();
            CompileOutcome served = selector.Compile(CompileRequestBuilder.Build(Wrapped("__pucResult = 2; // ready " + Guid.NewGuid().ToString("N"))));
            long compileMs = watch.ElapsedMilliseconds;

            Assert.That(served.Compiler, Is.EqualTo("roslyn-host"));
            Assert.That(compileMs, Is.LessThan(200), "the host-served compile waited for the retire");
            Assert.That(SpinUntil(() => !_server.IsRetiring && Servers(_server.PipeName).Count == 0, 5000), Is.True, "the idle server was not retired");
            Assert.That(_server.UsedThisDomain, Is.False);
            Assert.That(File.Exists(_server.RecordPath), Is.False);
            Assert.That(CscSharedStatus.TryRead(_server.StatusPath)!.Note, Is.EqualTo("retired: host-ready"));

            host.Ready = false;
            CompileOutcome again = selector.Compile(CompileRequestBuilder.Build(Wrapped("__pucResult = 3; // again " + Guid.NewGuid().ToString("N"))));
            Assert.That(again.Compiler, Is.EqualTo("csc-shared"), again.FallbackReason);
            Assert.That(SpinUntil(() => _server.Snapshot().ServerPid > 0 && Servers(_server.PipeName).Count == 1, 10000), Is.True, "no new server after the host failed again");
            Assert.That(_server.Snapshot().ServerPid, Is.Not.EqualTo(first));
            Assert.That(File.Exists(_server.RecordPath), Is.True);
        }

        [Test]
        public void Shutdown_KillsOurServerWithoutWaitingForAClient()
        {
            Execute("__pucResult = 1; // stop " + Guid.NewGuid().ToString("N"));
            Assert.That(SpinUntil(() => _server!.Snapshot().ServerPid > 0 && Servers(_server.PipeName).Count == 1, 10000), Is.True, "no server on " + _server!.PipeName);

            var watch = Stopwatch.StartNew();
            _server.Shutdown("test-stop");
            watch.Stop();

            Assert.That(Servers(_server.PipeName), Is.Empty);
            Assert.That(File.Exists(_server.RecordPath), Is.False);
            Assert.That(watch.ElapsedMilliseconds, Is.LessThan(300));
            Assert.That(_server.Snapshot().LastShutdownMs, Is.LessThan(300));

            // The next compile starts a new server on the same pipe.
            ExecuteCodePayload next = Execute("__pucResult = 6 * 7; // after stop " + Guid.NewGuid().ToString("N"));
            Assert.That(next.timings!.compiler, Is.EqualTo("csc-shared"), next.timings.compilerFallbackReason);
            Assert.That(next.result, Is.EqualTo("42"));
        }

        private static string Wrapped(string code)
        {
            return ExecuteCodeHandler.BuildWrappedCode(code);
        }

        /// <summary>Stands in for the host: an infrastructure failure until <see cref="Ready"/>, then a served compile.</summary>
        private sealed class SwitchableHostBackend : ICompileBackend
        {
            public bool Ready { get; set; }

            public string Id
            {
                get { return CompileBackendIds.RoslynHost; }
            }

            public CompileResult TryCompile(CompileRequest request)
            {
                return Ready
                    ? CompileResult.Success(Id, typeof(CscSharedLiveTests).Assembly, "/langversion:9")
                    : CompileResult.InfrastructureFailure(Id, string.Empty, null, null, "host-building: the host is still being built");
            }
        }

        private sealed class FailingBackend : ICompileBackend
        {
            public string Id
            {
                get { return CompileBackendIds.RoslynHost; }
            }

            public CompileResult TryCompile(CompileRequest request)
            {
                return CompileResult.InfrastructureFailure(Id, string.Empty, null, null, "host-crashed: test");
            }
        }
    }
}
