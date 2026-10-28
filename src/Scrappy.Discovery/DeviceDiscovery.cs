using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;

namespace Scrappy.Discovery;

/// <summary>
/// A tablet that answered on the local network.
///
/// The point of this type is that the user never types an IP. A device arrives either from the
/// UDP probe the Android app answers, or from a TCP sweep of the subnet for anything listening on
/// the mirror port. Both paths produce the same shape, so the picker does not care which one won.
/// </summary>
public sealed record DiscoveredDevice
{
    public required string Address { get; init; }

    public int Port { get; init; } = DeviceDiscovery.MirrorPort;

    /// <summary>What the tablet calls itself - the Android device name when it has one.</summary>
    public string Name { get; init; } = "";

    /// <summary>The model string, e.g. "25091RP04C". Shown under the name.</summary>
    public string Model { get; init; } = "";

    /// <summary>Idle, Listening, Connected, or empty when the tablet did not say.</summary>
    public string Phase { get; init; } = "";

    /// <summary>The tablet's own words about what it is doing. Used to explain a failed session.</summary>
    public string Message { get; init; } = "";

    /// <summary>The tablet's last error, one line, when it reported one.</summary>
    public string LastError { get; init; } = "";

    /// <summary>True when the tablet itself has a USB-tethered address.</summary>
    public bool WiredReady { get; init; }

    public int Width { get; init; }

    public int Height { get; init; }

    public double RefreshHz { get; init; }

    public string Version { get; init; } = "";

    /// <summary>"broadcast" or "scan". Only used for the status line, never for ranking.</summary>
    public string Source { get; init; } = "scan";

    /// <summary>
    /// "wired" when the address lives on a USB network (Android USB tethering), "wifi" when it
    /// lives on the wireless network, empty when the local interface could not be identified.
    /// The transport is a property of the link, so it is decided by which interface the address
    /// belongs to - not by what the tablet says about itself.
    /// </summary>
    public string Transport { get; init; } = "";

    public string TransportLabel => Transport switch
    {
        "wired" => "Wired",
        "wifi" => "Wi-Fi",
        _ => "",
    };

    /// <summary>True for a device reachable over the USB cable, which is the fastest path.</summary>
    public bool IsWired => Transport == "wired";

    public DateTime SeenUtc { get; init; } = DateTime.UtcNow;

    /// <summary>What the address field wants: host and port, nothing else.</summary>
    public string AddressText => $"{Address}:{Port}";

    /// <summary>The name to show, never empty: a device with no name is still pickable.</summary>
    public string DisplayName => string.IsNullOrWhiteSpace(Name)
        ? (string.IsNullOrWhiteSpace(Model) ? Address : Model)
        : Name;

    /// <summary>Second line in the picker: model, panel, and what the tablet is doing.</summary>
    public string Detail
    {
        get
        {
            // Transport first, then the address: on a tablet with both a cable and Wi-Fi, those are
            // the two facts that tell the entries apart.
            var parts = new List<string>(4);
            if (TransportLabel.Length > 0)
            {
                parts.Add(TransportLabel);
            }

            parts.Add(Address);

            if (Width > 0 && Height > 0)
            {
                parts.Add(RefreshHz > 0 ? $"{Width} x {Height} at {RefreshHz:0.#} Hz" : $"{Width} x {Height}");
            }

            if (!string.IsNullOrWhiteSpace(Phase))
            {
                parts.Add(Phase);
            }

            return string.Join(" - ", parts);
        }
    }

    /// <summary>Two entries are the same tablet when they share an address and port.</summary>
    public string Key => $"{Address}:{Port}";
}

/// <summary>Progress text for the status line while a scan runs.</summary>
public sealed record DiscoveryProgress(string Message, int Found);

/// <summary>
/// What a scan found, plus what the link state was. The link state is what lets the window say
/// "the cable is in, but tethering is off" instead of "no tablet answered".
/// </summary>
public sealed record DiscoveryResult(
    IReadOnlyList<DiscoveredDevice> Devices,
    LinkMap.WiredState Wired,
    int NetworksChecked)
{
    public int Count => Devices.Count;

    /// <summary>Kept for callers that only care whether a cable is a working route.</summary>
    public bool WiredLinkPresent => Wired == LinkMap.WiredState.Tethering;

    public static readonly DiscoveryResult Empty = new([], LinkMap.WiredState.None, 0);
}

/// <summary>
/// Which links a scan should sweep, and why. Separated from the sweep itself so the rule can be
/// asserted without a network: "the cable is swept on every scan" is the whole fix for a wired
/// tablet that never appeared while Wi-Fi answered first.
/// </summary>
public sealed record SweepPlan(bool Wired, bool Wireless)
{
    public bool Any => Wired || Wireless;

