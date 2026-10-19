#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using UnityCli.Protocol;
using UnityEditor;
using UnityEditor.Profiling;
using UnityEditorInternal;
using UnityEngine;

namespace UnityCliBridge.Bridge.Editor
{
    internal sealed partial class ProfileCommandHandler
    {
        private enum CapturePhase
        {
            Idle,
            Capturing,
            Processing,
        }

        private sealed class MarkerAggregate
        {
            public double SelfTotalMs;
            public readonly List<double> SelfSamplesMs = new List<double>();
            public long GcBytes;
            public long Calls;
            public int Frames;
        }

        private static readonly object _captureLock = new object();
        private static CapturePhase _phase = CapturePhase.Idle;
        private static string? _captureId;
        private static string? _lastCaptureId;
        private static DateTime _startedAtUtc;
        private static int _requestedFrames;
        private static int _durationSeconds;
        private static float _budgetMs;
        private static bool _requestClamped;
        private static int _startFrameIndex;

        // walk state (Processing phase)
        private static int _walkFrame;
        private static int _walkLastFrame;
        private static bool _walkTruncatedHead;
        private static List<ProfileFrameEntry>? _walkFrames;
        private static Dictionary<string, MarkerAggregate>? _walkMarkers;
        private static List<double>? _walkGpuMs;
        private static readonly List<int> _childBuffer = new List<int>(256);

        internal static string StartForSequence()
        {
            string json = StartCapture(new ProfileCaptureStartArgs());
            ProfileCaptureStartedPayload? payload = ProtocolJson.Deserialize<ProfileCaptureStartedPayload>(json);
            return payload == null ? string.Empty : payload.captureId;
        }

        internal static string? StopForSequenceIfActive()
        {
            lock (_captureLock)
            {
                if (_phase != CapturePhase.Capturing)
                {
                    return null;
                }
            }

            string id = _captureId ?? string.Empty;
            BeginProcessing();
            return string.IsNullOrEmpty(id) ? null : id;
        }

        private static string StartCapture(ProfileCaptureStartArgs args)
        {
            if (!EditorApplication.isPlaying)
            {
                throw new CommandFailureException(
                    ProtocolConstants.ErrorProfileRequiresPlaymode,
                    "profile capture start requires Play Mode. Enter Play Mode first (e.g. `unity-cli play`).");
            }

            lock (_captureLock)
            {
                if (_phase != CapturePhase.Idle)
                {
                    throw new CommandFailureException(
                        ProtocolConstants.ErrorProfileInProgress,
                        "A profile capture is already in progress. Stop it with `profile capture stop` first.");
                }

                if (_snapshotInFlight)
                {
                    throw new CommandFailureException(
                        ProtocolConstants.ErrorProfileInProgress,
                        "A memory snapshot is in progress. Wait for it to finish before starting a capture.");
                }

                _phase = CapturePhase.Capturing;
            }

            try
            {
                int ringBufferFrames = ReadRingBufferFrameCount();
                _captureId = Guid.NewGuid().ToString("N");
                _budgetMs = args.budgetMs > 0 ? args.budgetMs : ProtocolConstants.DefaultProfileBudgetMs;
                _requestClamped = args.frames > ringBufferFrames;
                _requestedFrames = args.frames > 0 ? Math.Min(args.frames, ringBufferFrames) : 0;
                _durationSeconds = args.durationSeconds > 0
                    ? Math.Min(args.durationSeconds, ProtocolConstants.MaxProfileCaptureSeconds)
                    : 0;
                _startedAtUtc = DateTime.UtcNow;

                ProfilerDriver.ClearAllFrames();
                ProfilerDriver.profileEditor = false;
                ProfilerDriver.enabled = true;
                _startFrameIndex = Math.Max(0, ProfilerDriver.lastFrameIndex + 1);

                SessionState.SetString(ProtocolConstants.ProfileSessionKeyActiveId, _captureId);
                RegisterAutoStopPoll();

                return ProtocolJson.Serialize(new ProfileCaptureStartedPayload
                {
                    captureId = _captureId,
                    status = "STARTED",
                    startedAt = _startedAtUtc.ToString("O"),
                    requestedFrames = _requestedFrames,
                    durationSeconds = _durationSeconds,
                    budgetMs = _budgetMs,
                });
            }
            catch
            {
                ResetCaptureState();
                throw;
            }
        }

