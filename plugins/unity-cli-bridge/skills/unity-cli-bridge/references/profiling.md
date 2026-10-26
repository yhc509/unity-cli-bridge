# Profiling

How to **read** `profile` output. Command usage lives in SKILL.md's Profile Workflow. This page only answers "can I trust this number?"

## Which script is expensive

1. `unity-cli play`
2. `unity-cli profile capture start --frames 300 --wait` is one call. It returns when the 300 frames are captured, with `captureId`, `capturedFrames`, and `summary` (`hotspots`, `gcTop`, `gcByScript`, `spikes`). Do not add `sleep` or a separate `stop`.
3. Script markers look like `Assembly-CSharp.dll!::Type.Update() [Invoke]`. `unity-cli profile analyze <captureId> --marker Type.Update` resolves that short name (also `Type.Update()`). `unity-cli profile analyze <captureId>` prints the summary again.
4. Which script allocates: `summary.gcByScript`, or `unity-cli profile analyze <captureId> --gc`, adds up `GC.Alloc` bytes under the nearest script marker. `gcTop` leaves out the player-loop containers (`PlayerLoop`, `BehaviourUpdate`, ...), engine end-of-frame and PlayerLoop phase markers (`PlayerEndOfFrame`, `PostLateUpdate`, `Phase.Subsystem`), and `GC.Alloc` itself.
5. To measure one or a few scripts without a capture (for an A/B check), run `unity-cli profile stats --marker Type.Update --frames 120`. Each marker gets ms min/median/p95/max and `gcBytesPerFrame`. Repeat `--marker` for more scripts. Short names resolve the same way as in `analyze`.
6. Do not open the sidecar file. Hotspots live under `summary` there; `analyze` is the reader. One short name with several matches returns `PROFILE_MARKER_AMBIGUOUS` and the candidate names.

Pitfalls:

- `PROFILE_EMPTY` means the capture recorded 0 frames: `stop` came right after `play` or `start`, or the Editor was not ticking. Retry with `profile capture start --frames <n> --wait`.
- `profile capture stop` after a `--frames` capture already finished, and `profile capture stop --wait`, return the same shape: `captureId`, `status`, `capturedFrames`, and `summary`. The already-finished stop also sets `notice: PROFILE_ALREADY_COMPLETED`.
- `--frames` larger than the profiler buffer is clamped to that buffer. The capture records up to the buffer limit and can come back slightly short, because the profiler ring buffer rotates. Unity 2021.3 holds 300 frames (a clamped capture has come back with about 254–297) and Unity 6 holds 2000 (a clamped capture has come back with 1998). The running editor reports its own limit. `capturedFrames` is the number of frames actually kept, and the finished summary reports it next to `frameClamp`. `requestedFrames` is the clamped cap. `warning` and `frameClamp` (`requested`, `clamped`, `bufferLimit`) are included only when the request was clamped; an unclamped start, `--wait`, or `stop` omits both. Example: `Requested 100000 frames, clamped to 300 (profiler buffer limit 300).` A huge `--frames` with a short `--timeout` therefore completes instead of returning `PROFILE_WAIT_TIMEOUT`. To check that timeout, use `--duration <seconds> --wait --timeout <smaller>` (or a `--frames` count inside the buffer).
- `PROFILE_WAIT_TIMEOUT` means the Editor ran Play Mode too slowly for `--timeout`. The error names the `captureId` and the frames captured so far, and the capture keeps running. `profile capture stop --wait` returns what it has, or raise `--timeout`.
- In the Editor, `GC.GetAllocatedBytesForCurrentThread()` returns 0. Read `ProfilerRecorder` `"GC Allocated In Frame"`, `GC.GetTotalAllocatedBytes(true)`, `unity-cli profile stats --preset memory`, or `profile stats --marker <name>` (`gcBytesPerFrame`) instead. `GarbageCollector.GCMode` cannot be set in the Editor.
- A `ProfilerRecorder` you start yourself in `execute` reads 0 for a script marker unless the profiler is recording, and the short name `Type.Update` is not a marker name. `profile stats --marker` turns the profiler on for the measurement and resolves the name for you.

## Workflow loop

Diagnose performance with this loop:

