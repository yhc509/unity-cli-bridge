using UnityCli.Cli.Models;
using ProtocolCliCommandCatalog = UnityCli.Protocol.CliCommandCatalog;

namespace UnityCli.Cli.Services;

public static class CliCommandMetadata
{
    public static string BuildHelpText()
    {
        return ProtocolCliCommandCatalog.BuildHelpText().TrimEnd();
    }

    /// <summary>
    /// Help for one command path. Matches the exact command and its subcommands (`qa` lists every
    /// `qa ...` command). Falls back to the longest matching parent path, then null (use global help).
    /// </summary>
    public static string? BuildTopicHelpText(string topic)
    {
        string[] words = topic.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        for (int take = words.Length; take > 0; take--)
        {
            string prefix = string.Join(" ", words.Take(take));
            var matches = ProtocolCliCommandCatalog.GetCommands()
                .Where(c => c.Command == prefix || c.Command.StartsWith(prefix + " ", StringComparison.Ordinal))
                .ToArray();
            if (matches.Length == 0)
            {
                continue;
            }

            var builder = new System.Text.StringBuilder();
            builder.AppendLine("usage: unity-cli [--json] [--output <default|json|compact>] [--project <path|name>] " + (matches.Length == 1 ? matches[0].Synopsis : prefix + " <subcommand> [options]"));
            builder.AppendLine();
            foreach (var command in matches)
            {
                builder.AppendLine("  " + command.Synopsis);
                builder.AppendLine("      " + command.Summary);
                foreach (string note in command.Notes)
                {
                    builder.AppendLine("      note: " + note);
                }
            }

            return builder.ToString().TrimEnd();
        }

        return null;
    }

    public static OutputMode DetectOutputMode(string[] args)
    {
        return CliArgumentParser.DetectOutputMode(args);
    }

    public static bool DetectJsonOutput(string[] args)
    {
        return DetectOutputMode(args) == OutputMode.Json;
    }
}
