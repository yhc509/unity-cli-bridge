using System.Reflection;
using UnityCli.Protocol;
using UnityCliBridge.Bridge.Editor;

namespace UnityCli.RoslynHost.Tests;

/// <summary>
/// The editor half of the host backend (connection, supervisor, backend, selector) end to end
/// against the real host loop, with real Roslyn, in-process. The references are the test runtime's
/// own framework, standing in for the editor's Mono assemblies.
/// </summary>
public sealed class RoslynHostBackendTests : IDisposable
{
    private readonly string _hostDirectory = Path.Combine(Path.GetTempPath(), "ucb-host-tests-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_hostDirectory))
        {
            Directory.Delete(_hostDirectory, true);
        }
    }

    private const string Wrapper =
        "using System;\nusing System.Linq;\n\npublic static class PucExecuteWrapper\n{\n    public static object Execute()\n    {\n"
        + "#line 1 \"user-code\"\n{0}\n#line default\n        return null;\n    }\n}\n";

    private static string Wrap(string userCode) => Wrapper.Replace("{0}", userCode);

    private static ExecuteCompilerSettings Settings(params (string Name, string Value)[] values)
    {
        var map = values.ToDictionary(v => v.Name, v => v.Value);
        map.TryAdd(ExecuteCompilerSettings.CompilerVariable, "roslyn-host");
        return ExecuteCompilerSettings.Parse(name => map.TryGetValue(name, out string? value) ? value : null);
    }

    private RoslynHostSupervisor Supervisor(
        IRoslynHostLauncher launcher,
        ExecuteCompilerSettings? settings = null,
        IRoslynHostSessionStore? session = null,
        Func<DateTime>? clock = null,
        TimeSpan? idleCheck = null)
    {
        return new RoslynHostSupervisor(
            settings ?? Settings(),
            session ?? new MemoryRoslynHostSessionStore(),
            launcher,
            HostTestSupport.FrameworkReferences,
            new RoslynHostSupervisorOptions
            {
                ProjectHash = "abc123def456",
                EditorPid = Environment.ProcessId,
                HostDirectory = _hostDirectory,
                IdleCheckInterval = idleCheck ?? TimeSpan.FromSeconds(30),
            },
            clock);
    }

    private static CompileRequest Request(string userCode, ProjectCompilerOptions? options = null)
    {
        return new CompileRequest(Wrap(userCode), "9.0", new[] { "UCB_ACTIVE_DEFINE" }, HostTestSupport.FrameworkReferences(), options);
    }

    private static object? Run(CompileResult result)
    {
        Assert.True(result.Succeeded, result.FormatFailureMessage());
        return result.Assembly!.GetType("PucExecuteWrapper")!.GetMethod("Execute")!.Invoke(null, null);
    }

    [Fact]
    public void Miss_CompilesThroughTheHost_AndTheAssemblyRuns()
    {
        var launcher = new FakeLauncher(_ => new InProcessHostInstance());
        using RoslynHostSupervisor supervisor = Supervisor(launcher);
        var backend = new RoslynHostBackend(supervisor);

        CompileResult result = backend.TryCompile(Request("Console.WriteLine(40 + 2);"));

        Run(result);
        Assert.Equal("roslyn-host", result.BackendId);
        Assert.Equal("ready", supervisor.State);
        Assert.Equal(1, launcher.Launches);
        Assert.True(launcher.PrewarmFlags[0]);
        Assert.StartsWith("/langversion:9 /define:UNITY_ASSERTIONS;UCB_ACTIVE_DEFINE", result.CompilerOptions);
    }

    [Fact]
    public void CSharp9_RecordInitWithPatternsAndTargetTypedNew_CompileAndRun()
    {
        var launcher = new FakeLauncher(_ => new InProcessHostInstance());
        using RoslynHostSupervisor supervisor = Supervisor(launcher);
        var backend = new RoslynHostBackend(supervisor);
        string code = string.Join("\n", new[]
        {
            "var p = new Point { X = 1, Y = 2 };",
            "var q = p with { Y = 5 };",
            "System.Collections.Generic.List<int> list = new();",
            "list.Add(q.Y);",
            "object o = list.Count;",
            "string kind = o is int n and > 0 ? \"pos\" : \"other\";",
            "string? s = null; s ??= \"set\";",
            "if (o is not null) { list.Add(Twice(3)); }",
            "static int Twice(int v) => v * 2;",
            "Func<int, int, int> first = (a, _) => a;",
            "return kind + \":\" + q + \":\" + string.Join(\",\", list) + \":\" + s + \":\" + first(7, 8);",
        });
        string source = Wrap(code) + "\npublic record Point { public int X { get; init; } public int Y { get; init; } }\n";

        CompileResult result = backend.TryCompile(new CompileRequest(source, "9.0", Array.Empty<string>(), HostTestSupport.FrameworkReferences()));

        Assert.Equal("pos:Point { X = 1, Y = 5 }:5,6:set:7", Run(result));
    }

