using System.Text.Json;

namespace Scrappy.Settings;

/// <summary>The value a single option currently carries.</summary>
public sealed record OptionSetting(bool IsSet, string? Value)
{
    public static readonly OptionSetting Unset = new(false, null);

    public static OptionSetting Flag(bool on) => new(on, on ? "true" : null);

    public static OptionSetting Of(string? value) =>
        string.IsNullOrWhiteSpace(value) ? Unset : new OptionSetting(true, value);

    /// <summary>Bare optional-value option: present, but carrying no argument.</summary>
    public static OptionSetting Bare() => new(true, null);
}

/// <summary>
/// Why an option cannot be changed from the UI even though it is listed. A locked option is
/// still visible and still built into the server arguments - it is not hidden, only fixed.
/// </summary>
public sealed record OptionLock(string OptionId, string Reason);

public static class LockedOptions
{
    /// <summary>
    /// Audio capture must duplicate rather than steal playback. Upstream picks the route flag from
    /// <c>audio_dup</c>: false selects ROUTE_FLAG_LOOP_BACK, which routes playback into the capture
    /// mix and away from the speaker, so the tablet goes silent. true selects
    /// ROUTE_FLAG_LOOP_BACK_RENDER, which plays locally and captures. <c>audio_source=output</c> is
    /// wrong for a second reason: it disables device playback by design.
    /// </summary>
    public static readonly IReadOnlyList<OptionLock> All =
    [
        new("audio_source", "Locked to playback: the tablet must keep playing its own audio."),
        new("audio_dup", "Locked on: without it, playback is routed into the capture and the speaker goes silent."),
    ];

    /// <summary>Values forced into every session, regardless of what the settings say.</summary>
    public static readonly IReadOnlyDictionary<string, OptionSetting> Forced =
        new Dictionary<string, OptionSetting>
        {
            ["audio_source"] = OptionSetting.Of("playback"),
            ["audio_dup"] = OptionSetting.Flag(true),
        };

    public static OptionLock? Find(string optionId) => All.FirstOrDefault(l => l.OptionId == optionId);
}

/// <summary>
/// The editable setting set: one <see cref="OptionSetting"/> per schema option, plus free-form raw
/// arguments for anything the schema cannot express.
/// </summary>
public sealed class SettingsStore
{
    private static readonly JsonSerializerOptions ProfileJson = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
    };

    private readonly Dictionary<string, OptionSetting> _values = new(StringComparer.Ordinal);

    /// <summary>
    /// Guards <see cref="_values"/>. The autosave writes from a timer thread while the UI thread
    /// edits, so a snapshot taken without this can enumerate a dictionary mid-mutation and throw.
    /// </summary>
    private readonly object _gate = new();

    private string _rawArguments = "";

    public SettingsStore(ScrcpySchema schema)
    {
        Schema = schema;
    }

    public ScrcpySchema Schema { get; }

    /// <summary>Extra arguments appended verbatim, e.g. <c>--video-codec-options=profile=1</c>.</summary>
    public string RawArguments
    {
        get
        {
            lock (_gate)
            {
                return _rawArguments;
            }
        }

        set
        {
            string next = value ?? "";
            lock (_gate)
            {
                if (string.Equals(_rawArguments, next, StringComparison.Ordinal))
                {
                    return;
                }

                _rawArguments = next;
            }

            Changed?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>Raised whenever any value changes, so views can refresh counts and previews.</summary>
    public event EventHandler? Changed;

    public OptionSetting Get(string optionId)
    {
        lock (_gate)
        {
            return _values.TryGetValue(optionId, out OptionSetting? setting) ? setting : OptionSetting.Unset;
        }
    }

    public void Set(string optionId, OptionSetting setting)
    {
        if (LockedOptions.Find(optionId) is not null)
        {
            return;
        }

        lock (_gate)
        {
            // Writing the value that is already there is not a change. Without this the settings
            // list fires on every keystroke and the autosave rewrites the file for nothing.
            OptionSetting current = _values.TryGetValue(optionId, out OptionSetting? existing)
                ? existing
                : OptionSetting.Unset;

            if (current == setting)
            {
                return;
            }

            if (!setting.IsSet)
            {
                _values.Remove(optionId);
            }
            else
            {
                _values[optionId] = setting;
            }
        }

        Changed?.Invoke(this, EventArgs.Empty);
    }

    public void Clear(string optionId) => Set(optionId, OptionSetting.Unset);

    public bool GetFlag(string optionId) => Get(optionId).IsSet;

    public void SetFlag(string optionId, bool on) => Set(optionId, on ? OptionSetting.Flag(true) : OptionSetting.Unset);

    public string? GetValue(string optionId) => Get(optionId).Value;

    public void SetValue(string optionId, string? value) =>
        Set(optionId, string.IsNullOrWhiteSpace(value) ? OptionSetting.Unset : OptionSetting.Of(value));

    /// <summary>Count of options the user has changed from "unset".</summary>
    public int ConfiguredCount
    {
        get
        {
            lock (_gate)
            {
                return _values.Count;
            }
        }
    }

    public IReadOnlyDictionary<string, OptionSetting> Snapshot()
    {
        Dictionary<string, OptionSetting> copy;
        lock (_gate)
        {
            copy = new Dictionary<string, OptionSetting>(_values, StringComparer.Ordinal);
        }

        foreach ((string id, OptionSetting forced) in LockedOptions.Forced)
        {
            copy[id] = forced;
        }

        return copy;
    }

    /// <summary>Replaces every value in one go. Locks are re-applied, so a profile cannot unlock them.</summary>
    public void Apply(IReadOnlyDictionary<string, OptionSetting> values)
    {
        lock (_gate)
        {
            _values.Clear();
            foreach ((string id, OptionSetting setting) in values)
            {
                if (setting.IsSet && LockedOptions.Find(id) is null)
                {
                    _values[id] = setting;
                }
            }
        }

        Changed?.Invoke(this, EventArgs.Empty);
    }

    public void Reset()
    {
        lock (_gate)
        {
            _values.Clear();
            _rawArguments = "";
        }

        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Serialises the current values, locks excluded, for a profile file.</summary>
    public Dictionary<string, string> ToProfileValues()
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        KeyValuePair<string, OptionSetting>[] entries;
        lock (_gate)
        {
            entries = _values.ToArray();
        }

        foreach ((string id, OptionSetting setting) in entries)
        {
            if (setting.IsSet)
            {
                result[id] = setting.Value ?? "";
            }
        }

        return result;
    }

    public void ApplyProfileValues(IReadOnlyDictionary<string, string> values)
    {
        var parsed = new Dictionary<string, OptionSetting>(StringComparer.Ordinal);
        foreach ((string id, string raw) in values)
        {
            ScrcpyOption? option = Schema.Find(id);
            if (option is null)
            {
                continue; // a profile written by a newer schema; ignore rather than fail
            }

            parsed[id] = option.Kind switch
            {
                OptionKind.Flag => raw is "" or "true" ? OptionSetting.Flag(true) : OptionSetting.Unset,
                _ => raw.Length == 0 ? OptionSetting.Bare() : OptionSetting.Of(raw),
            };
        }

        Apply(parsed);
    }

    public string ToJson() => JsonSerializer.Serialize(ToProfileValues(), ProfileJson);
}
