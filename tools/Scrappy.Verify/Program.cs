using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using Scrappy.Discovery;
using Scrappy.Protocol;
using Scrappy.Settings;
using Scrappy.Stream;

// End-to-end checks that need no tablet and no display server.
//
//   pipeline   synthetic device -> TCP -> protocol reader -> H.264 decode -> presenter
//   settings   every option in the schema reaches the server argument list
//   decode     decode throughput at a given size, informational
//
// Exit code 0 means every requested check passed.

string command = args.Length > 0 ? args[0] : "all";
string root = FindRepositoryRoot();

return command switch
{
    "pipeline" => await PipelineAsync(),
    "handshake" => await HandshakeAsync(),
    "codec" => await CodecAsync(),
    "settings" => Settings(),
    "state" => State(),
    "discovery" => await DiscoveryAsync(),
    "disconnect" => await DisconnectAsync(),
    "links" => Links(),
    "plan" => Plan(),
    "advisor" => Advisor(),
    "postmortem" => await PostmortemAsync(),
    "backlog" => await BacklogAsync(),
    "decode" => await DecodeThroughputAsync(args.Length > 1 ? args[1] : Fixture("pattern-960x640.h264")),
    "all" => await AllAsync(),
    _ => Usage(),
};

async Task<int> AllAsync()
{
    int settingsResult = Settings();
    int stateResult = State();
    int discoveryResult = await DiscoveryAsync();
    int linksResult = Links();
    int planResult = Plan();
    int advisorResult = Advisor();
    int postmortemResult = await PostmortemAsync();
    int disconnectResult = await DisconnectAsync();
    int pipelineResult = await PipelineAsync();
    int handshakeResult = await HandshakeAsync();
    int codecResult = await CodecAsync();
    int backlogResult = await BacklogAsync();
    return settingsResult == 0 && stateResult == 0 && discoveryResult == 0 && linksResult == 0 &&
           planResult == 0 && advisorResult == 0 && postmortemResult == 0 && disconnectResult == 0 && pipelineResult == 0 &&
           handshakeResult == 0 && codecResult == 0 && backlogResult == 0
        ? 0
        : 1;
}

static int Usage()
{
    Console.WriteLine("""
        Scrappy.Verify

          all                    every check below (default)
          settings               every schema option reaches the server arguments
          state                  settings, address and raw arguments survive a restart
          discovery              a device that answers the probe appears in the picker
          links                  a cable address is wired, a Wi-Fi address is not
          plan                   the cable is swept even when Wi-Fi answered first
          advisor                the suggested preset always exists, applies, and matches the codec
          postmortem             a dead session is explained by the tablet's own status
          disconnect             Stop closes the socket and a second session works
          pipeline               synthetic device -> decode -> presenter, end to end
          handshake              the exact byte order the Android app writes, and the
                                 failure a wrong order must produce
          codec                  H.265 decodes even when the settings claimed H.264
          backlog                a receiver that cannot keep up skips, it does not lag
          decode [file.h264]     decode throughput of a file, informational
        """);
    return 2;
}

int Settings()
{
    ScrcpySchema schema = ScrcpySchema.LoadEmbedded();
    var store = new SettingsStore(schema);
    var failures = new List<string>();

    // Turn every option on with a plausible value, then check each one is present in the built
    // argument list. This is the mechanical form of "every scrcpy setting is configurable":
    // if an option cannot be expressed, it fails here rather than being quietly missing.
    foreach (ScrcpyOption option in schema.Options)
    {
        if (LockedOptions.Find(option.Id) is not null)
        {
            continue;
        }

        switch (option.Kind)
        {
            case OptionKind.Flag:
                store.Set(option.Id, OptionSetting.Flag(true));
                break;
            case OptionKind.OptionalValue:
                store.Set(option.Id, OptionSetting.Bare());
                break;
            default:
                store.Set(option.Id, OptionSetting.Of(option.Enum?.FirstOrDefault() ?? "1"));
                break;
        }
    }

    IReadOnlyList<string> arguments = ServerArguments.Build(store);
    var keys = arguments
        .Select(a => a.Split('=', 2)[0])
        .ToHashSet(StringComparer.Ordinal);

    foreach (ScrcpyOption option in schema.Options)
    {
        if (!keys.Contains(option.ServerKey))
        {
            failures.Add($"{option.Flag} never reaches the server arguments");
        }
    }

    if (schema.Count != schema.Options.Count)
    {
        failures.Add($"schema says {schema.Count} options but lists {schema.Options.Count}");
    }

    // The audio pair is not a user choice; it must always be present and always be this pair.
    if (!arguments.Contains("audio_source=playback"))
    {
        failures.Add("audio_source=playback is missing from the arguments");
    }

    if (!arguments.Contains("audio_dup=true"))
    {
        failures.Add("audio_dup=true is missing from the arguments");
    }

    // A profile must not be able to unlock the audio pair.
    store.ApplyProfileValues(new Dictionary<string, string>
    {
        ["audio_source"] = "output",
        ["audio_dup"] = "false",
    });

    if (!ServerArguments.Build(store).Contains("audio_dup=true"))
    {
        failures.Add("a profile was able to turn audio_dup off");
    }

    failures.AddRange(ProfilesRoundTrip(schema));

    Console.WriteLine($"schema           {schema.Options.Count} options from {schema.SourceCommit ?? "unknown"}, {schema.Groups.Count} groups");
    Console.WriteLine($"enumerated       {schema.Options.Count(o => o.IsEnumerated)}");
    Console.WriteLine($"locked           {string.Join(", ", LockedOptions.All.Select(l => l.OptionId))}");
    Console.WriteLine($"arguments        {arguments.Count} built");

    if (failures.Count == 0)
    {
        Console.WriteLine("SETTINGS PASS");
        Console.WriteLine();
        return 0;
    }

    foreach (string failure in failures)
    {
        Console.WriteLine("SETTINGS FAIL  " + failure);
    }

    Console.WriteLine();
    return 1;
}

static List<string> ProfilesRoundTrip(ScrcpySchema schema)
{
    var failures = new List<string>();
    string directory = Path.Combine(Path.GetTempPath(), "scrappy-verify-" + Guid.NewGuid().ToString("N")[..8]);
    string file = Path.Combine(directory, "profiles.json");

    try
    {
        var store = new SettingsStore(schema);
        store.SetValue("max_size", "1920");
        store.SetValue("video_codec", "h264");
        store.Set("crop", OptionSetting.Bare());

        var profiles = new ProfileStore(file);
        profiles.SaveProfile("Test", store);

        var reloaded = new SettingsStore(schema);
        List<Profile> saved = new ProfileStore(file).Load();

        if (saved.Count != 1 || saved[0].Name != "Test")
        {
            failures.Add($"profile file round-trip: expected 1 profile named Test, got {saved.Count}");
            return failures;
        }

        reloaded.ApplyProfileValues(saved[0].Values);

        if (reloaded.GetValue("max_size") != "1920" || reloaded.GetValue("video_codec") != "h264")
        {
            failures.Add("values did not survive a profile round-trip");
        }

        if (!reloaded.Get("crop").IsSet)
        {
            failures.Add("a bare optional-value option did not survive a profile round-trip");
        }
    }
    catch (Exception ex)
    {
        failures.Add($"profiles: {ex.GetType().Name}: {ex.Message}");
    }
    finally
    {
        try
        {
            Directory.Delete(directory, recursive: true);
        }
        catch (IOException)
        {
            // Temporary directory; leaving it behind is not a test failure.
        }
    }

    return failures;
}

