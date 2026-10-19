---
name: unity-cli-bridge
description: "Use when the user wants to operate Unity through Unity CLI Bridge (`unity-cli`/`ucli`), including live command selection, asset commands, scene/prefab inspect/patch flows, QA test automation (Play Mode input simulation), console-log verification, or Unity CLI Bridge troubleshooting."
---

<!-- unity-cli-bridge-package-version: 0.5.5 -->
<!-- unity-cli-bridge-skill-channel: plugin -->

# Unity CLI Bridge

Operating skill for using `unity-cli` safely on real work. The point is not a long command list. It is a fixed flow: select the right project, pick the right command, and check the logs afterward.

This copy is the shared plugin skill for Unity package `com.yhc509.unity-cli-bridge` 0.5.5. It is one skill for every project, so the open project may be on a different package version. Warn the user when the versions differ, then keep going.

`unity-cli status` does not include the package version. Use it to confirm the live editor (`projectName`, `projectRoot`, `unityVersion`, and the play/compile flags). Read `version` from the resolved package file: `Packages/com.yhc509.unity-cli-bridge/package.json` when the package is embedded, otherwise `Library/PackageCache/com.yhc509.unity-cli-bridge@*/package.json`. Ignore `Packages/packages-lock.json` for this check when the dependency is `git` or `file:` — that `version` is a URL or path.

`UNKNOWN_COMMAND`, or a `PROTOCOL_MISMATCH` the CLI could not re-exec, means this project's package and this skill do not describe the same commands. Ask the user to install the matching CLI from that project's `Window > Unity CLI Manager`, and to refresh this plugin (`/plugin marketplace update unity-cli-bridge`, `codex plugin marketplace upgrade unity-cli-bridge`, or `grok plugin marketplace update unity-cli-bridge`). Loaded from the plugin, the slash command is `/unity-cli-bridge:unity-cli-bridge` (bare `/unity-cli-bridge` when the name is free).

## Quick Workflow

1. Find the executable.
- Resolution order, the `ucli` alias, and the install fallback are in [references/command-flows.md](references/command-flows.md) under `Finding the executable`.

2. **Choose the target project and always pass `--project`.**
- Passing `--project`, `pwd -P`, and multi-instance routing are in [references/command-flows.md](references/command-flows.md) under `Choosing the project`.
- A Unity project folder contains both `Assets/` and `Packages/`. If the working directory is not inside one, do not treat `pwd -P` as the project. Run `ucli instances list --brief` and pass `--project` explicitly. Details are under `Choosing the project`.

3. Check status before a write.
- Start with `status --json --project <name>` and confirm the live connection, the busy state, and that this is the right project.
- The response `projectName` must be the project you intended.
- If no live instance exists, do not ask the user to open the Editor. Launch it with `editor launch` (see Editor Lifecycle below).

4. Pick the flow that matches the task.
- Everyday commands, assets, and scene inspect/patch: `references/command-flows.md`
- Prefab assembly and patch specs: `references/prefab-workflows.md`
- Problems: `references/troubleshooting.md`

5. Verify after the task.
- The post-live verification routine and the `read-console --no-stacktrace --output compact` pattern are in [references/command-flows.md](references/command-flows.md) under `Verification routine`.

## Editor Lifecycle

If the target project's Editor is not running, launch it headless yourself. Do not ask the user to do it:

```bash
unity-cli editor launch --project <path>        # headless (-batchmode); waits until the bridge is ready
unity-cli editor stop --project <path>          # graceful quit (EDITOR_DIRTY if there are unsaved changes; --force discards them)
```

