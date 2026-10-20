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
        Assert.Equal("/langversion:9.0", CSharpLanguageVersion.ToCompilerOption(CSharpLanguageVersion.UnityDefault));
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