/// <summary>
/// The tablet decides which encoder it uses, and it may not be the one requested. Decoding H.265
/// with an H.264 decoder gives AVERROR_INVALIDDATA, which reads as corrupt data rather than as a
/// mismatch. The codec is therefore detected from the parameter sets, and this checks that a
/// wrong hint does not matter.
/// </summary>
async Task<int> CodecAsync()
{
    string fixture = Fixture("pattern-960x640-hevc.h264");
    if (!File.Exists(fixture))
    {
        Console.WriteLine($"CODEC FAIL  fixture not found: {fixture}");
        return 1;
    }

    const int frameCount = 120;

    await using var device = new SyntheticDevice(new SyntheticDeviceOptions
    {
        Port = 0,
        BindAddress = "127.0.0.1",
        Width = 960,
        Height = 640,
        Frames = frameCount,
        Fps = 60,
        Source = SyntheticSource.Bitstream,
        BitstreamPath = fixture,
        DeviceName = "Synthetic Pad 8",
    });

    device.Start();
    Task<int> sender = device.ServeOnceAsync();

    var presenter = new CountingPresenter { ExpectedWidth = 960, ExpectedHeight = 640 };
    var stats = new StreamStats();

    // Deliberately wrong hint: the stream is HEVC.
    await using MirrorSession session = await MirrorSession.ConnectAsync(
        "127.0.0.1",
        device.Port,
        presenter,
        new MirrorOptions { Request = new SessionRequest { Codec = "h264" } },
        stats);

    await session.RunAsync();
    await sender;

    var failures = new List<string>();

    if (stats.Codec != "hevc")
    {
        failures.Add($"codec detection: expected hevc, got '{stats.Codec}'");
    }

    if (stats.FramesDecoded != frameCount)
    {
        failures.Add($"frames decoded: expected {frameCount}, got {stats.FramesDecoded} (errors {stats.DecodeErrors})");
    }

    if (presenter.LastMeanLuminance < 8)
    {
        failures.Add($"frames look black (mean luminance {presenter.LastMeanLuminance:F1})");
    }

    Console.WriteLine($"codec detected   {stats.Codec} (settings said h264)");
    Console.WriteLine($"decoded          {stats.FramesDecoded} frames, {stats.DecodeMsAverage:F2} ms average");
    Console.WriteLine($"presented        {presenter.Presented} frames, mean luminance {presenter.LastMeanLuminance:F1}");

    if (failures.Count == 0)
    {
        Console.WriteLine("CODEC PASS");
        Console.WriteLine();
        return 0;
    }

    foreach (string failure in failures)
    {
        Console.WriteLine("CODEC FAIL  " + failure);
    }

    Console.WriteLine();
    return 1;
}

/// <summary>
/// Checks the handshake contract against the byte order the Android server writes:
/// 64-byte name, session header, config packet, frames.
///
/// The Android app once announced geometry before the name. The receiver read header bytes as
/// text, then waited forever for a name that had already gone past - the PC sat on "Connecting"
/// while the tablet counted frames. This test exists so that order cannot drift again unnoticed.
/// </summary>
async Task<int> HandshakeAsync()
{
    var failures = new List<string>();
    string fixture = Fixture("pattern-960x640.h264");
    byte[] stream = File.ReadAllBytes(fixture);
    IReadOnlyList<AccessUnit> units = AnnexB.SplitAccessUnits(stream);
    byte[] parameterSets = AnnexB.ExtractParameterSets(stream);

    int ServeOnce(bool nameFirst, int port)
    {
        var listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, port);
        listener.Start();

        _ = Task.Run(async () =>
        {
            using System.Net.Sockets.TcpClient peer = await listener.AcceptTcpClientAsync();
            peer.NoDelay = true;
            NetworkStream s = peer.GetStream();

            // The PC pushes one JSON line of settings before anything else.
            // Only wait briefly: this test's client sends no settings line, and a blocked read
            // here would look exactly like the bug the test is meant to catch.
            var reader = new StreamReader(s, System.Text.Encoding.UTF8, false, 1024, leaveOpen: true);
            await Task.WhenAny(reader.ReadLineAsync(), Task.Delay(300));

            byte[] name = new byte[ProtocolConstants.DeviceNameLength];
            System.Text.Encoding.UTF8.GetBytes("Xiaomi Pad 8", 0, 12, name, 0);

            byte[] session = new byte[ProtocolConstants.HeaderSize];
            StreamHeader.WriteSessionHeader(session, 960, 640, false);

            if (nameFirst)
            {
                await s.WriteAsync(name);
                await s.WriteAsync(session);
            }
            else
            {
                // The old, broken order.
                await s.WriteAsync(session);
                await s.WriteAsync(name);
            }

            await s.WriteAsync(PacketBlock(0, true, false, parameterSets.Length > 0 ? parameterSets : new byte[16]));

            for (int i = 0; i < Math.Min(30, units.Count); i++)
            {
                await s.WriteAsync(PacketBlock((ulong)i * 16_666, false, units[i].IsKeyFrame, units[i].Payload));
            }

            await s.FlushAsync();
            await Task.Delay(150);
            listener.Stop();
        });

        return listener.LocalEndpoint is System.Net.IPEndPoint ep ? ep.Port : port;
    }

    // 1. Correct order decodes.
    int goodPort = ServeOnce(nameFirst: true, 0);
    var presenter = new CountingPresenter();
    var stats = new StreamStats();
    try
    {
        await using MirrorSession session = await MirrorSession.ConnectAsync(
            "127.0.0.1", goodPort, presenter, new MirrorOptions { HandshakeTimeoutMs = 4000 }, stats);
        await session.RunAsync();

        if (session.DeviceName != "Xiaomi Pad 8")
        {
            failures.Add($"name first: expected 'Xiaomi Pad 8', got '{session.DeviceName}'");
        }

        if (stats.FramesDecoded < 25)
        {
            failures.Add($"name first: decoded only {stats.FramesDecoded} frames");
        }

        Console.WriteLine($"name first       {session.DeviceName}, {stats.FramesDecoded} frames decoded");
    }
    catch (Exception ex)
    {
        failures.Add($"name first: {ex.GetType().Name}: {ex.Message}");
    }

    // 2. Wrong order must fail quickly and say why, not hang.
    int badPort = ServeOnce(nameFirst: false, 0);
    var badStats = new StreamStats();
    var clock = Stopwatch.StartNew();
    try
    {
        await using MirrorSession session = await MirrorSession.ConnectAsync(
            "127.0.0.1", badPort, new CountingPresenter(), new MirrorOptions { HandshakeTimeoutMs = 4000 }, badStats);
        await session.RunAsync();

        // If it got this far it did not reject the bad order: say so.
        failures.Add($"wrong order: accepted a stream that began with a session header (name '{session.DeviceName}')");
    }
    catch (InvalidDataException ex)
    {
        Console.WriteLine($"wrong order      rejected in {clock.ElapsedMilliseconds} ms: {ex.Message[..Math.Min(60, ex.Message.Length)]}...");
    }
    catch (TimeoutException)
    {
        // Acceptable: it refused to guess rather than decoding garbage.
        Console.WriteLine($"wrong order      timed out in {clock.ElapsedMilliseconds} ms");
    }
    catch (Exception ex)
    {
        failures.Add($"wrong order: unexpected {ex.GetType().Name}: {ex.Message}");
    }

    if (failures.Count == 0)
    {
        Console.WriteLine("HANDSHAKE PASS");
        Console.WriteLine();
        return 0;
    }

    foreach (string failure in failures)
    {
        Console.WriteLine("HANDSHAKE FAIL  " + failure);
    }

    Console.WriteLine();
    return 1;
}

