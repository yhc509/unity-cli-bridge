using System.Text.RegularExpressions;
using UnityCli.Cli.Models;
using UnityCli.Protocol;

namespace UnityCli.Cli.Services;

internal sealed class CliUsagePresentation
{
    public CliUsagePresentation(
        string message,
        string? usage = null,
        string[]? suggestions = null,
        string? suggestionsLabel = null,
        string[]? availableCommands = null,
        string? availableCommandsLabel = null,
        bool fallbackToGlobalHelp = true)
    {
        Message = message;
        Usage = usage;
        Suggestions = suggestions ?? Array.Empty<string>();
        SuggestionsLabel = suggestionsLabel;
        AvailableCommands = availableCommands ?? Array.Empty<string>();
        AvailableCommandsLabel = availableCommandsLabel;
        FallbackToGlobalHelp = fallbackToGlobalHelp;
    }

    public string Message { get; }

    public string? Usage { get; }

    public string[] Suggestions { get; }

    public string? SuggestionsLabel { get; }

    public string[] AvailableCommands { get; }

    public string? AvailableCommandsLabel { get; }

    public bool FallbackToGlobalHelp { get; }

    public string? BuildDetailsJson()
    {
        var details = new Dictionary<string, object?>();
        if (!string.IsNullOrWhiteSpace(Usage))
        {
            details["usage"] = Usage;
        }

        if (Suggestions.Length > 0)
        {
            details["suggestions"] = Suggestions;
        }

        if (AvailableCommands.Length > 0)
        {
            details["availableCommands"] = AvailableCommands;
        }

        return details.Count == 0 ? null : ProtocolJson.Serialize(details);
    }
}

