using System.Text.Json;
using UnityCli.Cli;
using UnityCli.Cli.Models;
using UnityCli.Cli.Services;
using UnityCli.Protocol;

namespace UnityCli.Cli.Tests;

public sealed class ProfileCaptureWaitTests
{
    private static readonly InstanceRecord Target = new() { projectHash = "target-1", projectName = "Sample" };

    [Theory]
    [InlineData(120, null, null, 30 + ProfileCaptureWaiter.ProcessingAllowanceSeconds)]
    [InlineData(300, null, null, 75 + ProfileCaptureWaiter.ProcessingAllowanceSeconds)]
    [InlineData(null, 10, null, 10 + ProfileCaptureWaiter.ProcessingAllowanceSeconds)]
    [InlineData(100000, null, null, ProtocolConstants.MaxProfileCaptureSeconds + ProfileCaptureWaiter.ProcessingAllowanceSeconds)]
    [InlineData(120, null, 7, 7)]
    public void ResolveTimeoutSeconds_BudgetsFramesOrDuration(int? frames, int? duration, int? explicitSeconds, int expected)
    {
        Assert.Equal(expected, ProfileCaptureWaiter.ResolveTimeoutSeconds(frames, duration, explicitSeconds));
    }

    [Fact]
    public async Task Wait_PollsUntilCompleted_AndReturnsSummaryWithCaptureIdAndFrames()
    {
        int polls = 0;
        Task<ResponseEnvelope> Send(InstanceRecord _, CommandEnvelope command, int __, CancellationToken ___)
        {
            polls++;
            Assert.Equal(ProtocolConstants.CommandProfileStatus, command.command);
            Assert.Contains("cap1", command.argumentsJson);
            ProfileSummaryPayload summary = polls switch
            {
                1 => new ProfileSummaryPayload { captureId = "cap1", status = "Capturing", capturedFrames = 40, requestedFrames = 120 },
                2 => new ProfileSummaryPayload { captureId = "cap1", status = "Processing", capturedFrames = 120, requestedFrames = 120 },
                _ => new ProfileSummaryPayload
                {
                    captureId = "cap1",
                    status = "Completed",
                    capturedFrames = 120,
                    requestedFrames = 120,
                    hotspots = new[] { new ProfileHotspot { marker = "ThreatField.Update" } },
                },
            };
            return Task.FromResult(Success(summary));
        }

        ResponseEnvelope result = await ProfileCaptureWaiter.WaitAsync(
            Target, Send, Started("cap1", 120), 60, CancellationToken.None, NoDelay);

        Assert.Equal(ProtocolConstants.StatusSuccess, result.status);
        Assert.Equal(3, polls);
        JsonElement data = result.data!.Value;
        Assert.Equal("cap1", data.GetProperty("captureId").GetString());
        Assert.Equal(120, data.GetProperty("capturedFrames").GetInt32());
        Assert.Equal("Completed", data.GetProperty("status").GetString());
        Assert.Equal("ThreatField.Update", data.GetProperty("summary").GetProperty("hotspots")[0].GetProperty("marker").GetString());
        Assert.False(data.TryGetProperty("notice", out _));
        Assert.False(data.TryGetProperty("warning", out _));
    }

