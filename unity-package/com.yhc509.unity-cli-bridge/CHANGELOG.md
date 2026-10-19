# Changelog

All notable changes to this project will be documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/).

## [Unreleased]

## [0.5.5] - 2026-10-19

### Changed
- User-facing text is now English: CLI messages, Editor bridge messages, and the installed AI-agent skill. Error codes, JSON field names, placeholders, and exit codes are unchanged. Reinstall the skill from **Window > Unity CLI Manager** to pick up the English copy.
- The package now supports Unity 2021.3 LTS through Unity 6. It was checked on 2021.3.45f2, the latest public 2021.3 patch; later 2021.3 updates are paid extended-LTS releases. Gradient property edits still need Unity 2022.1 or newer. `record` compiles only when Unity Recorder 3.0.0 or newer is installed. On Unity 6000.4 and newer, Recorder 5.1.6 or later is still required or Recorder itself does not compile.

### Fixed
- Scene examples that add an object with `--parent "/Environment[0]"` now follow up with that object's real `createdPath` (`/Environment[0]/Cube[0]` or `/Environment[0]/Floor[0]`). The old `--node "/Cube[0]"` returns `SCENE_NODE_NOT_FOUND`.
- `profile memory snapshot` compiles on Unity 2021.3. That editor exposes the snapshot API as `UnityEngine.Profiling.Memory.Experimental`; Unity 2022.2 and newer use `Unity.Profiling.Memory`.
- Projects without the Input System no longer get an unreachable-code warning from `qa key` and `qa swipe`.

## [0.5.4] - 2026-08-18

### Added
- **Window > Unity CLI Manager** can now install the AI Agent Skill for Grok Build, next to Claude Code and Codex. Project scope writes to `<project-root>/.grok/skills/`; global scope writes to `~/.grok/skills/`.

### Changed
- The installed AI-agent skill now carries the same execute timeout, force-gate, backup, dirty-scene, and `qa --button right` rules as the maintainer copy. Reinstall the skill from **Window > Unity CLI Manager** to pick them up. QA examples in the skill now use `--output compact` and the default JPEG screenshot instead of a full-resolution PNG.

### Fixed
- The README still said this package pins Unity Recorder `5.1.6`. Recorder is optional and is not installed with the package — add it yourself if you record. On Unity `6000.4` and newer, use Recorder `5.1.6` or later.
- The **Overwrite Skill?** confirmation in **Window > Unity CLI Manager** is now in English, matching the rest of the window.

## [0.5.3] - 2026-08-15

### Added
- Long-running commands now finish faster. Test runs, package operations, profiler sampling, recording and `qa run-sequence` all advance one step per Editor update tick, and an idle Editor runs that loop slowly — measured at roughly six ticks per second, whether or not its window is in front. While one of those commands is in flight the bridge now keeps the Editor ticking at full rate and lets it settle back the moment the work finishes, which measured about 1.5× faster end to end (60 ticks: 9.4–10.4 s → 6.0–6.1 s on Unity 6000.3 / macOS). Nothing to turn on, and on an Editor version that does not expose the internal API this relies on, commands simply run at the old speed.
- Component values may now be written as JSON arrays: `"m_Center": [1, 2, 3]` alongside the existing `{"x": 1, "y": 2, "z": 3}`. Vector2/3/4, Vector2Int/Vector3Int, Quaternion, Rect, RectInt and Color all accept the short form, and a Color array may leave off alpha.
- Structured values that arrive quoted — `"[1,2,3]"` or `"{\"x\":1,\"y\":2,\"z\":3}"` instead of the JSON value itself — are now parsed instead of rejected. AI agents produce this shape often, and the previous error read as though the value were wrong rather than the quoting. Only strings that open a JSON object or array are re-read, so asset paths, object-reference handles, enum names and plain text are untouched, and a string that fails to parse still produces the original validation error.
- The bridge can now be kept from starting, for build machines that have no use for it. Set `UNITY_CLI_BRIDGE_DISABLE=1` or pass `-noUnityCliBridge` on the Editor command line and the bridge builds nothing at all — no socket, no instance registration, no token file, no console warnings if any of that would have failed. A CI job that only opens the Editor to produce a player build gets a quiet log and no registry lock to contend over with parallel builds.

### Changed
- **`screenshot` now defaults to a JPEG capped at 1024px wide** (quality 75) instead of a full-resolution PNG. A capture is by far the most expensive thing an AI agent can ask for — a 1920×1080 PNG bills at roughly 2,040 image tokens, against about 576 for the same frame capped at 1024px — and the capped JPEG is still perfectly readable for checking UI state, which is what nearly every capture is for. Three things keep the change from surprising anyone: `--format png` still gives you lossless, a `--path` ending in `.png` selects PNG on its own, and an explicit `--width`/`--height` is left exactly as you asked. `--max-width 0` turns the cap off while keeping the automatic size. Tap and dump coordinates are unaffected — they scale from the last capture's size, so a downscaled screenshot's coordinates still land correctly.
- Unity Recorder is no longer installed alongside the package. Only the `record` commands and `qa run-sequence --record` ever used it, so projects that do not record no longer carry the dependency. If you do record, add `com.unity.recorder` to your project — `record start` now fails with that exact instruction when it is missing, and `record stop` / `record status` keep working on recordings you already have. `com.unity.test-framework`, which the test commands genuinely require, is now declared outright instead of arriving as a side effect of the Recorder dependency.

