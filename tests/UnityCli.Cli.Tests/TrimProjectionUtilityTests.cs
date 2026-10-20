using UnityCli.Cli.Services;
using UnityCli.Protocol;

namespace UnityCli.Cli.Tests;

public sealed class TrimProjectionUtilityTests
{
    [Fact]
    public void ApplyUiDumpFilters_DefaultArgsPreserveElementsAndOrder()
    {
        QaUiElement[] elements =
        [
            Ui("Top", interactable: false),
            Ui("Bottom", interactable: true),
        ];

        QaUiElement[] result = QaDumpProjectionUtility.ApplyUiDumpFilters(elements, new QaUiDumpArgs());

        Assert.Same(elements[0], result[0]);
        Assert.Same(elements[1], result[1]);
    }

    [Fact]
    public void ApplyUiDumpFilters_AppliesInteractableTextAndLimitAfterCurrentOrder()
    {
        QaUiElement[] elements =
        [
            Ui("Start Game", interactable: true),
            Ui("Start Disabled", interactable: false),
            Ui("Start Options", interactable: true),
            Ui("Quit", interactable: true),
        ];

        QaUiElement[] result = QaDumpProjectionUtility.ApplyUiDumpFilters(
            elements,
            new QaUiDumpArgs
            {
                interactableOnly = true,
                text = "start",
                limit = 1,
            });

        Assert.Single(result);
        Assert.Equal("Start Game", result[0].text);
    }

    [Fact]
    public void ApplyWorldDumpFilters_DefaultArgsPreserveElementsAndOrder()
    {
        QaWorldElement[] elements =
        [
            World("Enemy A", onScreen: true, hasAction: true),
            World("Enemy B", onScreen: false, hasAction: false),
        ];

        QaWorldElement[] result = QaDumpProjectionUtility.ApplyWorldDumpFilters(elements, new QaWorldDumpArgs());

        Assert.Same(elements[0], result[0]);
        Assert.Same(elements[1], result[1]);
    }

    [Fact]
    public void ApplyWorldDumpFilters_AppliesTextAndLimitAfterCurrentOrder()
    {
        QaWorldElement[] elements =
        [
            World("Enemy A", onScreen: true, hasAction: true),
            World("Enemy B", onScreen: true, hasAction: true),
            World("Chest", onScreen: true, hasAction: true),
        ];

        QaWorldElement[] result = QaDumpProjectionUtility.ApplyWorldDumpFilters(
            elements,
            new QaWorldDumpArgs
            {
                text = "enemy",
                limit = 1,
            });

        Assert.Single(result);
        Assert.Equal("Enemy A", result[0].label);
    }

    [Fact]
    public void WorldProjection_IncludesOnScreenOnlyWhenIncludeOffscreenRequested()
    {
        QaWorldElement[] mixed =
        [
            World("Visible", onScreen: true, hasAction: true),
            World("Hidden", onScreen: false, hasAction: true),
        ];
        QaWorldElement[] singleOffscreen =
        [
            World("Hidden", onScreen: false, hasAction: true),
        ];
        QaWorldElement[] allOffscreen =
        [
            World("Hidden A", onScreen: false, hasAction: true),
            World("Hidden B", onScreen: false, hasAction: true),
        ];

        Assert.False(QaDumpProjectionUtility.ShouldIncludeWorldOnScreenField(mixed, new QaWorldDumpArgs()));
        Assert.True(QaDumpProjectionUtility.ShouldIncludeWorldOnScreenField(
            mixed,
            new QaWorldDumpArgs { includeOffscreen = true }));
        Assert.True(QaDumpProjectionUtility.ShouldIncludeWorldOnScreenField(
            singleOffscreen,
            new QaWorldDumpArgs { includeOffscreen = true }));
        Assert.True(QaDumpProjectionUtility.ShouldIncludeWorldOnScreenField(
            allOffscreen,
            new QaWorldDumpArgs { includeOffscreen = true }));
    }