        private static string StopCapture()
        {
            lock (_captureLock)
            {
                if (_phase == CapturePhase.Idle)
                {
                    throw new CommandFailureException(
                        ProtocolConstants.ErrorProfileNotRunning,
                        "No profile capture is active. Start one with `profile capture start`.");
                }
            }

            if (_phase == CapturePhase.Capturing)
            {
                BeginProcessing();
            }

            return ProtocolJson.Serialize(new ProfileSummaryPayload
            {
                captureId = _captureId ?? string.Empty,
                status = "Processing",
                capturedFrames = CountCapturedFrames(),
                requestedFrames = _requestedFrames,
                budgetMs = _budgetMs,
            });
        }

        private static string HandleStatus(ProfileStatusArgs args)
        {
            string? id = string.IsNullOrWhiteSpace(args.captureId) ? (_captureId ?? _lastCaptureId) : args.captureId;

            if (_phase != CapturePhase.Idle
                && _captureId != null
                && (string.IsNullOrWhiteSpace(args.captureId) || string.Equals(args.captureId, _captureId, StringComparison.Ordinal)))
            {
                return ProtocolJson.Serialize(new ProfileSummaryPayload
                {
                    captureId = _captureId,
                    status = _phase == CapturePhase.Capturing ? "Capturing" : "Processing",
                    capturedFrames = CountCapturedFrames(),
                    requestedFrames = _requestedFrames,
                    budgetMs = _budgetMs,
                });
            }

            if (!string.IsNullOrWhiteSpace(id))
            {
                string sidecar = SidecarPath(id!);
                if (File.Exists(sidecar))
                {
                    ProfileSidecarFile? file = ProtocolJson.Deserialize<ProfileSidecarFile>(File.ReadAllText(sidecar));
                    if (file != null)
                    {
                        return ProtocolJson.Serialize(file.summary);
                    }
                }
            }

            return ProtocolJson.Serialize(new ProfileSummaryPayload
            {
                captureId = id ?? string.Empty,
                status = "NotFound",
            });
        }

        private static int CountCapturedFrames()
        {
            if (_phase == CapturePhase.Idle)
            {
                return 0;
            }

            return Math.Max(0, ProfilerDriver.lastFrameIndex - _startFrameIndex + 1);
        }

        private static void RegisterAutoStopPoll()
        {
            void Poll()
            {
                if (_phase != CapturePhase.Capturing)
                {
                    EditorTickPump.Remove(Poll);
                    return;
                }

                double elapsed = (DateTime.UtcNow - _startedAtUtc).TotalSeconds;
                int captured = CountCapturedFrames();
                bool framesHit = _requestedFrames > 0 && captured >= _requestedFrames;
                bool durationHit = _durationSeconds > 0 && elapsed >= _durationSeconds;
                bool safetyHit = elapsed >= ProtocolConstants.MaxProfileCaptureSeconds;
                bool playExited = !EditorApplication.isPlaying;
                if (framesHit || durationHit || safetyHit || playExited)
                {
                    EditorTickPump.Remove(Poll);
                    try
                    {
                        BeginProcessing();
                    }
                    catch (Exception ex)
                    {
                        Debug.LogError("[UCB] profile auto-stop failed: " + ex);
                        ResetCaptureState();
                    }
                }
            }

            EditorTickPump.Add(Poll);
        }