1. Run `unity-cli qa run-sequence --spec-json <spec> --profile` to play the sequence and capture at the same time. The response `profileSummary` already contains frame-time percentiles, spike frames, hotspots, GC, and a CPU/GPU-bound verdict.
2. When you only need a capture, `unity-cli profile capture start --frames <n> --wait` returns the summary in one call. For an open-ended capture, start without `--frames` and end it with `unity-cli profile capture stop --wait`.
3. After the summary shows a problem (overBudget count, spike, verdict), drill in with `unity-cli profile analyze <captureId>` (the summary) or `--gc` / `--frame <n>` / `--marker <name>` / `--spikes`. A short `--marker` name such as `Type.Update` is resolved. There is no Editor round trip, so repeated reads are safe. Do not open the sidecar JSON; hotspots are under `summary`.
4. Fix the cause in code or assets, then run the same sequence with `--profile` again to make a new capture.
5. Compare before and after with `unity-cli profile compare <baseCaptureId> <headCaptureId>`. One response has the verdict (`regression` / `improvement` / `unchanged`), frame-time / overBudget / GC deltas, and per-marker regressions and improvements. Sensitivity is `--threshold <percent>` (default 5). List length is `--limit <n>`. This also reads local sidecars only, so it works with the Editor closed. When you read it:
   - An unfinished capture (stopped by a script recompile, for example) is not compared. It is rejected with `PROFILE_FAILED`. Recapture that side.
   - `deltaPercent` means something only when the same object's `deltaPercentAvailable` is `true`. If the baseline is 0 (for example the base had zero over-budget frames), a percent is undefined, so read the absolute `delta`.
   - A marker whose self time (`deltaMs`) is flat but whose GC allocation grew lands in `regressions`. One whose allocation shrank lands in `improvements`.
   - A budget, Unity version, or frame-count mismatch is a warning in `notes`. Do not trust the result as-is in that case.
6. For a quick counter sample in Edit Mode, use a preset: `unity-cli profile stats --frames 30 --preset memory` (`frame` / `render` / `gc` / `memory` / `all`). For named markers, use `unity-cli profile stats --marker <name> [--marker <name>...] --frames <n>` instead of a preset. Each result has a `path`: `recorder` means `ProfilerRecorder` timed the marker; `capture` means the time was read from the profiler frame hierarchy because the recorder had no samples. GC bytes always come from the frame hierarchy.

When a memory leak is the suspect, use the memory loop instead of the frame-time loop:

1. Create a baseline reportId with `unity-cli profile memory`. In Play Mode, advance a known number of frames with `unity-cli profile capture start --frames <n> --wait`, then run `profile memory` again for the head reportId. Both reports must be the **same mode** (both playmode, or both editmode). Do not `sleep` between them. The count recipe, including Texture2D via `profile memory top`, is in section 11.
2. Compare with `unity-cli profile memory compare <baseReportId> <headReportId>`. The verdict uses the `Total Used Memory` median (`--threshold`, default 5%), and `increases` says which counters grew. This works with the Editor closed.
3. An asset type whose Count and Memory rise together (for example `Texture Count` plus `Texture Memory`) is the first leak candidate. A rise in `GC Used Memory` means managed objects are still alive. A rise in `GC Reserved Memory` alone is heap growth. Do not use `Total Reserved` or `System Used` to call a leak.
4. Name the objects with `unity-cli profile memory top` (`--type Texture2D|Mesh|AudioClip|Material|AnimationClip|...`, `--limit`, default 20, `--scope loaded|scene|assets`). `totals` is count and bytes per type. `top` is the largest objects, with asset path when there is one, plus width/height/format/mipCount for textures and vertexCount/subMeshCount for meshes. These sizes are Editor measurements and differ from player builds. Compare the same mode with the same mode.
5. Take a `.snap` with `unity-cli profile memory snapshot` only when the trend is bad. It needs `com.unity.memoryprofiler` (otherwise it is rejected with an install hint), it does not run at the same time as profile capture, and analysis happens in Unity's Memory Profiler window. The CLI does not parse `.snap`. The file can exceed 1 GB and is not deleted automatically, so delete it when you are done.
6. If `notes` warns about a mode, Unity version, or frame-count mismatch, do not trust the result as-is. If `deltaPercentAvailable` is false, ignore the percent and read the absolute `delta`.

