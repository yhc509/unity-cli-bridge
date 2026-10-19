using System.Diagnostics;
using UnityCli.DocGen;

namespace UnityCli.Cli.Tests;

public sealed class PluginSkillSyncTests
{
    [Fact]
    public void GeneratedPluginSkill_MatchesTemplatesAndPackageVersion()
    {
        string repoRoot = RepositoryPaths.FindRepoRoot(AppContext.BaseDirectory);
        string script = Path.Combine(repoRoot, "scripts", "sync-plugin-skill.py");
        Assert.True(File.Exists(script), "Plugin skill sync script is missing: " + script);

        string python = ResolvePython();
        var start = new ProcessStartInfo
        {
            FileName = python,
            WorkingDirectory = repoRoot,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        start.ArgumentList.Add(script);
        start.ArgumentList.Add("--check");

        using Process process = Process.Start(start)
            ?? throw new InvalidOperationException("Failed to start " + python);
        string stdout = process.StandardOutput.ReadToEnd();
        string stderr = process.StandardError.ReadToEnd();
        process.WaitForExit();

        Assert.True(
            process.ExitCode == 0,
            "scripts/sync-plugin-skill.py --check failed."
            + Environment.NewLine
            + stdout
            + stderr);
    }

    private static string ResolvePython()
    {
        foreach (string candidate in new[] { "python3", "python" })
        {
            if (CanRun(candidate))
            {
                return candidate;
            }
        }

        throw new InvalidOperationException(
            "python3 is required to verify the generated plugin skill.");
    }

    private static bool CanRun(string fileName)
    {
        try
        {
            var start = new ProcessStartInfo
            {
                FileName = fileName,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            };
            start.ArgumentList.Add("--version");
            using Process? process = Process.Start(start);
            if (process is null)
            {
                return false;
            }

            if (!process.WaitForExit(5000))
            {
                process.Kill(entireProcessTree: true);
                return false;
            }

            return process.ExitCode == 0;
        }
        catch (Exception)
        {
            return false;
        }
    }
}