    public string Because => (Wired, Wireless) switch
    {
        (true, true) => "nothing answered the broadcast, so every local network is swept",
        (true, false) => "the cable is always swept, even when the tablet already answered over Wi-Fi",
        (false, true) => "no cable to check, so the wireless networks are swept",
        _ => "no sweepable network",
    };

    /// <summary>
    /// The rule. A wired link is swept whatever the probe found, because a broadcast is exactly
    /// what a tablet's firewall drops - and when it does, the cable silently disappears even though
    /// it works. Wireless links are only swept when the probe found nothing, because 254 addresses
    /// per network is not worth paying for twice.
    /// </summary>
    public static SweepPlan For(LinkMap links, IReadOnlyCollection<DiscoveredDevice> probed)
    {
        bool wiredAlreadyFound = probed.Any(d => d.IsWired);
        return new SweepPlan(
            Wired: links.HasWiredLink && !wiredAlreadyFound,
            Wireless: probed.Count == 0);
    }
}

/// <summary>
/// Finds tablets on the local network.
///
/// Two independent mechanisms, because either one alone fails on a real home network:
///
///   1. A UDP probe to the broadcast address. The Android app answers with its name and state, so
///      this path is fast (well under a second) and gives a human-readable name.
///   2. A TCP sweep of the PC's own subnets on the mirror port, then <c>GET /status</c> on anything
///      that accepts. This is slower, but it survives the router or the tablet's firewall dropping
///      broadcast traffic - which is the usual reason discovery "does not work" on HyperOS.
///
/// The cable is swept on every scan (see <see cref="SweepPlan"/>); the wireless networks only when
/// the probe came back empty.
/// </summary>
public sealed class DeviceDiscovery
{
    /// <summary>UDP port the Android app listens on for the probe. Not the mirror port.</summary>
    public const int DiscoveryPort = 27184;

    /// <summary>The port the tablet streams on, and the one the sweep looks for.</summary>
    public const int MirrorPort = 27183;

    /// <summary>The whole probe message. A reply means "a Scrappy tablet is here".</summary>
    public const string ProbeMessage = "SCRAPPY_DISCOVER";

    /// <summary>Where the probe is sent: the limited broadcast plus each interface's own broadcast.</summary>
    public static IEnumerable<IPEndPoint> BroadcastTargets() => BroadcastTargets(LinkMap.Read());

    /// <summary>
    /// The same targets, from a link map the caller already read. <see cref="LinkMap.Read"/> walks
    /// every interface on the machine, and a scan needs the map anyway, so reading it twice - once
    /// here, once in the caller - is pure duplicated work on the path between clicking Scan and the
    /// first datagram leaving the socket.
    /// </summary>
    public static IEnumerable<IPEndPoint> BroadcastTargets(LinkMap links)
    {
        yield return new IPEndPoint(IPAddress.Broadcast, DiscoveryPort);

        foreach (LinkMap.Link link in links.Links)
        {
            byte[]? broadcast = LinkMap.BroadcastOf(link);
            if (broadcast is null)
            {
                continue;
            }

            var endpoint = new IPEndPoint(new IPAddress(broadcast), DiscoveryPort);
            if (!endpoint.Address.Equals(IPAddress.Broadcast))
            {
                yield return endpoint;
            }
        }

        // A tablet on the same machine (the synthetic sender, or a test) never sees a broadcast.
        yield return new IPEndPoint(IPAddress.Loopback, DiscoveryPort);
    }

    public async Task<DiscoveryResult> ScanAsync(
        TimeSpan? probeWindow = null,
        IProgress<DiscoveryProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        TimeSpan window = probeWindow ?? TimeSpan.FromMilliseconds(900);
        LinkMap links = LinkMap.Read();
        LinkMap.WiredState wired = links.Wired;

        progress?.Report(new DiscoveryProgress(
            wired switch
            {
                LinkMap.WiredState.Tethering => "Looking for tablets, cable connected",
                LinkMap.WiredState.CableWithoutTether => "Looking for tablets, USB device found without an address",
                _ => "Looking for tablets",
            },
            0));

        IReadOnlyList<DiscoveredDevice> probed = await ProbeAsync(window, links, cancellationToken)
            .ConfigureAwait(false);

        SweepPlan plan = SweepPlan.For(links, probed);

        IReadOnlyList<DiscoveredDevice> swept = [];
        if (plan.Any)
        {
            if (plan.Wired)
            {
                progress?.Report(new DiscoveryProgress("Checking the cable", probed.Count));
            }

            swept = await SweepAsync(links, plan, progress, cancellationToken).ConfigureAwait(false);
        }

        IReadOnlyList<DiscoveredDevice> found = Merge(probed, swept);

        progress?.Report(new DiscoveryProgress(Describe(found, wired), found.Count));
        return new DiscoveryResult(found, wired, links.NetworkCount);
    }

