using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using UnityCli.Protocol;
using UnityCliBridge.Bridge.Editor;

namespace UnityCli.RoslynHost.Tests;

public sealed class CscSharedCommandLineTests
{
    private const string Pipe = "ucb-d6887d89-abc123";

    [Fact]
    public void Arguments_AreTheFixedShortCommandLine()
    {
        List<string> args = CscSharedCommandLine.BuildArguments("/U/DotNetSdkRoslyn/csc.dll", Pipe, "/L/x.rsp", "/L/x.cs");

        Assert.Equal(
            new[]
            {
                "exec", "/U/DotNetSdkRoslyn/csc.dll", "/noconfig", "/nologo", "/nostdlib+", "/target:library", "/langversion:9",
                "/preferreduilang:en-US", "/shared:" + Pipe, "@/L/x.rsp", "/L/x.cs",
            },
            args);
    }

    [Fact]
    public void CommandLine_DoesNotGrowWithTheReferenceCount()
    {
        List<string> references = Enumerable.Range(0, 357)
            .Select(i => "/Applications/Unity/Hub/Editor/6000.3.10f1/Unity.app/Contents/Managed/UnityEngine/Module" + i + ".dll")
            .ToList();
        var request = new RoslynHostCompileRequest { Source = "class A {}", AssemblyName = "ucb_execute_x" };

        string commandLine = BundledDotnetProcess.JoinArguments(
            CscSharedCommandLine.BuildArguments("/U/csc.dll", Pipe, "/L/ucb_execute_x.rsp", "/L/ucb_execute_x.cs"));
        List<string> rsp = CscSharedCommandLine.BuildResponseFile(request, references, "/L/ucb_execute_x.dll");

        Assert.True(commandLine.Length < 300, commandLine);
        Assert.Equal(357, rsp.Count(line => line.StartsWith("/r:", StringComparison.Ordinal)));
    }

    [Fact]
    public void ResponseFile_CarriesTheHostOptionsOutputAndReferences()
    {
        var request = new RoslynHostCompileRequest
        {
            Source = "class A {}",
            Defines = new List<string> { "UNITY_ASSERTIONS", "UNITY_EDITOR", "UCB_RSP_DEFINE" },
            AllowUnsafe = true,
            NoWarn = new List<string> { "CS0436", "0168", "CS0618;/out:evil", "cs0219" },
            AssemblyName = "ucb_execute_x",
        };

        List<string> rsp = CscSharedCommandLine.BuildResponseFile(request, new[] { "/a/UnityEngine.dll", "", "/b c/mscorlib.dll" }, "/L/tmp dir/ucb_execute_x.dll");

        Assert.Equal(
            new[]
            {
                "/optimize+", "/debug-", "/utf8output", "/codepage:65001",
                "/define:UNITY_ASSERTIONS;UNITY_EDITOR;UCB_RSP_DEFINE",
                "/unsafe+",
                "/nowarn:CS1701,CS1702,CS0436,0168,cs0219",
                "/out:\"/L/tmp dir/ucb_execute_x.dll\"",
                "/r:\"/a/UnityEngine.dll\"",
                "/r:\"/b c/mscorlib.dll\"",
            },
            rsp);
    }

    [Fact]
    public void ResponseFile_WithoutDefinesOrUnsafe_OmitsThem()
    {
        List<string> rsp = CscSharedCommandLine.BuildResponseFile(new RoslynHostCompileRequest(), Array.Empty<string>(), "/o.dll");

        Assert.DoesNotContain(rsp, line => line.StartsWith("/define", StringComparison.Ordinal));
        Assert.DoesNotContain("/unsafe+", rsp);
        Assert.Contains("/nowarn:CS1701,CS1702", rsp);
    }

    [Theory]
    [InlineData("/a/b.dll", "\"/a/b.dll\"")]
    [InlineData("/a b/c,d;e.dll", "\"/a b/c,d;e.dll\"")]
    [InlineData("/프로젝트/라이브러리.dll", "\"/프로젝트/라이브러리.dll\"")]
    [InlineData(@"C:\Program Files\Unity\x.dll", "\"C:\\Program Files\\Unity\\x.dll\"")]
    [InlineData(@"C:\dir\", "\"C:\\dir\\\\\"")]
    [InlineData("/a\"b.dll", "\"/a\\\"b.dll\"")]
    public void Quote_FollowsTheCompilerCommandLineRules(string value, string expected)
    {
        Assert.Equal(expected, CscSharedCommandLine.Quote(value));
    }

