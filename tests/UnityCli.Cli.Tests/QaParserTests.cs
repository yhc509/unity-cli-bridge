using System.Text.Json;
using UnityCli.Cli.Models;
using UnityCli.Cli.Services;
using UnityCli.Protocol;

namespace UnityCli.Cli.Tests;

public sealed class QaParserTests
{
    [Fact]
    public void Parse_QaClick_WithQaId()
    {
        var parsed = CliArgumentParser.Parse(["qa", "click", "--qa-id", "test-btn"]);

        Assert.Equal(CommandKind.QaClick, parsed.Kind);
        Assert.Equal("test-btn", parsed.QaId);
        Assert.Null(parsed.QaTarget);
    }

    [Fact]
    public void Parse_QaClick_WithTarget()
    {
        var parsed = CliArgumentParser.Parse(["qa", "click", "--target", "Canvas/Panel/Button"]);

        Assert.Equal(CommandKind.QaClick, parsed.Kind);
        Assert.Null(parsed.QaId);
        Assert.Equal("Canvas/Panel/Button", parsed.QaTarget);
    }

    [Fact]
    public void Parse_QaClick_UsesDefaultLeftButton()
    {
        var parsed = CliArgumentParser.Parse(["qa", "click", "--qa-id", "test-btn"]);

        var arguments = ParseArguments(parsed.ToEnvelope().argumentsJson);

        Assert.Equal("left", parsed.QaButton);
        Assert.Equal("left", arguments.GetProperty("button").GetString());
    }

    [Fact]
    public void Parse_QaClick_AcceptsRightButton()
    {
        var parsed = CliArgumentParser.Parse(["qa", "click", "--qa-id", "test-btn", "--button", "right"]);

        var arguments = ParseArguments(parsed.ToEnvelope().argumentsJson);

        Assert.Equal("right", parsed.QaButton);
        Assert.Equal("right", arguments.GetProperty("button").GetString());
    }

    [Fact]
    public void Parse_QaClick_WithInvalidButton_ThrowsUsage()
    {
        var ex = Assert.Throws<CliUsageException>(() => CliArgumentParser.Parse([
            "qa", "click",
            "--qa-id", "test-btn",
            "--button", "middle"
        ]));

        Assert.Contains("--button", ex.Message);
        Assert.Contains("left", ex.Message);
        Assert.Contains("right", ex.Message);
    }

    [Fact]
    public void Parse_QaTap_AcceptsCoordinates()
    {
        var parsed = CliArgumentParser.Parse(["qa", "tap", "--x", "100", "--y", "200"]);

        Assert.Equal(CommandKind.QaTap, parsed.Kind);
        Assert.Equal(100, parsed.QaTapX);
        Assert.Equal(200, parsed.QaTapY);
    }

    [Fact]
    public void Parse_QaTap_UsesDefaultLeftButton()
    {
        var parsed = CliArgumentParser.Parse(["qa", "tap", "--x", "100", "--y", "200"]);

        var arguments = ParseArguments(parsed.ToEnvelope().argumentsJson);

        Assert.Equal("left", parsed.QaButton);
        Assert.Equal("left", arguments.GetProperty("button").GetString());
    }

    [Fact]
    public void Parse_QaTap_AcceptsRightButton()
    {
        var parsed = CliArgumentParser.Parse(["qa", "tap", "--x", "100", "--y", "200", "--button", "right"]);

        var arguments = ParseArguments(parsed.ToEnvelope().argumentsJson);

        Assert.Equal("right", parsed.QaButton);
        Assert.Equal("right", arguments.GetProperty("button").GetString());
    }

    [Fact]
    public void Parse_QaTap_WithInvalidButton_ThrowsUsage()
    {
        var ex = Assert.Throws<CliUsageException>(() => CliArgumentParser.Parse([
            "qa", "tap",
            "--x", "100",
            "--y", "200",
            "--button", "middle"
        ]));

        Assert.Contains("--button", ex.Message);
        Assert.Contains("left", ex.Message);
        Assert.Contains("right", ex.Message);
    }

    [Fact]
    public void Parse_QaTap_AcceptsScreenshotDimensions()
    {
        var parsed = CliArgumentParser.Parse([
            "qa", "tap",
            "--x", "100",
            "--y", "200",
            "--screenshot-width", "1920",
            "--screenshot-height", "1080"
        ]);

        Assert.Equal(CommandKind.QaTap, parsed.Kind);
        Assert.Equal(1920, parsed.QaScreenshotWidth);
        Assert.Equal(1080, parsed.QaScreenshotHeight);
    }

    [Fact]
    public void Parse_QaUiDump_AcceptsScreenshotDimensions()
    {
        var parsed = CliArgumentParser.Parse([
            "qa", "ui-dump",
            "--screenshot-width", "800",
            "--screenshot-height", "600"
        ]);

        Assert.Equal(CommandKind.QaUiDump, parsed.Kind);
        Assert.Equal(800, parsed.QaScreenshotWidth);
        Assert.Equal(600, parsed.QaScreenshotHeight);
    }