    [Fact]
    public void UserError_KeepsTheCodeDomShapeAndUserLineNumbers()
    {
        var launcher = new FakeLauncher(_ => new InProcessHostInstance());
        using RoslynHostSupervisor supervisor = Supervisor(launcher);
        var backend = new RoslynHostBackend(supervisor);

        CompileResult result = backend.TryCompile(Request("int ok = 1;\nint x = \"nope\";\nint unused;"));

        Assert.False(result.Succeeded);
        Assert.False(result.IsInfrastructureFailure);
        Assert.Equal(new[] { "Line 2: CS0029: Cannot implicitly convert type 'string' to 'int'" }, result.Errors);
        Assert.StartsWith("Code compilation failed (/langversion:9 /define:UNITY_ASSERTIONS", result.FormatFailureMessage());
        Assert.EndsWith("\nLine 2: CS0029: Cannot implicitly convert type 'string' to 'int'", result.FormatFailureMessage());
    }

    [Fact]
    public void UserError_IsNotRetriedOnCodeDom()
    {
        var launcher = new FakeLauncher(_ => new InProcessHostInstance());
        using RoslynHostSupervisor supervisor = Supervisor(launcher);
        var codeDom = new CountingBackend("codedom");
        var selector = new CompileBackendSelector(codeDom, new ICompileBackend[] { new RoslynHostBackend(supervisor) }, null);

        CompileOutcome outcome = selector.Compile(Request("int x = ;"));

        Assert.Equal("roslyn-host", outcome.Compiler);
        Assert.Null(outcome.FallbackReason);
        Assert.Equal(0, codeDom.Calls);
        Assert.Contains("Line 1: CS1525", outcome.FormatFailureMessage());
    }

    [Fact]
    public void ProjectOptions_DefinesUnsafeAndNoWarnReachTheCompiler()
    {
        var launcher = new FakeLauncher(_ => new InProcessHostInstance());
        using RoslynHostSupervisor supervisor = Supervisor(launcher);
        var backend = new RoslynHostBackend(supervisor);
        ProjectCompilerOptions options = ProjectCompilerOptions.Parse("-define:UCB_RSP_DEFINE\n-unsafe\n-nowarn:0168");
        string code = "#if UCB_RSP_DEFINE && UCB_ACTIVE_DEFINE && UNITY_ASSERTIONS\nint value = 6;\n#else\nint value = 0;\n#endif\n"
            + "unsafe { int* p = &value; *p = *p * 7; }\nreturn value;";

        CompileResult result = backend.TryCompile(Request(code, options));

        Assert.Equal(42, Run(result));
        Assert.EndsWith(" /unsafe", result.CompilerOptions);
        Assert.Contains("UCB_RSP_DEFINE", result.CompilerOptions);

        CompileResult withoutUnsafe = backend.TryCompile(Request("int v = 1; unsafe { int* p = &v; }"));
        Assert.False(withoutUnsafe.Succeeded);
        Assert.Contains("CS0227", withoutUnsafe.Errors[0]);
    }

    [Fact]
    public void Polyfill_IsAppendedHiddenAndSuppressed()
    {
        RoslynHostCompileRequest request = RoslynHostBackend.BuildHostRequest(Request("return 1;"), new List<string> { "A" });

        Assert.EndsWith(RoslynHostBackend.IsExternalInitPolyfill, request.Source);
        Assert.Contains("#line hidden", RoslynHostBackend.IsExternalInitPolyfill);
        Assert.Contains("internal static class IsExternalInit", RoslynHostBackend.IsExternalInitPolyfill);
        Assert.Contains("CS0436", request.NoWarn);
        Assert.Equal("9", request.LanguageVersion);
        Assert.StartsWith("ucb_execute_", request.AssemblyName);
        Assert.NotEqual(request.AssemblyName, RoslynHostBackend.BuildHostRequest(Request("return 1;"), new List<string>()).AssemblyName);
    }