### Fixed
- The package no longer compiles its editor-side plumbing into your game. Everything the bridge uses to talk to the CLI — the instance registry, file-backup transactions, the command catalog and its help text — was being built into player builds as unreachable code, roughly 150 KB of it, along with the strings it carries. Player builds now receive only the QA marker types (`[QaTarget]`, `IQaTappable`, `IQaQueryable`, `QaTappable`) that a project references on purpose, about 7 KB. Nothing changes for editor use, and no code needs updating.
- An Editor whose IPC listener died is no longer advertised as reachable. The listener could stop accepting connections for good — an unexpected socket failure, or a first bind that never succeeded — while the instance kept publishing itself, so the CLI would route to it and fail to connect over and over. The bridge now watches its own listener and re-binds it in place within a few seconds; if it cannot be revived, the instance removes itself from the registry with an Editor console message telling you to restart, instead of staying on the list as a target that never answers.
- The IPC auth token is now compared in fixed time, so the check cannot leak the expected token through timing to other processes on the same machine. Authentication behavior is otherwise unchanged.

## [0.5.2] - 2026-08-13

### Added
- `editor launch` and `editor stop`: the CLI can now start and stop the Unity Editor itself, so a workflow no longer needs a human to open the project first. `editor launch` finds the Unity version the project asks for, starts it headless (`-batchmode`) by default, and waits until the bridge is reachable before returning (default 300 s; `--timeout <sec>` for projects with a long first import, `--no-wait` to return immediately). It is idempotent — if the editor is already running, the live instance is reused and the response says `"reused": true` — and it refuses with `EDITOR_ALREADY_RUNNING_CONFLICT` when an editor process already holds the project without a bridge, instead of tripping Unity's own project lock. Pass `--gui` for a visible window. The spawned editor logs to `Library/com.yhc509.unity-cli-bridge/editor-launch.log` and does not hold on to the CLI's output streams, so shell pipelines like `unity-cli editor launch | grep reused` finish normally instead of hanging. `editor stop` asks the editor to quit gracefully: it refuses with `EDITOR_DIRTY` while unsaved scene or prefab changes exist (`--force` discards them), waits for the process to exit (default 30 s), and works whether the editor is headless, focused, or sitting unfocused in the background.
- The bridge now starts in headless (`-batchmode`) editors, so every command — scene edits, tests, QA, profiling — works without an editor window. Unity's secondary processes (asset-import workers, MPE) stay excluded, so a project never registers twice. The default headless mode keeps the GPU initialized, which means `screenshot`, `record`, and coordinate-based `qa` commands keep producing real output with no window on screen. `instances list` now reports each editor's mode (`gui` / `headless` / `headless-nographics`).
- Rendering commands under a `-nographics` editor now fail fast with `HEADLESS_NO_GRAPHICS` instead of silently returning blank images, so an agent immediately knows the capture is impossible rather than reasoning about an all-gray screenshot.
- `profile memory` watches memory the way `profile capture` watches frame time. It samples the memory counters — total and reserved memory, GC, graphics, audio and video, plus per-asset-type object counts and bytes for textures, meshes, materials and animation clips — and saves the result as a report you can come back to. `profile memory compare <baseReportId> <headReportId>` then diffs two reports and answers whether memory grew: a `regression` / `improvement` / `unchanged` verdict based on total used memory, followed by the counters that moved the most, so a leak points at its own cause instead of just a rising total. Comparison runs entirely on the saved reports, so it works with the Editor closed, and reports taken in different modes or Unity versions are still compared with the mismatch noted rather than silently ignored. Counters that the running Unity version does not expose are listed as unavailable instead of failing the command.
- `profile memory snapshot` captures a full memory snapshot for the cases where counters are not enough. It writes a `.snap` file through the Memory Profiler package and returns its path, size, and capture flags — open it in **Window > Analysis > Memory Profiler** for the object-level view. The command requires `com.unity.memoryprofiler` and says so with install instructions when the package is missing, and it will not run at the same time as a profile capture in either direction. Snapshots are as large as the Editor's memory (often over a gigabyte) and are never deleted automatically, so remove old ones yourself.

### Fixed
- A killed or crashed editor session no longer breaks the next one. The editor's IPC auth token file could be left behind when the process died without cleaning up, and the next session then kept the stale file — every CLI command failed with `UNAUTHORIZED` until it was removed by hand. The token file is now replaced on startup.

### Compatibility
- The wire protocol is bumped to `7` (adds the graceful-quit command behind `editor stop`). As usual, installed CLIs dispatch per version automatically, and the CLI matching this package ships in the same release.

## [0.5.1] - 2026-07-29

### Added
- `profile compare <baseCaptureId> <headCaptureId>` answers "did my change make it faster or slower?" in one command. Point it at a capture from before your change and one from after, and it reports a `regression` / `improvement` / `unchanged` verdict together with the numbers behind it: median, 95th-percentile and worst frame time, how many frames went over budget, total GC allocation, and which markers got slower or faster (plus markers that appeared or disappeared between the two runs). A marker whose time stayed flat but whose allocation grew or shrank is listed too, so allocation-only regressions are not missed. `--threshold <percent>` sets how much the median frame time may move before the run still counts as unchanged (default 5), and `--limit <n>` caps each marker list; ties are broken by marker name, so repeated runs give the same list. Like `profile analyze`, it reads the capture files on disk, so it works with the Editor closed. A capture that never finished — for example one cut short by a script recompile — is rejected with a clear error rather than compared, so a dead capture can't be mistaken for a huge speed-up. Percentages come with a `deltaPercentAvailable` flag: when the "before" value was zero there is no percentage to report and only the absolute change is meaningful. If the two captures were recorded with different frame budgets, Unity versions, or very different frame counts, the response says so in `notes` instead of quietly comparing apples to oranges.
- The AI-agent skill now ships a profiling reference so an assistant reading `profile` output draws the right conclusion from it. Captures taken inside the Editor are full of markers that look like the problem and are not — thread waits, the Editor's own loop, and the idle time you spend when you are comfortably inside your frame budget — and an assistant with no guidance will happily point at one of them. The reference lists what to ignore, explains why `gpuMedianMs: -1` means "not measured" rather than "no GPU time", warns that a large percentage change can still be an immeasurably small one, notes that the default 16.67 ms frame budget assumes 60 fps on desktop, and collects performance advice that Unity has since contradicted so the assistant does not recommend it. Existing installs pick it up by reinstalling the skill from **Window > Unity CLI Manager**.

