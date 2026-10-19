---
name: unity-cli-bridge
description: "Use when the user wants to operate Unity through Unity CLI Bridge (`unity-cli`/`ucli`), including live command selection, asset commands, scene/prefab inspect/patch flows, QA test automation (Play Mode input simulation), console-log verification, or Unity CLI Bridge troubleshooting."
---

<!-- unity-cli-bridge-package-version: 0.5.5 -->
<!-- unity-cli-bridge-skill-channel: plugin -->

# Unity CLI Bridge

This copy is the shared plugin skill for Unity package `com.yhc509.unity-cli-bridge` 0.5.5. It is one skill for every project, so the open project may be on a different package version. Warn the user when the versions differ, then keep going.

`unity-cli status` does not include the package version. Use it to confirm the live editor (`projectName`, `projectRoot`, `unityVersion`, and the play/compile flags). Read `version` from the resolved package file: `Packages/com.yhc509.unity-cli-bridge/package.json` when the package is embedded, otherwise `Library/PackageCache/com.yhc509.unity-cli-bridge@*/package.json`. Ignore `Packages/packages-lock.json` for this check when the dependency is `git` or `file:` — that `version` is a URL or path.

`UNKNOWN_COMMAND`, or a `PROTOCOL_MISMATCH` the CLI could not re-exec, means this project's package and this skill do not describe the same commands. Ask the user to install the matching CLI from that project's `Window > Unity CLI Manager`, and to refresh this plugin (`/plugin marketplace update unity-cli-bridge`, `codex plugin marketplace upgrade unity-cli-bridge`, or `grok plugin marketplace update unity-cli-bridge`). Loaded from the plugin, the slash command is `/unity-cli-bridge:unity-cli-bridge` (bare `/unity-cli-bridge` when the name is free).

## Workflow

1. Run `unity-cli` (alias `ucli`). Every command and subcommand takes `--help` (`ucli qa --help`, `ucli qa key --help`).
2. **Always pass `--project <path|name>`.** A project folder has `Assets/` and `Packages/`; if the working directory is not one, run `ucli instances list --brief`. Details: command-flows.md `Choosing the project`.
3. Check `status --project "$P" --output compact` before a write and confirm `projectName`. If no Editor is live, start it yourself with `editor launch --project "$P"` (headless, idempotent, waits until the bridge is ready).
4. After the task, run `read-console --no-stacktrace --output compact`.

## Rules that matter most

- Add `--output compact` whenever an LLM reads the output.
- `play`, `stop`, `refresh`, and `compile` wait until Unity has settled, including the domain reload. **Do not `sleep` or poll `status` after them.** `--no-wait` returns at once.
- Scripts: write the `.cs` file, run `refresh`, then `read-console --type error`.
- `execute --force --code '...'` runs C# in the Editor. Return a value with `__pucResult = ...;`. Pass inputs with `--args '{json}'` and read the string `__pucArgsJson`. A snippet is compiled once per domain (`cacheHit: true`, `compileMs: 0` on repeats), so keep `--code` identical and vary `--args`. Long work: `--timeout <s>` (max 600) and check `__pucToken`.
- Inside `execute`, `Object` is `UnityEngine.Object`, so `Object.FindFirstObjectByType<T>()` compiles as written.
- Destructive or overwriting commands need `--force`. The full list, backups, and dirty-scene rules: [operating-rules.md](references/operating-rules.md).
- Before `scene patch` / `prefab patch`, inspect with `--with-values --omit-defaults` and copy paths and property names from it. Never guess `propertyPath`. Prefer `scene add-object`, `set-transform`, `assign-material` over a patch spec.
- A bare `screenshot` is already agent-sized. Pass its image coordinates straight to `qa tap --x --y`. Do not flip Y or scale. Find targets with `qa ui-dump` / `qa world-dump` instead of guessing.

## Per-frame Play Mode logic — never poll

**Never drive the game by calling `execute` in a shell loop, and never `sleep`-poll state between calls.** Register the logic once with `PucFrameLoop`, let Play Mode run, then read the result:

```bash
unity-cli play --project "$P" --output compact
unity-cli execute --force --project "$P" --output compact --code '
var bird = GameObject.Find("Bird");
PucFrameLoop.Start("autopilot", () => {
    if (bird == null) return false;
    if (bird.transform.position.y < 0f) bird.SendMessage("Flap");
    return true;
}, timeoutSeconds: 20);
__pucResult = "started";'
sleep 10
unity-cli execute --force --project "$P" --output compact --code '__pucResult = PucFrameLoop.Status("autopilot");'
```

- `PucFrameLoop.Start(name, Func<bool>, timeoutSeconds = 60)` runs once per editor frame until it returns false, throws, times out (max 600 s), or Play Mode exits. `Status(name)` gives `running|finished|timeout|stopped|play-mode-exited|error` plus `frames=`/`elapsed=`. `Stop(name)` ends one. `Running` lists them.
- A domain reload drops every loop, so start loops after `play` returns.

## QA input

- QA commands need Play Mode. With the Input System active, `qa tap`/`swipe`/`key` queue real device events.
- On the legacy Input Manager, pointer input is a simulated touch that also reads as `Input.GetMouseButtonDown(0)` and `Input.anyKeyDown`. Unity cannot inject legacy keys, so `qa key` reaches only `IQaKeyHandler` components (`QA_LEGACY_KEY_UNSUPPORTED` otherwise). Use `qa tap`, or a `PucFrameLoop` that calls the game's method.
- Multi-step input with waits: `qa run-sequence --spec-json @file`. Details: [qa-testing.md](references/qa-testing.md).

## Read next

- [operating-rules.md](references/operating-rules.md): editor lifecycle, force/backup rules, inspect sizing, `PROTOCOL_MISMATCH`, records, components, convenience commands
- [command-flows.md](references/command-flows.md): project selection, everyday commands, assets, scenes, test runner, verification
- [prefab-workflows.md](references/prefab-workflows.md): prefab create/inspect/patch specs
- [qa-testing.md](references/qa-testing.md): Play Mode input, coordinates, waits, sequences
- [profiling.md](references/profiling.md): profile/memory loops and how to read the numbers
- [troubleshooting.md](references/troubleshooting.md): stale instances, busy, live-connection problems, logs
- `assets/`: JSON templates
