#nullable enable
using System;
using System.Collections.Generic;
using System.Text;

namespace UnityCli.Protocol
{
    public enum CliCommandGroup
    {
        EditorControl,
        AssetWorkflows,
        SceneWorkflows,
        PrefabWorkflows,
        InstanceManagement,
        Diagnostics,
        PackageManagement,
        MaterialWorkflows,
        QaWorkflows,
    }

    public enum ForceRule
    {
        None,
        OnOverwrite,
        OnDestructiveOp,
        Always,
    }

    public sealed class CliCommandDescriptor
    {
        public CliCommandDescriptor(
            string command,
            string synopsis,
            string summary,
            CliCommandGroup group,
            string? protocolCommand,
            bool canUseLocal,
            bool canUseLive,
            bool isAllowedWhileBusy,
            string[]? notes = null,
            ForceRule forceRule = ForceRule.None,
            int? defaultLiveTimeoutMs = null,
            bool requiresGraphics = false)
        {
            Command = command;
            Synopsis = synopsis;
            Summary = summary;
            Group = group;
            ProtocolCommand = protocolCommand;
            CanUseLocal = canUseLocal;
            CanUseLive = canUseLive;
            IsAllowedWhileBusy = isAllowedWhileBusy;
            Notes = notes ?? Array.Empty<string>();
            ForceRule = forceRule;
            DefaultLiveTimeoutMs = defaultLiveTimeoutMs;
            RequiresGraphics = requiresGraphics;
        }

        public string Command { get; }

        public string Synopsis { get; }

        public string Summary { get; }

        public CliCommandGroup Group { get; }

        public string? ProtocolCommand { get; }

        public bool CanUseLocal { get; }

        public bool CanUseLive { get; }
        public bool IsAllowedWhileBusy { get; }
        public ForceRule ForceRule { get; }
        public int? DefaultLiveTimeoutMs { get; }
        public bool RequiresGraphics { get; }

        [Obsolete("Use ForceRule instead.")]
        public bool RequiresForce => ForceRule != ForceRule.None;

        public string[] Notes { get; }
    }

