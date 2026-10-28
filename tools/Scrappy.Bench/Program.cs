using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Text;
using Scrappy.Discovery;
using Scrappy.Settings;

namespace Scrappy.Bench;

/// <summary>
/// Startup and discovery micro-benchmarks: the parts of the app that run before the first frame is
/// on screen, plus the LAN sweep that runs on every Scan.
///
/// Everything here is deterministic and local. The state file is written into a temp directory, the
/// device that answers /status is an HttpListener on 127.0.0.1, and the subnet being classified is a
/// made-up /24 built with <see cref="LinkMap.FromLinks"/> - nothing touches the real network, so two
/// runs on the same machine are comparable.
///
///   dotnet run -c Release --project tools/Scrappy.Bench                all scenarios
///   dotnet run -c Release --project tools/Scrappy.Bench -- schema-load  one scenario
///   dotnet run -c Release --project tools/Scrappy.Bench -- check        behaviour parity only
/// </summary>
internal static class Program
{
    private const int Iterations = 7;
    private const int Warmup = 2;

    private static async Task<int> Main(string[] args)
    {
        // Flags may come before or after the scenario name, so the command is the first argument that
        // is not part of one: "all --iterations 5" and "--iterations 5 all" mean the same thing.
        var words = new List<string>();
        int iterations = Iterations;
        bool cold = false;

        for (int i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                // --iterations N lets a slower (or faster) machine ask for more samples.
                case "--iterations" when i + 1 < args.Length && int.TryParse(args[i + 1], out int requested):
                    iterations = Math.Max(3, requested);
                    i++;
                    break;
                case "--cold":
                    cold = true;
                    break;
                default:
                    words.Add(args[i]);
                    break;
            }
        }

        string command = words.Count > 0 ? words[0] : "all";

        if (command is "check" or "parity")
        {
            return Checks.Run();
        }

        Dictionary<string, Scenario> scenarios = Scenarios();

        // --cold runs a scenario exactly once in a fresh process and prints how long that one call
        // took. The app loads the schema and the state file once per launch, so the first call is the
        // number that reaches the user; the warmed-up medians below describe the second launch only.
        // Run it five times and take the median.
        if (cold)
        {
            if (words.Count < 1 || !scenarios.TryGetValue(words[0], out Scenario? only))
            {
                Console.WriteLine("--cold needs a scenario name: --cold schema-load");
                return 2;
            }

            long before = GC.GetTotalAllocatedBytes(precise: true);
            long start = Stopwatch.GetTimestamp();
            string value = await only.Body(CancellationToken.None).ConfigureAwait(false);
            long end = Stopwatch.GetTimestamp();
            long allocated = GC.GetTotalAllocatedBytes(precise: true) - before;

            Console.WriteLine(
                $"COLD|{only.Name}|ms={((end - start) * 1000.0 / Stopwatch.Frequency).ToString("0.000", CultureInfo.InvariantCulture)}|" +
                $"alloc_kb={(allocated / 1024.0).ToString("0.0", CultureInfo.InvariantCulture)}|digest={value}");
            return 0;
        }

        if (command is "-h" or "--help" or "help")
        {
            Usage(scenarios);
            return 2;
        }

        List<Scenario> selected = command == "all"
            ? scenarios.Values.ToList()
            : command.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(name => scenarios.TryGetValue(name, out Scenario? scenario)
                    ? scenario
                    : throw new ArgumentException($"unknown scenario '{name}'; run with --help"))
                .ToList();

        Console.WriteLine($"# machine  {Environment.MachineName}  {RuntimeInformation()}");
        Console.WriteLine($"# runtime  {Environment.Version}  {System.Runtime.InteropServices.RuntimeInformation.OSDescription}");
        Console.WriteLine($"# protocol warmup={Warmup} iterations={iterations} (median/p95 of the measured ones)");
        Console.WriteLine();

        foreach (Scenario scenario in selected)
        {
            int exitCode = await RunScenarioAsync(scenario, iterations).ConfigureAwait(false);
            if (exitCode != 0)
            {
                return exitCode;
            }
        }

