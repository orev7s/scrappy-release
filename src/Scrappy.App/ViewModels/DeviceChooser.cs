using Scrappy.Discovery;

namespace Scrappy.App.ViewModels;

/// <summary>
/// One row in the tablet picker: the tablet, plus the one fact the picker could not show before -
/// whether this is the tablet the last session used.
///
/// The badge is a property of the *row* rather than of <see cref="DiscoveredDevice"/> because the
/// device is what the network said, and "last used" is what this PC remembers. Mixing the two would
/// make the discovery result depend on the settings file.
/// </summary>
public sealed class DeviceChoice : ObservableObject
{
    private bool _lastUsed;

    public DeviceChoice(DiscoveredDevice device, bool lastUsed)
    {
        Device = device;
        _lastUsed = lastUsed;
    }

    public DiscoveredDevice Device { get; }

    /// <summary>True when this address is the one the last session ran on.</summary>
    public bool LastUsed
    {
        get => _lastUsed;
        set => SetProperty(ref _lastUsed, value);
    }

    public string Key => Device.Key;

    public string AddressText => Device.AddressText;

    public bool IsWired => Device.IsWired;

    public string DisplayName => Device.DisplayName;

    public string Detail => Device.Detail;
}

/// <summary>
/// Which tablet the picker should select after a scan, and why.
///
/// This is the rule that makes a one-tablet network need no clicks: the tablet from the last session
/// is selected again if it answered, a device the user has already picked in this run wins over
/// anything remembered, and a single answer is selected outright because there is nothing to choose
/// between. It never starts a session - selecting is a suggestion, and starting is the user's click.
/// </summary>
public static class DevicePreselect
{
    public enum Reason
    {
        /// <summary>Nothing answered, or nothing matched. The picker stays empty.</summary>
        None,

        /// <summary>The user picked this one already in this run; a rescan must not move it.</summary>
        PreviousSelection,

        /// <summary>The tablet the last session ran on, seen again.</summary>
        LastUsed,

        /// <summary>Exactly one tablet answered.</summary>
        OnlyDevice,

        /// <summary>Several answered and exactly one is on the cable, which is the better link.</summary>
        Wired,
    }

    public readonly record struct Choice(DiscoveredDevice? Device, Reason Reason)
    {
        public bool HasDevice => Device is not null;
    }

    /// <summary>
    /// <paramref name="lastUsedAddress"/> is the address the last session ran on, in <c>host:port</c>
    /// form; <paramref name="previousKey"/> is what the picker had selected before this scan.
    /// </summary>
    public static Choice Choose(
        IReadOnlyList<DiscoveredDevice> found,
        string? previousKey,
        string? lastUsedAddress)
    {
        if (found is null || found.Count == 0)
        {
            return new Choice(null, Reason.None);
        }

        if (!string.IsNullOrEmpty(previousKey) && found.FirstOrDefault(d => d.Key == previousKey) is { } same)
        {
            return new Choice(same, Reason.PreviousSelection);
        }

        if (!string.IsNullOrEmpty(lastUsedAddress) &&
            found.FirstOrDefault(d => string.Equals(d.Key, lastUsedAddress, StringComparison.Ordinal)) is { } remembered)
        {
            return new Choice(remembered, Reason.LastUsed);
        }

        if (found.Count == 1)
        {
            return new Choice(found[0], Reason.OnlyDevice);
        }

        DiscoveredDevice[] wired = found.Where(d => d.IsWired).ToArray();
        return wired.Length == 1
            ? new Choice(wired[0], Reason.Wired)
            : new Choice(null, Reason.None);
    }
}