    public static class CliCommandCatalog
    {
        private static readonly CliCommandDescriptor[] _commands =
        {
            new CliCommandDescriptor(
                "status",
                "status",
                "Reports the selected project and live editor state when a running bridge is reachable, with a local fallback when it is not.",
                CliCommandGroup.EditorControl,
                ProtocolConstants.CommandStatus,
                canUseLocal: true,
                canUseLive: true,
                isAllowedWhileBusy: true,
                notes: new[] { "Falls back to local registry and Unity-path inspection when no live editor is reachable." }),
            new CliCommandDescriptor(
                "compile",
                "compile [--no-wait]",
                "Triggers a script compile in the running editor and waits until compiling/importing finishes and the bridge is reachable again. Add --no-wait to return right after the request.",
                CliCommandGroup.EditorControl,
                ProtocolConstants.CommandCompile,
                canUseLocal: false,
                canUseLive: true,
                isAllowedWhileBusy: false,
                notes: new[] { "Waits by default: polls status every 2s for up to 120s (or --timeout-ms) after the compile request succeeds. --wait is accepted for compatibility." }),
            new CliCommandDescriptor(
                "refresh",
                "refresh [--no-wait]",
                "Refreshes the AssetDatabase in the running editor and waits until the resulting compile/import finishes and the bridge is reachable again. Add --no-wait to return right after the refresh.",
                CliCommandGroup.EditorControl,
                ProtocolConstants.CommandRefresh,
                canUseLocal: false,
                canUseLive: true,
                isAllowedWhileBusy: false,
                notes: new[] { "Waits by default: polls status every 2s for up to 120s (or --timeout-ms) after the refresh request succeeds. --wait is accepted for compatibility." }),
            new CliCommandDescriptor(
                "read-console",
                "read-console [--limit N] [--type log|warning|error|exception|assert] [--no-stacktrace]",
                "Reads recent editor console entries from a running editor. --type error matches Error, Exception, and Assert; exception and assert match only that type, and each entry keeps its original type name. Add --no-stacktrace to omit stack traces from the response while keeping them in the editor buffer.",
                CliCommandGroup.EditorControl,
                ProtocolConstants.CommandReadConsole,
                canUseLocal: false,
                canUseLive: true,
                isAllowedWhileBusy: true),
            new CliCommandDescriptor(
                "play",
                "play [--no-wait]",
                "Starts Play Mode in a running editor and returns once Play Mode has fully entered and the bridge is reachable again after the domain reload. Add --no-wait to return right after the request.",
                CliCommandGroup.EditorControl,
                ProtocolConstants.CommandPlay,
                canUseLocal: false,
                canUseLive: true,
                isAllowedWhileBusy: false,
                notes: new[] { "Waits by default: polls status every 200ms for up to 120s (or --timeout-ms); bridge transport errors during the domain reload are tolerated for 30s." }),
            new CliCommandDescriptor(
                "pause",
                "pause",
                "Pauses Play Mode in a running editor.",
                CliCommandGroup.EditorControl,
                ProtocolConstants.CommandPause,
                canUseLocal: false,
                canUseLive: true,
                isAllowedWhileBusy: false),
            new CliCommandDescriptor(
                "stop",
                "stop [--no-wait]",
                "Stops Play Mode in a running editor and returns once Edit Mode is back. Add --no-wait to return right after the request.",
                CliCommandGroup.EditorControl,
                ProtocolConstants.CommandStop,
                canUseLocal: false,
                canUseLive: true,
                isAllowedWhileBusy: false,
                notes: new[] { "Waits by default: polls status every 200ms for up to 120s (or --timeout-ms)." }),
            new CliCommandDescriptor(
                "editor launch",
                "editor launch [--gui] [--nographics] [--no-wait] [--timeout <sec>] [--editor-path <path>]",
                "Launches the Unity Editor for the selected project (headless -batchmode by default, GPU kept for rendering commands). Idempotent: reuses a live instance when one is already running. Waits for bridge readiness unless --no-wait.",
                CliCommandGroup.EditorControl,
                protocolCommand: null,
                canUseLocal: true,
                canUseLive: false,
                isAllowedWhileBusy: true,
                notes: new[]
                {
                    "Default headless mode passes -batchmode only; the GPU stays initialized so screenshot/record/qa keep working without a window.",
                    "--nographics disables the GPU entirely; rendering commands then fail with HEADLESS_NO_GRAPHICS.",
                    "Pre-flight refuses to double-launch: a live registry match is reused, and an editor process outside the registry fails with EDITOR_ALREADY_RUNNING_CONFLICT.",
                }),
            new CliCommandDescriptor(
                "editor stop",
                "editor stop [--force] [--no-wait] [--timeout <sec>]",
                "Gracefully quits the running editor for the selected project. Refuses with EDITOR_DIRTY when unsaved scene/prefab-stage changes exist; --force discards them. Waits for process exit unless --no-wait.",
                CliCommandGroup.EditorControl,
                ProtocolConstants.CommandEditorQuit,
                canUseLocal: false,
                canUseLive: true,
                isAllowedWhileBusy: false,
                forceRule: ForceRule.OnDestructiveOp,
                notes: new[]
                {
                    "The bridge replies first and exits on the next editor tick, so the CLI receives a normal success response.",
                    "A graceful quit removes the instance registry entry and the auth-token sidecar.",
                }),
            new CliCommandDescriptor(
                "execute-menu",
                "execute-menu (--path \"Menu/Item\" | --list \"Prefix\")",
                "Executes a Unity menu item or lists registered menu items matching a prefix in a running editor.",
                CliCommandGroup.EditorControl,
                ProtocolConstants.CommandExecuteMenu,
                canUseLocal: false,
                canUseLive: true,
                isAllowedWhileBusy: false,
                notes: new[] { "Use --list to inspect registered menu item paths before executing one." }),
            new CliCommandDescriptor(
                "screenshot",
                "screenshot [--view game|scene (default: game) | --camera <name>] [--path <output.jpg|output.png>] [--width N] [--height N] [--format png|jpg|jpeg] [--quality 1-100] [--max-width N|0]",
                "Captures a screenshot from the Game View, Scene View, or a named camera. Defaults to Game View, JPEG at quality 75, and a 1024px width cap — agent-friendly defaults that cut image tokens by roughly 72% at 1080p. Override with --format png for lossless, --quality for the JPEG setting, --max-width N for a different cap, or --max-width 0 for no cap. The cap only applies when neither --width nor --height is given, and --path with a .png extension selects PNG when --format is omitted. The response includes image size, actual saved format, and screen-space metadata (`screenWidth`, `screenHeight`, `imageOrigin`, `coordinateOrigin`) for QA coordinate alignment; qa tap/ui-dump scale from the last captured size, so downscaled shots keep their coordinates usable. In Play Mode, --view game can downscale the native Game View capture but does not upscale it.",
                CliCommandGroup.EditorControl,
                ProtocolConstants.CommandScreenshot,
                canUseLocal: false,
                canUseLive: true,
                isAllowedWhileBusy: false,
                notes: new[]
                {
                    "Live-only.",
                    "--format controls encoding regardless of --path extension; temporary paths use .png or .jpg to match the selected format.",
                    "--max-width is ignored when --width or --height is specified.",
                    "Play Mode --view game captures at the native Game View size first; larger --width/--height requests warn and save at native resolution instead of upscaling.",
                },
                requiresGraphics: true),
            new CliCommandDescriptor(
                "record start",
                "record start [--path <output.mp4>] [--fps <n> (default: 30)] [--max-width <n>] [--duration <seconds>] [--wait]",
                "Starts recording the Game View to an mp4 file via Unity Recorder. Returns immediately with a recordingId. Requires Play Mode.",
                CliCommandGroup.EditorControl,
                ProtocolConstants.CommandRecordStart,
                canUseLocal: false,
                canUseLive: true,
                isAllowedWhileBusy: true,
                notes: new[]
                {
                    "Live-only. Requires Play Mode.",
                    "--duration auto-stops after N seconds; without it, recording runs until `record stop` or the 600s safety cap.",
                    "--wait polls until the recording is finalized and requires --duration.",
                    "--max-width scales the captured frame width; unset keeps the native Game View size.",
                },
                requiresGraphics: true),
            new CliCommandDescriptor(
                "record stop",
                "record stop",
                "Stops the active recording, finalizes the mp4, and returns the output path.",
                CliCommandGroup.EditorControl,
                ProtocolConstants.CommandRecordStop,
                canUseLocal: false,
                canUseLive: true,
                isAllowedWhileBusy: true,
                notes: new[]
                {
                    "Live-only.",
                    "Fails with RECORD_NOT_ACTIVE when no recording is active.",
                }),
            new CliCommandDescriptor(
                "record status",
                "record status [--recording-id <id>]",
                "Reports whether a recording is active and the result of a finished recording.",
                CliCommandGroup.EditorControl,
                ProtocolConstants.CommandRecordStatus,
                canUseLocal: false,
                canUseLive: true,
                isAllowedWhileBusy: true),
            new CliCommandDescriptor(
                "profile stats",
                "profile stats [--frames <n> (default: 60)] [--preset <frame|render|gc|memory|all> (default: all)]",
                "Samples built-in profiler counters over N frames via ProfilerRecorder and returns min/median/p95/max per counter. Works in Edit Mode and Play Mode; render/gc counters are only meaningful while playing.",
                CliCommandGroup.Diagnostics,
                ProtocolConstants.CommandProfileStats,
                canUseLocal: false,
                canUseLive: true,
                isAllowedWhileBusy: true,
                notes: new[]
                {
                    "Live-only. Waits N editor frames before responding; the CLI raises its IPC timeout accordingly.",
                    "Counters missing on the current Unity version are listed under `unavailable` instead of failing.",
                    "The render preset also samples Shadow Casters Count, Visible Skinned Meshes Count, Render Textures Count/Bytes, and Used Textures Count/Bytes. The memory preset also samples App Resident/Committed Memory, AudioClip Count/Memory, Physics Used Memory, Profiler Used/Reserved Memory, and System Total Used Memory.",
                }),
            new CliCommandDescriptor(
                "profile capture start",
                "profile capture start [--frames <n> | --duration <seconds>] [--budget-ms <ms> (default: 16.67)]",
                "Starts a Play Mode profiler capture and returns immediately with a captureId. Auto-stops after --frames/--duration or the 600s safety cap.",
                CliCommandGroup.Diagnostics,
                ProtocolConstants.CommandProfileCaptureStart,
                canUseLocal: false,
                canUseLive: true,
                isAllowedWhileBusy: true,
                notes: new[]
                {
                    "Live-only. Requires Play Mode.",
                    "Only one capture can run at a time (PROFILE_IN_PROGRESS otherwise).",
                    "--budget-ms sets the frame budget used for spike detection and the verdict.",
                }),
            new CliCommandDescriptor(
                "profile capture stop",
                "profile capture stop [--wait]",
                "Stops the active capture and starts summary processing (chunked frame walk). --wait polls `profile status` until the summary is ready and prints it.",
                CliCommandGroup.Diagnostics,
                ProtocolConstants.CommandProfileCaptureStop,
                canUseLocal: false,
                canUseLive: true,
                isAllowedWhileBusy: true,
                notes: new[]
                {
                    "Live-only. Fails with PROFILE_NOT_RUNNING when no capture is active.",
                    "Without --wait the response status is `Processing`; poll with `profile status <captureId>`.",
                }),
            new CliCommandDescriptor(
                "profile status",
                "profile status [<captureId>]",
                "Reports the state of the active capture (Capturing/Processing) or the finished summary read from the profiles sidecar.",
                CliCommandGroup.Diagnostics,
                ProtocolConstants.CommandProfileStatus,
                canUseLocal: false,
                canUseLive: true,
                isAllowedWhileBusy: true),
            new CliCommandDescriptor(
                "profile analyze",
                "profile analyze <captureId> (--marker <name> | --frame <n> | --gc | --spikes) [--limit <n> (default: 5)]",
                "Drills into a finished capture by reading its sidecar JSON locally — no Editor round-trip. Works after the Editor exits as long as the sidecar file exists.",
                CliCommandGroup.Diagnostics,
                null,
                canUseLocal: true,
                canUseLive: false,
                isAllowedWhileBusy: true,
                notes: new[]
                {
                    "Local-only. Resolves the project root the same way as other commands (--project, CWD).",
                    "--marker frame appearances come from each frame's top-10 table, not the full hierarchy.",
                }),
            new CliCommandDescriptor(
                "profile compare",
                "profile compare <baseCaptureId> <headCaptureId> [--threshold <percent> (default: 5.0)] [--limit <n> (default: 5)]",
                "Diffs two finished captures by reading both sidecar JSON files locally — no Editor round-trip. Returns a regression/improvement/unchanged verdict plus the frame-time, over-budget, GC, and per-marker deltas that explain it.",
                CliCommandGroup.Diagnostics,
                null,
                canUseLocal: true,
                canUseLive: false,
                isAllowedWhileBusy: true,
                notes: new[]
                {
                    "Local-only. Both captures must have a sidecar under the same project root.",
                    "--threshold is the median frame-time change (in percent) below which the verdict stays `unchanged`.",
                    "A capture that did not finish (any status other than Completed, or zero captured frames) is rejected with PROFILE_FAILED instead of being compared.",
                    "Captures recorded with different budgets, Unity versions, or frame counts are still compared; the mismatch is reported in `notes`.",
                    "`deltaPercent` is only meaningful when the matching `deltaPercentAvailable` is true; a zero base leaves the percentage undefined.",
                }),
            new CliCommandDescriptor(
                "profile memory",
                "profile memory [--frames <n> (default: 30)]",
                "Samples memory profiler counters (total/GC/graphics, per-asset-type count and memory, plus App Resident/Committed, AudioClip, Physics, Profiler, and System Total Used when the Unity version exposes them) over N frames and writes the report to a local sidecar for later comparison. Works in Edit Mode and Play Mode.",
                CliCommandGroup.Diagnostics,
                ProtocolConstants.CommandProfileMemory,
                canUseLocal: false,
                canUseLive: true,
                isAllowedWhileBusy: true,
                notes: new[]
                {
                    "Returns a reportId and persists the report to Library/com.yhc509.unity-cli-bridge/memory/<reportId>.json.",
                    "Counters not available on the current Unity version/platform are listed in `unavailable` instead of failing.",
                    "Memory and GC values are bytes; count counters are object counts.",
                }),
            new CliCommandDescriptor(
                "profile memory top",
                "profile memory top [--type <Texture|Texture2D|RenderTexture|Cubemap|Mesh|AudioClip|Material|AnimationClip|Shader|Sprite>] [--limit <n> (default: 20)] [--scope <loaded|scene|assets> (default: loaded)]",
                "Lists the largest loaded objects by Profiler.GetRuntimeMemorySizeLong, with per-type totals and the top N objects. Editor-measured sizes include editor-only copies and differ from player builds.",
                CliCommandGroup.Diagnostics,
                ProtocolConstants.CommandProfileMemoryTop,
                canUseLocal: false,
                canUseLive: true,
                isAllowedWhileBusy: true,
                notes: new[]
                {
                    "Synchronous. Uses Resources.FindObjectsOfTypeAll and Profiler.GetRuntimeMemorySizeLong. A size of 0 means the profiler could not measure that object.",
                    "Editor-measured sizes include editor-only copies and differ from player builds. Compare results taken in the same mode.",
                    "HideFlags.HideAndDontSave objects and Unity built-in resources (unity editor resources, unity default resources, unity_builtin_extra) are skipped.",
                    "Omit --type to scan Texture2D, RenderTexture, Cubemap, Mesh, AudioClip, Material, AnimationClip, and Shader. Pass Texture to include every Texture subclass in one pass.",
                    "--scope loaded (default) keeps every remaining object. scene keeps non-persistent objects. assets keeps persistent objects under Assets/ or Packages/.",
                    "--limit defaults to 20 and cannot exceed 100. The scan measures at most 2000 objects of each type and stops after visiting 20000; truncated is true when either cap or the limit cuts the result.",
                }),
            new CliCommandDescriptor(
                "profile memory compare",
                "profile memory compare <baseReportId> <headReportId> [--threshold <percent> (default: 5.0)] [--limit <n> (default: 10)]",
                "Diffs two memory reports by reading both sidecar JSON files locally — no Editor round-trip. Returns a regression/improvement/unchanged verdict plus the per-counter byte/count deltas that explain it.",
                CliCommandGroup.Diagnostics,
                null,
                canUseLocal: true,
                canUseLive: false,
                isAllowedWhileBusy: true,
                notes: new[]
                {
                    "Local-only. Both reports must have a sidecar under the same project root.",
                    "--threshold is the Total Used Memory median change (in percent) below which the verdict stays `unchanged`.",
                    "Reports taken in different modes (editmode vs playmode), Unity versions, or frame counts are still compared; the mismatch is reported in `notes`.",
                    "`deltaPercent` is only meaningful when the matching `deltaPercentAvailable` is true; a zero base leaves the percentage undefined.",
                }),
            new CliCommandDescriptor(
                "profile memory snapshot",
                "profile memory snapshot",
                "Takes a full memory snapshot via MemoryProfiler.TakeSnapshot and saves it under Library/com.yhc509.unity-cli-bridge/snapshots/<id>.snap, returning only the path and metadata. Analyze the .snap in the Memory Profiler package GUI.",
                CliCommandGroup.Diagnostics,
                ProtocolConstants.CommandProfileMemorySnapshot,
                canUseLocal: false,
                canUseLive: true,
                isAllowedWhileBusy: true,
                notes: new[]
                {
                    "Requires the com.unity.memoryprofiler package; without it the command fails with install guidance instead of taking a snapshot.",
                    "Snapshots can be hundreds of MB; the file is never transferred or parsed — only its path is returned. Old snapshots are not garbage-collected.",
                    "Rejected with PROFILE_IN_PROGRESS while a profile capture or another snapshot is running (and capture start is rejected while a snapshot runs).",
                    "The editor main thread blocks while the snapshot is being captured; expect the command to take seconds on large projects.",
                }),
            new CliCommandDescriptor(
                "execute",
                "execute (--code <csharp> | --file <path>) [--args <json>] [--timeout <seconds>] --force",
                "Executes arbitrary C# code in the running editor context with optional JSON arguments, structured __pucResult return values, and an optional cooperative timeout; always requires --force.",
                CliCommandGroup.EditorControl,
                ProtocolConstants.CommandExecuteCode,
                canUseLocal: false,
                canUseLive: true,
                isAllowedWhileBusy: false,
                notes: new[]
                {
                    "Live-only.",
                    "Always requires --force as a safety gate.",
                    "--args JSON is exposed to user code as the __pucArgsJson string variable.",
                    "Assigning __pucResult returns type-preserving JSON in result. float uses G9 and double uses G17 round-trip format.",
                    "--timeout (default 30 seconds, maximum 600) is a cooperative cancel. User code must check __pucToken (System.Threading.CancellationToken) to stop — otherwise it keeps the main thread, and that is the --force user's responsibility.",
                    "Do not declare the reserved wrapper prefix `__puc_internal_*`, or the `__pucToken` and `__pucResult` variables, in user code.",
                    "Do not put secrets or credentials in --args. CodeDOM compilation can briefly write a .cs file under the OS temp directory.",
                    "Snippets compile with Unity's CodeDOM compiler (mcs). It accepts /langversion from 1 through 7.2, plus ISO-1, ISO-2, Default, and latest. The C# 9.0 project default, and any higher -langversion in Assets/csc.rsp, Assets/mcs.rsp, or Player Settings, is passed as /langversion:latest so default literals compile. A pin of 7.2 or below is passed through. Compile failures include the compiler error text.",
                    "Top-of-file using directives in --code or --file are hoisted above the wrapper. The compile cache is still keyed by the wrapped source, so a repeat reports cacheHit with compileMs 0.",
                },
                forceRule: ForceRule.Always),
            new CliCommandDescriptor(
                "custom",
                "custom <command-name> [--json <args>]",
                "Invokes a project-defined custom command registered via [PucCommand] attribute.",
                CliCommandGroup.EditorControl,
                ProtocolConstants.CommandCustom,
                canUseLocal: false,
                canUseLive: true,
                isAllowedWhileBusy: false,
                notes: new[]
                {
                    "Live-only.",
                    "Custom commands are registered via [PucCommand(\"name\")] attribute on static methods.",
                    "Editor-assembly custom commands can return ExecuteValueSerializer.Serialize(obj); runtime-assembly commands must serialize precise JSON themselves.",
                }),
            new CliCommandDescriptor(
                "asset find",
                "asset find [--name <term>] [--type <type>] [--folder <Assets/...>] [--limit N]",
                "Finds assets by name and/or type, with an optional folder filter. Requires at least one of --name or --type.",
                CliCommandGroup.AssetWorkflows,
                ProtocolConstants.CommandAssetFind,
                canUseLocal: false,
                canUseLive: true,
                isAllowedWhileBusy: true),
            new CliCommandDescriptor(
                "asset types",
                "asset types",
                "Lists built-in and project extension asset-create type descriptors available to the target project.",
                CliCommandGroup.AssetWorkflows,
                ProtocolConstants.CommandAssetTypes,
                canUseLocal: false,
                canUseLive: true,
                isAllowedWhileBusy: true),
            new CliCommandDescriptor(
                "asset info",
                "asset info (--path <Assets/...|Packages/...> | --guid <guid>)",
                "Reads asset metadata by path or GUID. Query paths may point to package assets.",
                CliCommandGroup.AssetWorkflows,
                ProtocolConstants.CommandAssetInfo,
                canUseLocal: false,
                canUseLive: true,
                isAllowedWhileBusy: true),
            new CliCommandDescriptor(
                "asset reimport",
                "asset reimport --path <Assets/...>",
                "Reimports an existing asset.",
                CliCommandGroup.AssetWorkflows,
                ProtocolConstants.CommandAssetReimport,
                canUseLocal: false,
                canUseLive: true,
                isAllowedWhileBusy: false),
            new CliCommandDescriptor(
                "asset mkdir",
                "asset mkdir --path <Assets/...>",
                "Creates missing folders under `Assets/...`.",
                CliCommandGroup.AssetWorkflows,
                ProtocolConstants.CommandAssetMkdir,
                canUseLocal: false,
                canUseLive: true,
                isAllowedWhileBusy: false),
            new CliCommandDescriptor(
                "asset move",
                "asset move --from <Assets/...> --to <Assets/...> [--force]",
                "Moves an asset to a new path; overwriting the destination requires --force.",
                CliCommandGroup.AssetWorkflows,
                ProtocolConstants.CommandAssetMove,
                canUseLocal: false,
                canUseLive: true,
                isAllowedWhileBusy: false,
                notes: new[] { "Overwriting an existing target requires --force." },
                forceRule: ForceRule.OnOverwrite),
            new CliCommandDescriptor(
                "asset rename",
                "asset rename --path <Assets/...> --name <newName> [--force]",
                "Renames an asset in place; overwriting the destination requires --force.",
                CliCommandGroup.AssetWorkflows,
                ProtocolConstants.CommandAssetRename,
                canUseLocal: false,
                canUseLive: true,
                isAllowedWhileBusy: false,
                notes: new[] { "Overwriting an existing target requires --force." },
                forceRule: ForceRule.OnOverwrite),
            new CliCommandDescriptor(
                "asset delete",
                "asset delete --path <Assets/...> --force",
                "Deletes an asset and always requires --force.",
                CliCommandGroup.AssetWorkflows,
                ProtocolConstants.CommandAssetDelete,
                canUseLocal: false,
                canUseLive: true,
                isAllowedWhileBusy: false,
                notes: new[] { "Deletion is always gated by --force." },
                forceRule: ForceRule.Always),
            new CliCommandDescriptor(
                "asset create",
                "asset create --type <kind> --path <Assets/...> [--data-json <json>] [options]",
                "Creates a built-in or extension asset type; overwriting an existing asset requires --force.",
                CliCommandGroup.AssetWorkflows,
                ProtocolConstants.CommandAssetCreate,
                canUseLocal: false,
                canUseLive: true,
                isAllowedWhileBusy: false,
                notes: new[] { "This repo ships the built-in asset types documented below.", "Runtime extension providers can add more types." },
                forceRule: ForceRule.OnOverwrite),
            new CliCommandDescriptor(
                "scene open",
                "scene open --path <Assets/...> [--force]",
                "Opens a saved scene asset; use --force to discard dirty loaded scenes.",
                CliCommandGroup.SceneWorkflows,
                ProtocolConstants.CommandSceneOpen,
                canUseLocal: false,
                canUseLive: true,
                isAllowedWhileBusy: false),
            new CliCommandDescriptor(
                "scene inspect",
                "scene inspect --path <Assets/...> [--node <scenePath>] [--with-values] [--max-depth <N>] [--omit-defaults]",
                "Inspects a saved scene hierarchy; use --with-values when authoring scene patch specs and the other options to reduce payload size.",
                CliCommandGroup.SceneWorkflows,
                ProtocolConstants.CommandSceneInspect,
                canUseLocal: false,
                canUseLive: true,
                isAllowedWhileBusy: false,
                notes: new[] { "Use --node to inspect one node and its subtree while preserving the scene.roots[] response shape.", "Use --with-values before authoring a patch spec.", "Use --max-depth and --omit-defaults to reduce inspect payload size.", "Output with --omit-defaults is read-only; omitted fields are not restored when used as patch input.", "Components and renderers include `enabled`. It defaults to true, so `--omit-defaults` drops true and keeps false, including inside nested prefab instances.", "A node that belongs to a prefab instance includes `prefab.assetPath`. Nested instances also set `prefab.nested`, and the instance root sets `prefab.isRoot`.", "Detailed scene patch rules live in references/scene-spec.md relative to the installed AI-agent skill folder." }),
            new CliCommandDescriptor(
                "scene patch",
                "scene patch --path <Assets/...> (--spec-file <file.json> | --spec-json <json>) [--force]",
                "Applies a deterministic scene patch spec; destructive operations require --force.",
                CliCommandGroup.SceneWorkflows,
                ProtocolConstants.CommandScenePatch,
                canUseLocal: false,
                canUseLive: true,
                isAllowedWhileBusy: false,
                notes: new[]
                {
                    "Supported operations: add-gameobject, modify-gameobject, delete-gameobject, add-component, modify-component, remove-component.",
                    "An unknown operation fails with SCENE_SPEC_INVALID, lists those operations, and suggests the closest name (`set-component-values` means `modify-component`).",
                    "Detailed scene patch rules live in references/scene-spec.md relative to the installed AI-agent skill folder.",
                },
                forceRule: ForceRule.OnDestructiveOp),
            new CliCommandDescriptor(
                "scene add-object",
                "scene add-object --path <Assets/...> [--parent <scenePath>] --name <name> [--primitive <Cube|Sphere|Capsule|Cylinder|Plane|Quad>] [--position x,y,z] [--components \"Type1,Type2\"]",
                "Adds a new GameObject or built-in primitive to a scene; shortcut for a single add-gameobject scene patch operation.",
                CliCommandGroup.SceneWorkflows,
                ProtocolConstants.CommandScenePatch,
                canUseLocal: false,
                canUseLive: true,
                isAllowedWhileBusy: false,
                notes: new[] { "Internally delegates to scene patch." }),
            new CliCommandDescriptor(
                "scene set-transform",
                "scene set-transform --node <scenePath> [--position x,y,z] [--rotation x,y,z] [--scale x,y,z]",
                "Sets local transform values on a node in the active loaded scene and saves the scene immediately.",
                CliCommandGroup.SceneWorkflows,
                ProtocolConstants.CommandSceneSetTransform,
                canUseLocal: false,
                canUseLive: true,
                isAllowedWhileBusy: false,
                notes: new[] { "Requires at least one of --position, --rotation, or --scale.", "Uses the active scene instead of --path.", "Saves the active scene immediately after mutation." }),
            new CliCommandDescriptor(
                "scene add-component",
                "scene add-component --path <Assets/...> --node <scenePath> --type <ComponentType> [--values <json>]",
                "Adds a component to a GameObject; shortcut for a single add-component scene patch operation.",
                CliCommandGroup.SceneWorkflows,
                ProtocolConstants.CommandScenePatch,
                canUseLocal: false,
                canUseLive: true,
                isAllowedWhileBusy: false,
                notes: new[] { "Internally delegates to scene patch." }),
            new CliCommandDescriptor(
                "scene remove-component",
                "scene remove-component --path <Assets/...> --node <scenePath> --type <ComponentType> [--index N] --force",
                "Removes a component from a GameObject; shortcut for a single remove-component scene patch operation.",
                CliCommandGroup.SceneWorkflows,
                ProtocolConstants.CommandScenePatch,
                canUseLocal: false,
                canUseLive: true,
                isAllowedWhileBusy: false,
                notes: new[] { "Always requires --force.", "Internally delegates to scene patch." },
                forceRule: ForceRule.Always),
            new CliCommandDescriptor(
                "scene assign-material",
                "scene assign-material --node <scenePath> --material <Assets/...>",
                "Assigns a material asset to MeshRenderer.sharedMaterials[0] on a node in the active loaded scene.",
                CliCommandGroup.SceneWorkflows,
                ProtocolConstants.CommandSceneAssignMaterial,
                canUseLocal: false,
                canUseLive: true,
                isAllowedWhileBusy: false,
                notes: new[] { "Uses the active scene instead of --path.", "Saves the active scene immediately after assignment." }),
            new CliCommandDescriptor(
                "scene list-components",
                "scene list-components --node <scenePath>",
                "Lists all components on a GameObject in the active loaded scene, returning type names and indices.",
                CliCommandGroup.SceneWorkflows,
                ProtocolConstants.CommandSceneListComponents,
                canUseLocal: false,
                canUseLive: true,
                isAllowedWhileBusy: true,
                notes: new[] { "Uses the active scene.", "Returns type + index pairs for use with add-component, remove-component, and modify-component." }),
            new CliCommandDescriptor(
                "prefab inspect",
                "prefab inspect --path <Assets/...> [--node <nodePath>] [--with-values] [--max-depth <N>] [--omit-defaults]",
                "Inspects prefab hierarchy and serialized property paths, including read-only model assets such as .fbx; use --with-values when authoring patch specs and the other options to reduce payload size.",
                CliCommandGroup.PrefabWorkflows,
                ProtocolConstants.CommandPrefabInspect,
                canUseLocal: false,
                canUseLive: true,
                isAllowedWhileBusy: true,
                notes: new[] { "Use --node to inspect one node and its subtree while preserving the root response shape.", "Use --with-values before authoring a patch spec.", "Use --max-depth and --omit-defaults to reduce inspect payload size.", "Output with --omit-defaults is read-only; omitted fields are not restored when used as patch input.", "Model assets (.fbx, .obj, .dae, .dxf, .3ds, .blend, .skp) can be inspected read-only. prefab patch still requires a .prefab.", "Detailed prefab patch rules live in docs/prefab-spec.md." }),
            new CliCommandDescriptor(
                "prefab create",
                "prefab create --path <Assets/...> (--spec-file <file.json> | --spec-json <json>) [--force]",
                "Creates a prefab from a JSON structure spec; use --force to overwrite an existing asset.",
                CliCommandGroup.PrefabWorkflows,
                ProtocolConstants.CommandPrefabCreate,
                canUseLocal: false,
                canUseLive: true,
                isAllowedWhileBusy: false,
                notes: new[] { "Use this instead of asset create --type prefab for structured prefab authoring.", "Detailed prefab patch rules live in docs/prefab-spec.md." },
                forceRule: ForceRule.OnOverwrite),
            new CliCommandDescriptor(
                "prefab patch",
                "prefab patch --path <Assets/...> (--spec-file <file.json> | --spec-json <json>) [--force]",
                "Applies a deterministic patch spec to an existing prefab; destructive operations require --force.",
                CliCommandGroup.PrefabWorkflows,
                ProtocolConstants.CommandPrefabPatch,
                canUseLocal: false,
                canUseLive: true,
                isAllowedWhileBusy: false,
                notes: new[] { "Detailed prefab patch rules live in docs/prefab-spec.md." },
                forceRule: ForceRule.OnDestructiveOp),
            new CliCommandDescriptor(
                "prefab add-component",
                "prefab add-component --path <Assets/...> --node <nodePath> --type <ComponentType> [--values <json>]",
                "Adds a component to a prefab node; shortcut for a single add-component prefab patch operation.",
                CliCommandGroup.PrefabWorkflows,
                ProtocolConstants.CommandPrefabPatch,
                canUseLocal: false,
                canUseLive: true,
                isAllowedWhileBusy: false,
                notes: new[] { "Internally delegates to prefab patch." }),
            new CliCommandDescriptor(
                "prefab remove-component",
                "prefab remove-component --path <Assets/...> --node <nodePath> --type <ComponentType> [--index N] --force",
                "Removes a component from a prefab node; shortcut for a single remove-component prefab patch operation.",
                CliCommandGroup.PrefabWorkflows,
                ProtocolConstants.CommandPrefabPatch,
                canUseLocal: false,
                canUseLive: true,
                isAllowedWhileBusy: false,
                notes: new[] { "Always requires --force.", "Internally delegates to prefab patch." },
                forceRule: ForceRule.Always),
            new CliCommandDescriptor(
                "prefab list-components",
                "prefab list-components --path <Assets/...> --node <nodePath>",
                "Lists all components on a node in a prefab asset, returning type names and indices.",
                CliCommandGroup.PrefabWorkflows,
                ProtocolConstants.CommandPrefabListComponents,
                canUseLocal: false,
                canUseLive: true,
                isAllowedWhileBusy: true,
                notes: new[] { "Returns type + index pairs for use with prefab patch add-component/remove-component." }),
            new CliCommandDescriptor(
                "test list",
                "test list [--mode <edit|play|all>] [--no-detail]",
                "Lists EditMode and/or PlayMode test cases discovered in the running editor; add --no-detail to return only fullName and mode.",
                CliCommandGroup.Diagnostics,
                ProtocolConstants.CommandTestList,
                canUseLocal: false,
                canUseLive: true,
                isAllowedWhileBusy: false,
                notes: new[] { "Returns full test names, assemblies, modes, and categories; with --no-detail, returns only fullName and mode." }),
            new CliCommandDescriptor(
                "test run",
                "test run --mode <edit|play> [--filter <substring>] [--category <name>] [--assembly <name>] [--no-domain-reload] [--failures-only] [--timeout <seconds>] [--wait]",
                "Executes EditMode or PlayMode tests. EditMode returns synchronously; PlayMode returns runId immediately and persists results to Library/com.yhc509.unity-cli-bridge/test-runs/<runId>.json. Add --failures-only to trim tests[] to non-passed entries while preserving summary counts.",
                CliCommandGroup.Diagnostics,
                ProtocolConstants.CommandTestRun,
                canUseLocal: false,
                canUseLive: true,
                isAllowedWhileBusy: false,
                notes: new[]
                {
                    "EditMode: synchronous response with full TestRunResult JSON.",
                    "PlayMode: asynchronous. STARTED+runId response; poll with `test results` or use --wait.",
                    "--no-domain-reload speeds up PlayMode runs but risks static state leakage (warning emitted in response).",
                    "Default timeout 300s, max 1800s. Timeout triggers TestRunnerApi.CancelTestRun(runGuid).",
                },
                forceRule: ForceRule.None,
                defaultLiveTimeoutMs: ProtocolConstants.DefaultTestRunTimeoutSeconds * 1000),
            new CliCommandDescriptor(
                "test results",
                "test results [--run-id <id>] [--failures-only]",
                "Retrieves cached test run result (or in-progress status). Without --run-id, returns the last run. Add --failures-only to trim tests[] to non-passed entries while preserving summary counts.",
                CliCommandGroup.Diagnostics,
                ProtocolConstants.CommandTestResults,
                canUseLocal: false,
                canUseLive: true,
                isAllowedWhileBusy: true,
                notes: new[] { "Reads from Library/com.yhc509.unity-cli-bridge/test-runs/<runId>.json or in-memory SessionState." }),
            new CliCommandDescriptor(
                "test cancel",
                "test cancel",
                "Cancels the in-progress test run and releases the run lock; a no-op success when no run is active.",
                CliCommandGroup.Diagnostics,
                ProtocolConstants.CommandTestCancel,
                canUseLocal: false,
                canUseLive: true,
                isAllowedWhileBusy: true,
                notes: new[]
                {
                    "Attempts a graceful TestRunnerApi cancel first, then always releases the run lock even if the graceful cancel fails.",
                    "Manual escape hatch for a stuck test-run lock; prefer this over reflecting into internal APIs.",
                }),
            new CliCommandDescriptor(
                "package list",
                "package list [--filter <substring>] [--limit N]",
                "Lists installed packages in the project, optionally filtered by name or display name.",
                CliCommandGroup.PackageManagement,
                ProtocolConstants.CommandPackageList,
                canUseLocal: false,
                canUseLive: true,
                isAllowedWhileBusy: true,
                defaultLiveTimeoutMs: ProtocolConstants.DefaultPackageLiveTimeoutMs),
            new CliCommandDescriptor(
                "package add",
                "package add --name <package> [--version <version>]",
                "Adds a package to the project; supports registry, git URL, and local paths.",
                CliCommandGroup.PackageManagement,
                ProtocolConstants.CommandPackageAdd,
                canUseLocal: false,
                canUseLive: true,
                isAllowedWhileBusy: false,
                notes: new[] { "The Editor may pause while a package operation runs." },
                defaultLiveTimeoutMs: ProtocolConstants.DefaultPackageLiveTimeoutMs),
            new CliCommandDescriptor(
                "package remove",
                "package remove --name <package> --force",
                "Removes a package from the project; always requires --force.",
                CliCommandGroup.PackageManagement,
                ProtocolConstants.CommandPackageRemove,
                canUseLocal: false,
                canUseLive: true,
                isAllowedWhileBusy: false,
                notes: new[] { "Removal is always gated by --force.", "The Editor may pause while a package operation runs." },
                forceRule: ForceRule.Always,
                defaultLiveTimeoutMs: ProtocolConstants.DefaultPackageLiveTimeoutMs),
            new CliCommandDescriptor(
                "package search",
                "package search --query <text>",
                "Searches the Unity registry for packages matching the query.",
                CliCommandGroup.PackageManagement,
                ProtocolConstants.CommandPackageSearch,
                canUseLocal: false,
                canUseLive: true,
                isAllowedWhileBusy: true,
                defaultLiveTimeoutMs: ProtocolConstants.DefaultPackageLiveTimeoutMs),
            new CliCommandDescriptor(
                "material info",
                "material info --path <Assets/...mat> [--omit-defaults]",
                "Inspects a material's shader and property values, with an option to omit properties still at the shader defaults.",
                CliCommandGroup.MaterialWorkflows,
                ProtocolConstants.CommandMaterialInfo,
                canUseLocal: false,
                canUseLive: true,
                isAllowedWhileBusy: true,
                notes: new[] { "Use --omit-defaults to reduce payload size by omitting properties equal to the shader default." }),
            new CliCommandDescriptor(
                "material set",
                "material set --path <Assets/...mat> (--property <name> --value <val> | --texture <name> --asset <Assets/...>)",
                "Sets a material property value or texture.",
                CliCommandGroup.MaterialWorkflows,
                ProtocolConstants.CommandMaterialSet,
                canUseLocal: false,
                canUseLive: true,
                isAllowedWhileBusy: false),
            new CliCommandDescriptor(
                "qa click",
                "qa click (--qa-id <id> | --target <path>) [--button left|right]",
                "Clicks a UI element identified by QA ID or GameObject path; defaults to left click and supports right click with --button right; requires Play Mode.",
                CliCommandGroup.QaWorkflows,
                ProtocolConstants.CommandQaClick,
                canUseLocal: false,
                canUseLive: true,
                isAllowedWhileBusy: false,
                requiresGraphics: true),
            new CliCommandDescriptor(
                "qa tap",
                "qa tap (--x <int> --y <int> | --target <path>) [--button left|right] [--screenshot-width <int> --screenshot-height <int>]",
                "Taps at a screenshot-derived coordinate, or at a world object resolved by --target. Defaults to left click and supports right click with --button right. With --x/--y, pass screenshot image coordinates directly with a top-left origin; the bridge auto-uses the last captured screenshot size when available and handles Y-flip plus scaling internally (--screenshot-width/--screenshot-height override the source size). With --target, left click invokes the object's IQaTappable action when present, otherwise simulates an Input System tap at the object's anchor (on the legacy Input Manager: a simulated touch, which Unity also reports as left mouse button 0 and anyKeyDown; coordinate taps that hit no UI use the same path); right click uses pointer handlers when available and otherwise simulates right-button Input System input; requires Play Mode.",
                CliCommandGroup.QaWorkflows,
                ProtocolConstants.CommandQaTap,
                canUseLocal: false,
                canUseLive: true,
                isAllowedWhileBusy: false,
                requiresGraphics: true),
            new CliCommandDescriptor(
                "qa swipe",
                "qa swipe [--target <path>] --from <x,y> --to <x,y> [--duration <ms>] [--button left|right] [--screenshot-width <int> --screenshot-height <int>]",
                "Swipes over multiple frames; defaults to left button drag and supports right button drag with --button right. Without --target, --from/--to use screenshot-style top-origin coordinates and auto-scale from the last captured screenshot when available, while --target keeps them as pixel offsets from the target RectTransform center; pass --screenshot-width/--screenshot-height to override the source size; requires Play Mode.",
                CliCommandGroup.QaWorkflows,
                ProtocolConstants.CommandQaSwipe,
                canUseLocal: false,
                canUseLive: true,
                isAllowedWhileBusy: false,
                requiresGraphics: true),
            new CliCommandDescriptor(
                "qa key",
                "qa key --key <keyName>",
                "Simulates a key press via Input System. On the legacy Input Manager (no Input System active), delivers the key to IQaKeyHandler components and fails with QA_LEGACY_KEY_UNSUPPORTED when none handles it; requires Play Mode.",
                CliCommandGroup.QaWorkflows,
                ProtocolConstants.CommandQaKey,
                canUseLocal: false,
                canUseLive: true,
                isAllowedWhileBusy: false),
            new CliCommandDescriptor(
                "qa ui-dump",
                "qa ui-dump [--limit N] [--interactable-only] [--text <substring>] [--omit-rect] [--screenshot-width <int> --screenshot-height <int>]",
                "Dumps currently clickable UI elements (path, type, text, interactable, image-space rect/center) as JSON; requires Play Mode. Use --limit, --interactable-only, --text, and --omit-rect to reduce payload size. Feed a returned path to `qa click --target`, or centerX/centerY to `qa tap`. The returned path is reliable when unique; if same-named siblings share a path, tap by centerX/centerY instead. Coordinates use the last captured screenshot size unless overridden.",
                CliCommandGroup.QaWorkflows,
                ProtocolConstants.CommandQaUiDump,
                canUseLocal: false,
                canUseLive: true,
                isAllowedWhileBusy: false,
                requiresGraphics: true),
            new CliCommandDescriptor(
                "qa world-dump",
                "qa world-dump [--include-offscreen] [--limit N] [--text <substring>] [--screenshot-width <int> --screenshot-height <int>]",
                "Dumps non-UI world objects that opt in via IQaTappable/QaTappable (label, hierarchy path, image-space center, onScreen, hasAction) as JSON. In Play Mode, coordinates use the last captured screenshot size unless overridden. Outside Play Mode (Edit Mode), returns a hint pointing at scene inspect instead of QA_NOT_PLAYING, plus opted-in objects without screenshot coordinates. Use --limit and --text to reduce payload size; filtered Play Mode responses keep onScreen when --include-offscreen is set, otherwise omit it, and omit constant hasAction when it carries no per-element signal. Feed a returned path to `qa tap --target`. Off-screen objects are excluded unless --include-offscreen is set.",
                CliCommandGroup.QaWorkflows,
                ProtocolConstants.CommandQaWorldDump,
                canUseLocal: false,
                canUseLive: true,
                isAllowedWhileBusy: false,
                requiresGraphics: true),
            new CliCommandDescriptor(
                "qa run-sequence",
                "qa run-sequence --spec-json <json|@file> [--timeout <ms>] [--record] [--record-path <out.mp4>]",
                "Runs a linear sequence of condition-gated action steps in the bridge: each step waits (ANDed conditions) then executes its actions, with no per-step screenshot round-trip. Conditions read built-in state (active/gone/transform/scene/log/interactable) or game-exposed IQaQueryable values, compared with ==/!=/>=/<=/near/changed. Actions reuse qa key/tap/swipe/wait. Returns the completed step count, or the stopped step with its unmet conditions and a state snapshot on timeout. Requires Play Mode; no force-rule.",
                CliCommandGroup.QaWorkflows,
                ProtocolConstants.CommandQaRunSequence,
                canUseLocal: false,
                canUseLive: true,
                isAllowedWhileBusy: false,
                notes: new[]
                {
                    "--record captures the sequence interval as an mp4 via Unity Recorder and returns recordingPath when finalized.",
                    "--record-path moves the finalized mp4 to the requested path.",
                },
                defaultLiveTimeoutMs: ProtocolConstants.MaxQaRunSequenceTimeoutMs + 5_000),
            new CliCommandDescriptor(
                "qa wait",
                "qa wait --ms <int>",
                "Waits for the specified number of milliseconds (local only, does not contact the editor).",
                CliCommandGroup.QaWorkflows,
                protocolCommand: null,
                canUseLocal: true,
                canUseLive: false,
                isAllowedWhileBusy: false),
            new CliCommandDescriptor(
                "qa wait-until",
                "qa wait-until (--scene <name> | --log-contains <text> | --object-exists <qa-id|path> | --object-interactable <qa-id|path> | --object-gone <qa-id|path>) [--timeout <ms>]",
                "Polls the editor until all supplied conditions are met (AND) or timeout expires; supports waiting for clickable/interactable UI or inactive/destroyed objects; requires Play Mode.",
                CliCommandGroup.QaWorkflows,
                ProtocolConstants.CommandQaWaitUntil,
                canUseLocal: false,
                canUseLive: true,
                isAllowedWhileBusy: false),
            new CliCommandDescriptor(
                "instances list",
                "instances list [--brief]",
                "Lists known Unity project instances and the active registry selection. Add --brief to return only projectName, projectRoot, projectHash, and state per instance.",
                CliCommandGroup.InstanceManagement,
                protocolCommand: null,
                canUseLocal: true,
                canUseLive: false,
                isAllowedWhileBusy: false),
            new CliCommandDescriptor(
                "instances use",
                "instances use <projectHash|projectPath|projectName>",
                "Pins the active target project by hash, project path, or registered project name. Existing directory paths win over name matches.",
                CliCommandGroup.InstanceManagement,
                protocolCommand: null,
                canUseLocal: true,
                canUseLive: false,
                isAllowedWhileBusy: false),
            new CliCommandDescriptor(
                "doctor",
                "doctor",
                "Shows registry, project detection, Unity path, and live reachability diagnostics.",
                CliCommandGroup.Diagnostics,
                protocolCommand: null,
                canUseLocal: true,
                canUseLive: false,
                isAllowedWhileBusy: false),
            new CliCommandDescriptor(
                "raw",
                "raw [--force] --json '{\"command\":\"status\",\"arguments\":{}}'",
                "Sends a raw live protocol envelope for low-level debugging.",
                CliCommandGroup.Diagnostics,
                protocolCommand: null,
                canUseLocal: false,
                canUseLive: true,
                isAllowedWhileBusy: false,
                notes: new[] { "This bypasses typed CLI validation.", "Use --force to inject `force: true` into raw arguments for destructive commands." }),
        };

