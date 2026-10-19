#nullable enable
using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Unity.Profiling;
using UnityCli.Protocol;
using UnityEditor;
using UnityEngine;

namespace UnityCliBridge.Bridge.Editor
{
    internal sealed partial class ProfileCommandHandler
    {
        public bool CanHandle(string command)
        {
            return string.Equals(command, ProtocolConstants.CommandProfileStats, StringComparison.Ordinal)
                || string.Equals(command, ProtocolConstants.CommandProfileCaptureStart, StringComparison.Ordinal)
                || string.Equals(command, ProtocolConstants.CommandProfileCaptureStop, StringComparison.Ordinal)
                || string.Equals(command, ProtocolConstants.CommandProfileStatus, StringComparison.Ordinal)
                || string.Equals(command, ProtocolConstants.CommandProfileMemory, StringComparison.Ordinal)
                || string.Equals(command, ProtocolConstants.CommandProfileMemorySnapshot, StringComparison.Ordinal);
        }

        // stats/memory wait N editor frames and snapshot runs on a TakeSnapshot callback, so they
        // must run deferred like qa wait-until.
        public bool IsDeferred(string command, string? argumentsJson = null)
        {
            return string.Equals(command, ProtocolConstants.CommandProfileStats, StringComparison.Ordinal)
                || string.Equals(command, ProtocolConstants.CommandProfileMemory, StringComparison.Ordinal)
                || string.Equals(command, ProtocolConstants.CommandProfileMemorySnapshot, StringComparison.Ordinal);
        }

        public string Handle(string command, string argumentsJson)
        {
            if (string.Equals(command, ProtocolConstants.CommandProfileStats, StringComparison.Ordinal)
                || string.Equals(command, ProtocolConstants.CommandProfileMemory, StringComparison.Ordinal)
                || string.Equals(command, ProtocolConstants.CommandProfileMemorySnapshot, StringComparison.Ordinal))
            {
                throw new InvalidOperationException("Deferred profile command must be started through StartDeferred: " + command);
            }

            if (string.Equals(command, ProtocolConstants.CommandProfileCaptureStart, StringComparison.Ordinal))
            {
                ProfileCaptureStartArgs args = ProtocolJson.Deserialize<ProfileCaptureStartArgs>(argumentsJson)
                    ?? new ProfileCaptureStartArgs();
                return StartCapture(args);
            }

            if (string.Equals(command, ProtocolConstants.CommandProfileCaptureStop, StringComparison.Ordinal))
            {
                return StopCapture();
            }

            if (string.Equals(command, ProtocolConstants.CommandProfileStatus, StringComparison.Ordinal))
            {
                ProfileStatusArgs args = ProtocolJson.Deserialize<ProfileStatusArgs>(argumentsJson)
                    ?? new ProfileStatusArgs();
                return HandleStatus(args);
            }

            throw new InvalidOperationException("Unhandled profile command: " + command);
        }

        public void StartDeferred(
            string command,
            string argumentsJson,
            TaskCompletionSource<ResponseEnvelope> completion,
            string projectHash)
        {
            if (completion.Task.IsCompleted)
            {
                return;
            }

            string requestId = GetRequestId(completion);
            if (string.Equals(command, ProtocolConstants.CommandProfileStats, StringComparison.Ordinal))
            {
                ProfileStatsArgs args = ProtocolJson.Deserialize<ProfileStatsArgs>(argumentsJson) ?? new ProfileStatsArgs();
                StartStatsDeferred(args, completion, projectHash, requestId);
                return;
            }

            if (string.Equals(command, ProtocolConstants.CommandProfileMemory, StringComparison.Ordinal))
            {
                ProfileMemoryArgs args = ProtocolJson.Deserialize<ProfileMemoryArgs>(argumentsJson) ?? new ProfileMemoryArgs();
                StartMemoryDeferred(args, completion, projectHash, requestId);
                return;
            }

            if (string.Equals(command, ProtocolConstants.CommandProfileMemorySnapshot, StringComparison.Ordinal))
            {
                StartSnapshotDeferred(completion, projectHash, requestId);
                return;
            }

            throw new InvalidOperationException("Unhandled deferred profile command: " + command);
        }

