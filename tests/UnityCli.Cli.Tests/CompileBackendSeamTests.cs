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

        Assert.Contains("BackendSelector.Select().TryCompile(", source);
        Assert.DoesNotContain("CSharpCodeProvider", source);
        Assert.DoesNotContain("CompileAssemblyFromSource", source);
    }

    private sealed class FakeBackend : ICompileBackend
    {
        public FakeBackend(string id)
        {
            Id = id;
        }

        public string Id { get; }

        public CompileResult TryCompile(CompileRequest request)
        {
            return CompileResult.CompileError(Id, string.Empty, null, null);
        }
    }
}