    [Fact]
    public void ShutdownArguments_NameOnlyOurPipe()
    {
        Assert.Equal(
            new[] { "exec", "/U/VBCSCompiler.dll", "-shutdown", "-pipename:" + Pipe },
            CscSharedCommandLine.BuildShutdownArguments("/U/VBCSCompiler.dll", Pipe));
    }

    [Fact]
    public void ParseDiagnostics_ReadsMappedUnmappedAndLocationlessLines()
    {
        string output = string.Join(
            "\r\n",
            "user-code(2,9): error CS0029: Cannot implicitly convert type 'string' to 'int'",
            "/L/tmp/ucb_execute_x.cs(41,5): warning CS0162: Unreachable code detected",
            "/L/tmp/ucb_execute_x.cs(3,1,3,9): error CS1002: ; expected",
            "error CS2001: Source file '/x.cs' could not be found.",
            "CSC : error CS0006: Metadata file '/missing.dll' could not be found",
            "Microsoft (R) Visual C# Compiler version 4.3.1",
            "");

        List<RoslynHostDiagnostic> diagnostics = CscSharedCommandLine.ParseDiagnostics(output);

        Assert.Equal(5, diagnostics.Count);
        Assert.Equal(("CS0029", "error", 2, 9, "user-code"), (diagnostics[0].Id, diagnostics[0].Severity, diagnostics[0].Line, diagnostics[0].Column, diagnostics[0].File));
        Assert.Equal("Cannot implicitly convert type 'string' to 'int'", diagnostics[0].Message);
        Assert.Equal(("CS0162", "warning", 41), (diagnostics[1].Id, diagnostics[1].Severity, diagnostics[1].Line));
        Assert.Equal(("CS1002", 3, 1), (diagnostics[2].Id, diagnostics[2].Line, diagnostics[2].Column));
        Assert.Equal(("CS2001", 0, ""), (diagnostics[3].Id, diagnostics[3].Line, diagnostics[3].File));
        Assert.Equal(("CS0006", 0), (diagnostics[4].Id, diagnostics[4].Line));
    }

    [Fact]
    public void Diagnostics_FormatExactlyLikeTheHost()
    {
        List<RoslynHostDiagnostic> diagnostics = CscSharedCommandLine.ParseDiagnostics(
            "user-code(2,9): error CS0029: Cannot implicitly convert type 'string' to 'int'\n"
            + "user-code(1,5): warning CS0219: The variable 'ok' is assigned but its value is never used\n");

        Assert.Equal(
            new[] { "Line 2: CS0029: Cannot implicitly convert type 'string' to 'int'" },
            RoslynHostBackend.FormatFailureDiagnostics(diagnostics));
    }

    [Fact]
    public void UserCompileError_NeedsAnErrorLocatedInTheSource()
    {
        Assert.True(CscSharedCommandLine.IsUserCompileError(CscSharedCommandLine.ParseDiagnostics("user-code(1,1): error CS0103: The name 'x' does not exist in the current context")));
        Assert.False(CscSharedCommandLine.IsUserCompileError(CscSharedCommandLine.ParseDiagnostics("error CS0006: Metadata file '/m.dll' could not be found")));
        Assert.False(CscSharedCommandLine.IsUserCompileError(CscSharedCommandLine.ParseDiagnostics("user-code(1,1): warning CS0168: The variable 'e' is declared but never used")));
        Assert.False(CscSharedCommandLine.IsUserCompileError(CscSharedCommandLine.ParseDiagnostics("Unhandled exception. System.IO.IOException: pipe")));
    }
}

public sealed class CscSharedServerTests
{
    private const string ProjectHash = "d6887d898597";
    private const int EditorPid = 500;
    private static readonly string Pipe = CscSharedPipe.BuildName(ProjectHash, "6000.3.10f1");