## [0.5.0] - 2026-07-28

### Added
- New `profile` command group turns the Editor into an agent-friendly profiler: `profile stats` samples built-in counters (min/median/p95/max), `profile capture start/stop` records Play Mode frames and produces a compact summary (frame-time percentiles, spike frames with their top marker, hotspots, per-marker GC bytes, and a CPU/GPU-bound verdict), and `profile analyze` drills into the capture sidecar locally without a running Editor. `qa run-sequence --profile` captures a sequence run and merges the summary into the response.

## [0.4.3] - 2026-07-21

### Security
- Installing the CLI from **Window > Unity CLI Manager** now refuses an archive that contains a symbolic link, and only downloads from this project's own release location. Extraction already rejected entries that tried to escape the staging folder, but an archive could still ship a link that the install step followed — copying whatever it pointed at, anywhere the Editor could read, into the install directory. Real releases contain a plain executable, so normal installs are unaffected.

### Added
- `test cancel` releases a stuck test run. A PlayMode run that never finished left the run lock held, and every later `test run` was refused with `TEST_RUN_IN_PROGRESS` until the Editor was restarted — the only way out was calling an internal method through `execute`. `test cancel` now stops the run, releases the lock, and reports the run id it cancelled; with nothing running it is a success, not an error. The cancelled run is recorded with status `Cancelled` and stays readable through `test results`.

### Fixed
- A client that connects to the bridge and then goes quiet no longer ties up the Editor. Such a connection previously kept its handler and socket alive for the rest of the session, so a crashed or wedged client left something behind every time; the Editor now reclaims it after 30 seconds. A single request is also capped at 32 MB, so a malformed sender can no longer make the Editor buffer without limit. Normal commands are unaffected — the deadline covers only the arrival of the request, never how long the command itself runs.
- `status` and `doctor` no longer report "no live instance" while a reachable Editor is registered. When the pinned instance was unreachable but its process was still alive, both commands stopped at that one target. They now fall back to another registered instance and mark the answer with `failedOverFrom` so it is clear which instance replied. Routing for every other command is unchanged: if the pinned instance is unreachable, commands that modify a project still fail rather than silently retargeting a different one.

## [0.4.2] - 2026-07-20

### Fixed
- `execute --timeout` above 30 seconds now works. The connection timeout stayed at 30 seconds regardless of `--timeout`, so a longer run was cut off by the CLI at 30 seconds and reported as a connection timeout instead of running to the deadline you asked for.
- `execute` now fails when your code throws. Previously an uncaught exception in `execute` code still returned a success response and exit code 0, so scripts and CI could mistake a failed run for a successful one. Such a run now returns an error with exit code 1 and the exception message.
- `instances use` now updates the instance registry atomically. Previously it read, changed, and rewrote the registry in separate steps, so an Editor heartbeat or another CLI command running at the same moment could be silently overwritten, leaving stale or lost instance data.
- A malformed `--json` or `--spec-json` payload now reports a usage error with exit code 2 instead of an internal-looking error with exit code 1. Payloads whose root is not an object, whose `command` is missing or not a non-empty string, or whose `arguments` is neither an object nor null are rejected up front, as are non-object `--spec-json` roots and step entries for `scene patch`, `prefab patch`, and `qa run-sequence`. Callers can now tell bad input apart from a bridge failure.
- The command reference now lists `prefab create` as requiring `--force` to overwrite an existing prefab. The command already refused to overwrite without it; only the documented rule was wrong.
- The README example for `scene add-object` now includes the required `--path`. Copying the old example gave a usage error.
- Running `dotnet test` no longer requires the .NET 9 runtime specifically — a newer runtime works. This only affects people building this repo from source.

### Security
- `test results --run-id` and `record status --recording-id` now require the 32-hex-digit form that `test run` and `record start` return. Both values went into a file path unchecked, so a caller on the local bridge could use path traversal in them to read `.json` files outside the intended directory.

## [0.4.1] - 2026-07-15

### Added
- Projects on different package versions can now share one `unity-cli` on your PATH. Install the CLI from each project's **Window > Unity CLI Manager**; when a command reaches a bridge that speaks an older wire protocol, the CLI hands the command off to the installed version that matches it. Nothing to configure, no flags, and the command's output and exit code pass through unchanged. This also un-breaks projects still on `0.3.x`, which needed no changes of their own.
- The CLI Manager now lists every installed CLI version with its wire protocol, shows which one your PATH resolves to, and offers a **Remove** button per version. Nothing is ever deleted automatically.

### Changed
- The CLI is now installed per version under `~/.unity-cli-bridge/versions/<version>/`. `~/.unity-cli-bridge/unity-cli/` is unchanged as the directory you put on your PATH, so existing PATH entries, aliases, and scripts keep working.
- **Install CLI** now downloads the CLI release matching the project's package version instead of the newest release, because only the matching CLI is guaranteed to speak that package's wire protocol.

### Compatibility
- A project still on package `0.4.0` or earlier has the old CLI Manager, which installs a single flat binary over `~/.unity-cli-bridge/unity-cli/` and overwrites the dispatcher. To recover, open **Window > Unity CLI Manager** in any project on this version or later and click **Install CLI**: it restores the PATH target and files away the flat binary it found. When a previous CLI Manager installed that binary its version is on record, so it is archived under its own version and stays available for hand-off — which is what keeps a project on an older package working.
- CLI binaries downloaded by hand from GitHub Releases carry no version metadata, and nothing in the binary reports its version, so their wire protocol is unknown and they cannot be hand-off candidates. Such a binary is never deleted: it is moved to `~/.unity-cli-bridge/orphaned/` and listed in the CLI Manager under **Unidentified Binaries**, where you can retrieve or remove it. Install from the CLI Manager if you work across projects on different package versions.

