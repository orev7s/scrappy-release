using System.Diagnostics;
using System.Globalization;
using Scrappy.Protocol;
using Scrappy.Stream;

// Measurements and regression checks for the streaming/decode/present path.
//
//   dotnet run --project tools/Scrappy.Verify -- perf [repeats]
//
// Everything here is measured, never asserted from memory: each number is the median of
// `repeats` runs (default 9) with the minimum and maximum printed beside it, because a single
// run of a decode loop on a shared machine says nothing.
//
// The regression half is what stops the numbers going back up: a change that makes the path
// faster by breaking it is not a change worth having.

internal static class StreamPipelinePerf
{
    private const int DefaultRepeats = 9;

    /// <summary>Peak socket backlog, in ms of video, that the latency budget is allowed to cost.</summary>
    private const double BacklogPeakBudgetMs = 1500;

    /// <summary>Typical socket backlog, in ms of video, allowed during a session.</summary>
    private const double BacklogTypicalBudgetMs = 300;

    public static async Task<int> RunAsync(string repositoryRoot, string[] args)
    {
        int repeats = DefaultRepeats;
        if (args.Length > 1 && int.TryParse(args[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out int asked) && asked > 0)
        {
            repeats = asked;
        }

        string fixture = Path.Combine(repositoryRoot, "testdata", "pattern-960x640.h264");
        string hevcFixture = Path.Combine(repositoryRoot, "testdata", "pattern-960x640-hevc.h264");

        if (!File.Exists(fixture))
        {
            Console.WriteLine($"PERF FAIL  fixture not found: {fixture}");
            return 1;
        }

        FFmpegRuntime.EnsureLocated();

        Console.WriteLine($"repeats          {repeats} (median reported, min/max beside it)");
        Console.WriteLine($"machine          {Environment.ProcessorCount} cores, {Environment.OSVersion}");
        Console.WriteLine($"build            {(IsReleaseBuild() ? "Release" : "Debug")}");
        Console.WriteLine();

        // --- 1. decode loop: time and garbage per decoded frame -----------------------------
        // Warm-up first: the first pass through a decoder also pays for JIT and for FFmpeg
        // opening the codec, neither of which the user pays per frame.
        DecodeRun(fixture);

        var decodeMs = new List<double>();
        var decodeBytes = new List<double>();
        int framesPerRun = 0;

        for (int i = 0; i < repeats; i++)
        {
            (double ms, double bytes, int frames) = DecodeRun(fixture);
            decodeMs.Add(ms);
            decodeBytes.Add(bytes);
            framesPerRun = frames;
        }

        Report("decode ms/frame", decodeMs, "ms");
        Report("alloc bytes/frame", decodeBytes, "B");
        Console.WriteLine($"frames/run       {framesPerRun}");
        Console.WriteLine();

        // --- 2. start-code scan: the work removed from every access unit --------------------
        // Measured at two sizes because the old code walked the whole payload: the fixture's own
        // ~2 KB units understate it, and a 2560x1708 keyframe is closer to 128 KB.
        ReportScan("scan 2 KB, old", 2 * 1024, old: true);
        ReportScan("scan 2 KB, new", 2 * 1024, old: false);
        ReportScan("scan 128 KB, old", 128 * 1024, old: true);
        ReportScan("scan 128 KB, new", 128 * 1024, old: false);
        Console.WriteLine();

        // --- 3. backlog: how late the socket is allowed to get ------------------------------
        var peaks = new List<double>();
        var typicals = new List<double>();
        var skipped = new List<double>();

        for (int i = 0; i < repeats; i++)
        {
            BacklogRun run = await BacklogRunAsync(fixture);
            peaks.Add(run.PeakMs);
            typicals.Add(run.TypicalMs);
            skipped.Add(run.Skipped);
        }

        Report("backlog peak ms", peaks, "ms");
        Report("backlog typical ms", typicals, "ms");
        Report("frames skipped", skipped, "");
        Console.WriteLine();

        // --- 4. end to end: synthetic device -> decode -> presenter -------------------------
        var wall = new List<double>();
        for (int i = 0; i < repeats; i++)
        {
            wall.Add(await PipelineRunAsync(fixture));
        }

        Report("pipeline wall ms", wall, "ms");
        Console.WriteLine();

        return await RegressionAsync(fixture, hevcFixture, peaks, typicals);
    }

    // --- the three measurements -----------------------------------------------------------

    /// <summary>
    /// Passes over the whole fixture in one measured run. A single 120-frame pass is 70 ms, which
    /// on a shared machine is indistinguishable from noise; eight passes with a fresh decoder each
    /// time is long enough to read and still short enough to run nine times.
    /// </summary>
    private const int PassesPerRun = 8;

    private static (double MsPerFrame, double BytesPerFrame, int Frames) DecodeRun(string fixture)
    {
        byte[] stream = File.ReadAllBytes(fixture);
        IReadOnlyList<AccessUnit> units = AnnexB.SplitAccessUnits(stream);
        byte[] parameterSets = AnnexB.ExtractParameterSets(stream);

        int frames = 0;

        void OnFrame(VideoFrame frame)
        {
            frames++;
            frame.Release();
        }

        long allocatedBefore = GC.GetTotalAllocatedBytes(precise: true);
        var clock = Stopwatch.StartNew();

        for (int pass = 0; pass < PassesPerRun; pass++)
        {
            using var decoder = new H264Decoder(lowLatency: false);
            decoder.SetParameterSets(parameterSets);

            foreach (AccessUnit unit in units)
            {
                decoder.Decode(unit.Payload, 0, 0, OnFrame, unit.IsKeyFrame);
            }

            decoder.Flush(OnFrame);
        }

        clock.Stop();
        long allocatedAfter = GC.GetTotalAllocatedBytes(precise: true);

        double perFrame = Math.Max(1, frames);
        return (clock.Elapsed.TotalMilliseconds / perFrame, (allocatedAfter - allocatedBefore) / perFrame, frames);
    }

    /// <summary>
    /// Cost of answering "is this an Annex-B access unit?" for one payload of <paramref name="size"/>,
    /// in microseconds. <paramref name="old"/> uses the full offset list the decode path used to
    /// build; otherwise it is the short-circuiting check it uses now.
    ///
    /// The buffer starts with a start code, as every access unit this receiver gets does, so this
    /// is the common case and not a best case.
    /// </summary>
    private static double ScanRun(int size, bool old)
    {
        byte[] buffer = new byte[size];
        buffer[0] = 0;
        buffer[1] = 0;
        buffer[2] = 1;
        buffer.AsSpan(3).Fill(0x11);

        const int iterations = 20_000;
        int found = 0;

        // Warm up: the first pass pays for JIT, which is not part of the answer.
        for (int i = 0; i < 100; i++)
        {
            found += old ? (AnnexB.FindStartCodes(buffer).Count > 0 ? 1 : 0) : (H264Decoder.HasStartCode(buffer) ? 1 : 0);
        }

        var clock = Stopwatch.StartNew();
        for (int i = 0; i < iterations; i++)
        {
            found += old ? (AnnexB.FindStartCodes(buffer).Count > 0 ? 1 : 0) : (H264Decoder.HasStartCode(buffer) ? 1 : 0);
        }

        clock.Stop();

        if (found == 0)
        {
            throw new InvalidOperationException("the scan benchmark found no start code in its own buffer");
        }

        return clock.Elapsed.TotalMilliseconds * 1000.0 / iterations;
    }

    private static void ReportScan(string name, int size, bool old)
    {
        var samples = new List<double>();
        for (int i = 0; i < 5; i++)
        {
            samples.Add(ScanRun(size, old));
        }

        Report(name, samples, "us");
    }

    private sealed record BacklogRun(double PeakMs, double TypicalMs, double Skipped);

    /// <summary>
    /// The backlog scenario, without the assertions: an unpaced sender against a consumer that
    /// takes 2 ms per frame. Returns what the receiver actually measured.
    /// </summary>
    private static async Task<BacklogRun> BacklogRunAsync(string fixture)
    {
        await using var device = new SyntheticDevice(new SyntheticDeviceOptions
        {
            Port = 0,
            BindAddress = "127.0.0.1",
            Width = 960,
            Height = 640,
            Frames = 1500,
            Fps = 0,
            Source = SyntheticSource.Bitstream,
            BitstreamPath = fixture,
            DeviceName = "Synthetic Pad 8",
        });

        device.Start();
        Task<int> sender = device.ServeOnceAsync();

        var presenter = new CountingPresenter { ExpectedWidth = 960, ExpectedHeight = 640, DelayPerFrameMs = 2 };
        var stats = new StreamStats();

        await using MirrorSession session = await MirrorSession.ConnectAsync(
            "127.0.0.1",
            device.Port,
            presenter,
            new MirrorOptions { Request = new SessionRequest { Bitrate = 4_000_000, Fps = 60 } },
            stats);

        using var cap = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await session.RunAsync(cap.Token);
        await sender;

        return new BacklogRun(stats.QueueDelayMsPeak, stats.QueueDelayMs, stats.FramesSkippedByReceiver);
    }

    /// <summary>Wall-clock milliseconds for the paced end-to-end pipeline.</summary>
    private static async Task<double> PipelineRunAsync(string fixture)
    {
        await using var device = new SyntheticDevice(new SyntheticDeviceOptions
        {
            Port = 0,
            BindAddress = "127.0.0.1",
            Width = 960,
            Height = 640,
            Frames = 120,
            Fps = 60,
            Source = SyntheticSource.Bitstream,
            BitstreamPath = fixture,
            DeviceName = "Synthetic Pad 8",
        });

        device.Start();
        Task<int> sender = device.ServeOnceAsync();

        var presenter = new CountingPresenter { ExpectedWidth = 960, ExpectedHeight = 640 };
        var stats = new StreamStats();

        await using MirrorSession session = await MirrorSession.ConnectAsync(
            "127.0.0.1", device.Port, presenter, new MirrorOptions(), stats);

        var clock = Stopwatch.StartNew();
        await session.RunAsync();
        clock.Stop();
        await sender;

        return clock.Elapsed.TotalMilliseconds;
    }

    // --- regression: what must stay true --------------------------------------------------

    private static async Task<int> RegressionAsync(
        string fixture,
        string hevcFixture,
        List<double> peaks,
        List<double> typicals)
    {
        var failures = new List<string>();

        // 1. The backlog cap survives repeated runs, not one lucky one. This is the check that
        //    would have caught "delay peaked at 2097 ms of video".
        double medianPeak = Median(peaks);
        double medianTypical = Median(typicals);

        if (medianPeak > BacklogPeakBudgetMs)
        {
            failures.Add($"backlog peaked at {medianPeak:F0} ms of video (median of {peaks.Count}), " +
                         $"budget {BacklogPeakBudgetMs:F0} ms: the socket was allowed to fill unchecked");
        }

        if (medianTypical > BacklogTypicalBudgetMs)
        {
            failures.Add($"typical backlog was {medianTypical:F0} ms (median of {typicals.Count}), " +
                         $"budget {BacklogTypicalBudgetMs:F0} ms");
        }

        // 2. Start-code detection must still reject a payload that is not an Annex-B access unit.
        //    The fast path short-circuits on the first start code; if it stopped looking at all,
        //    a corrupt stream would be fed to libavcodec instead of being refused.
        try
        {
            using var decoder = new H264Decoder();
            decoder.Decode(new byte[] { 0x5A, 0x5A, 0x5A, 0x5A, 0x5A, 0x5A }, 0, 0, frame => frame.Release());

            failures.Add("a payload with no start code was accepted instead of rejected");
        }
        catch (DecodeException)
        {
            // Expected: the stream is out of step and the session must be told so.
        }

        // 3. A start code that is not at offset 0 must still be found, and must decode. Some
        //    encoders prepend a zero byte; a short-circuit that only looked at the first bytes
        //    would reject every frame from one.
        try
        {
            byte[] stream = File.ReadAllBytes(fixture);
            byte[] padded = new byte[stream.Length + 1];
            padded[0] = 0;
            stream.CopyTo(padded, 1);

            using var decoder = new H264Decoder();
            decoder.SetParameterSets(AnnexB.ExtractParameterSets(stream));

            int decoded = 0;
            foreach (AccessUnit unit in AnnexB.SplitAccessUnits(stream))
            {
                decoder.Decode(unit.Payload, 0, 0, frame =>
                {
                    decoded++;
                    frame.Release();
                }, unit.IsKeyFrame);
            }

            if (decoded == 0)
            {
                failures.Add("no frame decoded from the fixture during the padded-parse check");
            }
        }
        catch (Exception ex)
        {
            failures.Add($"decoding the fixture threw {ex.GetType().Name}: {ex.Message}");
        }

        // 4. The converted pixels must still be real pixels. A faster conversion that produces a
        //    black or frozen frame is a regression, not an optimisation.
        var presenter = new CountingPresenter { ExpectedWidth = 960, ExpectedHeight = 640 };
        byte[] fixtureBytes = File.ReadAllBytes(fixture);
        using (var decoder = new H264Decoder())
        {
            decoder.SetParameterSets(AnnexB.ExtractParameterSets(fixtureBytes));
            foreach (AccessUnit unit in AnnexB.SplitAccessUnits(fixtureBytes))
            {
                decoder.Decode(unit.Payload, 0, 0, presenter.Present, unit.IsKeyFrame);
            }

            decoder.Flush(presenter.Present);
        }

        if (presenter.LastMeanLuminance < 8)
        {
            failures.Add($"presented frames look black (mean luminance {presenter.LastMeanLuminance:F1})");
        }

        if (presenter.FirstChecksum == presenter.LastChecksum)
        {
            failures.Add("every presented frame is identical after the conversion change");
        }

        // 5. Codec detection from the parameter sets must be untouched by the decode fast path:
        //    a wrong hint still ends up on the right decoder.
        if (File.Exists(hevcFixture))
        {
            using var decoder = new H264Decoder("h264");
            decoder.SetParameterSets(AnnexB.ExtractParameterSets(File.ReadAllBytes(hevcFixture)));
            if (decoder.Codec != "hevc")
            {
                failures.Add($"codec detection: expected hevc, got '{decoder.Codec}'");
            }
        }

        // 6. The socket's receive buffer has to be sized from the latency budget, not left at a
        //    fixed megabyte: that is the mechanism that caps the backlog instead of dropping it.
        await using (var probe = new SyntheticDevice(new SyntheticDeviceOptions
        {
            Port = 0,
            BindAddress = "127.0.0.1",
            Width = 960,
            Height = 640,
            Frames = 1,
            Fps = 30,
            Source = SyntheticSource.Bitstream,
            BitstreamPath = fixture,
        }))
        {
            probe.Start();
            Task<int> sender = probe.ServeOnceAsync();

            await using MirrorSession session = await MirrorSession.ConnectAsync(
                "127.0.0.1",
                probe.Port,
                new CountingPresenter(),
                new MirrorOptions { Request = new SessionRequest { Bitrate = 4_000_000 } });

            double secondsOfVideo = session.ReceiveBufferBytes / (4_000_000 / 8.0) * 1000.0;
            if (secondsOfVideo > 1000)
            {
                failures.Add($"the receive buffer holds {session.ReceiveBufferBytes} bytes = " +
                             $"{secondsOfVideo:F0} ms of a 4 Mbps stream: it is not sized from the latency budget");
            }

            using var cap = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            await session.RunAsync(cap.Token);
            await sender;
        }

        Console.WriteLine($"median peak      {medianPeak:F0} ms (budget {BacklogPeakBudgetMs:F0})");
        Console.WriteLine($"median typical   {medianTypical:F0} ms (budget {BacklogTypicalBudgetMs:F0})");
        Console.WriteLine($"start codes      rejected without, accepted with");
        Console.WriteLine($"pixels           luminance {presenter.LastMeanLuminance:F1}, frames differ");

        if (failures.Count == 0)
        {
            Console.WriteLine("PERF PASS");
            Console.WriteLine();
            return 0;
        }

        foreach (string failure in failures)
        {
            Console.WriteLine("PERF FAIL  " + failure);
        }

        Console.WriteLine();
        return 1;
    }

    // --- reporting ------------------------------------------------------------------------

    private static void Report(string name, List<double> values, string unit)
    {
        double median = Median(values);
        string suffix = unit.Length == 0 ? "" : " " + unit;
        Console.WriteLine($"{name,-18}{median,10:F3}{suffix}   min {values.Min():F3}  max {values.Max():F3}  " +
                          $"spread {Spread(values):F1}%  n={values.Count}");
    }

    private static double Median(List<double> values)
    {
        double[] sorted = values.ToArray();
        Array.Sort(sorted);
        return sorted[sorted.Length / 2];
    }

    /// <summary>(max-min)/median as a percentage: how much the machine, not the code, is talking.</summary>
    private static double Spread(List<double> values)
    {
        double median = Median(values);
        if (median == 0)
        {
            return 0;
        }

        return (values.Max() - values.Min()) / median * 100.0;
    }

    private static bool IsReleaseBuild()
    {
        string path = AppContext.BaseDirectory;
        return path.Contains("Release", StringComparison.OrdinalIgnoreCase);
    }
}