    [Fact]
    public void OrphanPolicy_KillsOnlyOurServerWhenItsEditorIsGoneOrCurrent()
    {
        var processes = new[]
        {
            Proc(700, "/U/dotnet exec /U/VBCSCompiler.dll -pipename:" + Pipe),
            Proc(701, "/U/dotnet exec /U/VBCSCompiler.dll -pipename:Aq3bTL0ZJ9ExP4jQn2N1wSGx+Ko"),
            Proc(702, "/U/dotnet exec /U/VBCSCompiler.dll -pipename:" + Pipe + "0"),
            Proc(703, "/U/dotnet exec /U/VBCSCompiler.dll -shutdown -pipename:" + Pipe),
            Proc(EditorPid, "/U/Unity -projectPath /p -pipename:" + Pipe + " VBCSCompiler"),
        };
        var deadEditor = new CscSharedServerRecord { Pipe = Pipe, EditorPid = 400 };
        var thisEditor = new CscSharedServerRecord { Pipe = Pipe, EditorPid = EditorPid };

        Assert.Equal(new[] { 700 }, CscSharedOrphanPolicy.SelectTargets(deadEditor, ProjectHash, EditorPid, _ => false, processes, out _));
        Assert.Equal(new[] { 700 }, CscSharedOrphanPolicy.SelectTargets(thisEditor, ProjectHash, EditorPid, _ => true, processes, out _));
    }

    [Fact]
    public void OrphanPolicy_SparesWithoutAProvenRecord()
    {
        var processes = new[] { Proc(700, "/U/dotnet exec /U/VBCSCompiler.dll -pipename:" + Pipe) };

        Assert.Empty(CscSharedOrphanPolicy.SelectTargets(null, ProjectHash, EditorPid, _ => false, processes, out string noRecord));
        Assert.Equal("no server record", noRecord);
        Assert.Empty(CscSharedOrphanPolicy.SelectTargets(
            new CscSharedServerRecord { Pipe = CscSharedPipe.BuildName("86c6e1d6e8e5", "6000.3.10f1"), EditorPid = 400 },
            ProjectHash,
            EditorPid,
            _ => false,
            new[] { Proc(701, "/U/dotnet exec /U/VBCSCompiler.dll -pipename:" + CscSharedPipe.BuildName("86c6e1d6e8e5", "6000.3.10f1")) },
            out string foreign));
        Assert.Contains("is not this project's", foreign);
        Assert.Empty(CscSharedOrphanPolicy.SelectTargets(
            new CscSharedServerRecord { Pipe = Pipe, EditorPid = 400 },
            ProjectHash,
            EditorPid,
            pid => pid == 400,
            processes,
            out string otherEditor));
        Assert.Contains("still running", otherEditor);
    }

    [Fact]
    public void Shutdown_NeverUsedAndNoRecord_DoesNothing()
    {
        using var temp = new TempDir();
        var table = new FakeProcessTable();
        var server = new CscSharedServer(Options(temp.Path), table);

        server.OnDomainLoaded();
        server.Shutdown("domain-reload");

        Assert.Equal(0, table.ListCalls);
        Assert.Empty(table.Killed);
        Assert.False(File.Exists(server.RecordPath));
    }

    [Fact]
    public void DomainLoad_WithALeftoverRecord_KillsThatServerAndDeletesTheRecord()
    {
        using var temp = new TempDir();
        var table = new FakeProcessTable
        {
            Processes =
            {
                Proc(700, "/U/dotnet exec /U/VBCSCompiler.dll -pipename:" + Pipe),
                Proc(701, "/U/dotnet exec /U/VBCSCompiler.dll -pipename:Aq3bTL0ZJ9ExP4jQn2N1wSGx+Ko"),
            },
        };
        var server = new CscSharedServer(Options(temp.Path), table);
        new CscSharedServerRecord { Pipe = Pipe, EditorPid = 400 }.Write(server.RecordPath);

        List<int> killed = server.CleanUpLeftoverServer();

        Assert.Equal(new[] { 700 }, killed);
        Assert.Equal(new[] { 700 }, table.Killed);
        Assert.False(File.Exists(server.RecordPath));
    }

    [Fact]
    public void DomainLoad_RecordOfAnotherLiveEditor_IsKept()
    {
        using var temp = new TempDir();
        var table = new FakeProcessTable { Alive = { 400 }, Processes = { Proc(700, "/U/dotnet exec /U/VBCSCompiler.dll -pipename:" + Pipe) } };
        var server = new CscSharedServer(Options(temp.Path), table);
        new CscSharedServerRecord { Pipe = Pipe, EditorPid = 400 }.Write(server.RecordPath);

        Assert.Empty(server.CleanUpLeftoverServer());
        Assert.Empty(table.Killed);
        Assert.True(File.Exists(server.RecordPath));
    }

