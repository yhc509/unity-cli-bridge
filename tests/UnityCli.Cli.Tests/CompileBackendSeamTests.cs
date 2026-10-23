using System.Reflection;
using UnityCli.DocGen;
using UnityCli.Protocol;
using UnityCliBridge.Bridge.Editor;

namespace UnityCli.Cli.Tests;

/// <summary>
/// Covers the Unity-free half of the <c>execute</c> compiler-backend seam: the backend chain
/// and the mcs argument string (response file and <c>-nostdlib</c>). The CodeDOM host call
/// needs Unity's mcs and is covered by the package's EditMode tests.
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
    public void ThreeStepChain_KeepsTheOrderHostCscSharedCodeDom()
    {
        var selector = new CompileBackendSelector(
            new FakeBackend("codedom"),
            new ICompileBackend[] { new FakeBackend("roslyn-host"), new FakeBackend("csc-shared") },
            null);

        Assert.Equal(new[] { "roslyn-host", "csc-shared", "codedom" }, selector.Chain.Select(b => b.Id));
        Assert.Equal("roslyn-host", selector.Select().Id);
    }

    [Fact]
    public void ThreeStepChain_HostInfrastructureFailure_IsServedByCscSharedWithTheHostReason()
    {
        var host = new FakeBackend(
            "roslyn-host",
            _ => CompileResult.InfrastructureFailure("roslyn-host", Options, null, null, "host-crashed: the host exited while idle"));
        var cscShared = new FakeBackend("csc-shared", _ => CompileResult.Success("csc-shared", typeof(CompileBackendSeamTests).Assembly, Options));
        var codeDom = new FakeBackend("codedom");

        CompileOutcome outcome = new CompileBackendSelector(codeDom, new ICompileBackend[] { host, cscShared }, null).Compile(Request());

        Assert.True(outcome.Result.Succeeded);
        Assert.Equal("csc-shared", outcome.Compiler);
        Assert.Equal("host-crashed: the host exited while idle", outcome.FallbackReason);
        Assert.Equal("roslyn-host", outcome.FellBackFrom);
        Assert.Equal(0, codeDom.Calls);
    }

    [Fact]
    public void ThreeStepChain_CscSharedUserError_IsReturnedAndNeverRetriedOnCodeDom()
    {
        string[] errors = { "Line 2: CS0029: Cannot implicitly convert type 'string' to 'int'" };
        var host = new FakeBackend(
            "roslyn-host",
            _ => CompileResult.InfrastructureFailure("roslyn-host", Options, null, null, "session-disabled: 3 host failures within 60 s"));
        var cscShared = new FakeBackend("csc-shared", _ => CompileResult.CompileError("csc-shared", Options, errors, null));
        var codeDom = new FakeBackend("codedom");

        CompileOutcome outcome = new CompileBackendSelector(codeDom, new ICompileBackend[] { host, cscShared }, null).Compile(Request());

        Assert.Equal("csc-shared", outcome.Compiler);
        Assert.False(outcome.Result.Succeeded);
        Assert.False(outcome.Result.IsInfrastructureFailure);
        Assert.Equal(errors, outcome.Result.Errors);
        Assert.Equal(0, codeDom.Calls);
        Assert.Equal(outcome.Result.FormatFailureMessage(), outcome.FormatFailureMessage());
    }

    [Fact]
    public void ThreeStepChain_BothRoslynBackendsFail_CodeDomGetsBothReasonsAndTheMcsNote()
    {
        string[] errors = { "Line 1: CS1525: Unexpected symbol `with'" };
        var host = new FakeBackend(
            "roslyn-host",
            _ => CompileResult.InfrastructureFailure("roslyn-host", Options, null, null, "host-crashed: gone"));
        var cscShared = new FakeBackend(
            "csc-shared",
            _ => CompileResult.InfrastructureFailure("csc-shared", Options, null, null, "csc-shared-timeout: csc did not finish in 10000 ms"));
        var codeDom = new FakeBackend("codedom", _ => CompileResult.CompileError("codedom", Options, errors, null));

        CompileOutcome outcome = new CompileBackendSelector(codeDom, new ICompileBackend[] { host, cscShared }, null).Compile(Request());

        Assert.Equal("codedom", outcome.Compiler);
        Assert.Equal("csc-shared", outcome.FellBackFrom);
        Assert.Equal("host-crashed: gone; csc-shared-timeout: csc did not finish in 10000 ms", outcome.FallbackReason);
        Assert.EndsWith(
            "\ncompiled with mcs fallback (C# 7.x); Roslyn host unavailable: host-crashed: gone; csc-shared-timeout: csc did not finish in 10000 ms",
            outcome.FormatFailureMessage());
    }

    [Fact]
    public void CscSharedForced_InfrastructureFailure_FallsBackToCodeDom()
    {
        var cscShared = new FakeBackend(
            "csc-shared",
            _ => CompileResult.InfrastructureFailure("csc-shared", Options, null, null, "csc-shared-unavailable: bundled Roslyn not found"));
        var codeDom = new FakeBackend("codedom", _ => CompileResult.Success("codedom", typeof(CompileBackendSeamTests).Assembly, Options));

        CompileOutcome outcome = new CompileBackendSelector(codeDom, new ICompileBackend[] { cscShared }, null).Compile(Request());

        Assert.Equal("codedom", outcome.Compiler);
        Assert.Equal("csc-shared-unavailable: bundled Roslyn not found", outcome.FallbackReason);
        Assert.Equal("csc-shared", outcome.FellBackFrom);
    }

    [Fact]
    public void ConfigurationReason_PrecedesASkippedBackendsReason()
    {
        var cscShared = new FakeBackend(
            "csc-shared",
            _ => CompileResult.InfrastructureFailure("csc-shared", Options, null, null, "csc-shared-failed: exit 1"));
        var codeDom = new FakeBackend("codedom", _ => CompileResult.Success("codedom", typeof(CompileBackendSeamTests).Assembly, Options));

        CompileOutcome outcome = new CompileBackendSelector(codeDom, new ICompileBackend[] { cscShared }, "start-failed: host setup failed").Compile(Request());

        Assert.Equal("start-failed: host setup failed; csc-shared-failed: exit 1", outcome.FallbackReason);
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

    [Fact]
    public void CompilerOptions_IncludeNoStdLibOnceWhenMscorlibIsReferenced()
    {
        var request = new CompileRequest(
            "class A {}",
            "9.0",
            new[] { "UNITY_EDITOR" },
            new[]
            {
                @"C:\Program Files\Unity\Hub\Editor\2021.3.45f2\Editor\Data\MonoBleedingEdge\lib\mono\unityjit-win32\mscorlib.dll",
                @"C:\Program Files\Unity\Hub\Editor\2021.3.45f2\Editor\Data\Managed\UnityEngine\UnityEngine.CoreModule.dll",
                "/Applications/Unity/Hub/Editor/6000.3.10f1/Unity.app/Contents/MonoBleedingEdge/lib/mono/unityaot-macos/mscorlib.dll",
                "MSCORLIB.DLL",
            });

        string options = CodeDomBackend.BuildCompilerOptions(request);

        Assert.Equal(1, CountOption(options, "-nostdlib"));
        Assert.Equal(0, CountOption(options, "/nostdlib"));
        Assert.Equal(
            "/langversion:latest /define:UNITY_ASSERTIONS;UNITY_EDITOR -nostdlib",
            options);
    }

    [Fact]
    public void CompilerOptions_OmitNoStdLibWhenMscorlibIsNotReferenced()
    {
        var request = new CompileRequest(
            "class A {}",
            "7.2",
            Array.Empty<string>(),
            new[]
            {
                "/tmp/UnityEngine.CoreModule.dll",
                @"C:\Unity\System.Core.dll",
                "notmscorlib.dll",
                "mscorlib.dll.bak",
                "Foo.mscorlib.dll",
                @"C:\Unity\lib\mscorlib.dll\Other.dll",
                "/mono/unityjit/mscorlib.dll.facade",
                "",
            });

        string options = CodeDomBackend.BuildCompilerOptions(request);

        Assert.Equal(0, CountOption(options, "-nostdlib"));
        Assert.DoesNotContain("nostdlib", options, StringComparison.Ordinal);
        Assert.Equal("/langversion:7.2 /define:UNITY_ASSERTIONS", options);

        string noReferences = CodeDomBackend.BuildCompilerOptions(
            new CompileRequest("class A {}", "9.0", null!, null!));
        Assert.DoesNotContain("nostdlib", noReferences, StringComparison.Ordinal);
        Assert.Equal("/langversion:latest /define:UNITY_ASSERTIONS", noReferences);
    }

    [Fact]
    public void ResponseFile_IncludesNoStdLibOnceWhenMscorlibIsReferenced()
    {
        var request = new CompileRequest(
            "class A {}",
            "9.0",
            new[] { "UNITY_EDITOR" },
            new[]
            {
                @"C:\Program Files\Unity\Hub\Editor\2021.3.45f2\Editor\Data\MonoBleedingEdge\lib\mono\unityjit-win32\mscorlib.dll",
                @"C:\Unity\-nostdlib\Helper.dll",
                "/Applications/Unity/Hub/Editor/6000.3.10f1/Unity.app/Contents/MonoBleedingEdge/lib/mono/unityaot-macos/MSCorLib.DLL",
            });

        AssertEffectiveNoStdLib(request, expected: 1);
    }

    [Fact]
    public void ResponseFile_OmitsNoStdLibWhenMscorlibIsNotReferenced()
    {
        var request = new CompileRequest(
            "class A {}",
            "7.2",
            Array.Empty<string>(),
            new[]
            {
                "/tmp/UnityEngine.CoreModule.dll",
                @"C:\Unity\System.Core.dll",
                "notmscorlib.dll",
                "mscorlib.dll.bak",
                "Foo.mscorlib.dll",
                @"C:\Unity\lib\mscorlib.dll\Other.dll",
                "/mono/unityjit/mscorlib.dll.facade",
                @"C:\Unity\-nostdlib\Helper.dll",
                "",
            });

        AssertEffectiveNoStdLib(request, expected: 0);
        AssertEffectiveNoStdLib(
            new CompileRequest("class A {}", "9.0", null!, null!),
            expected: 0);
    }

    /// <summary>
    /// Effective mcs arguments are the command line plus the response-file body. <c>-nostdlib</c>
    /// counts once across both, and references stay in the file.
    /// </summary>
    private static void AssertEffectiveNoStdLib(CompileRequest request, int expected)
    {
        string responseFile = ResponseFileWriter.CreateTempPath(Path.GetTempPath());
        try
        {
            CodeDomBackend.BuildMcsInvocation(
                request,
                responseFile,
                out string commandLine,
                out IReadOnlyList<string> responseFileArguments);
            ResponseFileWriter.Write(responseFile, responseFileArguments);
            string responseText = File.ReadAllText(responseFile);

            int nostdlib = CountMcsArgument(commandLine, "-nostdlib")
                + CountMcsArgument(responseText, "-nostdlib");
            int slashForm = CountMcsArgument(commandLine, "/nostdlib")
                + CountMcsArgument(responseText, "/nostdlib");

            Assert.Equal(expected, nostdlib);
            Assert.Equal(0, slashForm);
            Assert.Equal(ResponseFileWriter.ToCommandLineArgument(responseFile), commandLine);
            Assert.DoesNotContain("/r:", commandLine, StringComparison.Ordinal);
            Assert.Equal(0, CountMcsArgument(commandLine, "-nostdlib"));

            bool hasReference = false;
            for (int index = 0; index < request.ReferencePaths.Count; index++)
            {
                if (!string.IsNullOrEmpty(request.ReferencePaths[index]))
                {
                    hasReference = true;
                    break;
                }
            }

            if (hasReference)
            {
                Assert.Contains("/r:", responseText, StringComparison.Ordinal);
            }
            else
            {
                Assert.DoesNotContain("/r:", responseText, StringComparison.Ordinal);
            }
        }
        finally
        {
            ResponseFileWriter.TryDelete(responseFile);
        }

        Assert.False(File.Exists(responseFile));
    }

    private static int CountOption(string options, string option)
    {
        return options.Split(' ').Count(token => token == option);
    }

    /// <summary>Counts an mcs argument in a command line or response file. Quoted runs stay one token.</summary>
    private static int CountMcsArgument(string text, string argument)
    {
        int count = 0;
        int index = 0;
        while (index < text.Length)
        {
            char character = text[index];
            if (character == ' ' || character == '\n' || character == '\r' || character == '\t')
            {
                index++;
                continue;
            }

            int start = index;
            if (character == '"' || character == '\'')
            {
                char end = character;
                index++;
                while (index < text.Length && text[index] != end)
                {
                    index++;
                }

                if (index < text.Length)
                {
                    index++;
                }
            }
            else
            {
                while (index < text.Length
                    && text[index] != ' '
                    && text[index] != '\n'
                    && text[index] != '\r'
                    && text[index] != '\t'
                    && text[index] != '"'
                    && text[index] != '\'')
                {
                    index++;
                }
            }

            if (text.Substring(start, index - start) == argument)
            {
                count++;
            }
        }

        return count;
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