    [Fact]
    public void Diagnostics_ErrorsOnly_WarningsOnlyWithoutErrors()
    {
        var diagnostics = new List<RoslynHostDiagnostic>
        {
            new() { Id = "CS0168", Severity = "warning", Line = 1, Message = "unused" },
            new() { Id = "CS0029", Severity = "error", Line = 2, Message = "bad" },
            new() { Id = "CS8019", Severity = "error", Line = 0, Message = "no location" },
        };

        Assert.Equal(new[] { "Line 2: CS0029: bad", "CS8019: no location" }, RoslynHostBackend.FormatFailureDiagnostics(diagnostics));
        Assert.Equal(new[] { "Line 1: CS0168: unused" }, RoslynHostBackend.FormatFailureDiagnostics(diagnostics.Take(1).ToList()));
    }

    [Fact]
    public void SetRefs_IsSentOnStart_AndAgainOnlyWhenTheReferenceSetChanges()
    {
        InProcessHostInstance? host = null;
        var launcher = new FakeLauncher(_ => host = new InProcessHostInstance());
        using RoslynHostSupervisor supervisor = Supervisor(launcher);
        var backend = new RoslynHostBackend(supervisor);

        Run(backend.TryCompile(Request("return 1;")));
        Run(backend.TryCompile(Request("return 2;")));
        var fewer = new CompileRequest(Wrap("return 3;"), "9.0", Array.Empty<string>(), HostTestSupport.FrameworkReferences().Take(3).ToList());
        backend.TryCompile(fewer);

        Assert.Equal(new[] { "setRefs", "compile", "compile", "setRefs", "compile" }, host!.RequestTypes);
    }

    [Fact]
    public void ToolchainMissing_FallsBackWithTheReason_AndIsNotRetriedInTheDomain()
    {
        var launcher = new ThrowingLauncher(new RoslynHostException(RoslynHostFailureCodes.ToolchainMissing, "bundled .NET runtime not found: dotnet muxer not found"));
        using RoslynHostSupervisor supervisor = Supervisor(launcher);
        var codeDom = new CountingBackend("codedom", succeed: true);
        var selector = new CompileBackendSelector(codeDom, new ICompileBackend[] { new RoslynHostBackend(supervisor) }, null);

        CompileOutcome first = selector.Compile(Request("return 1;"));
        CompileOutcome second = selector.Compile(Request("return 2;"));

        Assert.Equal("codedom", first.Compiler);
        Assert.Equal("toolchain-missing: bundled .NET runtime not found: dotnet muxer not found", first.FallbackReason);
        Assert.Equal(first.FallbackReason, second.FallbackReason);
        Assert.Equal(1, launcher.Launches);
        Assert.Equal("failed", supervisor.State);
        Assert.Equal("codedom", supervisor.Snapshot().ActiveBackend);
    }

    [Theory]
    [InlineData(RoslynHostFailureCodes.BuildFailed)]
    [InlineData(RoslynHostFailureCodes.ForeignRuntime)]
    public void BuildFailureAndForeignRuntime_ArePermanentForTheDomain(string code)
    {
        var launcher = new ThrowingLauncher(new RoslynHostException(code, "detail"));
        using RoslynHostSupervisor supervisor = Supervisor(launcher);

        Assert.Null(supervisor.AcquireReady(2000, out string reason));
        Assert.Null(supervisor.AcquireReady(2000, out _));

        Assert.Equal(code + ": detail", reason);
        Assert.Equal(1, launcher.Launches);
    }