static byte[] PacketBlock(ulong pts, bool isConfig, bool isKeyFrame, byte[] payload)
{
    byte[] block = new byte[ProtocolConstants.HeaderSize + payload.Length];
    StreamHeader.WritePacketHeader(block, pts, isConfig, isKeyFrame, payload.Length);
    payload.CopyTo(block, ProtocolConstants.HeaderSize);
    return block;
}

// --- links: a USB address must be recognised as wired, a Wi-Fi one as wireless -------------

int Links()
{
    var failures = new List<string>();

    var wired = new LinkMap.Link(IPAddress.Parse("192.168.42.100"), IPAddress.Parse("255.255.255.0"), true, "Remote NDIS");
    var wireless = new LinkMap.Link(IPAddress.Parse("192.168.1.23"), IPAddress.Parse("255.255.255.0"), false, "wlan0");
    LinkMap map = LinkMap.FromLinks([wired, wireless]);

    if (map.TransportOf("192.168.42.129") != "wired")
    {
        failures.Add($"192.168.42.129 classified as '{map.TransportOf("192.168.42.129")}', expected wired");
    }

    if (map.TransportOf("192.168.1.40") != "wifi")
    {
        failures.Add($"192.168.1.40 classified as '{map.TransportOf("192.168.1.40")}', expected wifi");
    }

    if (map.TransportOf("10.9.9.9") != "")
    {
        failures.Add("an address on no local link was given a transport");
    }

    if (!map.HasWiredLink)
    {
        failures.Add("a map containing a USB link did not report one");
    }

    List<IPAddress> hosts = map.SubnetHosts();
    if (hosts.Count == 0 || !hosts[0].Equals(IPAddress.Parse("192.168.42.1")))
    {
        // Wired first: the cable has to be checked before 253 Wi-Fi addresses.
        failures.Add("the sweep does not start on the wired link");
    }

    if (hosts.Contains(wired.Local) || hosts.Contains(wireless.Local))
    {
        failures.Add("the sweep includes this machine's own addresses");
    }

    Console.WriteLine($"links            wired {wired.Local}, wireless {wireless.Local}, {hosts.Count} hosts, wired first");

    if (failures.Count == 0)
    {
        Console.WriteLine("LINKS PASS");
        Console.WriteLine();
        return 0;
    }

    foreach (string failure in failures)
    {
        Console.WriteLine("LINKS FAIL  " + failure);
    }

    Console.WriteLine();
    return 1;
}

// --- disconnect: Stop has to close the socket, and a reconnect has to work ------------------

async Task<int> DisconnectAsync()
{
    string fixture = Fixture("pattern-960x640.h264");
    if (!File.Exists(fixture))
    {
        Console.WriteLine($"DISCONNECT FAIL  fixture not found: {fixture}");
        return 1;
    }

    var failures = new List<string>();

    // Frames = 0 means "keep sending": the session stays open until someone closes it, which is
    // the state the tablet was stuck in.
    await using var device = new SyntheticDevice(new SyntheticDeviceOptions
    {
        Port = 0,
        BindAddress = "127.0.0.1",
        Width = 960,
        Height = 640,
        Frames = 0,
        Fps = 30,
        Source = SyntheticSource.Bitstream,
        BitstreamPath = fixture,
        DeviceName = "Synthetic Pad 8",
    });

    device.Start();

    // --- first session: connect, run, then Stop -----------------------------------------
    using var senderStop = new CancellationTokenSource();
    Task<int> sender1 = Task.Run(async () =>
    {
        try
        {
            return await device.ServeOnceAsync(senderStop.Token);
        }
        catch (Exception)
        {
            // A closed socket on write is the expected outcome of Stop.
            return -1;
        }
    });

    var presenter = new CountingPresenter { ExpectedWidth = 960, ExpectedHeight = 640 };
    var stats = new StreamStats();
    await using MirrorSession session = await MirrorSession.ConnectAsync(
        "127.0.0.1", device.Port, presenter, new MirrorOptions(), stats);

    Task run = session.RunAsync();

    if (!await WaitForAsync(() => stats.FramesDecoded >= 5, 4000))
    {
        failures.Add("the session never decoded a frame, so the disconnect was not exercised");
    }

    var stopClock = Stopwatch.StartNew();
    session.Stop();

    bool ended = await Task.WhenAny(run, Task.Delay(3000)) == run;
    stopClock.Stop();

    if (!ended)
    {
        failures.Add("the read loop did not finish within 3 s of Stop: the socket was not closed");
    }

    if (session.EndReason != "stopped")
    {
        failures.Add($"the session ended with '{session.EndReason}' instead of 'stopped'");
    }

    await session.DisposeAsync();

    bool senderSawClose = await Task.WhenAny(sender1, Task.Delay(3000)) == sender1;
    if (!senderSawClose)
    {
        // This is the tablet's side of the same fact: if the sender is still writing, the tablet
        // would still be showing "Connected".
        failures.Add("the sender is still writing: the connection was never closed from this end");
    }

    senderStop.Cancel();

    // --- second session: the same listener has to accept again --------------------------
    using var secondStop = new CancellationTokenSource();
    Task<int> sender2 = Task.Run(async () =>
    {
        try
        {
            return await device.ServeOnceAsync(secondStop.Token);
        }
        catch (Exception)
        {
            return -1;
        }
    });

    var stats2 = new StreamStats();
    await using MirrorSession again = await MirrorSession.ConnectAsync(
        "127.0.0.1", device.Port, new CountingPresenter { ExpectedWidth = 960, ExpectedHeight = 640 },
        new MirrorOptions(), stats2);

    Task run2 = again.RunAsync();

    if (!await WaitForAsync(() => stats2.FramesDecoded >= 5, 4000))
    {
        failures.Add("a second session decoded no frames: reconnecting after Stop does not work");
    }

    again.Stop();
    await Task.WhenAny(run2, Task.Delay(3000));
    await again.DisposeAsync();
    secondStop.Cancel();

    Console.WriteLine($"stop             run loop ended in {stopClock.ElapsedMilliseconds} ms, sender released");
    Console.WriteLine($"reconnect        second session decoded {stats2.FramesDecoded} frames");

    if (failures.Count == 0)
    {
        Console.WriteLine("DISCONNECT PASS");
        Console.WriteLine();
        return 0;
    }

    foreach (string failure in failures)
    {
        Console.WriteLine("DISCONNECT FAIL  " + failure);
    }

    Console.WriteLine();
    return 1;
}

