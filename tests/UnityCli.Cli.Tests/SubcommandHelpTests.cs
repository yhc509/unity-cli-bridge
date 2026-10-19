using UnityCli.Cli.Models;
using UnityCli.Cli.Services;
using UnityCli.Protocol;

namespace UnityCli.Cli.Tests;

public sealed class SubcommandHelpTests
{
    [Theory]
    [InlineData(new[] { "qa", "--help" }, "qa")]
    [InlineData(new[] { "qa", "-h" }, "qa")]
    [InlineData(new[] { "qa", "key", "--help" }, "qa key")]
    [InlineData(new[] { "qa", "key", "--key", "Space", "--help" }, "qa key")]
    [InlineData(new[] { "scene", "patch", "--help" }, "scene patch")]
    [InlineData(new[] { "--json", "play", "--help" }, "play")]
    [InlineData(new[] { "help", "qa", "tap" }, "qa tap")]
    [InlineData(new[] { "--help" }, null)]
    [InlineData(new[] { "help" }, null)]
    public void Parse_HelpAnywhere_ReturnsHelpForCommandPath(string[] args, string? topic)
    {
        ParsedCommand parsed = CliArgumentParser.Parse(args);

        Assert.Equal(CommandKind.Help, parsed.Kind);
        Assert.Equal(topic, parsed.HelpTopic);
    }

    [Fact]
    public void Parse_HelpAsOptionValue_IsNotAHelpRequest()
    {
        ParsedCommand parsed = CliArgumentParser.Parse(["execute", "--force", "--code", "--help"]);

        Assert.Equal(CommandKind.ExecuteCode, parsed.Kind);
    }

    [Fact]
    public void Parse_UnknownSubcommandWithHelp_StillReturnsHelp()
    {
        ParsedCommand parsed = CliArgumentParser.Parse(["qa", "bogus", "--help"]);

        Assert.Equal(CommandKind.Help, parsed.Kind);
        Assert.Equal("qa bogus", parsed.HelpTopic);
        string? text = CliCommandMetadata.BuildTopicHelpText(parsed.HelpTopic!);
        Assert.NotNull(text);
        Assert.Contains("qa key", text);
    }

    [Fact]
    public void BuildTopicHelpText_Group_ListsEverySubcommand()
    {
        string? text = CliCommandMetadata.BuildTopicHelpText("qa");

        Assert.NotNull(text);
        foreach (var command in CliCommandCatalog.GetCommands().Where(c => c.Command.StartsWith("qa ", StringComparison.Ordinal)))
        {
            Assert.Contains(command.Synopsis, text);
        }
    }

    [Fact]
    public void BuildTopicHelpText_Leaf_ShowsSynopsisAndSummary()
    {
        CliCommandDescriptor key = CliCommandCatalog.GetCommands().Single(c => c.Command == "qa key");

        string? text = CliCommandMetadata.BuildTopicHelpText("qa key");

        Assert.NotNull(text);
        Assert.Contains("usage: unity-cli", text);
        Assert.Contains(key.Synopsis, text);
        Assert.Contains(key.Summary, text);
        Assert.DoesNotContain("qa tap", text);
    }

    [Fact]
    public void BuildTopicHelpText_EveryCatalogCommand_HasHelp()
    {
        foreach (CliCommandDescriptor command in CliCommandCatalog.GetCommands())
        {
            string? text = CliCommandMetadata.BuildTopicHelpText(command.Command);
            Assert.NotNull(text);
            Assert.Contains(command.Synopsis, text);
        }
    }

    [Fact]
    public void BuildTopicHelpText_Unknown_ReturnsNull()
    {
        Assert.Null(CliCommandMetadata.BuildTopicHelpText("definitely-not-a-command"));
    }

    [Fact]
    public async Task Run_SubcommandHelp_ExitsZeroAndPrintsTopic()
    {
        var writer = new StringWriter();
        TextWriter original = Console.Out;
        Console.SetOut(writer);
        int exitCode;
        try
        {
            exitCode = await CliApp.RunAsync(["qa", "key", "--help"]);
        }
        finally
        {
            Console.SetOut(original);
        }

        Assert.Equal(0, exitCode);
        Assert.Contains("qa key", writer.ToString());
        Assert.DoesNotContain("commands:", writer.ToString());
    }
}
