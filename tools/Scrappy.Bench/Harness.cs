using System.Diagnostics;

namespace Scrappy.Bench;

/// <summary>
/// The measurement loop. Deliberately small: warm up, run N iterations, report median + p95 and the
/// allocation cost of one iteration, one machine-readable line each.
///
/// Nothing here is a framework. A benchmark that needs a framework also needs a framework version,
/// and this repo is one project the user builds with <c>dotnet build</c>.
/// </summary>
internal static class Harness
{
    public sealed record Sample(double MedianMs, double P95Ms, double MinMs, double MaxMs, double AllocKb, int Gen0, int Gen1, int Gen2);

    /// <summary>
    /// Runs <paramref name="body"/> <paramref name="iterations"/> times after
    /// <paramref name="warmup"/> discarded runs and returns the summary of the measured ones.
    ///
    /// Allocation is measured with <see cref="GC.GetTotalAllocatedBytes(bool)"/> around each
    /// iteration rather than per-thread bytes, because several scenarios await socket work that
    /// allocates on pool threads - attributing that to the scenario is the point.
    /// </summary>
    public static async Task<Sample> MeasureAsync(
        Func<CancellationToken, Task> body,
        int iterations,
        int warmup,
        CancellationToken cancellationToken)
    {
        for (int i = 0; i < warmup; i++)
        {
            await body(cancellationToken).ConfigureAwait(false);
        }

        var times = new double[iterations];
        long allocated = 0;
        int gen0 = 0, gen1 = 0, gen2 = 0;

        for (int i = 0; i < iterations; i++)
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();

            int g0 = GC.CollectionCount(0), g1 = GC.CollectionCount(1), g2 = GC.CollectionCount(2);
            long before = GC.GetTotalAllocatedBytes(precise: true);

            long start = Stopwatch.GetTimestamp();
            await body(cancellationToken).ConfigureAwait(false);
            long end = Stopwatch.GetTimestamp();

            allocated += GC.GetTotalAllocatedBytes(precise: true) - before;
            gen0 += GC.CollectionCount(0) - g0;
            gen1 += GC.CollectionCount(1) - g1;
            gen2 += GC.CollectionCount(2) - g2;

            times[i] = (end - start) * 1000.0 / Stopwatch.Frequency;
        }

        Array.Sort(times);
        return new Sample(
            MedianMs: Percentile(times, 0.50),
            P95Ms: Percentile(times, 0.95),
            MinMs: times[0],
            MaxMs: times[^1],
            AllocKb: allocated / 1024.0 / iterations,
            Gen0: gen0,
            Gen1: gen1,
            Gen2: gen2);
    }

    /// <summary>
    /// Linear-interpolated percentile of an already sorted array. With the seven samples this
    /// harness collects, nearest-rank p95 would just be the maximum, which reads as noise; the
    /// interpolated form says "the tail between median and worst" instead.
    /// </summary>
    private static double Percentile(double[] sorted, double fraction)
    {
        if (sorted.Length == 1)
        {
            return sorted[0];
        }

        double position = fraction * (sorted.Length - 1);
        int low = (int)Math.Floor(position);
        int high = Math.Min(low + 1, sorted.Length - 1);
        double weight = position - low;
        return sorted[low] * (1 - weight) + sorted[high] * weight;
    }
}