static async Task<bool> WaitForAsync(Func<bool> condition, int timeoutMs)
{
    var clock = Stopwatch.StartNew();
    while (clock.ElapsedMilliseconds < timeoutMs)
    {
        if (condition())
        {
            return true;
        }

        await Task.Delay(25);
    }

    return condition();
}

// --- state: the settings must survive a restart, and a new build must not reset them ------

int State()
{
    ScrcpySchema schema = ScrcpySchema.LoadEmbedded();
    var failures = new List<string>();
    string directory = Path.Combine(Path.GetTempPath(), "scrappy-state-" + Guid.NewGuid().ToString("N")[..8]);
    string path = Path.Combine(directory, "state.json");

    try
    {
        // 1. A machine with no file is the only case that may get factory defaults.
        var firstStore = new AppStateStore(path);
        LoadedState empty = firstStore.Load();
        if (!empty.IsFresh)
        {
            failures.Add("a missing state file did not read as a fresh install");
        }

        ScrcpyOption flagOption = schema.Options.First(o =>
            o.Kind == OptionKind.Flag && LockedOptions.Find(o.Id) is null);

        var settings = new SettingsStore(schema);
        settings.SetValue("max_size", "2560");
        settings.SetValue("max_fps", "60");
        settings.SetValue("video_bit_rate", "12000000");
        settings.SetValue("video_codec", "h265");

        var autosave = new AppStateAutosave(firstStore, settings, TimeSpan.Zero);
        autosave.Restore(empty);
        autosave.Flush();

        if (!File.Exists(path))
        {
            failures.Add("the first save did not create the state file");
        }

        // 2. The user changes things no default covers.
        settings.SetValue("max_size", "1920");
        settings.SetValue("max_fps", "30");
        settings.SetValue("video_bit_rate", "18000000");
        settings.Set(flagOption.Id, OptionSetting.Flag(true));
        settings.RawArguments = "video_codec_options=i-frame-interval:2";
        autosave.Address = "192.168.1.23:27183";
        autosave.LastDeviceName = "Xiaomi Pad 8";
        autosave.Flush();

        // 3. A new build starts: new objects, same file, no defaults applied.
        LoadedState reloaded = new AppStateStore(path).Load();
        if (reloaded.IsFresh)
        {
            failures.Add("an existing state file still read as a fresh install");
        }

        var restored = new SettingsStore(schema);
        restored.ApplyProfileValues(reloaded.State.Options);
        restored.RawArguments = reloaded.State.RawArguments;

        foreach ((string id, string expected) in new Dictionary<string, string>
                 {
                     ["max_size"] = "1920",
                     ["max_fps"] = "30",
                     ["video_bit_rate"] = "18000000",
                 })
        {
            if (restored.GetValue(id) != expected)
            {
                failures.Add($"{id} came back as '{restored.GetValue(id)}' instead of '{expected}' after a restart");
            }
        }

        if (!restored.GetFlag(flagOption.Id))
        {
            failures.Add($"{flagOption.Id} was lost across a restart");
        }

        if (restored.RawArguments != "video_codec_options=i-frame-interval:2")
        {
            failures.Add("raw arguments were lost across a restart");
        }

        if (reloaded.State.Address != "192.168.1.23:27183")
        {
            failures.Add($"the address came back as '{reloaded.State.Address}'");
        }

        if (reloaded.State.LastDeviceName != "Xiaomi Pad 8")
        {
            failures.Add("the remembered device name was lost");
        }

        if (restored.GetValue("max_size") == "2560")
        {
            failures.Add("a saved setting was overwritten by the factory default");
        }

        autosave.Dispose();

        // 4. A change is written without anyone calling Flush.
        string debouncedPath = Path.Combine(directory, "debounced.json");
        var debouncedStore = new SettingsStore(schema);
        using (var debounced = new AppStateAutosave(
                   new AppStateStore(debouncedPath), debouncedStore, TimeSpan.FromMilliseconds(30)))
        {
            debouncedStore.SetValue("max_fps", "45");
            Thread.Sleep(400);

            if (!File.Exists(debouncedPath))
            {
                failures.Add("a change was not saved automatically");
            }

            if (debounced.SaveCount == 0)
            {
                failures.Add("the autosave never wrote after a change");
            }
        }

        // 5. A damaged file is reported, and is not treated as a fresh install.
        File.WriteAllText(path, "{ this is not json");
        LoadedState damaged = new AppStateStore(path).Load();
        if (damaged.IsFresh)
        {
            failures.Add("a damaged state file was treated as a fresh install");
        }

        if (damaged.Problem is null)
        {
            failures.Add("a damaged state file produced no explanation");
        }

        // 6. The path must be outside the program folder, or an upgrade erases it.
        string defaultPath = AppStateStore.DefaultPath();
        if (defaultPath.StartsWith(AppContext.BaseDirectory, StringComparison.Ordinal))
        {
            failures.Add($"the state file lives in the program folder ({defaultPath})");
        }

        Console.WriteLine($"state file       {defaultPath}");
        Console.WriteLine($"round trip       {reloaded.State.Options.Count} options, address kept, defaults not reapplied");

        if (failures.Count == 0)
        {
            Console.WriteLine("STATE PASS");
            Console.WriteLine();
            return 0;
        }

        foreach (string failure in failures)
        {
            Console.WriteLine("STATE FAIL  " + failure);
        }

        Console.WriteLine();
        return 1;
    }
    finally
    {
        try
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
        catch (IOException)
        {
            // A temporary directory that will not delete is not a test failure.
        }
    }
}

// --- discovery: a device that answers the probe must appear in the picker ----------------

