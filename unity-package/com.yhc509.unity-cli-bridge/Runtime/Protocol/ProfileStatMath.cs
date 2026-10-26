#nullable enable
using System;

namespace UnityCli.Protocol
{
    /// <summary>Pure statistics helpers for profile summaries. Editor- and .NET-safe.</summary>
    public static class ProfileStatMath
    {
        public static double Median(double[] values)
        {
            if (values == null || values.Length == 0)
            {
                return 0.0;
            }

            double[] sorted = (double[])values.Clone();
            Array.Sort(sorted);
            int mid = sorted.Length / 2;
            return sorted.Length % 2 == 1
                ? sorted[mid]
                : (sorted[mid - 1] + sorted[mid]) / 2.0;
        }

        /// <summary>Nearest-rank percentile. Input must be sorted ascending.</summary>
        public static double Percentile(double[] sortedAscending, double percentile)
        {
            if (sortedAscending == null || sortedAscending.Length == 0)
            {
                return 0.0;
            }

            int rank = (int)Math.Ceiling(percentile / 100.0 * sortedAscending.Length);
            int index = Math.Min(Math.Max(rank - 1, 0), sortedAscending.Length - 1);
            return sortedAscending[index];
        }

        public static ProfileFrameTimeStats BuildFrameTimeStats(double[] frameMs, float budgetMs)
        {
            var stats = new ProfileFrameTimeStats();
            if (frameMs == null || frameMs.Length == 0)
            {
                return stats;
            }

            double worst = double.MinValue;
            int worstFrame = -1;
            int overBudget = 0;
            for (int index = 0; index < frameMs.Length; index++)
            {
                if (frameMs[index] > worst)
                {
                    worst = frameMs[index];
                    worstFrame = index;
                }

                if (frameMs[index] > budgetMs)
                {
                    overBudget++;
                }
            }

            double[] sorted = (double[])frameMs.Clone();
            Array.Sort(sorted);

            stats.medianMs = Median(frameMs);
            stats.p95Ms = Percentile(sorted, 95);
            stats.worstMs = worst;
            stats.worstFrame = worstFrame;
            stats.overBudgetCount = overBudget;
            return stats;
        }

        public static ProfileVerdict ComputeVerdict(
            double gpuMedianMs,
            double cpuMedianMs,
            bool sawGpuWaitMarker,
            int overBudgetCount)
        {
            var verdict = new ProfileVerdict
            {
                gpuMedianMs = gpuMedianMs > 0 ? gpuMedianMs : -1,
            };

            if (gpuMedianMs > 0)
            {
                verdict.basis = "gpuTime";
            }
            else if (sawGpuWaitMarker)
            {
                verdict.basis = "waitMarkers";
            }
            else
            {
                verdict.basis = "none";
            }

            if (overBudgetCount == 0)
            {
                verdict.bound = "withinBudget";
                return verdict;
            }

            if (gpuMedianMs > 0)
            {
                verdict.bound = gpuMedianMs >= cpuMedianMs ? "gpu" : "cpu";
                return verdict;
            }

            verdict.bound = sawGpuWaitMarker ? "gpu" : "cpu";
            return verdict;
        }

        public const string MarkerPathRecorder = "recorder";
        public const string MarkerPathCapture = "capture";

        /// <summary>
        /// Builds one `profile stats --marker` result from per-frame series. Time comes from ProfilerRecorder when it
        /// recorded the marker over at least half of the window, otherwise from the captured frame hierarchy. GC always
        /// comes from the hierarchy, because ProfilerRecorder cannot attribute GC.Alloc bytes to a marker.
        /// </summary>

        /// <param name="recorderMs">Per-frame recorder time in ms, or null when no recorder could be started.</param>
        /// <param name="recorderCalls">Per-frame recorder sample counts, same length as <paramref name="recorderMs"/>.</param>
        public static ProfileMarkerStat BuildMarkerStat(
            string query,
            string marker,
            bool resolvedFromShortName,
            double[]? recorderMs,
            long[]? recorderCalls,
            double[] captureMs,
            long[] captureCalls,
            long[] captureGcBytes)
        {
            double[] captureSeries = captureMs ?? Array.Empty<double>();
            long[] captureCallSeries = captureCalls ?? Array.Empty<long>();
            int windowFrames = captureSeries.Length;
            bool recorderCovers = recorderMs != null && recorderMs.Length * 2 >= windowFrames;
            bool recorderHasData = recorderCovers && HasData(recorderMs, recorderCalls);
            bool captureHasData = HasData(captureSeries, captureCallSeries);
            double[] series;
            long[] calls;
            string path;
            string note = string.Empty;
            if (recorderHasData)
            {
                series = recorderMs!;
                calls = recorderCalls ?? new long[series.Length];
                path = MarkerPathRecorder;
            }
            else if (captureHasData)
            {
                series = captureSeries;
                calls = captureCallSeries;
                path = MarkerPathCapture;
                if (recorderMs != null)
                {
                    note = recorderCovers
                        ? "ProfilerRecorder returned no samples for this marker; times come from the profiler frame hierarchy."
                        : "ProfilerRecorder covered " + recorderMs.Length + " of " + windowFrames + " frames; times come from the profiler frame hierarchy.";
                }
            }
            else
            {
                series = recorderCovers ? recorderMs! : captureSeries;
                calls = recorderCovers ? (recorderCalls ?? new long[series.Length]) : captureCallSeries;
                path = recorderCovers ? MarkerPathRecorder : MarkerPathCapture;
                note = "The marker did not run during these frames.";
            }

            var stat = new ProfileMarkerStat
            {
                query = query ?? string.Empty,
                marker = marker ?? string.Empty,
                resolvedMarker = resolvedFromShortName ? marker ?? string.Empty : string.Empty,
                path = path,
                frames = series.Length,
                note = note,
            };

            if (series.Length > 0)
            {
                double[] sorted = (double[])series.Clone();
                Array.Sort(sorted);
                stat.min = sorted[0];
                stat.median = Median(series);
                stat.p95 = Percentile(sorted, 95);
                stat.max = sorted[sorted.Length - 1];
            }

            long callTotal = 0;
            int framesWithCalls = 0;
            for (int index = 0; index < series.Length; index++)
            {
                long frameCalls = index < calls.Length ? calls[index] : 0;
                callTotal += frameCalls;
                if (frameCalls > 0 || series[index] > 0)
                {
                    framesWithCalls++;
                }
            }

            stat.framesWithCalls = framesWithCalls;
            stat.callsPerFrame = series.Length > 0 ? (double)callTotal / series.Length : 0;

            long gcTotal = 0;
            long[] gc = captureGcBytes ?? Array.Empty<long>();
            for (int index = 0; index < gc.Length; index++)
            {
                gcTotal += gc[index];
            }

            stat.gcBytesTotal = gcTotal;
            stat.gcBytesPerFrame = gc.Length > 0 ? (double)gcTotal / gc.Length : 0;
            return stat;
        }

        private static bool HasData(double[]? ms, long[]? calls)
        {
            if (ms == null || ms.Length == 0)
            {
                return false;
            }

            for (int index = 0; index < ms.Length; index++)
            {
                if (ms[index] > 0 || (calls != null && index < calls.Length && calls[index] > 0))
                {
                    return true;
                }
            }

            return false;
        }
    }
}