    [Fact]
    public void Parse_QaUiDump_DefaultsToUnfilteredFullRect()
    {
        var parsed = CliArgumentParser.Parse(["qa", "ui-dump"]);
        var args = ProtocolJson.Deserialize<QaUiDumpArgs>(parsed.ToEnvelope().argumentsJson);

        Assert.Equal(CommandKind.QaUiDump, parsed.Kind);
        Assert.NotNull(args);
        Assert.Equal(0, args!.limit);
        Assert.False(args.interactableOnly);
        Assert.Equal(string.Empty, args.text);
        Assert.False(args.omitRect);
    }

    [Fact]
    public void Parse_QaUiDump_AcceptsTrimFilters()
    {
        var parsed = CliArgumentParser.Parse([
            "qa", "ui-dump",
            "--limit", "20",
            "--interactable-only",
            "--text", "Start",
            "--omit-rect"
        ]);
        var args = ProtocolJson.Deserialize<QaUiDumpArgs>(parsed.ToEnvelope().argumentsJson);

        Assert.Equal(CommandKind.QaUiDump, parsed.Kind);
        Assert.Equal(20, parsed.QaDumpLimit);
        Assert.True(parsed.QaDumpInteractableOnly);
        Assert.Equal("Start", parsed.QaDumpText);
        Assert.True(parsed.QaDumpOmitRect);
        Assert.NotNull(args);
        Assert.Equal(20, args!.limit);
        Assert.True(args.interactableOnly);
        Assert.Equal("Start", args.text);
        Assert.True(args.omitRect);
    }

    [Fact]
    public void Parse_QaSwipe_AcceptsDuration()
    {
        var parsed = CliArgumentParser.Parse(["qa", "swipe", "--from", "100,200", "--to", "300,400", "--duration", "500"]);

        Assert.Equal(CommandKind.QaSwipe, parsed.Kind);
        Assert.Equal("100,200", parsed.QaSwipeFrom);
        Assert.Equal("300,400", parsed.QaSwipeTo);
        Assert.Equal(500, parsed.QaSwipeDuration);
    }

    [Fact]
    public void Parse_QaSwipe_WithTarget_AcceptsDuration()
    {
        var parsed = CliArgumentParser.Parse(["qa", "swipe", "--target", "/Canvas/Slider", "--from", "0,0", "--to", "100,0", "--duration", "500"]);

        Assert.Equal(CommandKind.QaSwipe, parsed.Kind);
        Assert.Equal("/Canvas/Slider", parsed.QaTarget);
        Assert.Equal("0,0", parsed.QaSwipeFrom);
        Assert.Equal("100,0", parsed.QaSwipeTo);
        Assert.Equal(500, parsed.QaSwipeDuration);
    }

    [Fact]
    public void Parse_QaSwipe_AcceptsScreenshotDimensions()
    {
        var parsed = CliArgumentParser.Parse([
            "qa", "swipe",
            "--from", "100,200",
            "--to", "300,400",
            "--screenshot-width", "1920",
            "--screenshot-height", "1080"
        ]);

        Assert.Equal(CommandKind.QaSwipe, parsed.Kind);
        Assert.Equal(1920, parsed.QaScreenshotWidth);
        Assert.Equal(1080, parsed.QaScreenshotHeight);
    }

    [Fact]
    public void Parse_QaSwipe_UsesDefaultDuration()
    {
        var parsed = CliArgumentParser.Parse(["qa", "swipe", "--from", "100,200", "--to", "300,400"]);

        Assert.Equal(CommandKind.QaSwipe, parsed.Kind);
        Assert.Equal(ProtocolConstants.DefaultQaSwipeDurationMs, parsed.QaSwipeDuration);
    }

    [Fact]
    public void Parse_QaSwipe_UsesDefaultLeftButton()
    {
        var parsed = CliArgumentParser.Parse(["qa", "swipe", "--from", "100,200", "--to", "300,400"]);

        var arguments = ParseArguments(parsed.ToEnvelope().argumentsJson);

        Assert.Equal("left", parsed.QaButton);
        Assert.Equal("left", arguments.GetProperty("button").GetString());
    }

    [Fact]
    public void Parse_QaSwipe_AcceptsRightButton()
    {
        var parsed = CliArgumentParser.Parse(["qa", "swipe", "--from", "100,200", "--to", "300,400", "--button", "right"]);

        var arguments = ParseArguments(parsed.ToEnvelope().argumentsJson);

        Assert.Equal("right", parsed.QaButton);
        Assert.Equal("right", arguments.GetProperty("button").GetString());
    }