async Task<int> DiscoveryAsync()
{
    var failures = new List<string>();

    // The responder binds the real discovery port, so this also proves the port is free and the
    // broadcast reaches a listener - the two things that actually break discovery on a LAN.
    using var responder = new UdpClient(new IPEndPoint(IPAddress.Any, DeviceDiscovery.DiscoveryPort));
    responder.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);

    string reply = """
        {"scrappy":1,"name":"Xiaomi Pad 8","model":"25091RP04C","port":27183,
         "version":"0.1.6","phase":"Waiting for the PC","width":3200,"height":2136,"refreshHz":144}
        """;

    using var stop = new CancellationTokenSource();
    Task responderTask = Task.Run(async () =>
    {
        var buffer = new byte[256];
        while (!stop.IsCancellationRequested)
        {
            try
            {
                var received = await responder.ReceiveAsync(stop.Token);
                string text = Encoding.UTF8.GetString(received.Buffer);

                // Something that is not a probe must be ignored silently.
                if (text.Contains("not a probe", StringComparison.Ordinal))
                {
                    continue;
                }

                // Garbage first: the parser has to survive another program on the port.
                byte[] junk = "{\"unrelated\":true}"u8.ToArray();
                await responder.SendAsync(junk, received.RemoteEndPoint, stop.Token);

                byte[] payload = Encoding.UTF8.GetBytes(reply);
                await responder.SendAsync(payload, received.RemoteEndPoint, stop.Token);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (SocketException)
            {
                return;
            }
        }
    });

    try
    {
        var discovery = new DeviceDiscovery();
        DiscoveryResult scan = await discovery.ScanAsync(TimeSpan.FromMilliseconds(700));
        IReadOnlyList<DiscoveredDevice> found = scan.Devices;

        // The same tablet can legitimately answer on more than one address (loopback and the LAN
        // interface in this container), so the count is not the assertion - the contents are.
        if (found.Count == 0)
        {
            failures.Add("the probe found no devices, expected the one that answered");
        }
        else
        {
            DiscoveredDevice device = found[0];
            if (device.Name != "Xiaomi Pad 8")
            {
                failures.Add($"the device name came back as '{device.Name}'");
            }

            if (device.Port != 27183)
            {
                failures.Add($"the port came back as {device.Port}");
            }

            if (device.AddressText != $"{device.Address}:27183")
            {
                failures.Add("the picker's address is not host:port");
            }

            if (device.DisplayName.Length == 0)
            {
                failures.Add("the device has no display name");
            }

            Console.WriteLine($"probe            {found.Count} address(es), first: {device.DisplayName} at {device.AddressText}");
            Console.WriteLine($"detail line      {device.Detail}");
        }
    }
    finally
    {
        stop.Cancel();
        try
        {
            await responderTask;
        }
        catch (OperationCanceledException)
        {
            // Expected.
        }
    }

    if (failures.Count == 0)
    {
        Console.WriteLine("DISCOVERY PASS");
        Console.WriteLine();
        return 0;
    }

    foreach (string failure in failures)
    {
        Console.WriteLine("DISCOVERY FAIL  " + failure);
    }

    Console.WriteLine();
    return 1;
}

async Task<int> PipelineAsync()
{
    string fixture = Fixture("pattern-960x640.h264");
    if (!File.Exists(fixture))
    {
        Console.WriteLine($"PIPELINE FAIL  fixture not found: {fixture}");
        return 1;
    }

    const int expectedWidth = 960;
    const int expectedHeight = 640;
    const int frameCount = 120;
    const int fps = 60;

    await using var device = new SyntheticDevice(new SyntheticDeviceOptions
    {
        Port = 0,
        BindAddress = "127.0.0.1",
        Width = expectedWidth,
        Height = expectedHeight,
        Frames = frameCount,
        Fps = fps,
        Source = SyntheticSource.Bitstream,
        BitstreamPath = fixture,
        DeviceName = "Synthetic Pad 8",
    });

    device.Start();
    Task<int> sender = device.ServeOnceAsync();

    var presenter = new CountingPresenter { ExpectedWidth = expectedWidth, ExpectedHeight = expectedHeight };
    var stats = new StreamStats();
    await using MirrorSession session = await MirrorSession.ConnectAsync(
        "127.0.0.1", device.Port, presenter, new MirrorOptions(), stats);

    var clock = Stopwatch.StartNew();
    await session.RunAsync();
    clock.Stop();
    await sender;

    var failures = new List<string>();

    if (session.DeviceName != "Synthetic Pad 8")
    {
        failures.Add($"device name: got '{session.DeviceName}'");
    }

    if (stats.FramesReceived != frameCount)
    {
        failures.Add($"frames received: expected {frameCount}, got {stats.FramesReceived}");
    }

    if (stats.FramesDecoded != frameCount)
    {
        // The decoder is flushed at end of stream, so this is exact, not approximate: every access
        // unit that arrived must come out as a frame.
        failures.Add($"frames decoded: expected {frameCount}, got {stats.FramesDecoded}");
    }

    if (stats.DecodeErrors != 0)
    {
        failures.Add($"decode errors: {stats.DecodeErrors}");
    }

    if (stats.Fps is < fps * 0.8 or > fps * 1.2)
    {
        failures.Add($"throughput: expected about {fps} fps over the wire, measured {stats.Fps:F1}");
    }

    if (presenter.Presented == 0)
    {
        failures.Add("no frame reached the presenter");
    }

    if (presenter.LastMeanLuminance < 8)
    {
        failures.Add($"presented frames look black (mean luminance {presenter.LastMeanLuminance:F1})");
    }

    if (presenter.FirstChecksum == presenter.LastChecksum)
    {
        failures.Add("every presented frame is identical");
    }

    Console.WriteLine($"device name      {session.DeviceName}");
    Console.WriteLine($"geometry         {stats.GeometryWidth}x{stats.GeometryHeight}");
    Console.WriteLine($"backend          {stats.Backend}");
    Console.WriteLine($"wire             {stats.FramesReceived} frames, {stats.BytesReceived / 1024.0 / 1024.0:F1} MiB");
    Console.WriteLine($"decoded          {stats.FramesDecoded} frames, {stats.DecodeMsAverage:F2} ms average");
    Console.WriteLine($"presented        {presenter.Presented} frames, mean luminance {presenter.LastMeanLuminance:F1}");
    Console.WriteLine($"wall clock       {clock.ElapsedMilliseconds} ms");
    Console.WriteLine($"end              {session.EndReason}");

    if (failures.Count == 0)
    {
        Console.WriteLine("PIPELINE PASS");
        Console.WriteLine();
        return 0;
    }

    foreach (string failure in failures)
    {
        Console.WriteLine("PIPELINE FAIL  " + failure);
    }

    Console.WriteLine();
    return 1;
}


// --- advisor: Check tablet must never throw, whatever the tablet reports ---------------------
//
// This is the crash that killed the PC app the first time the button was pressed on a tablet
// without a hardware H.265 encoder: the advisor looked up a preset called "Low bitrate" that does
// not exist, and First() threw out of an async void handler, which takes the process with it.

