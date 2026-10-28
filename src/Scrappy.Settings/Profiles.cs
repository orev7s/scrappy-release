using System.Text.Json;

namespace Scrappy.Settings;

/// <summary>A named set of option values, saved next to the user's other application data.</summary>
public sealed class Profile
{
    public string Name { get; set; } = "";

    /// <summary>Option id to raw value. Empty string means "present, no argument".</summary>
    public Dictionary<string, string> Values { get; set; } = new(StringComparer.Ordinal);
}

/// <summary>Loads and saves profiles as one small JSON file.</summary>
public sealed class ProfileStore
{
    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
    };

    public ProfileStore(string filePath)
    {
        FilePath = filePath;
    }

    public string FilePath { get; }

    public static string DefaultPath()
    {
        string root = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        if (string.IsNullOrEmpty(root))
        {
            root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config");
        }

        return Path.Combine(root, "Scrappy", "profiles.json");
    }

    public List<Profile> Load()
    {
        try
        {
            if (!File.Exists(FilePath))
            {
                return [];
            }

            return JsonSerializer.Deserialize<List<Profile>>(File.ReadAllText(FilePath), Json) ?? [];
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            // A corrupt profile file must not stop the app from starting.
            return [];
        }
    }

    public void Save(IEnumerable<Profile> profiles)
    {
        string? directory = Path.GetDirectoryName(FilePath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        File.WriteAllText(FilePath, JsonSerializer.Serialize(profiles.ToList(), Json));
    }

    public void SaveProfile(string name, SettingsStore settings)
    {
        List<Profile> profiles = Load();
        profiles.RemoveAll(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase));
        profiles.Add(new Profile { Name = name, Values = settings.ToProfileValues() });
        profiles.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));
        Save(profiles);
    }

    public void Delete(string name)
    {
        List<Profile> profiles = Load();
        profiles.RemoveAll(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase));
        Save(profiles);
    }
}

/// <summary>
/// The handful of choices worth a permanent place beside the session. These are not extra
/// settings: each preset writes ordinary schema options, so the settings list stays the single
/// source of truth and nothing here can drift out of sync with it.
/// </summary>
public static class DailyPresets
{
    public sealed record Choice(string Label, IReadOnlyDictionary<string, string> Values, string Note = "")
    {
        /// <summary>What a combo box shows. The label is the whole point of the type.</summary>
        public override string ToString() => Label;
    }

    /// <summary>Longest edge, so 3200 caps the 3200x2136 panel and 2560 gives 2560x1708.</summary>
    public static readonly IReadOnlyList<Choice> Resolution =
    [
        new("3200 x 2136 (native)", new Dictionary<string, string> { ["max_size"] = "3200" }),
        new("2560 x 1708", new Dictionary<string, string> { ["max_size"] = "2560" }),
        new("1920 x 1280", new Dictionary<string, string> { ["max_size"] = "1920" }),
        new("1280 x 854", new Dictionary<string, string> { ["max_size"] = "1280" }),
    ];

    public static readonly IReadOnlyList<Choice> FrameRate =
    [
        new("60 fps", new Dictionary<string, string> { ["max_fps"] = "60" }),
        new("30 fps", new Dictionary<string, string> { ["max_fps"] = "30" }),
        new("Uncapped", new Dictionary<string, string>()),
    ];

    public static readonly IReadOnlyList<Choice> Bitrate =
    [
        new("8 Mbps", new Dictionary<string, string> { ["video_bit_rate"] = "8000000" }),
        new("12 Mbps", new Dictionary<string, string> { ["video_bit_rate"] = "12000000" }),
        new("16 Mbps", new Dictionary<string, string> { ["video_bit_rate"] = "16000000" }),
        new("24 Mbps", new Dictionary<string, string> { ["video_bit_rate"] = "24000000" }),
    ];

    public static readonly IReadOnlyList<Choice> Codec =
    [
        new("H.264", new Dictionary<string, string> { ["video_codec"] = "h264" }),
        new("H.265", new Dictionary<string, string> { ["video_codec"] = "h265" }),
    ];

    /// <summary>
    /// Named bundles. Each writes ordinary options, so the settings list still shows the truth and
    /// a preset can be inspected, edited and saved under another name.
    /// </summary>
    public static readonly IReadOnlyList<Choice> Quality =
    [
        new("Tablet first", new Dictionary<string, string>
        {
            ["video_codec"] = "h265",
            ["video_bit_rate"] = "8000000",
            ["max_size"] = "1920",
            ["max_fps"] = "60",
            ["video_buffer"] = "30",
        }, "Tablet: light  PC: light  Link: 8 Mbps"),
        new("Balanced", new Dictionary<string, string>
        {
            ["video_codec"] = "h265",
            ["video_bit_rate"] = "12000000",
            ["max_size"] = "2560",
            ["max_fps"] = "60",
            ["video_buffer"] = "30",
        }, "Tablet: moderate  PC: moderate  Link: 12 Mbps"),
        new("Best quality", new Dictionary<string, string>
        {
            ["video_codec"] = "h265",
            ["video_bit_rate"] = "24000000",
            ["max_size"] = "3200",
            ["max_fps"] = "60",
            ["video_buffer"] = "50",
        }, "Tablet: heavy  PC: heavy  Link: 24 Mbps"),
    ];

    public static void Apply(SettingsStore settings, Choice choice)
    {
        foreach ((string id, string value) in choice.Values)
        {
            settings.SetValue(id, value);
        }
    }

    /// <summary>
    /// Secondary values a preset also writes. Matching ignores them, so "Balanced" is still shown
    /// as the active preset when the user changes a buffer - a preset is identified by what it does
    /// to resolution, frame rate, codec and bitrate, not by every key it happens to touch.
    /// </summary>
    private static readonly HashSet<string> SecondaryKeys = new(StringComparer.Ordinal) { "video_buffer" };

    /// <summary>Picks the choice whose primary values are all currently set.</summary>
    public static Choice? Match(SettingsStore settings, IReadOnlyList<Choice> choices)
    {
        foreach (Choice choice in choices)
        {
            bool matches = choice.Values.Count > 0
                ? choice.Values
                    .Where(kv => !SecondaryKeys.Contains(kv.Key))
                    .All(kv => settings.GetValue(kv.Key) == kv.Value)
                : choice.Values.Count == 0;

            if (matches)
            {
                return choice;
            }
        }

        return null;
    }
}
