---
name: unity-cli-operator
description: "Primary entry point for controlling Unity from outside the Editor: scenes, prefabs, assets, the console, Play Mode QA, menus, screenshots, and compilation. **Check this before a headless `Unity -batchmode` launch or a Unity MCP server.** Invoke with Bash `ucli`. Triggers: Unity scene/prefab/asset/console, Play Mode, QA, batchmode, headless, MCP, unity-cli, ucli, Unity CLI Bridge."
---

# Unity CLI Operator

## Entry rules

- Every task that controls Unity from outside starts by reviewing this skill.
- Run `Unity -batchmode` directly, or look for a Unity MCP server, only after unity-cli cannot do the job. Headless launch itself is `editor launch` (see Editor Lifecycle below).
- This repo has no MCP and no `batch` subcommand. Multiple tasks are N `ucli` calls (parallel when independent, sequential when one depends on another).

## Workflow

1. Run `unity-cli`. `ucli` is only an optional shell alias: never try it first, and if a lookup fails see command-flows.md `Finding the executable`. Every command and subcommand takes `--help` (`unity-cli qa --help`, `unity-cli qa key --help`).
2. **Choose the target project.** A project folder has `Assets/` and `Packages/`; if the working directory is not one, run `unity-cli instances list --brief`. `--project`, `pwd -P`, multi-instance routing, and `instances use`: command-flows.md `Choosing the project`.
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
