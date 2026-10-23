using System.Diagnostics;
using System.Text;
using System.Text.Json;
using UnityCli.Protocol;

namespace UnityCli.RoslynHost.Tests;

public sealed class RoslynHostBuilderTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "ucb-roslyn-builder-tests-" + Guid.NewGuid().ToString("N"));

    public RoslynHostBuilderTests()
    {
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, true);
        }
        catch (IOException)
        {
        }
    }

    [Fact]
    public void HostSourceRelativePaths_AllExistInThePackage()
    {
        List<string> paths = RoslynHostBuilder.GetHostSourcePaths(HostTestSupport.PackageRoot());

        Assert.All(paths, path => Assert.True(File.Exists(path), path));
        Assert.Contains(paths, path => path.EndsWith("UcbRoslynHost.cs", StringComparison.Ordinal));
    }

    [Fact]
    public void CacheKey_IsUnityVersionPlusTwelveHex_AndStableAcrossSourceOrder()
    {
        var a = new KeyValuePair<string, byte[]>("A.cs", Encoding.UTF8.GetBytes("class A {}"));
        var b = new KeyValuePair<string, byte[]>("B.cs", Encoding.UTF8.GetBytes("class B {}"));

        string key1 = RoslynHostBuilder.ComputeCacheKey("2021.3.45f2", new[] { a, b }, "6.0.21", "4.3.1-3.22526.13");
        string key2 = RoslynHostBuilder.ComputeCacheKey("2021.3.45f2", new[] { b, a }, "6.0.21", "4.3.1-3.22526.13");

        Assert.Equal(key1, key2);
        Assert.Matches("^2021\\.3\\.45f2-[0-9a-f]{12}$", key1);
    }

    [Fact]
    public void CacheKey_ChangesWithSourcesRuntimeRoslynAndUnityVersion()
    {
        var source = new KeyValuePair<string, byte[]>("A.cs", Encoding.UTF8.GetBytes("class A {}"));
        string baseline = RoslynHostBuilder.ComputeCacheKey("6000.3.10f1", new[] { source }, "6.0.21", "4.3.1");
        string Hash(string key) => key.Substring(key.LastIndexOf('-') + 1);

        var keys = new[]
        {
            RoslynHostBuilder.ComputeCacheKey("6000.3.10f1", new[] { new KeyValuePair<string, byte[]>("A.cs", Encoding.UTF8.GetBytes("class A { }")) }, "6.0.21", "4.3.1"),
            RoslynHostBuilder.ComputeCacheKey("6000.3.10f1", new[] { new KeyValuePair<string, byte[]>("Z.cs", source.Value) }, "6.0.21", "4.3.1"),
            RoslynHostBuilder.ComputeCacheKey("6000.3.10f1", new[] { source }, "6.0.22", "4.3.1"),
            RoslynHostBuilder.ComputeCacheKey("6000.3.10f1", new[] { source }, "6.0.21", "4.8.0"),
        };

        Assert.All(keys, key => Assert.NotEqual(Hash(baseline), Hash(key)));
        Assert.Equal(Hash(baseline), Hash(RoslynHostBuilder.ComputeCacheKey("2021.3.45f2", new[] { source }, "6.0.21", "4.3.1")));
        Assert.StartsWith("6000.3.10f1-", baseline);
    }

    [Fact]
    public void CacheKey_SanitizesTheUnityVersionForUseAsAFolderName()
    {
        string key = RoslynHostBuilder.ComputeCacheKey("2022.1 beta/1", new[] { new KeyValuePair<string, byte[]>("A.cs", new byte[1]) }, "6", "4");

        Assert.StartsWith("2022.1_beta_1-", key);
    }

    [Fact]
    public void RuntimeConfig_PinsTheExactRuntimeAndSetsTheGcAndPgoKnobs()
    {
        using JsonDocument doc = JsonDocument.Parse(RoslynHostBuilder.BuildRuntimeConfigJson("6.0.21"));
        JsonElement options = doc.RootElement.GetProperty("runtimeOptions");

        Assert.Equal("net6.0", options.GetProperty("tfm").GetString());
        Assert.Equal("Microsoft.NETCore.App", options.GetProperty("framework").GetProperty("name").GetString());
        Assert.Equal("6.0.21", options.GetProperty("framework").GetProperty("version").GetString());
        Assert.Equal("Disable", options.GetProperty("rollForward").GetString());
        JsonElement config = options.GetProperty("configProperties");
        Assert.False(config.GetProperty("System.GC.Server").GetBoolean());
        Assert.False(config.GetProperty("System.GC.Concurrent").GetBoolean());
        Assert.False(config.GetProperty("System.Runtime.TieredPGO").GetBoolean());

        // Tiered compilation must stay at the runtime default (on).
        Assert.False(config.TryGetProperty("System.Runtime.TieredCompilation", out _));
        Assert.Equal(3, config.EnumerateObject().Count());
        Assert.Equal("net8.0", JsonDocument.Parse(RoslynHostBuilder.BuildRuntimeConfigJson("8.0.1")).RootElement
            .GetProperty("runtimeOptions").GetProperty("tfm").GetString());
    }

    [Fact]
    public void ResponseFile_HasTheCompilerOptionsQuotedReferencesAndSources()
    {
        string fx = Path.Combine(_root, "fx dir");
        List<string> lines = RoslynHostBuilder.BuildResponseFileLines(
            new[] { Path.Combine(fx, "System.Runtime.dll"), Path.Combine(fx, "System.Private.CoreLib.dll") },
            Path.Combine(_root, "Roslyn"),
            Path.Combine(_root, "out", "UcbRoslynHost.dll"),
            new[] { Path.Combine(_root, "src", "UcbRoslynHost.cs"), Path.Combine(_root, "src", "RoslynHostJson.cs") });

        foreach (string option in new[] { "/noconfig", "/nostdlib+", "/target:exe", "/langversion:9", "/optimize+", "/nologo" })
        {
            Assert.Contains(option, lines);
        }

        Assert.Contains("/out:\"" + Path.Combine(_root, "out", "UcbRoslynHost.dll") + "\"", lines);
        List<string> references = lines.Where(line => line.StartsWith("/r:", StringComparison.Ordinal)).ToList();
        Assert.Equal(
            new[]
            {
                "/r:\"" + Path.Combine(fx, "System.Private.CoreLib.dll") + "\"",
                "/r:\"" + Path.Combine(fx, "System.Runtime.dll") + "\"",
                "/r:\"" + Path.Combine(_root, "Roslyn", "Microsoft.CodeAnalysis.dll") + "\"",
                "/r:\"" + Path.Combine(_root, "Roslyn", "Microsoft.CodeAnalysis.CSharp.dll") + "\"",
            },
            references);
        Assert.Equal("\"" + Path.Combine(_root, "src", "RoslynHostJson.cs") + "\"", lines[^1]);
        Assert.DoesNotContain(lines, line => line.StartsWith("/shared", StringComparison.Ordinal));
    }

    [Fact]
    public void ManagedAssemblyCheck_AcceptsManagedPeFiles_AndRejectsNativeOrJunk()
    {
        string managed = typeof(object).Assembly.Location;
        string junk = Path.Combine(_root, "junk.dll");
        File.WriteAllText(junk, "not a PE file at all, just some text that is long enough to have a header area ......");
        string native = Path.Combine(_root, "native.dll");
        File.WriteAllBytes(native, MinimalPe(withCliHeader: false));
        string fakeManaged = Path.Combine(_root, "fake-managed.dll");
        File.WriteAllBytes(fakeManaged, MinimalPe(withCliHeader: true));

        Assert.True(RoslynHostBuilder.IsManagedAssembly(managed));
        Assert.False(RoslynHostBuilder.IsManagedAssembly(junk));
        Assert.False(RoslynHostBuilder.IsManagedAssembly(native));
        Assert.True(RoslynHostBuilder.IsManagedAssembly(fakeManaged));
        Assert.False(RoslynHostBuilder.IsManagedAssembly(Path.Combine(_root, "missing.dll")));

        string fx = Path.Combine(_root, "fx");
        Directory.CreateDirectory(fx);
        File.Copy(managed, Path.Combine(fx, "System.Private.CoreLib.dll"));
        File.Copy(native, Path.Combine(fx, "coreclr.dll"));
        File.WriteAllText(Path.Combine(fx, "notes.txt"), "x");
        Assert.Equal(new[] { Path.Combine(fx, "System.Private.CoreLib.dll") }, RoslynHostBuilder.ListFrameworkAssemblies(fx));
    }

    [Fact]
    public void EnsureBuilt_RunsBundledCscOnce_ThenHitsTheCache()
    {
        FakeToolchain toolchain = CreateToolchain();
        string cacheRoot = Path.Combine(_root, "cache");
        var calls = new List<ProcessStartInfo>();
        Func<ProcessStartInfo, TimeSpan, BundledDotnetRunResult> runner = (startInfo, _) =>
        {
            calls.Add(startInfo);
            File.WriteAllBytes(Path.Combine(startInfo.WorkingDirectory, RoslynHostBuilder.HostAssemblyFileName), new byte[] { 0x4D, 0x5A });
            return new BundledDotnetRunResult(0, "ok", string.Empty);
        };

        RoslynHostBuildResult first = RoslynHostBuilder.EnsureBuilt(Request(toolchain, cacheRoot), runner);
        RoslynHostBuildResult second = RoslynHostBuilder.EnsureBuilt(Request(toolchain, cacheRoot), runner);

        Assert.True(first.Success, first.Error);
        Assert.False(first.CacheHit);
        Assert.True(second.Success);
        Assert.True(second.CacheHit);
        Assert.Equal(0, second.BuildMs);
        Assert.Single(calls);
        Assert.Equal(first.OutputDirectory, second.OutputDirectory);
        Assert.Equal(Path.Combine(cacheRoot, first.CacheKey!), first.OutputDirectory);
        Assert.StartsWith("6000.3.10f1-", first.CacheKey);
        Assert.True(File.Exists(first.HostAssemblyPath));
        Assert.Equal(RoslynHostBuilder.BuildRuntimeConfigJson("6.0.21"), File.ReadAllText(first.RuntimeConfigPath!));
        Assert.True(File.Exists(Path.Combine(first.OutputDirectory!, RoslynHostBuilder.BuildMarkerFileName)));
        Assert.Empty(Directory.GetDirectories(cacheRoot, ".build-*"));

        // The compiler ran on the bundled muxer with the bundled csc and a response file, nothing else.
        ProcessStartInfo csc = calls[0];
        Assert.Equal(toolchain.Muxer, csc.FileName);
        Assert.False(csc.UseShellExecute);
        Assert.True(csc.CreateNoWindow);
        Assert.Equal(
            BundledDotnetProcess.JoinArguments(new[] { "exec", toolchain.Csc, "@" + Path.Combine(csc.WorkingDirectory, RoslynHostBuilder.ResponseFileName) }),
            csc.Arguments);
        Assert.Equal("0", csc.Environment["DOTNET_MULTILEVEL_LOOKUP"]);
    }

    [Fact]
    public void EnsureBuilt_ResponseFileListsEveryManagedFrameworkAssemblyAndTheHashedSourceCopies()
    {
        FakeToolchain toolchain = CreateToolchain();
        string? rsp = null;
        string? copiedSource = null;
        RoslynHostBuildResult result = RoslynHostBuilder.EnsureBuilt(Request(toolchain, Path.Combine(_root, "cache")), (startInfo, _) =>
        {
            rsp = File.ReadAllText(Path.Combine(startInfo.WorkingDirectory, RoslynHostBuilder.ResponseFileName));
            copiedSource = File.ReadAllText(Path.Combine(startInfo.WorkingDirectory, "src", "Host.cs"));
            File.WriteAllBytes(Path.Combine(startInfo.WorkingDirectory, RoslynHostBuilder.HostAssemblyFileName), new byte[1]);
            return new BundledDotnetRunResult(0, string.Empty, string.Empty);
        });

        Assert.True(result.Success, result.Error);
        Assert.Contains("/r:\"" + Path.Combine(toolchain.SharedFramework, "System.Private.CoreLib.dll") + "\"", rsp);
        Assert.Contains("/r:\"" + Path.Combine(toolchain.SharedFramework, "System.Runtime.dll") + "\"", rsp);
        Assert.DoesNotContain("coreclr.dll", rsp);
        Assert.Contains("/r:\"" + Path.Combine(toolchain.RoslynDirectory, "Microsoft.CodeAnalysis.CSharp.dll") + "\"", rsp);
        Assert.Equal("class Host {}", copiedSource);
        Assert.DoesNotContain("\r", rsp);
        Assert.False(File.ReadAllBytes(Path.Combine(result.OutputDirectory!, "csc.rsp")).Take(3).SequenceEqual(new byte[] { 0xEF, 0xBB, 0xBF }));
    }

    [Fact]
    public void EnsureBuilt_RebuildsWhenAHostSourceChanges()
    {
        FakeToolchain toolchain = CreateToolchain();
        string cacheRoot = Path.Combine(_root, "cache");
        int calls = 0;
        Func<ProcessStartInfo, TimeSpan, BundledDotnetRunResult> runner = (startInfo, _) =>
        {
            calls++;
            File.WriteAllBytes(Path.Combine(startInfo.WorkingDirectory, RoslynHostBuilder.HostAssemblyFileName), new byte[1]);
            return new BundledDotnetRunResult(0, string.Empty, string.Empty);
        };

        RoslynHostBuildResult first = RoslynHostBuilder.EnsureBuilt(Request(toolchain, cacheRoot), runner);
        File.WriteAllText(toolchain.Source, "class Host { int changed; }");
        RoslynHostBuildResult second = RoslynHostBuilder.EnsureBuilt(Request(toolchain, cacheRoot), runner);

        Assert.Equal(2, calls);
        Assert.NotEqual(first.CacheKey, second.CacheKey);
        Assert.True(Directory.Exists(first.OutputDirectory));
        Assert.True(Directory.Exists(second.OutputDirectory));
    }

    [Fact]
    public void EnsureBuilt_CompilerFailure_ReportsOutputAndLeavesNoFolders()
    {
        FakeToolchain toolchain = CreateToolchain();
        string cacheRoot = Path.Combine(_root, "cache");

        RoslynHostBuildResult result = RoslynHostBuilder.EnsureBuilt(Request(toolchain, cacheRoot), (_, _) =>
            new BundledDotnetRunResult(1, "Host.cs(1,1): error CS1001: Identifier expected", string.Empty));

        Assert.False(result.Success);
        Assert.Contains("exit code 1", result.Error);
        Assert.Contains("CS1001", result.CompilerOutput);
        Assert.Equal(1, result.CompilerExitCode);
        Assert.Empty(Directory.GetDirectories(cacheRoot));
    }

    [Fact]
    public void EnsureBuilt_Timeout_IsAFailure()
    {
        FakeToolchain toolchain = CreateToolchain();

        RoslynHostBuildResult result = RoslynHostBuilder.EnsureBuilt(Request(toolchain, Path.Combine(_root, "cache")), (_, _) =>
            new BundledDotnetRunResult(null, string.Empty, string.Empty));

        Assert.False(result.Success);
        Assert.Contains("did not finish", result.Error);
    }

    [Fact]
    public void EnsureBuilt_ReplacesAHalfWrittenOutputFolder()
    {
        FakeToolchain toolchain = CreateToolchain();
        string cacheRoot = Path.Combine(_root, "cache");
        RoslynHostBuildResult first = RoslynHostBuilder.EnsureBuilt(Request(toolchain, cacheRoot), WriteDll);
        File.Delete(Path.Combine(first.OutputDirectory!, RoslynHostBuilder.BuildMarkerFileName));

        RoslynHostBuildResult second = RoslynHostBuilder.EnsureBuilt(Request(toolchain, cacheRoot), WriteDll);

        Assert.True(second.Success, second.Error);
        Assert.False(second.CacheHit);
        Assert.True(RoslynHostBuilder.IsComplete(second.OutputDirectory!));
    }

    [Fact]
    public void EnsureBuilt_RefusesAnIncompleteToolchainOrRelativeCacheRoot()
    {
        FakeToolchain toolchain = CreateToolchain();
        var noRuntime = new BundledToolchainReport(null, null, "A", null, new BundledToolComponent(null, null, null, "dotnet muxer not found"), toolchain.Report.Roslyn, null, null);
        var noRoslyn = new BundledToolchainReport(null, null, "A", null, toolchain.Report.Runtime, new BundledToolComponent(null, null, null, "Roslyn not found"), null, null);
        Func<ProcessStartInfo, TimeSpan, BundledDotnetRunResult> mustNotRun = (_, _) => throw new InvalidOperationException("csc must not run");

        RoslynHostBuildResult a = RoslynHostBuilder.EnsureBuilt(new RoslynHostBuildRequest(noRuntime, new[] { toolchain.Source }, _root, "x"), mustNotRun);
        RoslynHostBuildResult b = RoslynHostBuilder.EnsureBuilt(new RoslynHostBuildRequest(noRoslyn, new[] { toolchain.Source }, _root, "x"), mustNotRun);
        RoslynHostBuildResult c = RoslynHostBuilder.EnsureBuilt(new RoslynHostBuildRequest(toolchain.Report, new[] { toolchain.Source }, "relative/cache", "x"), mustNotRun);
        RoslynHostBuildResult d = RoslynHostBuilder.EnsureBuilt(new RoslynHostBuildRequest(toolchain.Report, new[] { Path.Combine(_root, "missing.cs") }, _root, "x"), mustNotRun);

        Assert.Contains("dotnet muxer not found", a.Error);
        Assert.Contains("Roslyn not found", b.Error);
        Assert.Contains("absolute", c.Error);
        Assert.Contains("Cannot read host source", d.Error);
        Assert.All(new[] { a, b, c, d }, r => Assert.False(r.Success));
    }

    private static BundledDotnetRunResult WriteDll(ProcessStartInfo startInfo, TimeSpan timeout)
    {
        File.WriteAllBytes(Path.Combine(startInfo.WorkingDirectory, RoslynHostBuilder.HostAssemblyFileName), new byte[1]);
        return new BundledDotnetRunResult(0, string.Empty, string.Empty);
    }

    private static RoslynHostBuildRequest Request(FakeToolchain toolchain, string cacheRoot)
    {
        return new RoslynHostBuildRequest(toolchain.Report, new[] { toolchain.Source }, cacheRoot, "6000.3.10f1");
    }

    /// <summary>A fake editor install: a muxer file, a shared framework with one managed and one native DLL, and Roslyn files.</summary>
    private FakeToolchain CreateToolchain()
    {
        string runtime = Path.Combine(_root, "Editor Data", "NetCoreRuntime");
        string sharedFramework = Path.Combine(runtime, "shared", "Microsoft.NETCore.App", "6.0.21");
        string roslyn = Path.Combine(_root, "Editor Data", "DotNetSdkRoslyn");
        Directory.CreateDirectory(sharedFramework);
        Directory.CreateDirectory(roslyn);
        string muxer = Path.Combine(runtime, OperatingSystem.IsWindows() ? "dotnet.exe" : "dotnet");
        File.WriteAllText(muxer, "fake muxer");
        File.Copy(typeof(object).Assembly.Location, Path.Combine(sharedFramework, "System.Private.CoreLib.dll"));
        File.Copy(typeof(object).Assembly.Location, Path.Combine(sharedFramework, "System.Runtime.dll"));
        File.WriteAllBytes(Path.Combine(sharedFramework, "coreclr.dll"), MinimalPe(withCliHeader: false));
        string csc = Path.Combine(roslyn, "csc.dll");
        File.WriteAllText(csc, "fake csc");
        File.WriteAllText(Path.Combine(roslyn, "Microsoft.CodeAnalysis.dll"), "fake");
        File.WriteAllText(Path.Combine(roslyn, "Microsoft.CodeAnalysis.CSharp.dll"), "fake");
        string source = Path.Combine(_root, "Host.cs");
        File.WriteAllText(source, "class Host {}");
        var report = new BundledToolchainReport(
            _root,
            Path.Combine(_root, "Editor Data"),
            "A",
            Path.Combine(_root, "Editor Data"),
            new BundledToolComponent(muxer, runtime, "6.0.21", null),
            new BundledToolComponent(csc, roslyn, "4.3.1-3.22526.13", null),
            null,
            null);
        return new FakeToolchain(report, muxer, csc, roslyn, sharedFramework, source);
    }

    /// <summary>PE32+ headers only; data directory 14 (CLI header) set or empty.</summary>
    private static byte[] MinimalPe(bool withCliHeader)
    {
        var bytes = new byte[512];
        bytes[0] = (byte)'M';
        bytes[1] = (byte)'Z';
        BitConverter.GetBytes(0x80).CopyTo(bytes, 0x3C);
        BitConverter.GetBytes(0x00004550u).CopyTo(bytes, 0x80);
        BitConverter.GetBytes((ushort)240).CopyTo(bytes, 0x80 + 4 + 16);
        int optional = 0x80 + 24;
        BitConverter.GetBytes((ushort)0x20B).CopyTo(bytes, optional);
        BitConverter.GetBytes(16u).CopyTo(bytes, optional + 108);
        if (withCliHeader)
        {
            BitConverter.GetBytes(0x2000u).CopyTo(bytes, optional + 112 + 14 * 8);
            BitConverter.GetBytes(72u).CopyTo(bytes, optional + 112 + 14 * 8 + 4);
        }

        return bytes;
    }

    private sealed record FakeToolchain(
        BundledToolchainReport Report,
        string Muxer,
        string Csc,
        string RoslynDirectory,
        string SharedFramework,
        string Source);
}