    [Fact]
    public void MissingToolchain_IsAnInfrastructureFailure_AndTheChainFallsBack()
    {
        using var temp = new TempDir();
        CscSharedServerOptions options = Options(temp.Path);
        options.Toolchain = () => null;
        var server = new CscSharedServer(options, new FakeProcessTable());
        var backend = new CscSharedBackend(server);
        var codeDom = new StubBackend("codedom", CompileResult.Success("codedom", typeof(CscSharedServerTests).Assembly, "/langversion:7.3"));

        CompileResult result = backend.TryCompile(new CompileRequest("class A {}", "9.0", Array.Empty<string>(), Array.Empty<string>()));
        CompileOutcome outcome = new CompileBackendSelector(codeDom, new ICompileBackend[] { backend }, null)
            .Compile(new CompileRequest("class A {}", "9.0", Array.Empty<string>(), Array.Empty<string>()));

        Assert.True(result.IsInfrastructureFailure);
        Assert.StartsWith("csc-shared-unavailable: ", result.FailureDetail);
        Assert.Equal("codedom", outcome.Compiler);
        Assert.StartsWith("csc-shared-unavailable: ", outcome.FallbackReason);
        Assert.Equal("failed", server.Snapshot().State);
        Assert.StartsWith("csc-shared-unavailable: ", server.Snapshot().LastError);
        Assert.False(File.Exists(server.RecordPath));
    }

    [Fact]
    public void StartInfo_UsesTheBundledMuxerForCscAndTheServer()
    {
        using var temp = new TempDir();
        string dotnet = Path.Combine(temp.Path, RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? "dotnet.exe" : "dotnet");
        File.WriteAllText(dotnet, string.Empty);
        var toolchain = new CscSharedToolchain(dotnet, "/U/csc.dll", "/U/VBCSCompiler.dll", "4.3.1");
        var server = new CscSharedServer(Options(temp.Path), new FakeProcessTable());
        string? saved = Environment.GetEnvironmentVariable("DOTNET_ROOT");
        Environment.SetEnvironmentVariable("DOTNET_ROOT", "/opt/homebrew/opt/dotnet/libexec");
        try
        {
            ProcessStartInfo startInfo = server.CreateCscStartInfo(toolchain, "/L/x.rsp", "/L/x.cs");

            Assert.Equal(dotnet, startInfo.FileName);
            Assert.False(startInfo.UseShellExecute);
            Assert.Equal(dotnet, startInfo.Environment["DOTNET_HOST_PATH"]);
            Assert.False(startInfo.Environment.ContainsKey("DOTNET_ROOT"));
            Assert.Equal("0", startInfo.Environment["DOTNET_MULTILEVEL_LOOKUP"]);
            Assert.Contains("/shared:" + Pipe, startInfo.Arguments);
            Assert.Contains("@/L/x.rsp", startInfo.Arguments);
        }
        finally
        {
            Environment.SetEnvironmentVariable("DOTNET_ROOT", saved);
        }
    }

    [Fact]
    public void ServerPid_IsTheRealProcess_NotZero_OnceTheServerIsVisible()
    {
        using var temp = new TempDir();
        var table = new FakeProcessTable();
        int listed = 0;
        table.Listing = () =>
        {
            int n = Interlocked.Increment(ref listed);
            if (n < 4)
            {
                return Array.Empty<KeyValuePair<int, string>>();
            }

            return new[] { Proc(432, "/U/dotnet exec /U/VBCSCompiler.dll -pipename:" + Pipe) };
        };
        var server = new CscSharedServer(Options(temp.Path), table);
        Invoke(server, "EnsureRecord");
        Assert.Equal(0, CscSharedServerRecord.TryRead(server.RecordPath)!.ServerPid);

        for (int i = 0; i < 4; i++)
        {
            Invoke(server, "Succeed", new CscSharedRun(), Stopwatch.StartNew());
            Assert.True(WaitFor(() => !Discovering(server), 2000), "discovery did not finish");
        }

        Assert.Equal(432, server.Snapshot().ServerPid);
        Assert.Equal(432, CscSharedServerRecord.TryRead(server.RecordPath)!.ServerPid);
        Assert.NotEqual(0, CscSharedServerRecord.TryRead(server.RecordPath)!.ServerPid);
        Assert.Equal(432, CscSharedStatus.TryRead(server.StatusPath)!.ServerPid);
        Assert.NotEqual(0, CscSharedStatus.TryRead(server.StatusPath)!.ServerPid);
    }