    [Fact]
    public void Crash_FallsBackThisRequest_ThenRestartsOnTheNext()
    {
        var instances = new List<ScriptedHostInstance>();
        var launcher = new FakeLauncher(attempt =>
        {
            var instance = new ScriptedHostInstance(ScriptedHostInstance.Replies(request =>
            {
                if (attempt == 1)
                {
                    throw new RoslynHostException(RoslynHostFailureCodes.Crashed, "the host closed its output (process exited)");
                }

                return new RoslynHostCompileResult { Id = request.Id, Ok = false, Diagnostics = { new RoslynHostDiagnostic { Id = "CS0001", Severity = "error", Line = 1, Message = "m" } } }.ToMessage();
            }));
            instances.Add(instance);
            return instance;
        });
        using RoslynHostSupervisor supervisor = Supervisor(launcher);
        var codeDom = new CountingBackend("codedom", succeed: true);
        var selector = new CompileBackendSelector(codeDom, new ICompileBackend[] { new RoslynHostBackend(supervisor) }, null);

        CompileOutcome crashed = selector.Compile(Request("return 1;"));
        CompileOutcome restarted = selector.Compile(Request("return 2;"));

        Assert.Equal("codedom", crashed.Compiler);
        Assert.Equal("host-crashed: the host closed its output (process exited)", crashed.FallbackReason);
        Assert.Equal(1, instances[0].Kills);
        Assert.Equal("roslyn-host", restarted.Compiler);
        Assert.Equal(2, launcher.Launches);
        Assert.Equal(1, supervisor.Snapshot().RecentCrashes);
        Assert.False(File.Exists(supervisor.PidFilePath) && RoslynHostPidRecord.TryRead(supervisor.PidFilePath)!.Pid == instances[0].Pid);
    }

    [Fact]
    public void ThreeCrashesInSixtySeconds_DisableTheHostForTheSession()
    {
        var clock = new MutableClock();
        var session = new MemoryRoslynHostSessionStore();
        var launcher = new FakeLauncher(_ => new ScriptedHostInstance(ScriptedHostInstance.Replies(
            _ => throw new RoslynHostException(RoslynHostFailureCodes.Timeout, "no reply from the host within 10000 ms"))));
        using RoslynHostSupervisor supervisor = Supervisor(launcher, session: session, clock: () => clock.Now);
        var codeDom = new CountingBackend("codedom", succeed: true);
        var selector = new CompileBackendSelector(codeDom, new ICompileBackend[] { new RoslynHostBackend(supervisor) }, null);

        for (int i = 0; i < 3; i++)
        {
            clock.Now = clock.Now.AddSeconds(10);
            Assert.StartsWith("timeout: ", selector.Compile(Request("return " + i + ";")).FallbackReason);
        }

        CompileOutcome disabled = selector.Compile(Request("return 9;"));

        Assert.Equal(3, launcher.Launches);
        Assert.Equal("disabled", supervisor.State);
        Assert.StartsWith("session-disabled: 3 host failures within 60 s; last: timeout: ", disabled.FallbackReason);
        Assert.StartsWith("3 host failures within 60 s", session.GetString(RoslynHostSupervisor.SessionKeyDisabledReason, ""));
        Assert.Equal("codedom", supervisor.Snapshot().ActiveBackend);

        // A new domain in the same session starts disabled and never launches.
        var next = new FakeLauncher(_ => new InProcessHostInstance());
        using RoslynHostSupervisor reloaded = Supervisor(next, session: session, clock: () => clock.Now);
        Assert.Null(reloaded.AcquireReady(100, out string reason));
        Assert.StartsWith("session-disabled: ", reason);
        Assert.Equal(0, next.Launches);
    }

    [Fact]
    public void CrashesSpreadOverMoreThanSixtySeconds_DoNotDisable()
    {
        var clock = new MutableClock();
        var launcher = new FakeLauncher(_ => new ScriptedHostInstance(ScriptedHostInstance.Replies(
            _ => throw new RoslynHostException(RoslynHostFailureCodes.Crashed, "gone"))));
        using RoslynHostSupervisor supervisor = Supervisor(launcher, clock: () => clock.Now);
        var backend = new RoslynHostBackend(supervisor);

        for (int i = 0; i < 4; i++)
        {
            clock.Now = clock.Now.AddSeconds(35);
            backend.TryCompile(Request("return 1;"));
        }

        Assert.NotEqual("disabled", supervisor.State);
        Assert.True(launcher.Launches >= 4);
    }

