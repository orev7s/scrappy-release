using Scrappy.Settings;

namespace Scrappy.App.ViewModels;

/// <summary>
/// One option in the settings list. Generated from the schema row, so the flag name, the kind and
/// the choices are whatever scrcpy's own table says - none of it is hand-written here.
/// </summary>
public sealed class OptionRowViewModel : ObservableObject
{
    private readonly SettingsStore _store;

    public OptionRowViewModel(ScrcpyOption option, SettingsStore store)
    {
        Option = option;
        _store = store;
        Lock = LockedOptions.Find(option.Id);
    }

    public ScrcpyOption Option { get; }

    /// <summary>The real flag, with its short form when upstream has one.</summary>
    public string Flag => Option.Short is null ? Option.Flag : $"{Option.Short}, {Option.Flag}";

    public string Help => Option.Help;

    /// <summary>Plain-language explanation, shown under the flag. Never empty: the schema falls
    /// back to upstream's own help text.</summary>
    public string Explanation => string.IsNullOrWhiteSpace(Option.Explanation) ? Option.Help : Option.Explanation;

    public string? Default => Option.Default;

    public string Group => Option.Group;

    public OptionKind Kind => Option.Kind;

    public bool IsLocked => Lock is not null;

    public OptionLock? Lock { get; }

    /// <summary>Tooltip: why this row cannot be changed, or what the argument looks like.</summary>
    public string Hint => Lock?.Reason
                          ?? (Option.Arg is null
                              ? Option.Help.Replace('\n', ' ')
                              : $"{Option.Arg}\n{Option.Help}".Replace('\n', ' '));

    /// <summary>True when the row shows a value field.</summary>
    public bool HasValueField => Option.Kind is OptionKind.Value or OptionKind.OptionalValue;

    /// <summary>True when the row shows an on/off switch.</summary>
    public bool HasToggle => Option.Kind is OptionKind.Flag or OptionKind.OptionalValue;

    public bool IsEnumerated => Option.IsEnumerated;

    /// <summary>Rows that are locked still show their value, but none of the controls are live.</summary>
    public bool IsEditable => !IsLocked;

    public bool ShowCheckbox => HasToggle && IsEditable;

    public bool ShowCombo => HasValueField && IsEnumerated && IsEditable;

    public bool ShowText => HasValueField && !IsEnumerated && IsEditable;

    public IReadOnlyList<string> Choices => Option.Enum ?? [];

    public string ArgumentHint => Option.Arg ?? "value";

    /// <summary>Combo binding: null when nothing is chosen, so the placeholder shows.</summary>
    public string? SelectedChoice
    {
        get => string.IsNullOrEmpty(Value) ? null : Value;
        set
        {
            Value = value ?? "";
            Raise(nameof(SelectedChoice));
        }
    }

    /// <summary>Set means "this option is sent". For a flag that is on; for a value, non-empty.</summary>
    public bool IsSet
    {
        get => _store.Get(Option.Id).IsSet || IsLocked;
        set
        {
            if (IsLocked)
            {
                Raise();
                return;
            }

            if (Option.Kind == OptionKind.Flag)
            {
                _store.Set(Option.Id, value ? OptionSetting.Flag(true) : OptionSetting.Unset);
            }
            else if (value)
            {
                _store.Set(Option.Id, OptionSetting.Bare());
            }
            else
            {
                _store.Set(Option.Id, OptionSetting.Unset);
            }

            Raise(nameof(IsSet));
            Raise(nameof(Value));
            Raise(nameof(StateText));
        }
    }

    public string Value
    {
        get
        {
            if (LockedOptions.Forced.TryGetValue(Option.Id, out OptionSetting? forced))
            {
                return forced.Value ?? "";
            }

            return _store.GetValue(Option.Id) ?? "";
        }
        set
        {
            if (IsLocked)
            {
                Raise();
                return;
            }

            _store.SetValue(Option.Id, value);
            Raise(nameof(IsSet));
            Raise(nameof(StateText));
        }
    }

    /// <summary>Re-reads everything from the store after a profile or reset moved the values.</summary>
    public void Refresh()
    {
        Raise(nameof(IsSet));
        Raise(nameof(Value));
        Raise(nameof(StateText));
    }

    /// <summary>Short right-hand label, so a dense list can be scanned without reading controls.</summary>
    public string StateText => IsLocked ? "locked"
        : !IsSet ? ""
        : Option.Kind == OptionKind.Flag ? ""
        : Value.Length == 0 ? "on" : Value;
}