For an unattended regression with the Editor launched by you, use `editor launch` (headless by default), then `play`, capture or memory, `stop`, `editor stop`, and a local `compare`. The full pipeline and the `LIVE_UNAVAILABLE` retry rule during a domain reload are in section 12 (`Headless regression pipeline`) below.

**Read the sections below before interpreting numbers.** In particular:
- These numbers were measured inside the Editor. Do not report absolute values as shipping performance. Use them for relative comparison only.
- If the top hotspot is `Semaphore.WaitForSignal`, `WaitForTargetFPS`, `EditorLoop`, or `Gfx.WaitFor*`, that is a wait or Editor overhead, not the cause. This is a common healthy result.
- `gpuMedianMs: -1` is an unmeasured sentinel, not "GPU 0 ms".
- A large `deltaPercent` is noise when `deltaMs` is negligible next to the frame budget. Unity has never published a noise threshold, so do not invent one.
- The `--budget-ms` default of 16.67 assumes PC 60 fps. For mobile, Unity's recommendation at 30 fps is 21.66 ms.
- Before suggesting an optimization, check the folklore table (section 10). Caching `Camera.main`, `foreach` boxing, and `GetComponent` GC are all wrong advice on current Unity.

## 0. The limit you need first — these numbers were measured inside the Editor

`profile capture` runs inside the Unity Editor process. Unity's own position:

> Profiling the Editor has a large effect on accuracy. **The Profiler window is effectively profiling itself recursively.**
> — Ultimate guide to profiling Unity games (Unity 6 edition)

So:

- **Do not report absolute values as shipping performance.** Do not conclude "this game cannot hold 60 fps" from this data.
- **Use it for relative comparison only.** Same project, same scenario, before and after a change. That is why `profile compare` is the center of this command family.
- The bias is always **upward**. The Editor does more work, not less. Unity has never published a number for **how much** it inflates the result. Do not invent a ratio such as "1.5×".
- Especially untrustworthy: **memory** (the Editor forces textures read/write, so they look larger), **fill rate** (the resolution is not the target device), **script cost** (the Editor is Mono; many builds are IL2CPP), and **VSync behavior** (the Editor does not use GPU VSync; it imitates it with `WaitForTargetFPS`).

The workflow Unity endorses is exactly where this tool sits: find the problem in a build, iterate quickly in the Editor, then confirm again in a build.

## 1. Markers to ignore — the most common misread

If the top hotspot or a spike's `topMarker` is in the list below, **it is not the cause.** It is idle time or Editor overhead.

When Unity's Highlights module computes "CPU Active Time", it **subtracts** the first three. By Unity's own definition, those are not work.

| Marker | What it actually is | How to read it |
|---|---|---|
| `WaitForTargetFPS` | Idle. Time left until the target frame rate | **Larger is better.** The frame fit the budget. If it sits under `Gfx.WaitForPresentOnGfxThread`, it is a GPU wait |
| `Gfx.WaitForPresentOnGfxThread` | Main thread waiting on the render thread | Do not call the frame GPU-bound from this marker alone. See section 2 |
| `Gfx.WaitForGfxCommandsFromMainThread` / `Gfx.WaitForCommands` | Render thread is idle | Signal of a **main-thread bottleneck** |
| `Semaphore.WaitForSignal` | Thread synchronization wait | Unity staff: "`…WaitFor…` samples are never the culprit. They only mean the slow part is somewhere else" |
| `Gfx.WaitForRenderThread` | Main thread waiting | The cost is on the render thread |
| `<API>.WaitForLastPresent` | Main thread waiting for the GPU flip | Idle |
| `EditorLoop` | **Editor cost. It does not exist in a build** | Subtract it from the frame budget. A spike caused by `EditorLoop` is an Editor artifact |
| `EditorOnly ...` prefix | Editor-only checks (prefab, consistency, and similar) | Safe to ignore. GC bytes attached here are Editor artifacts too |
| `Mono.JIT` | JIT compile on first call | The Editor is Mono. An IL2CPP build does not have this |
| `Profiler.CollectEditorStats` | The profiler's own cost | Ignore |

By contrast, **`GC.Collect` is real work.** It is actual pause time. Do not ignore it.

