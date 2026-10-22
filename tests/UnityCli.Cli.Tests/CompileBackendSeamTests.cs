using System.Reflection;
using UnityCli.DocGen;
using UnityCli.Protocol;
using UnityCliBridge.Bridge.Editor;

namespace UnityCli.Cli.Tests;

/// <summary>
/// Covers the Unity-free half of the <c>execute</c> compiler-backend seam. The CodeDOM backend
/// itself needs Unity's mcs host and is covered by the package's EditMode tests.
/// </summary>
public sealed class CompileBackendSeamTests
{
    private const string Options = "/langversion:latest /define:UNITY_ASSERTIONS";

    [Fact]
    public void Selector_ReturnsTheFallbackBackend()
    {
        var backend = new FakeBackend("fake");
        var selector = new CompileBackendSelector(backend);

        Assert.Same(backend, selector.Select());
        Assert.Same(backend, selector.Select());
    }

    [Fact]
    public void Selector_RejectsMissingFallback()
    {
        Assert.Throws<ArgumentNullException>(() => new CompileBackendSelector(null!));
    }

    [Fact]
    public void Request_NormalizesNullCollections()
    {
        var request = new CompileRequest("class A {}", null!, null!, null!);

        Assert.Equal("class A {}", request.Source);
        Assert.Equal(string.Empty, request.LanguageVersion);
        Assert.Empty(request.ScriptingDefines);
        Assert.Empty(request.ReferencePaths);
    }

    [Fact]
    public void Request_RejectsMissingSource()
    {
        Assert.Throws<ArgumentNullException>(
            () => new CompileRequest(null!, "9.0", Array.Empty<string>(), Array.Empty<string>()));
    }

    [Fact]
    public void Success_CarriesAssemblyAndBackendId()
    {
        Assembly assembly = typeof(CompileBackendSeamTests).Assembly;

        CompileResult result = CompileResult.Success("codedom", assembly, Options);

        Assert.True(result.Succeeded);
        Assert.False(result.IsInfrastructureFailure);
        Assert.Same(assembly, result.Assembly);
        Assert.Equal("codedom", result.BackendId);
        Assert.Empty(result.Errors);
    }

    [Fact]
    public void Success_RejectsMissingAssembly()
    {
        Assert.Throws<ArgumentNullException>(() => CompileResult.Success("codedom", null!, Options));
    }

    [Fact]
    public void CompileError_IsNotAnInfrastructureFailure()
    {
        string[] errors = { "Line 1: CS1525: Unexpected symbol `;'" };

        CompileResult result = CompileResult.CompileError("codedom", Options, errors, null);

        Assert.False(result.Succeeded);
        Assert.False(result.IsInfrastructureFailure);
        Assert.Null(result.Assembly);
        Assert.Equal(errors, result.Errors);
        Assert.Empty(result.Output);
    }

    [Fact]
    public void CompileError_MessageMatchesTheExistingFailureFormat()
    {
        string[] errors = { "Line 1: CS1525: Unexpected symbol `;'" };
        string[] output = { "mcs output" };

        CompileResult result = CompileResult.CompileError("codedom", Options, errors, output);

        Assert.Equal(
            CSharpLanguageVersion.FormatCompilerFailure(Options, errors, output, null),
            result.FormatFailureMessage());
        Assert.Equal(
            "Code compilation failed (" + Options + "):\nLine 1: CS1525: Unexpected symbol `;'",
            result.FormatFailureMessage());
    }

    [Fact]
    public void InfrastructureFailure_FallsBackToTheDetail()
    {
        CompileResult result = CompileResult.InfrastructureFailure(
            "codedom",
            Options,
            Array.Empty<string>(),
            Array.Empty<string>(),
            "Could not load file or assembly.");

        Assert.False(result.Succeeded);
        Assert.True(result.IsInfrastructureFailure);
        Assert.Equal(
            "Code compilation failed (" + Options + "):\nCould not load file or assembly.",
            result.FormatFailureMessage());
    }

    [Fact]
    public void InfrastructureFailure_WithoutDetail_KeepsTheDefaultMessage()
    {
        CompileResult result = CompileResult.InfrastructureFailure("codedom", Options, null, null, null);

        Assert.Equal(
            "Code compilation failed (" + Options + "):\nCompiler failed to produce the assembly.",
            result.FormatFailureMessage());
    }