    [Fact]
    public void Parse_QaSwipe_WithInvalidButton_ThrowsUsage()
    {
        var ex = Assert.Throws<CliUsageException>(() => CliArgumentParser.Parse([
            "qa", "swipe",
            "--from", "100,200",
            "--to", "300,400",
            "--button", "middle"
        ]));

        Assert.Contains("--button", ex.Message);
        Assert.Contains("left", ex.Message);
        Assert.Contains("right", ex.Message);
    }

    [Fact]
    public void Parse_QaKey_AcceptsKeyName()
    {
        var parsed = CliArgumentParser.Parse(["qa", "key", "--key", "Space"]);

        Assert.Equal(CommandKind.QaKey, parsed.Kind);
        Assert.Equal("Space", parsed.QaKeyName);
    }

    [Fact]
    public void Parse_QaWait_AcceptsMilliseconds()
    {
        var parsed = CliArgumentParser.Parse(["qa", "wait", "--ms", "2000"]);

        Assert.Equal(CommandKind.QaWait, parsed.Kind);
        Assert.Equal(2000, parsed.QaWaitMs);
    }

    [Fact]
    public void Parse_QaWaitUntil_AcceptsSceneAndTimeout()
    {
        var parsed = CliArgumentParser.Parse(["qa", "wait-until", "--scene", "GameScene", "--timeout", "5000"]);

        Assert.Equal(CommandKind.QaWaitUntil, parsed.Kind);
        Assert.Equal("GameScene", parsed.QaWaitScene);
        Assert.Equal(5000, parsed.QaWaitTimeout);
    }

    [Fact]
    public void Parse_QaWaitUntil_AcceptsLogContains()
    {
        var parsed = CliArgumentParser.Parse(["qa", "wait-until", "--log-contains", "Loading"]);

        Assert.Equal(CommandKind.QaWaitUntil, parsed.Kind);
        Assert.Equal("Loading", parsed.QaWaitLogContains);
    }

    [Fact]
    public void Parse_QaWaitUntil_AcceptsObjectExists()
    {
        var parsed = CliArgumentParser.Parse(["qa", "wait-until", "--object-exists", "start-btn"]);

        Assert.Equal(CommandKind.QaWaitUntil, parsed.Kind);
        Assert.Equal("start-btn", parsed.QaWaitObjectExists);
    }

    [Fact]
    public void Parse_QaWaitUntil_AcceptsObjectInteractable()
    {
        var parsed = CliArgumentParser.Parse(["qa", "wait-until", "--object-interactable", "start-btn"]);

        Assert.Equal(CommandKind.QaWaitUntil, parsed.Kind);
        Assert.Equal("start-btn", parsed.QaWaitObjectInteractable);
    }

    [Fact]
    public void Parse_QaWaitUntil_AcceptsObjectGone()
    {
        var parsed = CliArgumentParser.Parse(["qa", "wait-until", "--object-gone", "loading-spinner"]);

        Assert.Equal(CommandKind.QaWaitUntil, parsed.Kind);
        Assert.Equal("loading-spinner", parsed.QaWaitObjectGone);
    }

    [Fact]
    public void Parse_Qa_WithoutSubcommand_ThrowsUsage()
    {
        Assert.Throws<CliUsageException>(() => CliArgumentParser.Parse(["qa"]));
    }

    [Fact]
    public void Parse_Qa_InvalidSubcommand_ThrowsUsage()
    {
        var ex = Assert.Throws<CliUsageException>(() => CliArgumentParser.Parse(["qa", "invalid"]));

        Assert.Contains("invalid", ex.Message);
    }

    [Fact]
    public void Parse_QaClick_WithQaIdAndTarget_ThrowsUsage()
    {
        var ex = Assert.Throws<CliUsageException>(() => CliArgumentParser.Parse([
            "qa", "click",
            "--qa-id", "test-btn",
            "--target", "Canvas/Panel/Button"
        ]));

        Assert.Contains("--qa-id", ex.Message);
        Assert.Contains("--target", ex.Message);
    }

    [Fact]
    public void Parse_QaTap_WithNonIntegerCoordinate_ThrowsUsage()
    {
        var ex = Assert.Throws<CliUsageException>(() => CliArgumentParser.Parse([
            "qa", "tap",
            "--x", "left",
            "--y", "200"
        ]));

        Assert.Contains("--x", ex.Message);
        Assert.Contains("an integer", ex.Message);
    }

    [Fact]
    public void Parse_QaTap_WithOnlyScreenshotWidth_ThrowsUsage()
    {
        var ex = Assert.Throws<CliUsageException>(() => CliArgumentParser.Parse([
            "qa", "tap",
            "--x", "100",
            "--y", "200",
            "--screenshot-width", "1920"
        ]));

        Assert.Contains("--screenshot-width", ex.Message);
        Assert.Contains("--screenshot-height", ex.Message);
    }