    [Fact]
    public void WorldProjection_OmitsHasActionWhenAllEntriesAreActionable()
    {
        QaWorldElement[] actionable =
        [
            World("Visible", onScreen: true, hasAction: true),
            World("Also Visible", onScreen: true, hasAction: true),
        ];
        QaWorldElement[] mixed =
        [
            World("Visible", onScreen: true, hasAction: true),
            World("Marker Only", onScreen: true, hasAction: false),
        ];

        Assert.False(QaDumpProjectionUtility.ShouldIncludeWorldHasActionField(actionable));
        Assert.True(QaDumpProjectionUtility.ShouldIncludeWorldHasActionField(mixed));
    }

    [Fact]
    public void ApplyFailuresOnly_PreservesSummaryAndWarnings()
    {
        var summary = new TestRunSummary { total = 3, passed = 1, failed = 1, skipped = 1, completed = 3 };
        var payload = new TestRunResultPayload
        {
            runId = "run-1",
            mode = "edit",
            status = "Completed",
            summary = summary,
            tests =
            [
                new TestResultEntry { fullName = "PassedTest", outcome = "Passed" },
                new TestResultEntry { fullName = "FailedTest", outcome = "Failed" },
                new TestResultEntry { fullName = "SkippedTest", outcome = "Skipped" },
            ],
            warnings = ["warning"],
        };

        TestRunResultPayload result = TestResultProjectionUtility.ApplyFailuresOnly(payload, failuresOnly: true);

        Assert.Same(summary, result.summary);
        Assert.Equal(2, result.tests.Length);
        Assert.DoesNotContain(result.tests, test => test.outcome == "Passed");
        Assert.Equal(["warning"], result.warnings);
    }

    [Fact]
    public void ApplyFailuresOnly_DoesNotMutateOriginalPayload()
    {
        var payload = new TestRunResultPayload
        {
            runId = "run-1",
            mode = "edit",
            status = "Completed",
            tests =
            [
                new TestResultEntry { fullName = "PassedTest", outcome = "Passed" },
                new TestResultEntry { fullName = "FailedTest", outcome = "Failed" },
            ],
        };
        string originalJson = ProtocolJson.Serialize(payload);

        TestRunResultPayload result = TestResultProjectionUtility.ApplyFailuresOnly(payload, failuresOnly: true);

        Assert.Single(result.tests);
        Assert.Equal(2, payload.tests.Length);
        Assert.Equal("PassedTest", payload.tests[0].fullName);
        Assert.Equal(originalJson, ProtocolJson.Serialize(payload));
    }

    [Fact]
    public void ApplyFailuresOnly_DefaultReturnsSamePayload()
    {
        var payload = new TestRunResultPayload();

        TestRunResultPayload result = TestResultProjectionUtility.ApplyFailuresOnly(payload, failuresOnly: false);

        Assert.Same(payload, result);
    }

    [Theory]
    [InlineData("Error", "error", true)]
    [InlineData("Exception", "error", true)]
    [InlineData("Assert", "error", true)]
    [InlineData("exception", "ERROR", true)]
    [InlineData("ASSERT", "error", true)]
    [InlineData("Warning", "error", false)]
    [InlineData("Log", "error", false)]
    [InlineData("Exception", "exception", true)]
    [InlineData("Error", "exception", false)]
    [InlineData("Assert", "exception", false)]
    [InlineData("Assert", "assert", true)]
    [InlineData("Exception", "assert", false)]
    [InlineData("Warning", "warning", true)]
    [InlineData("Error", "warning", false)]
    [InlineData("Log", "log", true)]
    [InlineData("Warning", "log", false)]
    [InlineData("Exception", null, true)]
    [InlineData("Log", "", true)]
    [InlineData("Error", "   ", true)]
    [InlineData(null, "error", false)]
    [InlineData("", "error", false)]
    public void MatchesType_FiltersConsoleTypes(string? entryType, string? filter, bool expected)
    {
        Assert.Equal(expected, ConsoleLogProjectionUtility.MatchesType(entryType, filter));
    }

    [Fact]
    public void ConsoleProjection_DefaultKeepsStackTrace()
    {
        Assert.False(ConsoleLogProjectionUtility.ShouldOmitStackTrace(new ReadConsoleArgs()));
        Assert.True(ConsoleLogProjectionUtility.ShouldOmitStackTrace(new ReadConsoleArgs { noStackTrace = true }));
    }

