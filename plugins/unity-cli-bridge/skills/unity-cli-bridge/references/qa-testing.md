# QA Test Automation

How to simulate input in Play Mode and check the result.

## Test flow

```
status → play → (simulate input) → verify (logs + screenshot) → stop
```

1. Confirm `isCompiling: false` with `status --json`
2. Enter Play Mode with `play` (`runInBackground = true` is set automatically)
3. Simulate input with QA commands
4. **Log check**: `read-console --type error --limit N --no-stacktrace` (Error, Exception, and Assert) plus `read-console --type log --limit N --no-stacktrace`
5. **Visual check**: `screenshot --view game --path /tmp/qa-check.jpg`, then look at the image (the default is jpg q75 scaled to 1024px; a lossless original needs `--format png --max-width 0`)
6. Leave Play Mode with `stop` (`runInBackground` is restored)

## Choosing an input command

| Command | How it works | When to use it |
|--------|-----------|-----------|
| `qa click --target <path>` | EventSystem.Execute | Click a UI button, toggle, and similar |
| `qa click --qa-id <id>` | EventSystem.Execute | A target marked with `[QaTarget]` |
| `qa ui-dump [--limit N] [--interactable-only] [--text S] [--omit-rect] [--screenshot-width W --screenshot-height H]` | Bridge UI structure dump | Look up path/text/rect/center of clickable UI |
| `qa tap --x N --y N [--screenshot-width W --screenshot-height H]` | EventSystem raycast + Execute | Tap UI coordinates read from a screenshot |
| `qa swipe --target <path> --from x,y --to x,y` | Input System target-relative swipe | Drag a UI slider or ScrollRect |
| `qa swipe --from x,y --to x,y [--screenshot-width W --screenshot-height H]` | Input System screenshot coordinates | Screen swipe from a screenshot |
| `qa key --key <name>` | Input System keyboard | Simulate a key |
| `qa wait --ms N` | CLI-side wait | Gap between commands |
| `qa wait-until --scene <name>` | Bridge polling | Wait for a scene change |
| `qa wait-until --log-contains <text>` | Bridge polling | Wait for a specific log line |
| `qa wait-until --object-exists <path>` | Bridge polling | Wait until an object exists |
| `qa wait-until --object-interactable <path>` | Bridge polling | Wait until a button or target is clickable |
| `qa wait-until --object-gone <path>` | Bridge polling | Wait until a loading indicator or object is inactive or destroyed |
| `qa run-sequence --spec-json @seq.json [--record]` | Bridge deferred state machine | Run condition waits and input actions in one request, optionally recording mp4 |

## How to pick the input path

### UI click → EventSystem path (click)
- Buttons, toggles, and anything EventSystem handles immediately
- Pass a hierarchy path with `--target`: `/Canvas[0]/Button[0]`
- The scene needs an EventSystem (otherwise `QA_NO_EVENT_SYSTEM`)

### Finding clickable UI → structured dump (ui-dump)
- `qa ui-dump` collects active GameObjects in the current Play Mode view that have `IPointerClickHandler`.
- The response `path` can go straight into `qa click --target <path>`.
- `path` is reliable when it is unique. If same-named siblings share a path, use `centerX`/`centerY` with `qa tap`.
- `centerX`/`centerY` are screenshot image coordinates (top-left origin), so they go straight into `qa tap --x <centerX> --y <centerY>`.
- `text` is read by reflection from a child `text` property such as UGUI Text or TMP_Text. It works without the TMP package.
- `qa ui-dump` itself does not raycast, so it does not require an EventSystem. A real `qa click` and a coordinate `qa tap --x/--y` do.

