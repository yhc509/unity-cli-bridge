#nullable enable
using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Unity.Profiling;
using Unity.Profiling.LowLevel;
using Unity.Profiling.LowLevel.Unsafe;
using UnityCli.Protocol;
using UnityEditor;
using UnityEditor.Profiling;
using UnityEditorInternal;

namespace UnityCliBridge.Bridge.Editor
{
    internal sealed partial class ProfileCommandHandler
    {
        private sealed class MarkerStatsTarget
        {
            public string Query = string.Empty;
            public string Marker = string.Empty;
            public bool ResolvedFromShortName;
            public ProfilerRecorder Recorder;
            public bool HasRecorder;
            public double[]? RecorderMs;
            public long[]? RecorderCalls;
            public double[] CaptureMs = Array.Empty<double>();
            public long[] CaptureCalls = Array.Empty<long>();
            public long[] CaptureGc = Array.Empty<long>();
        }

        private static bool _markerStatsInFlight;

        /// <summary>
        /// `profile stats --marker`. Script markers (`Type.Update() [Invoke]`) are only emitted while the profiler
        /// records, on 2021.3 and 6000.3 alike, so the profiler is switched on for the measurement and restored after.
        /// </summary>
        private void StartMarkerStatsDeferred(
            ProfileStatsArgs args,
            int frames,
            TaskCompletionSource<ResponseEnvelope> completion,
            string projectHash,
            string requestId)
        {
            var stopwatch = System.Diagnostics.Stopwatch.StartNew();
            lock (_captureLock)
            {
                if (_phase != CapturePhase.Idle || _snapshotInFlight || _markerStatsInFlight)
                {
                    completion.TrySetResult(ResponseEnvelope.Failure(
                        requestId,
                        projectHash,
                        ProtocolConstants.ErrorProfileInProgress,
                        "A profile capture, memory snapshot, or another `profile stats --marker` is running. Wait for it to finish first.",
                        true,
                        stopwatch.ElapsedMilliseconds,
                        ProtocolConstants.TransportLive));
                    return;
                }

                _markerStatsInFlight = true;
            }

            var session = new MarkerStatsSession(
                args.markers,
                Math.Min(frames, ReadRingBufferFrameCount()),
                completion,
                projectHash,
                requestId,
                stopwatch);
            session.Start();
        }

        /// <summary>
        /// One measurement. Phases: warm-up frames so script markers register, resolve names, ProfilerRecorder for
        /// N frames, then a walk of the same frames for GC bytes (and for time when the recorder saw nothing).
        /// </summary>
        private sealed class MarkerStatsSession
        {
            private readonly string[] _queries;
            private readonly int _frames;
            private readonly TaskCompletionSource<ResponseEnvelope> _completion;
            private readonly string _projectHash;
            private readonly string _requestId;
            private readonly System.Diagnostics.Stopwatch _stopwatch;
            private readonly List<MarkerStatsTarget> _targets = new List<MarkerStatsTarget>();
            private readonly bool _previousEnabled;
            private readonly bool _previousProfileEditor;
            private int _phase;
            private int _warmupStart;
            private int _measureStart;
            private int _walkFrame;
            private int _walkLast = -1;
            private double _framesReachedSeconds = -1;
            private bool _restored;

            public MarkerStatsSession(
                string[] queries,
                int frames,
                TaskCompletionSource<ResponseEnvelope> completion,
                string projectHash,
                string requestId,
                System.Diagnostics.Stopwatch stopwatch)
            {
                _queries = queries;
                _frames = frames;
                _completion = completion;
                _projectHash = projectHash;
                _requestId = requestId;
                _stopwatch = stopwatch;
                _previousEnabled = ProfilerDriver.enabled;
                _previousProfileEditor = ProfilerDriver.profileEditor;
            }

            public void Start()
            {
                try
                {
                    ProfilerDriver.profileEditor = !EditorApplication.isPlaying;
                    ProfilerDriver.enabled = true;
                    _warmupStart = ProfilerDriver.lastFrameIndex;
                    EditorTickPump.Add(Poll);
                }
                catch (Exception exception)
                {
                    Restore();
                    _completion.TrySetResult(CreateFailureResponse(_requestId, _projectHash, exception, _stopwatch.ElapsedMilliseconds));
                }
            }

            private const double RecorderFillGraceSeconds = 2.0;

            private bool RecordersFull()
            {
                foreach (MarkerStatsTarget target in _targets)
                {
                    if (target.HasRecorder && target.Recorder.Count < _frames)
                    {
                        return false;
                    }
                }

                return true;
            }