        private static void BeginProcessing()
        {
            lock (_captureLock)
            {
                if (_phase != CapturePhase.Capturing)
                {
                    return;
                }

                _phase = CapturePhase.Processing;
            }

            ProfilerDriver.enabled = false;

            int firstAvailable = Math.Max(_startFrameIndex, Math.Max(0, ProfilerDriver.firstFrameIndex));
            int last = ProfilerDriver.lastFrameIndex;
            if (_requestedFrames > 0 && last >= firstAvailable)
            {
                last = Math.Min(last, firstAvailable + _requestedFrames - 1);
            }

            _walkTruncatedHead = firstAvailable > _startFrameIndex;
            _walkFrame = firstAvailable;
            _walkLastFrame = last;
            _walkFrames = new List<ProfileFrameEntry>(Math.Max(0, last - firstAvailable + 1));
            _walkMarkers = new Dictionary<string, MarkerAggregate>(512, StringComparer.Ordinal);
            _walkGpuMs = new List<double>();

            EditorTickPump.Add(WalkStep);
        }

        private static void WalkStep()
        {
            try
            {
                int processed = 0;
                while (_walkFrame <= _walkLastFrame && processed < ProtocolConstants.ProfileWalkFramesPerTick)
                {
                    ProcessFrame(_walkFrame);
                    _walkFrame++;
                    processed++;
                }

                if (_walkFrame > _walkLastFrame)
                {
                    EditorTickPump.Remove(WalkStep);
                    FinishProcessing("Completed");
                }
            }
            catch (Exception ex)
            {
                EditorTickPump.Remove(WalkStep);
                Debug.LogError("[UCB] profile walk failed: " + ex);
                try
                {
                    FinishProcessing("Failed");
                }
                catch (Exception inner)
                {
                    Debug.LogError("[UCB] profile summary write failed: " + inner);
                    ResetCaptureState();
                }
            }
        }

        private static void ProcessFrame(int frameIndex)
        {
            using (HierarchyFrameDataView view = ProfilerDriver.GetHierarchyFrameDataView(
                frameIndex,
                0,
                HierarchyFrameDataView.ViewModes.MergeSamplesWithTheSameName,
                HierarchyFrameDataView.columnSelfTime,
                false))
            {
                if (view == null || !view.valid)
                {
                    return;
                }

                double frameMs = view.frameTimeMs;
                double gpuMs = view.frameGpuTimeMs;
                if (gpuMs > 0)
                {
                    _walkGpuMs!.Add(gpuMs);
                }

                var perFrame = new Dictionary<string, (double Self, long Gc, int Calls)>(256, StringComparer.Ordinal);
                var pending = new Stack<int>();
                _childBuffer.Clear();
                view.GetItemChildren(view.GetRootItemID(), _childBuffer);
                for (int index = 0; index < _childBuffer.Count; index++)
                {
                    pending.Push(_childBuffer[index]);
                }

                while (pending.Count > 0)
                {
                    int itemId = pending.Pop();
                    string name = view.GetItemName(itemId);
                    double self = view.GetItemColumnDataAsDouble(itemId, HierarchyFrameDataView.columnSelfTime);
                    long gc = (long)view.GetItemColumnDataAsDouble(itemId, HierarchyFrameDataView.columnGcMemory);
                    int calls = (int)view.GetItemColumnDataAsDouble(itemId, HierarchyFrameDataView.columnCalls);

                    perFrame.TryGetValue(name, out (double Self, long Gc, int Calls) agg);
                    perFrame[name] = (agg.Self + self, agg.Gc + gc, agg.Calls + calls);

                    _childBuffer.Clear();
                    view.GetItemChildren(itemId, _childBuffer);
                    for (int index = 0; index < _childBuffer.Count; index++)
                    {
                        pending.Push(_childBuffer[index]);
                    }
                }

                ProfileFrameMarker[] top = perFrame
                    .OrderByDescending(pair => pair.Value.Self)
                    .Take(ProtocolConstants.ProfileFrameTopMarkerCount)
                    .Select(pair => new ProfileFrameMarker
                    {
                        m = pair.Key,
                        self = pair.Value.Self,
                        gc = pair.Value.Gc,
                        calls = pair.Value.Calls,
                    })
                    .ToArray();

                _walkFrames!.Add(new ProfileFrameEntry
                {
                    i = frameIndex,
                    ms = frameMs,
                    gpuMs = gpuMs > 0 ? gpuMs : -1,
                    top = top,
                });

                foreach (KeyValuePair<string, (double Self, long Gc, int Calls)> pair in perFrame)
                {
                    if (!_walkMarkers!.TryGetValue(pair.Key, out MarkerAggregate aggregate))
                    {
                        aggregate = new MarkerAggregate();
                        _walkMarkers[pair.Key] = aggregate;
                    }

                    aggregate.SelfTotalMs += pair.Value.Self;
                    aggregate.SelfSamplesMs.Add(pair.Value.Self);
                    aggregate.GcBytes += pair.Value.Gc;
                    aggregate.Calls += pair.Value.Calls;
                    aggregate.Frames++;
                }
            }
        }

