using System.Reflection;
using System.Runtime.Loader;
using UnityCli.Protocol;
using UnityCliBridge.RoslynHost;

namespace UnityCli.RoslynHost.Tests;

public sealed class RoslynCompilerServiceTests : IDisposable
{
    private const string IsExternalInitPolyfill =
        "namespace System.Runtime.CompilerServices { internal static class IsExternalInit {} }\n";

    private readonly string _tempDirectory = Path.Combine(Path.GetTempPath(), "ucb-roslyn-host-tests-" + Guid.NewGuid().ToString("N"));

    public RoslynCompilerServiceTests()
    {
        Directory.CreateDirectory(_tempDirectory);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_tempDirectory, true);
        }
        catch (IOException)
        {
        }
    }

    [Fact]
    public void SetReferences_ReadsEachFileOnce_AndReusesUnchangedFiles()
    {
        var service = new RoslynCompilerService();
        List<string> paths = HostTestSupport.FrameworkReferences();

        RoslynHostRefsSet first = service.SetReferences("h1", paths);
        RoslynHostRefsSet second = service.SetReferences("h2", paths);

        Assert.Equal((paths.Count, paths.Count, 0), (first.Count, first.Loaded, first.Reused));
        Assert.Equal((paths.Count, 0, paths.Count), (second.Count, second.Loaded, second.Reused));
        Assert.Equal(paths.Count, service.FilesRead);
        Assert.Equal("h2", service.RefsHash);
    }

    [Fact]
    public void SetReferences_RereadsAFileOnlyWhenItsMtimeOrSizeChanges()
    {
        var service = new RoslynCompilerService();
        string a = CopyReference("A.dll");
        string b = CopyReference("B.dll");
        service.SetReferences("h", new[] { a, b });

        File.SetLastWriteTimeUtc(a, File.GetLastWriteTimeUtc(a).AddMinutes(-5));
        RoslynHostRefsSet afterTouch = service.SetReferences("h", new[] { a, b });
        Assert.Equal((1, 1), (afterTouch.Loaded, afterTouch.Reused));

        DateTime keepMtime = File.GetLastWriteTimeUtc(b);
        using (var stream = new FileStream(b, FileMode.Append))
        {
            stream.WriteByte(0);
        }

        File.SetLastWriteTimeUtc(b, keepMtime);
        RoslynHostRefsSet afterGrow = service.SetReferences("h", new[] { a, b });
        Assert.Equal((1, 1), (afterGrow.Loaded, afterGrow.Reused));

        RoslynHostRefsSet unchanged = service.SetReferences("h", new[] { a, b });
        Assert.Equal((0, 2), (unchanged.Loaded, unchanged.Reused));
        Assert.Equal(4, service.FilesRead);
    }

    [Fact]
    public void SetReferences_DropsFilesNoLongerInTheSet_AndSkipsMissingOnes()
    {
        var service = new RoslynCompilerService();
        string a = CopyReference("A.dll");
        string b = CopyReference("B.dll");
        string missing = Path.Combine(_tempDirectory, "Missing.dll");
        service.SetReferences("h", new[] { a, b });

        RoslynHostRefsSet narrowed = service.SetReferences("h2", new[] { a, missing, a });
        Assert.Equal(1, narrowed.Count);
        Assert.Equal(new[] { missing }, narrowed.Skipped);

        // b fell out of the cache with the previous set, so it is read again.
        RoslynHostRefsSet widened = service.SetReferences("h3", new[] { a, b });
        Assert.Equal((1, 1), (widened.Loaded, widened.Reused));
    }

    [Fact]
    public void Compile_Success_ReturnsALoadableAssembly()
    {
        RoslynCompilerService service = ServiceWithFramework();

        RoslynHostCompileResult result = service.Compile(Request(
            "public static class Snippet { public static int Run() { return new System.Collections.Generic.List<int> { 1, 2, 3 }.Count; } }"));

        Assert.True(result.Ok, string.Join("\n", result.Diagnostics.Select(d => d.Message)));
        Assert.Null(result.Error);
        Assert.Empty(result.Diagnostics);
        Assert.True(result.CompileMs > 0);
        Assert.Equal(3, Invoke(result.Assembly!, "Snippet", "Run"));
    }

    [Fact]
    public void Compile_Error_ReportsIdSeverityPositionAndMessage_WithoutAssembly()
    {
        RoslynCompilerService service = ServiceWithFramework();

        RoslynHostCompileResult result = service.Compile(Request(
            "public static class Snippet\n{\n    public static int Run()\n    {\n        return missingName;\n    }\n}\n",
            request => request.FilePath = "wrapped.cs"));

        Assert.False(result.Ok);
        Assert.Null(result.Error);
        Assert.Null(result.Assembly);
        RoslynHostDiagnostic diagnostic = Assert.Single(result.Diagnostics);
        Assert.Equal("CS0103", diagnostic.Id);
        Assert.Equal("error", diagnostic.Severity);
        Assert.True(diagnostic.IsError);
        Assert.Equal((5, 16), (diagnostic.Line, diagnostic.Column));
        Assert.Equal("wrapped.cs", diagnostic.File);
        Assert.Equal("The name 'missingName' does not exist in the current context", diagnostic.Message);
    }

    [Fact]
    public void Compile_SyntaxErrorAndWarning_AreBothReported()
    {
        RoslynCompilerService service = ServiceWithFramework();

        RoslynHostCompileResult result = service.Compile(Request(
            "public static class Snippet { public static void Run() { int unused; int x = 1 } }"));

        Assert.False(result.Ok);
        Assert.Contains(result.Diagnostics, d => d.Id == "CS1002" && d.Severity == "error");
        Assert.Contains(result.Diagnostics, d => d.Id == "CS0168" && d.Severity == "warning");
    }

    [Fact]
    public void Compile_DiagnosticsFollowLineDirectives()
    {
        RoslynCompilerService service = ServiceWithFramework();
        string source =
            "public static class Wrapper\n"
            + "{\n"
            + "    public static void Run()\n"
            + "    {\n"
            + "#line 1 \"user-code\"\n"
            + "        int fine = 1;\n"
            + "        undefinedCall(fine);\n"
            + "#line default\n"
            + "        alsoMissing();\n"
            + "    }\n"
            + "}\n";

        RoslynHostCompileResult result = service.Compile(Request(source, request => request.FilePath = "wrapped.cs"));

        Assert.False(result.Ok);
        RoslynHostDiagnostic user = Assert.Single(result.Diagnostics, d => d.File == "user-code");
        Assert.Equal(("CS0103", 2, 9), (user.Id, user.Line, user.Column));
        RoslynHostDiagnostic wrapper = Assert.Single(result.Diagnostics, d => d.File == "wrapped.cs");
        Assert.Equal(9, wrapper.Line);
    }

    [Fact]
    public void Compile_CSharp9RecordInitAndWith_WorkWithAnIsExternalInitPolyfill()
    {
        RoslynCompilerService service = ServiceWithFramework();
        string source =
            IsExternalInitPolyfill
            + "public record Point(int X, int Y);\n"
            + "public class Box { public int Size { get; init; } }\n"
            + "public static class Snippet\n"
            + "{\n"
            + "    public static int Run()\n"
            + "    {\n"
            + "        var p = new Point(1, 2);\n"
            + "        var q = p with { Y = 40 };\n"
            + "        Box box = new() { Size = 3 };\n"
            + "        object o = q;\n"
            + "        int bonus = o is Point { X: > 0 } and not null ? 100 : 0;\n"
            + "        return q.X + q.Y + box.Size + bonus;\n"
            + "    }\n"
            + "}\n";

        RoslynHostCompileResult result = service.Compile(Request(source, request => request.NoWarn.Add("0436")));

        Assert.True(result.Ok, string.Join("\n", result.Diagnostics.Select(d => d.Id + " " + d.Message)));
        Assert.DoesNotContain(result.Diagnostics, d => d.Id == "CS0436");
        Assert.Equal(144, Invoke(result.Assembly!, "Snippet", "Run"));
    }

    [Fact]
    public void Compile_NoWarn_SuppressesTheListedWarning()
    {
        RoslynCompilerService service = ServiceWithFramework();
        string source = "public static class Snippet { public static void Run() { int unused; int alsoUnused; } }";

        RoslynHostCompileResult loud = service.Compile(Request(source));
        RoslynHostCompileResult quiet = service.Compile(Request(source, request => request.NoWarn.Add("168")));

        Assert.True(loud.Ok);
        Assert.Equal(2, loud.Diagnostics.Count(d => d.Id == "CS0168" && d.Severity == "warning"));
        Assert.True(quiet.Ok);
        Assert.Empty(quiet.Diagnostics);
    }

    [Fact]
    public void Compile_DefaultsToCSharp9_SoCSharp10SyntaxIsAUserError()
    {
        RoslynCompilerService service = ServiceWithFramework();
        string source = "public static class Snippet { public static int Run() { var p = (a: 1, b: 2); var q = p with { a = 5 }; return q.a; } }";

        RoslynHostCompileResult csharp9 = service.Compile(Request(source, request => request.LanguageVersion = ""));
        RoslynHostCompileResult csharp10 = service.Compile(Request(source, request => request.LanguageVersion = "10"));

        Assert.False(csharp9.Ok);
        Assert.Null(csharp9.Error);
        Assert.Contains(csharp9.Diagnostics, d => d.Id == "CS8773");
        Assert.True(csharp10.Ok);
    }

    [Fact]
    public void Compile_DefinesAndUnsafe_ComeFromTheRequest()
    {
        RoslynCompilerService service = ServiceWithFramework();
        string source =
            "public static class Snippet { public static unsafe int Run() {\n"
            + "#if UCB_TEST_DEFINE\n"
            + "    int x = 7; int* p = &x; return *p;\n"
            + "#else\n"
            + "    return -1;\n"
            + "#endif\n"
            + "} }\n";

        RoslynHostCompileResult noUnsafe = service.Compile(Request(source));
        RoslynHostCompileResult withUnsafe = service.Compile(Request(source, request =>
        {
            request.AllowUnsafe = true;
            request.Defines.Add(" UCB_TEST_DEFINE ");
            request.Defines.Add("");
        }));
        RoslynHostCompileResult noDefine = service.Compile(Request(source, request => request.AllowUnsafe = true));

        Assert.Contains(noUnsafe.Diagnostics, d => d.Id == "CS0227");
        Assert.True(withUnsafe.Ok, string.Join("\n", withUnsafe.Diagnostics.Select(d => d.Message)));
        Assert.Equal(7, Invoke(withUnsafe.Assembly!, "Snippet", "Run"));
        Assert.Equal(-1, Invoke(noDefine.Assembly!, "Snippet", "Run"));
    }

    [Fact]
    public void Compile_InfrastructureProblems_SetErrorInsteadOfDiagnostics()
    {
        var empty = new RoslynCompilerService();
        Assert.Contains("setRefs first", empty.Compile(Request("class A {}")).Error);

        RoslynCompilerService service = ServiceWithFramework();
        Assert.Contains("refsHash mismatch", service.Compile(Request("class A {}", r => r.RefsHash = "other")).Error);
        Assert.Contains("language version", service.Compile(Request("class A {}", r => r.LanguageVersion = "banana")).Error);
        Assert.Contains("no id", service.Compile(Request("class A {}", r => r.Id = "")).Error);
        Assert.True(service.Compile(Request("class A {}", r => r.RefsHash = "fw")).Ok);
    }

    [Theory]
    [InlineData("436", "CS0436")]
    [InlineData("0436", "CS0436")]
    [InlineData("cs0436", "CS0436")]
    [InlineData(" CS1701 ", "CS1701")]
    [InlineData("IDE0001", "IDE0001")]
    public void NormalizeDiagnosticId_AcceptsCscStyleIds(string input, string expected)
    {
        Assert.Equal(expected, RoslynCompilerService.NormalizeDiagnosticId(input));
    }

    [Fact]
    public void Prewarm_CompilesBothDummies()
    {
        RoslynCompilerService service = ServiceWithFramework();

        Assert.True(service.PrewarmFramework() > 0);
        Assert.True(service.PrewarmReferences() > 0);
        Assert.Equal(0, new RoslynCompilerService().PrewarmReferences());
    }

    [Fact]
    public void RoslynVersion_HasNoBuildMetadata()
    {
        string version = new RoslynCompilerService().RoslynVersion;

        Assert.StartsWith("4.3.1", version);
        Assert.DoesNotContain("+", version);
    }

    private static RoslynCompilerService ServiceWithFramework()
    {
        var service = new RoslynCompilerService();
        service.SetReferences("fw", HostTestSupport.FrameworkReferences());
        return service;
    }

    private static RoslynHostCompileRequest Request(string source, Action<RoslynHostCompileRequest>? configure = null)
    {
        var request = new RoslynHostCompileRequest
        {
            Id = Guid.NewGuid().ToString("N"),
            Source = source,
            AssemblyName = "ucbtest_" + Guid.NewGuid().ToString("N"),
        };
        configure?.Invoke(request);
        return request;
    }

    private static object? Invoke(byte[] assemblyBytes, string typeName, string methodName)
    {
        var context = new AssemblyLoadContext("ucb-roslyn-host-test", isCollectible: true);
        try
        {
            Assembly assembly = context.LoadFromStream(new MemoryStream(assemblyBytes));
            return assembly.GetType(typeName, throwOnError: true)!.GetMethod(methodName)!.Invoke(null, null);
        }
        finally
        {
            context.Unload();
        }
    }

    private string CopyReference(string name)
    {
        string path = Path.Combine(_tempDirectory, name);
        File.Copy(HostTestSupport.FrameworkReferences().First(p => p.EndsWith("System.Runtime.dll", StringComparison.Ordinal)), path);
        return path;
    }
}
