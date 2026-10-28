using System.Text.Json;
using System.Text.Json.Serialization;

namespace Scrappy.Update;

/// <summary>
/// "Don't show again", remembered.
///
/// The remembered thing is a version, not a flag. A flag would mean one click silenced the update
/// notice for good, which is the bug this shape avoids: the user said "not 0.3.0", not "never tell
/// me anything". When 0.3.1 is published it is a different version, so it is offered again.
/// </summary>
public sealed class UpdateSuppression
{
    /// <summary>Set to point the file somewhere else. Used by the tests, never by the app.</summary>
    public const string DirectoryOverrideVariable = "SCRAPPY_UPDATE_DIR";

    /// <summary>A user who says "not this one" ten times is not collecting versions; keep it bounded.</summary>
    public const int MaxRemembered = 64;

    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
    };

    private readonly List<string> _versions = [];

    private UpdateSuppression(string filePath, IReadOnlyList<string> versions)
    {
        FilePath = filePath;
        foreach (string version in versions)
        {
            if (Normalise(version) is { } normalised)
            {
                _versions.Add(normalised);
            }
        }
    }

    public string FilePath { get; }

    public IReadOnlyList<string> Versions => _versions;

    /// <summary>
    /// True only for the exact version that was dismissed. A newer version is not this version, so
    /// it is offered; an older one is not either, but there is no older one to offer once a newer
    /// release exists.
    /// </summary>
    public bool IsSuppressed(SemanticVersion version) => IsSuppressed(version.ToString());

    public bool IsSuppressed(string version)
    {
        if (Normalise(version) is not { } normalised)
        {
            return false;
        }

        return _versions.Contains(normalised, StringComparer.Ordinal);
    }

    /// <summary>Remembers one version. Writes immediately: a dismissal that is lost on a crash is a lie.</summary>
    public void Suppress(SemanticVersion version)
    {
        if (Normalise(version.ToString()) is not { } normalised)
        {
            return;
        }

        if (!_versions.Contains(normalised, StringComparer.Ordinal))
        {
            _versions.Add(normalised);
        }

        while (_versions.Count > MaxRemembered)
        {
            _versions.RemoveAt(0);
        }

        Save();
    }

    public void Forget(SemanticVersion version)
    {
        if (Normalise(version.ToString()) is { } normalised)
        {
            _versions.RemoveAll(v => string.Equals(v, normalised, StringComparison.Ordinal));
            Save();
        }
    }

    /// <summary>Never throws: a file it cannot write is a preference it cannot keep, not a failure.</summary>
    private void Save()
    {
        try
        {
            string? directory = Path.GetDirectoryName(FilePath);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            var file = new SuppressionFile { Suppressed = _versions.ToList() };
            string text = JsonSerializer.Serialize(file, Json);
            File.WriteAllText(FilePath, text);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            // No settings directory, a read-only profile, a path with a wild character in it. The
            // dialog will simply come back next launch, which is where it started.
        }
    }

    /// <summary>Reads the file. A missing or damaged one means "nothing was dismissed", never an error.</summary>
    public static UpdateSuppression Open(string? filePath = null)
    {
        string path = filePath ?? DefaultPath();
        var versions = new List<string>();

        try
        {
            if (File.Exists(path))
            {
                string text = File.ReadAllText(path);
                if (!string.IsNullOrWhiteSpace(text))
                {
                    SuppressionFile? file = JsonSerializer.Deserialize<SuppressionFile>(text, Json);
                    if (file?.Suppressed is { } remembered)
                    {
                        versions.AddRange(remembered);
                    }
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException
                                       or NotSupportedException)
        {
            // A damaged file is not a crash and not a decision: nothing is dismissed.
        }

        return new UpdateSuppression(path, versions);
    }

    /// <summary>
    /// The user's application data, not the program folder: an installer replaces the program
    /// folder, and a suppression the user asked for must survive an upgrade.
    /// </summary>
    public static string DefaultDirectory()
    {
        string? overridden = Environment.GetEnvironmentVariable(DirectoryOverrideVariable);
        if (!string.IsNullOrWhiteSpace(overridden))
        {
            return overridden;
        }

        string root = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        if (string.IsNullOrEmpty(root))
        {
            // Non-Windows fallback, so the Linux development build behaves the same way.
            root = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                ".config");
        }

        return Path.Combine(root, "Scrappy");
    }

    public static string DefaultPath() => Path.Combine(DefaultDirectory(), "update.json");

    private static string? Normalise(string? version)
    {
        SemanticVersion? parsed = SemanticVersion.Parse(version);
        return parsed?.ToString();
    }

    private sealed class SuppressionFile
    {
        [JsonPropertyName("version")]
        public int Version { get; set; } = 1;

        [JsonPropertyName("suppressed")]
        public List<string> Suppressed { get; set; } = [];
    }
}