`Semaphore.WaitForSignal` ranking first is a **common healthy result** from this tool. In a measured idle-scene capture of the sample project it led with `selfTotalMs: 35.4`, and `EditorLoop` was second. Neither is game code.

## 2. Reading a CPU-bound / GPU-bound verdict

`verdict` fields:

| Field | Value | Meaning |
|---|---|---|
| `bound` | `cpu` / `gpu` / `withinBudget` / `unknown` | `withinBudget` means optimizing will not raise the frame rate. Stop |
| `basis` | Why the verdict was chosen | `none` means there was no GPU measurement, so the call is a marker-signal estimate |
| `gpuMedianMs` | A number, or `-1` | **`-1` is an unmeasured sentinel, not "GPU 0 ms".** Do not read it as zero |

When `gpuMedianMs` is `-1`, this capture alone cannot confirm GPU-bound. `bound` is a marker-based estimate.

Unity's own decision table (33.33 ms budget):

- CPU 25 / GPU 20 → CPU-bound but **inside budget**. "No problem. Optimizing will not raise the frame rate"
- CPU 40 / GPU 20 → CPU-bound. Optimizing the GPU will not help
- CPU 20 / GPU 40 → GPU-bound
- CPU 40 / GPU 40 → both

The rule: **the slowest chip or thread sets the performance.** Change only that one.

## 3. Noise floor — when not to trust a delta

**Unity has never published a threshold of the form "below this many ms is noise."** Do not invent one. The only published guardrails are two sentences from the Performance Testing package:

> Avoid measurements under 1 ms. They are more sensitive to an unstable environment.
> Treat a dataset as stable when its deviation is under 5%.

When reading `profile compare`:

- **A large `deltaPercent` is not automatically important.** Look at the absolute `deltaMs` first. A measured case ranked `RenderLoop.Sort` first among regressions at `deltaMs: 0.0000625` and `deltaPercent: 75.3`. That looks like a 75% regression and is 0.00006 ms. It does not matter.
- **Convert the change into a share of the frame budget.** On a 16.67 ms budget, a 0.1 ms change is 0.6%. That ratio is the decision.
- **Check the sample size.** A small `capturedFrames` makes the median unstable. Captures Unity uses in practice are 300 frames (10 seconds) to 2000 frames.
- If `notes` says the frame counts differ a lot, **do not trust the comparison.** Capture again.
- Instrumentation overhead **scales with marker count** (it is not a fixed cost). Code with many calls looks more expensive than it is.

## 4. Judge GC in bytes only

`profile` emits GC as bytes and never as milliseconds. Unity's own docs explain why:

> The `GC.Alloc` marker does not time a Begin/End pair. To cut overhead it records only a timestamp and the allocation size. **The duration the Profiler shows is an artificial value added so the sample is visible in the view.**

Do not rank by `GC.Alloc` self time. Ignore that number in other tools too.

The GC column is inclusive: a marker's bytes include everything allocated below it. That is why `PlayerLoop`, `UpdateScene`, `BehaviourUpdate`, and `Update.ScriptRunBehaviourUpdate` all report the frame's whole allocation. `gcTop` and `analyze --gc` leave those containers, engine end-of-frame and PlayerLoop phase markers (`PlayerEndOfFrame`, `PostLateUpdate`, `EarlyUpdate`, and `Phase.Subsystem` names such as `PostLateUpdate.FinishFrameRendering`), and `GC.Alloc` itself out. `gcByScript` assigns each `GC.Alloc` sample to the nearest script marker above it (`Type.Update() [Invoke]` or your own `ProfilerMarker`), so it names the allocating script directly. Those engine markers are not script ancestors, even when the profiler flags them as script code, so their bytes go to the nearest real script above them or to `gcUnattributedBytes`. `gcUnattributedBytes` is allocation with no script marker above it (engine or Editor work). A capture recorded before 0.6.1 has no `gcByScript`; `notes` says so, and a new capture fixes it.

Unity states the target clearly: **0 bytes per frame in the main game loop**, as close to zero as possible.

**A trap: an empty spike list does not mean GC is fine.** Incremental GC, Unity's default, spreads collection across frames and uses idle time at the end of a frame. Allocation pressure may never show up as a `GC.Collect` spike. **Bytes per frame is the leading indicator. `GC.Collect` milliseconds are a lagging indicator, and they may not appear at all.**

