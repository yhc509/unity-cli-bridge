using UnityCli.Protocol;

namespace UnityCli.RoslynHost.Tests;

public sealed class RoslynHostWireTests
{
    [Fact]
    public void Json_RoundTripsEscapesControlCharactersAndNonAscii()
    {
        string text = "quote\" back\\ nl\n cr\r tab\t bell\u0007 한글 😀 /";

        string json = RoslynHostJson.Serialize(new Dictionary<string, object?> { ["s"] = text });
        var parsed = (Dictionary<string, object?>)RoslynHostJson.Parse(json)!;

        Assert.Equal(text, parsed["s"]);
        Assert.Contains("\\u0007", json);
        Assert.Contains("한글", json);
    }

    [Fact]
    public void Json_EscapesLoneSurrogatesSoTheHeaderStaysValidUtf8()
    {
        string text = "a\uD800b";

        string json = RoslynHostJson.Serialize(text);

        Assert.Equal("\"a\\ud800b\"", json);
        Assert.Equal(text, RoslynHostJson.Parse(json));
    }

    [Fact]
    public void Json_ParsesNumbersBoolsNullArraysAndNestedObjects()
    {
        var parsed = (Dictionary<string, object?>)RoslynHostJson.Parse(
            " { \"n\": -12.5e1, \"i\": 42, \"t\": true, \"f\": false, \"z\": null, \"a\": [1, \"x\", {\"k\": []}] } ")!;

        Assert.Equal(-125d, parsed["n"]);
        Assert.Equal(42L, RoslynHostJson.GetInt64(parsed, "i"));
        Assert.True(RoslynHostJson.GetBool(parsed, "t"));
        Assert.False(RoslynHostJson.GetBool(parsed, "f", true));
        Assert.Null(parsed["z"]);
        var array = (List<object?>)parsed["a"]!;
        Assert.Equal(3, array.Count);
        Assert.IsType<Dictionary<string, object?>>(array[2]);
    }

    [Fact]
    public void Json_WritesInvariantDoublesAndNullForNonFinite()
    {
        string json = RoslynHostJson.Serialize(new object?[] { 1.5, double.NaN, 3, 4L, null });

        Assert.Equal("[1.5,null,3,4,null]", json);
    }

    [Theory]
    [InlineData("{\"a\":1,\"a\":2}", "duplicate")]
    [InlineData("{\"a\":1", "end of input")]
    [InlineData("\"abc", "unterminated")]
    [InlineData("\"\\x\"", "invalid escape")]
    [InlineData("\"\\u12\"", "\\u escape")]
    [InlineData("[1,]", "unexpected character")]
    [InlineData("tru", "invalid literal")]
    [InlineData("1e999", "invalid number")]
    [InlineData("\"a\u0001\"", "control character")]
    public void Json_RejectsMalformedInput(string json, string expected)
    {
        var ex = Assert.Throws<FormatException>(() => RoslynHostJson.Parse(json));
        Assert.Contains(expected, ex.Message);
    }

    [Fact]
    public void Json_RejectsNestingDeeperThanTheLimit()
    {
        string deep = new string('[', RoslynHostJson.MaxDepth + 2) + new string(']', RoslynHostJson.MaxDepth + 2);

        Assert.Throws<FormatException>(() => RoslynHostJson.Parse(deep));
    }

    [Fact]
    public void GetInt64_IgnoresFractionsAndOutOfRangeValues()
    {
        var obj = (Dictionary<string, object?>)RoslynHostJson.Parse("{\"a\":1.5,\"b\":-3,\"c\":\"7\"}")!;

        Assert.Equal(-1, RoslynHostJson.GetInt64(obj, "a", -1));
        Assert.Equal(-1, RoslynHostJson.GetInt64(obj, "b", -1, 0));
        Assert.Equal(-1, RoslynHostJson.GetInt64(obj, "c", -1));
    }