    [Fact]
    public void ExecuteCodeHandler_CompilesOnlyThroughTheSelector()
    {
        string repoRoot = RepositoryPaths.FindRepoRoot(AppContext.BaseDirectory);
        string source = File.ReadAllText(Path.Combine(
            repoRoot,
            "unity-package",
            "com.yhc509.unity-cli-bridge",
            "Editor",
            "ExecuteCodeHandler.cs"));

        Assert.Contains("BackendSelector.Compile(", source);
        Assert.DoesNotContain("CSharpCodeProvider", source);
        Assert.DoesNotContain("CompileAssemblyFromSource", source);
    }

    [Fact]
    public void Chain_StartsWithThePreferredBackend()
    {
        var host = new FakeBackend("roslyn-host");
        var codeDom = new FakeBackend("codedom");
        var selector = new CompileBackendSelector(codeDom, new ICompileBackend[] { host }, null);

        Assert.Same(host, selector.Select());
        Assert.Equal(new[] { "roslyn-host", "codedom" }, selector.Chain.Select(b => b.Id));
    }

    [Fact]
    public void Chain_Success_StopsWithoutAFallbackReason()
    {
        var host = new FakeBackend("roslyn-host", _ => CompileResult.Success("roslyn-host", typeof(CompileBackendSeamTests).Assembly, Options));
        var codeDom = new FakeBackend("codedom");

        CompileOutcome outcome = new CompileBackendSelector(codeDom, new ICompileBackend[] { host }, null).Compile(Request());

        Assert.True(outcome.Result.Succeeded);
        Assert.Equal("roslyn-host", outcome.Compiler);
        Assert.Null(outcome.FallbackReason);
        Assert.Null(outcome.FellBackFrom);
        Assert.Equal(0, codeDom.Calls);
    }

    [Fact]
    public void Chain_UserCompileError_IsReturnedAsIsAndNeverRetriedOnCodeDom()
    {
        string[] errors = { "Line 1: CS8400: Feature 'x' is not available in C# 7.3." };
        var host = new FakeBackend("roslyn-host", _ => CompileResult.CompileError("roslyn-host", Options, errors, null));
        var codeDom = new FakeBackend("codedom");

        CompileOutcome outcome = new CompileBackendSelector(codeDom, new ICompileBackend[] { host }, null).Compile(Request());

        Assert.Equal("roslyn-host", outcome.Compiler);
        Assert.False(outcome.Result.IsInfrastructureFailure);
        Assert.Equal(errors, outcome.Result.Errors);
        Assert.Equal(0, codeDom.Calls);
        Assert.Equal(outcome.Result.FormatFailureMessage(), outcome.FormatFailureMessage());
    }

    [Fact]
    public void Chain_InfrastructureFailure_FallsThroughWithTheReason()
    {
        var host = new FakeBackend(
            "roslyn-host",
            _ => CompileResult.InfrastructureFailure("roslyn-host", Options, null, null, "host-crashed: the host closed its output"));
        var codeDom = new FakeBackend("codedom", _ => CompileResult.Success("codedom", typeof(CompileBackendSeamTests).Assembly, Options));

        CompileOutcome outcome = new CompileBackendSelector(codeDom, new ICompileBackend[] { host }, null).Compile(Request());

        Assert.True(outcome.Result.Succeeded);
        Assert.Equal("codedom", outcome.Compiler);
        Assert.Equal("host-crashed: the host closed its output", outcome.FallbackReason);
        Assert.Equal("roslyn-host", outcome.FellBackFrom);
        Assert.Equal(1, host.Calls);
        Assert.Equal(1, codeDom.Calls);
    }

    [Fact]
    public void Chain_PreferredBackendThrows_IsTreatedAsAnInfrastructureFailure()
    {
        var host = new FakeBackend("roslyn-host", _ => throw new InvalidOperationException("pipe gone"));
        var codeDom = new FakeBackend("codedom", _ => CompileResult.Success("codedom", typeof(CompileBackendSeamTests).Assembly, Options));

        CompileOutcome outcome = new CompileBackendSelector(codeDom, new ICompileBackend[] { host }, null).Compile(Request());

        Assert.Equal("codedom", outcome.Compiler);
        Assert.Equal("backend-exception: InvalidOperationException: pipe gone", outcome.FallbackReason);
    }