    [Fact]
    public void Succeed_DoesNotWaitForTheServerPidLookup()
    {
        using var temp = new TempDir();
        var table = new FakeProcessTable();
        using var started = new ManualResetEventSlim(false);
        using var release = new ManualResetEventSlim(false);
        table.Listing = () =>
        {
            started.Set();
            Assert.True(release.Wait(TimeSpan.FromSeconds(5)), "lookup was not released");
            return new[] { Proc(432, "/U/dotnet exec /U/VBCSCompiler.dll -pipename:" + Pipe) };
        };
        var server = new CscSharedServer(Options(temp.Path), table);
        Invoke(server, "EnsureRecord");
        var watch = Stopwatch.StartNew();
        try
        {
            Invoke(server, "Succeed", new CscSharedRun(), Stopwatch.StartNew());
            watch.Stop();

            Assert.True(watch.Elapsed < TimeSpan.FromMilliseconds(750), "compile waited " + watch.ElapsedMilliseconds + " ms for the pid lookup");
            Assert.True(started.Wait(TimeSpan.FromSeconds(2)), "pid lookup did not start");
            Assert.Equal(0, server.Snapshot().ServerPid);
        }
        finally
        {
            release.Set();
        }

        Assert.True(WaitFor(() => !Discovering(server) && server.Snapshot().ServerPid == 432, 2000), "lookup did not record the server");
        Assert.Equal(432, CscSharedServerRecord.TryRead(server.RecordPath)!.ServerPid);
        Assert.Equal(432, CscSharedStatus.TryRead(server.StatusPath)!.ServerPid);
    }

    [Fact]
    public void ServerPid_UpdatesWhenTheRecordedServerDies()
    {
        using var temp = new TempDir();
        var table = new FakeProcessTable();
        int generation = 0;
        using var started = new ManualResetEventSlim(false);
        using var release = new ManualResetEventSlim(false);
        table.Alive.Add(432);
        table.Listing = () =>
        {
            if (Volatile.Read(ref generation) == 0)
            {
                return new[] { Proc(432, "/U/dotnet exec /U/VBCSCompiler.dll -pipename:" + Pipe) };
            }

            started.Set();
            Assert.True(release.Wait(TimeSpan.FromSeconds(5)), "replacement lookup was not released");
            return new[] { Proc(2244, "/U/dotnet exec /U/VBCSCompiler.dll -pipename:" + Pipe) };
        };
        var server = new CscSharedServer(Options(temp.Path), table);
        Invoke(server, "EnsureRecord");
        Invoke(server, "Succeed", new CscSharedRun(), Stopwatch.StartNew());
        Assert.True(WaitFor(() => !Discovering(server) && server.Snapshot().ServerPid == 432, 2000), "first server was not recorded");

        int lists = table.ListCalls;
        Invoke(server, "Succeed", new CscSharedRun(), Stopwatch.StartNew());
        Assert.False(Discovering(server));
        Assert.Equal(lists, table.ListCalls);
        Assert.Equal(432, server.Snapshot().ServerPid);

        Volatile.Write(ref generation, 1);
        table.Alive.Remove(432);
        var watch = Stopwatch.StartNew();
        try
        {
            Invoke(server, "Succeed", new CscSharedRun(), Stopwatch.StartNew());
            watch.Stop();

            Assert.True(watch.Elapsed < TimeSpan.FromMilliseconds(750), "compile waited " + watch.ElapsedMilliseconds + " ms for the replacement lookup");
            Assert.True(started.Wait(TimeSpan.FromSeconds(2)), "replacement lookup did not start");
            Assert.NotEqual(2244, server.Snapshot().ServerPid);
        }
        finally
        {
            release.Set();
        }

        Assert.True(WaitFor(() => !Discovering(server) && server.Snapshot().ServerPid == 2244, 2000), "replacement server was not recorded");
        Assert.Equal(2244, CscSharedServerRecord.TryRead(server.RecordPath)!.ServerPid);
        Assert.Equal(2244, CscSharedStatus.TryRead(server.StatusPath)!.ServerPid);
        Assert.NotEqual(432, CscSharedStatus.TryRead(server.StatusPath)!.ServerPid);
    }