    [Fact]
    public async Task Wait_CompletedAfterClamp_CopiesWarningOntoTheResultAndTheSummary()
    {
        ProfileFrameClamp? clamp = ProfileFrameClamp.TryCreate(100000, 300);
        Assert.NotNull(clamp);
        string warning = clamp!.WarningText();
        Task<ResponseEnvelope> Send(InstanceRecord _, CommandEnvelope __, int ___, CancellationToken ____)
            => Task.FromResult(Success(new ProfileSummaryPayload
            {
                captureId = "cap1",
                status = "Completed",
                capturedFrames = 297,
                requestedFrames = 300,
                warning = warning,
                frameClamp = clamp,
            }));

        ResponseEnvelope result = await ProfileCaptureWaiter.WaitAsync(
            Target, Send, Started("cap1", 300), 60, CancellationToken.None, NoDelay);

        JsonElement data = result.data!.Value;
        Assert.Equal("cap1", data.GetProperty("captureId").GetString());
        Assert.Equal("Completed", data.GetProperty("status").GetString());
        Assert.Equal(297, data.GetProperty("capturedFrames").GetInt32());
        Assert.Equal(300, data.GetProperty("requestedFrames").GetInt32());
        Assert.Equal(warning, data.GetProperty("warning").GetString());
        Assert.Equal(100000, data.GetProperty("frameClamp").GetProperty("requested").GetInt32());
        Assert.Equal(300, data.GetProperty("frameClamp").GetProperty("clamped").GetInt32());
        Assert.Equal(300, data.GetProperty("frameClamp").GetProperty("bufferLimit").GetInt32());
        Assert.Contains("Requested 100000 frames, clamped to 300 (profiler buffer limit 300).", warning);
        JsonElement summary = data.GetProperty("summary");
        Assert.Equal(warning, summary.GetProperty("warning").GetString());
        Assert.Equal(297, summary.GetProperty("capturedFrames").GetInt32());
        Assert.False(data.TryGetProperty("notice", out _));
        Assert.True(data.GetProperty("summary").TryGetProperty("capturedFrames", out _));
    }

    [Fact]
    public async Task Wait_UnclampedJsonUtilitySummary_OmitsWarningAndFrameClamp()
    {
        const string summary =
            "{\"captureId\":\"cap1\",\"status\":\"Completed\",\"capturedFrames\":120,\"requestedFrames\":120,"
            + "\"warning\":\"\",\"frameClamp\":{\"requested\":0,\"clamped\":0,\"bufferLimit\":0}}";
        Task<ResponseEnvelope> Send(InstanceRecord _, CommandEnvelope __, int ___, CancellationToken ____)
            => Task.FromResult(JsonSuccess(summary));

        ResponseEnvelope result = await ProfileCaptureWaiter.WaitAsync(
            Target, Send, Started("cap1", 120), 60, CancellationToken.None, NoDelay);

        AssertNoClampKeys(result.data!.Value);
        Assert.Equal(120, result.data.Value.GetProperty("capturedFrames").GetInt32());
        Assert.Equal(120, result.data.Value.GetProperty("summary").GetProperty("capturedFrames").GetInt32());
        AssertNoClampKeys(result.data.Value.GetProperty("summary"));
    }

    [Fact]
    public void PresentFinishedCapture_UnclampedFlatSummary_OmitsWarningAndFrameClamp()
    {
        ResponseEnvelope poll = JsonSuccess(
            "{\"captureId\":\"cap1\",\"status\":\"Completed\",\"capturedFrames\":254,\"requestedFrames\":300,"
            + "\"warning\":\"\",\"frameClamp\":{\"requested\":0,\"clamped\":0,\"bufferLimit\":0}}");

        ResponseEnvelope presented = CliApp.PresentFinishedCapture(poll);

        AssertNoClampKeys(presented.data!.Value);
        Assert.Equal(254, presented.data.Value.GetProperty("capturedFrames").GetInt32());
        Assert.Equal(254, presented.data.Value.GetProperty("summary").GetProperty("capturedFrames").GetInt32());
        AssertNoClampKeys(presented.data.Value.GetProperty("summary"));
    }