    [Fact]
    public void Parse_QaUiDump_WithOnlyScreenshotWidth_ThrowsUsage()
    {
        var ex = Assert.Throws<CliUsageException>(() => CliArgumentParser.Parse([
            "qa", "ui-dump",
            "--screenshot-width", "800"
        ]));

        Assert.Contains("--screenshot-width", ex.Message);
        Assert.Contains("--screenshot-height", ex.Message);
    }

    [Fact]
    public void Parse_QaSwipe_WithInvalidCoordinate_ThrowsUsage()
    {
        var ex = Assert.Throws<CliUsageException>(() => CliArgumentParser.Parse([
            "qa", "swipe",
            "--from", "100,down",
            "--to", "300,400"
        ]));

        Assert.Contains("--from", ex.Message);
        Assert.Contains("absolute screen pixel coordinates", ex.Message);
    }

    [Fact]
    public void Parse_QaSwipe_WithTargetAndInvalidCoordinate_ThrowsTargetRelativeUsage()
    {
        var ex = Assert.Throws<CliUsageException>(() => CliArgumentParser.Parse([
            "qa", "swipe",
            "--target", "/Canvas/Slider",
            "--from", "100,down",
            "--to", "300,400"
        ]));

        Assert.Contains("--from", ex.Message);
        Assert.Contains("pixel offset from the target center", ex.Message);
    }

    [Fact]
    public void Parse_QaSwipe_WithOnlyScreenshotHeight_ThrowsUsage()
    {
        var ex = Assert.Throws<CliUsageException>(() => CliArgumentParser.Parse([
            "qa", "swipe",
            "--from", "100,200",
            "--to", "300,400",
            "--screenshot-height", "1080"
        ]));

        Assert.Contains("--screenshot-width", ex.Message);
        Assert.Contains("--screenshot-height", ex.Message);
    }

    [Fact]
    public void Parse_QaWaitUntil_WithoutCondition_ThrowsUsage()
    {
        var ex = Assert.Throws<CliUsageException>(() => CliArgumentParser.Parse(["qa", "wait-until"]));

        Assert.Contains("at least one", ex.Message);
    }

    [Fact]
    public void Parse_QaWaitUntil_WithQaIdAndObjectExists_ThrowsUsage()
    {
        var ex = Assert.Throws<CliUsageException>(() => CliArgumentParser.Parse([
            "qa", "wait-until",
            "--qa-id", "start-btn",
            "--object-exists", "/Canvas/Panel/Button"
        ]));

        Assert.Contains("--qa-id", ex.Message);
        Assert.Contains("--object-exists", ex.Message);
    }

    [Fact]
    public void Parse_QaWaitUntil_WithSameObjectGoneAndObjectExists_ThrowsUsage()
    {
        var ex = Assert.Throws<CliUsageException>(() => CliArgumentParser.Parse([
            "qa", "wait-until",
            "--object-gone", "Foo",
            "--object-exists", "Foo"
        ]));

        Assert.Contains("--object-gone", ex.Message);
        Assert.Contains("--object-exists", ex.Message);
    }

    [Fact]
    public void Parse_QaWaitUntil_WithSameObjectGoneAndObjectInteractable_ThrowsUsage()
    {
        var ex = Assert.Throws<CliUsageException>(() => CliArgumentParser.Parse([
            "qa", "wait-until",
            "--object-gone", "Foo",
            "--object-interactable", "Foo"
        ]));

        Assert.Contains("--object-gone", ex.Message);
        Assert.Contains("--object-interactable", ex.Message);
    }

    [Fact]
    public void Parse_QaWaitUntil_WithDifferentObjectGoneAndObjectExists_Succeeds()
    {
        var parsed = CliArgumentParser.Parse([
            "qa", "wait-until",
            "--object-gone", "A",
            "--object-exists", "B"
        ]);

        Assert.Equal(CommandKind.QaWaitUntil, parsed.Kind);
        Assert.Equal("A", parsed.QaWaitObjectGone);
        Assert.Equal("B", parsed.QaWaitObjectExists);
    }

    [Fact]
    public void Parse_QaClick_ToEnvelope_UsesQaClickCommand()
    {
        var parsed = CliArgumentParser.Parse(["qa", "click", "--qa-id", "test-btn"]);

        var envelope = parsed.ToEnvelope();
        var arguments = ParseArguments(envelope.argumentsJson);

        Assert.Equal(ProtocolConstants.CommandQaClick, envelope.command);
        Assert.Equal("test-btn", arguments.GetProperty("qaId").GetString());
    }

    [Fact]
    public void Parse_QaTap_ToEnvelope_UsesQaTapCommand()
    {
        var parsed = CliArgumentParser.Parse(["qa", "tap", "--x", "100", "--y", "200"]);

        var envelope = parsed.ToEnvelope();
        var arguments = ParseArguments(envelope.argumentsJson);

        Assert.Equal(ProtocolConstants.CommandQaTap, envelope.command);
        Assert.Equal(100, arguments.GetProperty("x").GetInt32());
        Assert.Equal(200, arguments.GetProperty("y").GetInt32());
    }

