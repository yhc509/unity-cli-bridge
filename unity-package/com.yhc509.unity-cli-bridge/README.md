# Unity CLI Bridge

`com.yhc509.unity-cli-bridge` is the bridge package that lets `unity-cli` control a running Unity Editor over local IPC. Its key advantage is simple operation: no manual server startup, no per-project ports, and project-aware attachment by default.

## Requirements

- Unity `2021.3` LTS or newer, including Unity 6. Checked on `2021.3.45f2`, the latest public 2021.3 patch. Later 2021.3 releases are paid extended-LTS builds.
- the companion CLI `unity-cli`

## What This Package Provides

- A local bridge that starts automatically when the Editor opens
- Per-project instance registration and automatic selection
- Live editor control: `status`, `refresh`, `compile`, `play`, `pause`, `stop`, `execute-menu`, `execute`, `custom`, `screenshot`, `read-console`
- Asset commands: `find`, `types`, `info`, `reimport`, `mkdir`, `move`, `rename`, `delete`, `create`
- Material commands: `info`, `set`
- Package commands: `list`, `add`, `remove`, `search`
- Scene commands: `open`, `inspect`, `patch`
- Prefab commands: `inspect`, `create`, `patch`
- A single live IPC command surface for controlling the running Editor

In practice, this means the package can expose Unity as a project-aware automation surface instead of a manually managed editor plugin session.

- It removes the need to keep a custom bridge server running.
- It removes per-project port configuration when several editors are open.
- It keeps project-aware live editor work on one protocol and command model.
- It gives the CLI direct access to asset, material, package, and prefab workflows instead of relying only on menu execution.

## Install

This package lives inside the `unity-cli-bridge` mono-repo.

For local development, add a file reference to `unity-package/com.yhc509.unity-cli-bridge` in your Unity project's `Packages/manifest.json`.

For Git-based installation, use the package path inside the repository.

```json
{
  "dependencies": {
    "com.yhc509.unity-cli-bridge": "https://github.com/yhc509/unity-cli-bridge.git?path=/unity-package/com.yhc509.unity-cli-bridge#main"
  }
}
```

If you are migrating from the old package, update the dependency key in `Packages/manifest.json` from `com.puc.bridge` to `com.yhc509.unity-cli-bridge`. If your Unity project references the bridge asmdefs directly, also rename `PUC.Editor` / `PUC.Runtime` references to `UnityCliBridge.Bridge.Editor` / `UnityCliBridge.Bridge.Runtime`.

The CLI executable is installed separately from **Window > Unity CLI Manager**. The package includes the AI Agent Skill template, and the same window can install or remove it for Claude Code, Codex, or Grok Build.

By default, the skill installs into the current Unity project under `<project-root>/.claude/skills/`, `<project-root>/.agents/skills/` (Codex), or `<project-root>/.grok/skills/`. The same window can target the git repository root or your home directory instead. Commit a project or git-root install if you want teammates to use the same skill version as the package. Choose User (home) only when you want one user-wide copy. Older `.codex/skills/unity-cli-bridge` copies are left in place until you remove them from the window.

## Notes

- This package includes `Newtonsoft.Json.dll` in `Editor/Plugins` for scene/prefab spec parsing.
- Play Mode recording depends on Unity Recorder, which this package does not install. Add `com.unity.recorder` `3.0.0` or newer if you want `record` or `qa run-sequence --record`. On Unity `6000.4` and newer, use Recorder `5.1.6` or later — older Recorder releases fail to compile there.
- In Play Mode, `screenshot --view game` uses `ScreenCapture.CaptureScreenshotAsTexture()`. `--width` and `--height` can downscale the native Game View capture, but larger requests log a warning and save the native capture without upscaling.
- `input-actions` assets are created as JSON files that Unity's Input System importer reads.
- `scene inspect --with-values` is meant to be used as the source of truth when authoring `scene patch` specs.
- `scene patch` uses `/Root[0]/Child[0]` paths, treats `/` as the virtual scene root, and requires `--force` for destructive ops.
- `prefab patch` requires `--force` for destructive ops such as `remove-node` and `remove-component`.
- `execute` is live-only and always requires `--force` because it runs arbitrary C# in the editor context.
- `package remove` always requires `--force`.
- Package Manager requests are single-flight. If another package command is already running, the bridge returns `PACKAGE_BUSY`; package commands use a 360-second CLI live timeout so the bridge can surface its 300-second `PACKAGE_TIMEOUT` response.
- Token trim options are opt-in and preserve default output: `read-console --no-stacktrace`, `test list --no-detail`, `test run/results --failures-only`, `qa ui-dump --limit/--interactable-only/--text/--omit-rect`, and `qa world-dump --limit/--text`.
- `custom` is live-only and invokes project-defined static methods marked with `[PucCommand("name")]`.
- if the target scene is already loaded, `scene inspect` expects it to be clean and `scene patch` refuses unsaved target-scene changes even with `--force`.
- `scene open` requires `--force` if the currently loaded scenes have unsaved changes that should be discarded.
- Scene/prefab patch and asset overwrite flows create backups for the asset body and `.meta` under `Library/com.yhc509.unity-cli-bridge/backups/`, keeping rollback files outside `Assets/` and normal git tracking.
- If backup restore itself fails, the bridge returns `BACKUP_RESTORE_FAILED` with backup paths for manual recovery.
- `prefab patch` values are applied through `SerializedProperty.propertyPath`.
- `prefab inspect --with-values` is meant to be used as the source of truth when authoring patch specs.
- The root prefab object name is normalized to the prefab file name after save.
- This package does not include the CLI executable itself.