    [Theory]
    [InlineData(OutputMode.Json)]
    [InlineData(OutputMode.Default)]
    [InlineData(OutputMode.Compact)]
    public void Format_UnclampedStartWaitAndStop_OmitWarningAndFrameClamp(OutputMode mode)
    {
        const string start =
            "{\"captureId\":\"cap1\",\"status\":\"STARTED\",\"requestedFrames\":5,\"durationSeconds\":0,\"budgetMs\":16.67,"
            + "\"warning\":\"\",\"frameClamp\":{\"requested\":0,\"clamped\":0,\"bufferLimit\":0}}";
        const string waited =
            "{\"captureId\":\"cap1\",\"status\":\"Completed\",\"capturedFrames\":5,\"requestedFrames\":5,\"warning\":\"\","
            + "\"frameClamp\":{\"requested\":0,\"clamped\":0,\"bufferLimit\":0},\"summary\":{\"captureId\":\"cap1\","
            + "\"status\":\"Completed\",\"capturedFrames\":5,\"requestedFrames\":5,\"warning\":\"\","
            + "\"frameClamp\":{\"requested\":0,\"clamped\":0,\"bufferLimit\":0},\"hotspots\":[{\"marker\":\"say \\\"warning\\\"\"}]}}";
        const string stopped =
            "{\"captureId\":\"cap1\",\"status\":\"Completed\",\"capturedFrames\":297,\"requestedFrames\":300,"
            + "\"notice\":\"PROFILE_ALREADY_COMPLETED\",\"warning\":\"\",\"frameClamp\":{\"requested\":0,\"clamped\":0,\"bufferLimit\":0},"
            + "\"summary\":{\"captureId\":\"cap1\",\"status\":\"Completed\",\"capturedFrames\":297,\"requestedFrames\":300,\"warning\":\"\","
            + "\"frameClamp\":{\"requested\":0,\"clamped\":0,\"bufferLimit\":0}}}";

        foreach (string payload in new[] { start, waited, stopped })
        {
            string output = ResponseFormatter.Format(mode, JsonSuccess(payload));
            string json = mode == OutputMode.Default
                ? output[(output.IndexOf("data:", StringComparison.Ordinal) + "data:".Length)..].Trim()
                : output;
            using JsonDocument parsed = JsonDocument.Parse(json);
            Assert.False(ProfileClampJson.HasProperty(json, "warning"), output);
            Assert.False(ProfileClampJson.HasProperty(json, "frameClamp"), output);
            Assert.Contains("cap1", output);
            _ = parsed;
        }

        string clamped = ResponseFormatter.Format(OutputMode.Json, JsonSuccess(
            "{\"captureId\":\"cap1\",\"status\":\"Completed\",\"capturedFrames\":1998,\"requestedFrames\":2000,"
            + "\"warning\":\"Requested 100000 frames, clamped to 2000 (profiler buffer limit 2000).\","
            + "\"frameClamp\":{\"requested\":100000,\"clamped\":2000,\"bufferLimit\":2000},"
            + "\"summary\":{\"capturedFrames\":1998,\"warning\":\"Requested 100000 frames, clamped to 2000 (profiler buffer limit 2000).\","
            + "\"frameClamp\":{\"requested\":100000,\"clamped\":2000,\"bufferLimit\":2000}}}"));
        Assert.Contains("\"warning\"", clamped);
        Assert.Contains("\"frameClamp\"", clamped);
        Assert.Contains("\"capturedFrames\":1998", clamped);
        Assert.Contains("\"clamped\":2000", clamped);
    }

    [Theory]
    [InlineData(100000, 300, true)]
    [InlineData(100000, 2000, true)]
    [InlineData(300, 300, false)]
    [InlineData(120, 300, false)]
    [InlineData(0, 300, false)]
    public void FrameClamp_TryCreate_OnlyWhenTheRequestExceedsTheBuffer(int requested, int buffer, bool clamped)
    {
        ProfileFrameClamp? clamp = ProfileFrameClamp.TryCreate(requested, buffer);

        if (!clamped)
        {
            Assert.Null(clamp);
            return;
        }

        Assert.Equal(requested, clamp!.requested);
        Assert.Equal(buffer, clamp.clamped);
        Assert.Equal(buffer, clamp.bufferLimit);
        Assert.Contains(requested.ToString(), clamp.WarningText());
        Assert.Contains("clamped to " + buffer, clamp.WarningText());
        Assert.Contains("profiler buffer limit " + buffer, clamp.WarningText());
    }

    [Fact]
    public void PresentFinishedCapture_WrapsAFlatSummaryIntoTheStopShape()
    {
        ProfileFrameClamp clamp = ProfileFrameClamp.TryCreate(100000, 300)!;
        ResponseEnvelope poll = Success(new ProfileSummaryPayload
        {
            captureId = "cap1",
            status = "Completed",
            capturedFrames = 297,
            requestedFrames = 300,
            warning = clamp.WarningText(),
            frameClamp = clamp,
            hotspots = new[] { new ProfileHotspot { marker = "ThreatField.Update" } },
        });

        ResponseEnvelope presented = CliApp.PresentFinishedCapture(poll);
        JsonElement data = presented.data!.Value;

        Assert.Equal("cap1", data.GetProperty("captureId").GetString());
        Assert.Equal("Completed", data.GetProperty("status").GetString());
        Assert.Equal(297, data.GetProperty("capturedFrames").GetInt32());
        Assert.Equal(clamp.WarningText(), data.GetProperty("warning").GetString());
        Assert.Equal("ThreatField.Update", data.GetProperty("summary").GetProperty("hotspots")[0].GetProperty("marker").GetString());
        Assert.False(data.GetProperty("summary").TryGetProperty("summary", out _));
        Assert.False(data.TryGetProperty("notice", out _));
    }

