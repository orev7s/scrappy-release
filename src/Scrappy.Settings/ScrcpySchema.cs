using System.Text.Json;
using System.Text.Json.Serialization;

namespace Scrappy.Settings;

/// <summary>
/// The table is parsed on every launch, before the window exists, so it is parsed by generated code
/// rather than by reflection. See <see cref="SchemaJsonContext"/>.
/// </summary>
[JsonConverter(typeof(OptionKindJsonConverter))]
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
    /// <remarks>
    /// Filled in after the deserialisation pass, from a separate resource, so it is not part of the
    /// JSON shape of an option.
    /// </remarks>
    [JsonIgnore]
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
        ScrcpySchema schema = JsonSerializer.Deserialize(stream, SchemaJsonContext.Default.ScrcpySchema)
            ?? throw new InvalidDataException("Option schema was empty.");

        if (schema.Options.Count == 0)
        {
            throw new InvalidDataException("Option schema contained no options.");
        }

        AttachExplanations(schema);
        return schema;
    }

    private static void AttachExplanations(ScrcpySchema schema)
    {
        Dictionary<string, string>? explanations = null;

        using (Stream? stream = typeof(ScrcpySchema).Assembly.GetManifestResourceStream(EmbeddedExplanationsName))
        {
            if (stream is not null)
            {
                explanations = JsonSerializer.Deserialize(stream, SchemaJsonContext.Default.DictionaryStringString);
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

/// <summary>
/// Source-generated serialisation for the option table and the explanation table.
///
/// The schema is read once per launch, before the window exists, and it is the same file every
/// time. Parsing it through reflection means that launch pays for building the property metadata for
/// <see cref="ScrcpySchema"/> and its 109 <see cref="ScrcpyOption"/> rows - JsonPropertyInfo objects,
/// property accessors, a dictionary converter built with reflection emit - before a single byte of
/// the screen has been drawn. The generated code has none of that: the parser is ordinary C# that
/// the JIT compiles once, and the property names are string literals instead of looked-up metadata.
/// </summary>
[JsonSourceGenerationOptions(PropertyNameCaseInsensitive = true)]
[JsonSerializable(typeof(ScrcpySchema))]
[JsonSerializable(typeof(Dictionary<string, string>))]
internal sealed partial class SchemaJsonContext : JsonSerializerContext
{
}

/// <summary>
/// <see cref="OptionKind"/> as the schema writes it: <c>flag</c>, <c>value</c>, <c>optional-value</c>.
///
/// This replaces <c>JsonStringEnumConverter</c> with a kebab-case naming policy. That converter is a
/// factory: it builds a converter per enum at run time, transforming every member name through the
/// policy first. The set of names is fixed and tiny, so it is spelled out here - which also removes
/// the factory from the source-generated context, where it is not supported at all.
/// </summary>
internal sealed class OptionKindJsonConverter : JsonConverter<OptionKind>
{
    public override OptionKind Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        switch (reader.TokenType)
        {
            case JsonTokenType.String:
                string text = reader.GetString() ?? "";
                if (text.Equals("flag", StringComparison.OrdinalIgnoreCase))
                {
                    return OptionKind.Flag;
                }

                if (text.Equals("value", StringComparison.OrdinalIgnoreCase))
                {
                    return OptionKind.Value;
                }

                if (text.Equals("optional-value", StringComparison.OrdinalIgnoreCase))
                {
                    return OptionKind.OptionalValue;
                }

                throw new JsonException($"'{text}' is not a scrcpy option kind (flag, value, optional-value).");

            case JsonTokenType.Number when reader.TryGetInt32(out int number) &&
                                           number is >= 0 and <= (int)OptionKind.OptionalValue:
                return (OptionKind)number;

            default:
                throw new JsonException($"Unexpected token {reader.TokenType} where an option kind was expected.");
        }
    }

    public override void Write(Utf8JsonWriter writer, OptionKind value, JsonSerializerOptions options) =>
        writer.WriteStringValue(value switch
        {
            OptionKind.Flag => "flag",
            OptionKind.Value => "value",
            OptionKind.OptionalValue => "optional-value",
            _ => throw new JsonException($"{value} is not a scrcpy option kind."),
        });
}
