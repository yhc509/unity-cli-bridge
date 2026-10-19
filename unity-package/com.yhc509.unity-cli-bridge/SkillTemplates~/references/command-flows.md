# Command Flows

## Finding the executable

- If `UNITY_CLI_BIN` is set, use that first.
- If `ucli` or `unity-cli` is on `PATH`, use that path.
- Otherwise look for a `unity-cli` binary in the current directory or a parent directory.
- From the repo root, `./scripts/publish-osx-arm64.sh` builds `dist/unity-cli/unity-cli`.
- If none of those exist, report that an install is required and stop. For frequent calls, add a `ucli` alias or symlink.

## Choosing the project

**Pass `--project` on every command.** Without it, the CLI may route to the Unity project in the current working directory, the pin from `instances use`, or the single live instance, so automation must name the project.

```bash
# Only when the current directory itself is a Unity project
# (it contains both Assets/ and ProjectSettings/)
PROJECT="$(pwd -P)"

# You know the running project's name
PROJECT="<your-project>"

# Developing or testing inside the unity-cli repo
PROJECT="unity-cli-bridge-sample"

# Pin a project by path
PROJECT="/path/to/YourProject"
```

When several projects are open, or the working directory is not a Unity project:

```bash
ucli instances list --brief
```

The command name is `instances list`. `unity-cli` and `ucli` are the same executable.

- If the working directory is not a Unity project, do not use `PROJECT="$(pwd -P)"`. That path is the current directory, not the Unity project, and the command will target the wrong folder. Run `instances list` and pass `--project` explicitly with the `projectRoot` or `projectName` you want.
- If `instances list` is empty, look for a child directory that contains `ProjectSettings/ProjectVersion.txt`. If several match, ask which project to use before passing `--project`.
- `--project` accepts a project name or a full path.
- On macOS, always use the real path from `pwd -P` when the current directory is the Unity project.
- To pin a default for repeated work, run `ucli instances use <projectPath|projectName>`.
- A 12-character hash that matches more than one instance is rejected as ambiguous.

## Checking status

Start from this call:

```bash
ucli status --project "$PROJECT" --output compact
```

Values that matter:

- `projectName` — **confirm this is the project you meant**
- `projectRoot`
- `isCompiling`
- `isUpdating`

> `--output compact` drops envelope metadata (`status`, `transport`, `durationMs`, and similar) and returns only the data payload. Attach it by default whenever an LLM consumes the command. Use `--json` when a person is reading or the full envelope is required.

## Everyday commands

- Connection: `status`, `refresh`
- Play state: `play`, `pause`, `stop`
- Logs: `read-console`
- Run a menu: `execute-menu`
- List menus: `execute-menu --list`
- Switch scene: `scene open`
- Code with arguments: `ucli execute --project "$PROJECT" --code 'Debug.Log(__pucArgsJson);' --args '{"k":"v"}' --force --output compact`

Commands that edit live state should run when the Editor is open and not busy.

## Running menus (execute-menu)

### When to list menus first

Menu paths differ by Unity version. **Do not guess a path.** Confirm it with `--list` first.

Use this when:

- **Before adding UI**: Unity 6 has no `GameObject/UI/Button`. It has `GameObject/UI (Canvas)/Button - TextMeshPro`. Always list first.
- **Before the first menu run**: after connecting, run `--list "GameObject"` once to see the menu tree.
- **When `executed: false` comes back**: the path is wrong or validation failed. Confirm the path with `--list`.
- **Custom package menus**: menus a package registers may not match the standard paths, so list them.

```bash
# List every menu under GameObject
ucli execute-menu --list "GameObject" --project "$PROJECT" --json

# List UI menus only
ucli execute-menu --list "GameObject/UI" --project "$PROJECT" --json

# Run the exact path from the listing
ucli execute-menu --path "GameObject/UI (Canvas)/Button - TextMeshPro" --project "$PROJECT" --json
```

### When execute-menu is enough