    [Fact]
    public void Chain_FallbackThrows_Propagates()
    {
        var codeDom = new FakeBackend("codedom", _ => throw new InvalidOperationException("mcs missing"));

        Assert.Throws<InvalidOperationException>(() => new CompileBackendSelector(codeDom).Compile(Request()));
    }

    [Fact]
    public void Chain_CodeDomCompileErrorAfterHostFallback_AppendsTheMcsNote()
    {
        string[] errors = { "Line 1: CS1525: Unexpected symbol `with'" };
        var host = new FakeBackend(
            "roslyn-host",
            _ => CompileResult.InfrastructureFailure("roslyn-host", Options, null, null, "host-not-ready: the host is still starting after 1500 ms"));
        var codeDom = new FakeBackend("codedom", _ => CompileResult.CompileError("codedom", Options, errors, null));

        CompileOutcome outcome = new CompileBackendSelector(codeDom, new ICompileBackend[] { host }, null).Compile(Request());

        Assert.Equal(
            "Code compilation failed (" + Options + "):\nLine 1: CS1525: Unexpected symbol `with'\n"
            + "compiled with mcs fallback (C# 7.x); Roslyn host unavailable: host-not-ready: the host is still starting after 1500 ms",
            outcome.FormatFailureMessage());
    }

    [Fact]
    public void Chain_CodeDomOnly_NeverAppendsTheMcsNote()
    {
        string[] errors = { "Line 1: CS0029: Cannot implicitly convert type `string' to `int'" };
        var codeDom = new FakeBackend("codedom", _ => CompileResult.CompileError("codedom", Options, errors, null));

        CompileOutcome outcome = new CompileBackendSelector(codeDom).Compile(Request());

        Assert.Equal("codedom", outcome.Compiler);
        Assert.Null(outcome.FallbackReason);
        Assert.Equal(outcome.Result.FormatFailureMessage(), outcome.FormatFailureMessage());
    }

    [Fact]
    public void Chain_ConfigurationReason_IsReportedOnTheFallbackOnlyChain()
    {
        var codeDom = new FakeBackend("codedom", _ => CompileResult.Success("codedom", typeof(CompileBackendSeamTests).Assembly, Options));

        CompileOutcome outcome = new CompileBackendSelector(codeDom, Array.Empty<ICompileBackend>(), "csc-shared-unavailable: later").Compile(Request());

        Assert.Equal("codedom", outcome.Compiler);
        Assert.Equal("csc-shared-unavailable: later", outcome.FallbackReason);
        Assert.Null(outcome.FellBackFrom);
    }

    [Fact]
    public void BackendIds_MatchTheWireNames()
    {
        Assert.Equal("roslyn-host", CompileBackendIds.RoslynHost);
        Assert.Equal("csc-shared", CompileBackendIds.CscShared);
        Assert.Equal("codedom", CompileBackendIds.CodeDom);
    }

    [Fact]
    public void Request_CarriesProjectOptions_DefaultingToEmpty()
    {
        Assert.Same(ProjectCompilerOptions.Empty, Request().ProjectOptions);
        ProjectCompilerOptions options = ProjectCompilerOptions.Parse("-unsafe");
        Assert.Same(options, new CompileRequest("x", "9.0", Array.Empty<string>(), Array.Empty<string>(), options).ProjectOptions);
    }

    private static CompileRequest Request()
    {
        return new CompileRequest("class A {}", "9.0", Array.Empty<string>(), Array.Empty<string>());
    }

    private sealed class FakeBackend : ICompileBackend
    {
        private readonly Func<CompileRequest, CompileResult> _compile;

        public FakeBackend(string id, Func<CompileRequest, CompileResult>? compile = null)
        {
            Id = id;
            _compile = compile ?? (_ => CompileResult.CompileError(id, string.Empty, null, null));
        }

        public string Id { get; }

        public int Calls { get; private set; }

        public CompileResult TryCompile(CompileRequest request)
        {
            Calls++;
            return _compile(request);
        }
    }
}
