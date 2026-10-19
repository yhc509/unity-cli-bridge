#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using Unity.Profiling;
using UnityCli.Protocol;
using UnityEditor;
using UnityEngine;
// Unity.Profiling.Memory shipped in 2022.2. 2022.1 and 2021.3 only have the
// experimental types. On 2022.2 and later those experimental types are obsolete,
// and current Unity 6 makes them a compile error, so the alias cannot be shared.
#if UNITY_2022_2_OR_NEWER
using MemProf = Unity.Profiling.Memory;
#else
using MemProf = UnityEngine.Profiling.Memory.Experimental;
#endif

namespace UnityCliBridge.Bridge.Editor
{
    internal sealed partial class ProfileCommandHandler
    {
        // Counter names missing on the current Unity version resolve to recorder.Valid == false and are
        // reported through `unavailable` instead of failing the command. Prune dead names once measured live.
        private static readonly ProfileCounterSpec[] MemoryReportCounters =
        {
            new ProfileCounterSpec(ProfilerCategory.Memory, "Total Used Memory", "bytes"),
            new ProfileCounterSpec(ProfilerCategory.Memory, "Total Reserved Memory", "bytes"),
            new ProfileCounterSpec(ProfilerCategory.Memory, "System Used Memory", "bytes"),
            new ProfileCounterSpec(ProfilerCategory.Memory, "GC Used Memory", "bytes"),
            new ProfileCounterSpec(ProfilerCategory.Memory, "GC Reserved Memory", "bytes"),
            new ProfileCounterSpec(ProfilerCategory.Memory, "Gfx Used Memory", "bytes"),
            new ProfileCounterSpec(ProfilerCategory.Memory, "Gfx Reserved Memory", "bytes"),
            new ProfileCounterSpec(ProfilerCategory.Memory, "Audio Used Memory", "bytes"),
            new ProfileCounterSpec(ProfilerCategory.Memory, "Video Used Memory", "bytes"),
            new ProfileCounterSpec(ProfilerCategory.Memory, "Texture Count", "count"),
            new ProfileCounterSpec(ProfilerCategory.Memory, "Texture Memory", "bytes"),
            new ProfileCounterSpec(ProfilerCategory.Memory, "Mesh Count", "count"),
            new ProfileCounterSpec(ProfilerCategory.Memory, "Mesh Memory", "bytes"),
            new ProfileCounterSpec(ProfilerCategory.Memory, "Material Count", "count"),
            new ProfileCounterSpec(ProfilerCategory.Memory, "Material Memory", "bytes"),
            new ProfileCounterSpec(ProfilerCategory.Memory, "AnimationClip Count", "count"),
            new ProfileCounterSpec(ProfilerCategory.Memory, "AnimationClip Memory", "bytes"),
            new ProfileCounterSpec(ProfilerCategory.Memory, "Asset Count", "count"),
            new ProfileCounterSpec(ProfilerCategory.Memory, "Scene Object Count", "count"),
            new ProfileCounterSpec(ProfilerCategory.Memory, "Object Count", "count"),
        };

