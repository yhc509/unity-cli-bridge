#nullable enable
using System.IO;
using System.Linq;
using System.Text.Json;
using UnityCli.Cli.Models;
using UnityCli.Protocol;

namespace UnityCli.Cli.Services;

public static partial class CliArgumentParser
{
    public static ParsedCommand Parse(string[] args)
    {
        if (args.Length == 0)
        {
            return new ParsedCommand(CommandKind.Help);
        }

        var tokens = new Queue<string>(args);
        var parsed = new ParsedCommand(CommandKind.Help);
        var outputMode = OutputMode.Default;
        string? projectOverride = null;

        while (tokens.Count > 0)
        {
            if (tokens.Peek() == "--json")
            {
                outputMode = OutputMode.Json;
                tokens.Dequeue();
                continue;
            }

            if (tokens.Peek() == "--output")
            {
                tokens.Dequeue();
                outputMode = RequireOutputMode(RequireValue(tokens, "--output"));
                continue;
            }

            if (tokens.Peek() == "--project")
            {
                tokens.Dequeue();
                projectOverride = RequireValue(tokens, "--project");
                continue;
            }

            break;
        }

        if (tokens.Count == 0)
        {
            parsed.OutputMode = outputMode;
            return parsed;
        }

        if (TryGetHelpTopic(tokens.ToArray(), out string? helpTopic))
        {
            return new ParsedCommand(CommandKind.Help)
            {
                OutputMode = outputMode,
                ProjectOverride = projectOverride,
                HelpTopic = helpTopic,
            };
        }

        var command = tokens.Dequeue().ToLowerInvariant();
        parsed = command switch
        {
            "status" => new ParsedCommand(CommandKind.Status),
            // compile/refresh/play/stop wait for the Editor to settle by default; --no-wait opts out.
            "compile" => new ParsedCommand(CommandKind.Compile) { Wait = true },
            "refresh" => new ParsedCommand(CommandKind.Refresh) { Wait = true },
            "read-console" => new ParsedCommand(CommandKind.ReadConsole),
            "play" => new ParsedCommand(CommandKind.Play) { Wait = true },
            "pause" => new ParsedCommand(CommandKind.Pause),
            "stop" => new ParsedCommand(CommandKind.Stop) { Wait = true },
            "execute-menu" => new ParsedCommand(CommandKind.ExecuteMenu),
            "screenshot" => new ParsedCommand(CommandKind.Screenshot),
            "execute" => new ParsedCommand(CommandKind.ExecuteCode),
            "custom" => ParseCustom(tokens),
            "asset" => ParseAsset(tokens),
            "scene" => ParseScene(tokens),
            "prefab" => ParsePrefab(tokens),
            "test" => ParseTestCommand(tokens),
            "package" => ParsePackage(tokens),
            "material" => ParseMaterial(tokens),
            "qa" => ParseQa(tokens),
            "record" => ParseRecordCommand(tokens),
            "editor" => ParseEditorCommand(tokens),
            "profile" => ParseProfile(tokens),
            "instances" => ParseInstances(tokens),
            "doctor" => new ParsedCommand(CommandKind.Doctor),
            "raw" => new ParsedCommand(CommandKind.Raw),
            "help" or "--help" or "-h" => new ParsedCommand(CommandKind.Help),
            "version" or "--version" => new ParsedCommand(CommandKind.Version),
            _ => throw new CliUsageException($"Unknown command: {command}"),
        };

        parsed.OutputMode = outputMode;
        parsed.ProjectOverride = projectOverride;
        ApplyCatalogDefaults(parsed);

        ParseCommandOptions(parsed, tokens);
        return parsed;
    }

    /// <summary>
    /// `--help` / `-h` anywhere in a command line asks for help on the leading command path
    /// (`qa --help`, `qa key --help`, `scene patch --path X --help`). A `--help` that directly
    /// follows a value-taking option (`execute --code --help`) is that option's value, not a help request.
    /// </summary>
    internal static bool TryGetHelpTopic(string[] tokens, out string? topic)
    {
        topic = null;
        if (tokens.Length > 1 && string.Equals(tokens[0], "help", StringComparison.OrdinalIgnoreCase))
        {
            string[] rest = tokens.Skip(1).TakeWhile(t => !t.StartsWith("-", StringComparison.Ordinal)).Select(t => t.ToLowerInvariant()).ToArray();
            topic = rest.Length == 0 ? null : string.Join(" ", rest);
            return true;
        }

        int helpIndex = -1;
        for (int i = 0; i < tokens.Length; i++)
        {
            if (tokens[i] is "--help" or "-h")
            {
                bool followsOption = i > 0 && tokens[i - 1].StartsWith("--", StringComparison.Ordinal);
                bool directlyAfterPath = tokens.Take(i).All(t => !t.StartsWith("-", StringComparison.Ordinal));
                if (directlyAfterPath || !followsOption)
                {
                    helpIndex = i;
                    break;
                }
            }
        }

        if (helpIndex < 0)
        {
            return false;
        }

        string[] path = tokens
            .Take(helpIndex)
            .TakeWhile(t => !t.StartsWith("-", StringComparison.Ordinal))
            .Select(t => t.ToLowerInvariant())
            .Where(t => t != "help")
            .ToArray();
        topic = path.Length == 0 ? null : string.Join(" ", path);
        return true;
    }

    private static void ApplyCatalogDefaults(ParsedCommand parsed)
    {
        if (parsed.Kind == CommandKind.Help)
        {
            return;
        }

        CliCommandDescriptor descriptor = GetCatalogDescriptor(parsed.Kind);
        parsed.TimeoutMs = descriptor.DefaultLiveTimeoutMs ?? ProtocolConstants.DefaultLiveTimeoutMs;
    }

    public static string BuildHelpText()
    {
        string[] notes =
        [
            "qa tap and coordinate-based qa swipe treat coordinates as screenshot pixels with a top-left origin; pass screenshot image coordinates directly because the bridge handles Y-flip and scaling, auto-uses the last captured screenshot size when available, and still accepts --screenshot-width/--screenshot-height as explicit overrides.",
            "qa swipe --target still treats --from/--to as pixel offsets from the target RectTransform center.",
            "screenshot --view game in Play Mode captures at the native Game View size first; --width/--height only downscale it, and larger requests save at native size with a warning."
        ];

        return string.Join(
            Environment.NewLine,
            [
                CliCommandMetadata.BuildHelpText(),
                "notes:",
                ..notes,
                string.Empty,
            ]);
    }