    [Fact]
    public void HostNotReadyWithinTheWait_FallsBack_ThenTheReadyHostServesTheNextRequest()
    {
        var launcher = new FakeLauncher(_ => new InProcessHostInstance()) { Gate = new ManualResetEventSlim(false) };
        using RoslynHostSupervisor supervisor = Supervisor(launcher, Settings((ExecuteCompilerSettings.HostWaitVariable, "100")));
        var codeDom = new CountingBackend("codedom", succeed: true);
        var selector = new CompileBackendSelector(codeDom, new ICompileBackend[] { new RoslynHostBackend(supervisor) }, null);

        CompileOutcome waiting = selector.Compile(Request("return 1;"));
        launcher.Gate.Set();
        Assert.True(SpinWait.SpinUntil(() => supervisor.State == "ready", TimeSpan.FromSeconds(30)));
        CompileOutcome ready = selector.Compile(Request("return 2;"));

        Assert.Equal("codedom", waiting.Compiler);
        Assert.StartsWith("host-not-ready: the host is still starting after ", waiting.FallbackReason);
        Assert.Equal("roslyn-host", ready.Compiler);
        Assert.Equal(1, launcher.Launches);
    }

    [Fact]
    public void HostThatDiedWhileIdle_IsCountedAndRestarted()
    {
        var instances = new List<ScriptedHostInstance>();
        var launcher = new FakeLauncher(_ =>
        {
            var instance = new ScriptedHostInstance(ScriptedHostInstance.Replies(
                request => new RoslynHostCompileResult { Id = request.Id, Ok = false }.ToMessage()));
            instances.Add(instance);
            return instance;
        });
        using RoslynHostSupervisor supervisor = Supervisor(launcher);

        Assert.NotNull(supervisor.AcquireReady(5000, out _));
        instances[0].Alive = false;
        IRoslynHostInstance? thisRequest = supervisor.AcquireReady(5000, out string reason);
        IRoslynHostInstance? next = supervisor.AcquireReady(5000, out _);

        Assert.Null(thisRequest);
        Assert.Equal("host-crashed: the host exited while idle", reason);
        Assert.Same(instances[1], next);
        Assert.Equal(1, supervisor.Snapshot().RecentCrashes);
        Assert.StartsWith("host-crashed: the host exited while idle", supervisor.Snapshot().LastError);
    }

    [Fact]
    public void MismatchedReplyId_IsAFrameError()
    {
        var launcher = new FakeLauncher(_ => new ScriptedHostInstance(ScriptedHostInstance.Replies(
            _ => new RoslynHostCompileResult { Id = "other", Ok = false }.ToMessage())));
        using RoslynHostSupervisor supervisor = Supervisor(launcher);

        CompileResult result = new RoslynHostBackend(supervisor).TryCompile(Request("return 1;"));

        Assert.True(result.IsInfrastructureFailure);
        Assert.StartsWith("frame-error: reply id 'other' does not match", result.FailureDetail);
    }

    [Fact]
    public void HostReportedError_FallsBackWithoutCountingACrash()
    {
        var launcher = new FakeLauncher(_ => new ScriptedHostInstance(ScriptedHostInstance.Replies(
            request => new RoslynHostCompileResult { Id = request.Id, Error = "Compiler failure: boom" }.ToMessage())));
        using RoslynHostSupervisor supervisor = Supervisor(launcher);

        CompileResult result = new RoslynHostBackend(supervisor).TryCompile(Request("return 1;"));

        Assert.True(result.IsInfrastructureFailure);
        Assert.Equal("host-error: Compiler failure: boom", result.FailureDetail);
        Assert.Equal(0, supervisor.Snapshot().RecentCrashes);
    }

    [Fact]
    public void UnloadableImage_IsAnInfrastructureFailure()
    {
        var launcher = new FakeLauncher(_ => new ScriptedHostInstance(ScriptedHostInstance.Replies(
            request => new RoslynHostCompileResult { Id = request.Id, Ok = true, Assembly = new byte[] { 1, 2, 3 } }.ToMessage())));
        using RoslynHostSupervisor supervisor = Supervisor(launcher);

        CompileResult result = new RoslynHostBackend(supervisor).TryCompile(Request("return 1;"));

        Assert.True(result.IsInfrastructureFailure);
        Assert.StartsWith("load-failed: BadImageFormatException", result.FailureDetail);
    }