        private static readonly string[] _supportedProtocolCommands = BuildSupportedProtocolCommands();

        public static CliCommandDescriptor[] GetCommands()
        {
            return (CliCommandDescriptor[])_commands.Clone();
        }

        public static string BuildHelpText()
        {
            var builder = new StringBuilder();
            builder.AppendLine("usage: unity-cli [--json] [--output <default|json|compact>] [--project <path|name>] <command> [options]");
            builder.AppendLine();
            builder.AppendLine("options:");
            builder.AppendLine("  --json                Equivalent to --output json. If both --json and --output are specified, the last one wins.");
            builder.AppendLine("  --output <mode>       Response format: default, json (full envelope), or compact (data payload / compact error JSON).");
            builder.AppendLine("  --project <path|name>  Existing directory paths take precedence over registered project names. Project-name matches are case-insensitive.");
            builder.AppendLine();
            builder.AppendLine("commands:");
            foreach (CliCommandDescriptor command in _commands)
            {
                builder.Append("  ");
                builder.AppendLine(command.Synopsis);
            }

            return builder.ToString();
        }

        public static string[] GetSupportedProtocolCommands()
        {
            return (string[])_supportedProtocolCommands.Clone();
        }

        private static string[] BuildSupportedProtocolCommands()
        {
            var commands = new List<string> { ProtocolConstants.CommandPing };
            foreach (CliCommandDescriptor descriptor in _commands)
            {
                if (descriptor.CanUseLive && !string.IsNullOrWhiteSpace(descriptor.ProtocolCommand))
                {
                    commands.Add(descriptor.ProtocolCommand!);
                }
            }

            return commands.ToArray();
        }

