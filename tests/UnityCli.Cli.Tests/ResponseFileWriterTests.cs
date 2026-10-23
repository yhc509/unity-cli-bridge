using System.Text;
using UnityCli.DocGen;
using UnityCliBridge.Bridge.Editor;

namespace UnityCli.Cli.Tests;

/// <summary>
/// Covers the response file the CodeDOM backend hands to mcs (#220). <see cref="ParseLikeMcs"/>
/// mirrors mcs's <c>CommandLineParser.LoadArgs</c> (Unity 2021.3 and 6000.3 ship the same one),
/// so the quoting is checked against the reader that actually consumes the file.
/// </summary>
public sealed class ResponseFileWriterTests
{
    private const string LanguageOption = "/langversion:latest";

    [Fact]
    public void Quote_WrapsPathsWithSpaces()
    {
        const string path = "/Users/dev/My Project/Library/ScriptAssemblies/Assembly-CSharp.dll";

        string quoted = ResponseFileWriter.Quote(path);

        Assert.Equal("\"" + path + "\"", quoted);
        Assert.Equal(new[] { path }, ParseLikeMcs(quoted));
    }

    [Theory]
    [InlineData("/tmp/a\"b/Lib.dll", "\"/tmp/a\"'\"'\"b/Lib.dll\"")]
    [InlineData("\"", "\"\"'\"'\"\"")]
    [InlineData("it's \"here\".dll", "\"it's \"'\"'\"here\"'\"'\".dll\"")]
    public void Quote_EscapesEmbeddedDoubleQuotes(string path, string expected)
    {
        string quoted = ResponseFileWriter.Quote(path);

        Assert.Equal(expected, quoted);
        Assert.Equal(new[] { path }, ParseLikeMcs(quoted));
    }

    [Theory]
    [InlineData("/Users/개발자/유니티 프로젝트/Library/ScriptAssemblies/Assembly-CSharp.dll")]
    [InlineData("C:\\Users\\José Ñuñez\\AppData\\Local\\Temp\\Bibliothèque.dll")]
    [InlineData("/Users/dev/プロジェクト/日本語 フォルダ/Lib.dll")]
    public void Write_RoundTripsNonAsciiPathsAsUtf8WithoutBom(string path)
    {
        string responseFile = ResponseFileWriter.CreateTempPath(Path.GetTempPath());
        try
        {
            ResponseFileWriter.Write(
                responseFile,
                ResponseFileWriter.BuildCompilerArguments(new[] { path }, LanguageOption));

            byte[] bytes = File.ReadAllBytes(responseFile);
            Assert.False(
                bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF,
                "The response file must not start with a UTF-8 BOM.");
            string text = new UTF8Encoding(false, throwOnInvalidBytes: true).GetString(bytes);
            Assert.Equal(new[] { "/r:" + path, LanguageOption }, ParseLikeMcs(text));
        }
        finally
        {
            ResponseFileWriter.TryDelete(responseFile);
        }
    }

    [Fact]
    public void BuildCompilerArguments_KeepsReferenceOrderThenOptions()
    {
        string[] references = { "/b/Second.dll", "", "/a/First.dll", "/c/Third.dll" };

        List<string> arguments = ResponseFileWriter.BuildCompilerArguments(
            references,
            LanguageOption,
            "/define:UNITY_ASSERTIONS;UNITY_EDITOR");

        Assert.Equal(
            new[]
            {
                "/r:\"/b/Second.dll\"",
                "/r:\"/a/First.dll\"",
                "/r:\"/c/Third.dll\"",
                LanguageOption,
                "/define:UNITY_ASSERTIONS;UNITY_EDITOR",
            },
            arguments);
    }

    [Fact]
    public void FormatContent_PutsOneArgumentPerLine()
    {
        string content = ResponseFileWriter.FormatContent(new[] { "/r:\"/a b/X.dll\"", LanguageOption });

        Assert.Equal("/r:\"/a b/X.dll\"\n" + LanguageOption + "\n", content);
    }

    [Theory]
    [InlineData("/tmp/a\nb.dll")]
    [InlineData("/tmp/a\rb.dll")]
    public void FormatContent_RejectsLineBreaks(string path)
    {
        List<string> arguments = ResponseFileWriter.BuildCompilerArguments(new[] { path });

        Assert.Throws<ArgumentException>(() => ResponseFileWriter.FormatContent(arguments));
    }