    /// <summary>The one line the window shows after a scan. Every branch says what to do next.</summary>
    public static string Describe(IReadOnlyList<DiscoveredDevice> found, LinkMap.WiredState wired)
    {
        if (found.Count == 0)
        {
            return wired switch
            {
                LinkMap.WiredState.Tethering =>
                    "Cable connected but nothing answered. Start Scrappy on the tablet, then Scan again.",
                LinkMap.WiredState.CableWithoutTether =>
                    "USB device found but no address. On the tablet: Settings -> Hotspot & tethering -> USB tethering.",
                _ => "No tablet answered. Open Scrappy on the tablet, or type its address.",
            };
        }

        int wiredCount = found.Count(d => d.IsWired);

        if (wiredCount > 0)
        {
            DiscoveredDevice cable = found.First(d => d.IsWired);
            return found.Count == 1
                ? $"1 tablet over the cable at {cable.Address} - use this one, it does not share the Wi-Fi."
                : $"{found.Count} addresses, one over the cable ({cable.Address}) - pick the Wired entry.";
        }

        // Nothing on the cable, but the tablet says it has a tethered address: tethering is on and
        // the PC cannot see the link, which is usually a driver or a disabled adapter.
        if (found.Any(d => d.WiredReady) && wired == LinkMap.WiredState.None)
        {
            return $"1 tablet found over Wi-Fi. The tablet has a USB address but Windows does not see the cable - " +
                   "check Device Manager for an RNDIS adapter, or replug the cable.";
        }

        return found.Count == 1
            ? "1 tablet found over Wi-Fi."
            : $"{found.Count} addresses found, all over Wi-Fi.";
    }