### UI coordinate tap → EventSystem raycast path (tap)
- Use this to hit the UGUI target under coordinates read from a screenshot
- A `screenshot` response has `imageOrigin` `top-left` and `coordinateOrigin` `bottom-left`. In the usual case, pass the image coordinates `(x, y)` you read from the screenshot straight to `qa tap --x --y`.
- `qa tap` accepts top-origin screenshot coordinates and converts them to Unity screen space (bottom-origin) at execution time.
- It defaults to the `width`/`height` of the last successful `screenshot` and scales to the current `Screen.width`/`Screen.height`.
- The automatic fallback cache resets when Play Mode enters or exits, so a previous session's screenshot size is not reused even when domain reload is off.
- EventSystem raycasts the coordinate, picks the top target, and if the click handler is on a child it walks up to the actual `IPointerClickHandler`.
- If there is no UI target under the coordinate, the command returns `QA_TAP_NO_TARGET`.

### UI drag → target-relative Input System path (swipe --target)
- Pass a hierarchy path with `--target`: `/Canvas[0]/Scroll View[0]/Viewport[0]`
- The target needs a `RectTransform` (otherwise `QA_TARGET_NOT_RECT_TRANSFORM`)
- `--from`/`--to` are **pixel offsets** from the center of the target `RectTransform`
- The bridge converts those offsets to screen coordinates, then runs a multi-frame deferred swipe
- UGUI consumes the input only when the scene has an EventSystem

### Non-UI, gesture, or key input → simulated device input (swipe, key)
- Touching a 3D object, a screen-swipe gesture, or a keyboard press
- With the Input System active, the bridge queues Input System device events
- With only the legacy Input Manager active (Active Input Handling = Input Manager (Old), or no `com.unity.inputsystem`), see `Legacy Input Manager` under Prerequisites
- Coordinate `qa swipe` uses **screenshot space**.
- Y=0 is the top of the screenshot. The bridge converts to Unity screen space (bottom-origin) at execution time.
- It defaults to the `width`/`height` of the last successful `screenshot` and scales to the current `Screen.width`/`Screen.height`.
- The automatic fallback cache resets when Play Mode enters or exits, so a previous session's screenshot size is not reused even when domain reload is off.
- Pass `--screenshot-width`/`--screenshot-height` together only when you must convert from a different reference image size.
- Every `swipe` and `key` uses a deferred or frame-based input path

## Prerequisites

### EventSystem
`qa click`, coordinate `qa tap --x/--y`, and UGUI `qa swipe --target` need an EventSystem in the scene. A world-object `qa tap --target` uses a direct action or an Input System coordinate fallback, so an EventSystem is not required.
Add one from the menu:

```bash
# Confirm the menu path first
ucli execute-menu --list "GameObject/UI" --project "$P" --json

# Add an EventSystem
ucli execute-menu --path "GameObject/UI (Canvas)/Event System" --project "$P" --json
```

### Input System or legacy Input Manager
With the Input System active, `qa key`, `qa swipe`, and the pointer fallbacks of `qa tap` queue Input System device events.

### Legacy Input Manager
A project on the legacy Input Manager (no `com.unity.inputsystem`, or Active Input Handling = Input Manager (Old)) is supported without installing anything:
- Pointer input (`qa tap --x/--y` that hits no UI, `qa tap --target` world fallback, coordinate `qa swipe`, and the same `qa run-sequence` actions) is a simulated touch. Unity reports it as `Input.touches`, as left mouse button 0 (`Input.GetMouseButtonDown(0)`, `Input.mousePosition`), and as `Input.anyKeyDown`. A tap holds for two frames so `GetMouseButtonDown` polling in `Update` sees it. A coordinate tap with no EventSystem in the scene goes straight to this path.
- `--button right` is not available (`QA_LEGACY_BUTTON_UNSUPPORTED`).
- `qa key` cannot reach `Input.GetKey*` — Unity has no API to inject legacy keyboard state. The key is delivered to every active MonoBehaviour that implements `UnityCliBridge.Bridge.IQaKeyHandler` (`bool TryQaKey(KeyCode key)`); with none, the error is `QA_LEGACY_KEY_UNSUPPORTED`. Key names accept `KeyCode` names and the common Input System names (`Space`, `Enter`, `Digit1`, `LeftCtrl`).
- For a key-driven legacy game, prefer `qa tap` when the game also accepts mouse/touch, or drive it from a `PucFrameLoop` that calls the game's own method (see `SKILL.md`).