            private void Restore()
            {
                if (_restored)
                {
                    return;
                }

                _restored = true;
                foreach (MarkerStatsTarget target in _targets)
                {
                    if (target.HasRecorder)
                    {
                        target.Recorder.Dispose();
                        target.HasRecorder = false;
                    }
                }

                ProfilerDriver.enabled = _previousEnabled;
                ProfilerDriver.profileEditor = _previousProfileEditor;
                lock (_captureLock)
                {
                    _markerStatsInFlight = false;
                }
            }

            private void Finish(ResponseEnvelope response)
            {
                EditorTickPump.Remove(Poll);
                Restore();
                _completion.TrySetResult(response);
            }

            private void Poll()
            {
                if (_completion.Task.IsCompleted)
                {
                    EditorTickPump.Remove(Poll);
                    Restore();
                    return;
                }

                try
                {
                    Step();
                }
                catch (Exception exception)
                {
                    Finish(CreateFailureResponse(_requestId, _projectHash, exception, _stopwatch.ElapsedMilliseconds));
                }
            }

            private void Step()
            {
                if (_stopwatch.Elapsed.TotalSeconds >= ProtocolConstants.ProfileStatsTimeoutSeconds)
                {
                    Finish(ResponseEnvelope.Failure(
                        _requestId,
                        _projectHash,
                        ProtocolConstants.ErrorProfileTimeout,
                        $"profile stats --marker did not collect {_frames} frames within {ProtocolConstants.ProfileStatsTimeoutSeconds} seconds.",
                        true,
                        _stopwatch.ElapsedMilliseconds,
                        ProtocolConstants.TransportLive));
                    return;
                }

                int last = ProfilerDriver.lastFrameIndex;
                if (_phase == 0)
                {
                    if (last - _warmupStart < ProtocolConstants.ProfileMarkerStatsWarmupFrames)
                    {
                        return;
                    }

                    ResponseEnvelope? failure = ResolveMarkerTargets(
                        _queries, _targets, _frames, _requestId, _projectHash, _stopwatch.ElapsedMilliseconds);
                    if (failure != null)
                    {
                        Finish(failure);
                        return;
                    }

                    _measureStart = ProfilerDriver.lastFrameIndex + 1;
                    _phase = 1;
                    return;
                }

                if (_phase == 1)
                {
                    if (last - _measureStart + 1 < _frames)
                    {
                        return;
                    }

                    // Recorder samples arrive a few frames after the profiler frame index moves on, so give the
                    // recorders a bounded moment to fill before reading them.
                    double now = _stopwatch.Elapsed.TotalSeconds;
                    if (_framesReachedSeconds < 0)
                    {
                        _framesReachedSeconds = now;
                    }

                    if (!RecordersFull() && now - _framesReachedSeconds < RecorderFillGraceSeconds)
                    {
                        return;
                    }

                    foreach (MarkerStatsTarget target in _targets)
                    {
                        ReadRecorder(target, _frames);
                        target.CaptureMs = new double[_frames];
                        target.CaptureCalls = new long[_frames];
                        target.CaptureGc = new long[_frames];
                    }

                    _walkFrame = Math.Max(_measureStart, ProfilerDriver.firstFrameIndex);
                    _walkLast = _measureStart + _frames - 1;
                    _phase = 2;
                    return;
                }

                int processed = 0;
                while (_walkFrame <= _walkLast && processed < ProtocolConstants.ProfileWalkFramesPerTick)
                {
                    WalkMarkerFrame(_walkFrame, _walkFrame - _measureStart, _targets);
                    _walkFrame++;
                    processed++;
                }

                if (_walkFrame <= _walkLast)
                {
                    return;
                }

                var stats = new ProfileMarkerStat[_targets.Count];
                for (int index = 0; index < _targets.Count; index++)
                {
                    MarkerStatsTarget target = _targets[index];
                    stats[index] = ProfileStatMath.BuildMarkerStat(
                        target.Query,
                        target.Marker,
                        target.ResolvedFromShortName,
                        target.RecorderMs,
                        target.RecorderCalls,
                        target.CaptureMs,
                        target.CaptureCalls,
                        target.CaptureGc);
                }

                var payload = new ProfileStatsPayload
                {
                    frames = _frames,
                    preset = "markers",
                    mode = EditorApplication.isPlaying ? "playmode" : "editmode",
                    markerStats = stats,
                };
                Finish(CreateSuccessResponse(_requestId, _projectHash, payload, _stopwatch.ElapsedMilliseconds));
            }
        }

