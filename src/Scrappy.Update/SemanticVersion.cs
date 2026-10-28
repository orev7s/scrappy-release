using System.Globalization;
using System.Text;

namespace Scrappy.Update;

/// <summary>
/// A version number as a release tag writes it: <c>v0.2.1</c>, <c>0.3.0</c>, <c>1.0.0-rc.2</c>.
///
/// Only comparison lives here, because comparison is the whole decision. A tag that cannot be read
/// as a version is not an error - it is a tag this program must ignore, and it is reported as
/// <c>null</c> rather than as an exception.
/// </summary>
/// <remarks>
/// Ordering follows Semantic Versioning 2.0.0: numeric fields compare numerically, a version
/// without a pre-release suffix is newer than the same numbers with one, and pre-release
/// identifiers compare field by field with numeric fields ranking below alphanumeric ones.
/// </remarks>
public sealed class SemanticVersion : IComparable<SemanticVersion>, IEquatable<SemanticVersion>
{
    /// <summary>Release tags carry four fields at most; anything longer is not a version.</summary>
    private const int MaxFields = 4;

    private readonly int[] _numbers;
    private readonly string[] _preRelease;

    private SemanticVersion(int[] numbers, string[] preRelease)
    {
        _numbers = numbers;
        _preRelease = preRelease;
    }

    /// <summary>The version a build with no version of its own compares as: below every release.</summary>
    public static SemanticVersion Zero { get; } = Parse("0.0.0")!;

    public int Major => Field(0);

    public int Minor => Field(1);

    public int Patch => Field(2);

    /// <summary>True for <c>1.0.0-rc.1</c> and false for <c>1.0.0</c>.</summary>
    public bool IsPreRelease => _preRelease.Length > 0;

    /// <summary>
    /// The normalised text: no <c>v</c>, no build metadata. Two tags that mean the same version
    /// produce the same string, which is what makes it usable as a suppression key.
    /// </summary>
    public override string ToString()
    {
        var text = new StringBuilder();
        text.Append(CultureInfo.InvariantCulture, $"{Major}.{Minor}.{Patch}");

        for (int i = 3; i < _numbers.Length; i++)
        {
            if (_numbers[i] != 0)
            {
                text.Append(CultureInfo.InvariantCulture, $".{_numbers[i]}");
            }
        }

        if (_preRelease.Length > 0)
        {
            text.Append('-').Append(string.Join('.', _preRelease));
        }

        return text.ToString();
    }

    private int Field(int index) => index < _numbers.Length ? _numbers[index] : 0;

    /// <summary>Reads a tag. Returns null when it is not a version, never throws.</summary>
    public static SemanticVersion? Parse(string? text) =>
        TryParse(text, out SemanticVersion? version) ? version : null;

    public static bool TryParse(string? text, out SemanticVersion? version)
    {
        version = null;

        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        string tag = text.Trim();

        // "v0.2.1" and "0.2.1" are the same release; every tag in this repository carries the v.
        if (tag.StartsWith('v') || tag.StartsWith('V'))
        {
            tag = tag[1..];
        }

        // Build metadata ("1.0.0+build.7") is deliberately dropped: it never ranks, and keeping it
        // would make the same release look like two different ones.
        int plus = tag.IndexOf('+');
        if (plus >= 0)
        {
            tag = tag[..plus];
        }

        string[] halves = tag.Split('-', 2);
        string core = halves[0];
        string[] identifiers = halves.Length == 2 ? halves[1].Split('.') : [];

        string[] fields = core.Split('.');
        if (fields.Length is 0 or > MaxFields)
        {
            return false;
        }

        var numbers = new int[fields.Length];
        for (int i = 0; i < fields.Length; i++)
        {
            if (!int.TryParse(fields[i], NumberStyles.None, CultureInfo.InvariantCulture, out numbers[i]))
            {
                // NumberStyles.None rejects a sign, a space and a thousands separator: "1.2.3 " and
                // "1.2.-3" are not versions.
                return false;
            }
        }

        foreach (string identifier in identifiers)
        {
            if (identifier.Length == 0)
            {
                return false;
            }

            foreach (char c in identifier)
            {
                if (!char.IsAsciiLetterOrDigit(c) && c != '-')
                {
                    return false;
                }
            }
        }

        version = new SemanticVersion(numbers, identifiers);
        return true;
    }

    public int CompareTo(SemanticVersion? other)
    {
        if (other is null)
        {
            return 1;
        }

        for (int i = 0; i < Math.Max(_numbers.Length, other._numbers.Length); i++)
        {
            int difference = Field(i).CompareTo(other.Field(i));
            if (difference != 0)
            {
                return difference;
            }
        }

        return ComparePreRelease(_preRelease, other._preRelease);
    }

    /// <summary>
    /// A release outranks its own pre-releases, and pre-release identifiers rank field by field:
    /// numeric below alphanumeric, numeric numerically, alphanumeric by ordinal.
    /// </summary>
    private static int ComparePreRelease(string[] left, string[] right)
    {
        if (left.Length == 0 && right.Length == 0)
        {
            return 0;
        }

        // 1.0.0 is newer than 1.0.0-rc.1.
        if (left.Length == 0)
        {
            return 1;
        }

        if (right.Length == 0)
        {
            return -1;
        }

        for (int i = 0; i < Math.Max(left.Length, right.Length); i++)
        {
            if (i >= left.Length)
            {
                return -1;
            }

            if (i >= right.Length)
            {
                return 1;
            }

            bool leftNumeric = IsNumeric(left[i], out int leftValue);
            bool rightNumeric = IsNumeric(right[i], out int rightValue);

            if (leftNumeric && rightNumeric)
            {
                if (leftValue != rightValue)
                {
                    return leftValue.CompareTo(rightValue);
                }

                continue;
            }

            // A numeric identifier is always lower than an alphanumeric one.
            if (leftNumeric)
            {
                return -1;
            }

            if (rightNumeric)
            {
                return 1;
            }

            int difference = string.CompareOrdinal(left[i], right[i]);
            if (difference != 0)
            {
                return difference;
            }
        }

        return 0;
    }

    private static bool IsNumeric(string text, out int value) =>
        int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out value);

    public bool Equals(SemanticVersion? other) => CompareTo(other) == 0;

    public override bool Equals(object? obj) => obj is SemanticVersion other && Equals(other);

    public override int GetHashCode()
    {
        var hash = new HashCode();
        foreach (int number in _numbers)
        {
            hash.Add(number);
        }

        foreach (string identifier in _preRelease)
        {
            hash.Add(identifier, StringComparer.Ordinal);
        }

        return hash.ToHashCode();
    }

    public static bool operator ==(SemanticVersion? left, SemanticVersion? right) =>
        left is null ? right is null : left.Equals(right);

    public static bool operator !=(SemanticVersion? left, SemanticVersion? right) => !(left == right);

    public static bool operator <(SemanticVersion? left, SemanticVersion? right) =>
        left is null ? right is not null : left.CompareTo(right) < 0;

    public static bool operator >(SemanticVersion? left, SemanticVersion? right) =>
        left is not null && left.CompareTo(right) > 0;

    public static bool operator <=(SemanticVersion? left, SemanticVersion? right) => !(left > right);

    public static bool operator >=(SemanticVersion? left, SemanticVersion? right) => !(left < right);
}