        private static void FinishProcessing(string status)
        {
            string captureId = _captureId ?? string.Empty;
            List<ProfileFrameEntry> frames = _walkFrames ?? new List<ProfileFrameEntry>();
            Dictionary<string, MarkerAggregate> markerMap = _walkMarkers ?? new Dictionary<string, MarkerAggregate>();
            List<double> gpuValues = _walkGpuMs ?? new List<double>();

            double[] frameMs = frames.Select(f => f.ms).ToArray();
            ProfileFrameTimeStats frameStats = ProfileStatMath.BuildFrameTimeStats(frameMs, _budgetMs);

            double gpuMedian = gpuValues.Count > 0 ? ProfileStatMath.Median(gpuValues.ToArray()) : 0.0;
            bool sawGpuWait = markerMap.ContainsKey(ProtocolConstants.GpuWaitMarkerName);
            ProfileVerdict verdict = ProfileStatMath.ComputeVerdict(
                gpuMedian, frameStats.medianMs, sawGpuWait, frameStats.overBudgetCount);

            // worstFrame in stats is an array index; map to the profiler frame index.
            if (frameStats.worstFrame >= 0 && frameStats.worstFrame < frames.Count)
            {
                frameStats.worstFrame = frames[frameStats.worstFrame].i;
            }

            ProfileSpike[] spikes = frames
                .Where(f => f.ms > _budgetMs)
                .OrderByDescending(f => f.ms)
                .Take(ProtocolConstants.DefaultProfileListLimit)
                .Select(f => new ProfileSpike
                {
                    frame = f.i,
                    ms = f.ms,
                    topMarker = f.top.Length > 0 ? f.top[0].m : string.Empty,
                    topMarkerSelfMs = f.top.Length > 0 ? f.top[0].self : 0,
                })
                .ToArray();

            ProfileMarkerEntry[] markers = markerMap
                .Select(pair =>
                {
                    double[] samples = pair.Value.SelfSamplesMs.ToArray();
                    double[] sorted = (double[])samples.Clone();
                    Array.Sort(sorted);
                    return new ProfileMarkerEntry
                    {
                        m = pair.Key,
                        selfTotalMs = pair.Value.SelfTotalMs,
                        selfMedianMs = ProfileStatMath.Median(samples),
                        selfP95Ms = ProfileStatMath.Percentile(sorted, 95),
                        gcBytes = pair.Value.GcBytes,
                        calls = pair.Value.Calls,
                        frames = pair.Value.Frames,
                    };
                })
                .OrderByDescending(entry => entry.selfTotalMs)
                .ToArray();

            ProfileHotspot[] hotspots = markers
                .Take(ProtocolConstants.DefaultProfileListLimit)
                .Select(entry => new ProfileHotspot
                {
                    marker = entry.m,
                    selfMedianMs = entry.selfMedianMs,
                    selfP95Ms = entry.selfP95Ms,
                    selfTotalMs = entry.selfTotalMs,
                    calls = entry.calls,
                })
                .ToArray();

            ProfileGcEntry[] gcTop = markers
                .Where(entry => entry.gcBytes > 0)
                .OrderByDescending(entry => entry.gcBytes)
                .Take(ProtocolConstants.DefaultProfileListLimit)
                .Select(entry => new ProfileGcEntry
                {
                    marker = entry.m,
                    bytesTotal = entry.gcBytes,
                    framesWithAlloc = entry.frames,
                })
                .ToArray();

            var summary = new ProfileSummaryPayload
            {
                captureId = captureId,
                status = status,
                capturedFrames = frames.Count,
                requestedFrames = _requestedFrames,
                truncated = _walkTruncatedHead || _requestClamped,
                mode = "playmode",
                unityVersion = Application.unityVersion,
                budgetMs = _budgetMs,
                frameTime = frameStats,
                verdict = verdict,
                spikes = spikes,
                hotspots = hotspots,
                gcTop = gcTop,
                sidecarPath = ProtocolConstants.ProfilesDirectoryRelative + "/" + captureId + ".json",
            };

            var sidecar = new ProfileSidecarFile
            {
                schemaVersion = 1,
                captureId = captureId,
                createdUtc = DateTime.UtcNow.ToString("O"),
                summary = summary,
                frames = frames.ToArray(),
                markers = markers,
            };

            WriteSidecar(captureId, sidecar);
            _lastCaptureId = captureId;
            ResetCaptureState();
        }

