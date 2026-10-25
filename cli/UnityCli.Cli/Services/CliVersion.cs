#nullable enable
using System.Reflection;

namespace UnityCli.Cli.Services;

/// <summary>
/// The CLI's version is the Unity package version. The project file reads
/// <c>package.json</c> at build time, and the SDK appends <c>+sha</c> from git.
/// </summary>
internal static class CliVersion
{
    internal static string InformationalVersion { get; } = Read();

    private static string Read()
    {
        string? version = typeof(CliVersion).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()
            ?.InformationalVersion;
        return string.IsNullOrWhiteSpace(version) ? "0.0.0" : version.Trim();
    }
}