        private readonly struct ProfileCounterSpec
        {
            public ProfileCounterSpec(ProfilerCategory category, string name, string unit)
            {
                Category = category;
                Name = name;
                Unit = unit;
            }

            public ProfilerCategory Category { get; }
            public string Name { get; }
            public string Unit { get; }
        }

        private static readonly ProfileCounterSpec[] FrameCounters =
        {
            new ProfileCounterSpec(ProfilerCategory.Internal, "Main Thread", "ms"),
        };

        private static readonly ProfileCounterSpec[] RenderCounters =
        {
            new ProfileCounterSpec(ProfilerCategory.Render, "Draw Calls Count", "count"),
            new ProfileCounterSpec(ProfilerCategory.Render, "SetPass Calls Count", "count"),
            new ProfileCounterSpec(ProfilerCategory.Render, "Batches Count", "count"),
            new ProfileCounterSpec(ProfilerCategory.Render, "Triangles Count", "count"),
            new ProfileCounterSpec(ProfilerCategory.Render, "Vertices Count", "count"),
        };

        private static readonly ProfileCounterSpec[] GcCounters =
        {
            new ProfileCounterSpec(ProfilerCategory.Memory, "GC Allocated In Frame", "bytes"),
            new ProfileCounterSpec(ProfilerCategory.Memory, "GC Allocation In Frame Count", "count"),
            new ProfileCounterSpec(ProfilerCategory.Memory, "GC Used Memory", "bytes"),
            new ProfileCounterSpec(ProfilerCategory.Memory, "GC Reserved Memory", "bytes"),
        };

        private static readonly ProfileCounterSpec[] MemoryCounters =
        {
            new ProfileCounterSpec(ProfilerCategory.Memory, "Total Used Memory", "bytes"),
            new ProfileCounterSpec(ProfilerCategory.Memory, "System Used Memory", "bytes"),
            new ProfileCounterSpec(ProfilerCategory.Memory, "Texture Memory", "bytes"),
            new ProfileCounterSpec(ProfilerCategory.Memory, "Mesh Memory", "bytes"),
        };

        private static ProfileCounterSpec[] ResolvePreset(string preset)
        {
            switch (string.IsNullOrWhiteSpace(preset) ? "all" : preset.ToLowerInvariant())
            {
                case "frame":
                    return FrameCounters;
                case "render":
                    return RenderCounters;
                case "gc":
                    return GcCounters;
                case "memory":
                    return MemoryCounters;
                default:
                {
                    var all = new List<ProfileCounterSpec>();
                    all.AddRange(FrameCounters);
                    all.AddRange(RenderCounters);
                    all.AddRange(GcCounters);
                    all.AddRange(MemoryCounters);
                    return all.ToArray();
                }
            }
        }