        private static void ResetCaptureState()
        {
            lock (_captureLock)
            {
                _phase = CapturePhase.Idle;
            }

            _captureId = null;
            _requestedFrames = 0;
            _durationSeconds = 0;
            _requestClamped = false;
            _walkFrames = null;
            _walkMarkers = null;
            _walkGpuMs = null;
            SessionState.EraseString(ProtocolConstants.ProfileSessionKeyActiveId);
        }

        private static string SidecarPath(string captureId)
        {
            string projectRoot = Path.GetFullPath(Path.Combine(Application.dataPath, ".."));
            string dir = Path.Combine(projectRoot, ProtocolConstants.ProfilesDirectoryRelative);
            return Path.Combine(dir, captureId + ".json");
        }

        private static void WriteSidecar(string captureId, ProfileSidecarFile sidecar)
        {
            string path = SidecarPath(captureId);
            AtomicFileUtility.WriteAllText(path, ProtocolJson.Serialize(sidecar));
            AtomicFileUtility.CleanupTempFiles(Path.GetDirectoryName(path)!);
        }

        private static int ReadRingBufferFrameCount()
        {
            try
            {
                Type? type = Type.GetType("UnityEditor.Profiling.ProfilerUserSettings,UnityEditor");
                PropertyInfo? property = type?.GetProperty(
                    "frameCount",
                    BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
                if (property?.GetValue(null) is int frameCount && frameCount > 0)
                {
                    return frameCount;
                }
            }
            catch
            {
                // fall through to the fallback below
            }

            return ProtocolConstants.ProfileRingBufferFallbackFrames;
        }

        [InitializeOnLoadMethod]
        private static void RestoreFromSessionOnLoad()
        {
            string activeId = SessionState.GetString(ProtocolConstants.ProfileSessionKeyActiveId, string.Empty);
            if (string.IsNullOrEmpty(activeId))
            {
                return;
            }

            SessionState.EraseString(ProtocolConstants.ProfileSessionKeyActiveId);
            // A domain reload wiped the capture state; leave a terminal sidecar so
            // `profile status <id>` reports Interrupted instead of NotFound.
            var sidecar = new ProfileSidecarFile
            {
                captureId = activeId,
                createdUtc = DateTime.UtcNow.ToString("O"),
                summary = new ProfileSummaryPayload
                {
                    captureId = activeId,
                    status = "Interrupted",
                },
            };
            try
            {
                WriteSidecar(activeId, sidecar);
            }
            catch (Exception ex)
            {
                Debug.LogError("[UCB] profile interrupted-sidecar write failed: " + ex);
            }
        }
    }
}