int Advisor()
{
    var failures = new List<string>();
    ScrcpySchema schema = ScrcpySchema.LoadEmbedded();

    int cases = 0;

    foreach (bool hevc in new[] { true, false })
    foreach (bool avc in new[] { true, false })
    foreach (int width in new[] { 0, 1280, 1920, 2560, 3200 })
    {
        cases++;

        var facts = new DeviceFacts
        {
            Name = "Xiaomi Pad 8",
            Model = "25091RP04C",
            Android = "15",
            Width = width,
            Height = width * 2 / 3,
            RefreshHz = 144,
            RefreshModes = [60, 120, 144],
            H264Hardware = avc,
            H265Hardware = hevc,
        };

        string caseName = $"hevc={hevc} h264={avc} width={width}";
        PresetAdvisor.Advice advice;

        try
        {
            advice = PresetAdvisor.Recommend(facts);
        }
        catch (Exception ex)
        {
            failures.Add($"{caseName}: Check tablet would crash the app ({ex.GetType().Name}: {ex.Message})");
            continue;
        }

        if (string.IsNullOrWhiteSpace(advice.Preset.Label))
        {
            failures.Add($"{caseName}: the suggestion has no name");
        }

        // A named preset has to be one the picker can actually show, or the window says "Custom"
        // straight after applying the app's own suggestion.
        if (advice.Preset.Label != PresetAdvisor.SuggestedLabel &&
            !DailyPresets.Quality.Any(p => p.Label == advice.Preset.Label))
        {
            failures.Add($"{caseName}: suggested preset '{advice.Preset.Label}' is not in the preset list");
        }

        if (!advice.Preset.Values.TryGetValue("video_codec", out string? codec))
        {
            failures.Add($"{caseName}: the suggestion does not choose a codec");
        }
        else if (codec != (hevc ? "h265" : "h264"))
        {
            failures.Add($"{caseName}: suggested {codec} but the tablet's hardware is {(hevc ? "H.265" : "H.264")}");
        }

        if (!advice.Preset.Values.ContainsKey("max_size") || !advice.Preset.Values.ContainsKey("max_fps"))
        {
            failures.Add($"{caseName}: the suggestion leaves size or frame rate unset");
        }

        if (string.IsNullOrWhiteSpace(advice.Reason))
        {
            failures.Add($"{caseName}: the suggestion has no reason");
        }

        // Applying it must work and must read back: a suggestion that cannot be applied is worse
        // than none, because the button claims it did something.
        var store = new SettingsStore(schema);
        try
        {
            DailyPresets.Apply(store, advice.Preset);
            foreach ((string id, string expected) in advice.Preset.Values)
            {
                if (store.GetValue(id) != expected)
                {
                    failures.Add($"{caseName}: {id} came back as '{store.GetValue(id)}' instead of '{expected}'");
                }
            }
        }
        catch (Exception ex)
        {
            failures.Add($"{caseName}: applying the suggestion threw {ex.GetType().Name}: {ex.Message}");
        }
    }

    Console.WriteLine($"cases            {cases} combinations of hardware and panel size");
    Console.WriteLine($"h.265 present    suggestion stays H.265 and keeps a named preset");
    Console.WriteLine($"no h.265         suggestion switches to H.264 instead of throwing");

    if (failures.Count == 0)
    {
        Console.WriteLine("ADVISOR PASS");
        Console.WriteLine();
        return 0;
    }

    foreach (string failure in failures.Take(8))
    {
        Console.WriteLine("ADVISOR FAIL  " + failure);
    }

    if (failures.Count > 8)
    {
        Console.WriteLine($"ADVISOR FAIL  ... and {failures.Count - 8} more");
    }

    Console.WriteLine();
    return 1;
}

// --- plan: the cable is swept even when the tablet already answered over Wi-Fi --------------
//
// This is the bug behind "I have the tablet wired and Scan does not get that it is wired": the
// probe found the Wi-Fi address, the scan stopped there, and the wired address was never looked
// for - because the sweep only ran when the probe came back empty.

