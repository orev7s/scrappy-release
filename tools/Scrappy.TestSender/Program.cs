using System.Net;
using Scrappy.Protocol;

// A synthetic tablet.
//
// `verify` proves the protocol layer round-trips: sender and reader agree on framing, flags and
// geometry, with no device involved.
//
// `send`   stands in for a real tablet on the wire, so the receiver, the decoder and the
//          presentation path can be developed without hardware attached. With --h264 it emits a
//          real elementary stream, so decoding is genuinely exercised; without it the payloads are
//          filler bytes and only framing is.

return args.Length == 0 ? Usage() : args[0] switch
{
    "verify" => await VerifyAsync(),
    "send" => await SendAsync(SyntheticArguments.Parse(args[1..])),
    _ => Usage(),
};

static int Usage()
{
    Console.WriteLine("""
        Scrappy.TestSender

          verify                 run a loopback round-trip self test (no device, no decoder)
          send [options]         act as a synthetic tablet on the wire

        send options:
          --port <n>             listen port (default 27183)
          --width <n>            reported width (default 2560)
          --height <n>           reported height (default 1708)
          --frames <n>           frames to send, 0 = until interrupted (default 120)
          --fps <n>              pacing target (default 60)
          --h264 <file>          stream this Annex-B H.264 file instead of filler bytes
          --loop                 repeat the file when it runs out (default on for --h264)
        """);
    return 2;
}

static async Task<int> VerifyAsync()
{
    const int frameCount = 90;
    const int width = 2560;
    const int height = 1708;
    const string deviceName = "Synthetic Pad 8";

    await using var device = new SyntheticDevice(new SyntheticDeviceOptions
    {
        Port = 0,
        BindAddress = "127.0.0.1",
        Width = width,
        Height = height,
        Frames = frameCount,
        Fps = 0,
        Source = SyntheticSource.Filler,
        DeviceName = deviceName,
    });

    device.Start();
    Task<int> sender = device.ServeOnceAsync();

    using var client = new System.Net.Sockets.TcpClient();
    await client.ConnectAsync(IPAddress.Loopback, device.Port);
    client.NoDelay = true;

    var reader = new VideoStreamReader();
    await using var stream = client.GetStream();
    string readName = await reader.ReadDeviceNameAsync(stream);

    var failures = new List<string>();
    int frameSeen = 0;
    int configSeen = 0;
    int keySeen = 0;
    SessionGeometry? geometry = null;
    ulong lastPts = 0;
    bool ptsMonotonic = true;

    try
    {
        await foreach (StreamMessage message in reader.ReadMessagesAsync(stream))
        {
            if (message.Kind == StreamMessageKind.Session)
            {
                geometry = message.Session;
                continue;
            }

            VideoPacket packet = message.Packet;
            if (packet.IsConfig)
            {
                configSeen++;
                continue;
            }

            if (packet.IsKeyFrame)
            {
                keySeen++;
            }

            if (frameSeen > 0 && packet.Pts < lastPts)
            {
                ptsMonotonic = false;
            }

            lastPts = packet.Pts;
            frameSeen++;
        }
    }
    catch (Exception ex)
    {
        failures.Add($"reader threw: {ex.GetType().Name}: {ex.Message}");
    }

    await sender;

    if (readName != deviceName) failures.Add($"device name: expected '{deviceName}', got '{readName}'");
    if (geometry is null) failures.Add("no session header received");
    else if (geometry.Value.Width != width || geometry.Value.Height != height)
        failures.Add($"geometry: expected {width}x{height}, got {geometry.Value.Width}x{geometry.Value.Height}");
    if (configSeen != 1) failures.Add($"config packets: expected 1, got {configSeen}");
    if (frameSeen != frameCount) failures.Add($"frames: expected {frameCount}, got {frameSeen}");
    if (keySeen != 2) failures.Add($"key frames: expected 2, got {keySeen}");
    if (!ptsMonotonic) failures.Add("presentation timestamps were not monotonic");

    Console.WriteLine($"device name      {readName}");
    Console.WriteLine($"geometry         {(geometry is null ? "none" : $"{geometry.Value.Width}x{geometry.Value.Height}")}");
    Console.WriteLine($"config packets   {configSeen}");
    Console.WriteLine($"frames           {frameSeen}");
    Console.WriteLine($"key frames       {keySeen}");
    Console.WriteLine($"pts monotonic    {ptsMonotonic}");
    Console.WriteLine();

    if (failures.Count == 0)
    {
        Console.WriteLine("PASS");
        return 0;
    }

    foreach (string failure in failures)
    {
        Console.WriteLine("FAIL  " + failure);
    }

    return 1;
}

static async Task<int> SendAsync(SyntheticArguments arguments)
{
    await using var device = new SyntheticDevice(new SyntheticDeviceOptions
    {
        Port = arguments.Port,
        Width = arguments.Width,
        Height = arguments.Height,
        Frames = arguments.Frames,
        Fps = arguments.Fps,
        Source = arguments.BitstreamPath is null ? SyntheticSource.Filler : SyntheticSource.Bitstream,
        BitstreamPath = arguments.BitstreamPath,
    });

    device.Start();

    string source = arguments.BitstreamPath is null
        ? "filler payloads (framing only)"
        : $"H.264 {Path.GetFileName(arguments.BitstreamPath)}";

    Console.WriteLine($"synthetic tablet listening on 0.0.0.0:{device.Port} " +
                      $"({arguments.Width}x{arguments.Height}, {arguments.Fps} fps, {arguments.Frames} frames)");
    Console.WriteLine($"source: {source}");

    int written = await device.ServeOnceAsync();
    Console.WriteLine($"sent {written} frames");
    return 0;
}

internal sealed record SyntheticArguments(int Port, int Width, int Height, int Frames, int Fps, string? BitstreamPath)
{
    public static SyntheticArguments Parse(string[] args)
    {
        int port = 27183, width = 2560, height = 1708, frames = 120, fps = 60;
        string? bitstream = null;

        for (int i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--port": port = int.Parse(args[++i]); break;
                case "--width": width = int.Parse(args[++i]); break;
                case "--height": height = int.Parse(args[++i]); break;
                case "--frames": frames = int.Parse(args[++i]); break;
                case "--fps": fps = int.Parse(args[++i]); break;
                case "--h264": bitstream = args[++i]; break;
                case "--loop": break; // looping is implied when a bitstream is supplied
                default:
                    throw new ArgumentException($"Unknown option '{args[i]}'. Run without arguments for usage.");
            }
        }

        return new SyntheticArguments(port, width, height, frames, fps, bitstream);
    }
}
