using System.Text.Json;
using System.Text.Json.Serialization;

namespace Scrappy.Settings;

/// <summary>
/// Everything the PC app must remember between runs: the option values, the raw arguments, and the
/// address of the tablet it last talked to.
///
/// This file exists because the app used to start every launch by resetting itself to the factory
/// defaults - so a new build, or an installer that replaced the program folder, silently threw away
/// whatever the user had configured. The state lives under the user's application-data directory,
/// which no installer replaces and no version bump touches.
/// </summary>
public sealed class AppStateFile
{
    /// <summary>Bumped only when the shape changes in a way older builds cannot read.</summary>
    public const int CurrentVersion = 1;

    [JsonPropertyName("version")]
    public int Version { get; set; } = CurrentVersion;

    /// <summary>Last address used, in <c>host:port</c> form. Empty means none was chosen yet.</summary>
    [JsonPropertyName("address")]
    public string Address { get; set; } = "";

    /// <summary>Friendly name of the tablet last connected to, for the picker's empty state.</summary>
    [JsonPropertyName("lastDeviceName")]
    public string? LastDeviceName { get; set; }

    [JsonPropertyName("lastDeviceAddress")]
    public string? LastDeviceAddress { get; set; }

    /// <summary>Option id to raw value, the same shape a profile uses. Empty string = bare flag.</summary>
    [JsonPropertyName("options")]
    public Dictionary<string, string> Options { get; set; } = new(StringComparer.Ordinal);

    [JsonPropertyName("rawArguments")]
    public string RawArguments { get; set; } = "";

    /// <summary>
    /// Whether the stream carries sound. On by default: a mirror without audio is a surprising
    /// default for the thing this app is for.
    /// </summary>
    [JsonPropertyName("audioEnabled")]
    public bool AudioEnabled { get; set; } = true;

    /// <summary>0 to 100, applied in software so it never touches the system mixer.</summary>
    [JsonPropertyName("audioVolume")]
    public int AudioVolume { get; set; } = 100;

    /// <summary>Playback device name. Empty means the Windows default device.</summary>
    [JsonPropertyName("audioDevice")]
    public string AudioDevice { get; set; } = "";

    /// <summary>
    /// Where the window was last seen. Optional: a v1 file has no such section, and a build that
    /// predates it ignores the property, so adding it changes nothing for either side. Null means
    /// "never saved", which is not the same as 0,0,0,0 and must not be treated as a position.
    /// </summary>
    [JsonPropertyName("window")]
    public WindowPlacement? Window { get; set; }

    /// <summary>Informational: when this file was last written. Never used to decide anything.</summary>
    [JsonPropertyName("savedUtc")]
    public DateTime SavedUtc { get; set; }
}

/// <summary>
/// The last window frame, in device-independent pixels: the same units Avalonia's
/// <c>Window.Width</c>, <c>Window.Height</c> and <c>Bounds</c> use, so a 150% display and a 100%
/// display round-trip through it unchanged.
///
/// This is a separate type rather than four loose properties on <see cref="AppStateFile"/> so that
/// "there is no remembered frame" is one nullable thing instead of four numbers that have to be
/// checked for being zero, and so the geometry code can be tested without a settings store.
/// </summary>
public sealed class WindowPlacement
{
    [JsonPropertyName("left")]
    public double Left { get; set; }

    [JsonPropertyName("top")]
    public double Top { get; set; }

    [JsonPropertyName("width")]
    public double Width { get; set; }

    [JsonPropertyName("height")]
    public double Height { get; set; }

    /// <summary>
    /// True when the window was maximised. The frame above is then the last *normal* frame, so
    /// un-maximising after a restart returns the window to the size it had before it was maximised.
    /// </summary>
    [JsonPropertyName("maximised")]
    public bool Maximised { get; set; }

