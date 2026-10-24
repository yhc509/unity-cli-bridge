#nullable enable
using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using NUnit.Framework;
using UnityCli.Protocol;
using UnityEditor;
using UnityEngine;

namespace UnityCliBridge.Bridge.Editor.Tests
{
    /// <summary>
    /// The real host: built with this editor's bundled csc, run on its bundled .NET, driven through
    /// <see cref="ExecuteCodeHandler"/> with a roslyn-host → codedom selector. Uses its own cache and
    /// pidfile folder (<c>Library/com.yhc509.unity-cli-bridge/roslyn-host-tests</c>) so it never
    /// touches an opted-in editor's own host.
    /// </summary>
    [Category("RoslynHostLive")]
    public sealed class RoslynHostLiveTests
    {
        private RoslynHostSupervisor? _supervisor;
        private CompileBackendSelector? _previous;
        private string _hostDirectory = string.Empty;

        [OneTimeSetUp]
        public void StartHost()
        {
            BundledToolchainReport toolchain = BundledToolchainLocator.Locate(EditorApplication.applicationContentsPath);
            Assume.That(toolchain.Runtime != null && toolchain.Runtime.Found && toolchain.Roslyn != null && toolchain.Roslyn.Found, "no bundled .NET runtime / Roslyn in this editor");

            string projectRoot = ProtocolConstants.GetCanonicalPath(Path.Combine(Application.dataPath, ".."));
            _hostDirectory = Path.Combine(Path.Combine(Path.Combine(projectRoot, "Library"), "com.yhc509.unity-cli-bridge"), "roslyn-host-tests");
            string packageRoot = UnityEditor.PackageManager.PackageInfo.FindForAssembly(typeof(ExecuteCodeHandler).Assembly)!.resolvedPath;
            string projectHash = ProtocolConstants.ComputeProjectHash(projectRoot);
            int editorPid = Process.GetCurrentProcess().Id;
            ExecuteCompilerSettings settings = ExecuteCompilerSettings.Parse(name =>
                name == ExecuteCompilerSettings.CompilerVariable ? "roslyn-host"
                : name == ExecuteCompilerSettings.HostWaitVariable ? "20000"
                : null);
            _supervisor = new RoslynHostSupervisor(
                settings,
                new MemoryRoslynHostSessionStore(),
                new RoslynHostLauncher(new RoslynHostLauncherOptions
                {
                    ContentsPath = EditorApplication.applicationContentsPath,
                    PackageRoot = packageRoot,
                    CacheRoot = _hostDirectory,
                    UnityVersion = Application.unityVersion,
                    ProjectHash = projectHash,
                    EditorPid = editorPid,
                }),
                () => CompileRequestBuilder.CollectLoadedAssemblyReferences(),
                new RoslynHostSupervisorOptions { ProjectHash = projectHash, EditorPid = editorPid, HostDirectory = _hostDirectory });
            _previous = ExecuteCodeHandler.SwapSelectorForTests(new CompileBackendSelector(
                new CodeDomBackend(),
                new ICompileBackend[] { new RoslynHostBackend(_supervisor) },
                null));
            // A cold image build does not block AcquireReady (execute falls back at once and the
            // build continues). This harness waits for that background start, including connect.
            string reason = string.Empty;
            bool settled = SpinUntil(() =>
            {
                string state = _supervisor!.State;
                if (state == "failed" || state == "disabled")
                {
                    return true;
                }

                return _supervisor.AcquireReady(2000, out reason) != null;
            }, 60000);
            if (_supervisor!.State == "failed" || _supervisor.State == "disabled")
            {
                _supervisor.AcquireReady(0, out reason);
                Assert.Fail(reason);
            }

            Assert.That(settled, Is.True, reason + " state=" + _supervisor.State);
        }

        [OneTimeTearDown]
        public void StopHost()
        {
            if (_previous != null)
            {
                ExecuteCodeHandler.SwapSelectorForTests(_previous);
            }

            if (_supervisor != null)
            {
                int pid = _supervisor.HostPid;
                // The domain-reload stop: kill without waiting for the exit.
                _supervisor.Shutdown("test-teardown", TimeSpan.Zero);
                Assert.That(File.Exists(Path.Combine(_hostDirectory, RoslynHostStatus.PidFileName)), Is.False);
                if (pid > 0)
                {
                    Assert.That(SpinUntil(() => !RoslynHostProcessInspector.IsAlive(pid), 5000), Is.True, "host " + pid + " still running");
                }
            }
        }

        private static ExecuteCodePayload Execute(string code)
        {
            var handler = new ExecuteCodeHandler();
            string args = ProtocolJson.Serialize(new ExecuteCodeArgs { code = code, force = true });
            return ProtocolJson.Deserialize<ExecuteCodePayload>(handler.Handle(ProtocolConstants.CommandExecuteCode, args));
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

                Thread.Sleep(20);
            }

            return condition();
        }