    [Fact]
    public void Parse_QaTap_ToEnvelope_IncludesScreenshotDimensions()
    {
        var parsed = CliArgumentParser.Parse([
            "qa", "tap",
            "--x", "100",
            "--y", "200",
            "--screenshot-width", "1920",
            "--screenshot-height", "1080"
        ]);

        var envelope = parsed.ToEnvelope();
        var arguments = ParseArguments(envelope.argumentsJson);

        Assert.Equal(1920, arguments.GetProperty("screenshotWidth").GetInt32());
        Assert.Equal(1080, arguments.GetProperty("screenshotHeight").GetInt32());
    }

    [Fact]
    public void Parse_QaUiDump_ToEnvelope_UsesQaUiDumpCommand()
    {
        var parsed = CliArgumentParser.Parse([
            "qa", "ui-dump",
            "--screenshot-width", "800",
            "--screenshot-height", "600"
        ]);

        var envelope = parsed.ToEnvelope();
        var arguments = ParseArguments(envelope.argumentsJson);

        Assert.Equal(ProtocolConstants.CommandQaUiDump, envelope.command);
        Assert.Equal(800, arguments.GetProperty("screenshotWidth").GetInt32());
        Assert.Equal(600, arguments.GetProperty("screenshotHeight").GetInt32());
    }

    [Fact]
    public void Parse_QaSwipe_ToEnvelope_UsesQaSwipeCommand()
    {
        var parsed = CliArgumentParser.Parse(["qa", "swipe", "--from", "100,200", "--to", "300,400", "--duration", "500"]);

        var envelope = parsed.ToEnvelope();
        var arguments = ParseArguments(envelope.argumentsJson);

        Assert.Equal(ProtocolConstants.CommandQaSwipe, envelope.command);
        Assert.Equal(100, arguments.GetProperty("fromX").GetInt32());
        Assert.Equal(200, arguments.GetProperty("fromY").GetInt32());
        Assert.Equal(300, arguments.GetProperty("toX").GetInt32());
        Assert.Equal(400, arguments.GetProperty("toY").GetInt32());
        Assert.Equal(500, arguments.GetProperty("durationMs").GetInt32());
    }

    [Fact]
    public void Parse_QaSwipe_ToEnvelope_IncludesScreenshotDimensions()
    {
        var parsed = CliArgumentParser.Parse([
            "qa", "swipe",
            "--from", "100,200",
            "--to", "300,400",
            "--screenshot-width", "1920",
            "--screenshot-height", "1080"
        ]);

        var envelope = parsed.ToEnvelope();
        var arguments = ParseArguments(envelope.argumentsJson);

        Assert.Equal(1920, arguments.GetProperty("screenshotWidth").GetInt32());
        Assert.Equal(1080, arguments.GetProperty("screenshotHeight").GetInt32());
    }

    [Fact]
    public void Parse_QaKey_ToEnvelope_UsesQaKeyCommand()
    {
        var parsed = CliArgumentParser.Parse(["qa", "key", "--key", "Space"]);

        var envelope = parsed.ToEnvelope();
        var arguments = ParseArguments(envelope.argumentsJson);

        Assert.Equal(ProtocolConstants.CommandQaKey, envelope.command);
        Assert.Equal("Space", arguments.GetProperty("key").GetString());
    }

    [Fact]
    public void Parse_QaWaitUntil_ToEnvelope_UsesQaWaitUntilCommand()
    {
        var parsed = CliArgumentParser.Parse([
            "qa", "wait-until",
            "--scene", "GameScene",
            "--log-contains", "Loading",
            "--object-exists", "start-btn",
            "--object-interactable", "ready-btn",
            "--object-gone", "loading-spinner",
            "--timeout", "5000"
        ]);

        var envelope = parsed.ToEnvelope();
        var arguments = ParseArguments(envelope.argumentsJson);

        Assert.Equal(ProtocolConstants.CommandQaWaitUntil, envelope.command);
        Assert.Equal("GameScene", arguments.GetProperty("scene").GetString());
        Assert.Equal("Loading", arguments.GetProperty("logContains").GetString());
        Assert.Equal("start-btn", arguments.GetProperty("objectExists").GetString());
        Assert.Equal("ready-btn", arguments.GetProperty("objectInteractable").GetString());
        Assert.Equal("loading-spinner", arguments.GetProperty("objectGone").GetString());
        Assert.Equal(5000, arguments.GetProperty("timeoutMs").GetInt32());
    }