## [0.4.0] - 2026-07-14

### Added
- The Unity CLI Manager can now install or remove the AI Agent Skill for the selected scope, so each project can keep the skill version that matches its package version.

### Changed
- AI Agent Skill installation now defaults to the project scope. Installed `SKILL.md` files include the package version they came from, and the Unity CLI Manager reports when an installed skill is older than the current package or shadowed by a global copy that can be updated or removed.

### Fixed
- Fixed package compilation on Unity 6000.5 and later after Unity object instance ID APIs became compile-time errors. `execute` and `custom` results still use the `instanceID` field name for Unity objects, but the value is now emitted as a JSON string so Unity 6000.4 and newer 64-bit object identifiers remain exact for JavaScript consumers.
- Commands issued during a domain reload (script recompilation) no longer fail intermittently with `UNAUTHORIZED`. The Editor keeps its registry entry and auth token across the reload; while the bridge listener is restarting, callers get a retryable unavailable response instead.
- `asset find` no longer fails outright when a search term matches assets under `Packages/`. Package paths resolve through symlinks into the package cache (or, for local packages, outside the project), which the asset-root containment check rejected — so common search terms could break the whole command.
- The Unity CLI Manager window now refreshes status while open and handles prerelease or custom CLI version suffixes without clearing the installer state (#143).

### Security
- Live IPC now requires a per-Editor authentication token. The wire protocol is bumped to `5`, so the CLI binary and Unity package must be upgraded together; mixed versions are rejected before commands run. Each Editor's token lives in its own owner-only file rather than the shared instance registry, so one Editor cannot read or strip another's (#115).

### Compatibility
- Breaking: the minimum supported Unity version is now `2023.1`, and this package pins `com.unity.recorder` `5.1.6` for Play Mode recording. Older Recorder releases fail to compile on Unity `6000.4` and newer, because Unity promotes the obsolete object identity API to a compile-time error.
- Breaking: live IPC requires the CLI and the Unity package to be on the same wire protocol (`5`). Upgrade both together.
- Breaking: Unity objects returned from `execute` results, and from `custom` commands that use `ExecuteValueSerializer`, now report `instanceID` as a JSON string instead of a JSON number. For example, `{"instanceID":568105589213746584}` is now `{"instanceID":"568105589213746584"}`. The field name is unchanged; parse the value as a string to avoid precision loss in JavaScript.

## [0.3.5] - 2026-06-26

### Added
- The **Unity CLI Manager** window (`Window > Unity CLI Manager`) now has a **Refresh** button beside the latest-release version in the Package Info section. It re-checks GitHub for the newest published CLI release right away, bypassing the cached value (which otherwise lingers for up to an hour), so a just-published release shows up without waiting for the cache to expire.

## [0.3.4] - 2026-06-26

### Added
- Play Mode mp4 recording via `record start`, `record stop`, `record status`, and `qa run-sequence --record`.
- `qa ui-dump` and `qa world-dump` now accept token-trimming filters (`--limit`, `--text`, plus UI-only `--interactable-only` and `--omit-rect`) while preserving default output when omitted (#130).
- List-style responses now have opt-in trims: `read-console --no-stacktrace`, `test list --no-detail`, `test run/results --failures-only`, and `instances list --brief` (#131).

### Compatibility
- This package now depends on `com.unity.recorder`; importing projects will pull it in.
- Recording declares `com.unity.recorder` `2.5.2` as the dependency floor, but the resolved Recorder version varies by Unity version. The feature has been verified with Unity 6 and Recorder `5.1.x`; Unity 2021.3 with Recorder `2.5.x` is a known limitation and has not yet been live-verified.

## [0.3.3] - 2026-06-21

### Added
- `scene inspect` and `prefab inspect` accept `--node <path>` to return a single node and its subtree instead of the entire hierarchy. For an agent that only needs one object, this cuts the response — and the tokens spent reading it — by roughly 70–90% on a targeted lookup, and removes the need to dump a full `--with-values` hierarchy before every patch. Omitting `--node` is unchanged. A bare `--node /` means the whole hierarchy, matching how scene paths already treat `/` as the virtual root (#126).
- `package list` accepts `--filter <substring>` (matches a package `name` or display name, case-insensitively) and `--limit <N>`. Checking whether a single package is installed no longer returns the entire dependency set — a targeted lookup drops the response by ~95%. Without these options the output, including sort order, is unchanged (#128).

### Changed
- The bundled AI-agent operator skill now defaults to lower-token command patterns — compact console reads, default-omitting inspects, and smaller agent-facing screenshots — so agent sessions spend fewer tokens out of the box (#125).

## [0.3.2] - 2026-06-21

### Added
- Play Mode mp4 recording via `record start`, `record stop`, `record status`, and `qa run-sequence --record`.
- `qa click`, `qa tap`, and `qa swipe` now accept `--button left|right` so Play Mode QA can drive right-click and right-drag input paths. The default remains `left`.

### Fixed
- Fixed #78: queued IPC commands that have not started yet are now cancelled when the CLI disconnects or times out, preventing delayed writes after the caller has already failed. Commands that have started still use at-least-once semantics, so check state before retrying mutation commands after a timeout.
- `scene set-transform` and `scene assign-material` now refuse to run when the active scene already has unsaved changes, instead of silently saving those unrelated edits along with the requested change. This matches the existing `scene patch` behavior; save or discard first.
- `prefab patch` and `prefab create` (overwrite) now refuse to write when the target prefab is open in a Prefab Stage with unsaved changes — including a parent stage left dirty while you edit a nested Prefab — instead of silently overwriting your in-editor edits. Save or discard the Prefab Stage first (#119).
- `qa ui-dump` label search is now scoped to each element's own subtree, so a label no longer matches text from an unrelated same-named sibling (#113).

### Security
- Asset path normalization now rejects path-traversal inputs (absolute or drive-qualified paths and `.`/`..` segments) and adds a canonical Assets/Packages containment check, so inputs like `Assets/../ProjectSettings/foo` can no longer escape the intended root (#69).
- Scene and prefab component patching now refuses serialized fields that are hidden from `inspect`, so the patchable surface matches what `inspect` shows instead of letting hand-written property paths reach internal or non-editable fields. `SerializeReference` (`$type`) assignments are validated against the field's declared type and must be a constructible, assignable type.

### Compatibility
- This package now depends on `com.unity.recorder`; importing projects will pull it in.
- Recording declares `com.unity.recorder` `2.5.2` as the dependency floor, but the resolved Recorder version varies by Unity version. The feature has been verified with Unity 6 and Recorder `5.1.x`; Unity 2021.3 with Recorder `2.5.x` is a known limitation and has not yet been live-verified.

## [0.3.1] - 2026-06-12

### Added
- `qa world-dump` lists non-UI world objects (3D/2D scene objects, not Canvas UI) that a game opts in to exposing — through the `IQaTappable` interface in code or the `QaTappable` marker component in the inspector. Each entry returns a `label`, hierarchy `path`, image-space center, an `onScreen` flag, and whether it has a tap action. Feed a `path` to `qa tap --target`, which runs the object's own `TryQaTap()` action first and otherwise falls back to an Input System pointer tap at the object's screen anchor — reaching raw Input System polling that coordinate-based `qa tap` and the EventSystem cannot. Off-screen objects are excluded unless `--include-offscreen` is passed. Play Mode is required.
- `qa run-sequence --spec-json <json|@file>` runs a condition-gated, multi-step action sequence in a single call, removing the screenshot → reason → act round-trip that makes deep turn-based and real-time flows tedious. Each step waits until its conditions hold — built-in checks (`active`, `gone`, `transform`, `scene`, `log`, `interactable`) or values the game exposes through the new `IQaQueryable` interface — then runs `key`/`tap`/`swipe`/`wait`/`screenshot` actions. Conditions within a step are ANDed; comparison operators are `==`, `!=`, `>=`, `<=`, `near` (with epsilon), and `changed`. On timeout the response reports which step's conditions went unmet plus a state snapshot for diagnosis. Play Mode is required.

### Fixed
- The **Unity CLI Manager** window (`Window > Unity CLI Manager`) now installs and updates against the latest *published* GitHub release instead of building the download URL from the package version. Previously, when the matching release was still a draft, the installer tried to fetch a draft asset and failed with an error dialog. Draft and pre-release versions are now ignored.
- The installer now tells a failed release check (network error, timeout, GitHub rate limit) apart from "no published release available". A transient failure no longer locks the window into a false "no release" state for up to an hour — it surfaces a retryable "Check failed" status and keeps using the cached release version. An installed CLI whose version is unknown is once again offered a reinstall path.

## [0.3.0] - 2026-06-10

### Added
- `qa ui-dump` lists the currently clickable UI elements as JSON — each with its hierarchy path, component type, child text, interactable state, and image-space rect/center — so an agent can resolve a target in one call instead of capturing a screenshot, analyzing the image, and guessing coordinates. Feed a returned `path` to `qa click --target`, or `centerX`/`centerY` to `qa tap`. Clickable elements are detected via `IPointerClickHandler` and restricted to RectTransform-backed UI; text and interactable state are read by reflection, so the package gains no dependency on `UnityEngine.UI` or TextMeshPro. Interactability honors the `CanvasGroup` chain via `Selectable.IsInteractable()`.
- `screenshot --format <png|jpg>`, `--quality <1-100>`, and `--max-width <int>`. JPEG output and aspect-preserving downscaling make agent-consumed captures much smaller (PNG remains the default). The response reports the `format` written alongside the existing `fileSizeBytes`.
- `qa wait-until --object-interactable <id|path>` waits until a target is active and actually interactable (honoring the `CanvasGroup` chain), and `qa wait-until --object-gone <id|path>` waits until a target disappears from the active hierarchy. These replace fixed sleeps with condition polling. Multiple conditions combine with AND; a self-contradictory combination on the same target is rejected up front.

### Fixed
- `qa tap` / `qa swipe` coordinate conversion now samples the Game View render resolution (`Handles.GetMainGameViewSize()`) instead of the editor panel size, so taps no longer miss their target in Play Mode. Screenshot responses report the same render resolution in `screenWidth`/`screenHeight`.
- When no screenshot has been captured and no explicit size is given, `qa tap` / `qa swipe` / `qa ui-dump` now fall back to the current Game View size as the coordinate basis instead of skipping the Y-flip, so coordinates stay top-left and round-trip even on the first call.

## [0.2.3] - 2026-06-05

### Added
- `execute` now returns structured values: assign to `__pucResult` to receive a type-preserving JSON result (float G9 / double G17 round-trip). `ExecuteValueSerializer` is public for use in `custom` commands.

### Changed
- No-`--project` routing is now safe in multi-instance setups. When two or more Editors are live and none is pinned, the CLI fails with a candidate list instead of silently selecting the first instance. Routing still auto-selects when the current directory is inside a project or a pin is set. A pin set via `instances use` is distinguished from the auto-promoted `activeProjectRoot` and is the only value trusted for the no-`--project` fallback.

### Compatibility
- `activeProjectRootPinned` is an additive registry field that older CLIs/packages ignore; no wire/protocol bump. Pin persistence requires both the CLI and the Unity package to be on this version — in a mixed setup an older Editor-side bridge drops the pin on each heartbeat, after which routing falls back to the safe ambiguous-target error (never a silent mis-route).
- Behavior change: multi-instance commands with no `--project` previously succeeded by picking the first instance; they now fail with a candidate list. Pass `--project`, run from a project directory, or set a default with `instances use`.

## [0.2.2] - 2026-05-29

### Added
- `unity-cli compile --wait` and `unity-cli refresh --wait` flags that block until the Editor finishes compiling, importing, and the bridge is reachable again, so callers don't have to poll status manually.

## [0.2.1] - 2026-05-28

### Changed
- BUSY error responses now include retry and diagnostic guidance instead of encouraging callers to spawn a new Editor.

## [0.2.0] - 2026-05-16

### Added
- `test list`, `test run`, and `test results` commands for running Unity Test Runner suites from the CLI. EditMode returns synchronously; PlayMode returns `STARTED` plus a `runId` immediately, then writes results atomically to `Library/com.yhc509.unity-cli-bridge/test-runs/<runId>.json`. Agents can use `--wait` to poll from the CLI and build a repair loop around failing tests.
- `test run --mode play --no-domain-reload` as an opt-in speed path that can save 30-120 seconds of domain reload overhead when the suite is safe from static state leakage; responses warn about the risk.

### Changed
- The legacy `run-tests` parser path is no longer present. Use `test run` for the structured Test Runner workflow.

### Fixed
- `test run --filter` now matches test full names by case-insensitive substring instead of passing the substring to Unity Test Framework as an exact full-name match.
- PlayMode test runs now keep a watchdog after the initial `STARTED` response, cancel and mark timed-out runs after the configured timeout plus grace period, and restore callback registration after PlayMode domain reloads so results are flushed instead of leaving the single-flight lock stuck.
- `test run --mode play` now fails with `TEST_PLAYMODE_ENTRY_FAILED` if Unity does not begin entering Play Mode shortly after `TestRunnerApi.Execute`.
- Non-`Completed` test run results now return an error envelope and CLI exit code 1; interrupted EditMode runs after domain reload are marked `Failed` instead of leaving the run lock stuck.
- `test run --mode edit --no-domain-reload` is now rejected as a usage error because the option only applies to PlayMode.

## [0.1.13] - 2026-05-13

### Added
- `unity-cli execute --timeout <seconds>` cooperative cancellation token. Wrapper exposes `__pucToken` for user code to check; timeouts surface as `EXECUTE_TIMEOUT` errors. Default 30s, max 600s. Non-cooperative code still occupies the Editor main thread (force-user responsibility).
- `unity-cli doctor` now reports the live-side error code and message when it cannot reach the Editor, so protocol mismatches (`PROTOCOL_MISMATCH`) are distinguishable from a simple unreachable instance.

### Changed
- `instances list`/`status` responses now use `activeProjectRoot` (canonical project root) instead of `activeProjectHash`. Instances stay separated safely in multi-worktree setups even if their 12-character hashes collide. Hash-only target resolution rejects an unsuffixed 12-character hash as ambiguous when a suffixed sibling exists, so accidental routing to the wrong Editor is no longer silent.

### Fixed
- Windows named-pipe listener now takes an OS-level ownership lock per pipe name, preventing two Editors that share the same 12-character hash from both binding the same pipe through a probe race.
- Unix socket cleanup probes the path for a live listener before unlinking, so a freshly bound socket from another Editor is no longer accidentally deleted by a late teardown from the previous owner. Stale suffixed socket files (`hash-1`...`hash-15`) are also reclaimed at acquire time.
- `UnixSocketProbe.IsLive` now uses a 50 ms connect timeout, matching the named-pipe probe and avoiding a startup stall when an old socket file points at a process that no longer accepts connections.

### Compatibility
- Wire protocol bumped from 3 to 4 for the registry identity migration. Upgrade the CLI binary and Unity package together; mixed versions return `PROTOCOL_MISMATCH`. Existing `registry.json` files with `activeProjectHash` are migrated in memory on first load and persisted on the next registry write.

## [0.1.12] - 2026-05-08

### Changed
- Live IPC error responses now carry `error.details` as an inline JSON value (object, string, or null), mirroring the `data` field. CLI `--json` output preserves structured error context directly instead of wrapping it in an escaped JSON string.
- Bumped GitHub Actions workflows to versions running on Node 24 (`actions/checkout@v5`, `actions/setup-dotnet@v5`, `actions/upload-artifact@v6`, `actions/download-artifact@v7`, `softprops/action-gh-release@v3`) ahead of the GitHub Actions Node 20 sunset.

### Compatibility
- Upgrade the CLI binary and Unity package together. The wire `protocolVersion` is bumped to `3` for the inline `error.details` change; mixing versions returns a clear `PROTOCOL_MISMATCH` error pointing to whichever side is out of date.

## [0.1.11] - 2026-05-07

### Added
- Live IPC now reports a clear, side-specific error message when the CLI and Unity package wire versions are incompatible, telling you which side to upgrade instead of returning empty or malformed responses.

### Changed
- Live IPC responses now send payloads as an inline JSON `data` field, so CLI `--json` output preserves structured objects directly instead of wrapping them in a string-valued `dataJson` field.
- JSON output paths (`--output json` and outbound IPC payloads) now emit non-ASCII text (such as Korean) literally instead of escaping it as `\uXXXX`. Existing recipients keep working because `\uXXXX` and literal forms are both valid JSON.

### Fixed
- Inspector and patch responses with `NaN` or `±Infinity` float values now serialize as JSON strings instead of breaking JSON parsing on the CLI side.
- The bridge falls back to a structured `INTERNAL_INVALID_PAYLOAD` error envelope when a handler somehow produces invalid raw JSON, instead of dropping the connection.

### Removed
- Removed the `ResponseEnvelope.dataJson` wire field from live IPC responses.

### Compatibility
- Upgrade the CLI binary and Unity package together. Mixing versions returns a clear protocol-mismatch error pointing to whichever side is out of date, but commands cannot run until both sides match.

## [0.1.10] - 2026-05-07

### Added
- CLI protocol builds now fail if shared protocol source files are added directly under `cli/UnityCli.Protocol/`; protocol sources must live in the Unity package's shared `Runtime/Protocol/` directory.

### Fixed
- Asset create provider registry no longer gets stuck in a partial-init state when a built-in provider registration throws.
- Bridge now returns a structured `INVALID_COMMAND` response when an IPC payload fails JSON deserialization, instead of dropping the connection.
- CLI installer's archive-extract / chmod step no longer risks a pipe-buffer deadlock when the spawned process produces non-trivial stdout or stderr.
- CLI instance registry treats records with unparseable `lastSeenUtc` as stale, subject to live-PID confirmation, instead of keeping them alive forever.
- Protocol JSON handling now explicitly uses `MaxDepth = 128`, avoiding the System.Text.Json default depth of 64 that could make deep scene inspect data fail as silent `data = null`.
- `scene patch` / `prefab patch` `Bounds` mutations with missing `center` or `size` now return a clear `PREFAB_FIELD_INVALID` error instead of crashing with a null reference exception.

## [0.1.9] - 2026-05-03

### Security
- Enforce server-side `--force` gates on destructive or dangerous commands, including asset delete/move/rename, scene and prefab patch destructive operations, execute-code, and package remove. CLI-only validation could be bypassed through raw IPC.
- Add `Library/com.yhc509.unity-cli-bridge/backups/` backup/restore transactions for scene/prefab patch and asset overwrite flows, including `.meta` preservation and critical restore-failure reporting; backups stay outside `Assets/` and normal Unity git tracking.

### Added
- Catalog `ForceRule` metadata as the single source of truth for force-gated commands.
- `raw --force` support to inject `force=true` into raw envelope arguments.
- `BACKUP_FAILED`, `BACKUP_RESTORE_FAILED`, `SCENE_DIRTY`, and `PACKAGE_TIMEOUT` protocol error constants for transactional mutation failures, dirty scene refusal, and stalled Package Manager requests.

### Changed
- `raw --force` now fails fast when the raw payload explicitly conflicts with the flag instead of overwriting payload `force`.
- `scene patch` now refuses already-loaded dirty target scenes even with `--force`; callers must save or discard in-memory scene changes before patching.
- Backup files are written under the project `Library/` folder instead of next to assets, avoiding AssetDatabase scanning and accidental git exposure.

### Fixed
- `package list`, `package add`, `package remove`, and `package search` now poll Unity Package Manager requests from `EditorApplication.update` instead of blocking the editor thread, preserving bridge heartbeats and returning `PACKAGE_TIMEOUT` after 300 seconds if Package Manager stalls.
- Instance registry lock now uses atomic `FileMode.CreateNew` ownership with PID + UTC timestamp content, releases the lock only when this process actually acquired it, and recovers crash-leftover lock files through an open-then-rename-then-delete reclaim path that compares `Process.StartTime` against the recorded lock timestamp. Earlier behaviour could unlink a peer's live lock during a retry and could leave the bridge unable to register itself after a crash.

### Notes
- Prefab Edit Mode dirty-state checks remain a follow-up item for a later PR.

### Compatibility Note
- Destructive command protocol arguments now include a `force` field. Older clients that omit it are interpreted by the bridge as `force=false`, so destructive or dangerous operations are rejected until the client sends `force=true`.

## [0.1.8] - 2026-04-30

### Changed
- Renamed package and internal identifiers (#34). C# namespaces are now under `UnityCliBridge.Bridge.*`, the UPM package id is `com.yhc509.unity-cli-bridge`, the solution file is `UnityCliBridge.sln`, EditorPrefs use the `UnityCliBridge.CLI.*` prefix, the install directory is `~/.unity-cli-bridge`, and the GitHub URL changed.
- Moved the Editor menu entry to `Window > Unity CLI Manager` to match Unity's standard menu location (#34).
- Hardened `execute --args` wrapper compilation by isolating internal wrapper variables behind the reserved `__puc_internal_*` prefix, reporting user-code compile errors from `user-code`, and disabling debug/temp file retention for CodeDOM.

### Notes
- `execute --args` values should not contain secrets or credentials because CodeDOM may briefly create temporary `.cs` files under the OS temp directory; debug/temp retention settings reduce retained artifacts but do not fully prevent transient compiler files.
- The install directory changed; previous install locations from earlier versions are not migrated. Remove any old install manually and reinstall the CLI through `Window > Unity CLI Manager`.

## [0.1.7] - 2026-04-28

### Added
- Friendly key alias catalog for Rigidbody, Collider, Renderer, Light, and Camera component value patches. Aliases like `damping`, `materials[0]`, `shadowStrength`, `fieldOfView`, and `backgroundColor` now resolve to Unity's internal `SerializedProperty.propertyPath` values; multi-candidate aliases (e.g. `damping → m_Drag` on Unity 2021.3 and `m_LinearDamping` on Unity 6) are tried in order before falling back to the original key and `m_PascalCase` (#29).

### Fixed
- Reset Game View screenshot dimensions on Play Mode transitions to prevent stale coordinate scaling when Enter Play Mode Options disables domain reload (#28).

## [0.1.6] - 2026-04-28

### Added
- `execute` now accepts `--args <json>` for passing structured JSON into inline or file-based code execution.
- Execute wrapper code now exposes the supplied JSON as the `__pucArgsJson` string variable.

### Notes
- Requires bridge `0.1.6` or newer. A newer CLI against an older bridge fails to compile when user code references `__pucArgsJson`.

## [0.1.5] - 2026-04-26

### Added
- `screenshot` response now includes 4 metadata fields — `screenWidth`, `screenHeight`, `coordinateOrigin`, `imageOrigin` — so AI agents can determine the `qa tap` coordinate system from the screenshot response alone (#21).

### Changed
- AI skills installer in `Window > Unity CLI Manager` now writes to the user's global skills directory (`~/.claude/skills/`, `~/.codex/skills/`) instead of `{ProjectRoot}/.claude/skills/` and `{ProjectRoot}/.agents/skills/`; the project-local `.claude/skills/unity-cli-operator` symlink was removed (#23).
- Rewrote the `unity-cli-operator` skill so agents stop bypassing it and detouring through `Unity -batchmode` headless runs or searching for a (nonexistent) Unity MCP server: `SKILL.md` description rewritten as a forward-firing trigger and an "Entry rules" section added; `agents/openai.yaml` populated with `short_description` and `default_prompt` rules (#24).
- Bridge package author changed from `yhjang` to `yhc509` (#22).

## [0.1.4] - 2026-04-08

### Added
- QA screenshot coordinate conversion: `qa tap` and coordinate-based `qa swipe` now accept top-origin screenshot-space coordinates with automatic Y-axis inversion and resolution scaling when screenshot dimensions are available.
- `--screenshot-width` / `--screenshot-height` options for `qa tap` and `qa swipe` to explicitly specify screenshot resolution for coordinate scaling.
- Auto screenshot dimension fallback: bridge stores last Game View screenshot resolution and uses it automatically for subsequent QA coordinate conversion.
- macOS ad-hoc codesigning (`codesign -s -`) added to `publish-osx-arm64.sh`.

### Changed
- `qa tap` and coordinate-based `qa swipe` now convert coordinates from screenshot space (Y=0 at top) to Unity screen space (Y=0 at bottom) when screenshot dimensions are known (explicit or auto-detected). Without screenshot dimensions, coordinates pass through as raw screen pixels (backward-compatible).
- Improved error message for unsupported SerializedPropertyType to include the actual type name

### Documentation
- ExposedReference and FixedBufferSize are intentionally unsupported (extremely rare in typical scene/prefab workflows)

## [0.1.2] - 2026-04-07

### Added
- Component operations for scenes and prefabs: list, add, and remove components from the CLI (`scene list-components`, `scene add-component`, `scene remove-component`, `prefab list-components`, `prefab add-component`, and `prefab remove-component`) (#6).
- An AI skill installer in `Window > Unity CLI Manager` for Claude Code and Codex (#8).
- Latest GitHub release version display in `Window > Unity CLI Manager` to simplify update checks (#9).
- `SerializedValueApplier` support for `AnimationCurve` (with `preWrapMode`/`postWrapMode`), `Gradient`, `ManagedReference` (`[SerializeReference]`), and `Hash128` property types — enabling inspect/patch for virtually all Unity built-in components.

### Changed
- **Breaking:** Unified `--target` to `--node` in scene/prefab `add-component` and `remove-component` commands for consistency with other node-targeting commands.
- Split `InspectorUtility` into focused utility classes (`InspectorJsonWriterUtility`, `InspectorPathParserUtility`, `InspectorMutationReaderUtility`, `InspectorDefaultPruningUtility`) for maintainability.
- Reduced GC allocations across bridge handlers: single-parse `argumentsJson`, `ComponentEntry` struct conversion, cached protocol commands and asset descriptors, closure and LINQ elimination.
- Moved `Socket.Bind`/`Listen` to a background thread in `BridgeHost` to avoid editor startup hitch.

### Fixed
- A null check after `AddComponent` to prevent a null reference exception during component creation (#7).
- CLI Manager now preserves the last known release version on network failure instead of clearing the cache.
- Added `UnityWebRequest` timeouts (15 s for version checks, 60 s for downloads).
- Cached `IsUpdateAvailable()` result to avoid repeated version parsing in `OnGUI`.

## [0.1.1] - 2026-04-05

### Added
- `CLI Manager`, an EditorWindow for one-click `unity-cli` install and update from the Unity Editor (#5).
- CI status checks for pull requests and pushes to `main`.

### Changed
- Renamed the CLI binary from `puc` to `unity-cli` (#5).

## [0.1.0] - 2026-04-05

### Added
- Initial release of the UnityCliBridge mono-repo with a .NET 9 CLI, a Unity UPM package, and shared protocol models.
- Live IPC control for a running Unity Editor over local transports, with no manual bridge startup required.
- Scene and prefab inspect and patch workflows for structured hierarchy edits and serialized value changes.
- Asset workflows for search, metadata inspection, creation, move, rename, and delete operations.
- Commands for screenshots, materials, packages, custom execution, and Play Mode QA automation.
- Token-saving output modes for AI workflows, including `--output compact`, `--max-depth`, and `--omit-defaults`.
- Cross-platform distribution for macOS arm64 and Windows x64, plus a GitHub Actions release workflow (#3).

### Changed
- Rebranded the project from `PUC` to `UnityCliBridge`.

### Fixed
- Prevented the Unity "modified externally" dialog after scene and prefab saves (#1).
- Preserved missing `.meta` files during relevant asset operations (#2).
- Improved Windows pipe reconnect behavior and reduced instance registry file contention (#4).

### Removed
- Removed batch mode support; the CLI is now live IPC only and requires a running Unity Editor.