        [Test]
        public void Miss_ThenHit_UseTheHost()
        {
            string code = "__pucResult = UnityEngine.Mathf.Max(40, 2) + 2; // live " + Guid.NewGuid().ToString("N");

            ExecuteCodePayload miss = Execute(code);
            ExecuteCodePayload hit = Execute(code);

            Assert.That(miss.result, Is.EqualTo("42"));
            Assert.That(miss.cacheHit, Is.False);
            Assert.That(miss.timings!.compiler, Is.EqualTo("roslyn-host"), miss.timings.compilerFallbackReason);
            Assert.That(miss.timings.compilerFallbackReason, Is.Empty);
            Assert.That(miss.compileMs, Is.LessThan(1000));
            Assert.That(hit.cacheHit, Is.True);
            Assert.That(hit.compileMs, Is.EqualTo(0));
            Assert.That(hit.timings!.compiler, Is.EqualTo("roslyn-host"));
            Assert.That(hit.result, Is.EqualTo("42"));
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

            Assert.That(payload.timings!.compiler, Is.EqualTo("roslyn-host"), payload.timings.compilerFallbackReason);
            Assert.That(payload.result, Is.EqualTo("\"pos:5,6:set:7\""));
        }

        [Test]
        public void CSharp9_RecordInitWith_CompileWithThePolyfillAndRunOnMono()
        {
            string source = string.Join("\n", new[]
            {
                "public record UcbLivePoint { public int X { get; init; } public int Y { get; init; } }",
                "public sealed record UcbLivePair(string Name, int Count);",
                "public static class UcbLiveRecords",
                "{",
                "    public static string Run()",
                "    {",
                "        var p = new UcbLivePoint { X = 1, Y = 2 };",
                "        var q = p with { Y = 5 };",
                "        var pair = new UcbLivePair(\"a\", 1) with { Count = 3 };",
                "        return q.X + \",\" + q.Y + \":\" + (p == new UcbLivePoint { X = 1, Y = 2 }) + \":\" + pair.Name + pair.Count;",
                "    }",
                "}",
            });
            CompileRequest request = CompileRequestBuilder.Build(source);

            CompileResult result = new RoslynHostBackend(_supervisor!).TryCompile(request);

            Assert.That(result.Succeeded, Is.True, result.FormatFailureMessage());
            object value = result.Assembly!.GetType("UcbLiveRecords")!.GetMethod("Run")!.Invoke(null, null);
            Assert.That(value, Is.EqualTo("1,5:True:a3"));
        }

        [Test]
        public void CompileError_KeepsTheFormatAndUserLineAndDoesNotFallBack()
        {
            var ex = Assert.Throws<CommandFailureException>(() => Execute("int ok = 1;\nint x = \"nope\"; // " + Guid.NewGuid().ToString("N")));

            Assert.That(ex!.ErrorCode, Is.EqualTo("COMPILE_FAILED"));
            Assert.That(ex.Message, Does.StartWith("Code compilation failed (/langversion:9 /define:UNITY_ASSERTIONS"));
            Assert.That(ex.Message, Does.Contain("\nLine 2: CS0029: Cannot implicitly convert type 'string' to 'int'"));
            Assert.That(ex.Message, Does.Not.Contain("mcs fallback"));
        }

        [Test]
        public void Hello_ReportsTheBundledRuntime()
        {
            RoslynHostStatus status = _supervisor!.Snapshot();
            string contents = Path.GetFullPath(EditorApplication.applicationContentsPath);

            Assert.That(status.State, Is.EqualTo("ready"));
            Assert.That(Path.GetFullPath(status.RuntimeDirectory), Does.StartWith(contents));
            Assert.That(status.RuntimeVersion, Does.StartWith("6."));
            Assert.That(status.RoslynVersion, Is.Not.Empty);
            Assert.That(RoslynHostProcessInspector.ReadCommandLine(status.Pid), Does.Contain("--ucb-roslyn-host " + ProtocolConstants.ComputeProjectHash(ProtocolConstants.GetCanonicalPath(Path.Combine(Application.dataPath, "..")))));
            Assert.That(RoslynHostPidRecord.TryRead(Path.Combine(_hostDirectory, RoslynHostStatus.PidFileName))!.Pid, Is.EqualTo(status.Pid));
        }

        [Test]
        public void ForcedKill_FallsBackOnce_ThenRestarts()
        {
            int pid = _supervisor!.HostPid;
            Assume.That(pid, Is.GreaterThan(0));
            RoslynHostProcessInspector.TryKill(pid);
            Assert.That(SpinUntil(() => !RoslynHostProcessInspector.IsAlive(pid), 5000), Is.True);

            ExecuteCodePayload fallback = Execute("__pucResult = 1 + 1; // kill " + Guid.NewGuid().ToString("N"));
            ExecuteCodePayload restarted = Execute("__pucResult = 2 + 2; // restart " + Guid.NewGuid().ToString("N"));

            Assert.That(fallback.result, Is.EqualTo("2"));
            Assert.That(fallback.timings!.compiler, Is.EqualTo("codedom"));
            Assert.That(fallback.timings.compilerFallbackReason, Does.StartWith("host-crashed: "));
            Assert.That(restarted.result, Is.EqualTo("4"));
            Assert.That(restarted.timings!.compiler, Is.EqualTo("roslyn-host"), restarted.timings.compilerFallbackReason);
            Assert.That(_supervisor.HostPid, Is.Not.EqualTo(pid));
            Assert.That(_supervisor.Snapshot().RecentCrashes, Is.GreaterThanOrEqualTo(1));
        }
    }
}
