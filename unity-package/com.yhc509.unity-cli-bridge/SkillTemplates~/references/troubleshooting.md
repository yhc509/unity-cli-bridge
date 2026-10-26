# Troubleshooting

## `liveReachable: false`

- Unity may still be importing or compiling.
- The project path may be a symlink. Resolve it again with `pwd -P`.
- Run `instances list` and confirm you are not attached to a different project.

## Busy state

- When `isCompiling` or `isUpdating` is true, treat write commands as a retry flow.
- Until then, keep only read-mostly commands such as `status`, `read-console`, `asset info`, `asset find`, and `prefab inspect`.

## Stale instance

- The registry lives at `~/Library/Application Support/unity-cli/instances.json`.
- If an old instance still appears, close every Editor and inspect that file before opening one again.

## Live connection problems

- Commands that change Editor state, and asset/material/package/scene/prefab commands, all need a live connection.
- When `liveReachable: false`, open the Editor, wait for the bridge import/compile to finish, then retry.
- You may have selected the wrong project. Recheck `pwd -P`, `status --project ... --json`, and `instances use`.

## Checking logs

- Do not close the task on a success response alone.
- After a live command, start with one `read-console --no-stacktrace --output compact` so errors, warnings, and logs come back together.
- Narrow with `--type error` or `--type warning` only when you need that type alone.
- If a new error or warning appears, explain that cause first. Do not report success.

## Profile error codes

- `PROFILE_EMPTY` — a capture ended with 0 frames (`stop` right after `play` or `start`, or the Editor was not ticking). The message names the `captureId`. Retry with `profile capture start --frames <n> --wait`. Returned by `profile capture stop`, `stop --wait`, and `start --wait`.
- `PROFILE_WAIT_TIMEOUT` — `profile capture start --wait` reached `--timeout` before the capture finished. `error.details` has `captureId`, `status`, `capturedFrames`, and `requestedFrames`. The capture keeps running: `profile capture stop --wait` returns what it has. Raise `--timeout` when the Editor runs Play Mode slowly in the background. Check this with `--duration <seconds> --wait --timeout <smaller>`, or with a `--frames` count inside the profiler buffer. `--frames` above the buffer is clamped and the capture stops around that cap (it can come back slightly short), so it does not time out.
- `PROFILE_ALREADY_COMPLETED` — not an error. `profile capture stop` found no running capture, but the last one in this Editor session already finished on its own. The response matches `profile capture stop --wait`: `captureId`, `status`, `capturedFrames`, and `summary`, plus this `notice`.
- Frame-clamp `warning` — not an error, and only present when `--frames` was above the profiler buffer (300 on Unity 2021.3, 2000 on Unity 6; the editor reports its own limit). The capture records up to that buffer and can come back slightly short, because the ring buffer rotates. `capturedFrames` is the actual count. `warning` states the requested count, the clamped cap, and the buffer limit. `frameClamp` has `requested`, `clamped`, and `bufferLimit`. `requestedFrames` is the clamped cap. Present on the start response and on the finished summary (`start --wait`, `stop --wait`, and `stop` after the capture already finished). An unclamped response omits `warning` and `frameClamp`.
- `PROFILE_NOT_RUNNING` — `profile capture stop` with no running capture and no finished capture in this Editor session, or the last one ended as `Interrupted` / `Failed` (the message names it).
- `PROFILE_IN_PROGRESS` — a capture, a memory snapshot, or `profile stats --marker` is already using the profiler. Wait for it to finish.
- `PROFILE_MARKER_AMBIGUOUS` / `PROFILE_NOT_FOUND` — a `--marker` name matched several markers (pass one of the listed full names) or none (similar names are listed). For `profile stats --marker`, a script marker exists only after its method ran while the profiler was recording, so enter Play Mode first.

## Test Runner error codes

- `TEST_RUN_IN_PROGRESS` — the editor is busy with a test run. A second `test run` is refused while one is already in progress; the response includes that `runId`. `play`, `pause`, `stop`, and scene commands also get this error while Unity is restoring the scene after a test run finishes. Wait briefly and retry, or use `test run --wait`, which returns only after that restore. If a missing callback leaves the lock held, clear it with `test cancel`.
- `TEST_RUN_TIMEOUT` — `--timeout` elapsed. The result payload is `status: TimedOut` and the CLI exits with code 1.
- `TEST_PLAYMODE_ENTRY_FAILED` — `isPlaying` did not become true within 15 seconds after `TestRunnerApi.Execute`. Check a dirty scene, an in-progress compile, and asmdef discovery first.
- `TEST_LIST_TIMEOUT` — the `RetrieveTestList` callback did not arrive within 30 seconds. The Editor may be compiling or importing assets.
- `TEST_RUN_NOT_FOUND` — there is no on-disk cache for `test results --run-id <id>`. Check the ID that `last-run.json` points at first.
- `TEST_INVALID_MODE` — `--mode` is outside the allowed set. `test run` accepts `edit`/`play`; `test list` accepts `edit`/`play`/`all`.
- `TEST_RUN_FAILED` / `TEST_RUN_CANCELLED` / `TEST_RUN_INTERRUPTED` — a non-`Completed` finish. Returned as an envelope error and CLI exit code 1.
- `CLI_USAGE` — a forbidden combination such as `--mode edit --no-domain-reload`. Change the options to match the message.