int Plan()
{
    var failures = new List<string>();

    LinkMap both = LinkMap.FromLinks([
        new LinkMap.Link(IPAddress.Parse("192.168.42.100"), IPAddress.Parse("255.255.255.0"), true, "Remote NDIS"),
        new LinkMap.Link(IPAddress.Parse("192.168.1.23"), IPAddress.Parse("255.255.255.0"), false, "Wi-Fi"),
    ]);

    var overWifi = new DiscoveredDevice
    {
        Address = "192.168.1.44",
        Transport = "wifi",
        Name = "Xiaomi Pad 8",
    };

    var overCable = new DiscoveredDevice
    {
        Address = "192.168.42.129",
        Transport = "wired",
        Name = "Xiaomi Pad 8",
    };

    SweepPlan afterWifiHit = SweepPlan.For(both, [overWifi]);
    if (!afterWifiHit.Wired)
    {
        failures.Add("a probe hit over Wi-Fi stopped the cable from being swept");
    }

    if (afterWifiHit.Wireless)
    {
        failures.Add("the wireless networks were swept again after the probe already found the tablet");
    }

    SweepPlan afterCableHit = SweepPlan.For(both, [overCable]);
    if (afterCableHit.Wired)
    {
        failures.Add("the cable was swept again although the tablet already answered on it");
    }

    SweepPlan nothing = SweepPlan.For(both, []);
    if (nothing is not { Wired: true, Wireless: true })
    {
        failures.Add("a scan that found nothing did not sweep both links");
    }

    LinkMap wirelessOnly = LinkMap.FromLinks([
        new LinkMap.Link(IPAddress.Parse("192.168.1.23"), IPAddress.Parse("255.255.255.0"), false, "Wi-Fi"),
    ]);

    if (SweepPlan.For(wirelessOnly, []).Wired)
    {
        failures.Add("a machine with no cable planned a wired sweep");
    }

    // Two routes to one tablet are two entries, and the cable comes first: they are different
    // experiences and the user is being asked to choose between them.
    IReadOnlyList<DiscoveredDevice> merged = DeviceDiscovery.Merge([overWifi], [overCable]);
    if (merged.Count != 2)
    {
        failures.Add($"a tablet on both links produced {merged.Count} entries, expected 2");
    }
    else if (!merged[0].IsWired)
    {
        failures.Add("the wired entry was not listed first");
    }

    // The same address from both mechanisms is one entry, and it keeps the name only one of them has.
    var sweptWithoutName = overCable with { Name = "", Source = "scan" };
    IReadOnlyList<DiscoveredDevice> sameAddress = DeviceDiscovery.Merge([overCable], [sweptWithoutName]);
    if (sameAddress.Count != 1 || sameAddress[0].Name != "Xiaomi Pad 8")
    {
        failures.Add("one address from both mechanisms produced a duplicate or lost its name");
    }

    // A 0.1.8 reply lists every address the tablet holds; both are kept when both are reachable.
    byte[] reply = Encoding.UTF8.GetBytes("""
        {"scrappy":1,"name":"Xiaomi Pad 8","phase":"Waiting for the PC","version":"0.1.8","port":27183,
         "wiredReady":true,"addresses":[{"address":"192.168.42.129:27183","link":"Wired"},
                                        {"address":"192.168.1.44:27183","link":"Wi-Fi"}]}
        """);

    List<DiscoveredDevice> parsed = DeviceDiscovery
        .ParseReplies(reply, IPAddress.Parse("192.168.1.44"), both)
        .ToList();

    if (parsed.Count != 2)
    {
        failures.Add($"a reply listing two addresses produced {parsed.Count} devices, expected 2");
    }
    else
    {
        if (parsed.Count(d => d.IsWired) != 1)
        {
            failures.Add("the reply's tethered address was not classified as wired");
        }

        if (!parsed.Any(d => d.Transport == "wifi"))
        {
            failures.Add("the reply's Wi-Fi address was not classified as wifi");
        }
    }

    // A tablet also reports addresses this PC has no link to - a mobile data address, an interface
    // on another network. Listing those would produce entries that cannot be connected to.
    byte[] elsewhere = Encoding.UTF8.GetBytes("""
        {"scrappy":1,"name":"Xiaomi Pad 8","phase":"Waiting for the PC","version":"0.1.8","port":27183,
         "addresses":[{"address":"10.9.9.9:27183","link":"Other"}]}
        """);

    List<DiscoveredDevice> filtered = DeviceDiscovery
        .ParseReplies(elsewhere, IPAddress.Parse("192.168.1.44"), both)
        .ToList();

    if (filtered.Count != 1 || filtered[0].Address != "192.168.1.44")
    {
        failures.Add($"an address on no local link was listed instead of the datagram source " +
                     $"({filtered.Count} entries: {string.Join(", ", filtered.Select(d => d.Address))})");
    }

    // Nothing usable at all still yields the sender, so the picker is never empty for no reason.
    List<DiscoveredDevice> fallback = DeviceDiscovery
        .ParseReplies(elsewhere, IPAddress.Parse("10.0.0.9"), both)
        .ToList();

    if (fallback.Count != 1 || fallback[0].Address != "10.0.0.9")
    {
        failures.Add("a reply from an address this machine cannot place was dropped entirely");
    }

    Console.WriteLine($"sweep plan       cable swept after a Wi-Fi hit, both swept when nothing answered");
    Console.WriteLine($"merge            {merged.Count} entries, wired first; one address stays one entry");
    Console.WriteLine($"reply            {parsed.Count} addresses from one datagram, {parsed.Count(d => d.IsWired)} of them wired");

    if (failures.Count == 0)
    {
        Console.WriteLine("PLAN PASS");
        Console.WriteLine();
        return 0;
    }

    foreach (string failure in failures)
    {
        Console.WriteLine("PLAN FAIL  " + failure);
    }

    Console.WriteLine();
    return 1;
}

// --- backlog: a receiver that cannot keep up skips to the next keyframe, it does not lag ------
//
// The device here is not pacing itself (Fps = 0), and the presenter takes 2 ms per frame: a stand-in
// for a PC that cannot decode this size in real time. Without the skip the socket fills with frames
// that are already late and the delay grows for the whole session - which is what "the frames come
// in extremely slowly" actually was.

async Task<int> BacklogAsync()
{
    string fixture = Fixture("pattern-960x640.h264");
    if (!File.Exists(fixture))
    {
        Console.WriteLine($"BACKLOG FAIL  fixture not found: {fixture}");
        return 1;
    }

    const int frames = 1500;

    await using var device = new SyntheticDevice(new SyntheticDeviceOptions
    {
        Port = 0,
        BindAddress = "127.0.0.1",
        Width = 960,
        Height = 640,
        Frames = frames,
        Fps = 0,
        Source = SyntheticSource.Bitstream,
        BitstreamPath = fixture,
        DeviceName = "Synthetic Pad 8",
    });

    device.Start();
    Task<int> sender = device.ServeOnceAsync();

    var presenter = new CountingPresenter
    {
        ExpectedWidth = 960,
        ExpectedHeight = 640,
        DelayPerFrameMs = 2,
    };
    var stats = new StreamStats();

    await using MirrorSession session = await MirrorSession.ConnectAsync(
        "127.0.0.1",
        device.Port,
        presenter,
        new MirrorOptions { Request = new SessionRequest { Bitrate = 4_000_000, Fps = 60 } },
        stats);

    var clock = Stopwatch.StartNew();
    using var cap = new CancellationTokenSource(TimeSpan.FromSeconds(30));
    await session.RunAsync(cap.Token);
    clock.Stop();
    await sender;

    var failures = new List<string>();

    if (stats.FramesReceived < frames / 2)
    {
        failures.Add($"only {stats.FramesReceived} of {frames} access units arrived, so the test never built a backlog");
    }

    if (stats.FramesSkippedByReceiver == 0)
    {
        failures.Add("the receiver never skipped: a 2 ms/frame consumer kept up with an unpaced sender");
    }

    if (stats.FramesDecoded >= stats.FramesReceived)
    {
        failures.Add($"decoded {stats.FramesDecoded} of {stats.FramesReceived}: nothing was skipped before decoding");
    }

    // The numbers that matter. "Typical" is what the user sits in front of; without the skip it
    // would climb for the whole session, because nothing ever removes a frame from a TCP stream.
    if (stats.QueueDelayMs > 300)
    {
        failures.Add($"typical delay was {stats.QueueDelayMs:F0} ms of video: the backlog was not capped");
    }

    // A peak above this means the socket was allowed to fill before anything was skipped. The
    // unpaced sender makes the first second burstier than a real encoder at 60 fps ever is.
    if (stats.QueueDelayMsPeak > 1500)
    {
        failures.Add($"delay peaked at {stats.QueueDelayMsPeak:F0} ms of video: the source outran the receiver unchecked");
    }

    // Skipping to a keyframe must not leave the decoder decoding against a frame it never saw.
    if (stats.DecodeErrors != 0)
    {
        failures.Add($"{stats.DecodeErrors} decode errors: skipping did not resynchronise at a keyframe");
    }

    Console.WriteLine($"arrived          {stats.FramesReceived} access units in {clock.ElapsedMilliseconds} ms");
    Console.WriteLine($"skipped          {stats.FramesSkippedByReceiver} late frames, {stats.FramesDecoded} decoded");
    Console.WriteLine($"worst lag        {stats.QueueDelayMsPeak:F0} ms in the socket ({stats.QueueDelayMs:F0} ms typical)");
    Console.WriteLine($"decode errors    {stats.DecodeErrors}");

    if (failures.Count == 0)
    {
        Console.WriteLine("BACKLOG PASS");
        Console.WriteLine();
        return 0;
    }

    foreach (string failure in failures)
    {
        Console.WriteLine("BACKLOG FAIL  " + failure);
    }

    Console.WriteLine();
    return 1;
}