    [Fact]
    public void ToEnvelope_QaSwipe_WithInvalidCoordinate_ThrowsUsage()
    {
        var parsed = new ParsedCommand(CommandKind.QaSwipe)
        {
            QaSwipeFrom = "100,down",
            QaSwipeTo = "300,400",
        };

        var ex = Assert.Throws<CliUsageException>(() => parsed.ToEnvelope());

        Assert.Contains("--from", ex.Message);
        Assert.Contains("absolute screen pixel coordinates", ex.Message);
    }

    [Fact]
    public void ToEnvelope_QaSwipe_WithTargetAndInvalidCoordinate_ThrowsTargetRelativeUsage()
    {
        var parsed = new ParsedCommand(CommandKind.QaSwipe)
        {
            QaTarget = "/Canvas/Slider",
            QaSwipeFrom = "100,down",
            QaSwipeTo = "300,400",
        };

        var ex = Assert.Throws<CliUsageException>(() => parsed.ToEnvelope());

        Assert.Contains("--from", ex.Message);
        Assert.Contains("pixel offset from the target center", ex.Message);
    }

    [Fact]
    public void BuildHelpText_IncludesQaCommands()
    {
        var helpText = CliArgumentParser.BuildHelpText();

        Assert.Contains("qa click", helpText);
        Assert.Contains("qa tap", helpText);
        Assert.Contains("qa swipe [--target <path>] --from <x,y> --to <x,y> [--duration <ms>] [--button left|right] [--screenshot-width <int> --screenshot-height <int>]", helpText);
        Assert.Contains("qa key", helpText);
        Assert.Contains("qa ui-dump", helpText);
        Assert.Contains("qa world-dump", helpText);
        Assert.Contains("qa run-sequence", helpText);
        Assert.Contains("qa wait --ms <int>", helpText);
        Assert.Contains("qa wait-until", helpText);
        Assert.Contains("qa tap and coordinate-based qa swipe treat coordinates as screenshot pixels with a top-left origin", helpText);
        Assert.Contains("last captured screenshot size", helpText);
        Assert.Contains("qa swipe --target still treats --from/--to as pixel offsets from the target RectTransform center.", helpText);
    }

    [Fact]
    public void QaSwipe_CommandCatalog_DescribesAutoDetectedScreenshotScalingAndTargetRelativeOffsets()
    {
        CliCommandDescriptor? descriptor = CliCommandCatalog.FindByCommand("qa swipe");

        Assert.NotNull(descriptor);
        Assert.Contains("--target <path>", descriptor!.Synopsis);
        Assert.Contains("--screenshot-width <int> --screenshot-height <int>", descriptor.Synopsis);
        Assert.Contains("top-origin coordinates", descriptor.Summary);
        Assert.Contains("last captured screenshot", descriptor.Summary);
        Assert.Contains("pixel offsets from the target RectTransform center", descriptor.Summary);
        Assert.Contains("multiple frames", descriptor.Summary);
    }

    [Fact]
    public void QaUiDump_CommandCatalog_DescribesClickableUiDump()
    {
        CliCommandDescriptor? descriptor = CliCommandCatalog.FindByCommand("qa ui-dump");

        Assert.NotNull(descriptor);
        Assert.Contains("--screenshot-width <int> --screenshot-height <int>", descriptor!.Synopsis);
        Assert.Contains("clickable UI elements", descriptor.Summary);
        Assert.Contains("centerX/centerY", descriptor.Summary);
        Assert.Equal(ProtocolConstants.CommandQaUiDump, descriptor.ProtocolCommand);
    }

    [Fact]
    public void Parse_QaWorldDump_AcceptsScreenshotDimensions()
    {
        var parsed = CliArgumentParser.Parse([
            "qa", "world-dump",
            "--screenshot-width", "800",
            "--screenshot-height", "600"
        ]);

        Assert.Equal(CommandKind.QaWorldDump, parsed.Kind);
        Assert.Equal(800, parsed.QaScreenshotWidth);
        Assert.Equal(600, parsed.QaScreenshotHeight);
        Assert.False(parsed.QaWorldDumpIncludeOffscreen);
    }

    [Fact]
    public void Parse_QaWorldDump_AcceptsIncludeOffscreen()
    {
        var parsed = CliArgumentParser.Parse(["qa", "world-dump", "--include-offscreen"]);

        Assert.Equal(CommandKind.QaWorldDump, parsed.Kind);
        Assert.True(parsed.QaWorldDumpIncludeOffscreen);
    }

    [Fact]
    public void Parse_QaWorldDump_DefaultsToUnfiltered()
    {
        var parsed = CliArgumentParser.Parse(["qa", "world-dump"]);
        var args = ProtocolJson.Deserialize<QaWorldDumpArgs>(parsed.ToEnvelope().argumentsJson);

        Assert.Equal(CommandKind.QaWorldDump, parsed.Kind);
        Assert.NotNull(args);
        Assert.Equal(0, args!.limit);
        Assert.Equal(string.Empty, args.text);
        Assert.False(args.includeOffscreen);
    }