### Play Mode
Every QA command runs only in Play Mode. Outside Play Mode the error is `QA_NOT_PLAYING`.
`qa world-dump` is the exception: outside Play Mode it returns `editMode: true`, a hint to use `scene inspect`, and any opted-in world objects without screenshot coordinates. Run `play` when you need `centerX` / `centerY`.

## Building test UI

Add UI with Unity's built-in menus via `execute-menu`. No custom command is required.

```bash
P="$(pwd -P)"

# Menu paths differ by Unity version
ucli execute-menu --list "GameObject/UI (Canvas)" --project "$P" --json

# Add Canvas + EventSystem + Button
ucli execute-menu --path "GameObject/UI (Canvas)/Canvas" --project "$P" --json
ucli execute-menu --path "GameObject/UI (Canvas)/Event System" --project "$P" --json
ucli execute-menu --path "GameObject/UI (Canvas)/Button - TextMeshPro" --project "$P" --json
```

When a custom script is required, run C# with `execute --code`. `using` directives are allowed:

```bash
ucli execute --force --project "$P" --code '
using UnityEngine.UI;
// code body
'
```

## How to verify

### Logs
Use `read-console` for `Debug.Log` output and errors. `--type error` includes Exception and Assert as well as Error, and the entry `type` stays the original name.

```bash
ucli read-console --type log --limit 5 --no-stacktrace --project "$P" --json
ucli read-console --type error --limit 5 --no-stacktrace --project "$P" --json
```

### Visual check (screenshot)
Capture the Game View with `screenshot --view game` and look at the visual change.

```bash
# The default is already jpg q75 scaled to 1024px, so a bare capture is enough
ucli screenshot --view game --path /tmp/qa-check.jpg --project "$P" --json
# Only when a lossless original is required
ucli screenshot --view game --format png --max-width 0 --path /tmp/qa-check.png --project "$P" --json
# Then inspect the image with the Read tool
```

Coordinate input and the conversion rules are in `Coordinate-Based UI Interaction` below.

**Limit**: in Play Mode the capture uses `ScreenCapture.CaptureScreenshotAsTexture()`, so a ScreenSpaceOverlay Canvas (UGUI) is included. Outside Play Mode the fallback is `Camera.Render()`, which does not include a ScreenSpaceOverlay Canvas.

Verify **logs first, and treat the screenshot as supporting evidence**. The log is the primary proof that an event fired. The screenshot confirms visual state.

## Coordinate-Based UI Interaction

### When to use `qa ui-dump`, `qa click`, or `qa tap`
- **`qa ui-dump`**: Use first when you need clickable UI candidates with path, visible text, and image-space rect/center.
- **`qa click --target <path>`**: Use when you know the GameObject path from `qa ui-dump` or `scene inspect`. 100% precise, no coordinate guessing.
- **`qa tap --x <X> --y <Y>`**: Use when you only have image coordinates, such as `centerX`/`centerY` from `qa ui-dump` or pixels read from a screenshot.

### qa ui-dump Workflow
1. Enter Play Mode and, if you already captured a reference image, keep its `width`/`height` available
2. Dump candidates: `qa ui-dump --limit 30 --interactable-only --omit-rect --output compact` -> inspect each element's `path`, `text`, `interactable`, `centerX`, `centerY`
3. Prefer path click: `qa click --target <path>`
4. If a coordinate tap is better, use `qa tap --x <centerX> --y <centerY>`

Returned `path` values are reliable when unique; if same-named siblings share a path, use `centerX`/`centerY` with `qa tap` instead.

If the dump should match a resized image, pass `--screenshot-width` and `--screenshot-height` together. Otherwise, the bridge uses the last successful `screenshot` dimensions when available.