        private void StartStatsDeferred(
            ProfileStatsArgs args,
            TaskCompletionSource<ResponseEnvelope> completion,
            string projectHash,
            string requestId)
        {
            int frames = args.frames > 0
                ? Math.Min(args.frames, ProtocolConstants.MaxProfileStatsFrames)
                : ProtocolConstants.DefaultProfileStatsFrames;
            string preset = string.IsNullOrWhiteSpace(args.preset) ? "all" : args.preset.ToLowerInvariant();
            ProfileCounterSpec[] specs = ResolvePreset(preset);

            var stopwatch = System.Diagnostics.Stopwatch.StartNew();
            var recorders = new List<(ProfileCounterSpec Spec, ProfilerRecorder Recorder)>();
            var unavailable = new List<string>();
            foreach (ProfileCounterSpec spec in specs)
            {
                ProfilerRecorder recorder = ProfilerRecorder.StartNew(spec.Category, spec.Name, frames);
                if (!recorder.Valid)
                {
                    unavailable.Add(spec.Name);
                    recorder.Dispose();
                    continue;
                }

                recorders.Add((spec, recorder));
            }

            int ticks = 0;

            void DisposeAll()
            {
                foreach ((_, ProfilerRecorder recorder) in recorders)
                {
                    recorder.Dispose();
                }
            }

            void Poll()
            {
                if (completion.Task.IsCompleted)
                {
                    EditorTickPump.Remove(Poll);
                    DisposeAll();
                    return;
                }

                try
                {
                    ticks++;
                    if (stopwatch.Elapsed.TotalSeconds >= ProtocolConstants.ProfileStatsTimeoutSeconds)
                    {
                        EditorTickPump.Remove(Poll);
                        DisposeAll();
                        completion.TrySetResult(ResponseEnvelope.Failure(
                            requestId,
                            projectHash,
                            ProtocolConstants.ErrorProfileTimeout,
                            $"profile stats did not collect {frames} frames within {ProtocolConstants.ProfileStatsTimeoutSeconds} seconds.",
                            true,
                            stopwatch.ElapsedMilliseconds,
                            ProtocolConstants.TransportLive));
                        return;
                    }

                    if (ticks < frames)
                    {
                        return;
                    }

                    EditorTickPump.Remove(Poll);
                    var counters = new List<ProfileCounterStat>();
                    var samples = new List<ProfilerRecorderSample>(frames);
                    foreach ((ProfileCounterSpec spec, ProfilerRecorder recorder) in recorders)
                    {
                        samples.Clear();
                        recorder.CopyTo(samples);
                        var values = new List<double>(samples.Count);
                        foreach (ProfilerRecorderSample sample in samples)
                        {
                            double value = sample.Value;
                            if (string.Equals(spec.Unit, "ms", StringComparison.Ordinal))
                            {
                                value /= 1_000_000.0; // ns -> ms
                            }

                            values.Add(value);
                        }

                        if (values.Count == 0)
                        {
                            unavailable.Add(spec.Name);
                            continue;
                        }

                        double[] array = values.ToArray();
                        double[] sorted = (double[])array.Clone();
                        Array.Sort(sorted);
                        counters.Add(new ProfileCounterStat
                        {
                            name = spec.Name,
                            category = spec.Category.Name,
                            unit = spec.Unit,
                            min = sorted[0],
                            median = ProfileStatMath.Median(array),
                            p95 = ProfileStatMath.Percentile(sorted, 95),
                            max = sorted[sorted.Length - 1],
                        });
                    }

                    DisposeAll();
                    var payload = new ProfileStatsPayload
                    {
                        frames = frames,
                        preset = preset,
                        mode = EditorApplication.isPlaying ? "playmode" : "editmode",
                        counters = counters.ToArray(),
                        unavailable = unavailable.ToArray(),
                    };
                    completion.TrySetResult(CreateSuccessResponse(requestId, projectHash, payload, stopwatch.ElapsedMilliseconds));
                }
                catch (Exception exception)
                {
                    EditorTickPump.Remove(Poll);
                    DisposeAll();
                    completion.TrySetResult(CreateFailureResponse(requestId, projectHash, exception, stopwatch.ElapsedMilliseconds));
                }
            }

            EditorTickPump.Add(Poll);
        }

        private static string GetRequestId(TaskCompletionSource<ResponseEnvelope> completion)
        {
            string? requestId = completion.Task.AsyncState as string;
            if (string.IsNullOrWhiteSpace(requestId))
            {
                throw new InvalidOperationException("Deferred profile request ID is missing.");
            }

            return requestId;
        }

        private static ResponseEnvelope CreateSuccessResponse(string requestId, string projectHash, object payload, long durationMs)
        {
            return ResponseEnvelope.Success(
                requestId,
                projectHash,
                ProtocolJson.Serialize(payload),
                durationMs,
                ProtocolConstants.TransportLive);
        }

        private static ResponseEnvelope CreateFailureResponse(string requestId, string projectHash, Exception exception, long durationMs)
        {
            if (exception is CommandFailureException failure)
            {
                return ResponseEnvelope.Failure(
                    requestId,
                    projectHash,
                    failure.ErrorCode,
                    failure.Message,
                    failure.IsRetryable,
                    durationMs,
                    ProtocolConstants.TransportLive,
                    failure.Details);
            }

            return ResponseEnvelope.Failure(
                requestId,
                projectHash,
                "COMMAND_FAILED",
                exception.Message,
                false,
                durationMs,
                ProtocolConstants.TransportLive,
                ProtocolErrorDetails.FromString(exception.ToString()));
        }
    }
}