    [Fact]
    public void ToolchainFromReport_NeedsTheCompilerServer()
    {
        using var temp = new TempDir();
        string roslyn = Path.Combine(temp.Path, "DotNetSdkRoslyn");
        Directory.CreateDirectory(roslyn);
        var report = new BundledToolchainReport(
            temp.Path,
            temp.Path,
            "A",
            temp.Path,
            new BundledToolComponent(Path.Combine(temp.Path, "NetCoreRuntime", "dotnet"), Path.Combine(temp.Path, "NetCoreRuntime"), "6.0.21", null),
            new BundledToolComponent(Path.Combine(roslyn, "csc.dll"), roslyn, "4.3.1-3.22526.13", null),
            null,
            null);

        Assert.Null(CscSharedToolchain.FromReport(report, out string missing));
        Assert.Contains("VBCSCompiler.dll", missing);

        File.WriteAllText(Path.Combine(roslyn, "VBCSCompiler.dll"), string.Empty);
        CscSharedToolchain? toolchain = CscSharedToolchain.FromReport(report, out _);

        Assert.NotNull(toolchain);
        Assert.Equal(Path.Combine(roslyn, "VBCSCompiler.dll"), toolchain!.ServerPath);
        Assert.Equal("4.3.1-3.22526.13", toolchain.RoslynVersion);
    }

    internal static CscSharedServerOptions Options(string directory)
    {
        return new CscSharedServerOptions
        {
            ProjectHash = ProjectHash,
            UnityVersion = "6000.3.10f1",
            EditorPid = EditorPid,
            Directory = directory,
            ConfiguredCompiler = "csc-shared",
        };
    }

    private static bool Discovering(CscSharedServer server)
    {
        return (bool)typeof(CscSharedServer).GetField("_discovering", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(server)!;
    }

    private static void Invoke(CscSharedServer server, string name, params object[] args)
    {
        MethodInfo method = typeof(CscSharedServer).GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)!;
        method.Invoke(server, args.Length == 0 ? null : args);
    }

    private static bool WaitFor(Func<bool> condition, int timeoutMs)
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

    private static KeyValuePair<int, string> Proc(int pid, string commandLine)
    {
        return new KeyValuePair<int, string>(pid, commandLine);
    }

    internal sealed class FakeProcessTable : ICscSharedProcessTable
    {
        private readonly object _lock = new object();
        private int _listCalls;

        public List<KeyValuePair<int, string>> Processes { get; } = new List<KeyValuePair<int, string>>();

        public HashSet<int> Alive { get; } = new HashSet<int>();

        public List<int> Killed { get; } = new List<int>();

        public int ListCalls
        {
            get { return Volatile.Read(ref _listCalls); }
        }

        /// <summary>When set, <see cref="ListServers"/> returns this instead of <see cref="Processes"/>.</summary>
        public Func<IReadOnlyList<KeyValuePair<int, string>>>? Listing { get; set; }

        public bool ListingIsSlow { get; set; }

        /// <summary>Pids <see cref="IsServerOnPipe"/> confirms (with any start time).</summary>
        public HashSet<int> OnPipe { get; } = new HashSet<int>();

        public Dictionary<int, long> StartTicks { get; } = new Dictionary<int, long>();

        /// <summary>False: a killed pid stays alive (a kill that does not take).</summary>
        public bool KillRemovesAlive { get; set; } = true;

        public List<ProcessStartInfo> Detached { get; } = new List<ProcessStartInfo>();

        public int OnPipeChecks { get; private set; }

        public IReadOnlyList<KeyValuePair<int, string>> ListServers()
        {
            Interlocked.Increment(ref _listCalls);
            return Listing != null ? Listing() : Processes;
        }

        public bool IsAlive(int pid)
        {
            lock (_lock)
            {
                return Alive.Contains(pid);
            }
        }

        public long? ReadStartTicks(int pid)
        {
            lock (_lock)
            {
                return StartTicks.TryGetValue(pid, out long ticks) ? ticks : (long?)null;
            }
        }

        public bool IsServerOnPipe(int pid, string pipe, long? startTicks)
        {
            lock (_lock)
            {
                OnPipeChecks++;
                return OnPipe.Contains(pid) && Alive.Contains(pid);
            }
        }

        public void Kill(int pid)
        {
            lock (_lock)
            {
                Killed.Add(pid);
                if (KillRemovesAlive)
                {
                    Alive.Remove(pid);
                }
            }
        }

