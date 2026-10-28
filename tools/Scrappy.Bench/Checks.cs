using System.Globalization;
using System.Net;
using Scrappy.Discovery;
using Scrappy.Settings;

namespace Scrappy.Bench;

/// <summary>
/// Behaviour checks that run beside the timings. A benchmark of a faster wrong answer is worse than
/// no benchmark, so every scenario in <see cref="Program"/> prints a digest of what it produced and
/// these checks pin the parts that a speed change could plausibly alter.
///
///   dotnet run -c Release --project tools/Scrappy.Bench -- check
/// </summary>
internal static class Checks
{
    public static int Run()
    {
        var failures = new List<string>();

        int before = failures.Count;
        EmbeddedSchemaMatchesSourceFile(failures);
        Report("schema-embedded-matches-source", before, failures);

        before = failures.Count;
        SweepPlanRule(failures);
        Report("sweep-plan-rule", before, failures);

        before = failures.Count;
        LinkClassification(failures);
        Report("link-classification", before, failures);

        before = failures.Count;
        ParseRepliesAddresses(failures);
        Report("parse-replies-addresses", before, failures);

        before = failures.Count;
        BroadcastTargets(failures);
        Report("broadcast-targets", before, failures);

        Console.WriteLine();
        if (failures.Count == 0)
        {
            Console.WriteLine("BENCH CHECKS PASS");
            return 0;
        }

        foreach (string failure in failures)
        {
            Console.WriteLine("  " + failure);
        }

        Console.WriteLine("BENCH CHECKS FAIL");
        return 1;
    }

    private static void Report(string name, int failuresBefore, List<string> failures) =>
        Console.WriteLine($"{(failures.Count == failuresBefore ? "PASS" : "FAIL")}  {name}");

    /// <summary>
    /// The copy embedded in the assembly and the copy in the repo must parse to the same table. If a
    /// faster parser loses a field, it loses it from both, so this also pins the parse itself: any
    /// option, group, enum value, help text or fallback explanation that goes missing changes the
    /// digest on one side only when the two paths differ.
    /// </summary>
    private static void EmbeddedSchemaMatchesSourceFile(List<string> failures)
    {
        ScrcpySchema embedded = ScrcpySchema.LoadEmbedded();
        string? path = FindSchemaPath();
        if (path is null)
        {
            failures.Add("schema/scrcpy-options.json not found from the current directory");
            return;
        }

        ScrcpySchema fromDisk = ScrcpySchema.Load(path);
        string a = Fingerprint.Schema(embedded);
        string b = Fingerprint.Schema(fromDisk);
        if (a != b)
        {
            failures.Add($"embedded schema differs from {path}: {a} vs {b}");
        }

        if (embedded.Count != embedded.Options.Count)
        {
            failures.Add($"schema says {embedded.Count} options but lists {embedded.Options.Count}");
        }

        // Every option must still be reachable by id and by flag - the two lookups the settings list
        // and the argument builder make.
        foreach (ScrcpyOption option in embedded.Options)
        {
            if (embedded.Find(option.Id) is null)
            {
                failures.Add($"{option.Id} cannot be found by id");
                break;
            }

            if (embedded.FindByFlag(option.Flag) is null)
            {
                failures.Add($"{option.Flag} cannot be found by flag");
                break;
            }

            if (option.Explanation.Length == 0)
            {
                failures.Add($"{option.Id} has no explanation");
                break;
            }
        }
    }

    /// <summary>The sweep rule, unchanged: the cable always, Wi-Fi only when nothing answered.</summary>
    private static void SweepPlanRule(List<string> failures)
    {
        LinkMap wired = SyntheticWired();
        LinkMap wireless = SyntheticWireless();

        Expect(failures, SweepPlan.For(wired, []), new SweepPlan(Wired: true, Wireless: true), "cable + no answer");
        Expect(failures, SweepPlan.For(wireless, []), new SweepPlan(Wired: false, Wireless: true), "Wi-Fi + no answer");
        Expect(
            failures,
            SweepPlan.For(wired, [Device("192.168.1.20", "wifi")]),
            new SweepPlan(Wired: true, Wireless: false),
            "cable + Wi-Fi answered");
        Expect(
            failures,
            SweepPlan.For(wired, [Device("192.168.42.129", "wired")]),
            new SweepPlan(Wired: false, Wireless: false),
            "cable answered, so it is not swept twice");
    }

