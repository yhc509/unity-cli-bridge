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