    /// <summary>
    /// A frame worth restoring: real numbers, and a size big enough to be a window. Anything else
    /// (a hand-edited file, a half-written one) is treated as "nothing remembered" rather than
    /// being clamped into a 1x1 window the user cannot resize back.
    /// </summary>
    [JsonIgnore]
    public bool IsUsable =>
        double.IsFinite(Left) && double.IsFinite(Top) &&
        double.IsFinite(Width) && double.IsFinite(Height) &&
        Width >= 1 && Height >= 1;

    public WindowPlacement Clone() => new()
    {
        Left = Left,
        Top = Top,
        Width = Width,
        Height = Height,
        Maximised = Maximised,
    };

    public bool SameAs(WindowPlacement? other) =>
        other is not null &&
        other.Left.Equals(Left) &&
        other.Top.Equals(Top) &&
        other.Width.Equals(Width) &&
        other.Height.Equals(Height) &&
        other.Maximised == Maximised;
}

/// <summary>What <see cref="AppStateStore.Load"/> found.</summary>
public sealed record LoadedState(AppStateFile State, bool Existed, string? Problem)
{
    /// <summary>
    /// True when there was nothing to restore, which is the only situation where factory defaults
    /// may be written. A file that exists but failed to parse is not a fresh install: it is a
    /// damaged one, and it is replaced on the next save rather than papered over with defaults.
    /// </summary>
    public bool IsFresh => !Existed;
}