        private void StartMemoryDeferred(
            ProfileMemoryArgs args,
            TaskCompletionSource<ResponseEnvelope> completion,
            string projectHash,
            string requestId)
        {
            int frames = args.frames > 0
                ? Math.Min(args.frames, ProtocolConstants.MaxProfileStatsFrames)
                : ProtocolConstants.DefaultProfileMemoryFrames;

            var stopwatch = System.Diagnostics.Stopwatch.StartNew();
            var recorders = new List<(ProfileCounterSpec Spec, ProfilerRecorder Recorder)>();
            var unavailable = new List<string>();
            foreach (ProfileCounterSpec spec in MemoryReportCounters)
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
                            $"profile memory did not collect {frames} frames within {ProtocolConstants.ProfileStatsTimeoutSeconds} seconds.",
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
                            values.Add(sample.Value);
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
                    var payload = new ProfileMemoryPayload
                    {
                        reportId = Guid.NewGuid().ToString("N"),
                        mode = EditorApplication.isPlaying ? "playmode" : "editmode",
                        frames = frames,
                        unityVersion = Application.unityVersion,
                        capturedAtUtc = DateTime.UtcNow.ToString("O"),
                        counters = counters.ToArray(),
                        unavailable = unavailable.ToArray(),
                    };
                    WriteMemorySidecar(payload);
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

        private static void WriteMemorySidecar(ProfileMemoryPayload report)
        {
            string projectRoot = Path.GetFullPath(Path.Combine(Application.dataPath, ".."));
            string path = Path.Combine(
                projectRoot,
                ProtocolConstants.MemoryReportsDirectoryRelative.Replace('/', Path.DirectorySeparatorChar),
                report.reportId + ".json");
            var sidecar = new ProfileMemorySidecarFile
            {
                schemaVersion = 1,
                reportId = report.reportId,
                report = report,
            };
            AtomicFileUtility.WriteAllText(path, ProtocolJson.Serialize(sidecar));
            AtomicFileUtility.CleanupTempFiles(Path.GetDirectoryName(path)!);
        }

        // Guarded by _captureLock (defined in ProfileCommandHandler.Capture.cs) so that snapshot and
        // capture reject each other instead of both driving the profiler at once.
        private static bool _snapshotInFlight;

        private void StartSnapshotDeferred(
            TaskCompletionSource<ResponseEnvelope> completion,
            string projectHash,
            string requestId)
        {
            var stopwatch = System.Diagnostics.Stopwatch.StartNew();
            try
            {
                if (UnityEditor.PackageManager.PackageInfo.FindForAssetPath(
                        "Packages/com.unity.memoryprofiler/package.json") is null)
                {
                    throw new CommandFailureException(
                        ProtocolConstants.ErrorProfileFailed,
                        "The Memory Profiler package is not installed. Install it with `unity-cli package add --name com.unity.memoryprofiler`, then try again.");
                }

                lock (_captureLock)
                {
                    if (_phase != CapturePhase.Idle)
                    {
                        throw new CommandFailureException(
                            ProtocolConstants.ErrorProfileInProgress,
                            "A profile capture is in progress. Stop it before taking a snapshot.");
                    }

                    if (_snapshotInFlight)
                    {
                        throw new CommandFailureException(
                            ProtocolConstants.ErrorProfileInProgress,
                            "Another memory snapshot is in progress.");
                    }

                    _snapshotInFlight = true;
                }
            }
            catch (Exception exception)
            {
                completion.TrySetResult(CreateFailureResponse(requestId, projectHash, exception, stopwatch.ElapsedMilliseconds));
                return;
            }

            string snapshotId = Guid.NewGuid().ToString("N");
            string projectRoot = Path.GetFullPath(Path.Combine(Application.dataPath, ".."));
            string directory = Path.Combine(
                projectRoot,
                ProtocolConstants.SnapshotsDirectoryRelative.Replace('/', Path.DirectorySeparatorChar));
            string snapshotPath = Path.Combine(directory, snapshotId + ".snap");

            const MemProf.CaptureFlags flags =
                MemProf.CaptureFlags.ManagedObjects
                | MemProf.CaptureFlags.NativeObjects
                | MemProf.CaptureFlags.NativeAllocations;

            bool finished = false;

            void Finish(Func<ResponseEnvelope> build)
            {
                if (finished)
                {
                    return;
                }

                finished = true;
                lock (_captureLock)
                {
                    _snapshotInFlight = false;
                }

                completion.TrySetResult(build());
            }

            // Watchdog for a TakeSnapshot callback that never arrives.
            void Watchdog()
            {
                if (finished)
                {
                    EditorTickPump.Remove(Watchdog);
                    return;
                }

                if (stopwatch.Elapsed.TotalSeconds < ProtocolConstants.ProfileMemorySnapshotTimeoutSeconds)
                {
                    return;
                }

                EditorTickPump.Remove(Watchdog);
                Finish(() => ResponseEnvelope.Failure(
                    requestId,
                    projectHash,
                    ProtocolConstants.ErrorProfileTimeout,
                    $"Memory snapshot did not finish within {ProtocolConstants.ProfileMemorySnapshotTimeoutSeconds} seconds.",
                    true,
                    stopwatch.ElapsedMilliseconds,
                    ProtocolConstants.TransportLive));
            }

            EditorTickPump.Add(Watchdog);

            try
            {
                Directory.CreateDirectory(directory);
                MemProf.MemoryProfiler.TakeSnapshot(
                    snapshotPath,
                    (resultPath, success) =>
                    {
                        EditorTickPump.Remove(Watchdog);
                        if (!success)
                        {
                            Finish(() => ResponseEnvelope.Failure(
                                requestId,
                                projectHash,
                                ProtocolConstants.ErrorProfileFailed,
                                "MemoryProfiler.TakeSnapshot reported a failure.",
                                false,
                                stopwatch.ElapsedMilliseconds,
                                ProtocolConstants.TransportLive));
                            return;
                        }

                        long sizeBytes = 0;
                        try
                        {
                            sizeBytes = new FileInfo(resultPath).Length;
                        }
                        catch (Exception)
                        {
                            // A metadata collection failure does not undo a successful snapshot.
                        }

                        var payload = new ProfileMemorySnapshotPayload
                        {
                            snapshotId = snapshotId,
                            path = resultPath,
                            sizeBytes = sizeBytes,
                            captureFlags = flags.ToString(),
                            elapsedMs = stopwatch.ElapsedMilliseconds,
                            guidance = "Open this .snap file in the Memory Profiler package (Window > Analysis > Memory Profiler).",
                        };
                        Finish(() => CreateSuccessResponse(requestId, projectHash, payload, stopwatch.ElapsedMilliseconds));
                    },
                    flags);
            }
            catch (Exception exception)
            {
                EditorTickPump.Remove(Watchdog);
                Finish(() => CreateFailureResponse(requestId, projectHash, exception, stopwatch.ElapsedMilliseconds));
            }
        }
    }
}