// --- postmortem: "the device closed the stream" is replaced by what the tablet says -----------

async Task<int> PostmortemAsync()
{
    var failures = new List<string>();

    // What a tablet reports when its encoder refused to start: exactly the situation that used to
    // show as a calm "Waiting for the PC" on the tablet and a dead stream on the PC.
    using (var failed = new StatusResponder("""
               {"scrappy":1,"name":"Xiaomi Pad 8","phase":"Failed","version":"0.1.8",
                "message":"Starting the encoder: IllegalStateException: createVirtualDisplay failed",
                "lastError":"Starting the encoder: IllegalStateException: createVirtualDisplay failed"}
               """))
    {
        string explained = await EndExplanation.DescribeAsync(
            "127.0.0.1", failed.Port, "the device closed the stream");

        if (!explained.Contains("createVirtualDisplay", StringComparison.Ordinal))
        {
            failures.Add($"the tablet's own error was not repeated: '{explained}'");
        }
    }

    // A tablet that went straight back to listening has nothing to say, and saying so is the point.
    using (var listening = new StatusResponder("""
               {"scrappy":1,"name":"Xiaomi Pad 8","phase":"Waiting for the PC","version":"0.1.8",
                "message":"Waiting for the PC on port 27183","lastError":""}
               """))
    {
        string explained = await EndExplanation.DescribeAsync(
            "127.0.0.1", listening.Port, "the device closed the stream");

        if (!explained.Contains("Waiting for the PC", StringComparison.Ordinal))
        {
            failures.Add($"a tablet back to listening was not described as such: '{explained}'");
        }
    }

    // Nothing listening at all means the app or its service is gone, which is different again.
    int deadPort;
    using (var probe = new TcpListener(IPAddress.Loopback, 0))
    {
        probe.Start();
        deadPort = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
    }

    string dead = await EndExplanation.DescribeAsync("127.0.0.1", deadPort, "the device closed the stream", 300);
    if (!dead.Contains("not answering", StringComparison.Ordinal))
    {
        failures.Add($"a tablet that stopped listening was not described as gone: '{dead}'");
    }

    // The original reason must survive whatever the tablet says: it is still the fact.
    if (!dead.StartsWith("the device closed the stream", StringComparison.Ordinal))
    {
        failures.Add("the original reason was dropped from the explanation");
    }

    Console.WriteLine("encoder failure  reported in the tablet's words");
    Console.WriteLine("back to listening recognised as an unfinished session");
    Console.WriteLine("no answer        recognised as the tablet being gone");

    if (failures.Count == 0)
    {
        Console.WriteLine("POSTMORTEM PASS");
        Console.WriteLine();
        return 0;
    }

    foreach (string failure in failures)
    {
        Console.WriteLine("POSTMORTEM FAIL  " + failure);
    }

    Console.WriteLine();
    return 1;
}

Task<int> DecodeThroughputAsync(string fixture)
{
    if (!File.Exists(fixture))
    {
        Console.WriteLine($"decode: fixture not found: {fixture}");
        return Task.FromResult(1);
    }

    byte[] stream = File.ReadAllBytes(fixture);
    IReadOnlyList<AccessUnit> units = AnnexB.SplitAccessUnits(stream);

    FFmpegRuntime.EnsureLocated();
    using var decoder = new H264Decoder(lowLatency: false);
    decoder.SetParameterSets(AnnexB.ExtractParameterSets(stream));

    int decoded = 0;
    var clock = Stopwatch.StartNew();
    foreach (AccessUnit unit in units)
    {
        decoder.Decode(unit.Payload, 0, 0, frame =>
        {
            decoded++;
            frame.Release();
        });
    }

    decoder.Flush(frame =>
    {
        decoded++;
        frame.Release();
    });

    clock.Stop();
    double fps = decoded * 1000.0 / Math.Max(1, clock.ElapsedMilliseconds);

    Console.WriteLine($"fixture          {Path.GetFileName(fixture)} ({stream.Length / 1024.0 / 1024.0:F1} MiB)");
    Console.WriteLine($"frames           {decoded} of {units.Count}");
    Console.WriteLine($"decode           {clock.ElapsedMilliseconds} ms, {fps:F1} fps, {clock.ElapsedMilliseconds * 1.0 / Math.Max(1, decoded):F2} ms/frame");
    Console.WriteLine($"threads          {decoder.ThreadCount}");
    Console.WriteLine($"backend          {decoder.Backend}");
    Console.WriteLine();
    Console.WriteLine("informational: this box has no GPU and few cores; do not read it as a product number.");
    return Task.FromResult(decoded == units.Count ? 0 : 1);
}

string Fixture(string name) => Path.Combine(root, "testdata", name);

static string FindRepositoryRoot()
{
    // Walk up from the binary until the schema directory shows up; works whether the tool runs
    // from bin/Debug/net8.0 or from a publish folder.
    string? directory = AppContext.BaseDirectory;
    for (int i = 0; i < 8 && directory is not null; i++)
    {
        if (Directory.Exists(Path.Combine(directory, "schema")) &&
            Directory.Exists(Path.Combine(directory, "src")))
        {
            return directory;
        }

        directory = Path.GetDirectoryName(directory);
    }

    return Directory.GetCurrentDirectory();
}

/// <summary>A one-endpoint stand-in for the tablet's /status, for the explanation tests.</summary>
sealed class StatusResponder : IDisposable
{
    private readonly TcpListener _listener;
    private readonly string _body;
    private readonly CancellationTokenSource _stop = new();

    public StatusResponder(string body)
    {
        _body = body;
        _listener = new TcpListener(IPAddress.Loopback, 0);
        _listener.Start();
        _ = Task.Run(ServeAsync);
    }

    public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;

    private async Task ServeAsync()
    {
        while (!_stop.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await _listener.AcceptTcpClientAsync(_stop.Token).ConfigureAwait(false);
            }
            catch (Exception)
            {
                return;
            }

            _ = Task.Run(async () =>
            {
                using (client)
                {
                    try
                    {
                        byte[] response = Encoding.UTF8.GetBytes(
                            "HTTP/1.1 200 OK\r\nContent-Type: application/json\r\n" +
                            $"Content-Length: {Encoding.UTF8.GetByteCount(_body)}\r\nConnection: close\r\n\r\n{_body}");

                        await client.GetStream().WriteAsync(response, _stop.Token).ConfigureAwait(false);
                    }
                    catch (Exception)
                    {
                        // The client gave up; nothing to report.
                    }
                }
            });
        }
    }

    public void Dispose()
    {
        _stop.Cancel();
        _listener.Stop();
        _stop.Dispose();
    }
}