    [Fact]
    public void PresentFinishedCapture_LeavesAnAlreadyWrappedResultAlone()
    {
        ResponseEnvelope already = Success(new ProfileCaptureResultPayload
        {
            captureId = "cap1",
            status = "Completed",
            capturedFrames = 30,
            requestedFrames = 30,
            notice = ProtocolConstants.NoticeProfileAlreadyCompleted,
            summary = new ProfileSummaryPayload
            {
                captureId = "cap1",
                status = "Completed",
                capturedFrames = 30,
                hotspots = new[] { new ProfileHotspot { marker = "ThreatField.Update" } },
            },
        });

        ResponseEnvelope presented = CliApp.PresentFinishedCapture(already);
        JsonElement data = presented.data!.Value;

        Assert.Equal(ProtocolConstants.NoticeProfileAlreadyCompleted, data.GetProperty("notice").GetString());
        Assert.Equal("ThreatField.Update", data.GetProperty("summary").GetProperty("hotspots")[0].GetProperty("marker").GetString());
        Assert.False(data.GetProperty("summary").TryGetProperty("summary", out _));
    }

    [Fact]
    public void PresentFinishedCapture_DoesNotWrapACaptureThatIsStillProcessing()
    {
        ResponseEnvelope poll = Success(new ProfileSummaryPayload { captureId = "cap1", status = "Processing", capturedFrames = 4 });

        ResponseEnvelope presented = CliApp.PresentFinishedCapture(poll);

        Assert.False(presented.data!.Value.TryGetProperty("summary", out _));
        Assert.Equal("Processing", presented.data!.Value.GetProperty("status").GetString());
    }

    [Fact]
    public async Task Wait_EmptyCapture_ReturnsProfileEmptyWithCaptureIdAndHint()
    {
        Task<ResponseEnvelope> Send(InstanceRecord _, CommandEnvelope __, int ___, CancellationToken ____)
            => Task.FromResult(Success(new ProfileSummaryPayload { captureId = "cap1", status = "Empty", requestedFrames = 300 }));

        ResponseEnvelope result = await ProfileCaptureWaiter.WaitAsync(
            Target, Send, Started("cap1", 300), 60, CancellationToken.None, NoDelay);

        Assert.Equal(ProtocolConstants.StatusError, result.status);
        Assert.Equal(ProtocolConstants.ErrorProfileEmpty, result.error!.code);
        Assert.True(result.retryable);
        Assert.Contains("cap1", result.error.message);
        Assert.Contains("--frames <n> --wait", result.error.message);
        JsonElement details = result.error.details!.Value;
        Assert.Equal("cap1", details.GetProperty("captureId").GetString());
        Assert.Equal(0, details.GetProperty("capturedFrames").GetInt32());
        Assert.True(details.TryGetProperty("hint", out _));
    }

    [Fact]
    public async Task Wait_Timeout_ReturnsWaitTimeoutWithCaptureIdAndFramesCaptured()
    {
        DateTime now = new(2026, 10, 25, 0, 0, 0, DateTimeKind.Utc);
        Task<ResponseEnvelope> Send(InstanceRecord _, CommandEnvelope __, int ___, CancellationToken ____)
            => Task.FromResult(Success(new ProfileSummaryPayload { captureId = "cap1", status = "Capturing", capturedFrames = 37, requestedFrames = 120 }));
        Task Delay(TimeSpan interval, CancellationToken _)
        {
            now = now.AddSeconds(4);
            return Task.CompletedTask;
        }

        ResponseEnvelope result = await ProfileCaptureWaiter.WaitAsync(
            Target, Send, Started("cap1", 120), 10, CancellationToken.None, Delay, () => now);

        Assert.Equal(ProtocolConstants.ErrorProfileWaitTimeout, result.error!.code);
        Assert.True(result.retryable);
        Assert.Contains("cap1", result.error.message);
        Assert.Contains("37 of 120 frames", result.error.message);
        Assert.Contains("profile capture stop --wait", result.error.message);
        JsonElement details = result.error.details!.Value;
        Assert.Equal("cap1", details.GetProperty("captureId").GetString());
        Assert.Equal(37, details.GetProperty("capturedFrames").GetInt32());
        Assert.Equal("Capturing", details.GetProperty("status").GetString());
    }