    [Fact]
    public void ManyReferences_StayInTheFileAndOffTheCommandLine()
    {
        // 357 references is the largest project measured for #220 (49,429 inline characters on
        // macOS). Each synthetic path is about 110 characters, like a PackageCache assembly path.
        const int referenceCount = 357;
        var references = new List<string>();
        for (int index = 0; index < referenceCount; index++)
        {
            references.Add(
                "/Users/developer/Projects/Large Project/Library/PackageCache/com.example.package-"
                + index.ToString("D3")
                + "@1.2.3/Runtime/Example.Package.dll");
        }

        Assert.All(references, path => Assert.InRange(path.Length, 100, 120));
        var defines = new List<string>();
        for (int index = 0; index < 120; index++)
        {
            defines.Add("UNITY_DEFINE_SYMBOL_" + index.ToString("D3"));
        }

        string defineOption = "/define:UNITY_ASSERTIONS;" + string.Join(";", defines);
        int inlineLength = references.Sum(path => ("/r:\"" + path + "\" ").Length);
        Assert.True(inlineLength > 32767, "The synthetic set must exceed the Windows limit when inlined.");

        string responseFile = ResponseFileWriter.CreateTempPath(Path.GetTempPath());
        try
        {
            ResponseFileWriter.Write(
                responseFile,
                ResponseFileWriter.BuildCompilerArguments(references, LanguageOption, defineOption));
            string commandLineArgument = ResponseFileWriter.ToCommandLineArgument(responseFile);

            Assert.True(
                commandLineArgument.Length < 8 * 1024,
                "Command-line argument is " + commandLineArgument.Length + " characters.");
            Assert.Equal("@\"" + responseFile + "\"", commandLineArgument);

            List<string> parsed = ParseLikeMcs(File.ReadAllText(responseFile, Encoding.UTF8));
            Assert.Equal(referenceCount + 2, parsed.Count);
            Assert.Equal(references.Select(path => "/r:" + path), parsed.Take(referenceCount));
            Assert.Equal(LanguageOption, parsed[referenceCount]);
            Assert.Equal(defineOption, parsed[referenceCount + 1]);
        }
        finally
        {
            ResponseFileWriter.TryDelete(responseFile);
        }

        Assert.False(File.Exists(responseFile));
    }

    [Fact]
    public void CreateTempPath_IsUniqueAndTryDeleteToleratesMissingFiles()
    {
        string directory = Path.GetTempPath();
        string first = ResponseFileWriter.CreateTempPath(directory);
        string second = ResponseFileWriter.CreateTempPath(directory);

        Assert.NotEqual(first, second);
        Assert.Equal(Path.GetFullPath(directory), Path.GetFullPath(Path.GetDirectoryName(first)! + Path.DirectorySeparatorChar));
        Assert.EndsWith(ResponseFileWriter.FileExtension, first, StringComparison.Ordinal);
        Assert.False(File.Exists(first));

        ResponseFileWriter.Write(first, new[] { LanguageOption });
        Assert.True(File.Exists(first));
        ResponseFileWriter.TryDelete(first);
        Assert.False(File.Exists(first));
        ResponseFileWriter.TryDelete(first);
    }

    [Fact]
    public void CodeDomBackend_PassesReferencesOnlyThroughTheResponseFile()
    {
        string repoRoot = RepositoryPaths.FindRepoRoot(AppContext.BaseDirectory);
        string source = File.ReadAllText(Path.Combine(
            repoRoot,
            "unity-package",
            "com.yhc509.unity-cli-bridge",
            "Editor",
            "Execute",
            "Compile",
            "CodeDomBackend.cs"));

        Assert.DoesNotContain("ReferencedAssemblies.Add", source);
        Assert.Contains("ResponseFileWriter.ToCommandLineArgument(", source);
        Assert.Matches(@"finally\s*\{\s*ResponseFileWriter\.TryDelete\(", source);
    }

    /// <summary>mcs <c>LoadArgs</c>: per line, <c>"</c> or <c>'</c> runs to the same character, a space splits.</summary>
    private static List<string> ParseLikeMcs(string content)
    {
        var arguments = new List<string>();
        var current = new StringBuilder();
        using var reader = new StringReader(content);
        string? line;
        while ((line = reader.ReadLine()) != null)
        {
            for (int index = 0; index < line.Length; index++)
            {
                char character = line[index];
                if (character == '"' || character == '\'')
                {
                    char end = character;
                    for (index++; index < line.Length && line[index] != end; index++)
                    {
                        current.Append(line[index]);
                    }
                }
                else if (character == ' ')
                {
                    if (current.Length > 0)
                    {
                        arguments.Add(current.ToString());
                        current.Clear();
                    }
                }
                else
                {
                    current.Append(character);
                }
            }

            if (current.Length > 0)
            {
                arguments.Add(current.ToString());
                current.Clear();
            }
        }

        return arguments;
    }
}
