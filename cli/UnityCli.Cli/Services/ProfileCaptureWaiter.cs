#nullable enable
using System.Text.Json;
using UnityCli.Cli.Models;
using UnityCli.Protocol;

namespace UnityCli.Cli.Services;

/// <summary>
/// `profile capture start --wait`: polls `profile status` until the capture that just started has finished
/// on its own (--frames / --duration) and its summary is written, then returns that summary.
/// </summary>
internal static class ProfileCaptureWaiter
{
    /// <summary>Seconds of walk/processing headroom on top of the capture itself.</summary>
    internal const int ProcessingAllowanceSeconds = 60;

    /// <summary>
    /// An unfocused Editor can tick Play Mode at about 4 fps, so the default budgets 250 ms per requested frame
    /// (or the duration) plus <see cref="ProcessingAllowanceSeconds"/>, capped at the 600 s capture safety stop
    /// plus that allowance.
    /// </summary>
    internal static int ResolveTimeoutSeconds(int? frames, int? durationSeconds, int? explicitSeconds)
    {
        if (explicitSeconds is int value && value > 0)
        {
            return value;
        }

        int captureSeconds = durationSeconds is int duration && duration > 0
            ? duration
            : frames is int count && count > 0
                ? (int)Math.Ceiling(count / 4.0)
                : ProtocolConstants.MaxProfileCaptureSeconds;
        captureSeconds = Math.Min(captureSeconds, ProtocolConstants.MaxProfileCaptureSeconds);
        return captureSeconds + ProcessingAllowanceSeconds;
    }

    internal static async Task<ResponseEnvelope> WaitAsync(
        InstanceRecord target,
        Func<InstanceRecord, CommandEnvelope, int, CancellationToken, Task<ResponseEnvelope>> sendAsync,
        ResponseEnvelope startResponse,
        int timeoutSeconds,
        CancellationToken cancellationToken,
        Func<TimeSpan, CancellationToken, Task>? delayAsync = null,
        Func<DateTime>? utcNow = null,
        TimeSpan? pollInterval = null)
    {
        delayAsync ??= Task.Delay;
        utcNow ??= () => DateTime.UtcNow;
        TimeSpan interval = pollInterval ?? TimeSpan.FromMilliseconds(ProtocolConstants.ProfileCaptureWaitPollMs);

        ProfileCaptureStartedPayload? started = CliApp.DeserializeData<ProfileCaptureStartedPayload>(startResponse);
        if (started is null || string.IsNullOrEmpty(started.captureId))
        {
            return startResponse;
        }

        string captureId = started.captureId;
        DateTime deadline = utcNow().AddSeconds(timeoutSeconds);
        ProfileSummaryPayload? last = null;
        while (true)
        {
            await delayAsync(interval, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();

            CommandEnvelope command = new ParsedCommand(CommandKind.ProfileStatus) { ProfileCaptureId = captureId }.ToEnvelope();
            ResponseEnvelope poll = await sendAsync(target, command, ProtocolConstants.DefaultLiveTimeoutMs, cancellationToken);
            if (!string.Equals(poll.status, ProtocolConstants.StatusSuccess, StringComparison.Ordinal))
            {
                return KeepCaptureId(poll, captureId);
            }

            ProfileSummaryPayload? summary = CliApp.DeserializeData<ProfileSummaryPayload>(poll);
            if (summary is null)
            {
                return poll;
            }

            last = summary;
            switch (summary.status)
            {
                case "Completed":
                    ProfileCaptureResultPayload finished = ProfileCaptureResultPayload.FromSummary(summary, null, null);
                    finished.captureId = captureId;
                    return ResponseEnvelope.Success(
                        poll.requestId,
                        poll.target,
                        JsonSerializer.SerializeToElement(finished, ProtocolJson.Default),
                        poll.durationMs,
                        poll.transport);
                case "Capturing":
                case "Processing":
                    break;
                default:
                    return TerminalFailure(poll, captureId, summary);
            }

            if (utcNow() >= deadline)
            {
                return ResponseEnvelope.Failure(
                    poll.requestId,
                    poll.target,
                    ProtocolConstants.ErrorProfileWaitTimeout,
                    $"profile capture `{captureId}` did not finish within {timeoutSeconds} seconds ({last.status}, {DescribeFrames(last)} captured). "
                        + "The capture is still running: `profile capture stop --wait` ends it and returns what was captured, or read it later with `profile status " + captureId + "`. "
                        + "Raise --timeout when the Editor runs Play Mode slowly (for example when it is in the background).",
                    true,
                    poll.durationMs,
                    poll.transport,
                    Details(captureId, last.status, last.capturedFrames, last.requestedFrames, null));
            }
        }
    }

    /// <summary>A terminal non-Completed status (Empty, Interrupted, Failed, NotFound) as an error that still names the capture.</summary>
    internal static ResponseEnvelope TerminalFailure(ResponseEnvelope poll, string captureId, ProfileSummaryPayload summary)
    {
        if (string.Equals(summary.status, "Empty", StringComparison.Ordinal))
        {
            return ResponseEnvelope.Failure(
                poll.requestId,
                poll.target,
                ProtocolConstants.ErrorProfileEmpty,
                $"profile capture `{captureId}` finished with 0 frames. {ProtocolConstants.ProfileEmptyHint}",
                true,
                poll.durationMs,
                poll.transport,
                Details(captureId, summary.status, 0, summary.requestedFrames, ProtocolConstants.ProfileEmptyHint));
        }

        string code = summary.status switch
        {
            "NotFound" => ProtocolConstants.ErrorProfileNotFound,
            "Interrupted" => ProtocolConstants.ErrorProfileInterrupted,
            _ => ProtocolConstants.ErrorProfileFailed,
        };
        return ResponseEnvelope.Failure(
            poll.requestId,
            poll.target,
            code,
            $"profile capture `{captureId}` status is {summary.status}.",
            false,
            poll.durationMs,
            poll.transport,
            Details(captureId, summary.status, summary.capturedFrames, summary.requestedFrames, null));
    }

    private static string DescribeFrames(ProfileSummaryPayload summary)
    {
        return summary.requestedFrames > 0
            ? $"{summary.capturedFrames} of {summary.requestedFrames} frames"
            : $"{summary.capturedFrames} frames";
    }

    private static ResponseEnvelope KeepCaptureId(ResponseEnvelope failure, string captureId)
    {
        if (failure.error is not null && !failure.error.message.Contains(captureId, StringComparison.Ordinal))
        {
            failure.error.message = failure.error.message.TrimEnd()
                + $" The capture `{captureId}` was started; read it with `profile status {captureId}`.";
        }

        return failure;
    }

    private static string Details(string captureId, string status, int capturedFrames, int requestedFrames, string? hint)
    {
        var details = new Dictionary<string, object>
        {
            ["captureId"] = captureId,
            ["status"] = status,
            ["capturedFrames"] = capturedFrames,
            ["requestedFrames"] = requestedFrames,
        };
        if (hint is not null)
        {
            details["hint"] = hint;
        }

        return JsonSerializer.Serialize(details, ProtocolJson.Default);
    }
}