    [Fact]
    public void ConsoleProjection_NoStackTraceDoesNotMutateSourceEntries()
    {
        ConsoleLogEntry[] entries =
        [
            new ConsoleLogEntry
            {
                timestampUtc = "2026-06-26T00:00:00.0000000Z",
                type = "Error",
                message = "Boom",
                stackTrace = "Original stack",
            },
        ];

        ConsoleLogEntryWithoutStackTrace[] result = ConsoleLogProjectionUtility.ApplyNoStackTrace(entries);
        string resultJson = ProtocolJson.Serialize(new { entries = result });

        Assert.Equal("Original stack", entries[0].stackTrace);
        Assert.Single(result);
        Assert.Equal("Boom", result[0].message);
        Assert.DoesNotContain("stackTrace", resultJson, StringComparison.Ordinal);
    }

    [Fact]
    public void QaDumpPayloadSerialization_DefaultUiDumpKeepsLegacyFields()
    {
        string json = ProtocolJson.Serialize(new QaUiDumpPayload
        {
            elements =
            [
                new QaUiElement
                {
                    path = "/Canvas[0]/Start[0]",
                    type = "Button",
                    text = "Start",
                    interactable = true,
                    x = 10,
                    y = 20,
                    width = 100,
                    height = 40,
                    centerX = 60,
                    centerY = 40,
                },
            ],
        });

        Assert.Contains("\"interactable\"", json, StringComparison.Ordinal);
        Assert.Contains("\"x\"", json, StringComparison.Ordinal);
        Assert.Contains("\"y\"", json, StringComparison.Ordinal);
        Assert.Contains("\"width\"", json, StringComparison.Ordinal);
        Assert.Contains("\"height\"", json, StringComparison.Ordinal);
        Assert.Contains("\"centerX\"", json, StringComparison.Ordinal);
        Assert.Contains("\"centerY\"", json, StringComparison.Ordinal);
    }

    [Fact]
    public void QaDumpPayloadSerialization_DefaultWorldDumpKeepsLegacyFields()
    {
        string json = ProtocolJson.Serialize(new QaWorldDumpPayload
        {
            elements =
            [
                World("Visible", onScreen: true, hasAction: false),
            ],
        });

        Assert.Contains("\"label\"", json, StringComparison.Ordinal);
        Assert.Contains("\"path\"", json, StringComparison.Ordinal);
        Assert.Contains("\"centerX\"", json, StringComparison.Ordinal);
        Assert.Contains("\"centerY\"", json, StringComparison.Ordinal);
        Assert.Contains("\"onScreen\"", json, StringComparison.Ordinal);
        Assert.Contains("\"hasAction\"", json, StringComparison.Ordinal);
    }

    [Fact]
    public void QaWorldDumpEditMode_HintPointsAtSceneInspect()
    {
        Assert.Contains("scene inspect", QaWorldDumpEditMode.Hint, StringComparison.Ordinal);
        Assert.Contains("`play`", QaWorldDumpEditMode.Hint, StringComparison.Ordinal);
        Assert.Contains("Edit Mode", QaWorldDumpEditMode.Hint, StringComparison.Ordinal);

        string? help = CliCommandMetadata.BuildTopicHelpText("qa world-dump");
        Assert.NotNull(help);
        Assert.Contains("scene inspect", help, StringComparison.Ordinal);
        Assert.Contains("Edit Mode", help, StringComparison.Ordinal);
    }

    private static QaUiElement Ui(string text, bool interactable)
    {
        return new QaUiElement
        {
            path = "/" + text.Replace(" ", string.Empty, StringComparison.Ordinal),
            text = text,
            interactable = interactable,
        };
    }

    private static QaWorldElement World(string label, bool onScreen, bool hasAction)
    {
        return new QaWorldElement
        {
            path = "/" + label.Replace(" ", string.Empty, StringComparison.Ordinal),
            label = label,
            onScreen = onScreen,
            hasAction = hasAction,
        };
    }
}