    [Fact]
    public void Messages_RoundTripThroughTheCodec()
    {
        var hello = new RoslynHostHello
        {
            RuntimeDirectory = "/u/NetCoreRuntime/shared/Microsoft.NETCore.App/6.0.21/",
            CoreLibraryPath = "/u/NetCoreRuntime/shared/Microsoft.NETCore.App/6.0.21/System.Private.CoreLib.dll",
            RuntimeVersion = "6.0.21",
            RoslynVersion = "4.3.1-3.22526.13",
            ProcessId = 4321,
            PrewarmMs = 123.456,
            Marker = "0123456789ab",
        };
        var compile = new RoslynHostCompileRequest
        {
            Id = "c1",
            RefsHash = "h",
            Source = "class A {}",
            Defines = new List<string> { "UNITY_EDITOR", "DEBUG" },
            LanguageVersion = "9",
            AllowUnsafe = true,
            NoWarn = new List<string> { "0436" },
            AssemblyName = "asm",
            FilePath = "wrapped.cs",
        };
        var result = new RoslynHostCompileResult
        {
            Id = "c1",
            Ok = true,
            CompileMs = 4.5,
            Assembly = new byte[] { 1, 2, 3 },
            Diagnostics = { new RoslynHostDiagnostic { Id = "CS0168", Severity = "warning", Line = 2, Column = 5, File = "user-code", Message = "unused" } },
        };

        List<RoslynHostMessage> decoded = HostTestSupport.DecodeAll(HostTestSupport.Encode(hello.ToMessage(), compile.ToMessage(), result.ToMessage()));

        RoslynHostHello hello2 = RoslynHostHello.FromMessage(decoded[0]);
        Assert.Equal(RoslynHostHello.CurrentProtocol, hello2.Protocol);
        Assert.Equal(hello.CoreLibraryPath, hello2.CoreLibraryPath);
        Assert.Equal(4321, hello2.ProcessId);
        Assert.Equal(123.456, hello2.PrewarmMs);
        Assert.Equal("0123456789ab", hello2.Marker);

        RoslynHostCompileRequest compile2 = RoslynHostCompileRequest.FromMessage(decoded[1]);
        Assert.Equal(compile.Defines, compile2.Defines);
        Assert.True(compile2.AllowUnsafe);
        Assert.Equal(compile.NoWarn, compile2.NoWarn);
        Assert.Equal("wrapped.cs", compile2.FilePath);

        RoslynHostCompileResult result2 = RoslynHostCompileResult.FromMessage(decoded[2]);
        Assert.True(result2.Ok);
        Assert.Equal(new byte[] { 1, 2, 3 }, result2.Assembly);
        RoslynHostDiagnostic diagnostic = Assert.Single(result2.Diagnostics);
        Assert.Equal(("CS0168", "warning", 2, 5, "user-code"), (diagnostic.Id, diagnostic.Severity, diagnostic.Line, diagnostic.Column, diagnostic.File));
    }

    [Fact]
    public void FailedResult_CarriesNoPayloadEvenIfBytesWereSet()
    {
        var result = new RoslynHostCompileResult { Id = "x", Ok = false, Assembly = new byte[] { 1 }, Error = "boom" };

        RoslynHostCompileResult decoded = RoslynHostCompileResult.FromMessage(HostTestSupport.DecodeAll(HostTestSupport.Encode(result.ToMessage()))[0]);

        Assert.Null(decoded.Assembly);
        Assert.Equal("boom", decoded.Error);
    }

    [Fact]
    public void FromMessage_RejectsTheWrongType()
    {
        Assert.Throws<ArgumentException>(() => RoslynHostHello.FromMessage(new RoslynHostMessage("result")));
    }

    [Fact]
    public void Hello_IsRuntimeUnder_RequiresBothPathsInsideTheBundledRuntime()
    {
        string root = Path.Combine(Path.GetTempPath(), "Unity", "NetCoreRuntime");
        string fx = Path.Combine(root, "shared", "Microsoft.NETCore.App", "6.0.21");
        var hello = new RoslynHostHello { RuntimeDirectory = fx + Path.DirectorySeparatorChar, CoreLibraryPath = Path.Combine(fx, "System.Private.CoreLib.dll") };

        Assert.True(hello.IsRuntimeUnder(root));
        Assert.True(hello.IsRuntimeUnder(root + Path.DirectorySeparatorChar));
        Assert.False(hello.IsRuntimeUnder(Path.Combine(Path.GetTempPath(), "Unity", "NetCore")));
        Assert.False(hello.IsRuntimeUnder(Path.Combine(Path.GetTempPath(), "dotnet")));

        hello.CoreLibraryPath = Path.Combine(Path.GetTempPath(), "dotnet", "System.Private.CoreLib.dll");
        Assert.False(hello.IsRuntimeUnder(root));
    }