internal static class CliUsageHelp
{
    private const string GlobalUsage = "usage: unity-cli [--json] [--output <default|json|compact>] [--project <path|name>] <command> [options]";
    private static readonly Regex OptionPattern = new(@"--[a-z0-9-]+", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    public static CliUsagePresentation Build(string[] args, string message, ParsedCommand? parsed)
    {
        var context = CliInputContext.From(args, parsed);

        if (TryExtractValue(message, "Unknown command: ", out var invalidCommand))
        {
            var suggestions = Suggest(
                invalidCommand,
                GetTopLevelCommands().Select(command => (key: command, display: command)));
            return new CliUsagePresentation(
                message,
                usage: GlobalUsage,
                suggestions: suggestions,
                suggestionsLabel: "Similar commands:",
                availableCommands: GetTopLevelCommands(),
                availableCommandsLabel: "Available commands:",
                fallbackToGlobalHelp: false);
        }

        if (!string.IsNullOrWhiteSpace(context.RootCommand) && context.RootHasSubcommands && !context.CommandPathResolved)
        {
            var availableCommands = GetSubcommandCommands(context.RootCommand!);
            var suggestions = Array.Empty<string>();
            if (!string.IsNullOrWhiteSpace(context.Subcommand))
            {
                suggestions = Suggest(
                    context.Subcommand!,
                    GetSubcommandNames(context.RootCommand!).Select(name => (key: name, display: context.RootCommand + " " + name)));
            }

            return new CliUsagePresentation(
                message,
                usage: BuildRootUsage(context.RootCommand!),
                suggestions: suggestions,
                suggestionsLabel: $"Similar `{context.RootCommand}` subcommands:",
                availableCommands: availableCommands,
                availableCommandsLabel: $"Available `{context.RootCommand}` subcommands:",
                fallbackToGlobalHelp: false);
        }

        var usage = context.CommandPath is not null
            ? TryBuildCommandUsage(context.CommandPath)
            : GlobalUsage;

        if (TryExtractValue(message, "Unsupported option: ", out var invalidOption) && context.CommandPath is not null)
        {
            var optionSuggestions = Suggest(
                invalidOption,
                GetKnownOptions(context.CommandPath).Select(option => (key: option, display: option)));
            return new CliUsagePresentation(
                message,
                usage: usage,
                suggestions: optionSuggestions,
                suggestionsLabel: "Similar options:",
                fallbackToGlobalHelp: false);
        }

        return new CliUsagePresentation(
            message,
            usage: usage,
            fallbackToGlobalHelp: string.IsNullOrWhiteSpace(usage));
    }

    public static void WriteTo(TextWriter writer, CliUsagePresentation presentation)
    {
        writer.WriteLine(presentation.Message);

        if (!string.IsNullOrWhiteSpace(presentation.Usage))
        {
            writer.WriteLine();
            writer.WriteLine(presentation.Usage);
        }

        if (presentation.Suggestions.Length > 0)
        {
            writer.WriteLine();
            writer.WriteLine(presentation.SuggestionsLabel ?? "Similar items:");
            foreach (var suggestion in presentation.Suggestions)
            {
                writer.WriteLine("  " + suggestion);
            }
        }

        if (presentation.AvailableCommands.Length > 0)
        {
            writer.WriteLine();
            writer.WriteLine(presentation.AvailableCommandsLabel ?? "Available commands:");
            foreach (var command in presentation.AvailableCommands)
            {
                writer.WriteLine("  " + command);
            }
        }

        if (presentation.FallbackToGlobalHelp)
        {
            writer.WriteLine();
            writer.WriteLine(CliArgumentParser.BuildHelpText());
        }
    }

    private static string BuildRootUsage(string rootCommand)
    {
        return $"usage: unity-cli [--json] [--output <default|json|compact>] [--project <path|name>] {rootCommand} <subcommand> [options]";
    }

    private static string? TryBuildCommandUsage(string commandPath)
    {
        var descriptor = CliCommandCatalog.FindByCommand(commandPath);
        if (descriptor is null)
        {
            return null;
        }

        return "usage: unity-cli [--json] [--output <default|json|compact>] [--project <path|name>] " + descriptor.Synopsis;
    }

    private static string[] GetTopLevelCommands()
    {
        return CliCommandCatalog.GetCommands()
            .Select(descriptor => descriptor.Command.Split(' ', 2)[0])
            .Append("help")
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(command => command, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static string[] GetSubcommandNames(string rootCommand)
    {
        return CliCommandCatalog.GetCommands()
            .Where(descriptor => descriptor.Command.StartsWith(rootCommand + " ", StringComparison.Ordinal))
            .Select(descriptor => descriptor.Command[(rootCommand.Length + 1)..].Split(' ', 2)[0])
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(command => command, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static string[] GetSubcommandCommands(string rootCommand)
    {
        return CliCommandCatalog.GetCommands()
            .Where(descriptor => descriptor.Command.StartsWith(rootCommand + " ", StringComparison.Ordinal))
            .Select(descriptor => descriptor.Command)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(command => command, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static string[] GetKnownOptions(string commandPath)
    {
        var options = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "--json",
            "--output",
            "--project",
            "--timeout-ms",
        };

        var descriptor = CliCommandCatalog.FindByCommand(commandPath);
        if (descriptor is not null)
        {
            foreach (Match match in OptionPattern.Matches(descriptor.Synopsis))
            {
                options.Add(match.Value);
            }
        }

        return options.OrderBy(option => option, StringComparer.OrdinalIgnoreCase).ToArray();
    }

    private static string[] Suggest(string input, IEnumerable<(string key, string display)> candidates)
    {
        var normalizedInput = Normalize(input);
        var matches = candidates
            .Select(candidate => new
            {
                candidate.display,
                Distance = ComputeEditDistance(normalizedInput, Normalize(candidate.key)),
                StartsWith = Normalize(candidate.key).StartsWith(normalizedInput, StringComparison.Ordinal),
                Contains = Normalize(candidate.key).Contains(normalizedInput, StringComparison.Ordinal),
            })
            .Where(candidate =>
                candidate.StartsWith
                || candidate.Contains
                || candidate.Distance <= Math.Max(2, normalizedInput.Length / 3))
            .OrderBy(candidate => candidate.Distance)
            .ThenByDescending(candidate => candidate.StartsWith)
            .ThenBy(candidate => candidate.display, StringComparer.OrdinalIgnoreCase)
            .Select(candidate => candidate.display)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(3)
            .ToArray();

        return matches;
    }

    private static bool TryExtractValue(string message, string prefix, out string value)
    {
        if (message.StartsWith(prefix, StringComparison.Ordinal))
        {
            value = message[prefix.Length..].Trim();
            return true;
        }

        value = string.Empty;
        return false;
    }

    private static string Normalize(string value)
    {
        return value.Trim().TrimStart('-').ToLowerInvariant();
    }

    private static int ComputeEditDistance(string left, string right)
    {
        if (left.Length == 0)
        {
            return right.Length;
        }

        if (right.Length == 0)
        {
            return left.Length;
        }

        var costs = new int[right.Length + 1];
        for (var index = 0; index <= right.Length; index++)
        {
            costs[index] = index;
        }

        for (var leftIndex = 1; leftIndex <= left.Length; leftIndex++)
        {
            var previousDiagonal = costs[0];
            costs[0] = leftIndex;

            for (var rightIndex = 1; rightIndex <= right.Length; rightIndex++)
            {
                var previous = costs[rightIndex];
                var substitutionCost = left[leftIndex - 1] == right[rightIndex - 1] ? 0 : 1;
                costs[rightIndex] = Math.Min(
                    Math.Min(costs[rightIndex] + 1, costs[rightIndex - 1] + 1),
                    previousDiagonal + substitutionCost);
                previousDiagonal = previous;
            }
        }

        return costs[right.Length];
    }

    private sealed class CliInputContext
    {
        public string? RootCommand { get; init; }

        public string? Subcommand { get; init; }

        public string? CommandPath { get; init; }

        public bool RootHasSubcommands { get; init; }

        public bool CommandPathResolved { get; init; }

        public static CliInputContext From(string[] args, ParsedCommand? parsed)
        {
            if (parsed is not null && TryGetCommandPath(parsed.Kind) is { } parsedCommandPath)
            {
                var parts = parsedCommandPath.Split(' ', 2);
                return new CliInputContext
                {
                    RootCommand = parts[0],
                    Subcommand = parts.Length > 1 ? parts[1] : null,
                    CommandPath = parsedCommandPath,
                    RootHasSubcommands = GetSubcommandNames(parts[0]).Length > 0,
                    CommandPathResolved = true,
                };
            }

            var tokens = new Queue<string>(args);
            while (tokens.Count > 0)
            {
                if (tokens.Peek() == "--json")
                {
                    tokens.Dequeue();
                    continue;
                }

                if (tokens.Peek() == "--output")
                {
                    tokens.Dequeue();
                    if (tokens.Count > 0)
                    {
                        tokens.Dequeue();
                    }

                    continue;
                }

                if (tokens.Peek() == "--project")
                {
                    tokens.Dequeue();
                    if (tokens.Count > 0)
                    {
                        tokens.Dequeue();
                    }

                    continue;
                }

                break;
            }

            if (tokens.Count == 0)
            {
                return new CliInputContext();
            }

            var rootCommand = tokens.Dequeue().ToLowerInvariant();
            var rootHasSubcommands = GetSubcommandNames(rootCommand).Length > 0;
            string? subcommand = null;
            string? resolvedCommandPath = rootCommand;
            var commandPathResolved = !rootHasSubcommands;

            if (rootHasSubcommands && tokens.Count > 0 && !tokens.Peek().StartsWith("--", StringComparison.Ordinal))
            {
                subcommand = tokens.Dequeue().ToLowerInvariant();
                var candidatePath = rootCommand + " " + subcommand;
                commandPathResolved = CliCommandCatalog.FindByCommand(candidatePath) is not null;
                resolvedCommandPath = commandPathResolved ? candidatePath : rootCommand;
            }

            return new CliInputContext
            {
                RootCommand = rootCommand,
                Subcommand = subcommand,
                CommandPath = resolvedCommandPath,
                RootHasSubcommands = rootHasSubcommands,
                CommandPathResolved = commandPathResolved,
            };
        }

        private static string? TryGetCommandPath(CommandKind kind)
        {
            return kind switch
            {
                CommandKind.Version => "version",
                CommandKind.Status => "status",
                CommandKind.Compile => "compile",
                CommandKind.Refresh => "refresh",
                CommandKind.ReadConsole => "read-console",
                CommandKind.Play => "play",
                CommandKind.Pause => "pause",
                CommandKind.Stop => "stop",
                CommandKind.ExecuteMenu => "execute-menu",
                CommandKind.Screenshot => "screenshot",
                CommandKind.ExecuteCode => "execute",
                CommandKind.Custom => "custom",
                CommandKind.AssetFind => "asset find",
                CommandKind.AssetTypes => "asset types",
                CommandKind.AssetInfo => "asset info",
                CommandKind.AssetReimport => "asset reimport",
                CommandKind.AssetMkdir => "asset mkdir",
                CommandKind.AssetMove => "asset move",
                CommandKind.AssetRename => "asset rename",
                CommandKind.AssetDelete => "asset delete",
                CommandKind.AssetCreate => "asset create",
                CommandKind.SceneOpen => "scene open",
                CommandKind.SceneInspect => "scene inspect",
                CommandKind.ScenePatch => "scene patch",
                CommandKind.SceneAddObject => "scene add-object",
                CommandKind.SceneSetTransform => "scene set-transform",
                CommandKind.SceneAddComponent => "scene add-component",
                CommandKind.SceneRemoveComponent => "scene remove-component",
                CommandKind.SceneListComponents => "scene list-components",
                CommandKind.SceneAssignMaterial => "scene assign-material",
                CommandKind.PrefabInspect => "prefab inspect",
                CommandKind.PrefabCreate => "prefab create",
                CommandKind.PrefabPatch => "prefab patch",
                CommandKind.PrefabAddComponent => "prefab add-component",
                CommandKind.PrefabRemoveComponent => "prefab remove-component",
                CommandKind.PrefabListComponents => "prefab list-components",
                CommandKind.InstancesList => "instances list",
                CommandKind.InstancesUse => "instances use",
                CommandKind.Doctor => "doctor",
                CommandKind.Raw => "raw",
                CommandKind.PackageList => "package list",
                CommandKind.PackageAdd => "package add",
                CommandKind.PackageRemove => "package remove",
                CommandKind.PackageSearch => "package search",
                CommandKind.MaterialInfo => "material info",
                CommandKind.MaterialSet => "material set",
                _ => null,
            };
        }
    }
}