        public static bool IsCommandAllowedWhileBusy(string command)
        {
            if (string.Equals(command, ProtocolConstants.CommandPing, StringComparison.Ordinal))
            {
                return true;
            }

            CliCommandDescriptor? descriptor = FindByProtocolCommand(command);
            return descriptor is not null && descriptor.IsAllowedWhileBusy;
        }

        public static bool IsProtocolCommandInGroup(string command, CliCommandGroup group)
        {
            CliCommandDescriptor? descriptor = FindByProtocolCommand(command);
            return descriptor is not null && descriptor.Group == group;
        }

        public static CliCommandDescriptor? FindByCommand(string command)
        {
            foreach (CliCommandDescriptor descriptor in _commands)
            {
                if (string.Equals(descriptor.Command, command, StringComparison.Ordinal))
                {
                    return descriptor;
                }
            }

            return null;
        }

        public static CliCommandDescriptor? FindByProtocolCommand(string command)
        {
            foreach (CliCommandDescriptor descriptor in _commands)
            {
                if (!string.IsNullOrWhiteSpace(descriptor.ProtocolCommand)
                    && string.Equals(descriptor.ProtocolCommand, command, StringComparison.Ordinal))
                {
                    return descriptor;
                }
            }

            return null;
        }

        public static bool RequiresGraphics(string protocolCommand)
        {
            CliCommandDescriptor? descriptor = FindByProtocolCommand(protocolCommand);
            return descriptor != null && descriptor.RequiresGraphics;
        }
    }
}
