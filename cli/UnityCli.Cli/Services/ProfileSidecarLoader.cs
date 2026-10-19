#nullable enable
using System.IO;
using System.Text.Json;
using UnityCli.Protocol;

namespace UnityCli.Cli.Services;

/// <summary>Shared local read of a capture sidecar for the CLI-local profile commands. No IPC.</summary>
internal static class ProfileSidecarLoader
{
    /// <summary>
    /// Resolves and deserializes the capture sidecar at
    /// <c>{projectRoot}/Library/com.yhc509.unity-cli-bridge/profiles/{captureId}.json</c>.
    /// Returns false and fills <paramref name="failure"/> with the response envelope on any error.
    /// </summary>
    internal static bool TryLoad(
        string? projectRoot,
        string? captureId,
        out ProfileSidecarFile sidecar,
        out ResponseEnvelope? failure)
    {
        sidecar = null!;
        failure = null;

        if (string.IsNullOrWhiteSpace(projectRoot))
        {
            failure = ResponseEnvelope.Failure(
                "local",
                null,
                "CLI_USAGE",
                "Could not find the project root. Run inside a Unity project, or pass --project.",
                false,
                0,
                "cli");
            return false;
        }

        string sidecarPath = Path.Combine(
            projectRoot,
            ProtocolConstants.ProfilesDirectoryRelative.Replace('/', Path.DirectorySeparatorChar),
            captureId + ".json");
        if (!File.Exists(sidecarPath))
        {
            failure = ResponseEnvelope.Failure(
                "local",
                null,
                ProtocolConstants.ErrorProfileNotFound,
                $"Could not find the sidecar for captureId `{captureId}`: {sidecarPath}",
                false,
                0,
                "cli");
            return false;
        }

        ProfileSidecarFile? loaded;
        try
        {
            loaded = ProtocolJson.Deserialize<ProfileSidecarFile>(File.ReadAllText(sidecarPath));
        }
        catch (JsonException exception)
        {
            failure = ResponseEnvelope.Failure(
                "local",
                null,
                ProtocolConstants.ErrorProfileFailed,
                "Could not read sidecar JSON: " + exception.Message,
                false,
                0,
                "cli");
            return false;
        }

        if (loaded is null)
        {
            failure = ResponseEnvelope.Failure(
                "local", null, ProtocolConstants.ErrorProfileFailed, "The sidecar is empty.", false, 0, "cli");
            return false;
        }

        sidecar = loaded;
        return true;
    }

    /// <summary>
    /// Resolves and deserializes the memory report sidecar at
    /// <c>{projectRoot}/Library/com.yhc509.unity-cli-bridge/memory/{reportId}.json</c>.
    /// Returns false and fills <paramref name="failure"/> with the response envelope on any error.
    /// </summary>
    internal static bool TryLoadMemory(
        string? projectRoot,
        string? reportId,
        out ProfileMemorySidecarFile sidecar,
        out ResponseEnvelope? failure)
    {
        sidecar = null!;
        failure = null;

        if (string.IsNullOrWhiteSpace(projectRoot))
        {
            failure = ResponseEnvelope.Failure(
                "local",
                null,
                "CLI_USAGE",
                "Could not find the project root. Run inside a Unity project, or pass --project.",
                false,
                0,
                "cli");
            return false;
        }

        string sidecarPath = Path.Combine(
            projectRoot,
            ProtocolConstants.MemoryReportsDirectoryRelative.Replace('/', Path.DirectorySeparatorChar),
            reportId + ".json");
        if (!File.Exists(sidecarPath))
        {
            failure = ResponseEnvelope.Failure(
                "local",
                null,
                ProtocolConstants.ErrorProfileNotFound,
                $"Could not find the memory sidecar for reportId `{reportId}`: {sidecarPath}",
                false,
                0,
                "cli");
            return false;
        }

        ProfileMemorySidecarFile? loaded;
        try
        {
            loaded = ProtocolJson.Deserialize<ProfileMemorySidecarFile>(File.ReadAllText(sidecarPath));
        }
        catch (JsonException exception)
        {
            failure = ResponseEnvelope.Failure(
                "local",
                null,
                ProtocolConstants.ErrorProfileFailed,
                "Could not read memory sidecar JSON: " + exception.Message,
                false,
                0,
                "cli");
            return false;
        }

        if (loaded is null)
        {
            failure = ResponseEnvelope.Failure(
                "local", null, ProtocolConstants.ErrorProfileFailed, "The memory sidecar is empty.", false, 0, "cli");
            return false;
        }

        sidecar = loaded;
        return true;
    }
}