If the visible label is known, add `--text <substring>` so filtering happens in the Editor before serialization. Omit `--omit-rect` only when you need `x`/`y`/`width`/`height`; `centerX`/`centerY` remain available either way.

### Non-UI world objects (`qa world-dump`)

UI elements come from `qa ui-dump`. For non-UI world objects (units on a battle grid, world-space interactables), the game opts them in:

- Implement `UnityCliBridge.Bridge.IQaTappable` on your own component, or attach the `QaTappable` marker component (set `label`, optional `anchor`, optional `onQaTap`).
- `qa world-dump --limit 30 --output compact` then lists them with `label`, `path`, image-space `centerX`/`centerY`, `onScreen`, `hasAction`; add `--text <substring>` when the label is known.
- Tap with `qa tap --target <path>`. If the object's `TryQaTap()` handles it (e.g. `onQaTap` is wired), the bridge invokes it directly; otherwise the bridge simulates an Input System tap at the anchor, which reaches games that poll the Input System directly (where `qa tap --x --y`'s EventSystem path does not).
- Off-screen objects are hidden unless you pass `--include-offscreen`.
- Same-named sibling world objects share a path and resolve to the first match; give tappable objects unique names/labels to target them individually.

### Conditional multi-step actions (`qa run-sequence`)

Use `qa run-sequence` when a test needs to wait for state, press or tap, wait again, and keep going without a command round trip between every step. The command is deferred in the bridge, so it requires Play Mode and returns only when the sequence completes, times out, or errors.

```bash
ucli qa run-sequence --spec-json @seq-ok.json --timeout 60000 --project "$P" --json
ucli qa run-sequence --spec-json @seq-ok.json --record --record-path /tmp/qa-seq.mp4 --timeout 60000 --project "$P" --json
```

`--record` captures only the interval the sequence runs, as mp4 through Unity Recorder (`com.unity.recorder` required). The completion or timeout response puts the final path in `recordingPath`. Omit `--record-path` and the file is written under `Library/com.yhc509.unity-cli-bridge/recordings/`.

`seq-ok.json`:

```json
{
  "steps": [
    {
      "name": "move",
      "wait": [
        { "target": "/Battle/Erich", "transform": "position", "op": "near", "value": [3, 0, 3], "epsilon": 0.2 }
      ],
      "timeoutMs": 5000,
      "actions": [
        { "key": "space" }
      ]
    },
    {
      "name": "ready",
      "wait": [
        { "target": "/State", "query": "phase", "op": "==", "value": "Ready" }
      ],
      "actions": [
        { "tap": { "target": "/Battle/Erich" } }
      ]
    }
  ]
}
```

Condition keys:

| Kind | Spec shape | Notes |
|------|------------|-------|
| `active` | `{ "target": "/X", "active": true }` | Waits for active hierarchy resolve. |
| `gone` | `{ "target": "/Spinner", "gone": true }` | Waits until the target can no longer be resolved. |
| `transform` | `{ "target": "/Unit", "transform": "position", "op": "near", "value": [0,0,0] }` | `position`, `rotation`, or `scale`; vector value. |
| `scene` | `{ "scene": "GameScene" }` | Active scene name. |
| `log` | `{ "log": "Game started" }` | Recent console log contains text. |
| `interactable` | `{ "target": "/Start", "interactable": true }` | Same effective interactable check as `qa wait-until`. |
| `query` | `{ "target": "/State", "query": "phase", "op": "==", "value": "Ready" }` | Reads game state from `IQaQueryable`. |

`transform` and `query` support `==`, `!=`, `>=`, `<=`, `near`, and `changed`. `near` uses `epsilon` when provided, otherwise the bridge default. `changed` captures a baseline when the step starts.

Action keys:

| Kind | Spec shape |
|------|------------|
| `key` | `{ "key": "space" }` |
| `tap` | `{ "tap": { "x": 400, "y": 300 } }` or `{ "tap": { "target": "/Unit" } }` |
| `swipe` | `{ "swipe": { "from": [100,200], "to": [300,400], "durationMs": 500 } }` |
| `wait` | `{ "wait": 250 }` |

On timeout the response payload still uses a success envelope, with `status: "TimedOut"`. Read:

- `completedSteps`: how far the sequence got
- `failedStep.index` and `failedStep.name`: where it stopped
- `failedStep.unmet`: each unmet condition, or a pending action when the step timed out during actions, with `expected` and `actual`
- `failedStep.stateSnapshot`: current values for that step's conditions

Expose query state by implementing `IQaQueryable` on a component:

```csharp
using UnityCliBridge.Bridge;
using UnityEngine;

public sealed class QaBattleState : MonoBehaviour, IQaQueryable
{
    public string phase = "Ready";
    public int hp = 10;

    public bool TryQaQuery(string key, out object? value)
    {
        switch (key)
        {
            case "phase":
                value = phase;
                return true;
            case "hp":
                value = hp;
                return true;
            default:
                value = null;
                return false;
        }
    }
}
```

If a `query` target has no `IQaQueryable` or the key is unsupported, timeout diagnostics include an `actual` value like `<no IQaQueryable: phase>`.

### qa tap Workflow
1. Take a screenshot: `screenshot` -> note `width`, `height`, and when needed `screenWidth`, `screenHeight`, `imageOrigin`, `coordinateOrigin` from the response
2. Analyze the screenshot image to find the target pixel coordinates `(X, Y)` with `Y=0` at the top-left
3. Tap: `qa tap --x <X> --y <Y>` -> pass the screenshot image coordinates directly; the bridge automatically reuses the last screenshot dimensions for coordinate conversion
4. Verify: `read-console` to check whether the expected log or event fired

The bridge automatically:
- Accepts screenshot image coordinates as-is, so you do not pre-flip Y or pre-scale for DPI/resolution differences
- Inverts the Y axis (screenshot top-origin -> Unity bottom-origin)
- Scales coordinates if the screenshot resolution differs from `Screen.width` / `Screen.height` (common in the Editor because of DPI)
- Resets cached screenshot dimensions when Play Mode enters or exits, so stale sizes from a previous session are not reused when domain reload is disabled

### Explicit screenshot dimensions
If you need to specify different dimensions, for example when the screenshot was resized. If you omit them, the bridge uses the last successful `screenshot` size automatically:

```bash
ucli qa tap --x 480 --y 225 --screenshot-width 961 --screenshot-height 554 --project "$P" --json
```

### Precise coordinate lookup fallback via execute
Prefer `qa ui-dump` for clickable UI. When screenshot coordinates are imprecise and the target is not exposed by `IPointerClickHandler`, use `execute` to log exact UI element positions:

```bash
# Find a button's screen center coordinates
ucli execute --file coords.cs --force --project "$P" --json
```

Where `coords.cs` contains:

```csharp
using UnityEngine;

var btn = GameObject.Find("ButtonName");
var rt = btn.GetComponent<RectTransform>();
var corners = new Vector3[4];
rt.GetWorldCorners(corners);
Vector3 center = (corners[0] + corners[2]) * 0.5f;
// For ScreenSpaceOverlay canvas, world coords = screen coords
Debug.Log($"Screen center: {center.x},{center.y} Screen={Screen.width}x{Screen.height}");
```

Then reverse-calculate to screenshot coordinates:
- `imageX = screenX * screenshotWidth / Screen.width`
- `imageY = screenshotHeight - (screenY * screenshotHeight / Screen.height)`

### Coordinate system summary
| Space | Y=0 | Used by |
|-------|-----|---------|
| Screenshot/Image | Top | `qa tap` input coordinates |
| Unity Screen | Bottom | Internal Unity coordinates |

The bridge converts automatically when screenshot dimensions are known.

## Example: button click