    // Best-effort pre-parse for early error formatting before full command parsing.
    // Structural limitation: positional values can be mistaken for `--output` values,
    // so `execute --code --output compact` is detected as compact even though `--output`
    // should be consumed as the `--code` value. The old `DetectJsonOutput` pre-parse had
    // the same limitation, so this does not introduce a new regression.
    public static OutputMode DetectOutputMode(string[] args)
    {
        var tokens = new Queue<string>(args);
        var outputMode = OutputMode.Default;

        while (tokens.Count > 0)
        {
            if (tokens.Peek() == "--json")
            {
                outputMode = OutputMode.Json;
                tokens.Dequeue();
                continue;
            }

            if (tokens.Peek() == "--output")
            {
                tokens.Dequeue();
                if (tokens.Count == 0)
                {
                    break;
                }

                outputMode = TryGetOutputMode(tokens.Dequeue()) ?? outputMode;
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
            return outputMode;
        }

        var command = tokens.Dequeue().ToLowerInvariant();
        while (tokens.Count > 0)
        {
            var token = tokens.Dequeue();
            if (token == "--output")
            {
                if (tokens.Count == 0)
                {
                    break;
                }

                outputMode = TryGetOutputMode(tokens.Dequeue()) ?? outputMode;
                continue;
            }

            if (token == "--json" && command is not ("raw" or "custom"))
            {
                outputMode = OutputMode.Json;
            }
        }

        return outputMode;
    }

    private static ParsedCommand ParseInstances(Queue<string> tokens)
    {
        if (tokens.Count == 0)
        {
            throw new CliUsageException("`instances` requires `list` or `use`.");
        }

        var subCommand = tokens.Dequeue().ToLowerInvariant();
        return subCommand switch
        {
            "list" => new ParsedCommand(CommandKind.InstancesList),
            "use" => new ParsedCommand(CommandKind.InstancesUse)
            {
                InstanceTarget = tokens.Count > 0 ? tokens.Dequeue() : null,
            },
            _ => throw new CliUsageException($"Unknown instances subcommand: {subCommand}"),
        };
    }

    private static ParsedCommand ParseAsset(Queue<string> tokens)
    {
        if (tokens.Count == 0)
        {
            throw new CliUsageException("`asset` requires a subcommand.");
        }

        var subCommand = tokens.Dequeue().ToLowerInvariant();
        return subCommand switch
        {
            "find" => new ParsedCommand(CommandKind.AssetFind),
            "types" => new ParsedCommand(CommandKind.AssetTypes),
            "info" => new ParsedCommand(CommandKind.AssetInfo),
            "reimport" => new ParsedCommand(CommandKind.AssetReimport),
            "mkdir" => new ParsedCommand(CommandKind.AssetMkdir),
            "move" => new ParsedCommand(CommandKind.AssetMove),
            "rename" => new ParsedCommand(CommandKind.AssetRename),
            "delete" => new ParsedCommand(CommandKind.AssetDelete),
            "create" => new ParsedCommand(CommandKind.AssetCreate),
            _ => throw new CliUsageException($"Unknown asset subcommand: {subCommand}"),
        };
    }

    private static ParsedCommand ParseScene(Queue<string> tokens)
    {
        if (tokens.Count == 0)
        {
            throw new CliUsageException("`scene` requires a subcommand.");
        }

        var subCommand = tokens.Dequeue().ToLowerInvariant();
        return subCommand switch
        {
            "open" => new ParsedCommand(CommandKind.SceneOpen),
            "inspect" => new ParsedCommand(CommandKind.SceneInspect),
            "patch" => new ParsedCommand(CommandKind.ScenePatch),
            "add-object" => new ParsedCommand(CommandKind.SceneAddObject),
            "set-transform" => new ParsedCommand(CommandKind.SceneSetTransform),
            "add-component" => new ParsedCommand(CommandKind.SceneAddComponent),
            "remove-component" => new ParsedCommand(CommandKind.SceneRemoveComponent),
            "list-components" => new ParsedCommand(CommandKind.SceneListComponents),
            "assign-material" => new ParsedCommand(CommandKind.SceneAssignMaterial),
            _ => throw new CliUsageException($"Unknown scene subcommand: {subCommand}"),
        };
    }

    private static ParsedCommand ParsePrefab(Queue<string> tokens)
    {
        if (tokens.Count == 0)
        {
            throw new CliUsageException("`prefab` requires a subcommand.");
        }

        var subCommand = tokens.Dequeue().ToLowerInvariant();
        return subCommand switch
        {
            "inspect" => new ParsedCommand(CommandKind.PrefabInspect),
            "create" => new ParsedCommand(CommandKind.PrefabCreate),
            "patch" => new ParsedCommand(CommandKind.PrefabPatch),
            "add-component" => new ParsedCommand(CommandKind.PrefabAddComponent),
            "remove-component" => new ParsedCommand(CommandKind.PrefabRemoveComponent),
            "list-components" => new ParsedCommand(CommandKind.PrefabListComponents),
            _ => throw new CliUsageException($"Unknown prefab subcommand: {subCommand}"),
        };
    }

    private static ParsedCommand ParsePackage(Queue<string> tokens)
    {
        if (tokens.Count == 0)
        {
            throw new CliUsageException("`package` requires a subcommand.");
        }

        var subCommand = tokens.Dequeue().ToLowerInvariant();
        return subCommand switch
        {
            "list" => new ParsedCommand(CommandKind.PackageList),
            "add" => new ParsedCommand(CommandKind.PackageAdd),
            "remove" => new ParsedCommand(CommandKind.PackageRemove),
            "search" => new ParsedCommand(CommandKind.PackageSearch),
            _ => throw new CliUsageException($"Unknown package subcommand: {subCommand}"),
        };
    }

    private static ParsedCommand ParseTestCommand(Queue<string> tokens)
    {
        if (tokens.Count == 0)
        {
            throw new CliUsageException("`test` requires one of `list`, `run`, `results`, or `cancel`.");
        }

        var subCommand = tokens.Dequeue().ToLowerInvariant();
        return subCommand switch
        {
            "list" => ParseTestList(),
            "run" => ParseTestRun(),
            "results" => ParseTestResults(),
            "cancel" => ParseTestCancel(),
            _ => throw new CliUsageException($"Unknown test subcommand: {subCommand}"),
        };
    }

    private static ParsedCommand ParseTestList()
    {
        return new ParsedCommand(CommandKind.TestList)
        {
            TestMode = "all",
        };
    }

    private static ParsedCommand ParseTestRun()
    {
        return new ParsedCommand(CommandKind.TestRun);
    }

    private static ParsedCommand ParseTestResults()
    {
        return new ParsedCommand(CommandKind.TestResults);
    }

    private static ParsedCommand ParseTestCancel()
    {
        return new ParsedCommand(CommandKind.TestCancel);
    }

    private static ParsedCommand ParseRecordCommand(Queue<string> tokens)
    {
        if (tokens.Count == 0)
        {
            throw new CliUsageException("`record` requires one of `start`, `stop`, or `status`.");
        }

        var subCommand = tokens.Dequeue().ToLowerInvariant();
        return subCommand switch
        {
            "start" => new ParsedCommand(CommandKind.RecordStart),
            "stop" => new ParsedCommand(CommandKind.RecordStop),
            "status" => new ParsedCommand(CommandKind.RecordStatus),
            _ => throw new CliUsageException($"Unknown record subcommand: {subCommand}"),
        };
    }

    private static ParsedCommand ParseEditorCommand(Queue<string> tokens)
    {
        if (tokens.Count == 0)
        {
            throw new CliUsageException("`editor` requires `launch` or `stop`.");
        }

        var subCommand = tokens.Dequeue().ToLowerInvariant();
        return subCommand switch
        {
            "launch" => new ParsedCommand(CommandKind.EditorLaunch),
            "stop" => new ParsedCommand(CommandKind.EditorStop),
            _ => throw new CliUsageException($"Unknown editor subcommand: {subCommand}"),
        };
    }

    private static ParsedCommand ParseProfile(Queue<string> tokens)
    {
        if (tokens.Count == 0)
        {
            throw new CliUsageException("`profile` requires one of `stats`, `capture`, `status`, `analyze`, `compare`, or `memory`.");
        }

        var subCommand = tokens.Dequeue().ToLowerInvariant();
        switch (subCommand)
        {
            case "stats":
                return new ParsedCommand(CommandKind.ProfileStats);
            case "capture":
            {
                if (tokens.Count == 0)
                {
                    throw new CliUsageException("`profile capture` requires `start` or `stop`.");
                }

                var captureSub = tokens.Dequeue().ToLowerInvariant();
                return captureSub switch
                {
                    "start" => new ParsedCommand(CommandKind.ProfileCaptureStart),
                    "stop" => new ParsedCommand(CommandKind.ProfileCaptureStop),
                    _ => throw new CliUsageException($"Unknown profile capture subcommand: {captureSub}"),
                };
            }
            case "status":
            {
                var parsed = new ParsedCommand(CommandKind.ProfileStatus);
                if (tokens.Count > 0 && !tokens.Peek().StartsWith("--", StringComparison.Ordinal))
                {
                    parsed.ProfileCaptureId = tokens.Dequeue();
                }

                return parsed;
            }
            case "analyze":
            {
                if (tokens.Count == 0 || tokens.Peek().StartsWith("--", StringComparison.Ordinal))
                {
                    throw new CliUsageException("`profile analyze` requires <captureId>.");
                }

                var parsed = new ParsedCommand(CommandKind.ProfileAnalyze)
                {
                    ProfileCaptureId = tokens.Dequeue(),
                };
                return parsed;
            }
            case "compare":
            {
                if (tokens.Count == 0 || tokens.Peek().StartsWith("--", StringComparison.Ordinal))
                {
                    throw new CliUsageException("`profile compare` requires <baseCaptureId> and <headCaptureId>.");
                }

                var parsed = new ParsedCommand(CommandKind.ProfileCompare)
                {
                    ProfileCompareBaseId = tokens.Dequeue(),
                };
                if (tokens.Count == 0 || tokens.Peek().StartsWith("--", StringComparison.Ordinal))
                {
                    throw new CliUsageException("`profile compare` also requires the comparison target <headCaptureId>.");
                }

                parsed.ProfileCompareHeadId = tokens.Dequeue();
                return parsed;
            }
            case "memory":
            {
                if (tokens.Count > 0 && !tokens.Peek().StartsWith("--", StringComparison.Ordinal))
                {
                    var memorySub = tokens.Dequeue().ToLowerInvariant();
                    switch (memorySub)
                    {
                        case "compare":
                        {
                            if (tokens.Count == 0 || tokens.Peek().StartsWith("--", StringComparison.Ordinal))
                            {
                                throw new CliUsageException("`profile memory compare` requires <baseReportId> and <headReportId>.");
                            }

                            var parsed = new ParsedCommand(CommandKind.ProfileMemoryCompare)
                            {
                                ProfileCompareBaseId = tokens.Dequeue(),
                            };
                            if (tokens.Count == 0 || tokens.Peek().StartsWith("--", StringComparison.Ordinal))
                            {
                                throw new CliUsageException("`profile memory compare` also requires the comparison target <headReportId>.");
                            }

                            parsed.ProfileCompareHeadId = tokens.Dequeue();
                            return parsed;
                        }

                        case "snapshot":
                            return new ParsedCommand(CommandKind.ProfileMemorySnapshot);

                        case "top":
                            return new ParsedCommand(CommandKind.ProfileMemoryTop);

                        default:
                            throw new CliUsageException($"Unknown profile memory subcommand: {memorySub}");
                    }
                }

                return new ParsedCommand(CommandKind.ProfileMemory);
            }

            default:
                throw new CliUsageException($"Unknown profile subcommand: {subCommand}");
        }
    }

    private static ParsedCommand ParseMaterial(Queue<string> tokens)
    {
        if (tokens.Count == 0)
        {
            throw new CliUsageException("`material` requires a subcommand.");
        }

        var subCommand = tokens.Dequeue().ToLowerInvariant();
        return subCommand switch
        {
            "info" => new ParsedCommand(CommandKind.MaterialInfo),
            "set" => new ParsedCommand(CommandKind.MaterialSet),
            _ => throw new CliUsageException($"Unknown material subcommand: {subCommand}"),
        };
    }

    private static ParsedCommand ParseQa(Queue<string> tokens)
    {
        string subCommand = RequireSubcommand(tokens, "qa");
        return subCommand switch
        {
            "click" => new ParsedCommand(CommandKind.QaClick),
            "tap" => new ParsedCommand(CommandKind.QaTap),
            "swipe" => new ParsedCommand(CommandKind.QaSwipe),
            "key" => new ParsedCommand(CommandKind.QaKey),
            "ui-dump" => new ParsedCommand(CommandKind.QaUiDump),
            "world-dump" => new ParsedCommand(CommandKind.QaWorldDump),
            "wait" => new ParsedCommand(CommandKind.QaWait),
            "wait-until" => new ParsedCommand(CommandKind.QaWaitUntil),
            "run-sequence" => new ParsedCommand(CommandKind.QaRunSequence),
            _ => throw new CliUsageException($"Unknown qa subcommand: {subCommand}"),
        };
    }

    private static ParsedCommand ParseCustom(Queue<string> tokens)
    {
        if (tokens.Count == 0)
        {
            throw new CliUsageException("`custom` requires a command name.");
        }

        var commandName = tokens.Dequeue();
        return new ParsedCommand(CommandKind.Custom)
        {
            CustomCommandName = commandName,
        };
    }

    private static void ParseCommandOptions(ParsedCommand parsed, Queue<string> tokens)
    {
        while (tokens.Count > 0)
        {
            var token = tokens.Dequeue();
            if (token == "--project")
            {
                parsed.ProjectOverride = RequireValue(tokens, "--project");
                continue;
            }

            if (token == "--timeout-ms")
            {
                parsed.TimeoutMs = RequireInt(RequireValue(tokens, "--timeout-ms"), "--timeout-ms");
                parsed.TimeoutMsSpecified = true;
                continue;
            }

            if (token == "--output")
            {
                parsed.OutputMode = RequireOutputMode(RequireValue(tokens, "--output"));
                continue;
            }

            if (token == "--json" && parsed.Kind != CommandKind.Raw && parsed.Kind != CommandKind.Custom)
            {
                parsed.OutputMode = OutputMode.Json;
                continue;
            }

            switch (parsed.Kind)
            {
                case CommandKind.Raw when token == "--json":
                    parsed.RawJson = RequireValue(tokens, "--json");
                    break;
                case CommandKind.Raw when token == "--force":
                    parsed.Force = true;
                    break;
                case CommandKind.Compile or CommandKind.Refresh or CommandKind.Play or CommandKind.Stop when token == "--wait":
                    parsed.Wait = true;
                    break;
                case CommandKind.Compile or CommandKind.Refresh or CommandKind.Play or CommandKind.Stop when token == "--no-wait":
                    parsed.Wait = false;
                    break;
                case CommandKind.ReadConsole when token == "--limit":
                    parsed.ConsoleLimit = RequireInt(RequireValue(tokens, "--limit"), "--limit");
                    break;
                case CommandKind.ReadConsole when token == "--type":
                    parsed.ConsoleType = RequireConsoleType(RequireValue(tokens, "--type"));
                    break;
                case CommandKind.ReadConsole when token == "--no-stacktrace":
                    parsed.ConsoleNoStackTrace = true;
                    break;
                case CommandKind.ExecuteMenu when token == "--path":
                    parsed.MenuPath = RequireValue(tokens, "--path");
                    break;
                case CommandKind.ExecuteMenu when token == "--list":
                    parsed.MenuList = true;
                    parsed.MenuListPrefix = RequireValue(tokens, "--list");
                    break;
                case CommandKind.Screenshot when token == "--view":
                    parsed.ScreenshotView = RequireScreenshotView(RequireValue(tokens, "--view"));
                    break;
                case CommandKind.Screenshot when token == "--camera":
                    parsed.ScreenshotCamera = RequireValue(tokens, "--camera");
                    break;
                case CommandKind.Screenshot when token == "--path":
                    parsed.ScreenshotPath = RequireValue(tokens, "--path");
                    break;
                case CommandKind.Screenshot when token == "--width":
                    parsed.ScreenshotWidth = RequireInt(RequireValue(tokens, "--width"), "--width");
                    break;
                case CommandKind.Screenshot when token == "--height":
                    parsed.ScreenshotHeight = RequireInt(RequireValue(tokens, "--height"), "--height");
                    break;
                case CommandKind.Screenshot when token == "--format":
                    parsed.ScreenshotFormat = RequireScreenshotFormat(RequireValue(tokens, "--format"));
                    break;
                case CommandKind.Screenshot when token == "--quality":
                    parsed.ScreenshotQuality = RequireScreenshotQuality(RequireValue(tokens, "--quality"));
                    break;
                case CommandKind.Screenshot when token == "--max-width":
                    // 0 is allowed and means "no cap" — the way to opt out of the default downscale
                    // without having to state an explicit --width/--height.
                    parsed.ScreenshotMaxWidth = RequireInt(RequireValue(tokens, "--max-width"), "--max-width", minimumValue: 0);
                    break;
                case CommandKind.ExecuteCode when token == "--code":
                    parsed.ExecuteCodeSnippet = RequireValue(tokens, "--code");
                    break;
                case CommandKind.ExecuteCode when token == "--file":
                    parsed.ExecuteCodeFile = RequireValue(tokens, "--file");
                    break;
                case CommandKind.ExecuteCode when token == "--args":
                    parsed.ExecuteCodeArgsJson = RequireJsonValue(tokens, "--args");
                    break;
                case CommandKind.ExecuteCode when token == "--timeout":
                    parsed.ExecuteCodeTimeoutSeconds = RequireInt(RequireValue(tokens, "--timeout"), "--timeout");
                    break;
                case CommandKind.ExecuteCode when token == "--force":
                    parsed.Force = true;
                    break;
                case CommandKind.Custom when token == "--json":
                    parsed.CustomArgsJson = RequireValue(tokens, "--json");
                    break;
                case CommandKind.PackageAdd when token == "--name":
                    parsed.PackageName = RequireValue(tokens, "--name");
                    break;
                case CommandKind.PackageAdd when token == "--version":
                    parsed.PackageVersion = RequireValue(tokens, "--version");
                    break;
                case CommandKind.PackageRemove when token == "--name":
                    parsed.PackageName = RequireValue(tokens, "--name");
                    break;
                case CommandKind.PackageRemove when token == "--force":
                    parsed.Force = true;
                    break;
                case CommandKind.PackageList when token == "--filter":
                    parsed.PackageFilter = RequireValue(tokens, "--filter");
                    break;
                case CommandKind.PackageList when token == "--limit":
                    parsed.PackageLimit = RequireInt(RequireValue(tokens, "--limit"), "--limit", minimumValue: null);
                    break;
                case CommandKind.PackageSearch when token == "--query":
                    parsed.PackageQuery = RequireValue(tokens, "--query");
                    break;
                case CommandKind.TestList when token == "--mode":
                    parsed.TestMode = RequireTestMode(RequireValue(tokens, "--mode"), allowAll: true);
                    break;
                case CommandKind.TestList when token == "--no-detail":
                    parsed.TestNoDetail = true;
                    break;
                case CommandKind.TestRun when token == "--mode":
                    parsed.TestMode = RequireTestMode(RequireValue(tokens, "--mode"), allowAll: false);
                    break;
                case CommandKind.TestRun when token == "--filter":
                    parsed.TestFilter = RequireValue(tokens, "--filter");
                    break;
                case CommandKind.TestRun when token == "--category":
                    parsed.TestCategory = RequireValue(tokens, "--category");
                    break;
                case CommandKind.TestRun when token == "--assembly":
                    parsed.TestAssembly = RequireValue(tokens, "--assembly");
                    break;
                case CommandKind.TestRun when token == "--no-domain-reload":
                    parsed.TestNoDomainReload = true;
                    break;
                case CommandKind.TestRun when token == "--failures-only":
                    parsed.TestFailuresOnly = true;
                    break;
                case CommandKind.TestRun when token == "--timeout":
                    parsed.TestTimeoutSeconds = RequireInt(RequireValue(tokens, "--timeout"), "--timeout");
                    break;
                case CommandKind.TestRun when token == "--wait":
                    parsed.TestWait = true;
                    break;
                case CommandKind.TestResults when token == "--run-id":
                    parsed.TestRunId = RequireValue(tokens, "--run-id");
                    break;
                case CommandKind.TestResults when token == "--failures-only":
                    parsed.TestFailuresOnly = true;
                    break;
                case CommandKind.RecordStart when token == "--path":
                    parsed.RecordPath = RequireValue(tokens, "--path");
                    break;
                case CommandKind.RecordStart when token == "--duration":
                    parsed.RecordDuration = RequireInt(RequireValue(tokens, "--duration"), "--duration");
                    break;
                case CommandKind.RecordStart when token == "--fps":
                    parsed.RecordFps = RequireInt(RequireValue(tokens, "--fps"), "--fps");
                    break;
                case CommandKind.RecordStart when token == "--max-width":
                    parsed.RecordMaxWidth = RequireInt(RequireValue(tokens, "--max-width"), "--max-width");
                    break;
                case CommandKind.RecordStart when token == "--wait":
                    parsed.RecordWait = true;
                    break;
                case CommandKind.RecordStatus when token == "--recording-id":
                    parsed.RecordRunId = RequireValue(tokens, "--recording-id");
                    break;
                case CommandKind.EditorLaunch when token == "--gui":
                    parsed.EditorGui = true;
                    break;
                case CommandKind.EditorLaunch when token == "--nographics":
                    parsed.EditorNoGraphics = true;
                    break;
                case CommandKind.EditorLaunch when token == "--editor-path":
                    parsed.EditorPathOverride = RequireValue(tokens, "--editor-path");
                    break;
                case CommandKind.EditorLaunch or CommandKind.EditorStop when token == "--no-wait":
                    parsed.EditorNoWait = true;
                    break;
                case CommandKind.EditorLaunch or CommandKind.EditorStop when token == "--timeout":
                    parsed.EditorWaitTimeoutSeconds = RequireInt(RequireValue(tokens, "--timeout"), "--timeout");
                    break;
                case CommandKind.EditorStop when token == "--force":
                    parsed.Force = true;
                    break;
                case CommandKind.MaterialInfo when token == "--path":
                case CommandKind.MaterialSet when token == "--path":
                    parsed.MaterialPath = RequireAssetPath(RequireValue(tokens, "--path"), "--path");
                    break;
                case CommandKind.MaterialInfo when token == "--omit-defaults":
                    parsed.OmitDefaults = true;
                    break;
                case CommandKind.MaterialSet when token == "--property":
                    parsed.MaterialProperty = RequireValue(tokens, "--property");
                    break;
                case CommandKind.MaterialSet when token == "--value":
                    parsed.MaterialValue = RequireValue(tokens, "--value");
                    break;
                case CommandKind.MaterialSet when token == "--texture":
                    parsed.MaterialTexture = RequireValue(tokens, "--texture");
                    break;
                case CommandKind.MaterialSet when token == "--asset":
                    parsed.MaterialTextureAsset = RequireAssetPath(RequireValue(tokens, "--asset"), "--asset");
                    break;
                case CommandKind.QaClick when token == "--qa-id":
                case CommandKind.QaWaitUntil when token == "--qa-id":
                    parsed.QaId = RequireValue(tokens, "--qa-id");
                    break;
                case CommandKind.QaClick when token == "--target":
                case CommandKind.QaSwipe when token == "--target":
                case CommandKind.QaTap when token == "--target":
                    parsed.QaTarget = RequireValue(tokens, "--target");
                    break;
                case CommandKind.QaClick when token == "--button":
                case CommandKind.QaTap when token == "--button":
                case CommandKind.QaSwipe when token == "--button":
                    parsed.QaButton = RequireQaButton(RequireValue(tokens, "--button"));
                    break;
                case CommandKind.QaTap when token == "--x":
                    parsed.QaTapX = RequireInt(RequireValue(tokens, "--x"), "--x", minimumValue: null);
                    break;
                case CommandKind.QaTap when token == "--y":
                    parsed.QaTapY = RequireInt(RequireValue(tokens, "--y"), "--y", minimumValue: null);
                    break;
                case CommandKind.QaTap when token == "--screenshot-width":
                case CommandKind.QaUiDump when token == "--screenshot-width":
                case CommandKind.QaWorldDump when token == "--screenshot-width":
                case CommandKind.QaSwipe when token == "--screenshot-width":
                    parsed.QaScreenshotWidth = RequireInt(RequireValue(tokens, "--screenshot-width"), "--screenshot-width");
                    break;
                case CommandKind.QaTap when token == "--screenshot-height":
                case CommandKind.QaUiDump when token == "--screenshot-height":
                case CommandKind.QaWorldDump when token == "--screenshot-height":
                case CommandKind.QaSwipe when token == "--screenshot-height":
                    parsed.QaScreenshotHeight = RequireInt(RequireValue(tokens, "--screenshot-height"), "--screenshot-height");
                    break;
                case CommandKind.QaWorldDump when token == "--include-offscreen":
                    parsed.QaWorldDumpIncludeOffscreen = true;
                    break;
                case CommandKind.QaUiDump when token == "--limit":
                case CommandKind.QaWorldDump when token == "--limit":
                    parsed.QaDumpLimit = RequireInt(RequireValue(tokens, "--limit"), "--limit", minimumValue: 0);
                    break;
                case CommandKind.QaUiDump when token == "--interactable-only":
                    parsed.QaDumpInteractableOnly = true;
                    break;
                case CommandKind.QaUiDump when token == "--text":
                case CommandKind.QaWorldDump when token == "--text":
                    parsed.QaDumpText = RequireValue(tokens, "--text");
                    break;
                case CommandKind.QaUiDump when token == "--omit-rect":
                    parsed.QaDumpOmitRect = true;
                    break;
                case CommandKind.QaSwipe when token == "--from":
                    parsed.QaSwipeFrom = RequireValue(tokens, "--from");
                    break;
                case CommandKind.QaSwipe when token == "--to":
                    parsed.QaSwipeTo = RequireValue(tokens, "--to");
                    break;
                case CommandKind.QaSwipe when token == "--duration":
                    parsed.QaSwipeDuration = RequireInt(RequireValue(tokens, "--duration"), "--duration");
                    break;
                case CommandKind.QaKey when token == "--key":
                    parsed.QaKeyName = RequireValue(tokens, "--key");
                    break;
                case CommandKind.QaWait when token == "--ms":
                    parsed.QaWaitMs = RequireInt(RequireValue(tokens, "--ms"), "--ms");
                    break;
                case CommandKind.QaWaitUntil when token == "--scene":
                    parsed.QaWaitScene = RequireValue(tokens, "--scene");
                    break;
                case CommandKind.QaWaitUntil when token == "--log-contains":
                    parsed.QaWaitLogContains = RequireValue(tokens, "--log-contains");
                    break;
                case CommandKind.QaWaitUntil when token == "--object-exists":
                    parsed.QaWaitObjectExists = RequireValue(tokens, "--object-exists");
                    break;
                case CommandKind.QaWaitUntil when token == "--object-interactable":
                    parsed.QaWaitObjectInteractable = RequireValue(tokens, "--object-interactable");
                    break;
                case CommandKind.QaWaitUntil when token == "--object-gone":
                    parsed.QaWaitObjectGone = RequireValue(tokens, "--object-gone");
                    break;
                case CommandKind.QaWaitUntil when token == "--timeout":
                    parsed.QaWaitTimeout = RequireInt(RequireValue(tokens, "--timeout"), "--timeout");
                    break;
                case CommandKind.QaRunSequence when token == "--spec-json":
                {
                    string raw = RequireValue(tokens, "--spec-json");
                    string specJson = raw.StartsWith("@", StringComparison.Ordinal)
                        ? ReadSpecJsonFile(raw.Substring(1))
                        : raw;
                    parsed.QaSequenceArgs = QaSequenceSpecParser.Parse(specJson);
                    break;
                }
                case CommandKind.QaRunSequence when token == "--timeout":
                    parsed.QaSequenceTimeoutMs = RequireInt(RequireValue(tokens, "--timeout"), "--timeout");
                    break;
                case CommandKind.QaRunSequence when token == "--record":
                    parsed.QaSequenceRecord = true;
                    break;
                case CommandKind.QaRunSequence when token == "--record-path":
                    parsed.QaSequenceRecord = true;
                    parsed.QaSequenceRecordPath = RequireValue(tokens, "--record-path");
                    break;
                case CommandKind.QaRunSequence when token == "--profile":
                    parsed.QaSequenceProfile = true;
                    break;
                case CommandKind.ProfileMemory when token == "--frames":
                case CommandKind.ProfileStats when token == "--frames":
                    parsed.ProfileFrames = RequireInt(RequireValue(tokens, "--frames"), "--frames");
                    break;
                case CommandKind.ProfileStats when token == "--preset":
                {
                    string preset = RequireValue(tokens, "--preset").ToLowerInvariant();
                    if (preset is not ("frame" or "render" or "gc" or "memory" or "all"))
                    {
                        throw new CliUsageException("--preset must be one of frame, render, gc, memory, or all.");
                    }

                    parsed.ProfilePreset = preset;
                    break;
                }
                case CommandKind.ProfileCaptureStart when token == "--frames":
                    parsed.ProfileFrames = RequireInt(RequireValue(tokens, "--frames"), "--frames");
                    break;
                case CommandKind.ProfileCaptureStart when token == "--duration":
                    parsed.ProfileDuration = RequireInt(RequireValue(tokens, "--duration"), "--duration");
                    break;
                case CommandKind.ProfileCaptureStart when token == "--budget-ms":
                {
                    string rawBudget = RequireValue(tokens, "--budget-ms");
                    if (!double.TryParse(rawBudget, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double budget) || budget <= 0)
                    {
                        throw new CliUsageException("--budget-ms must be a positive number of milliseconds.");
                    }

                    parsed.ProfileBudgetMs = budget;
                    break;
                }
                case CommandKind.ProfileCaptureStop when token == "--wait":
                    parsed.ProfileWait = true;
                    break;
                case CommandKind.ProfileAnalyze when token == "--marker":
                    parsed.ProfileAnalyzeMarker = RequireValue(tokens, "--marker");
                    break;
                case CommandKind.ProfileAnalyze when token == "--frame":
                    parsed.ProfileAnalyzeFrame = RequireInt(RequireValue(tokens, "--frame"), "--frame", minimumValue: 0);
                    break;
                case CommandKind.ProfileAnalyze when token == "--gc":
                    parsed.ProfileAnalyzeGc = true;
                    break;
                case CommandKind.ProfileAnalyze when token == "--spikes":
                    parsed.ProfileAnalyzeSpikes = true;
                    break;
                case CommandKind.ProfileAnalyze when token == "--limit":
                    parsed.ProfileLimit = RequireInt(RequireValue(tokens, "--limit"), "--limit");
                    break;
                case CommandKind.ProfileMemoryCompare when token == "--threshold":
                case CommandKind.ProfileCompare when token == "--threshold":
                {
                    string rawThreshold = RequireValue(tokens, "--threshold");
                    if (!double.TryParse(rawThreshold, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double threshold))
                    {
                        throw new CliUsageException("--threshold must be a numeric percent.");
                    }

                    parsed.ProfileThresholdPercent = threshold;
                    break;
                }
                case CommandKind.ProfileMemoryCompare when token == "--limit":
                case CommandKind.ProfileCompare when token == "--limit":
                    parsed.ProfileLimit = RequireInt(RequireValue(tokens, "--limit"), "--limit");
                    break;
                case CommandKind.ProfileMemoryTop when token == "--type":
                {
                    string rawType = RequireValue(tokens, "--type");
                    if (!ProfileMemoryTopCatalog.TryNormalizeType(rawType, out string normalizedType))
                    {
                        throw new CliUsageException("`--type` must be one of " + ProfileMemoryTopCatalog.AcceptedTypeList() + ".");
                    }

                    parsed.ProfileMemoryTopType = normalizedType;
                    break;
                }
                case CommandKind.ProfileMemoryTop when token == "--scope":
                {
                    string rawScope = RequireValue(tokens, "--scope");
                    if (string.IsNullOrWhiteSpace(rawScope)
                        || !ProfileMemoryTopCatalog.TryNormalizeScope(rawScope, out string normalizedScope))
                    {
                        throw new CliUsageException("`--scope` must be `loaded`, `scene`, or `assets`.");
                    }

                    parsed.ProfileMemoryTopScope = normalizedScope;
                    break;
                }
                case CommandKind.ProfileMemoryTop when token == "--limit":
                {
                    int limit = RequireInt(RequireValue(tokens, "--limit"), "--limit");
                    if (limit > ProtocolConstants.MaxProfileMemoryTopLimit)
                    {
                        throw new CliUsageException(
                            "--limit must be an integer from 1 to " + ProtocolConstants.MaxProfileMemoryTopLimit + ".");
                    }

                    parsed.ProfileLimit = limit;
                    break;
                }
                case CommandKind.AssetFind when token == "--name":
                    parsed.AssetName = RequireValue(tokens, "--name");
                    break;
                case CommandKind.AssetFind when token == "--type":
                    parsed.AssetType = RequireValue(tokens, "--type");
                    break;
                case CommandKind.AssetFind when token == "--folder":
                    parsed.AssetFolder = RequireAssetPath(RequireValue(tokens, "--folder"), "--folder");
                    break;
                case CommandKind.AssetFind when token == "--limit":
                    parsed.AssetLimit = RequireInt(RequireValue(tokens, "--limit"), "--limit");
                    break;
                case CommandKind.AssetInfo when token == "--path":
                    parsed.AssetPath = RequireAssetPath(RequireValue(tokens, "--path"), "--path", allowPackages: true);
                    break;
                case CommandKind.AssetInfo when token == "--guid":
                    parsed.AssetGuid = RequireValue(tokens, "--guid");
                    break;
                case CommandKind.AssetReimport when token == "--path":
                case CommandKind.AssetMkdir when token == "--path":
                case CommandKind.AssetDelete when token == "--path":
                    parsed.AssetPath = RequireAssetPath(RequireValue(tokens, "--path"), "--path");
                    break;
                case CommandKind.AssetMove when token == "--from":
                    parsed.AssetFrom = RequireAssetPath(RequireValue(tokens, "--from"), "--from");
                    break;
                case CommandKind.AssetMove when token == "--to":
                    parsed.AssetTo = RequireAssetPath(RequireValue(tokens, "--to"), "--to");
                    break;
                case CommandKind.AssetRename when token == "--path":
                    parsed.AssetPath = RequireAssetPath(RequireValue(tokens, "--path"), "--path");
                    break;
                case CommandKind.AssetRename when token == "--name":
                    parsed.AssetNewName = RequireValue(tokens, "--name");
                    break;
                case CommandKind.AssetMove when token == "--force":
                case CommandKind.AssetRename when token == "--force":
                case CommandKind.AssetDelete when token == "--force":
                case CommandKind.AssetCreate when token == "--force":
                    parsed.Force = true;
                    break;
                case CommandKind.AssetCreate when token == "--type":
                    parsed.AssetCreateType = RequireAssetCreateType(RequireValue(tokens, "--type"));
                    break;
                case CommandKind.AssetCreate when token == "--path":
                    parsed.AssetPath = RequireAssetPath(RequireValue(tokens, "--path"), "--path");
                    break;
                case CommandKind.AssetCreate when token == "--data-json":
                    parsed.AssetDataJson = RequireValue(tokens, "--data-json");
                    break;
                case CommandKind.AssetCreate when token == "--shader":
                    parsed.AssetShader = RequireValue(tokens, "--shader");
                    break;
                case CommandKind.AssetCreate when token == "--script":
                    parsed.AssetScript = RequireAssetPath(RequireValue(tokens, "--script"), "--script");
                    break;
                case CommandKind.AssetCreate when token == "--type-name":
                    parsed.AssetTypeName = RequireValue(tokens, "--type-name");
                    break;
                case CommandKind.AssetCreate when token == "--legacy":
                    parsed.AssetLegacy = true;
                    break;
                case CommandKind.AssetCreate when token == "--initial-map":
                    parsed.AssetInitialMap = RequireValue(tokens, "--initial-map");
                    break;
                case CommandKind.AssetCreate when token == "--root-name":
                    parsed.AssetRootName = RequireValue(tokens, "--root-name");
                    break;
                case CommandKind.AssetCreate when token == "--base-controller":
                    parsed.AssetBaseController = RequireAssetPath(RequireValue(tokens, "--base-controller"), "--base-controller");
                    break;
                case CommandKind.AssetCreate when token == "--width":
                    parsed.AssetWidth = RequireInt(RequireValue(tokens, "--width"), "--width");
                    break;
                case CommandKind.AssetCreate when token == "--height":
                    parsed.AssetHeight = RequireInt(RequireValue(tokens, "--height"), "--height");
                    break;
                case CommandKind.AssetCreate when token == "--depth":
                    parsed.AssetDepth = RequireInt(RequireValue(tokens, "--depth"), "--depth", minimumValue: 0);
                    break;
                case CommandKind.SceneOpen when token == "--path":
                case CommandKind.SceneInspect when token == "--path":
                case CommandKind.ScenePatch when token == "--path":
                case CommandKind.SceneAddObject when token == "--path":
                case CommandKind.SceneAddComponent when token == "--path":
                case CommandKind.SceneRemoveComponent when token == "--path":
                    parsed.ScenePath = RequireAssetPath(RequireValue(tokens, "--path"), "--path");
                    break;
                case CommandKind.SceneOpen when token == "--force":
                case CommandKind.ScenePatch when token == "--force":
                case CommandKind.SceneRemoveComponent when token == "--force":
                    parsed.Force = true;
                    break;
                case CommandKind.SceneInspect when token == "--with-values":
                    parsed.SceneWithValues = true;
                    break;
                case CommandKind.SceneInspect when token == "--node":
                    parsed.SceneTarget = RequireValue(tokens, "--node");
                    break;
                case CommandKind.SceneInspect when token == "--max-depth":
                    parsed.MaxDepth = RequireInt(RequireValue(tokens, "--max-depth"), "--max-depth");
                    break;
                case CommandKind.SceneInspect when token == "--omit-defaults":
                    parsed.OmitDefaults = true;
                    break;
                case CommandKind.ScenePatch when token == "--spec-file":
                    parsed.SceneSpecFile = RequireValue(tokens, "--spec-file");
                    break;
                case CommandKind.ScenePatch when token == "--spec-json":
                    parsed.SceneSpecJson = RequireValue(tokens, "--spec-json");
                    break;
                case CommandKind.SceneAddObject when token == "--parent":
                    parsed.SceneParent = RequireValue(tokens, "--parent");
                    break;
                case CommandKind.SceneAddObject when token == "--name":
                    parsed.SceneObjectName = RequireValue(tokens, "--name");
                    break;
                case CommandKind.SceneAddObject when token == "--primitive":
                    parsed.ScenePrimitive = RequireScenePrimitive(RequireValue(tokens, "--primitive"));
                    break;
                case CommandKind.SceneAddObject when token == "--components":
                    parsed.SceneComponents = RequireValue(tokens, "--components");
                    break;
                case CommandKind.SceneAddComponent when token == "--node":
                case CommandKind.SceneRemoveComponent when token == "--node":
                case CommandKind.SceneListComponents when token == "--node":
                case CommandKind.SceneSetTransform when token == "--node":
                case CommandKind.SceneAssignMaterial when token == "--node":
                    parsed.SceneTarget = RequireValue(tokens, "--node");
                    break;
                case CommandKind.SceneAddObject when token == "--position":
                    parsed.ScenePosition = RequireValue(tokens, "--position");
                    break;
                case CommandKind.SceneSetTransform when token == "--position":
                    parsed.ScenePosition = RequireValue(tokens, "--position");
                    break;
                case CommandKind.SceneSetTransform when token == "--rotation":
                    parsed.SceneRotation = RequireValue(tokens, "--rotation");
                    break;
                case CommandKind.SceneSetTransform when token == "--scale":
                    parsed.SceneScale = RequireValue(tokens, "--scale");
                    break;
                case CommandKind.SceneAddComponent when token == "--type":
                case CommandKind.SceneRemoveComponent when token == "--type":
                    parsed.SceneComponentType = RequireValue(tokens, "--type");
                    break;
                case CommandKind.SceneRemoveComponent when token == "--index":
                case CommandKind.PrefabRemoveComponent when token == "--index":
                    parsed.SceneComponentIndex = RequireInt(RequireValue(tokens, "--index"), "--index", minimumValue: 0);
                    break;
                case CommandKind.SceneAddComponent when token == "--values":
                    parsed.SceneComponentValues = RequireValue(tokens, "--values");
                    break;
                case CommandKind.SceneAssignMaterial when token == "--material":
                    parsed.MaterialPath = RequireAssetPath(RequireValue(tokens, "--material"), "--material");
                    break;
                case CommandKind.PrefabInspect when token == "--path":
                    parsed.PrefabPath = RequireAssetPath(RequireValue(tokens, "--path"), "--path");
                    break;
                case CommandKind.PrefabInspect when token == "--with-values":
                    parsed.PrefabWithValues = true;
                    break;
                case CommandKind.PrefabInspect when token == "--node":
                    parsed.SceneTarget = RequireValue(tokens, "--node");
                    break;
                case CommandKind.PrefabInspect when token == "--max-depth":
                    parsed.MaxDepth = RequireInt(RequireValue(tokens, "--max-depth"), "--max-depth");
                    break;
                case CommandKind.PrefabInspect when token == "--omit-defaults":
                    parsed.OmitDefaults = true;
                    break;
                case CommandKind.PrefabCreate when token == "--path":
                case CommandKind.PrefabPatch when token == "--path":
                case CommandKind.PrefabAddComponent when token == "--path":
                case CommandKind.PrefabRemoveComponent when token == "--path":
                case CommandKind.PrefabListComponents when token == "--path":
                    parsed.PrefabPath = RequireAssetPath(RequireValue(tokens, "--path"), "--path");
                    break;
                case CommandKind.PrefabCreate when token == "--force":
                case CommandKind.PrefabPatch when token == "--force":
                case CommandKind.PrefabRemoveComponent when token == "--force":
                    parsed.Force = true;
                    break;
                case CommandKind.PrefabCreate when token == "--spec-file":
                case CommandKind.PrefabPatch when token == "--spec-file":
                    parsed.PrefabSpecFile = RequireValue(tokens, "--spec-file");
                    break;
                case CommandKind.PrefabCreate when token == "--spec-json":
                case CommandKind.PrefabPatch when token == "--spec-json":
                    parsed.PrefabSpecJson = RequireValue(tokens, "--spec-json");
                    break;
                case CommandKind.PrefabAddComponent when token == "--node":
                case CommandKind.PrefabRemoveComponent when token == "--node":
                case CommandKind.PrefabListComponents when token == "--node":
                    parsed.SceneTarget = RequireValue(tokens, "--node");
                    break;
                case CommandKind.PrefabAddComponent when token == "--type":
                case CommandKind.PrefabRemoveComponent when token == "--type":
                    parsed.SceneComponentType = RequireValue(tokens, "--type");
                    break;
                case CommandKind.PrefabAddComponent when token == "--values":
                    parsed.SceneComponentValues = RequireValue(tokens, "--values");
                    break;
                case CommandKind.InstancesList when token == "--brief":
                    parsed.InstancesBrief = true;
                    break;
                default:
                    if (parsed.Kind == CommandKind.AssetCreate && token.StartsWith("--", StringComparison.Ordinal))
                    {
                        AddAssetCreateCustomOption(parsed, token, tokens);
                        break;
                    }

                    throw new CliUsageException($"Unsupported option: {token}");
            }
        }

        ValidateExecuteMenuOptions(parsed);
        ValidateAssetOptions(parsed);
        ValidateSceneOptions(parsed);
        ValidateScreenshotOptions(parsed);
        ValidatePackageOptions(parsed);
        ValidateTestOptions(parsed);
        ValidateRecordOptions(parsed);
        ValidateProfileOptions(parsed);
        ValidateMaterialOptions(parsed);
        ValidateQaOptions(parsed);
        ValidateExecuteOptions(parsed);

        if (parsed.Kind == CommandKind.QaRunSequence)
        {
            int overall = parsed.QaSequenceTimeoutMs > 0
                ? parsed.QaSequenceTimeoutMs
                : parsed.QaSequenceArgs?.timeoutMs > 0
                    ? parsed.QaSequenceArgs.timeoutMs
                : ProtocolConstants.DefaultQaRunSequenceTimeoutMs;
            parsed.TimeoutMs = overall + 5_000;
        }

        if (parsed.Kind == CommandKind.Raw && string.IsNullOrWhiteSpace(parsed.RawJson))
        {
            throw new CliUsageException("`raw` requires a `--json` payload.");
        }
    }

    private static string ReadSpecJsonFile(string path)
    {
        try
        {
            return File.ReadAllText(path);
        }
        catch (Exception exception) when (exception is IOException
            || exception is UnauthorizedAccessException
            || exception is NotSupportedException)
        {
            throw new CliUsageException($"Failed to read --spec-json @{path}: {exception.Message}");
        }
    }

    private static void AddAssetCreateCustomOption(ParsedCommand parsed, string token, Queue<string> tokens)
    {
        string optionName = NormalizeCustomOptionName(token);
        object value = true;
        if (tokens.Count > 0 && !tokens.Peek().StartsWith("--", StringComparison.Ordinal))
        {
            value = ParseCustomOptionValue(tokens.Dequeue());
        }

        parsed.AssetCustomOptions[optionName] = value;
    }

    private static object ParseCustomOptionValue(string rawValue)
    {
        if (bool.TryParse(rawValue, out bool boolValue))
        {
            return boolValue;
        }

        if (int.TryParse(rawValue, out int intValue))
        {
            return intValue;
        }

        if (double.TryParse(rawValue, out double doubleValue))
        {
            return doubleValue;
        }

        return rawValue;
    }

    private static string NormalizeCustomOptionName(string token)
    {
        string trimmed = token.Trim();
        if (!trimmed.StartsWith("--", StringComparison.Ordinal) || trimmed.Length <= 2)
        {
            throw new CliUsageException($"Unsupported option: {token}");
        }

        string body = trimmed[2..];
        string[] parts = body.Split('-', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0)
        {
            throw new CliUsageException($"Unsupported option: {token}");
        }

        return string.Concat(parts.Select((part, index) =>
            index == 0
                ? char.ToLowerInvariant(part[0]) + part[1..]
                : char.ToUpperInvariant(part[0]) + part[1..]));
    }

    private static bool IsBuiltInAssetCreateType(string? type)
    {
        return BuiltInAssetCreateCatalog.IsBuiltInType(type);
    }

    private static string ToKebabCase(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return value;
        }

        var builder = new System.Text.StringBuilder(value.Length + 4);
        for (int index = 0; index < value.Length; index++)
        {
            char character = value[index];
            if (char.IsUpper(character) && index > 0)
            {
                builder.Append('-');
            }

            builder.Append(char.ToLowerInvariant(character));
        }

        return builder.ToString();
    }

    private static OutputMode RequireOutputMode(string value)
    {
        if (value.StartsWith("--", StringComparison.Ordinal))
        {
            throw new CliUsageException("`--output` requires a value. Use `default`, `json`, or `compact`.");
        }

        return TryGetOutputMode(value)
            ?? throw new CliUsageException("`--output` must be `default`, `json`, or `compact`.");
    }

    private static OutputMode? TryGetOutputMode(string value)
    {
        return value.ToLowerInvariant() switch
        {
            "default" => OutputMode.Default,
            "json" => OutputMode.Json,
            "compact" => OutputMode.Compact,
            _ => null,
        };
    }

}