    [Fact]
    public void Shutdown_QuitsTheHost_DeletesThePidfile_AndWritesStoppedStatus()
    {
        InProcessHostInstance? host = null;
        var launcher = new FakeLauncher(_ => host = new InProcessHostInstance());
        RoslynHostSupervisor supervisor = Supervisor(launcher);
        Assert.NotNull(supervisor.AcquireReady(10000, out _));
        RoslynHostPidRecord? record = RoslynHostPidRecord.TryRead(supervisor.PidFilePath);
        Assert.Equal(host!.Pid, record!.Pid);
        Assert.Equal("--ucb-roslyn-host abc123def456", record.Marker);
        Assert.Equal(Environment.ProcessId, record.EditorPid);
        Assert.Equal("ready", RoslynHostStatus.TryRead(supervisor.StatusFilePath)!.State);

        supervisor.Shutdown("domain-reload");

        Assert.True(host.WaitForServerExit(TimeSpan.FromSeconds(5)));
        Assert.Equal(UnityCliBridge.RoslynHost.RoslynHostExitReason.Quit, host.ExitReason);
        Assert.False(File.Exists(supervisor.PidFilePath));
        RoslynHostStatus status = RoslynHostStatus.TryRead(supervisor.StatusFilePath)!;
        Assert.Equal("stopped", status.State);
        Assert.Equal("domain-reload", status.Note);
        Assert.Null(supervisor.AcquireReady(100, out _));
        Assert.Equal(1, launcher.Launches);
    }

    [Fact]
    public void Shutdown_DuringAStart_KillsTheSpawnedHost()
    {
        var instance = new ScriptedHostInstance(ScriptedHostInstance.Replies(request => new RoslynHostCompileResult { Id = request.Id }.ToMessage()));
        var launcher = new FakeLauncher(_ => instance) { Gate = new ManualResetEventSlim(false) };
        RoslynHostSupervisor supervisor = Supervisor(launcher);
        supervisor.BeginStart("test");
        Assert.True(SpinWait.SpinUntil(() => launcher.Launches == 1 && File.Exists(supervisor.PidFilePath), TimeSpan.FromSeconds(10)));

        supervisor.Shutdown("editor-quit");
        launcher.Gate.Set();

        Assert.True(SpinWait.SpinUntil(() => !supervisor.IsStarting, TimeSpan.FromSeconds(10)));
        Assert.True(instance.Kills >= 1);
        Assert.Equal(0, supervisor.HostPid);
        Assert.False(File.Exists(supervisor.PidFilePath));
    }

    [Fact]
    public void IdleExit_StopsAnUnusedHost_AndTheNextRequestStartsANewOne()
    {
        var clock = new MutableClock();
        var instances = new List<ScriptedHostInstance>();
        var launcher = new FakeLauncher(_ =>
        {
            var instance = new ScriptedHostInstance(ScriptedHostInstance.Replies(request => new RoslynHostCompileResult { Id = request.Id }.ToMessage()));
            instances.Add(instance);
            return instance;
        });
        using RoslynHostSupervisor supervisor = Supervisor(
            launcher,
            Settings((ExecuteCompilerSettings.IdleExitVariable, "1")),
            clock: () => clock.Now,
            idleCheck: TimeSpan.FromMilliseconds(50));
        Assert.NotNull(supervisor.AcquireReady(5000, out _));

        clock.Now = clock.Now.AddSeconds(30);
        Thread.Sleep(300);
        Assert.Equal("ready", supervisor.State);
        clock.Now = clock.Now.AddSeconds(61);
        Assert.True(SpinWait.SpinUntil(() => supervisor.State == "stopped", TimeSpan.FromSeconds(10)));

        Assert.Equal(1, instances[0].Stops);
        Assert.True(SpinWait.SpinUntil(() => RoslynHostStatus.TryRead(supervisor.StatusFilePath)?.Note == "idle-exit", TimeSpan.FromSeconds(10)));
        Assert.NotNull(supervisor.AcquireReady(5000, out _));
        Assert.Equal(2, launcher.Launches);
        Assert.Equal(0, supervisor.Snapshot().RecentCrashes);
    }

    [Theory]
    [InlineData("Auto", false, false)]
    [InlineData("Auto", true, true)]
    [InlineData("Eager", false, true)]
    [InlineData("Lazy", true, false)]
    [InlineData("Off", true, false)]
    public void PrewarmPolicy_DecidesTheStartOnDomainLoad(string policyName, bool used, bool expected)
    {
        var policy = Enum.Parse<RoslynHostPrewarmPolicy>(policyName);
        Assert.Equal(expected, RoslynHostSupervisor.ShouldPrewarmOnLoad(policy, used));
    }