    [Fact]
    public void Parse_QaWorldDump_AcceptsTrimFilters()
    {
        var parsed = CliArgumentParser.Parse([
            "qa", "world-dump",
            "--include-offscreen",
            "--limit", "5",
            "--text", "Enemy"
        ]);
        var args = ProtocolJson.Deserialize<QaWorldDumpArgs>(parsed.ToEnvelope().argumentsJson);

        Assert.Equal(CommandKind.QaWorldDump, parsed.Kind);
        Assert.True(parsed.QaWorldDumpIncludeOffscreen);
        Assert.Equal(5, parsed.QaDumpLimit);
        Assert.Equal("Enemy", parsed.QaDumpText);
        Assert.NotNull(args);
        Assert.True(args!.includeOffscreen);
        Assert.Equal(5, args.limit);
        Assert.Equal("Enemy", args.text);
    }

    [Fact]
    public void Parse_QaWorldDump_WithOnlyScreenshotWidth_ThrowsUsage()
    {
        var ex = Assert.Throws<CliUsageException>(() => CliArgumentParser.Parse([
            "qa", "world-dump",
            "--screenshot-width", "800"
        ]));

        Assert.Contains("--screenshot-width", ex.Message);
        Assert.Contains("--screenshot-height", ex.Message);
    }

    [Fact]
    public void Parse_QaWorldDump_ToEnvelope_UsesQaWorldDumpCommand()
    {
        var parsed = CliArgumentParser.Parse(["qa", "world-dump", "--include-offscreen"]);

        var envelope = parsed.ToEnvelope();
        var arguments = ParseArguments(envelope.argumentsJson);

        Assert.Equal(ProtocolConstants.CommandQaWorldDump, envelope.command);
        Assert.True(arguments.GetProperty("includeOffscreen").GetBoolean());
    }

    [Fact]
    public void Parse_QaTap_WithTarget()
    {
        var parsed = CliArgumentParser.Parse(["qa", "tap", "--target", "/Battle/Units/Unit_Erich"]);

        Assert.Equal(CommandKind.QaTap, parsed.Kind);
        Assert.Equal("/Battle/Units/Unit_Erich", parsed.QaTarget);
        Assert.Null(parsed.QaTapX);
        Assert.Null(parsed.QaTapY);
    }

    [Fact]
    public void Parse_QaTap_WithTarget_ToEnvelope_IncludesTarget()
    {
        var parsed = CliArgumentParser.Parse(["qa", "tap", "--target", "/Battle/Units/Unit_Erich"]);

        var envelope = parsed.ToEnvelope();
        var arguments = ParseArguments(envelope.argumentsJson);

        Assert.Equal(ProtocolConstants.CommandQaTap, envelope.command);
        Assert.Equal("/Battle/Units/Unit_Erich", arguments.GetProperty("target").GetString());
    }

    [Fact]
    public void Parse_QaTap_WithTargetAndCoordinates_ThrowsUsage()
    {
        var ex = Assert.Throws<CliUsageException>(() => CliArgumentParser.Parse([
            "qa", "tap",
            "--target", "/Battle/Units/Unit_Erich",
            "--x", "100",
            "--y", "200"
        ]));

        Assert.Contains("--target", ex.Message);
    }

    [Fact]
    public void Parse_QaTap_WithNeitherTargetNorCoordinates_ThrowsUsage()
    {
        var ex = Assert.Throws<CliUsageException>(() => CliArgumentParser.Parse(["qa", "tap"]));

        Assert.Contains("--x", ex.Message);
    }

    [Fact]
    public void QaWorldDump_CommandCatalog_DescribesWorldTappables()
    {
        CliCommandDescriptor? descriptor = CliCommandCatalog.FindByCommand("qa world-dump");

        Assert.NotNull(descriptor);
        Assert.Contains("--include-offscreen", descriptor!.Synopsis);
        Assert.Contains("IQaTappable", descriptor.Summary);
        Assert.Contains("qa tap --target", descriptor.Summary);
        Assert.Equal(ProtocolConstants.CommandQaWorldDump, descriptor.ProtocolCommand);
    }

    private const string MinimalSequenceSpec =
        """{ "steps": [ { "wait": [ { "target": "/X", "active": true } ], "actions": [ { "key": "space" } ] } ] }""";

    [Fact]
    public void Parse_QaRunSequence_BuildsWireArgs()
    {
        var parsed = CliArgumentParser.Parse(["qa", "run-sequence", "--spec-json", MinimalSequenceSpec]);

        Assert.Equal(CommandKind.QaRunSequence, parsed.Kind);
        Assert.NotNull(parsed.QaSequenceArgs);
        Assert.Single(parsed.QaSequenceArgs!.steps);
    }