Incremental GC is not free either — write barriers add up to about 1 ms of scripting overhead per frame. If allocation is already 0, Incremental GC is unnecessary.

## 5. The `--budget-ms` default assumes PC 60 fps

The default `16.67` is 60 fps. **Do not leave it there for a mobile project.**

The only headroom figure Unity has published as a number:

> Leave about **35% idle time** to deal with thermal limits.

| Target | Naive budget | Mobile recommendation (35% headroom) |
|---|---|---|
| 30 fps | 33.33 ms | **21.66 ms** |
| 60 fps | 16.67 ms | **10.83 ms** |
| Quest 2 (72 fps) | 13.88 ms | — |

Unity itself says mobile 60 fps is hard to hit on many devices and roughly doubles battery use, which is why many mobile games target 30 fps.

`--budget-ms` drives `overBudgetFrames`, spike detection, and the `withinBudget` verdict. A wrong budget tilts the whole conclusion toward optimism. **If you do not know the project target, ask.**

## 6. Discard the first frames

Frames right after entering Play Mode are systematically slow. The causes are documented:

- Entering Play Mode / domain reload
- `Mono.JIT` — compile on a function's first call
- Shader variant compile and GPU upload (in the Editor, a `Library/ShaderCache` miss compiles the shader too)
- Scene and asset loading

Measured example: the only spike in a sample capture was frame 1 (34.6 ms), and the marker was `Semaphore.WaitForSignal`. That is startup cost, not a performance problem.

The Performance Testing package's default warmup is **80 ms or at least 3 frames**. That is the only citable precedent. Discarding more than that is a judgment; say that it is a judgment.

The first run right after a shader or material edit is not comparable to the next run. Warm up once before `profile compare`.

## 7. How to use `profile compare`

Follow Unity's A/B procedure:

1. **Pick a reproducible scenario.** Unity: "A scripted or repeatable manual playthrough is best — it minimizes random side effects." `qa run-sequence --profile` is the command for that.
2. Capture before the change
3. Change **one thing**
4. Capture after the change
5. `profile compare <before> <after>`

Read in this order:

| Field | What to check |
|---|---|
| `notes` | **Start here.** A non-empty note means the comparison is less trustworthy |
| `verdict` | Decision from median frame time |
| `deltaPercentAvailable` | `false` means the baseline was 0, so a percent is undefined. **Ignore `deltaPercent` and read `delta`** |
| `frameTimeWorstMs` | Median can improve while worst gets worse. Hitches come from worst |
| `gcBytesTotal` | A flat time with more allocation is still a regression |
| `regressions` | Judge `deltaMs` against the frame budget (section 3) |
| `truncated` | `true` means the list was cut. Raise `--limit` and run again |

**An unfinished capture is not compared.** A domain reload during capture leaves a sidecar with `status = "Interrupted"`, and `profile compare` rejects it with `PROFILE_FAILED`. That guard stops an interrupted capture from reading as a silent "100% improvement". Recapture when you see this error.

## 8. Causes and fixes, by marker

The table below includes **only markers documented in Unity's official marker reference.** Captures also show undocumented names (`RenderLoop.Sort`, `Render.Mesh`, the `Shadows` family, and others). **Do not assert what those mean.** There is no source.

