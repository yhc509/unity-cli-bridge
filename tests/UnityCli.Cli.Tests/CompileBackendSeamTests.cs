using System.Reflection;
using UnityCli.DocGen;
using UnityCli.Protocol;
using UnityCliBridge.Bridge.Editor;

namespace UnityCli.Cli.Tests;

/// <summary>
/// Covers the Unity-free half of the <c>execute</c> compiler-backend seam, including the mcs
/// argument string. The CodeDOM host call needs Unity's mcs and is covered by the package's
/// EditMode tests.
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
