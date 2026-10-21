using System.Diagnostics;
using System.Globalization;

namespace UnityCli.Cli.Services;

/// <summary>
/// Opt-in phase log for <c>UCB_TIMING=1</c>. Lines go to stderr only, so stdout JSON stays clean.
/// Anything other than the exact value <c>1</c> leaves the log off.
/// </summary>
internal static class CliTiming
{
    public const string EnvironmentVariable = "UCB_TIMING";

    private static readonly long ClockStartTimestamp = Stopwatch.GetTimestamp();
    private static readonly double ProcessStartToClockStartMs = MeasureProcessStartToClockStart();

    public static bool IsEnabled =>
        string.Equals(Environment.GetEnvironmentVariable(EnvironmentVariable), "1", StringComparison.Ordinal);

    public static Stopwatch? Start()
    {
        return IsEnabled ? Stopwatch.StartNew() : null;
    }

    public static void ReportProcessStartToMain()
    {
        if (!IsEnabled)
        {
            return;
        }

        double sinceProcessStart = SinceProcessStartMilliseconds();
        Write(Console.Error, "processStartToMain", sinceProcessStart, sinceProcessStart);
    }

    public static void Report(string phase, Stopwatch? stopwatch)
    {
        if (stopwatch == null)
        {
            return;
        }

        stopwatch.Stop();
        ReportMilliseconds(phase, stopwatch.Elapsed.TotalMilliseconds);
    }

    public static void ReportMilliseconds(string phase, double milliseconds)
    {
        if (!IsEnabled)
        {
            return;
        }

        Write(Console.Error, phase, milliseconds, SinceProcessStartMilliseconds());
    }

    public static void Write(TextWriter writer, string phase, double milliseconds, double sinceProcessStartMilliseconds)
    {
        writer.WriteLine(string.Format(
            CultureInfo.InvariantCulture,
            "ucb-timing phase={0} ms={1:0.###} sinceProcessStartMs={2:0.###}",
            phase,
            milliseconds,
            sinceProcessStartMilliseconds));
    }

    public static double SinceProcessStartMilliseconds()
    {
        double sinceClock = (Stopwatch.GetTimestamp() - ClockStartTimestamp) * 1000.0 / Stopwatch.Frequency;
        return Math.Max(0, ProcessStartToClockStartMs + sinceClock);
    }

    private static double MeasureProcessStartToClockStart()
    {
        try
        {
            double elapsed = (DateTime.Now - Process.GetCurrentProcess().StartTime).TotalMilliseconds;
            return double.IsFinite(elapsed) ? Math.Max(0, elapsed) : 0;
        }
        catch (Exception)
        {
            return 0;
        }
    }
}
