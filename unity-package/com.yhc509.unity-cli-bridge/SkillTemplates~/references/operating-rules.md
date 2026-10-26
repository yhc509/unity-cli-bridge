# Operating Rules

The full rule set behind the short list in `SKILL.md`. Read the section you need.

## Editor lifecycle

If the target project's Editor is not running, launch it headless yourself. Do not ask the user to do it:

```bash
unity-cli editor launch --project <path>        # headless (-batchmode); waits until the bridge is ready
unity-cli editor stop --project <path>          # graceful quit (EDITOR_DIRTY if there are unsaved changes; --force discards them)
```

- `editor launch` is idempotent. A live instance is reused and the response includes `"reused": true`, so calling it at the start of a workflow is safe and avoids a double-launch dialog.
- The default headless launch keeps the GPU: screenshot, record, and qa work without a window. Rendering commands return `HEADLESS_NO_GRAPHICS` only when you pass `--nographics`. Pass `--gui` when you need a window.
- If an unregistered Editor process already has the project open, launch is rejected with `EDITOR_ALREADY_RUNNING_CONFLICT` (retryable — it may still be booting or compiling). The launch log is `Library/com.yhc509.unity-cli-bridge/editor-launch.log`.
- On a long first import, `EDITOR_WAIT_TIMEOUT` means raise `--timeout <seconds>` and retry (default 300 seconds).

## Rules

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

- `play`, `stop`, `refresh`, and `compile` wait until Unity has settled: Play Mode fully entered (or exited) with the bridge reachable again after the domain reload, or the compile/import finished. Do not add `sleep` after them. `--no-wait` returns right after the request.
- Inside `execute`, `Object` means `UnityEngine.Object` (the wrapper adds `using Object = UnityEngine.Object;`), so `Object.FindFirstObjectByType<T>()` compiles as written. A user alias with the same name wins.
- A snippet is compiled once and cached until the next domain reload. A repeat call with the same `--code` skips compilation (about 0.5 s saved per call; the response sets `cacheHit: true` and `compileMs: 0`), so keep the code identical and pass changing values through `--args`.
- `execute` compiles C# 9 snippets with the Roslyn host by default (the C# compiler shipped inside the Unity editor, on the editor's bundled .NET). Set `UCB_EXECUTE_COMPILER=codedom` and restart the editor to turn the host off and compile with Unity's bundled mcs instead. The host does not accept covariant returns, default interface methods, function pointers, or C# 10 or newer. On the mcs path, versions above 7.2, including the C# 9.0 project default, are passed as `/langversion:latest` so `default` literals compile; 7.2 and below are passed through. A `--code` snippet or a `--file` may start with `using` directives, including a one-line `--code` value such as `using UnityEngine; return Application.unityVersion;`; they are hoisted above the wrapper. The cache key is still the wrapped source. Compile failures include the compiler error text.
- In `execute --args` user code, do not declare the wrapper-reserved prefix `__puc_internal_*`, or the variables `__pucToken` and `__pucResult`.
- Do not put secrets or credentials in `execute --args`. CodeDOM compilation can briefly write a `.cs` file to the OS temp directory.
- **Attach `--output compact` by default whenever an LLM consumes the command.** The reason and the exceptions are in [references/command-flows.md](command-flows.md) under `Checking status`.
- Before `scene patch`, run `scene inspect --with-values` when you can, and take GameObject paths and field names from that result.
- Before `prefab patch`, run `prefab inspect --with-values` when you can, and take paths and field names from that result. Add `--force` when the spec contains a destructive op such as `remove-node` or `remove-component`.
- When an inspect response is large, cap depth with `--max-depth N` and drop defaults with `--omit-defaults`. **Always pair `--with-values` with `--omit-defaults`** so 0/null/false/empty defaults are omitted (about 30–55% smaller per component). `enabled` / `m_Enabled` default to true, so `false` is kept, including on renderers inside nested prefab instances. Those nodes also include `prefab.assetPath` (and `prefab.nested` when the instance is nested).
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
- Tap a listed world object with `qa tap --target <path>`. If the object's `TryQaTap()` handles it, that runs; otherwise the bridge injects an Input System tap at the anchor. That reaches raw Input System polling that `qa tap --x --y` (EventSystem) does not. Same-named siblings resolve to the first match, so give tappable objects unique names or labels. Details are in [references/qa-testing.md](qa-testing.md).
- Send a condition wait and a multi-step input together with `qa run-sequence --spec-json @file`. When you need video, add `--record --record-path /tmp/seq.mp4` so only the sequence interval is saved as mp4. Details are in [references/qa-testing.md](qa-testing.md).

## Script workflow (no dedicated commands)

unity-cli does not have dedicated script create/delete commands. Use this combination:

**Create/modify script:**
1. Write .cs file directly to the Assets/ folder via filesystem
2. `unity-cli refresh` — refresh the AssetDatabase; it waits until the resulting compile/import finishes and the bridge is reachable again
3. `unity-cli compile` — only when you need an explicit recompile without file changes; it also waits by default
4. `unity-cli read-console --type error` — check for compile errors. `--type error` also includes Exception and Assert; the entry `type` stays `Exception` or `Assert`.

**Delete script:**
- `unity-cli asset delete Assets/Scripts/MyScript.cs --force`
  (`--path` is the same path. Deletion handles .meta cleanup and refresh automatically)

## Component operations

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

## Convenience commands — prefer the dedicated command

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