    [Fact]
    public async Task Wait_DurationCaptureTimeout_ReportsFramesWithoutARequestedCount()
    {
        DateTime now = new(2026, 10, 25, 0, 0, 0, DateTimeKind.Utc);
        Task<ResponseEnvelope> Send(InstanceRecord _, CommandEnvelope __, int ___, CancellationToken ____)
            => Task.FromResult(Success(new ProfileSummaryPayload { captureId = "cap1", status = "Capturing", capturedFrames = 512 }));
        Task Delay(TimeSpan interval, CancellationToken _)
        {
            now = now.AddSeconds(3);
            return Task.CompletedTask;
        }

        ResponseEnvelope result = await ProfileCaptureWaiter.WaitAsync(
            Target, Send, Started("cap1", 0), 5, CancellationToken.None, Delay, () => now);

        Assert.Equal(ProtocolConstants.ErrorProfileWaitTimeout, result.error!.code);
        Assert.Contains("(Capturing, 512 frames captured)", result.error.message);
    }

    [Theory]
    [InlineData("Interrupted", ProtocolConstants.ErrorProfileInterrupted)]
    [InlineData("Failed", ProtocolConstants.ErrorProfileFailed)]
    [InlineData("NotFound", ProtocolConstants.ErrorProfileNotFound)]
    public async Task Wait_OtherTerminalStatus_KeepsCaptureId(string status, string code)
    {
        Task<ResponseEnvelope> Send(InstanceRecord _, CommandEnvelope __, int ___, CancellationToken ____)
            => Task.FromResult(Success(new ProfileSummaryPayload { captureId = "cap1", status = status }));

        ResponseEnvelope result = await ProfileCaptureWaiter.WaitAsync(
            Target, Send, Started("cap1", 10), 60, CancellationToken.None, NoDelay);

        Assert.Equal(code, result.error!.code);
        Assert.Equal("cap1", result.error.details!.Value.GetProperty("captureId").GetString());
    }

    [Fact]
    public async Task Wait_PollTransportFailure_NamesTheStartedCapture()
    {
        Task<ResponseEnvelope> Send(InstanceRecord _, CommandEnvelope __, int ___, CancellationToken ____)
            => Task.FromResult(ResponseEnvelope.Failure("req", "target-1", "LIVE_UNAVAILABLE", "Editor is reloading.", true));

        ResponseEnvelope result = await ProfileCaptureWaiter.WaitAsync(
            Target, Send, Started("cap1", 10), 60, CancellationToken.None, NoDelay);

        Assert.Equal("LIVE_UNAVAILABLE", result.error!.code);
        Assert.Contains("profile status cap1", result.error.message);
    }

    [Fact]
    public async Task PollProfileStatus_EmptyStatus_ReturnsProfileEmpty()
    {
        Task<ResponseEnvelope> Send(InstanceRecord _, CommandEnvelope __, int ___, CancellationToken ____)
            => Task.FromResult(Success(new ProfileSummaryPayload { captureId = "cap1", status = "Empty" }));

        ResponseEnvelope result = await CliApp.PollProfileStatusAsync(
            Target, Send, "cap1", CancellationToken.None, delayAsync: NoDelay);

        Assert.Equal(ProtocolConstants.ErrorProfileEmpty, result.error!.code);
        Assert.Contains(ProtocolConstants.ProfileEmptyHint, result.error.message);
    }