- `editor launch` is idempotent. A live instance is reused and the response includes `"reused": true`, so calling it at the start of a workflow is safe and avoids a double-launch dialog.
- The default headless launch keeps the GPU: screenshot, record, and qa work without a window. Rendering commands return `HEADLESS_NO_GRAPHICS` only when you pass `--nographics`. Pass `--gui` when you need a window.
- If an unregistered Editor process already has the project open, launch is rejected with `EDITOR_ALREADY_RUNNING_CONFLICT` (retryable — it may still be booting or compiling). The launch log is `Library/com.yhc509.unity-cli-bridge/editor-launch.log`.
- On a long first import, `EDITOR_WAIT_TIMEOUT` means raise `--timeout <seconds>` and retry (default 300 seconds).

## Operating Rules

- Treat every asset path as `Assets/...`. Read-only commands (`asset find`, `asset info`) also accept `Packages/...`.
- Assume destructive operations and overwrites are allowed only with `--force`. Always force-gated: `asset delete`, `execute`, `package remove`, `scene remove-component`, `prefab remove-component`. Conditionally force-gated: `asset move` / `asset rename` / `asset create` / `prefab create` when they overwrite an existing path; `delete-gameobject` / `remove-component` inside `scene patch`; `remove-node` / `remove-component` inside `prefab patch`.
- Scene/prefab patch and asset overwrite back up the asset body and `.meta` under `Library/com.yhc509.unity-cli-bridge/backups/` before they run. On `BACKUP_RESTORE_FAILED`, stop immediately and recover manually from the backup path in the response.
- `scene patch` refuses a target scene that is already dirty, even with `--force`. Save or discard first, then run it again.
- `execute` / `execute-code` run arbitrary C#, so they always need `--force`.
- JSON passed as `execute --code 'Debug.Log(__pucArgsJson);' --args '{"k":"v"}' --force` is read in user code from the string variable `__pucArgsJson`.
- To return a value, assign `__pucResult = <value>;` in user code. The response sets `hasResult` to `true` and puts type-preserving JSON in `result` (float uses G9, double uses G17 round-trip format).
- A `[PucCommand]` custom command in an Editor assembly may return `ExecuteValueSerializer.Serialize(obj)`. A command in a runtime assembly must serialize precise JSON itself.
- Attach `--timeout <seconds>` to an `execute` that may run for a while. The default is 30 seconds and the cap is 600. Cancellation is cooperative: user code must check `__pucToken`.
- Prefer this pattern in generated loops and waits:

```csharp
for (int i = 0; i < workItems.Count; i++)
{
    __pucToken.ThrowIfCancellationRequested();
    // work
}
```

