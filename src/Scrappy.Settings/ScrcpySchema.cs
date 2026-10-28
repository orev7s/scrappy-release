using System.Text.Json;
using System.Text.Json.Serialization;

namespace Scrappy.Settings;

/// <summary>How a scrcpy option takes its argument, straight from the CLI table.</summary>
public enum OptionKind
{
    /// <summary>No argument. Present means on.</summary>
    Flag,

    /// <summary>Requires a value.</summary>
    Value,

    /// <summary>Value is optional, e.g. <c>--new-display</c> with or without a geometry.</summary>
    OptionalValue,
}

/// <summary>One row of <c>schema/scrcpy-options.json</c>. Generated - never hand-edited.</summary>
public sealed class ScrcpyOption
{
    [JsonPropertyName("id")] public string Id { get; init; } = "";

    /// <summary>The real CLI flag, e.g. <c>--max-size</c>. Shown in the settings list.</summary>
    [JsonPropertyName("flag")] public string Flag { get; init; } = "";

    [JsonPropertyName("short")] public string? Short { get; init; }

    [JsonPropertyName("kind")] public OptionKind Kind { get; init; }

    /// <summary>Argument placeholder from the help table, e.g. <c>width:height:x:y</c>.</summary>
    [JsonPropertyName("arg")] public string? Arg { get; init; }

    [JsonPropertyName("group")] public string Group { get; init; } = "";

    /// <summary>Enumerated choices, when the option is enumerable.</summary>
    [JsonPropertyName("enum")] public IReadOnlyList<string>? Enum { get; init; }

    [JsonPropertyName("default")] public string? Default { get; init; }

    [JsonPropertyName("help")] public string Help { get; init; } = "";

    /// <summary>
    /// Plain-language explanation from scrcpy-options.explain.json. Falls back to the CLI help
    /// text, which is still an explanation, so no row is ever blank.
    /// </summary>
    public string Explanation { get; internal set; } = "";

    /// <summary>
    /// The key the device-side server expects: the flag without dashes, hyphens to underscores.
    /// <c>--max-size</c> becomes <c>max_size</c>.
    /// </summary>
    public string ServerKey => Flag.TrimStart('-').Replace('-', '_');

    public bool IsEnumerated => Enum is { Count: > 0 };
}

/// <summary>The whole generated option table.</summary>
public sealed class ScrcpySchema
{
    public const string EmbeddedResourceName = "scrcpy-options.json";

    public const string EmbeddedExplanationsName = "scrcpy-options.explain.json";

    /// <summary>
    /// The options worth a place in the everyday view. Everything else is still there, one click
    /// away - this list decides what is shown before the user asks for more, not what exists.
    /// </summary>
    public static readonly IReadOnlyList<string> CoreOptionIds =
    [
        "max_size",
        "max_fps",
        "video_bit_rate",
        "video_codec",
        "video_buffer",
        "audio_codec",
        "audio_bit_rate",
        "record",
        "record_format",
        "hwdec",
    ];

    [JsonPropertyName("schema_version")] public int SchemaVersion { get; init; }

    [JsonPropertyName("generated_from")] public string GeneratedFrom { get; init; } = "";

    [JsonPropertyName("source_commit")] public string SourceCommit { get; init; } = "";

    [JsonPropertyName("count")] public int Count { get; init; }

    [JsonPropertyName("groups")] public IReadOnlyList<string> Groups { get; init; } = [];

    [JsonPropertyName("options")] public IReadOnlyList<ScrcpyOption> Options { get; init; } = [];

    /// <summary>Loads the copy embedded at build time, so the app never depends on repo layout.</summary>
    public static ScrcpySchema LoadEmbedded()
    {
        using Stream stream = typeof(ScrcpySchema).Assembly.GetManifestResourceStream(EmbeddedResourceName)
            ?? throw new InvalidOperationException(
                $"Embedded schema '{EmbeddedResourceName}' is missing. The build did not embed schema/scrcpy-options.json.");
        return Parse(stream);
    }

    public static ScrcpySchema Load(string path)
    {
        using FileStream stream = File.OpenRead(path);
        return Parse(stream);
    }

    public static ScrcpySchema Parse(Stream stream)
    {
        ScrcpySchema schema = JsonSerializer.Deserialize<ScrcpySchema>(stream, SerializerOptions)
            ?? throw new InvalidDataException("Option schema was empty.");

        if (schema.Options.Count == 0)
        {
            throw new InvalidDataException("Option schema contained no options.");
        }

        AttachExplanations(schema);
        return schema;
    }

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.KebabCaseLower) },
    };

    private static void AttachExplanations(ScrcpySchema schema)
    {
        Dictionary<string, string>? explanations = null;

        using (Stream? stream = typeof(ScrcpySchema).Assembly.GetManifestResourceStream(EmbeddedExplanationsName))
        {
            if (stream is not null)
            {
                explanations = JsonSerializer.Deserialize<Dictionary<string, string>>(stream, SerializerOptions);
            }
        }

        foreach (ScrcpyOption option in schema.Options)
        {
            option.Explanation =
                explanations is not null && explanations.TryGetValue(option.Id, out string? text)
                    ? text
                    : option.Help.Replace('\n', ' ').Trim();
        }
    }

    /// <summary>Group names in a stable order: the schema's own order, unknown groups last.</summary>
    public IEnumerable<string> OrderedGroups()
    {
        foreach (string g in Groups)
        {
            if (Options.Any(o => o.Group == g))
            {
                yield return g;
            }
        }

        foreach (string g in Options.Select(o => o.Group).Distinct().Except(Groups))
        {
            yield return g;
        }
    }

    public ScrcpyOption? Find(string id) => Options.FirstOrDefault(o => o.Id == id);

    public ScrcpyOption? FindByFlag(string flag) => Options.FirstOrDefault(o => o.Flag == flag);
}