    /// <summary>
    /// One entry per address. A tablet with a cable and Wi-Fi answers on both, and both are kept on
    /// purpose: they are different routes to the same tablet, and the cable is a different
    /// experience. Entries for the same address are merged, preferring the one that carries a name.
    /// </summary>
    public static IReadOnlyList<DiscoveredDevice> Merge(
        IEnumerable<DiscoveredDevice> probed,
        IEnumerable<DiscoveredDevice> swept)
    {
        var merged = new Dictionary<string, DiscoveredDevice>(StringComparer.Ordinal);

        foreach (DiscoveredDevice device in probed.Concat(swept))
        {
            if (!merged.TryGetValue(device.Key, out DiscoveredDevice? seen))
            {
                merged[device.Key] = device;
                continue;
            }

            // The richer record wins field by field; a swept entry has a name from /status, a
            // probed entry usually has everything.
            merged[device.Key] = seen with
            {
                Name = Better(seen.Name, device.Name),
                Model = Better(seen.Model, device.Model),
                Phase = Better(seen.Phase, device.Phase),
                Message = Better(seen.Message, device.Message),
                LastError = Better(seen.LastError, device.LastError),
                Version = Better(seen.Version, device.Version),
                WiredReady = seen.WiredReady || device.WiredReady,
                Width = seen.Width > 0 ? seen.Width : device.Width,
                Height = seen.Height > 0 ? seen.Height : device.Height,
                RefreshHz = seen.RefreshHz > 0 ? seen.RefreshHz : device.RefreshHz,
                Transport = seen.Transport.Length > 0 ? seen.Transport : device.Transport,
            };
        }

        // The cable first: it is the stable link, and on a tablet that answers on both it is the
        // one to use for a stream.
        return merged.Values
            .OrderByDescending(d => d.IsWired)
            .ThenBy(d => d.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(d => d.Address, StringComparer.Ordinal)
            .ToList();
    }

    private static string Better(string current, string candidate) =>
        string.IsNullOrWhiteSpace(current) ? candidate : current;

    // --- mechanism 1: the broadcast probe ------------------------------------------------

    private static async Task<IReadOnlyList<DiscoveredDevice>> ProbeAsync(
        TimeSpan window,
        LinkMap links,
        CancellationToken cancellationToken)
    {
        var found = new Dictionary<string, DiscoveredDevice>(StringComparer.Ordinal);

        using var udp = new UdpClient(AddressFamily.InterNetwork);
        try
        {
            udp.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
            udp.EnableBroadcast = true;
            udp.Client.Bind(new IPEndPoint(IPAddress.Any, 0));
        }
        catch (SocketException)
        {
            return [];
        }

        byte[] probe = Encoding.UTF8.GetBytes(ProbeMessage);

        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(window);

        foreach (IPEndPoint target in BroadcastTargets(links))
        {
            try
            {
                await udp.SendAsync(probe, target, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is SocketException or ObjectDisposedException)
            {
                // One unreachable interface must not stop the others.
            }
        }

        try
        {
            while (!deadline.IsCancellationRequested)
            {
                UdpReceiveResult reply = await udp.ReceiveAsync(deadline.Token).ConfigureAwait(false);

                foreach (DiscoveredDevice device in ParseReplies(reply.Buffer, reply.RemoteEndPoint.Address, links))
                {
                    found[device.Key] = device;
                }
            }
        }
        catch (OperationCanceledException)
        {
            // The window closed: expected, not an error.
        }
        catch (SocketException)
        {
            // The socket went away mid-window; whatever arrived is still useful.
        }

        return Merge(found.Values, []);
    }

    /// <summary>
    /// Reads a reply from any version of the app, ignoring anything that is not one.
    ///
    /// One datagram can describe more than one address. 0.1.8 added an <c>addresses</c> array so a
    /// tablet with a cable and Wi-Fi reports both at once; older versions declare a single address
    /// or nothing at all. In every case the datagram's source address is kept, because that is the
    /// address that provably reached this PC.
    /// </summary>
    public static IEnumerable<DiscoveredDevice> ParseReplies(byte[] payload, IPAddress? sender, LinkMap links)
    {
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(payload);
        }
        catch (JsonException)
        {
            yield break;
        }

        using (document)
        {
            JsonElement root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                yield break;
            }

            // A reply without our marker is some other program on the same port.
            if (!root.TryGetProperty("scrappy", out JsonElement marker) || marker.ValueKind != JsonValueKind.Number)
            {
                yield break;
            }

            string name = GetString(root, "name");
            string model = GetString(root, "model");
            string phase = GetString(root, "phase");
            string message = GetString(root, "message");
            string lastError = GetString(root, "lastError");
            string version = GetString(root, "version");
            bool wiredReady = root.TryGetProperty("wiredReady", out JsonElement ready) && ready.ValueKind == JsonValueKind.True;
            int port = GetInt(root, "port", MirrorPort);
            int width = GetInt(root, "width", 0);
            int height = GetInt(root, "height", 0);
            double refresh = GetDouble(root, "refreshHz");

            var candidates = new List<string>(4);

            if (sender is not null)
            {
                candidates.Add(sender.ToString());
            }

            if (root.TryGetProperty("addresses", out JsonElement addresses) && addresses.ValueKind == JsonValueKind.Array)
            {
                foreach (JsonElement entry in addresses.EnumerateArray())
                {
                    string? address = entry.ValueKind == JsonValueKind.String
                        ? entry.GetString()
                        : entry.ValueKind == JsonValueKind.Object ? GetString(entry, "address") : null;

                    if (!string.IsNullOrWhiteSpace(address))
                    {
                        candidates.Add(StripPort(address!));
                    }
                }
            }

            if (root.TryGetProperty("address", out JsonElement declared) && declared.ValueKind == JsonValueKind.String)
            {
                string? address = declared.GetString();
                if (!string.IsNullOrWhiteSpace(address))
                {
                    candidates.Add(StripPort(address!));
                }
            }

            var emitted = new HashSet<string>(StringComparer.Ordinal);

            foreach (string candidate in candidates)
            {
                // Only addresses this PC has a link to are useful: the tablet may list a mobile
                // data address or an IPv6-only interface, and neither can be connected to.
                string transport = links.TransportOf(candidate);
                if (transport.Length == 0 || !emitted.Add(candidate))
                {
                    continue;
                }

                yield return new DiscoveredDevice
                {
                    Address = candidate,
                    Port = port,
                    Name = name,
                    Model = model,
                    Phase = phase,
                    Message = message,
                    LastError = lastError,
                    WiredReady = wiredReady,
                    Width = width,
                    Height = height,
                    RefreshHz = refresh,
                    Version = version,
                    Source = "broadcast",
                    Transport = transport,
                    SeenUtc = DateTime.UtcNow,
                };
            }

            // Nothing on a local link (a test on loopback, or a reply from an address this machine
            // cannot see): still report the sender, so the picker is never empty for no reason.
            if (emitted.Count == 0 && sender is not null)
            {
                string host = sender.ToString();
                yield return new DiscoveredDevice
                {
                    Address = host,
                    Port = port,
                    Name = name,
                    Model = model,
                    Phase = phase,
                    Message = message,
                    LastError = lastError,
                    WiredReady = wiredReady,
                    Width = width,
                    Height = height,
                    RefreshHz = refresh,
                    Version = version,
                    Source = "broadcast",
                    SeenUtc = DateTime.UtcNow,
                };
            }
        }
    }

