#nullable enable
using System.Text;
using UnityCli.Cli.Models;
using UnityCli.Protocol;

namespace UnityCli.Cli.Services;

internal enum DispatchAction
{
    /// <summary>Nothing to do: print the response the bridge (or the CLI) already produced.</summary>
    None,

    /// <summary>Hand the original argv to the installed CLI that speaks the bridge's protocol.</summary>
    Exec,

    /// <summary>The bridge speaks a protocol no installed CLI speaks. Tell the user how to fix it.</summary>
    NoMatchingVersion,
}

internal sealed class DispatchDecision
{
    private DispatchDecision(
        DispatchAction action,
        InstalledCliVersion? match,
        string requiredProtocolVersion,
        IReadOnlyList<InstalledCliVersion> installedVersions)
    {
        Action = action;
        Match = match;
        RequiredProtocolVersion = requiredProtocolVersion;
        InstalledVersions = installedVersions;
    }

    public DispatchAction Action { get; }

    public InstalledCliVersion? Match { get; }

    public string RequiredProtocolVersion { get; }

    public IReadOnlyList<InstalledCliVersion> InstalledVersions { get; }

    public static DispatchDecision None() =>
        new(DispatchAction.None, null, string.Empty, Array.Empty<InstalledCliVersion>());

    public static DispatchDecision Exec(InstalledCliVersion match, string requiredProtocolVersion) =>
        new(DispatchAction.Exec, match, requiredProtocolVersion, Array.Empty<InstalledCliVersion>());

    public static DispatchDecision NoMatchingVersion(
        string requiredProtocolVersion,
        IReadOnlyList<InstalledCliVersion> installedVersions) =>
        new(DispatchAction.NoMatchingVersion, null, requiredProtocolVersion, installedVersions);
}

/// <summary>
/// Decides whether a response should be answered by re-running a different installed CLI.
///
/// The whole design rests on one property of the bridge: a protocol mismatch is checked before auth
/// and before dispatch, it returns a normal error envelope, and that envelope carries the *bridge's*
/// protocol version. So the mismatch response itself tells us which CLI to hand off to, and nothing
/// ran on the Unity side — re-sending the command cannot double-execute it.
/// </summary>
internal static class CliDispatchPolicy
{
    /// <summary>
    /// Commands that never dispatch.
    ///
    /// `instances`, `qa wait` and `help` answer from local state and never talk to a bridge.
    /// `doctor` does probe the bridge, but a mismatch is its *finding*: it lands in `liveErrorCode`
    /// and the command still succeeds. Re-running it under another CLI would report on a different
    /// binary than the one the user is diagnosing, which defeats the point.
    ///
    /// `status` is deliberately NOT here. It returns the bridge's envelope verbatim, so a mismatch
    /// becomes its result and it exits 1 — it needs the hand-off like any other live command. It is
    /// read-only, and on a mismatch nothing ran, so re-sending it is safe.
    /// </summary>
    internal static bool IsLocalOnlyCommand(CommandKind kind) => kind is
        CommandKind.Help
        or CommandKind.InstancesList
        or CommandKind.InstancesUse
        or CommandKind.Doctor
        or CommandKind.QaWait;

    internal static bool IsProtocolMismatch(ResponseEnvelope response) =>
        string.Equals(response.error?.code, ProtocolConstants.ErrorProtocolMismatch, StringComparison.Ordinal);

    internal static DispatchDecision Decide(
        CommandKind kind,
        ResponseEnvelope response,
        ICliVersionDispatcher dispatcher)
    {
        if (IsLocalOnlyCommand(kind) || !IsProtocolMismatch(response))
        {
            return DispatchDecision.None();
        }

        // We are the process that was already dispatched to. Dispatching again would loop forever.
        if (dispatcher.IsDispatchGuardSet)
        {
            return DispatchDecision.None();
        }

        string requiredProtocolVersion = response.protocolVersion?.Trim() ?? string.Empty;
        if (requiredProtocolVersion.Length == 0)
        {
            // Bridge did not tell us its protocol; we have nothing to route on.
            return DispatchDecision.None();
        }

        // A bridge claiming our own protocol while rejecting us is self-contradictory. Handing off
        // to a CLI that speaks our protocol could pick this very binary, so report instead.
        if (string.Equals(requiredProtocolVersion, ProtocolConstants.ProtocolVersion, StringComparison.Ordinal))
        {
            return DispatchDecision.None();
        }

        IReadOnlyList<InstalledCliVersion> installedVersions = dispatcher.ListInstalledVersions();
        InstalledCliVersion? match = CliInstallLayout.FindByProtocolVersion(installedVersions, requiredProtocolVersion);
        return match is null
            ? DispatchDecision.NoMatchingVersion(requiredProtocolVersion, installedVersions)
            : DispatchDecision.Exec(match, requiredProtocolVersion);
    }

    internal static ResponseEnvelope BuildNoMatchingVersionResponse(
        ResponseEnvelope response,
        string requiredProtocolVersion,
        IReadOnlyList<InstalledCliVersion> installedVersions)
    {
        var details = new StringBuilder();
        details.Append("This CLI uses protocol ")
            .Append(ProtocolConstants.ProtocolVersion)
            .Append(".");

        if (installedVersions.Count == 0)
        {
            details.Append('\n').Append("No CLI versions are installed: ").Append(CliInstallLayout.GetVersionsDirectory());
        }
        else
        {
            details.Append('\n').Append("Installed CLI versions:");
            foreach (InstalledCliVersion installed in installedVersions)
            {
                details.Append('\n')
                    .Append("  - v")
                    .Append(installed.Version)
                    .Append(" (protocol ")
                    .Append(installed.ProtocolVersion)
                    .Append(")  ")
                    .Append(installed.ExecutablePath);
            }
        }

        var failure = ResponseEnvelope.Failure(
            response.requestId,
            response.target,
            ProtocolConstants.ErrorProtocolMismatch,
            "This project's Unity package uses protocol " + requiredProtocolVersion + ", "
                + "but no installed CLI speaks protocol " + requiredProtocolVersion + ". "
                + "In that project's Unity Editor, run Window > Unity CLI Manager > Install CLI.",
            retryable: false,
            durationMs: response.durationMs,
            transport: "cli",
            details: details.ToString());

        // Failure() stamps our own protocol version. On a PROTOCOL_MISMATCH the field must name the
        // protocol the bridge needs, or the JSON contradicts the message it ships with.
        failure.protocolVersion = requiredProtocolVersion;
        return failure;
    }

    /// <summary>
    /// The hand-off itself failed (bad permissions, deleted binary, …). Keep reporting the original
    /// PROTOCOL_MISMATCH — that is still the user's actual problem and it carries the actionable
    /// message — and record why we could not route around it.
    /// </summary>
    internal static ResponseEnvelope BuildHandoffFailedResponse(
        ResponseEnvelope response,
        InstalledCliVersion match,
        string failureMessage)
    {
        var failure = ResponseEnvelope.Failure(
            response.requestId,
            response.target,
            ProtocolConstants.ErrorProtocolMismatch,
            response.error?.message
                ?? "Unity package version is incompatible with this CLI.",
            retryable: false,
            durationMs: response.durationMs,
            transport: "cli",
            details: "Could not switch to CLI v" + match.Version + " (protocol " + (response.protocolVersion ?? string.Empty) + "): "
                + match.ExecutablePath + "\n" + failureMessage);

        failure.protocolVersion = response.protocolVersion;
        return failure;
    }
}
