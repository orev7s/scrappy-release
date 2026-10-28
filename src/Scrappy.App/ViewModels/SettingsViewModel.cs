using System.Collections.ObjectModel;
using Scrappy.Settings;

namespace Scrappy.App.ViewModels;

/// <summary>
/// The full settings list. Every row comes from the generated schema and every row is reachable:
/// nothing is hidden, nothing needs a terminal. Search filters on flag name, group and help text.
/// </summary>
public sealed class SettingsViewModel : ObservableObject
{
    private const string AllGroups = "All groups";

    private readonly SettingsStore _store;
    private readonly ProfileStore _profiles;
    private readonly AppStateAutosave? _autosave;
    private readonly List<OptionRowViewModel> _allRows;

    private string _search = "";
    private string _selectedGroup = AllGroups;
    private Profile? _selectedProfile;
    private string _profileName = "";
    private int _visibleCount;
    private bool _showAdvanced;

    public SettingsViewModel(SettingsStore store, ProfileStore profiles, AppStateAutosave? autosave = null)
    {
        _store = store;
        _profiles = profiles;
        _autosave = autosave;

        _allRows = store.Schema.Options
            .Select(option => new OptionRowViewModel(option, store))
            .ToList();

        Groups = [AllGroups, .. store.Schema.OrderedGroups()];
        Rows = new ObservableCollection<OptionRowViewModel>(_allRows);
        Profiles = new ObservableCollection<Profile>(_profiles.Load());

        // Apply the everyday filter before the first bind, or the window opens showing everything
        // while claiming to show a subset.
        Refresh();

        store.Changed += (_, _) =>
        {
            Raise(nameof(ArgumentsPreview));
            Raise(nameof(ConfiguredCount));
            Raise(nameof(ConfiguredLabel));
            Raise(nameof(SaveStatus));
        };
    }

    public IReadOnlyList<string> Groups { get; }

    public ObservableCollection<OptionRowViewModel> Rows { get; }

    public ObservableCollection<Profile> Profiles { get; }

    public int TotalCount => _allRows.Count;

    /// <summary>
    /// Off by default: the everyday options, with their explanation, and nothing else. Turning it
    /// on adds the remaining options - all of them, still generated from the schema.
    /// </summary>
    public bool ShowAdvanced
    {
        get => _showAdvanced;
        set
        {
            if (SetProperty(ref _showAdvanced, value))
            {
                Refresh();
                Raise(nameof(ScopeLabel));
            }
        }
    }

    public string ScopeLabel => _showAdvanced
        ? $"{VisibleCount} of {TotalCount} options"
        : $"{VisibleCount} everyday options, {_allRows.Count - CountCore()} more available";

    /// <summary>
    /// Where the settings are kept, in one line. This is the answer to "does it remember what I
    /// set" - the window says so instead of leaving the user to find out on the next launch.
    /// </summary>
    public string SaveStatus
    {
        get
        {
            if (_autosave is null)
            {
                return "Changes save automatically";
            }

            if (_autosave.LastError is { Length: > 0 } error)
            {
                return $"Could not save: {error}";
            }

            return _autosave.LastSavedUtc is null
                ? $"Saved automatically to {_autosave.FilePath}"
                : $"Saved automatically to {_autosave.FilePath}, last write {_autosave.LastSavedUtc.Value.ToLocalTime():HH:mm:ss}";
        }
    }

    public int ConfiguredCount => _store.ConfiguredCount;

    public string ConfiguredLabel => $"{ConfiguredCount} of {TotalCount} set";

    public string Search
    {
        get => _search;
        set
        {
            if (SetProperty(ref _search, value))
            {
                Refresh();
            }
        }
    }

    public string SelectedGroup
    {
        get => _selectedGroup;
        set
        {
            if (SetProperty(ref _selectedGroup, value))
            {
                Refresh();
            }
        }
    }

    public int VisibleCount
    {
        get => _visibleCount;
        private set => SetProperty(ref _visibleCount, value);
    }

    public Profile? SelectedProfile
    {
        get => _selectedProfile;
        set
        {
            if (SetProperty(ref _selectedProfile, value))
            {
                ProfileName = value?.Name ?? "";
            }
        }
    }

    public string ProfileName
    {
        get => _profileName;
        set => SetProperty(ref _profileName, value);
    }

    /// <summary>The argument list that would be sent to the device, in the server's key=value form.</summary>
    public string ArgumentsPreview => ServerArguments.Preview(_store);

    /// <summary>The same set as CLI flags, for checking against upstream documentation.</summary>
    public string CliPreview => ServerArguments.AsCliFlags(_store);

    public string RawArguments
    {
        get => _store.RawArguments;
        set
        {
            _store.RawArguments = value;
            Raise();
            Raise(nameof(ArgumentsPreview));
            Raise(nameof(CliPreview));
        }
    }

    public void ApplySelectedProfile()
    {
        if (SelectedProfile is null)
        {
            return;
        }

        _store.ApplyProfileValues(SelectedProfile.Values);
        RefreshRowValues();
    }

    public void SaveProfile()
    {
        string name = string.IsNullOrWhiteSpace(ProfileName) ? "Untitled" : ProfileName.Trim();
        _profiles.SaveProfile(name, _store);

        Profiles.Clear();
        foreach (Profile profile in _profiles.Load())
        {
            Profiles.Add(profile);
        }

        SelectedProfile = Profiles.FirstOrDefault(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase));
    }

    public void DeleteSelectedProfile()
    {
        if (SelectedProfile is null)
        {
            return;
        }

        _profiles.Delete(SelectedProfile.Name);
        Profiles.Remove(SelectedProfile);
        SelectedProfile = null;
        ProfileName = "";
    }

    public void ResetAll()
    {
        _store.Reset();
        RefreshRowValues();
        RawArguments = "";
    }

    private int CountCore() =>
        _allRows.Count(r => ScrcpySchema.CoreOptionIds.Contains(r.Option.Id));

    private void Refresh()
    {
        string term = _search.Trim();

        IEnumerable<OptionRowViewModel> filtered = _allRows;

        if (!_showAdvanced)
        {
            filtered = filtered.Where(r => ScrcpySchema.CoreOptionIds.Contains(r.Option.Id));
        }

        if (_selectedGroup != AllGroups)
        {
            filtered = filtered.Where(r => string.Equals(r.Group, _selectedGroup, StringComparison.OrdinalIgnoreCase));
        }

        if (term.Length > 0)
        {
            filtered = filtered.Where(r =>
                r.Flag.Contains(term, StringComparison.OrdinalIgnoreCase) ||
                r.Option.Id.Contains(term, StringComparison.OrdinalIgnoreCase) ||
                r.Group.Contains(term, StringComparison.OrdinalIgnoreCase) ||
                r.Help.Contains(term, StringComparison.OrdinalIgnoreCase));
        }

        Rows.Clear();
        foreach (OptionRowViewModel row in filtered)
        {
            Rows.Add(row);
        }

        VisibleCount = Rows.Count;
        Raise(nameof(ScopeLabel));
        Raise(nameof(VisibleCount));
    }

    /// <summary>Rows cache their displayed state, so they have to be told when the store moved.</summary>
    private void RefreshRowValues()
    {
        foreach (OptionRowViewModel row in _allRows)
        {
            row.Refresh();
        }

        Raise(nameof(ArgumentsPreview));
        Raise(nameof(CliPreview));
    }
}
