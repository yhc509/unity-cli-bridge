using UnityCli.Protocol;

namespace UnityCli.Cli.Tests;

public sealed class UsingDirectiveUtilityTests
{
    [Fact]
    public void StripUsingDirectives_ExtractsNamespaceUsing()
    {
        const string source = """
using UnityEngine;

Debug.Log("hello");
""";

        string stripped = UsingDirectiveUtility.StripUsingDirectives(source, out string[] usings);

        Assert.Equal(["using UnityEngine;"], usings);
        Assert.Equal("Debug.Log(\"hello\");", stripped);
    }

    [Fact]
    public void StripUsingDirectives_ExtractsStaticUsing()
    {
        const string source = """
using static UnityEngine.Mathf;

Debug.Log(Max(1, 2));
""";

        string stripped = UsingDirectiveUtility.StripUsingDirectives(source, out string[] usings);

        Assert.Equal(["using static UnityEngine.Mathf;"], usings);
        Assert.Equal("Debug.Log(Max(1, 2));", stripped);
    }

    [Fact]
    public void StripUsingDirectives_ExtractsAliasUsing()
    {
        const string source = """
using Vec3 = UnityEngine.Vector3;

Debug.Log(Vec3.one);
""";

        string stripped = UsingDirectiveUtility.StripUsingDirectives(source, out string[] usings);

        Assert.Equal(["using Vec3 = UnityEngine.Vector3;"], usings);
        Assert.Equal("Debug.Log(Vec3.one);", stripped);
    }

    [Fact]
    public void StripUsingDirectives_FileHeader_HoistsBomCommentsAndTrailingRemarks()
    {
        const string source = "\uFEFFusing UnityEngine; // camera\r\nusing UnityEngine.Rendering.Universal;\r\n\r\nvar btn = GameObject.Find(\"ButtonName\");\r\n";

        string stripped = UsingDirectiveUtility.StripUsingDirectives(source, out string[] usings);

        Assert.Equal(["using UnityEngine;", "using UnityEngine.Rendering.Universal;"], usings);
        Assert.Equal("var btn = GameObject.Find(\"ButtonName\");", stripped.TrimEnd('\r', '\n'));
        Assert.DoesNotContain("using ", stripped);
    }

    [Fact]
    public void StripUsingDirectives_LeavesUsingStatementsInTheBody()
    {
        const string source = "using var reader = new System.IO.StringReader(\"x\");";

        string stripped = UsingDirectiveUtility.StripUsingDirectives(source, out string[] usings);

        Assert.Empty(usings);
        Assert.Equal(source, stripped);
    }

    [Fact]
    public void RemoveShadowedAliases_UserAliasWithSameNameWins()
    {
        string[] template = ["using System;", "using Object = UnityEngine.Object;"];
        string[] user = ["using Object = System.Object;"];

        Assert.Equal(["using System;"], UsingDirectiveUtility.RemoveShadowedAliases(template, user));
    }

    [Fact]
    public void RemoveShadowedAliases_KeepsIdenticalAndUnrelatedAliases()
    {
        string[] template = ["using Object = UnityEngine.Object;", "using PucFrameLoop = UnityCliBridge.Bridge.Editor.PucFrameLoop;"];
        string[] user = ["using Object = UnityEngine.Object;", "using Vec3 = UnityEngine.Vector3;"];

        Assert.Equal(template, UsingDirectiveUtility.RemoveShadowedAliases(template, user));
    }
}

public sealed class CSharpLanguageVersionTests
{
    [Fact]
    public void Resolve_UsesUnity9WhenTheProjectDoesNotOptUp()
    {
        Assert.Equal("9.0", CSharpLanguageVersion.Resolve(null, null));
        Assert.Equal("/langversion:latest", CSharpLanguageVersion.ToCompilerOption(CSharpLanguageVersion.UnityDefault));
    }

    [Fact]
    public void ToCompilerOption_MapsVersionsMcsRejectsToLatest()
    {
        Assert.Equal("/langversion:latest", CSharpLanguageVersion.ToCompilerOption("9.0"));
        Assert.Equal("/langversion:latest", CSharpLanguageVersion.ToCompilerOption("7.3"));
        Assert.Equal("/langversion:latest", CSharpLanguageVersion.ToCompilerOption("10.0"));
        Assert.Equal("/langversion:latest", CSharpLanguageVersion.ToCompilerOption("preview"));
        Assert.Equal("/langversion:latest", CSharpLanguageVersion.ToCompilerOption("Latest"));
        Assert.Equal("/langversion:7.2", CSharpLanguageVersion.ToCompilerOption("7.2"));
        Assert.Equal("/langversion:7.1", CSharpLanguageVersion.ToCompilerOption("7.1"));
        Assert.Equal("/langversion:7", CSharpLanguageVersion.ToCompilerOption("7"));
        Assert.Equal("/langversion:6", CSharpLanguageVersion.ToCompilerOption("6.0"));
        Assert.Equal("/langversion:ISO-1", CSharpLanguageVersion.ToCompilerOption("iso-1"));
        Assert.Equal("/langversion:ISO-2", CSharpLanguageVersion.ToCompilerOption("ISO-2"));
        Assert.Equal("/langversion:Default", CSharpLanguageVersion.ToCompilerOption("default"));
    }

    [Fact]
    public void FormatCompilerFailure_IncludesErrorTextAndIsNeverEmpty()
    {
        string withErrors = CSharpLanguageVersion.FormatCompilerFailure(
            "/langversion:9.0",
            new[] { "CS1617: Invalid -langversion option '9.0'" },
            Array.Empty<string>(),
            "Compiler failed to produce the assembly. Output: ''");
        Assert.Contains("CS1617", withErrors);
        Assert.Contains("/langversion:9.0", withErrors);
        Assert.DoesNotContain("Output: ''", withErrors);

        string withOutput = CSharpLanguageVersion.FormatCompilerFailure(
            "/langversion:latest",
            Array.Empty<string>(),
            new[] { "error CS1617: Invalid -langversion option '9.0'" },
            "Compiler failed to produce the assembly. Output: ''");
        Assert.Contains("CS1617", withOutput);

        string withFallback = CSharpLanguageVersion.FormatCompilerFailure(
            "/langversion:latest",
            Array.Empty<string>(),
            new[] { "  " },
            "Compiler failed to produce the assembly. Output: ''");
        Assert.Contains("Compiler failed to produce the assembly", withFallback);
        Assert.False(string.IsNullOrWhiteSpace(withFallback));

        string withNothing = CSharpLanguageVersion.FormatCompilerFailure(null, null, null, null);
        Assert.Contains("Compiler failed to produce the assembly", withNothing);
    }

    [Fact]
    public void Resolve_ResponseFileThenAdditionalArguments_LastWins()
    {
        const string response = """
            // project default
            -unsafe
            -langversion:10.0
            """;

        Assert.Equal("10.0", CSharpLanguageVersion.Resolve(response, null));
        Assert.Equal(
            "preview",
            CSharpLanguageVersion.Resolve(response, new[] { "-nullable:enable", "/langversion:preview" }));
    }

    [Fact]
    public void Resolve_IgnoresUnsafeLangVersionText()
    {
        Assert.Equal("9.0", CSharpLanguageVersion.Resolve("-langversion:9.0;injection", null));
    }
}
