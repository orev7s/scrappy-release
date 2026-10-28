using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace Scrappy.Discovery;

/// <summary>
/// The machine's IPv4 links, and which of them is a cable.
///
/// "Wired" here means Android USB tethering: the tablet exposes a network interface over the USB
/// cable, the PC gets an address on it, and the mirror stream runs over it exactly as it does over
/// Wi-Fi. Nothing about the protocol changes - only which interface the address belongs to - so the
/// only thing that has to be worked out is whether a given address is on the cable or in the air.
///
/// There is a third state, and it is the one users actually hit: the cable is in, the RNDIS adapter
/// exists, and tethering is off, so the adapter has no routable address and there is nothing to
/// classify. Reporting that as "no tablet answered" is what made the wired path look broken when it
/// was simply not switched on, so it is reported as its own state.
/// </summary>
public sealed class LinkMap
{
    public sealed record Link(IPAddress Local, IPAddress Mask, bool IsWired, string Name)
    {
        public long Network => ToUInt32(Local.GetAddressBytes()) & ToUInt32(Mask.GetAddressBytes());

        public bool Contains(IPAddress address)
        {
            byte[] bytes = address.GetAddressBytes();
            if (bytes.Length != 4)
            {
                return false;
            }

            return (ToUInt32(bytes) & ToUInt32(Mask.GetAddressBytes())) == Network;
        }

        /// <summary>Sweeping a /16 politely is not possible; a /30 has no room to spare either.</summary>
        public bool IsSweepable => PrefixLength is >= 24 and <= 30;

        public int PrefixLength => PopCount(ToUInt32(Mask.GetAddressBytes()));
    }

    /// <summary>What the cable situation is, in the terms the window needs to explain it.</summary>
    public enum WiredState
    {
        /// <summary>No USB network interface at all: no cable, or the tablet is not presenting one.</summary>
        None,

        /// <summary>A tethered interface with a usable address: the cable is a real route.</summary>
        Tethering,

        /// <summary>An Android USB interface exists but has no address: tethering is switched off.</summary>
        CableWithoutTether,
    }

    private LinkMap(IReadOnlyList<Link> links, IReadOnlyList<string> dormantWired)
    {
        Links = links;
        DormantWired = dormantWired;
    }

    /// <summary>
    /// A map built from links the caller supplies, so the classification can be tested on a machine
    /// that has no USB tether attached. The app always uses <see cref="Read"/>.
    /// </summary>
    public static LinkMap FromLinks(IEnumerable<Link> links) => new(links.ToList(), []);

    /// <summary>A map for tests that need the "cable in, tethering off" state too.</summary>
    public static LinkMap FromLinks(IEnumerable<Link> links, IEnumerable<string> dormantWired) =>
        new(links.ToList(), dormantWired.ToList());

    public IReadOnlyList<Link> Links { get; }

    /// <summary>USB interfaces that exist but carry no usable IPv4 address.</summary>
    public IReadOnlyList<string> DormantWired { get; }

    /// <summary>True when a USB network interface is up: the cable is connected and tethering is on.</summary>
    public bool HasWiredLink => Links.Any(l => l.IsWired);

    public WiredState Wired => HasWiredLink
        ? WiredState.Tethering
        : DormantWired.Count > 0 ? WiredState.CableWithoutTether : WiredState.None;

    /// <summary>
    /// The line the window shows about the cable. Written to be actionable: it names the switch
    /// the user has to flip, on the device they have to flip it on.
    /// </summary>
    public string WiredAdvice => Wired switch
    {
        WiredState.Tethering => "Cable connected - the wired address is the one to use.",
        WiredState.CableWithoutTether =>
            "USB device found but no address: turn on Settings -> Hotspot & tethering -> USB tethering on the tablet.",
        _ => "No USB cable detected. Plug the tablet in and turn on USB tethering for the fastest link.",
    };

    public int NetworkCount => Links.Count(l => l.IsSweepable);

    public static LinkMap Read()
    {
        var links = new List<Link>();
        var dormant = new List<string>();

        foreach (NetworkInterface nic in SafeInterfaces())
        {
            bool loopback = nic.NetworkInterfaceType == NetworkInterfaceType.Loopback;
            if (loopback)
            {
                continue;
            }

            bool wiredName = LooksWired(nic);
            bool usable = false;

            foreach (UnicastIPAddressInformation unicast in SafeUnicast(nic))
            {
                if (unicast.Address.AddressFamily != AddressFamily.InterNetwork || unicast.IPv4Mask is null)
                {
                    continue;
                }

                byte[] address = unicast.Address.GetAddressBytes();
                if (address.Length != 4 || IsLoopbackOrLinkLocal(address))
                {
                    continue;
                }

                if (!nic.OperationalStatus.Equals(OperationalStatus.Up))
                {
                    continue;
                }

                // Android's tethering subnet is fixed, so the address alone identifies the cable
                // even when the adapter description is unhelpful.
                bool onTetherSubnet = address[0] == 192 && address[1] == 168 && address[2] == 42;

                usable = true;
                links.Add(new Link(
                    unicast.Address,
                    unicast.IPv4Mask,
                    wiredName || onTetherSubnet,
                    nic.Name));
            }

            // An Android USB interface with nothing routable on it is not a link, but it is the
            // strongest evidence there is that the cable is plugged in and tethering is off.
            if (wiredName && !usable)
            {
                dormant.Add($"{nic.Name} ({nic.OperationalStatus})");
            }
        }

        return new LinkMap(links, dormant);
    }