    private static void Expect(List<string> failures, SweepPlan actual, SweepPlan expected, string what)
    {
        if (actual != expected)
        {
            failures.Add($"{what}: expected {expected}, got {actual}");
        }
    }

    private static void LinkClassification(List<string> failures)
    {
        LinkMap links = LinkMap.FromLinks(
        [
            new LinkMap.Link(IPAddress.Parse("192.168.1.10"), IPAddress.Parse("255.255.255.0"), false, "Wi-Fi"),
            new LinkMap.Link(IPAddress.Parse("192.168.42.129"), IPAddress.Parse("255.255.255.0"), true, "Remote NDIS"),
        ]);

        (string Address, string Expected)[] cases =
        [
            ("192.168.1.20", "wifi"),
            ("192.168.1.10", "wifi"),
            ("192.168.42.130", "wired"),
            ("192.168.42.129", "wired"),
            ("10.0.0.5", ""),
            ("not an address", ""),
            ("fe80::1", ""),
        ];

        foreach ((string address, string expected) in cases)
        {
            string actual = links.TransportOf(address);
            if (actual != expected)
            {
                failures.Add($"TransportOf({address}) expected '{expected}', got '{actual}'");
            }
        }

        if (links.Wired != LinkMap.WiredState.Tethering)
        {
            failures.Add($"expected tethering, got {links.Wired}");
        }

        if (links.NetworkCount != 2)
        {
            failures.Add($"expected 2 sweepable networks, got {links.NetworkCount}");
        }
    }

    /// <summary>
    /// One datagram that lists a second address must produce one entry per local link, and nothing
    /// for an address this PC has no link to.
    /// </summary>
    private static void ParseRepliesAddresses(List<string> failures)
    {
        LinkMap links = LinkMap.FromLinks(
        [
            new LinkMap.Link(IPAddress.Parse("192.168.1.10"), IPAddress.Parse("255.255.255.0"), false, "Wi-Fi"),
            new LinkMap.Link(IPAddress.Parse("192.168.42.129"), IPAddress.Parse("255.255.255.0"), true, "Remote NDIS"),
        ]);

        byte[] payload = System.Text.Encoding.UTF8.GetBytes("""
            {"scrappy":1,"name":"25091RP04C","phase":"Waiting for the PC","wiredReady":true,
             "width":2560,"height":1708,"refreshHz":120,"port":27183,
             "addresses":["192.168.42.129","192.168.42.129","10.9.9.9"]}
            """);

        List<DiscoveredDevice> devices = DeviceDiscovery
            .ParseReplies(payload, IPAddress.Parse("192.168.1.10"), links)
            .ToList();

        string seen = string.Join(',', devices.Select(d => $"{d.Address}:{d.Transport}"));
        if (seen != "192.168.1.10:wifi,192.168.42.129:wired")
        {
            failures.Add($"expected the sender on Wi-Fi then the cable address, got '{seen}'");
        }

        if (devices.Any(d => d.Name != "25091RP04C" || d.Width != 2560 || d.RefreshHz != 120))
        {
            failures.Add("the reply's fields did not survive parsing");
        }
    }

