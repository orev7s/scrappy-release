using System.Reflection;

namespace Scrappy.App.Updates;

/// <summary>
/// The one place that knows what version is running, and the only place that decides whether one
/// version is newer than another.
///
/// The version used to be a literal in two files (the window's demo row and the tablet's gradle
/// file), so a release could ship with a number that did not match the thing that compared it.
/// It is read from the assembly now, which means the project file is the single source.
///
/// Comparison is numeric, component by component, never lexical: <c>0.10.0</c> is newer than
/// <c>0.9.9</c>, and <c>1.0.0</c> is newer than <c>0.99.99</c>. Up to four components are allowed
/// (1, 1.2, 1.2.3, 1.2.3.4); a leading <c>v</c>, build metadata (<c>+abc</c>) and a prerelease
/// suffix (<c>-beta.1</c>) are understood, and a prerelease sorts below the same stable version.
/// Anything that is not a version reads as <c>null</c> instead of throwing, because the value comes
/// from the network.
/// </summary>
public sealed class AppVersion : IComparable<AppVersion>, IEquatable<AppVersion>
{
    /// <summary>Four is the most a Windows assembly version holds; more is treated as junk.</summary>
    public const int MaxComponents = 4;

    private readonly int[] _components;

    private AppVersion(int[] components, string text, bool isPrerelease, string prereleaseTag)
    {
        _components = components;
        Text = text;
        IsPrerelease = isPrerelease;
        PrereleaseTag = prereleaseTag;
    }

    /// <summary>
    /// The running app's version. Read once: an assembly does not change under a running process.
    /// Falls back to the assembly version, then to 0.0.0, so this can never throw at startup.
    /// </summary>
    public static AppVersion Current { get; } = ReadCurrent();

    /// <summary>Normalized numeric form: no <c>v</c>, no build metadata, no prerelease tag.</summary>
    public string Text { get; }

    public bool IsPrerelease { get; }

    /// <summary>Everything after the first <c>-</c>, e.g. <c>beta.1</c>. Empty for a release.</summary>
    public string PrereleaseTag { get; }

    /// <summary>What to show a user: the text, plus the tag when there is one.</summary>
    public string Display => IsPrerelease ? $"{Text}-{PrereleaseTag}" : Text;

    public static AppVersion? TryParse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        string working = text.Trim();

        if (working.Length > 1 && (working[0] == 'v' || working[0] == 'V'))
        {
            working = working[1..];
        }

        // Build metadata is informational; it never participates in an ordering.
        int plus = working.IndexOf('+');
        if (plus >= 0)
        {
            working = working[..plus];
        }

        bool isPrerelease = false;
        string prereleaseTag = "";
        int dash = working.IndexOf('-');
        if (dash >= 0)
        {
            prereleaseTag = working[(dash + 1)..];
            working = working[..dash];

            // "1.2.3-" is a broken tag, not a release.
            if (prereleaseTag.Length == 0)
            {
                return null;
            }

            isPrerelease = true;
        }

        if (working.Length == 0)
        {
            return null;
        }

        string[] parts = working.Split('.');
        if (parts.Length is < 1 or > MaxComponents)
        {
            return null;
        }

        var components = new int[MaxComponents];
        for (int i = 0; i < parts.Length; i++)
        {
            string part = parts[i];
            if (part.Length == 0 || part.Length > 9)
            {
                return null;
            }

            int value = 0;
            foreach (char c in part)
            {
                if (c is < '0' or > '9')
                {
                    return null;
                }

                value = (value * 10) + (c - '0');
            }

            components[i] = value;
        }

        return new AppVersion(components, string.Join('.', parts), isPrerelease, prereleaseTag);
    }

    /// <summary>
    /// Numeric ordering, padded with zeros. A prerelease sorts below the same stable version, so
    /// <c>0.2.2-beta.1 &lt; 0.2.2</c>: a beta must never look newer than the build it precedes.
    /// </summary>
    public int CompareTo(AppVersion? other)
    {
        if (other is null)
        {
            return 1;
        }

        for (int i = 0; i < MaxComponents; i++)
        {
            int difference = _components[i].CompareTo(other._components[i]);
            if (difference != 0)
            {
                return difference;
            }
        }

        if (IsPrerelease == other.IsPrerelease)
        {
            return 0;
        }

        return IsPrerelease ? -1 : 1;
    }

    public bool Equals(AppVersion? other) => other is not null && CompareTo(other) == 0;

    public override bool Equals(object? obj) => obj is AppVersion other && Equals(other);

    public override int GetHashCode()
    {
        int hash = 17;
        foreach (int component in _components)
        {
            hash = (hash * 31) + component;
        }

        return IsPrerelease ? hash * 31 : hash;
    }

    public override string ToString() => Display;

    public static bool operator >(AppVersion left, AppVersion right) => left.CompareTo(right) > 0;

    public static bool operator <(AppVersion left, AppVersion right) => left.CompareTo(right) < 0;

    public static bool operator >=(AppVersion left, AppVersion right) => left.CompareTo(right) >= 0;

    public static bool operator <=(AppVersion left, AppVersion right) => left.CompareTo(right) <= 0;

    private static AppVersion ReadCurrent()
    {
        try
        {
            Assembly assembly = typeof(AppVersion).Assembly;
            string? informational = assembly
                .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
                .InformationalVersion;

            return TryParse(informational)
                ?? TryParse(assembly.GetName().Version?.ToString(AppVersion.MaxComponents))
                ?? TryParse("0.0.0")!;
        }
        catch (Exception)
        {
            // A version string is not worth failing a launch over.
            return TryParse("0.0.0")!;
        }
    }
}