| Marker | Meaning | What to do |
|---|---|---|
| `BehaviourUpdate` | Every `MonoBehaviour.Update` | If the cause is not visible, **do not guess — collect more data**. Unity's advice is "add a Profiler Marker or use deep profiling" |
| `Update.ScriptRunBehaviourUpdate` | The above, plus coroutines | Same |
| `PreLateUpdate.ScriptRunBehaviourLateUpdate` | Every `LateUpdate` | Same |
| `FixedBehaviourUpdate` | Every `FixedUpdate` | See the physics rows below |
| `Physics.Processing` / `Physics.Simulate` | Physics simulation wait/prepare | **A call count near 10 is a warning.** A heavy frame makes physics catch up by stepping several times — a death spiral. It may be a *symptom*, not a physics bug. Check `Maximum Allowed Timestep` (Project Settings > Time) and lower the fixed timestep rate |
| `Physics.ProcessReports` | Dispatch of callbacks such as `OnTriggerEnter` | The cost is in **your callback**, not the simulation |
| High `Physics.Processing` with almost no physics objects | Job stealing attributes other systems' work to physics | Easy to misread |
| `Camera.Render` (main thread) | Per-camera processing time | **Reduce the number of active cameras.** Adding a camera that draws nothing still costs. Unless you are doing split-screen, one camera should be active |
| `Camera.Render` (**render thread**) | — | **A CPU-bound signal.** Too much time is going to draw calls or texture transfers |
| `Canvas.BuildBatch` | UI rebatch | One Canvas has too many Canvas Renderers → **split the Canvas** |
| `Canvas.SendWillRenderCanvases` | UI layout and graphic rebuild | If there are many Layout components, replace them with RectTransform. If this runs every frame but is not a hotspot, dynamic and static elements share one Canvas → split them |
| `Text_OnPopulateMesh` | Text mesh generation | Turn Best Fit off. Unity Learn: "Best Fit on UI Text should generally never be used" |
| `Animators.*` / `Director.PrepareFrame` | Animation | Do not put several Animators on the same Transform hierarchy (they cannot run in parallel). A StateMachineBehaviour that implements `OnStateMachineEnter/Exit` is forced onto the main thread. Scale curves cost more than translation/rotation |
| `GC.Alloc` concentrated on one marker | Allocation source | Trace it with the Call Stacks toggle and the Calls dropdown (2019.3+, no deep profiling required) |
| `Resources.Load` during gameplay | Synchronous load | Move to Addressables. Resources stay in memory until unloaded |