        public void StartDetached(ProcessStartInfo startInfo)
        {
            lock (_lock)
            {
                Detached.Add(startInfo);
            }
        }
    }

    private sealed class StubBackend : ICompileBackend
    {
        private readonly CompileResult _result;

        public StubBackend(string id, CompileResult result)
        {
            Id = id;
            _result = result;
        }

        public string Id { get; }

        public CompileResult TryCompile(CompileRequest request)
        {
            return _result;
        }
    }
}

public sealed class CscSharedProcessListingTests
{
    private const string Pipe = "ucb-d6887d89-abc123";

    private const string ServerLine =
        "432 \"C:\\Program Files\\Unity\\Hub\\Editor\\2021.3.45f2\\Editor\\Data\\NetCoreRuntime\\dotnet.exe\" exec "
        + "\"C:\\Program Files\\Unity\\Hub\\Editor\\2021.3.45f2\\Editor\\Data\\DotNetSdkRoslyn\\VBCSCompiler.dll\" "
        + "\"-pipename:" + Pipe + "\"";

    [Fact]
    public void WrappedWindowsListing_KeepsTheServerPid()
    {
        var wrapped = new StringBuilder("0 \n");
        for (int index = 0; index < ServerLine.Length; index += 80)
        {
            int take = Math.Min(80, ServerLine.Length - index);
            wrapped.Append(ServerLine, index, take);
            wrapped.Append('\n');
        }

        wrapped.Append("433 \"C:\\U\\dotnet.exe\" exec \"C:\\U\\VBCSCompiler.dll\" -shutdown -pipename:").Append(Pipe).Append('\n');

        List<KeyValuePair<int, string>> found = RoslynHostProcessInspector.ParseCommandLineListing(wrapped.ToString(), "VBCSCompiler");

        Assert.Contains(found, pair => pair.Key == 432);
        Assert.DoesNotContain(found, pair => pair.Key == 0);
        string server = found.Single(pair => pair.Key == 432).Value;
        Assert.True(CscSharedPipe.IsServerCommandLine(server, Pipe));
        Assert.False(CscSharedPipe.IsServerCommandLine(found.Single(pair => pair.Key == 433).Value, Pipe));
    }

    [Fact]
    public void Utf16ListingReadAsSingleByte_KeepsTheServerPid()
    {
        string listing = "0 \n" + ServerLine + "\n";
        string misread = Encoding.GetEncoding("ISO-8859-1").GetString(Encoding.Unicode.GetBytes(listing));

        List<KeyValuePair<int, string>> found = RoslynHostProcessInspector.ParseCommandLineListing(misread, "VBCSCompiler");

        Assert.Equal(432, Assert.Single(found).Key);
        Assert.NotEqual(0, found[0].Key);
        Assert.True(CscSharedPipe.IsServerCommandLine(found[0].Value, Pipe));
    }

    [Fact]
    public void PsListing_DoesNotJoinSeparateProcesses()
    {
        string listing = "10 /bin/sleep 5\n432 /U/dotnet exec /U/VBCSCompiler.dll -pipename:" + Pipe + "\n";

        List<KeyValuePair<int, string>> found = RoslynHostProcessInspector.ParseCommandLineListing(listing, "VBCSCompiler");

        Assert.Equal(432, Assert.Single(found).Key);
        Assert.NotEqual(0, found[0].Key);
    }
}