```bash
P="$(pwd -P)"

ucli play --project "$P" --output compact
sleep 1

ucli qa click --target "/Canvas/Button" --project "$P" --output compact

# Log check
ucli read-console --type log --limit 5 --no-stacktrace --project "$P" --output compact

# Visual check — the default is already jpg q75 scaled to 1024px
ucli screenshot --view game --path /tmp/qa-click.jpg --project "$P" --output compact

ucli stop --project "$P" --output compact
```

## Example: wait for a condition, then verify

```bash
ucli play --project "$P" --json
sleep 1

# Wait until a specific scene is active (10 seconds max)
ucli qa wait-until --scene GameScene --timeout 10000 --project "$P" --json

# Simulate input after the transition
ucli qa click --target "/Canvas/StartButton" --project "$P" --json
ucli qa wait-until --log-contains "Game started" --timeout 5000 --project "$P" --json

ucli stop --project "$P" --json
```

Waiting on screen state instead of a fixed sleep avoids extra delays. Conditions passed together must all be true (AND). A button waits for both active and a real interactable state. A loading indicator waits until it disappears from the active hierarchy. `--object-interactable` prefers the target component's `IsInteractable()` result, then a public bool `interactable` property, and treats an object with neither as true once it resolves as active.

```bash
# Wait until the start button is clickable, then click
ucli qa wait-until --object-interactable StartButton --timeout 10000 --project "$P" --json
ucli qa click --qa-id StartButton --project "$P" --json

# Wait until the loading spinner is inactive or destroyed
ucli qa wait-until --object-gone LoadingSpinner --timeout 10000 --project "$P" --json
```

## Known limits

- **Coordinate basis**: `--from`/`--to` on `qa swipe --target` are pixel offsets from the target `RectTransform` center, not absolute screen coordinates.
- **Screenshot coordinate conversion**: `qa tap` and coordinate `qa swipe` take top-origin screenshot coordinates and convert them to bottom-origin Unity screen coordinates at execution time.
- **Screenshot**: in Play Mode a `ScreenCapture`-based capture includes an Overlay Canvas. Outside Play Mode the `Camera.Render()` fallback does not include a ScreenSpaceOverlay Canvas.
- **using limits**: `execute --code` extracts `using Namespace;`, `using static Namespace.Type;`, and `using Alias = Namespace.Type;`. A generic type alias such as `using Dict = Dictionary<string, int>;` is not supported.
- **Game View updates**: `play` sets `runInBackground = true` automatically, so the Game View keeps updating when Unity loses focus. `stop` restores the previous value.

## Troubleshooting

| Error code | Cause | Fix |
|-----------|------|------|
| `QA_NOT_PLAYING` | Not in Play Mode | Run `play` first |
| `QA_NO_EVENT_SYSTEM` | No EventSystem | Add one with `execute-menu` |
| `QA_TAP_NO_TARGET` | No clickable UI under the coordinate | Check the coordinate and resolution basis, and whether UI is actually drawn there |
| `QA_LEGACY_KEY_UNSUPPORTED` | `qa key` on the legacy Input Manager with no `IQaKeyHandler` | Use `qa tap`, implement `IQaKeyHandler`, or `package add --name com.unity.inputsystem` |
| `QA_LEGACY_BUTTON_UNSUPPORTED` | `--button right` on the legacy Input Manager | `package add --name com.unity.inputsystem` |
| `QA_TARGET_NOT_FOUND` | Hierarchy path is wrong | Confirm the path with `scene inspect` |
| `QA_TARGET_NOT_RECT_TRANSFORM` | The `qa swipe --target` object is not a UI `RectTransform` | Pass a UI hierarchy path |
| `QA_NO_POINTER_DEVICE` | No Mouse or Touchscreen | Check Input System devices |
| `QA_WAIT_TIMEOUT` | The condition was not met in time | Recheck the condition, or raise the timeout |
| `BUSY` | Unity is compiling or updating | Retry after compilation finishes |