**When GPU-bound, fix in this order** (Unity's three buckets):
1. **Fill-rate bound** — cut overdraw (overlapping transparent UI, particles, sprites), fragment shader cost, Dynamic Resolution
2. **Memory-bandwidth bound** — enable mipmaps, use a compressed format
3. **Vertex-processing bound** — triangle count, UV seams and hard edges, LOD

**When CPU-bound, fix in this order** (Unity's order): physics → script Update → GC allocation/collection → camera culling and rendering → draw-call batching → UI rebuild → animation

## 9. Counters (`profile stats`) have no absolute threshold

Unity has not published a "above this is a problem" number for draw calls, SetPass, batches, triangles, or memory. Figures such as "mobile draw calls under 200" are community practice. Do not state them as fact.

Unity draws a line in exactly two places: **GC allocation (target 0 bytes per frame)** and **the frame budget**.

Judge counters only as **change against this project's own baseline**.

| Metric | Meaning | Note |
|---|---|---|
| SetPass Calls | Shader-pass switches | **The most meaningful of the three.** Changing render state is the most expensive graphics-API operation |
| Draw Calls | Total draw calls issued | |
| Batches | Batches processed | |
| `CPU Total Frame Time` | Full time between frames | **Includes wait time.** Do not use it as a work metric |

Counters that exist only in an Editor capture: Texture/Mesh/Material/AnimationClip Count, and GC Allocated In Frame — they are absent from a release build. The Editor also forces textures read/write, so **texture memory is systematically inflated.**

## 10. Advice not to recommend (folklore Unity has contradicted)

These are widely repeated and **wrong on current Unity.** Do not put them in an optimization suggestion.

| Common advice | What is actually true |
|---|---|
| `Camera.main` searches by tag every time, so you must cache it | **Cached since 2019.4.9.** Official docs: "a small CPU overhead similar to a `GetComponent` call". It is not a tag search and not GC. Caching is still fine, but the reason is much weaker |
| `foreach` boxes the enumerator | **Fixed by the Unity 5.5 compiler change.** Arrays and concrete `List<T>` / `Dictionary<K,V>` are safe. Boxing remains only when you iterate a variable typed as `IEnumerable<T>` |
| `GetComponent<T>()` allocates | **It does not allocate in a build.** In the Editor, and only *when the component is missing*, it allocates a fake-null wrapper. `TryGetComponent` avoids that too |
| Reduce draw calls | **The focus is wrong on URP/HDRP.** Unity says turn the static batching and GPU Instancing checkboxes *off* and use the SRP Batcher plus GPU Resident Drawer — that cuts **state changes**, not draw calls. Built-in pipeline advice reverses here |
| Save draw calls with dynamic batching | Unity: "no longer recommended for most uses. The CPU overhead can exceed the draw-call overhead." Not supported in HDRP |
| Merge many `Update` methods into one manager | **No official Unity source.** It is only widely repeated. Unity's advice is "add markers or deep-profile", not a structural rewrite |
| `RaycastNonAlloc` is deprecated | **Only the 2D version.** 3D `Physics.RaycastNonAlloc` is still a recommended pattern in the Unity 6.3 manual |

Allocation causes that **are still valid**: string concatenation and interpolation, boxing (Unity's GC is not generational, so it hurts more), LINQ on a hot path, closures and lambdas that capture state, arrays returned by Unity APIs (a new copy every call), `new WaitForSeconds`, and `params` arrays.

## 11. Watching a memory-leak trend (`profile memory`)

Use this when you are looking at **memory**, not frame time. It is the light path: counter medians, no snapshot. It follows the order Unity staff recommend (counter trend first, snapshot only when needed).

To see whether Texture2D or object counts grow, sample twice in Play Mode and let the Editor advance by frames. Do not insert a fixed `sleep 10` (or any other fixed sleep) between the samples.

```bash
unity-cli play
unity-cli profile memory                          # baseline reportId
unity-cli profile memory top --type Texture2D     # totals.count and totals.bytes
unity-cli profile capture start --frames 300 --wait
unity-cli profile memory                          # head reportId, still Play Mode
unity-cli profile memory top --type Texture2D
unity-cli profile memory compare <baseReportId> <headReportId>
```

`play` already waits until Play Mode is entered. `profile capture start` runs only in Play Mode. With `--frames <n> --wait` it returns when that many frames have been captured (`--frames` or `--duration` is required with `--wait`). That captured stretch is the gap between the two samples: a frame count, not a wall-clock sleep. `--duration <seconds> --wait` is the time-based form of the same wait. Prefer `--frames` when the question is what changed after N frames. Each `profile memory` call also waits for its own sample window (`--frames`, default 30). That window is the sample, not the gap.

`profile memory` records `Texture Count`, `Object Count`, and `Scene Object Count`, plus the byte counters. `profile memory compare` diffs those two reports locally. It does not diff `profile memory top`. For Texture2D specifically, read `totals` on each `profile memory top --type Texture2D` response (`count` and `bytes`). `Texture Count` on the memory report is every texture, not Texture2D alone. `--type` also accepts Texture, RenderTexture, Cubemap, Mesh, AudioClip, Material, AnimationClip, Shader, and Sprite. `--scope` is `loaded` (default), `scene`, or `assets`.

Do not do this:

```bash
unity-cli profile memory top --type Texture2D
sleep 10
unity-cli profile memory top --type Texture2D
```

A fixed sleep does not advance a known number of frames, and it does not wait for the Editor. `profile memory snapshot` writes a `.snap` for the Memory Profiler window. It is rejected with `PROFILE_IN_PROGRESS` while a capture is running, and this recipe does not use it.

`profile memory top --limit 20` and `profile memory top --type Texture2D --scope assets` are the follow-up once `increases` names a type. Take both sides in the same mode.

How to read it:

- **The verdict is decided by the `Total Used Memory` median alone.** Growth past `--threshold` (default 5%) is `regression`; a drop of that size is `improvement`. The other counters explain *why*.
- In `increases`, look first at an **asset type whose Count and Memory rise together**. `Texture Count` plus `Texture Memory` means textures are not being released. Memory up with Count flat means individual assets got larger (resolution or format).
- A rise in `GC Used Memory` means managed objects are still alive. A rise in `GC Reserved Memory` alone can be heap growth and may not be a leak. Keep them apart.
- **Do not use `Total Reserved` or `System Used` to call a leak.** Reserved memory staying allocated is normal.
- Compare the same mode with the same mode. An editmode versus playmode comparison is meaningless because entering Play moves hundreds of megabytes by itself, and `notes` warns about the mode mismatch. Do not trust the result when that warning is present.
- When `deltaPercentAvailable` is false, ignore the percent and read the absolute `delta`. The verdict is fixed at `unchanged` in that case.
- The `unavailable` array is only counters that Unity version does not have. It is not a failure. If **`Total Used Memory` is in that array, the verdict is meaningless**, so judge from the individual counters. `profile memory` and `profile stats` also ask for App Resident/Committed Memory, AudioClip Count/Memory, Physics Used Memory, Profiler Used/Reserved Memory, and System Total Used Memory. `profile stats --preset render` also asks for Shadow Casters Count, Visible Skinned Meshes Count, Render Textures Count/Bytes, and Used Textures Count/Bytes. Any of those names the running editor does not expose lands in `unavailable`. Measured on 2021.3.45f2, the only ones in that list are App Resident Memory, App Committed Memory, and System Total Used Memory. AudioClip Count, AudioClip Memory, Physics Used Memory, and Visible Skinned Meshes Count are available on that editor. On 6000.3.10f1 every one of these counters is available.
- The Editor-measurement limit in section 0 applies here too. The Editor forces textures read/write, so texture memory is inflated — read the change, not the absolute value.
- `profile memory top` answers which objects hold those bytes. `totals` sums count and bytes per type after hidden and built-in objects are skipped. `top` is the largest remaining objects (default 20). Texture entries add width, height, format, and mipCount. Mesh entries add vertexCount and subMeshCount. The `note` field is the reminder that Editor sizes differ from player builds. `truncated` means the limit or the scan cap cut the list. `--scope scene` is non-persistent objects; `--scope assets` is persistent objects under `Assets/` or `Packages/`.

Move to the precise step only when the trend is bad:

```bash
unity-cli profile memory snapshot     # → .snap path
```

This needs `com.unity.memoryprofiler` (otherwise it is rejected with an install hint) and does not run at the same time as profile capture. Analyze in **Window > Analysis > Memory Profiler**. The CLI does not parse `.snap`. The file grows to about the size of Editor memory (over 1 GB is common) and is not deleted automatically, so delete it when you are done.

## 12. Headless regression pipeline

Everything below runs with no person in the loop — no GUI Editor and no focus required.

```bash
unity-cli editor launch --project <path>     # headless by default (the GPU stays available)
unity-cli play
unity-cli profile capture start --duration 10 --wait   # → head captureId + summary
unity-cli profile memory                     # → head reportId
unity-cli stop
unity-cli editor stop
# From here the work is local and needs no Editor:
unity-cli profile compare <baseCapture> <headCapture>
unity-cli profile memory compare <baseReport> <headReport>
```

Keep the captureId and reportId from a known-good run as the baseline. Sidecars stay under `Library/com.yhc509.unity-cli-bridge/` across Editor restarts.

Note: entering Play Mode and installing a package cause a domain reload, which drops the IPC socket briefly. If the next command fails with `LIVE_UNAVAILABLE` (retryable), wait a few seconds and retry — that means a reload is in progress, not a hard failure.

## 13. Sources

All Unity official (primary):

- [Best practices for profiling game performance](https://unity.com/how-to/best-practices-for-profiling-game-performance) — frame budget, CPU/GPU-bound table, artificial `GC.Alloc` duration, mobile 35% headroom
- [Profiler markers reference](https://docs.unity3d.com/6000.3/Documentation/Manual/profiler-markers.html) — per-marker definitions, Editor VSync imitation, render-thread split
- [Highlights Profiler module](https://docs.unity3d.com/6000.3/Documentation/Manual/ProfilerHighlights.html) — wait markers subtracted from CPU Active Time
- [Play mode / Edit mode samples](https://docs.unity3d.com/6000.3/Documentation/Manual/profiler-play-edit-samples.html) — `EditorLoop`, `EditorOnly` prefix
- [Profiler counters reference](https://docs.unity3d.com/6000.3/Documentation/Manual/profiler-counters-reference.html) — counter definitions and release-build availability
- [Incremental garbage collection](https://docs.unity3d.com/6000.3/Documentation/Manual/performance-incremental-garbage-collection.html)
- [Physics performance issues](https://docs.unity3d.com/Manual/physics-performance-issues.html) — call-count threshold of 10
- [Choose a method for optimizing draw calls](https://docs.unity3d.com/6000.0/Documentation/Manual/optimizing-draw-calls-choose-method.html) — batching advice by pipeline
- [Camera.main](https://docs.unity3d.com/ScriptReference/Camera-main.html) — cached
- [Performance Testing package](https://docs.unity3d.com/Packages/com.unity.test-framework.performance@3.1/manual/writing-tests.html) — 1 ms / 5% guardrails, default warmup