- In `execute --args` user code, do not declare the wrapper-reserved prefix `__puc_internal_*`, or the variables `__pucToken` and `__pucResult`.
- Do not put secrets or credentials in `execute --args`. CodeDOM compilation can briefly write a `.cs` file to the OS temp directory.
- **Attach `--output compact` by default whenever an LLM consumes the command.** The reason and the exceptions are in [references/command-flows.md](references/command-flows.md) under `Checking status`.
- Before `scene patch`, run `scene inspect --with-values` when you can, and take GameObject paths and field names from that result.
- Before `prefab patch`, run `prefab inspect --with-values` when you can, and take paths and field names from that result. Add `--force` when the spec contains a destructive op such as `remove-node` or `remove-component`.
- When an inspect response is large, cap depth with `--max-depth N` and drop defaults with `--omit-defaults`. **Always pair `--with-values` with `--omit-defaults`** so 0/null/false/empty defaults are omitted (about 30–55% smaller per component).
- `material info` also supports `--omit-defaults`. On URP/Lit that is 48 properties reduced to the ones that changed, about 71% fewer tokens.
- `--omit-defaults` output is read-only. Feeding it back as a patch input does not restore the omitted fields.
- Do not guess `SerializedProperty.propertyPath`. Use the inspect result.
- While a live edit command is compiling or updating, keep only read-only commands and treat the rest as a retry.
- `PROTOCOL_MISMATCH` is usually resolved by the CLI itself. If the target project's bridge speaks a different wire protocol, the CLI switches to an installed build that speaks that protocol and runs the same command again (nothing for the agent to do). If the error still comes back, no installed CLI speaks that protocol. Retrying will not fix it. Ask the user to run `Window > Unity CLI Manager > Install CLI` in **that project's** Unity Editor.
- `package list/add/remove/search` run without blocking the bridge. `PACKAGE_TIMEOUT` means Package Manager did not respond within 300 seconds. Check Package Manager in the Editor, then retry.
- Scene paths use `/Root[0]/Child[0]`. `/` is the virtual scene root.
- Assume the root prefab name is normalized to the file name, because that is how Unity saves it.
- `screenshot` defaults to the game view when `--view` is omitted. Pass `--view scene` when you need the Scene View.
- **A screenshot needs no extra options.** The default is already JPEG quality 75 scaled to 1024px wide, which is a readable size for an agent (about 72% fewer image tokens than a 1080p PNG). Add `--format png --max-width 0` only when you need a lossless original. A `--path` that ends in `.png` is saved as PNG even without `--format`.
- To keep a Play Mode video, use `record start --duration N --wait --path /tmp/out.mp4`. A manual recording is `record start`, then `record status`, then `record stop`. `record start` is Play Mode only and works only in a project with `com.unity.recorder`. If it is missing, the response is `RECORD_FAILED` plus a hint to run `unity-cli package add --name com.unity.recorder`; install it and retry.
- Pass image coordinates you read from a `screenshot` straight to `qa tap --x --y`. The response `imageOrigin` is `top-left` and `coordinateOrigin` is `bottom-left`.
- `qa click`, `qa tap`, and `qa swipe` default to left click / left drag. Add `--button right` when the path under test is a right-click.
- Do not flip Y or scale for resolution yourself. The bridge uses the last `screenshot` size, or the `--screenshot-width` / `--screenshot-height` you pass.
- Do not guess coordinates. Enumerate targets first: uGUI buttons with `qa ui-dump --limit 30 --interactable-only --omit-rect --output compact`, and non-UI world objects (battle-grid units and similar) with `qa world-dump --limit 30 --output compact`. When you know the text or label, filter on the Editor side with `--text <substring>`. Both return `centerX` / `centerY` image coordinates as-is, and dropping the envelope matters most on a large screen dump.
- `qa world-dump` only sees objects the game opted in: a game component implements `UnityCliBridge.Bridge.IQaTappable`, or a `QaTappable` marker is attached. Off-screen objects are included only with `--include-offscreen`.
- Tap a listed world object with `qa tap --target <path>`. If the object's `TryQaTap()` handles it, that runs; otherwise the bridge injects an Input System tap at the anchor. That reaches raw Input System polling that `qa tap --x --y` (EventSystem) does not. Same-named siblings resolve to the first match, so give tappable objects unique names or labels. Details are in [references/qa-testing.md](references/qa-testing.md).
- Send a condition wait and a multi-step input together with `qa run-sequence --spec-json @file`. When you need video, add `--record --record-path /tmp/seq.mp4` so only the sequence interval is saved as mp4. Details are in [references/qa-testing.md](references/qa-testing.md).

### Script Workflow (No Dedicated Commands)

unity-cli does not have dedicated script create/delete commands. Use this combination:

**Create/modify script:**
1. Write .cs file directly to the Assets/ folder via filesystem
2. `unity-cli refresh` — trigger AssetDatabase refresh
3. `unity-cli compile` — request script compilation
4. `unity-cli read-console --type error` — check for compile errors

**Delete script:**
- `unity-cli asset delete --path Assets/Scripts/MyScript.cs --force`
  (handles .meta cleanup and refresh automatically)

### Test Runner Workflow

The default test-runner calls after a code change, when to use `--failures-only` / `--wait` / `--no-domain-reload`, and the rerun loop after `refresh` are in [references/command-flows.md](references/command-flows.md) under `Test runner`.

### Profile Workflow

Diagnose performance with this loop:

1. Run `unity-cli qa run-sequence --spec-json <spec> --profile` to play the sequence and capture at the same time. The response `profileSummary` already contains frame-time percentiles, spike frames, hotspots, GC, and a CPU/GPU-bound verdict.
2. When you only need a capture, start with `unity-cli profile capture start --frames <n>` and wait for the summary with `unity-cli profile capture stop --wait`.
3. After the summary shows a problem (overBudget count, spike, verdict), drill into the local sidecar with `unity-cli profile analyze <captureId> --gc` / `--frame <n>` / `--marker <name>` / `--spikes`. There is no Editor round trip, so repeated reads are safe.
4. Fix the cause in code or assets, then run the same sequence with `--profile` again to make a new capture.
5. Compare before and after with `unity-cli profile compare <baseCaptureId> <headCaptureId>`. One response has the verdict (`regression` / `improvement` / `unchanged`), frame-time / overBudget / GC deltas, and per-marker regressions and improvements. Sensitivity is `--threshold <percent>` (default 5). List length is `--limit <n>`. This also reads local sidecars only, so it works with the Editor closed. When you read it:
   - An unfinished capture (stopped by a script recompile, for example) is not compared. It is rejected with `PROFILE_FAILED`. Recapture that side.
   - `deltaPercent` means something only when the same object's `deltaPercentAvailable` is `true`. If the baseline is 0 (for example the base had zero over-budget frames), a percent is undefined, so read the absolute `delta`.
   - A marker whose self time (`deltaMs`) is flat but whose GC allocation grew lands in `regressions`. One whose allocation shrank lands in `improvements`.
   - A budget, Unity version, or frame-count mismatch is a warning in `notes`. Do not trust the result as-is in that case.
6. For a quick counter sample in Edit Mode, use a preset: `unity-cli profile stats --frames 30 --preset memory` (`frame` / `render` / `gc` / `memory` / `all`).

When a memory leak is the suspect, use the memory loop instead of the frame-time loop:

1. Create a baseline reportId with `unity-cli profile memory`. Reproduce the suspect flow, then run it again for a head reportId. Both must be the **same mode** (both playmode, or both editmode).
2. Compare with `unity-cli profile memory compare <baseReportId> <headReportId>`. The verdict uses the `Total Used Memory` median (`--threshold`, default 5%), and `increases` says which counters grew. This works with the Editor closed.
3. An asset type whose Count and Memory rise together (for example `Texture Count` plus `Texture Memory`) is the first leak candidate. A rise in `GC Used Memory` means managed objects are still alive. A rise in `GC Reserved Memory` alone is heap growth. Do not use `Total Reserved` or `System Used` to call a leak.
4. Take a `.snap` with `unity-cli profile memory snapshot` only when the trend is bad. It needs `com.unity.memoryprofiler` (otherwise it is rejected with an install hint), it does not run at the same time as profile capture, and analysis happens in Unity's Memory Profiler window. The CLI does not parse `.snap`. The file can exceed 1 GB and is not deleted automatically, so delete it when you are done.
5. If `notes` warns about a mode, Unity version, or frame-count mismatch, do not trust the result as-is. If `deltaPercentAvailable` is false, ignore the percent and read the absolute `delta`.

For an unattended regression with the Editor launched by you, use `editor launch` (headless by default), then `play`, capture or memory, `stop`, `editor stop`, and a local `compare`. The full pipeline and the `LIVE_UNAVAILABLE` retry rule during a domain reload are in [references/profiling.md](references/profiling.md) under `Headless regression pipeline`.

