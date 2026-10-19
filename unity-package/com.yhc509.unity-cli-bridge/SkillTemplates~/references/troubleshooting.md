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

## Test Runner error codes

- `TEST_RUN_IN_PROGRESS` — single-flight lock conflict. The response includes the in-progress `runId`; wait, or check again after the watchdog deadline. If a missing callback leaves the lock held, clear it with `test cancel`.
- `TEST_RUN_TIMEOUT` — `--timeout` elapsed. The result payload is `status: TimedOut` and the CLI exits with code 1.
- `TEST_PLAYMODE_ENTRY_FAILED` — `isPlaying` did not become true within 15 seconds after `TestRunnerApi.Execute`. Check a dirty scene, an in-progress compile, and asmdef discovery first.
- `TEST_LIST_TIMEOUT` — the `RetrieveTestList` callback did not arrive within 30 seconds. The Editor may be compiling or importing assets.
- `TEST_RUN_NOT_FOUND` — there is no on-disk cache for `test results --run-id <id>`. Check the ID that `last-run.json` points at first.
- `TEST_INVALID_MODE` — `--mode` is outside the allowed set. `test run` accepts `edit`/`play`; `test list` accepts `edit`/`play`/`all`.
- `TEST_RUN_FAILED` / `TEST_RUN_CANCELLED` / `TEST_RUN_INTERRUPTED` — a non-`Completed` finish. Returned as an envelope error and CLI exit code 1.
- `CLI_USAGE` — a forbidden combination such as `--mode edit --no-domain-reload`. Change the options to match the message.