/// <summary>
/// Reads and writes <see cref="AppStateFile"/>. Writes are atomic: a crash mid-save leaves the
/// previous file intact instead of a half-written one that parses as nothing.
/// </summary>
public sealed class AppStateStore
{
    /// <summary>Set to point the store somewhere else. Used by the tests, never by the app.</summary>
    public const string DirectoryOverrideVariable = "SCRAPPY_STATE_DIR";

    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    };

    public AppStateStore(string filePath)
    {
        FilePath = filePath;
    }

    public string FilePath { get; }

    /// <summary>
    /// The user's application data, not the program folder. An installer replaces the program
    /// folder; %APPDATA% survives upgrades, reinstalls and side-by-side versions.
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

    public static string DefaultPath() => Path.Combine(DefaultDirectory(), "state.json");

    /// <summary>Never throws: a missing, empty or damaged file reads as an empty state.</summary>
    public LoadedState Load()
    {
        if (!File.Exists(FilePath))
        {
            return new LoadedState(new AppStateFile(), Existed: false, Problem: null);
        }

        try
        {
            string text = File.ReadAllText(FilePath);
            if (string.IsNullOrWhiteSpace(text))
            {
                // An empty file is what an interrupted first save looks like. Treat it as fresh
                // rather than pretending the user configured nothing on purpose.
                return new LoadedState(new AppStateFile(), Existed: false, Problem: "the state file was empty");
            }

            AppStateFile? state = JsonSerializer.Deserialize<AppStateFile>(text, Json);
            if (state is null)
            {
                return new LoadedState(new AppStateFile(), Existed: false, Problem: "the state file held no values");
            }

            state.Options ??= new Dictionary<string, string>(StringComparer.Ordinal);
            state.Address ??= "";
            state.RawArguments ??= "";
            state.AudioDevice ??= "";
            state.AudioVolume = Math.Clamp(state.AudioVolume, 0, 100);

            // A frame that cannot be a window is dropped here, once, so every caller downstream can
            // treat a non-null Window as usable. That also keeps the clamp in the UI from having to
            // reason about NaN.
            if (state.Window is not null && !state.Window.IsUsable)
            {
                state.Window = null;
            }

            return new LoadedState(state, Existed: true, Problem: null);
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException or NotSupportedException)
        {
            // Keep the damaged file for inspection, but do not let it stop the app from starting.
            string? quarantine = Quarantine();
            return new LoadedState(
                new AppStateFile(),
                Existed: true,
                Problem: quarantine is null
                    ? $"the state file could not be read ({ex.Message})"
                    : $"the state file could not be read ({ex.Message}); it was moved to {quarantine}");
        }
    }

    /// <summary>Writes atomically. Throws only on a real failure, so the caller can report it.</summary>
    public void Save(AppStateFile state)
    {
        string? directory = Path.GetDirectoryName(FilePath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        state.Version = AppStateFile.CurrentVersion;
        state.SavedUtc = DateTime.UtcNow;

        string json = JsonSerializer.Serialize(state, Json);
        string temporary = FilePath + ".tmp";
        File.WriteAllText(temporary, json);

        try
        {
            // Replace is atomic where the platform supports it and keeps the original on failure.
            if (File.Exists(FilePath))
            {
                File.Replace(temporary, FilePath, destinationBackupFileName: null);
            }
            else
            {
                File.Move(temporary, FilePath);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
        {
            // File.Replace is not available on every filesystem (network shares, some containers).
            File.Move(temporary, FilePath, overwrite: true);
        }
    }

    /// <summary>Moves a damaged file out of the way so the next save can succeed.</summary>
    private string? Quarantine()
    {
        try
        {
            string target = Path.Combine(
                Path.GetDirectoryName(FilePath) ?? ".",
                $"state.damaged-{DateTime.Now:yyyyMMdd-HHmmss}.json");
            File.Move(FilePath, target, overwrite: true);
            return target;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}

/// <summary>
/// Keeps <see cref="AppStateStore"/> in step with what the user is doing, without the user having
/// to press anything.
///
/// Every change to the settings store, to the address, or to the remembered device schedules a
/// save; rapid changes coalesce into one write after <see cref="Debounce"/> of quiet, and
/// <see cref="Flush"/> writes immediately on shutdown. A failed write is reported through
/// <see cref="LastError"/> rather than thrown into the UI thread.
/// </summary>
public sealed class AppStateAutosave : IDisposable
{
    private readonly AppStateStore _store;
    private readonly SettingsStore _settings;
    private readonly object _gate = new();
    private readonly Timer _timer;

    private string _address = "";
    private bool _audioEnabled = true;
    private int _audioVolume = 100;
    private string _audioDevice = "";
    private string? _lastDeviceName;
    private string? _lastDeviceAddress;
    private WindowPlacement? _window;
    private bool _pending;
    private bool _disposed;

    public AppStateAutosave(AppStateStore store, SettingsStore settings, TimeSpan? debounce = null)
    {
        _store = store;
        _settings = settings;
        Debounce = debounce ?? TimeSpan.FromMilliseconds(400);

        _settings.Changed += OnSettingsChanged;
        _timer = new Timer(_ => Flush(), null, Timeout.Infinite, Timeout.Infinite);
    }

    /// <summary>How long changes are allowed to settle before a write. Zero writes on every change.</summary>
    public TimeSpan Debounce { get; }

    public string FilePath => _store.FilePath;

    /// <summary>Number of successful writes. Used by the verification tool and the settings footer.</summary>
    public int SaveCount { get; private set; }

    public DateTime? LastSavedUtc { get; private set; }

    /// <summary>Why the last write failed. Null when the last write succeeded.</summary>
    public string? LastError { get; private set; }

    public string Address
    {
        get => _address;
        set
        {
            if (string.Equals(_address, value, StringComparison.Ordinal))
            {
                return;
            }

            _address = value ?? "";
            Schedule();
        }
    }

    public bool AudioEnabled
    {
        get => _audioEnabled;
        set
        {
            if (_audioEnabled == value)
            {
                return;
            }

            _audioEnabled = value;
            Schedule();
        }
    }

    public int AudioVolume
    {
        get => _audioVolume;
        set
        {
            int clamped = Math.Clamp(value, 0, 100);
            if (_audioVolume == clamped)
            {
                return;
            }

            _audioVolume = clamped;
            Schedule();
        }
    }

    public string AudioDevice
    {
        get => _audioDevice;
        set
        {
            if (string.Equals(_audioDevice, value, StringComparison.Ordinal))
            {
                return;
            }

            _audioDevice = value ?? "";
            Schedule();
        }
    }

    public string? LastDeviceName
    {
        get => _lastDeviceName;
        set
        {
            if (string.Equals(_lastDeviceName, value, StringComparison.Ordinal))
            {
                return;
            }

            _lastDeviceName = value;
            Schedule();
        }
    }

    public string? LastDeviceAddress
    {
        get => _lastDeviceAddress;
        set
        {
            if (string.Equals(_lastDeviceAddress, value, StringComparison.Ordinal))
            {
                return;
            }

            _lastDeviceAddress = value;
            Schedule();
        }
    }

    /// <summary>
    /// The window frame to restore on the next launch. Written whenever the window moves or
    /// resizes, coalesced by the same debounce as everything else, so dragging a window does not
    /// write the file sixty times a second.
    /// </summary>
    public WindowPlacement? Window
    {
        get => _window;
        set
        {
            if (value is null ? _window is null : value.SameAs(_window))
            {
                return;
            }

            _window = value?.Clone();
            Schedule();
        }
    }

    /// <summary>Applies a loaded state. Does not schedule a save: nothing has changed yet.</summary>
    public void Restore(LoadedState loaded)
    {
        _address = loaded.State.Address ?? "";
        _audioEnabled = loaded.State.AudioEnabled;
        _audioVolume = loaded.State.AudioVolume;
        _audioDevice = loaded.State.AudioDevice ?? "";
        _lastDeviceName = loaded.State.LastDeviceName;
        _lastDeviceAddress = loaded.State.LastDeviceAddress;
        _window = loaded.State.Window?.Clone();
    }

    /// <summary>The values a save would write right now.</summary>
    public AppStateFile Snapshot() => new()
    {
        Address = _address,
        AudioEnabled = _audioEnabled,
        AudioVolume = _audioVolume,
        AudioDevice = _audioDevice,
        LastDeviceName = _lastDeviceName,
        LastDeviceAddress = _lastDeviceAddress,
        Window = _window?.Clone(),
        Options = _settings.ToProfileValues(),
        RawArguments = _settings.RawArguments,
    };

    public bool HasPendingSave
    {
        get
        {
            lock (_gate)
            {
                return _pending;
            }
        }
    }

    /// <summary>Writes now, and clears the pending flag. Safe to call from any thread, repeatedly.</summary>
    public void Flush()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _pending = false;
            TryStopTimer();

            try
            {
                _store.Save(Snapshot());
                SaveCount++;
                LastSavedUtc = DateTime.UtcNow;
                LastError = null;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
            {
                // Losing a save is bad; crashing the app over it is worse. The footer shows this.
                LastError = ex.Message;
            }
        }
    }

    private void OnSettingsChanged(object? sender, EventArgs e) => Schedule();

    private void Schedule()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _pending = true;

            if (Debounce <= TimeSpan.Zero)
            {
                // Deferred out of the caller's stack: Flush takes the same lock.
                TrySchedule(TimeSpan.Zero);
                return;
            }

            TrySchedule(Debounce);
        }
    }

    /// <summary>
    /// Timer calls that lose a race with <see cref="Dispose"/> are not errors: the state was
    /// already written by the flush in Dispose.
    /// </summary>
    private void TrySchedule(TimeSpan due)
    {
        try
        {
            _timer.Change(due, Timeout.InfiniteTimeSpan);
        }
        catch (ObjectDisposedException)
        {
            // Disposed underneath us; nothing left to save.
        }
    }

    private void TryStopTimer()
    {
        try
        {
            _timer.Change(Timeout.Infinite, Timeout.Infinite);
        }
        catch (ObjectDisposedException)
        {
            // Same.
        }
    }

    public void Dispose()
    {
        Flush();

        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
        }

        _settings.Changed -= OnSettingsChanged;
        _timer.Dispose();
    }
}