**Read [references/profiling.md](references/profiling.md) before interpreting numbers.** In particular:
- These numbers were measured inside the Editor. Do not report absolute values as shipping performance. Use them for relative comparison only.
- If the top hotspot is `Semaphore.WaitForSignal`, `WaitForTargetFPS`, `EditorLoop`, or `Gfx.WaitFor*`, that is a wait or Editor overhead, not the cause. This is a common healthy result.
- `gpuMedianMs: -1` is an unmeasured sentinel, not "GPU 0 ms".
- A large `deltaPercent` is noise when `deltaMs` is negligible next to the frame budget. Unity has never published a noise threshold, so do not invent one.
- The `--budget-ms` default of 16.67 assumes PC 60 fps. For mobile, Unity's recommendation at 30 fps is 21.66 ms.
- Before suggesting an optimization, check the folklore table in `references/profiling.md`. Caching `Camera.main`, `foreach` boxing, and `GetComponent` GC are all wrong advice on current Unity.

### Component Operations

**List components on a node:**
- `unity-cli scene list-components --node "/Player[0]"`
- `unity-cli prefab list-components --path Assets/Prefabs/Player.prefab --node "/Root[0]"`

**Add component (with optional initial values):**
- `unity-cli scene add-component --path Assets/Scenes/S.unity --node "/Player[0]" --type Rigidbody --values '{"mass":5,"damping":1}'`
- `unity-cli prefab add-component --path Assets/Prefabs/P.prefab --node "/Root[0]" --type BoxCollider`

**Remove component:**
- `unity-cli scene remove-component --path Assets/Scenes/S.unity --node "/Player[0]" --type BoxCollider --force`
- `unity-cli prefab remove-component --path Assets/Prefabs/P.prefab --node "/Root[0]" --type BoxCollider --force`

**Friendly key mapping:** Rigidbody, Collider, Renderer, Light, and Camera values accept common keys like `mass`, `damping`, `isTrigger`, `materials[0]`, `shadowStrength`, and `fieldOfView`, which resolve to Unity `SerializedProperty.propertyPath` values. If a key is not found, use `list-components` then `inspect --with-values`.

**Value shapes:** Vector2/3/4, Vector2Int/Vector3Int, Quaternion, Rect, RectInt, and Color accept both a member object (`{"x":1,"y":2,"z":3}`) and a short array (`[1,2,3]`). A Color array with no alpha is 1. A value that arrives wrapped in a string (`"[1,2,3]"`, `"{\"x\":1}"`) is parsed again by the bridge, but **send real JSON**. String wrapping is not unwrapped for an actual string field such as an asset path or an enum name.

## Convenience Commands — prefer the dedicated command

For the tasks below, prefer the dedicated convenience command over `scene patch --spec-json`. That saves calls and tokens.

| Task | Convenience command | What it replaces |
|------|-----------|-------------|
| Add a primitive GameObject | `scene add-object --primitive Cube --parent ... --position x,y,z` | Three steps: add-object (empty GO), inspect, patch Transform |
| Edit a Transform | `scene set-transform --node ... --position/--rotation/--scale` | scene patch modify-component |
| Assign a material | `scene assign-material --node ... --material Assets/...` | inspect plus scene patch `m_Materials.Array.data[0]` |
| Find assets by type | `asset find --type Material` | `--name` used to be required |

- A `scene add-object` response includes `createdPath`, so the next command can use that path without a separate inspect. An object created with `--parent "/Environment[0]"` is `/Environment[0]/Cube[0]`, not `/Cube[0]`.
- Convenience commands that point at a scene or prefab hierarchy node use `--node`. Fields inside a JSON patch spec still use `target` / `parent`.
- An unrecognized key in `set-node` comes back in `warnings`. If every key fails, `patched` is `false`.

## What To Read Next

- Everyday use, asset create, scene inspect/patch: [references/command-flows.md](references/command-flows.md)
- Prefab create/inspect/patch and writing a spec: [references/prefab-workflows.md](references/prefab-workflows.md)
- QA test automation (Play Mode input simulation): [references/qa-testing.md](references/qa-testing.md)
- Reading profile results (markers to ignore, noise, budgets, folklore): [references/profiling.md](references/profiling.md)
- Stale instances, busy, liveReachable, and logs: [references/troubleshooting.md](references/troubleshooting.md)
- JSON templates to start from: `assets/`