    /// <summary>"wired", "wifi", or empty when the address is not on any local link.</summary>
    public string TransportOf(string address)
    {
        if (!IPAddress.TryParse(address, out IPAddress? parsed) || parsed.AddressFamily != AddressFamily.InterNetwork)
        {
            return "";
        }

        // A wired match wins: if the same address somehow appears on two links, the cable is the
        // one that matters for throughput claims.
        Link? match = Links.FirstOrDefault(l => l.IsWired && l.Contains(parsed))
                      ?? Links.FirstOrDefault(l => l.Contains(parsed));

        if (match is null)
        {
            return "";
        }

        return match.IsWired ? "wired" : "wifi";
    }

    /// <summary>
    /// Every usable host on the sweepable links, wired first so the cable is checked in the first
    /// few milliseconds instead of after 254 Wi-Fi addresses.
    /// </summary>
    public List<IPAddress> SubnetHosts() => SubnetHosts(wiredOnly: false);

    /// <summary>
    /// The same list, optionally restricted to the cable. The wired sweep is cheap enough to run on
    /// every scan, which is why it is not restricted to "nothing answered" any more.
    /// </summary>
    public List<IPAddress> SubnetHosts(bool wiredOnly)
    {
        var hosts = new List<IPAddress>();

        foreach (Link link in Links
                     .Where(l => l.IsSweepable && (!wiredOnly || l.IsWired))
                     .OrderByDescending(l => l.IsWired))
        {
            byte[] mask = link.Mask.GetAddressBytes();
            long maskValue = ToUInt32(mask);
            long network = link.Network;
            long broadcast = network | (~maskValue & 0xFFFFFFFFL);

            for (long candidate = network + 1; candidate < broadcast; candidate++)
            {
                var host = new IPAddress(new[]
                {
                    (byte)((candidate >> 24) & 0xFF),
                    (byte)((candidate >> 16) & 0xFF),
                    (byte)((candidate >> 8) & 0xFF),
                    (byte)(candidate & 0xFF),
                });

                if (!host.Equals(link.Local))
                {
                    hosts.Add(host);
                }

                if (hosts.Count >= DeviceDiscovery.MaxSweepHosts)
                {
                    return hosts;
                }
            }
        }

        return hosts;
    }

    /// <summary>
    /// Windows calls a tethered Android phone a "Remote NDIS based Internet Sharing Device"; Linux
    /// calls it rndis or usb0. The keywords cover both without depending on either.
    ///
    /// A USB Wi-Fi dongle would match "usb" and is not a cable, so anything that also calls itself
    /// wireless is excluded - the classification is a claim about the link, and a wrong "Wired"
    /// label would send the user's stream down the wrong interface.
    /// </summary>
    private static bool LooksWired(NetworkInterface nic)
    {
        string haystack = $"{nic.Name} {nic.Description}".ToLowerInvariant();

        if (haystack.Contains("wireless") || haystack.Contains("wi-fi") ||
            haystack.Contains("wifi") || haystack.Contains("802.11") || haystack.Contains("wlan"))
        {
            return false;
        }

        string[] markers = ["rndis", "remote ndis", "ndis based", "usb", "tether", "android", "adb"];
        return markers.Any(haystack.Contains);
    }

    internal static byte[]? BroadcastOf(Link link)
    {
        byte[] a = link.Local.GetAddressBytes();
        byte[] m = link.Mask.GetAddressBytes();
        if (a.Length != 4 || m.Length != 4)
        {
            return null;
        }

        var result = new byte[4];
        for (int i = 0; i < 4; i++)
        {
            result[i] = (byte)(a[i] | ~m[i]);
        }

        return result;
    }

    internal static IEnumerable<NetworkInterface> SafeInterfaces()
    {
        try
        {
            return NetworkInterface.GetAllNetworkInterfaces();
        }
        catch (NetworkInformationException)
        {
            return [];
        }
    }

    internal static IEnumerable<UnicastIPAddressInformation> SafeUnicast(NetworkInterface nic)
    {
        try
        {
            return nic.GetIPProperties().UnicastAddresses;
        }
        catch (Exception ex) when (ex is NetworkInformationException or PlatformNotSupportedException)
        {
            return [];
        }
    }

    internal static bool IsLoopbackOrLinkLocal(byte[] address) =>
        address[0] == 127 || (address[0] == 169 && address[1] == 254);

    internal static long ToUInt32(byte[] bytes) =>
        ((long)bytes[0] << 24) | ((long)bytes[1] << 16) | ((long)bytes[2] << 8) | bytes[3];

    internal static int PopCount(long value)
    {
        int count = 0;
        for (int i = 0; i < 32; i++)
        {
            if ((value & (1L << i)) != 0)
            {
                count++;
            }
        }

        return count;
    }
}