        /// <summary>
        /// Resolves every query against the markers the Editor has registered, with the same short-name rules as
        /// `profile analyze --marker`, and starts one ProfilerRecorder per marker. Returns a failure envelope on the
        /// first ambiguous or unknown name.
        /// </summary>
        private static ResponseEnvelope? ResolveMarkerTargets(
            string[] queries,
            List<MarkerStatsTarget> targets,
            int frames,
            string requestId,
            string projectHash,
            long elapsedMs)
        {
            var handles = new List<ProfilerRecorderHandle>();
            ProfilerRecorderHandle.GetAvailable(handles);
            var categories = new Dictionary<string, ProfilerCategory>(handles.Count, StringComparer.Ordinal);
            foreach (ProfilerRecorderHandle handle in handles)
            {
                ProfilerRecorderDescription description = ProfilerRecorderHandle.GetDescription(handle);
                if ((description.Flags & MarkerFlags.Counter) != 0 || string.IsNullOrEmpty(description.Name))
                {
                    continue;
                }

                if (!categories.ContainsKey(description.Name))
                {
                    categories[description.Name] = description.Category;
                }
            }

            foreach (string query in queries)
            {
                ProfileMarkerResolution resolution = ProfileMarkerNameResolver.Resolve(categories.Keys, query);
                if (!resolution.IsMatch)
                {
                    string message = resolution.BuildMessage("registered profiler marker", "the Editor's registered profiler markers");
                    if (resolution.Kind == ProfileMarkerMatchKind.NotFound)
                    {
                        message += "\nA script marker exists only after its method has run while the profiler records. Enter Play Mode (`unity-cli play`) so Update methods run, then retry.";
                    }

                    return ResponseEnvelope.Failure(
                        requestId,
                        projectHash,
                        resolution.ErrorCode,
                        message,
                        false,
                        elapsedMs,
                        ProtocolConstants.TransportLive);
                }

                var target = new MarkerStatsTarget
                {
                    Query = query,
                    Marker = resolution.Marker!,
                    ResolvedFromShortName = resolution.Kind == ProfileMarkerMatchKind.Resolved,
                };
                ProfilerRecorder recorder = ProfilerRecorder.StartNew(categories[target.Marker], target.Marker, frames);
                if (recorder.Valid)
                {
                    target.Recorder = recorder;
                    target.HasRecorder = true;
                }
                else
                {
                    recorder.Dispose();
                }

                targets.Add(target);
            }

            return null;
        }

        private static void ReadRecorder(MarkerStatsTarget target, int frames)
        {
            if (!target.HasRecorder)
            {
                return;
            }

            ProfilerRecorder recorder = target.Recorder;
            int count = Math.Min(recorder.Count, frames);
            int first = recorder.Count - count;
            target.RecorderMs = new double[count];
            target.RecorderCalls = new long[count];
            for (int index = 0; index < count; index++)
            {
                ProfilerRecorderSample sample = recorder.GetSample(first + index);
                target.RecorderMs[index] = sample.Value / 1_000_000.0; // ns -> ms
                target.RecorderCalls[index] = sample.Count;
            }

            recorder.Dispose();
            target.HasRecorder = false;
        }

        private static void WalkMarkerFrame(int frameIndex, int slot, List<MarkerStatsTarget> targets)
        {
            if (slot < 0)
            {
                return;
            }

            using (HierarchyFrameDataView view = ProfilerDriver.GetHierarchyFrameDataView(
                frameIndex,
                0,
                HierarchyFrameDataView.ViewModes.MergeSamplesWithTheSameName,
                HierarchyFrameDataView.columnTotalTime,
                false))
            {
                if (view == null || !view.valid)
                {
                    return;
                }

                ProfileHierarchyWalker.Walk(new HierarchyViewAdapter(view), (itemId, name, _) =>
                {
                    for (int index = 0; index < targets.Count; index++)
                    {
                        MarkerStatsTarget target = targets[index];
                        if (slot >= target.CaptureMs.Length
                            || !string.Equals(target.Marker, name, StringComparison.Ordinal))
                        {
                            continue;
                        }

                        target.CaptureMs[slot] += view.GetItemColumnDataAsDouble(itemId, HierarchyFrameDataView.columnTotalTime);
                        target.CaptureCalls[slot] += (long)view.GetItemColumnDataAsDouble(itemId, HierarchyFrameDataView.columnCalls);
                        target.CaptureGc[slot] += (long)view.GetItemColumnDataAsDouble(itemId, HierarchyFrameDataView.columnGcMemory);
                    }
                });
            }
        }
    }
}