Adding an object from a built-in Unity menu (3D Object, UI, Audio, Light, Effects, and similar) does not need a dedicated template command. Before inventing a new command, check whether a menu already does it.

## Live connection required

Every command except `instances list`, `instances use`, and `doctor` needs a running Unity Editor with the bridge connected.

- If `liveReachable` is false in `status --json`, open the Editor and wait for the bridge import/compile to finish.
- `compile`, `refresh`, and asset/material/package/scene/prefab commands all run over live IPC.
- Treat `play`, `pause`, `stop`, `read-console`, `execute-menu`, `execute`, `custom`, and `screenshot` as live-only too.

## Asset workflow

### Find

```bash
ucli asset find --project "$PROJECT" --name Sample --folder Assets --limit 10 --output compact
ucli asset find --project "$PROJECT" --type Material --output compact
ucli asset types --project "$PROJECT" --output compact
ucli asset info --project "$PROJECT" --path Assets/Scenes/SampleScene.unity --output compact
```

> **`--name` is a Unity search-filter text query, not a glob.** Write `Sample`, not `Sample*`. Unity's `AssetDatabase.FindAssets("Sample")` already matches partially. `--type` alone is enough (for example `--type Scene` becomes `FindAssets("t:Scene")`). Combining them becomes `FindAssets("name t:Type")`.

> **`--folder`**: limits the search to one folder. The default is all of `Assets`. Child folders are included recursively. `--folder Assets/Prefabs` searches the Prefabs folder and everything under it.

### Create

```bash
ucli asset create --project "$PROJECT" --type material --path Assets/Materials/NewMaterial --output compact
ucli asset create --project "$PROJECT" --type scriptable-object --path Assets/Data/NewData --type-name MyNamespace.MyData --data-json '{"title":"hello"}' --output compact
```

### Safety

- Delete always needs `--force`
- Rename, move, and create-overwrite need `--force`
- Confirm the target with `asset info` or `asset find` first

## Scene workflow

### Inspect

```bash
# Structure only (cap depth and drop defaults to save tokens)
ucli scene inspect --project "$PROJECT" --path Assets/Scenes/SampleScene.unity --max-depth 2 --omit-defaults --output compact

# Include one node's component values (required before a patch). Always pair --with-values with --omit-defaults
ucli scene inspect --project "$PROJECT" --path Assets/Scenes/SampleScene.unity --with-values --omit-defaults --output compact
```

### Add an object

```bash
# Empty GameObject
ucli scene add-object --project "$PROJECT" --path Assets/Scenes/SampleScene.unity --name MyObject --output compact

# Primitive (MeshFilter + MeshRenderer + Collider are added for you)
ucli scene add-object --project "$PROJECT" --path Assets/Scenes/SampleScene.unity --name Floor --primitive Plane --parent "/Environment[0]" --position 0,0,0 --output compact
```

`--primitive` accepts Cube, Sphere, Capsule, Cylinder, Plane, and Quad. Passing `--parent` and `--position` together creates and places the object in one call, and the response includes `createdPath`, so a follow-up inspect is unnecessary. A Floor created under `/Environment[0]` is `/Environment[0]/Floor[0]`, not `/Floor[0]`.

### Edit Transform

```bash
ucli scene set-transform --project "$PROJECT" --node "/Environment[0]/Floor[0]" --position 3,0,0 --scale 2,2,2 --output compact
```

Pass at least one of `--position`, `--rotation`, or `--scale`. Prefer this convenience command over `scene patch --spec-json`.

### Assign a material

```bash
ucli scene assign-material --project "$PROJECT" --node "/Environment[0]/Floor[0]" --material Assets/Materials/MyMat.mat --output compact
```

Assigns the material to the node's `MeshRenderer.sharedMaterials[0]`. This is simpler than writing `m_Materials.Array.data[0]` in a `scene patch`.

### Patch from a spec

```bash
ucli scene patch --project "$PROJECT" --path Assets/Scenes/SampleScene.unity --spec-file /tmp/scene-patch-basic.json --output compact
```

