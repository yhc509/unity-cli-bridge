using System.Text.Json;
using UnityCli.Cli;
using UnityCli.Cli.Models;
using UnityCli.Protocol;

namespace UnityCli.Cli.Tests;

public sealed class ProfilePollTests
{
    [Fact]
    public async Task PollProfileStatus_ReturnsWhenCompleted()
    {
        var target = new InstanceRecord { projectHash = "target-1", projectName = "Sample" };
        int call = 0;

        Task<ResponseEnvelope> Fake(InstanceRecord _, CommandEnvelope command, int timeoutMs, CancellationToken ct)
        {
            call++;
            Assert.Equal(ProtocolConstants.CommandProfileStatus, command.command);
            var payload = new ProfileSummaryPayload
            {
                captureId = "cap1",
                status = call >= 3 ? "Completed" : "Processing",
            };
            return Task.FromResult(Success(payload));
        }

        ResponseEnvelope result = await CliApp.PollProfileStatusAsync(
            target,
            Fake,
            "cap1",
            CancellationToken.None,
            delayAsync: (_, _) => Task.CompletedTask);

        var final = CliApp.DeserializeData<ProfileSummaryPayload>(result);
        Assert.Equal("Completed", final!.status);
        Assert.True(call >= 3);
    }

    [Theory]
    [InlineData("NotFound", ProtocolConstants.ErrorProfileNotFound)]
    [InlineData("Failed", ProtocolConstants.ErrorProfileFailed)]
    [InlineData("Interrupted", ProtocolConstants.ErrorProfileInterrupted)]
    public async Task PollProfileStatus_ConvertsTerminalFailureToError(string status, string expectedCode)
    {
        var target = new InstanceRecord { projectHash = "target-1", projectName = "Sample" };

        Task<ResponseEnvelope> Fake(InstanceRecord _, CommandEnvelope __, int ___, CancellationToken ____)
        {
            return Task.FromResult(Success(new ProfileSummaryPayload { captureId = "cap1", status = status }));
        }

        ResponseEnvelope result = await CliApp.PollProfileStatusAsync(
            target,
            Fake,
            "cap1",
            CancellationToken.None,
            delayAsync: (_, _) => Task.CompletedTask);

        Assert.Equal(ProtocolConstants.StatusError, result.status);
        Assert.Equal(expectedCode, result.error!.code);
    }

    [Fact]
    public async Task MergeQaProfileSummary_PollThrows_KeepsOriginalResponse()
    {
        var target = new InstanceRecord { projectHash = "target-1", projectName = "Sample" };
        ResponseEnvelope original = Success(new QaRunSequencePayload
        {
            status = "Completed",
            profileCaptureId = "cap1",
        });

        // A transport exception is thrown during polling.
        Task<ResponseEnvelope> Throwing(InstanceRecord _, CommandEnvelope __, int ___, CancellationToken ____)
            => throw new InvalidOperationException("socket reset");

        ResponseEnvelope result = await CliApp.MergeQaProfileSummaryAsync(
            target, Throwing, original, CancellationToken.None);

        // A successful sequence response is kept instead of being dropped.
        Assert.Equal(ProtocolConstants.StatusSuccess, result.status);
        Assert.DoesNotContain("profileSummary", result.data!.Value.GetRawText());
    }

    [Fact]
    public async Task MergeQaProfileSummary_Cancelled_KeepsOriginalResponse()
    {
        var target = new InstanceRecord { projectHash = "target-1", projectName = "Sample" };
        ResponseEnvelope original = Success(new QaRunSequencePayload
        {
            status = "Completed",
            profileCaptureId = "cap1",
        });

        using var cts = new CancellationTokenSource();
        cts.Cancel();

        // Polling still returns Processing, but the cancelled token makes PollProfileStatusAsync throw.
        Task<ResponseEnvelope> Fake(InstanceRecord _, CommandEnvelope __, int ___, CancellationToken ____)
            => Task.FromResult(Success(new ProfileSummaryPayload { captureId = "cap1", status = "Processing" }));

        ResponseEnvelope result = await CliApp.MergeQaProfileSummaryAsync(
            target, Fake, original, cts.Token);

        Assert.Equal(ProtocolConstants.StatusSuccess, result.status);
        Assert.DoesNotContain("profileSummary", result.data!.Value.GetRawText());
    }

    [Fact]
    public async Task MergeQaProfileSummary_Completed_InjectsSummary()
    {
        var target = new InstanceRecord { projectHash = "target-1", projectName = "Sample" };
        ResponseEnvelope original = Success(new QaRunSequencePayload
        {
            status = "Completed",
            profileCaptureId = "cap1",
        });

        Task<ResponseEnvelope> Fake(InstanceRecord _, CommandEnvelope __, int ___, CancellationToken ____)
            => Task.FromResult(Success(new ProfileSummaryPayload { captureId = "cap1", status = "Completed" }));

        ResponseEnvelope result = await CliApp.MergeQaProfileSummaryAsync(
            target, Fake, original, CancellationToken.None);

        Assert.Equal(ProtocolConstants.StatusSuccess, result.status);
        Assert.Contains("profileSummary", result.data!.Value.GetRawText());
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
