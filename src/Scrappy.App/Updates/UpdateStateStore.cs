using System.Text.Json;
using System.Text.Json.Serialization;

namespace Scrappy.App.Updates;

/// <summary>
/// What the app remembers about the update prompt. One field does the work: the version the user
/// said "Don't show again" to. A flag would be wrong - it would silence every future release too.
/// </summary>
public sealed class UpdateStateFile
{
    public const int CurrentVersion = 1;

    [JsonPropertyName("version")]
    public int Version { get; set; } = CurrentVersion;

    /// <summary>Normalized text of the suppressed version, e.g. <c>0.2.1</c>. Null = nothing suppressed.</summary>
    [JsonPropertyName("dismissedVersion")]
    public string? DismissedVersion { get; set; }

    /// <summary>Informational: when the suppression was recorded. Never used to decide anything.</summary>
    [JsonPropertyName("dismissedUtc")]
    public DateTime? DismissedUtc { get; set; }
}

/// <summary>
/// Reads and writes <c>%LOCALAPPDATA%\Scrappy\update-state.json</c>, its own file so the update
/// check can never damage the settings the app is running on.
///
/// Loading is total: a missing, empty or corrupt file is the same as "nothing suppressed". Saving is
/// atomic (write a temporary file, then move it into place), so a crash mid-write leaves the previous
/// answer intact instead of a half-written one that parses as nothing.
/// </summary>
public sealed class UpdateStateStore
{
    /// <summary>Set to point the store somewhere else. Used by the tests, never by the app.</summary>
    public const string DirectoryOverrideVariable = "SCRAPPY_UPDATE_STATE_DIR";

    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
    };

    public UpdateStateStore(string filePath)
    {
        FilePath = filePath;
    }

    public string FilePath { get; }

    public static string DefaultDirectory()
    {
        string? overridden = Environment.GetEnvironmentVariable(DirectoryOverrideVariable);
        if (!string.IsNullOrWhiteSpace(overridden))
        {
            return overridden;
        }

        string root = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (string.IsNullOrEmpty(root))
        {
            // Non-Windows fallback, so a Linux development build behaves the same way.
            root = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                ".local",
                "share");
        }

        return Path.Combine(root, "Scrappy");
    }

    public static string DefaultPath() => Path.Combine(DefaultDirectory(), "update-state.json");

    /// <summary>Never throws. An unreadable answer means "ask again", which is the safe way round.</summary>
    public AppVersion? LoadDismissed()
    {
        try
        {
            if (!File.Exists(FilePath))
            {
                return null;
            }

            string text = File.ReadAllText(FilePath);
            if (string.IsNullOrWhiteSpace(text))
            {
                return null;
            }

            UpdateStateFile? state = JsonSerializer.Deserialize<UpdateStateFile>(text, Json);
            return AppVersion.TryParse(state?.DismissedVersion);
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException or NotSupportedException)
        {
            return null;
        }
    }

    /// <summary>
    /// Records "Don't show again" for one version. Returns false when the write failed; the caller
    /// stays quiet about that, because a failed suppression is not worth a dialog of its own.
    /// </summary>
    public bool Dismiss(AppVersion version)
    {
        try
        {
            string? directory = Path.GetDirectoryName(FilePath);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            var state = new UpdateStateFile
            {
                Version = UpdateStateFile.CurrentVersion,
                DismissedVersion = version.Text,
                DismissedUtc = DateTime.UtcNow,
            };

            string temporary = FilePath + ".tmp";
            File.WriteAllText(temporary, JsonSerializer.Serialize(state, Json));

            // Move with overwrite is a single replace on NTFS: a reader either sees the old file or
            // the new one, never a truncated mix of the two.
            File.Move(temporary, FilePath, overwrite: true);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            return false;
        }
    }
}