To start quickly, copy the installed skill's `assets/scene-patch-basic.json` to a separate file such as `/tmp/scene-patch-basic.json` and edit that copy.

### Safety

- Confirm the GameObject path and component fields with `scene inspect --with-values` first
- Common Rigidbody, Collider, Renderer, Light, and Camera values accept friendly keys such as `mass`, `damping`, `isTrigger`, `materials[0]`, `shadowStrength`, and `fieldOfView`
- `--omit-defaults` output is read-only. Feeding it back as a patch input does not restore the omitted fields
- Node paths use `/Root[0]/Child[0]`
- `/` is the virtual scene root, and is only a parent when adding a root GameObject
- If the target scene is already open, save it or discard changes so it is clean
- `delete-gameobject`, `remove-component`, and a `scene open` that discards a dirty scene need `--force`

## Material inspect

```bash
# Every property
ucli material info --project "$PROJECT" --path Assets/Materials/MyMat.mat --output compact

# Drop defaults (saves tokens; URP/Lit's 48 properties shrink to the ones that changed)
ucli material info --project "$PROJECT" --path Assets/Materials/MyMat.mat --omit-defaults --output compact
```

## Screenshot

```bash
# Game View — the default is already jpg q75 scaled to 1024px (--view defaults to game)
ucli screenshot --project "$PROJECT" --path /tmp/capture.jpg --output compact

# Only when a lossless original is required
ucli screenshot --project "$PROJECT" --path /tmp/capture.png --format png --max-width 0 --output compact

# Scene View
ucli screenshot --project "$PROJECT" --path /tmp/scene.jpg --view scene --output compact
```

## Test runner

`test list` / `test run` / `test results` drive the Test Runner without restarting the Editor.

Default list and run:

```bash
ucli test list --project "$PROJECT" --mode all --no-detail --output compact
ucli test run --project "$PROJECT" --mode edit --filter PlayerControllerTests --failures-only --output compact
ucli test run --project "$PROJECT" --mode play --filter Smoke --wait --failures-only --output compact
ucli test results --project "$PROJECT" --run-id <runId> --failures-only --output compact
```

- `test list --mode all` lists EditMode and PlayMode tests together
- `test run --mode edit` returns the result payload synchronously
- `test run --mode play` returns `STARTED` and a `runId` immediately by default
- Add `--wait` when the CLI should poll until the PlayMode result is ready
- `--filter` is a case-insensitive substring of the test full name
- `test results --run-id <runId>` re-reads the on-disk cache or progress of a run that already started
- `--failures-only` keeps the summary counts and trims `tests[]` to non-passed entries
- `test list --no-detail` returns only `fullName` and `mode`

If a test run stalls because a callback never arrived or the Editor misbehaved, and the lock stays held, clear it with `ucli test cancel --project "$PROJECT" --output compact`. That tries a graceful `TestRunnerApi` cancel first, always releases the lock, and returns the cancelled `runId`. With no run in progress it returns a no-op success.

When a PlayMode domain reload can be skipped:

```bash
ucli test run --project "$PROJECT" --mode play --filter Smoke --wait --no-domain-reload --failures-only --output compact
```

- `--no-domain-reload` is a PlayMode-only speed option
- Static state can leak, so use it only when the tests reset themselves
- Combining it with EditMode is a usage error

Keep the AI repair loop short:

```bash
ucli refresh --project "$PROJECT" --output compact
ucli test run --project "$PROJECT" --mode edit --filter <related-name> --failures-only --output compact
```

On failure, fix from the failing test name, message, and stack trace, then rerun the same filter. A non-`Completed` result comes back as envelope `status: error` and CLI exit code 1.

## Verification routine

Default check after a live command:

```bash
# Omitting --type returns error, warning, and log together — no need to split this into two calls
ucli read-console --project "$PROJECT" --limit 10 --no-stacktrace --output compact
# Narrow only when one type is required
ucli read-console --project "$PROJECT" --type error --limit 10 --no-stacktrace --output compact
```

If an error or warning is present, do not close the task as a success.