    /// <summary>
    /// The probe must still go to the limited broadcast, every interface's own broadcast, and
    /// loopback - in that order, and exactly once each.
    /// </summary>
    private static void BroadcastTargets(List<string> failures)
    {
        List<IPEndPoint> targets = DeviceDiscovery.BroadcastTargets().ToList();
        if (targets.Count == 0 || !targets[0].Address.Equals(IPAddress.Broadcast))
        {
            failures.Add("the limited broadcast is not the first probe target");
        }

        if (!targets[^1].Address.Equals(IPAddress.Loopback))
        {
            failures.Add("loopback is not the last probe target");
        }

        if (targets.Any(t => t.Port != DeviceDiscovery.DiscoveryPort))
        {
            failures.Add("a probe target is not on the discovery port");
        }

        if (targets.Distinct().Count() != targets.Count)
        {
            failures.Add("duplicate probe targets");
        }

        // The same targets, but computed from a link map the caller already has: that is the whole
        // point of the overload, so it must produce exactly what the parameterless form produces.
        LinkMap links = LinkMap.Read();
        List<IPEndPoint> fromMap = DeviceDiscovery.BroadcastTargets(links).ToList();
        if (fromMap.Count != targets.Count)
        {
            failures.Add($"a known link map produced {fromMap.Count} probe targets, the live one {targets.Count}");
        }

        foreach (IPEndPoint missing in targets.Except(fromMap))
        {
            failures.Add($"{missing} is missing when the link map is passed in");
        }

        // Each link's own broadcast address, computed here independently of the production code.
        foreach (LinkMap.Link link in links.Links)
        {
            byte[] address = link.Local.GetAddressBytes();
            byte[] mask = link.Mask.GetAddressBytes();
            if (address.Length != 4 || mask.Length != 4)
            {
                continue;
            }

            var broadcast = new IPAddress(new[]
            {
                (byte)(address[0] | ~mask[0]),
                (byte)(address[1] | ~mask[1]),
                (byte)(address[2] | ~mask[2]),
                (byte)(address[3] | ~mask[3]),
            });

            if (!broadcast.Equals(IPAddress.Broadcast) && fromMap.All(t => !t.Address.Equals(broadcast)))
            {
                failures.Add($"the broadcast address {broadcast} of {link.Name} is not probed");
            }
        }
    }

    private static LinkMap SyntheticWired() => LinkMap.FromLinks(
        [new LinkMap.Link(IPAddress.Parse("192.168.42.129"), IPAddress.Parse("255.255.255.0"), true, "Remote NDIS")]);

    private static LinkMap SyntheticWireless() => LinkMap.FromLinks(
        [new LinkMap.Link(IPAddress.Parse("192.168.1.10"), IPAddress.Parse("255.255.255.0"), false, "Wi-Fi")]);

    private static DiscoveredDevice Device(string address, string transport) =>
        new() { Address = address, Transport = transport };

    private static string? FindSchemaPath()
    {
        var directory = new DirectoryInfo(Directory.GetCurrentDirectory());
        while (directory is not null)
        {
            string candidate = Path.Combine(directory.FullName, "schema", "scrcpy-options.json");
            if (File.Exists(candidate))
            {
                return candidate;
            }

            directory = directory.Parent;
        }

        return null;
    }
}

/// <summary>Stable text fingerprints, so before/after runs can be compared literally.</summary>
internal static class Fingerprint
{
    /// <summary>
    /// FNV-1a over every field of every option: two builds that disagree about any value print a
    /// different digest, which is what makes the before/after benchmark comparable.
    /// </summary>
    public static string Schema(ScrcpySchema schema)
    {
        ulong hash = 14695981039346656037;

        void Mix(string? value)
        {
            foreach (char c in value ?? "")
            {
                hash = (hash ^ c) * 1099511628211;
            }

            hash = (hash ^ '|') * 1099511628211;
        }

        Mix(schema.SchemaVersion.ToString(CultureInfo.InvariantCulture));
        Mix(schema.GeneratedFrom);
        Mix(schema.SourceCommit);
        Mix(schema.Count.ToString(CultureInfo.InvariantCulture));

        foreach (string group in schema.Groups)
        {
            Mix(group);
        }

        foreach (ScrcpyOption option in schema.Options)
        {
            Mix(option.Id);
            Mix(option.Flag);
            Mix(option.Short);
            Mix(option.Kind.ToString());
            Mix(option.Arg);
            Mix(option.Group);
            Mix(option.Default);
            Mix(option.Help);
            Mix(option.Explanation);
            Mix(option.ServerKey);
            Mix(option.IsEnumerated.ToString());
            foreach (string value in option.Enum ?? [])
            {
                Mix(value);
            }
        }

        return $"options={schema.Options.Count};groups={schema.Groups.Count};hash={hash:x16}";
    }

    public static string Links(LinkMap links) =>
        string.Join('+', links.Links.Select(l => $"{l.Local}/{(l.IsWired ? "wired" : "wifi")}")) +
        $";dormant={links.DormantWired.Count};networks={links.NetworkCount}";
}