    private static string StripPort(string address)
    {
        int colon = address.LastIndexOf(':');
        return colon > 0 && int.TryParse(address.AsSpan(colon + 1), out _) ? address[..colon] : address;
    }

    // --- mechanism 2: the subnet sweep ----------------------------------------------------

    /// <summary>Hosts above this are not swept; a /16 would take minutes and find nothing new.</summary>
    public const int MaxSweepHosts = 1024;

    public const int SweepConnectTimeoutMs = 260;

    private async Task<IReadOnlyList<DiscoveredDevice>> SweepAsync(
        LinkMap links,
        SweepPlan plan,
        IProgress<DiscoveryProgress>? progress,
        CancellationToken cancellationToken)
    {
        List<IPAddress> candidates = [];

        if (plan.Wired)
        {
            candidates.AddRange(links.SubnetHosts(wiredOnly: true));
        }

        if (plan.Wireless)
        {
            foreach (IPAddress host in links.SubnetHosts(wiredOnly: false))
            {
                if (links.TransportOf(host.ToString()) != "wired")
                {
                    candidates.Add(host);
                }

                if (candidates.Count >= MaxSweepHosts)
                {
                    break;
                }
            }
        }

        if (candidates.Count == 0)
        {
            return [];
        }

        progress?.Report(new DiscoveryProgress(
            $"Checking {candidates.Count} addresses on {links.NetworkCount} network{(links.NetworkCount == 1 ? "" : "s")}",
            0));

        var open = new List<IPAddress>();
        using (var gate = new SemaphoreSlim(64))
        {
            var tasks = candidates.Select(async host =>
            {
                await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
                try
                {
                    if (await IsListeningAsync(host, MirrorPort, cancellationToken).ConfigureAwait(false))
                    {
                        lock (open)
                        {
                            open.Add(host);
                        }
                    }
                }
                finally
                {
                    gate.Release();
                }
            });

            try
            {
                await Task.WhenAll(tasks).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // Whatever was found before the cancel is returned below.
            }
        }

        var devices = new List<DiscoveredDevice>(open.Count);
        foreach (IPAddress host in open)
        {
            cancellationToken.ThrowIfCancellationRequested();

            DeviceFacts? facts = await DeviceProbe.QueryAsync(host.ToString(), MirrorPort, 1400, cancellationToken)
                .ConfigureAwait(false);

            devices.Add(new DiscoveredDevice
            {
                Address = host.ToString(),
                Port = MirrorPort,
                Name = facts?.Name ?? "",
                Model = facts?.Model ?? "",
                Phase = facts?.Phase ?? "",
                Message = facts?.Message ?? "",
                LastError = facts?.LastError ?? "",
                WiredReady = facts?.WiredReady ?? false,
                Width = facts?.Width ?? 0,
                Height = facts?.Height ?? 0,
                RefreshHz = facts?.RefreshHz ?? 0,
                Version = facts?.Version ?? "",
                Source = "scan",
                Transport = links.TransportOf(host.ToString()),
                SeenUtc = DateTime.UtcNow,
            });
        }

        return devices.OrderBy(d => d.DisplayName, StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static async Task<bool> IsListeningAsync(IPAddress host, int port, CancellationToken cancellationToken)
    {
        using var client = new TcpClient(AddressFamily.InterNetwork);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(SweepConnectTimeoutMs);

        try
        {
            await client.ConnectAsync(host, port, timeout.Token).ConfigureAwait(false);
            return client.Connected;
        }
        catch (Exception ex) when (ex is SocketException or OperationCanceledException or ObjectDisposedException)
        {
            return false;
        }
    }

    private static string GetString(JsonElement root, string name) =>
        root.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? ""
            : "";

    private static int GetInt(JsonElement root, string name, int fallback) =>
        root.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.Number &&
        value.TryGetInt32(out int parsed)
            ? parsed
            : fallback;

    private static double GetDouble(JsonElement root, string name) =>
        root.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.Number &&
        value.TryGetDouble(out double parsed)
            ? parsed
            : 0;
}