    [Fact]
    public void Parse_QaRunSequence_UsesSpecTimeoutForIpcTimeout()
    {
        const string spec = """
        { "timeoutMs": 40000, "steps": [ { "wait": [ { "target": "/X", "active": true } ], "actions": [ { "key": "space" } ] } ] }
        """;

        var parsed = CliArgumentParser.Parse(["qa", "run-sequence", "--spec-json", spec]);

        Assert.Equal(45_000, parsed.TimeoutMs);
    }

    [Fact]
    public void Parse_QaRunSequence_CliTimeoutOverridesSpecTimeoutForIpcTimeout()
    {
        const string spec = """
        { "timeoutMs": 40000, "steps": [ { "wait": [ { "target": "/X", "active": true } ], "actions": [ { "key": "space" } ] } ] }
        """;

        var parsed = CliArgumentParser.Parse(["qa", "run-sequence", "--spec-json", spec, "--timeout", "60000"]);

        Assert.Equal(65_000, parsed.TimeoutMs);
    }

    [Fact]
    public void Parse_QaRunSequence_ToEnvelope_UsesRunSequenceCommand()
    {
        var parsed = CliArgumentParser.Parse(["qa", "run-sequence", "--spec-json", MinimalSequenceSpec]);
        var envelope = parsed.ToEnvelope();
        var arguments = ParseArguments(envelope.argumentsJson);

        Assert.Equal(ProtocolConstants.CommandQaRunSequence, envelope.command);
        Assert.Equal("active", arguments.GetProperty("steps")[0].GetProperty("wait")[0].GetProperty("kind").GetString());
    }

    [Fact]
    public void Parse_QaRunSequence_RecordOptions_BuildsWireArgs()
    {
        var parsed = CliArgumentParser.Parse([
            "qa", "run-sequence",
            "--spec-json", MinimalSequenceSpec,
            "--record",
            "--record-path", "out.mp4",
        ]);
        var envelope = parsed.ToEnvelope();
        var arguments = ParseArguments(envelope.argumentsJson);

        Assert.True(parsed.QaSequenceRecord);
        Assert.Equal("out.mp4", parsed.QaSequenceRecordPath);
        Assert.True(arguments.GetProperty("record").GetBoolean());
        Assert.Equal("out.mp4", arguments.GetProperty("recordPath").GetString());
    }

    [Fact]
    public void Parse_QaRunSequence_RecordPath_ImpliesRecord()
    {
        var parsed = CliArgumentParser.Parse([
            "qa", "run-sequence",
            "--spec-json", MinimalSequenceSpec,
            "--record-path", "out.mp4",
        ]);
        var envelope = parsed.ToEnvelope();
        var arguments = ParseArguments(envelope.argumentsJson);

        Assert.True(parsed.QaSequenceRecord);
        Assert.Equal("out.mp4", parsed.QaSequenceRecordPath);
        Assert.True(arguments.GetProperty("record").GetBoolean());
        Assert.Equal("out.mp4", arguments.GetProperty("recordPath").GetString());
    }

    [Fact]
    public void Parse_QaRunSequence_MissingSpec_ThrowsUsage()
    {
        var ex = Assert.Throws<CliUsageException>(() => CliArgumentParser.Parse(["qa", "run-sequence"]));
        Assert.Contains("--spec-json", ex.Message);
    }

    [Fact]
    public void Parse_QaRunSequence_SpecJsonFileReadError_ThrowsUsage()
    {
        using var temp = new TempDirectory();
        string missingPath = Path.Combine(temp.Path, "missing.json");

        var ex = Assert.Throws<CliUsageException>(() =>
            CliArgumentParser.Parse(["qa", "run-sequence", "--spec-json", $"@{missingPath}"]));

        Assert.Contains("--spec-json", ex.Message);
        Assert.Contains("Failed to read", ex.Message);
    }

    [Fact]
    public void Parse_QaRunSequence_TimeoutOverMax_ThrowsUsage()
    {
        var ex = Assert.Throws<CliUsageException>(() => CliArgumentParser.Parse([
            "qa", "run-sequence", "--spec-json", MinimalSequenceSpec, "--timeout", "9999999"
        ]));
        Assert.Contains("--timeout", ex.Message);
    }

    [Fact]
    public void QaRunSequence_CommandCatalog_DescribesStepMachine()
    {
        CliCommandDescriptor? descriptor = CliCommandCatalog.FindByCommand("qa run-sequence");
        Assert.NotNull(descriptor);
        Assert.Contains("--spec-json", descriptor!.Synopsis);
        Assert.Contains("--record", descriptor.Synopsis);
        Assert.Contains("IQaQueryable", descriptor.Summary);
        Assert.Equal(ProtocolConstants.CommandQaRunSequence, descriptor.ProtocolCommand);
    }

    private static JsonElement ParseArguments(string argumentsJson)
    {
        using var document = JsonDocument.Parse(argumentsJson);
        return document.RootElement.Clone();
    }
}