    [Fact]
    public void OnDomainLoaded_AutoAfterExecuteWasUsed_StartsWithPrewarm_AndOffStartsWithout()
    {
        var session = new MemoryRoslynHostSessionStore();
        session.SetString(RoslynHostSupervisor.SessionKeyExecuteUsed, "1");
        var launcher = new FakeLauncher(_ => new InProcessHostInstance());
        using (RoslynHostSupervisor supervisor = Supervisor(launcher, session: session))
        {
            supervisor.OnDomainLoaded();
            Assert.True(SpinWait.SpinUntil(() => supervisor.State == "ready", TimeSpan.FromSeconds(30)));
        }

        var lazy = new FakeLauncher(_ => new InProcessHostInstance());
        using (RoslynHostSupervisor supervisor = Supervisor(lazy, Settings((ExecuteCompilerSettings.PrewarmVariable, "off")), session))
        {
            supervisor.OnDomainLoaded();
            Thread.Sleep(200);
            Assert.Equal(0, lazy.Launches);
            Assert.NotNull(supervisor.AcquireReady(10000, out _));
        }

        Assert.Equal(new[] { true }, launcher.PrewarmFlags);
        Assert.Equal(new[] { false }, lazy.PrewarmFlags);
    }

    [Fact]
    public void AcquireReady_MarksExecuteAsUsedForTheSession()
    {
        var session = new MemoryRoslynHostSessionStore();
        using RoslynHostSupervisor supervisor = Supervisor(new FakeLauncher(_ => new InProcessHostInstance()), session: session);
        Assert.False(supervisor.ExecuteUsedThisSession);

        supervisor.AcquireReady(10000, out _);

        Assert.True(supervisor.ExecuteUsedThisSession);
    }

    [Fact]
    public void CrashTracker_SlidesTheWindowAndRoundTrips()
    {
        var tracker = new RoslynHostCrashTracker();
        DateTime t = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

        Assert.False(tracker.Record(t));
        Assert.False(tracker.Record(t.AddSeconds(30)));
        Assert.False(tracker.Record(t.AddSeconds(61)));
        Assert.Equal(2, tracker.RecentCount(t.AddSeconds(61)));
        Assert.True(tracker.Record(t.AddSeconds(80)));

        var copy = new RoslynHostCrashTracker();
        copy.Load(tracker.Serialize(), t.AddSeconds(80));
        Assert.Equal(3, copy.RecentCount(t.AddSeconds(80)));
        copy.Load("junk,,12x", t);
        Assert.Equal(0, copy.RecentCount(t));
    }

    [Fact]
    public void ReferenceSetHash_ChangesWithTheSetOrAFileWrite_NotWithOrder()
    {
        string a = Path.Combine(_hostDirectory, "a.dll");
        string b = Path.Combine(_hostDirectory, "b.dll");
        Directory.CreateDirectory(_hostDirectory);
        File.WriteAllText(a, "a");
        File.WriteAllText(b, "b");

        string first = RoslynHostReferenceSet.ComputeHash(new[] { a, b });

        Assert.Equal(first, RoslynHostReferenceSet.ComputeHash(new[] { b, a }));
        Assert.NotEqual(first, RoslynHostReferenceSet.ComputeHash(new[] { a }));
        File.WriteAllText(b, "bb");
        Assert.NotEqual(first, RoslynHostReferenceSet.ComputeHash(new[] { a, b }));
        Assert.Equal(16, first.Length);
    }

    private sealed class ThrowingLauncher : IRoslynHostLauncher
    {
        private readonly RoslynHostException _error;

        public ThrowingLauncher(RoslynHostException error) => _error = error;

        public int Launches { get; private set; }

        public RoslynHostLaunch Launch(bool prewarm, Action<IRoslynHostInstance> onSpawned)
        {
            Launches++;
            throw _error;
        }
    }

    private sealed class CountingBackend : ICompileBackend
    {
        private readonly bool _succeed;

        public CountingBackend(string id, bool succeed = false)
        {
            Id = id;
            _succeed = succeed;
        }

        public string Id { get; }

        public int Calls { get; private set; }

        public CompileResult TryCompile(CompileRequest request)
        {
            Calls++;
            return _succeed
                ? CompileResult.Success(Id, typeof(CountingBackend).Assembly, "/langversion:latest")
                : CompileResult.CompileError(Id, "/langversion:latest", new[] { "Line 1: CS0000: fake" }, null);
        }
    }
}