    [Fact]
    public void Arguments_FormatAndParseRoundTrip_WithTheMarkerFirst()
    {
        var args = new RoslynHostArguments("0123456789ab", "/Unity/DotNetSdkRoslyn", 4242, 1700000000123, prewarm: true);

        List<string> list = args.ToArgumentList();
        Assert.Equal(RoslynHostArguments.MarkerOption, list[0]);
        Assert.Equal("0123456789ab", list[1]);

        Assert.True(RoslynHostArguments.TryParse(list, out RoslynHostArguments? parsed, out string? error), error);
        Assert.Equal("0123456789ab", parsed!.ProjectHash);
        Assert.Equal("/Unity/DotNetSdkRoslyn", parsed.RoslynDirectory);
        Assert.Equal(4242, parsed.ParentProcessId);
        Assert.Equal(1700000000123, parsed.ParentStartUnixMs);
        Assert.True(parsed.Prewarm);
    }

    [Fact]
    public void Arguments_PrewarmAndParentStartAreOptional()
    {
        var args = new RoslynHostArguments("abc", "/r", 1, null, prewarm: false);

        Assert.True(RoslynHostArguments.TryParse(args.ToArgumentList(), out RoslynHostArguments? parsed, out _));
        Assert.False(parsed!.Prewarm);
        Assert.Null(parsed.ParentStartUnixMs);
    }

    [Theory]
    [InlineData(new[] { "--roslyn-dir", "/r", "--parent-pid", "1" }, "--ucb-roslyn-host")]
    [InlineData(new[] { "--ucb-roslyn-host", "a b", "--roslyn-dir", "/r", "--parent-pid", "1" }, "spaces")]
    [InlineData(new[] { "--ucb-roslyn-host", "abc", "--parent-pid", "1" }, "--roslyn-dir")]
    [InlineData(new[] { "--ucb-roslyn-host", "abc", "--roslyn-dir", "/r" }, "--parent-pid")]
    [InlineData(new[] { "--ucb-roslyn-host", "abc", "--roslyn-dir", "/r", "--parent-pid", "-5" }, "Invalid --parent-pid")]
    [InlineData(new[] { "--ucb-roslyn-host", "abc", "--roslyn-dir", "/r", "--parent-pid", "1", "--parent-start-ms", "x" }, "Invalid --parent-start-ms")]
    [InlineData(new[] { "--ucb-roslyn-host", "abc", "--roslyn-dir" }, "Missing value")]
    [InlineData(new[] { "--ucb-roslyn-host", "abc", "--roslyn-dir", "/r", "--parent-pid", "1", "--bogus", "1" }, "Unknown option")]
    public void Arguments_RejectInvalidCommandLines(string[] args, string expected)
    {
        Assert.False(RoslynHostArguments.TryParse(args, out _, out string? error));
        Assert.Contains(expected, error);
    }

    [Theory]
    [InlineData("/U/dotnet exec /c/UcbRoslynHost.dll --ucb-roslyn-host 0123456789ab --roslyn-dir /r --parent-pid 9", "0123456789ab", true)]
    [InlineData("\"C:\\U\\dotnet.exe\" exec \"C:\\c\\UcbRoslynHost.dll\" --ucb-roslyn-host 0123456789ab --roslyn-dir \"C:\\r\"", "0123456789ab", true)]
    [InlineData("/U/dotnet exec /c/UcbRoslynHost.dll --ucb-roslyn-host 0123456789ab", "ffffffffffff", false)]
    [InlineData("/U/dotnet exec csc.dll --ucb-roslyn-host", "0123456789ab", false)]
    [InlineData("/bin/sleep 0123456789ab", "0123456789ab", false)]
    public void CommandLineHasMarker_MatchesOnlyTheMarkerAndItsHash(string commandLine, string hash, bool expected)
    {
        Assert.Equal(expected, RoslynHostArguments.CommandLineHasMarker(commandLine, hash));
    }
}