    [Fact]
    public void ShouldPollProfileStop_OnlyForAStopThatLeftTheCaptureRunning()
    {
        var parsed = new ParsedCommand(CommandKind.ProfileCaptureStop) { ProfileWait = true };

        Assert.True(CliApp.ShouldPollProfileStop(parsed, Success(new ProfileSummaryPayload { captureId = "cap1", status = "Processing" })));
        Assert.False(CliApp.ShouldPollProfileStop(parsed, Success(new ProfileCaptureResultPayload
        {
            captureId = "cap1",
            status = "Completed",
            notice = ProtocolConstants.NoticeProfileAlreadyCompleted,
        })));
        Assert.False(CliApp.ShouldPollProfileStop(
            new ParsedCommand(CommandKind.ProfileCaptureStop),
            Success(new ProfileSummaryPayload { captureId = "cap1", status = "Processing" })));
    }

    [Fact]
    public void ShouldWaitProfileCapture_RequiresStartWithWaitAndSuccess()
    {
        var parsed = new ParsedCommand(CommandKind.ProfileCaptureStart) { ProfileWait = true, ProfileFrames = 10 };

        Assert.True(CliApp.ShouldWaitProfileCapture(parsed, Started("cap1", 10)));
        Assert.False(CliApp.ShouldWaitProfileCapture(
            parsed, ResponseEnvelope.Failure("r", "t", ProtocolConstants.ErrorProfileRequiresPlaymode, "play first", false)));
        Assert.False(CliApp.ShouldWaitProfileCapture(new ParsedCommand(CommandKind.ProfileCaptureStart), Started("cap1", 10)));
    }

    [Fact]
    public void RequireMarkerStatsSupport_OldBridgeWithoutMarkerStats_Fails()
    {
        var parsed = new ParsedCommand(CommandKind.ProfileStats);
        parsed.ProfileStatsMarkers.Add("ThreatField.Update");
        ResponseEnvelope oldBridge = ResponseEnvelope.Success(
            "r",
            "t",
            JsonDocument.Parse("{\"frames\":60,\"preset\":\"all\",\"counters\":[],\"unavailable\":[]}").RootElement.Clone(),
            1);

        ResponseEnvelope result = CliApp.RequireMarkerStatsSupport(parsed, oldBridge);

        Assert.Equal(ProtocolConstants.ErrorProfileFailed, result.error!.code);
        Assert.Contains("does not support `profile stats --marker`", result.error.message);
    }

    [Fact]
    public void RequireMarkerStatsSupport_CurrentBridgeOrNoMarkers_PassesThrough()
    {
        var withMarkers = new ParsedCommand(CommandKind.ProfileStats);
        withMarkers.ProfileStatsMarkers.Add("ThreatField.Update");
        ResponseEnvelope current = Success(new ProfileStatsPayload { frames = 60, preset = "markers" });

        Assert.Same(current, CliApp.RequireMarkerStatsSupport(withMarkers, current));

        ResponseEnvelope countersOnly = ResponseEnvelope.Success(
            "r", "t", JsonDocument.Parse("{\"frames\":60}").RootElement.Clone(), 1);
        Assert.Same(countersOnly, CliApp.RequireMarkerStatsSupport(new ParsedCommand(CommandKind.ProfileStats), countersOnly));
    }

    private static void AssertNoClampKeys(JsonElement data)
    {
        Assert.False(data.TryGetProperty("warning", out _));
        Assert.False(data.TryGetProperty("frameClamp", out _));
    }

    private static ResponseEnvelope JsonSuccess(string json)
    {
        return ResponseEnvelope.Success(
            "req-1",
            "target-1",
            JsonDocument.Parse(json).RootElement.Clone(),
            durationMs: 1,
            transport: ProtocolConstants.TransportLive);
    }

    private static Task NoDelay(TimeSpan _, CancellationToken __) => Task.CompletedTask;

    private static ResponseEnvelope Started(string captureId, int frames)
    {
        return Success(new ProfileCaptureStartedPayload { captureId = captureId, status = "STARTED", requestedFrames = frames });
    }

    private static ResponseEnvelope Success<T>(T data)
    {
        return ResponseEnvelope.Success(
            "req-1",
            "target-1",
            JsonSerializer.SerializeToElement(data, ProtocolJson.Default),
            durationMs: 1,
            transport: ProtocolConstants.TransportLive);
    }
}