        return 0;
    }

    private static async Task<int> RunScenarioAsync(Scenario scenario, int iterations)
    {
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMinutes(5));

        string digest;
        Harness.Sample sample;

        // A digest of what the scenario produced, so a before/after comparison proves the workload
        // did the same work and produced the same answer - not just that it was quicker.
        string[] seen = new string[iterations];
        int index = 0;

        try
        {
            sample = await Harness.MeasureAsync(
                async token =>
                {
                    string value = await scenario.Body(token).ConfigureAwait(false);
                    if (index < seen.Length)
                    {
                        seen[index++] = value;
                    }
                },
                iterations,
                Warmup,
                cancellation.Token).ConfigureAwait(false);

            digest = seen.Where(s => s is not null).Distinct(StringComparer.Ordinal).ToList() switch
            {
                { Count: 0 } => "-",
                { Count: 1 } values => values[0],
                var many => $"UNSTABLE({many.Count})",
            };
        }
        catch (Exception ex)
        {
            Console.WriteLine($"BENCH|{scenario.Name}|ERROR|{ex.GetType().Name}: {ex.Message.Replace('|', '/')}");
            return 1;
        }

        Console.WriteLine(string.Join('|',
            "BENCH",
            scenario.Name,
            $"n={iterations}",
            $"median_ms={sample.MedianMs.ToString("0.000", CultureInfo.InvariantCulture)}",
            $"p95_ms={sample.P95Ms.ToString("0.000", CultureInfo.InvariantCulture)}",
            $"min_ms={sample.MinMs.ToString("0.000", CultureInfo.InvariantCulture)}",
            $"alloc_kb={sample.AllocKb.ToString("0.0", CultureInfo.InvariantCulture)}",
            $"gc={sample.Gen0}/{sample.Gen1}/{sample.Gen2}",
            $"digest={digest}"));

        if (digest.StartsWith("UNSTABLE", StringComparison.Ordinal))
        {
            Console.WriteLine($"# {scenario.Name} returned more than one answer - the workload is not deterministic:");
            foreach (string value in seen.Distinct(StringComparer.Ordinal))
            {
                Console.WriteLine($"#   {value}");
            }

            return 1;
        }

        return 0;
    }

    private static Dictionary<string, Scenario> Scenarios()
    {
        // One temp directory for everything the harness needs on disk; deleted on exit by the OS.
        string temp = Path.Combine(Path.GetTempPath(), "scrappy-bench-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(temp);

        string statePath = Path.Combine(temp, "state.json");
        SeedStateFile(statePath);

        LinkMap? syntheticLinks = null;
        var responder = new StatusResponder();

        Scenario[] list =
        [
            new("schema-load",
                "ScrcpySchema.LoadEmbedded(): the 47 KB option table plus the explanation table",
                _ => Task.FromResult(Fingerprint.Schema(ScrcpySchema.LoadEmbedded()))),

            new("startup-core",
                "the non-UI part of startup: schema + settings store + profile values + state file",
                _ =>
                {
                    ScrcpySchema schema = ScrcpySchema.LoadEmbedded();
                    var settings = new SettingsStore(schema);
                    settings.ApplyProfileValues(RealisticProfile());
                    settings.RawArguments = "--video-codec-options=profile=1";
                    LoadedState loaded = new AppStateStore(statePath).Load();
                    settings.SetValue("max_fps", loaded.State.Options.Count > 0 ? "60" : "30");
                    return Task.FromResult(
                        $"options={schema.Options.Count};set={settings.ConfiguredCount};address={loaded.State.Address}");
                }),

            new("appstate-load",
                "AppStateStore.Load() on a realistic 8 KB state file",
                _ =>
                {
                    LoadedState loaded = new AppStateStore(statePath).Load();
                    return Task.FromResult(
                        $"existed={loaded.Existed};options={loaded.State.Options.Count};address={loaded.State.Address}");
                }),

            new("linkmap-read",
                "LinkMap.Read(): one enumeration of the machine's IPv4 interfaces",
                _ => Task.FromResult(Fingerprint.Links(LinkMap.Read()))),

            new("nic-enumerate",
                "NetworkInterface.GetAllNetworkInterfaces() alone - the floor under LinkMap.Read()",
                _ => Task.FromResult(
                    System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces().Length
                        .ToString(CultureInfo.InvariantCulture))),

            new("nic-properties",
                "name, description, status and addresses for every interface, as LinkMap.Read() reads them",
                _ =>
                {
                    int addresses = 0;
                    var sb = new StringBuilder();
                    foreach (System.Net.NetworkInformation.NetworkInterface nic
                             in System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces())
                    {
                        sb.Append(nic.Name).Append('/').Append(nic.Description).Append('/')
                            .Append(nic.OperationalStatus).Append(';');
                        addresses += nic.GetIPProperties().UnicastAddresses.Count;
                    }

                    return Task.FromResult($"{addresses};{sb.Length}");
                }),

            new("probe-prep",
                "the link work one scan does before its first datagram: LinkMap.Read() + BroadcastTargets()",
                _ =>
                {
                    LinkMap links = LinkMap.Read();
                    return Task.FromResult(
                        string.Join(',', DeviceDiscovery.BroadcastTargets(links).Select(e => e.ToString())) +
                        $";networks={links.NetworkCount}");
                }),

            new("subnet-hosts",
                "LinkMap.SubnetHosts() over a synthetic /24",
                _ => Task.FromResult(Synthetic(ref syntheticLinks).SubnetHosts(false).Count.ToString(CultureInfo.InvariantCulture))),

            new("transport-batch",
                "LinkMap.TransportOf() for all 254 addresses of a /24 - link classification",
                _ =>
                {
                    LinkMap links = Synthetic(ref syntheticLinks);
                    int wired = 0, wifi = 0, none = 0;
                    for (int host = 1; host < 255; host++)
                    {
                        switch (links.TransportOf($"192.168.{WifiSubnet}.{host}"))
                        {
                            case "wired": wired++; break;
                            case "wifi": wifi++; break;
                            default: none++; break;
                        }
                    }

                    return Task.FromResult($"wired={wired};wifi={wifi};none={none}");
                }),

            new("parse-replies",
                "DeviceDiscovery.ParseReplies() for one 0.2.x answer carrying two addresses",
                _ =>
                {
                    LinkMap links = Synthetic(ref syntheticLinks);
                    IReadOnlyList<DiscoveredDevice> devices = DeviceDiscovery
                        .ParseReplies(ReplyPayload(), IPAddress.Parse($"192.168.{WifiSubnet}.77"), links)
                        .ToList();
                    return Task.FromResult(
                        $"count={devices.Count};addresses={string.Join('+', devices.Select(d => d.Address))}");
                }),

            new("probe-http",
                "DeviceProbe.QueryAsync() against a local /status responder - one device per sweep",
                async token =>
                {
                    DeviceFacts? facts = await DeviceProbe.QueryAsync("127.0.0.1", responder.Port, 1400, token)
                        .ConfigureAwait(false);
                    return facts is null ? "null" : $"{facts.Name}/{facts.Width}x{facts.Height}@{facts.RefreshHz}";
                }),
        ];

        return list.ToDictionary(s => s.Name, StringComparer.Ordinal);
    }

    private const int WifiSubnet = 1;

    /// <summary>A two-link map: one Wi-Fi /24 and the fixed Android tethering subnet.</summary>
    private static LinkMap Synthetic(ref LinkMap? cached) => cached ??= LinkMap.FromLinks(
        [
            new LinkMap.Link(IPAddress.Parse($"192.168.{WifiSubnet}.10"), IPAddress.Parse("255.255.255.0"), false, "Wi-Fi"),
            new LinkMap.Link(IPAddress.Parse("192.168.42.129"), IPAddress.Parse("255.255.255.0"), true, "Remote NDIS"),
        ]);

    private static byte[] ReplyPayload() => Encoding.UTF8.GetBytes($$"""
        {"scrappy":1,"name":"25091RP04C","model":"25091RP04C","android":"15","phase":"Waiting for the PC",
         "message":"","lastError":"","wiredReady":true,"version":"0.2.1","width":2560,"height":1708,
         "refreshHz":120,"port":27183,"addresses":["192.168.42.129"],"refreshModes":[60,90,120]}
        """);

    /// <summary>The settings a real installation carries, about the size of a saved profile.</summary>
    private static Dictionary<string, string> RealisticProfile() => new(StringComparer.Ordinal)
    {
        ["max_size"] = "2560",
        ["max_fps"] = "60",
        ["video_bit_rate"] = "12000000",
        ["video_codec"] = "h265",
        ["video_buffer"] = "30",
        ["audio_codec"] = "aac",
        ["audio_bit_rate"] = "128000",
        ["record_format"] = "mkv",
        ["hwdec"] = "auto",
        ["audio_source"] = "playback",
        ["audio_dup"] = "true",
        ["display_id"] = "0",
    };

    /// <summary>
    /// Writes an 8 KB state file through the real store, so the file the benchmark reads is the file
    /// the app writes - including the pretty-printing that makes it that big.
    /// </summary>
    private static void SeedStateFile(string path)
    {
        var state = new AppStateFile
        {
            Address = "192.168.1.77:27183",
            LastDeviceName = "25091RP04C",
            LastDeviceAddress = "192.168.1.77:27183",
            RawArguments = string.Join(' ', Enumerable.Range(0, 40).Select(i => $"--extra-option-{i}=value-{i}")),
            AudioDevice = "Speakers (Realtek(R) Audio)",
        };

        foreach ((string id, string value) in RealisticProfile())
        {
            state.Options[id] = value;
        }

        // Pad to roughly what a heavily customised install carries.
        for (int i = 0; i < 40; i++)
        {
            state.Options[$"pad_option_{i}"] = new string('v', 40) + i.ToString(CultureInfo.InvariantCulture);
        }

        new AppStateStore(path).Save(state);
    }

    private static string RuntimeInformation() =>
        $"{Environment.ProcessorCount} logical CPUs, {GC.GetGCMemoryInfo().TotalAvailableMemoryBytes / (1024 * 1024)} MB available";

    private static void Usage(Dictionary<string, Scenario> scenarios)
    {
        Console.WriteLine("Scrappy.Bench");
        Console.WriteLine();
        Console.WriteLine("  all                    every scenario (default)");
        Console.WriteLine("  check                  behaviour parity checks, no timing");
        foreach (Scenario scenario in scenarios.Values)
        {
            Console.WriteLine($"  {scenario.Name,-17}  {scenario.Description}");
        }

        Console.WriteLine();
        Console.WriteLine("  --iterations N         measured iterations per scenario (default 7, minimum 3)");
        Console.WriteLine("  --cold <scenario>      one call in a fresh process: what a launch actually pays");
        Console.WriteLine("  check                  behaviour checks beside the timings");
    }
}

internal sealed record Scenario(string Name, string Description, Func<CancellationToken, Task<string>> Body);