/// <summary>
/// End to end against the .NET SDK's own csc and compiler server (the editor's bundled pair is
/// covered by the EditMode and live checks): a real <c>/shared</c> compile, the user-error shape,
/// C# 9 with the polyfill, and that shutdown leaves no server on the pipe. Skipped on Windows and
/// when the running dotnet has no SDK.
/// </summary>
public sealed class CscSharedEndToEndTests
{
    [Fact]
    public void RealCscShared_CompilesReportsErrorsAndShutsTheServerDown()
    {
        CscSharedToolchain? toolchain = FindSdkToolchain();
        if (toolchain == null || RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            return;
        }

        using var temp = new TempDir();
        string projectHash = Guid.NewGuid().ToString("N").Substring(0, 12);
        CscSharedServerOptions options = CscSharedServerTests.Options(temp.Path);
        options.ProjectHash = projectHash;
        options.EditorPid = Environment.ProcessId;
        options.Toolchain = () => toolchain;
        options.CompileTimeoutMs = 60000;
        var server = new CscSharedServer(options);
        var backend = new CscSharedBackend(server);
        List<string> references = HostTestSupport.FrameworkReferences();
        try
        {
            CompileResult ok = backend.TryCompile(Request(
                "public static class UcbE2E { public static int Run() { int a = 40; return a + 2; } }",
                references));
            Assert.True(ok.Succeeded, ok.FormatFailureMessage());
            Assert.Equal(42, ok.Assembly!.GetType("UcbE2E")!.GetMethod("Run")!.Invoke(null, null));
            Assert.True(File.Exists(server.RecordPath));
            Assert.Empty(Directory.GetFiles(server.TempDirectory));

            CompileResult records = backend.TryCompile(Request(
                "public record UcbE2EPoint(int X, int Y);\n"
                + "public static class UcbE2ERecords { public static string Run() { var p = new UcbE2EPoint(1, 2) with { Y = 5 }; object o = p.Y; return o is int n and > 4 ? \"ok\" + n : \"no\"; } }",
                references));
            Assert.True(records.Succeeded, records.FormatFailureMessage());
            Assert.Equal("ok5", records.Assembly!.GetType("UcbE2ERecords")!.GetMethod("Run")!.Invoke(null, null));

            CompileResult error = backend.TryCompile(Request(
                "public static class UcbE2EBad { public static void Run() {\n#line 1 \"user-code\"\nint ok = 1;\nint x = \"nope\";\n#line default\n} }",
                references));
            Assert.False(error.Succeeded);
            Assert.False(error.IsInfrastructureFailure, error.FormatFailureMessage());
            Assert.Equal(new[] { "Line 2: CS0029: Cannot implicitly convert type 'string' to 'int'" }, error.Errors);

            Assert.True(SpinUntil(() => FindServers(server.PipeName).Count > 0, 10000), "no compiler server on " + server.PipeName);
            Assert.Equal("ready", server.Snapshot().State);
        }
        finally
        {
            server.Shutdown("test");
        }

        Assert.True(SpinUntil(() => FindServers(server.PipeName).Count == 0, 5000), "server still running on " + server.PipeName);
        Assert.False(File.Exists(server.RecordPath));
        Assert.Equal("stopped", server.Snapshot().State);
    }

    private static CompileRequest Request(string source, List<string> references)
    {
        return new CompileRequest(source, "9.0", Array.Empty<string>(), references);
    }

    private static List<int> FindServers(string pipe)
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

            Thread.Sleep(100);
        }

        return condition();
    }

    /// <summary>The running dotnet's root (…/shared/Microsoft.NETCore.App/&lt;ver&gt;/ is three levels below it) and its newest SDK's Roslyn.</summary>
    private static CscSharedToolchain? FindSdkToolchain()
    {
        try
        {
            string runtimeDirectory = Path.GetDirectoryName(typeof(object).Assembly.Location)!;
            string root = Path.GetFullPath(Path.Combine(runtimeDirectory, "..", "..", ".."));
            string muxer = Path.Combine(root, RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? "dotnet.exe" : "dotnet");
            string sdkRoot = Path.Combine(root, "sdk");
            if (!File.Exists(muxer) || !Directory.Exists(sdkRoot))
            {
                return null;
            }

            foreach (string sdk in Directory.GetDirectories(sdkRoot).OrderByDescending(d => d, StringComparer.Ordinal))
            {
                string bincore = Path.Combine(sdk, "Roslyn", "bincore");
                string csc = Path.Combine(bincore, "csc.dll");
                string server = Path.Combine(bincore, "VBCSCompiler.dll");
                if (File.Exists(csc) && File.Exists(server))
                {
                    return new CscSharedToolchain(muxer, csc, server, "sdk");
                }
            }
        }
        catch (Exception)
        {
            // No SDK: skip.
        }

        return null;
    }
}

internal sealed class TempDir : IDisposable
{
    public TempDir()
    {
        Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "ucb-csc-" + Guid.NewGuid().ToString("N").Substring(0, 8));
        Directory.CreateDirectory(Path);
    }

    public string Path { get; }

    public void Dispose()
    {
        try
        {
            Directory.Delete(Path, recursive: true);
        }
        catch (Exception)
        {
            // Best effort.
        }
    }
}
