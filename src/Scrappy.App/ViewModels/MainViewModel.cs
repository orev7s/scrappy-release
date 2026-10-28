using System.Collections.ObjectModel;
using System.Globalization;
using System.Net.Sockets;
using Avalonia.Threading;
using Scrappy.Discovery;
using Scrappy.App.Video;
using Scrappy.Settings;
using Scrappy.Stream;

namespace Scrappy.App.ViewModels;

/// <summary>
/// The session view. One connection, one video surface, and the five choices worth touching
/// without opening settings; everything else lives behind the settings list.
/// </summary>
public sealed class MainViewModel : ObservableObject, IDisposable
{
    private readonly SettingsStore _settings;
    private readonly ProfileStore _profiles;
    private readonly AppStateAutosave? _state;
    private readonly DeviceDiscovery _discovery = new();
    private readonly StreamStats _stats = new();
    private readonly DispatcherTimer _ticker;
    private readonly ObservableCollection<DiscoveredDevice> _devices = [];
    private MirrorSession? _session;
    private Task? _runTask;
    private bool _syncingPresets;
    private DiscoveredDevice? _selectedDevice;
    private bool _isScanning;
    private string _scanState = "No tablet found yet";
    private bool _scanned;
    private bool _showAddressEntry;

    private string _address = "";
    private string _status = "Not connected";
    private bool _isRunning;
    private string _fpsText = "-";
    private string _bitrateText = "-";
    private string _droppedText = "-";
    private string _decodeText = "-";
    private string _backendText = "-";
    private string _lagText = "-";
    private string _linkText = "";
    private string _deviceText = "";
    private DailyPresets.Choice? _selectedResolution;
    private DailyPresets.Choice? _selectedFrameRate;
    private string _bitrateMbps = "12";
    private string _bitrateWarning = "";
    private DailyPresets.Choice? _selectedCodec;
    private DailyPresets.Choice? _selectedQuality;
    private bool _hasFacts;
    private string _deviceSummary = "";
    private string _recommendation = "";
    private PresetAdvisor.Advice? _advice;
    private LinkMap.WiredState _wiredState = LinkMap.WiredState.None;
    private bool _audioEnabled = true;
    private int _audioVolume = 100;
    private string _audioDevice = "";
    private IAudioSink? _audioSink;
    private string _audioText = "off";
    private IReadOnlyList<string> _audioDevices = ["Default device"];

    public MainViewModel(SettingsStore settings, ProfileStore profiles, AppStateAutosave? state = null)
    {
        _settings = settings;
        _profiles = profiles;
        _state = state;
        _address = state?.Address ?? "";
        _settings.Changed += (_, _) => RefreshPresets();

        Presenter = new BitmapVideoPresenter { Stats = _stats };
        Presenter.SessionStarted += OnSessionStarted;

        // SessionEnded is deliberately not subscribed. The presenter is shared by every session, so
        // its event cannot tell a stale session from the current one - and a stale one firing after
        // a reconnect is exactly how the window ended up claiming "the device stopped the stream"
        // while a new stream was already playing. The run continuation knows which session it
        // belongs to, so it owns that update.

        if (state is not null)
        {
            _audioEnabled = state.AudioEnabled;
            _audioVolume = state.AudioVolume;
            _audioDevice = state.AudioDevice;
        }

        RefreshAudioDevices();

        _ticker = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
        _ticker.Tick += (_, _) => UpdateStats();
        _ticker.Start();

        RefreshPresets();
    }

    public BitmapVideoPresenter Presenter { get; }

    public bool HasFacts
    {
        get => _hasFacts;
        private set => SetProperty(ref _hasFacts, value);
    }

    /// <summary>What the tablet said about itself. Empty until the check has run.</summary>
    public string DeviceSummary
    {
        get => _deviceSummary;
        private set => SetProperty(ref _deviceSummary, value);
    }

    /// <summary>The suggestion and why. Never applied automatically.</summary>
    public string Recommendation
    {
        get => _recommendation;
        private set => SetProperty(ref _recommendation, value);
    }

    /// <summary>
    /// Asks the tablet for its display and encoder facts. Read-only: nothing is changed.
    ///
    /// Nothing in here is allowed to throw. This runs from a button on the main window, so an
    /// exception ends the process - and it did: a tablet that reported no hardware H.265 encoder
    /// hit a missing preset in the advisor and closed the whole app.
    /// </summary>
    public async Task CheckDeviceAsync()
    {
        if (!TryParseAddress(Address, out string host, out int port, out string? error))
        {
            Status = error!;
            return;
        }

        Status = "Checking tablet";

        try
        {
            DeviceFacts? facts = await DeviceProbe.QueryAsync(host, port).ConfigureAwait(true);

            if (facts is null)
            {
                HasFacts = false;
                DeviceSummary = "";
                Recommendation = "";
                Status = $"No answer from {host} on port {port}. Is the tablet app running, and is the PC on the same network?";
                return;
            }

            DeviceSummary = $"{facts.Model} - Android {facts.Android} - {facts.Width} x {facts.Height} at {facts.RefreshHz:0.#} Hz";
            _advice = PresetAdvisor.Recommend(facts);
            Recommendation = _advice.Reason;
            HasFacts = true;
            Status = "Tablet checked";
        }
        catch (Exception ex)
        {
            // A check that fails is a message, not the end of the program.
            HasFacts = false;
            DeviceSummary = "";
            Recommendation = "";
            Status = $"Check tablet failed: {ex.GetType().Name}: {ex.Message}";
        }
    }

    /// <summary>Applies the suggestion, only when the user asks for it.</summary>
    public void ApplyRecommendation()
    {
        if (_advice is null)
        {
            return;
        }

        DailyPresets.Apply(_settings, _advice.Preset);
        Status = "Applied " + _advice.Preset.Label;
    }

    public SettingsStore Settings => _settings;

    public ProfileStore Profiles => _profiles;

    /// <summary>The persistent state, or null when the app was started without one.</summary>
    public AppStateAutosave? State => _state;

    public ScrcpySchema Schema => _settings.Schema;

    /// <summary>
    /// The tablet's address, host:port. Set by the picker, or typed in by hand when discovery is
    /// blocked. Every change is written to the state file, so the next launch starts here.
    /// </summary>
    public string Address
    {
        get => _address;
        set
        {
            string next = (value ?? "").Trim();
            if (!SetProperty(ref _address, next))
            {
                return;
            }

            if (_state is not null)
            {
                _state.Address = next;
            }
        }
    }

    // --- finding the tablet, so the address never has to be typed -------------------------

    /// <summary>Tablets that answered on this network, newest scan first.</summary>
    public ObservableCollection<DiscoveredDevice> Devices => _devices;

    public bool HasDevices => _devices.Count > 0;

    public bool IsScanning
    {
        get => _isScanning;
        private set
        {
            if (SetProperty(ref _isScanning, value))
            {
                Raise(nameof(ScanLabel));
                Raise(nameof(CanBrowse));
            }
        }
    }

    public bool CanBrowse => !_isScanning;

    public string ScanLabel => _isScanning ? "Scanning" : "Scan";

    /// <summary>
    /// The typed-address field. Hidden until it is needed: when discovery finds nothing it opens
    /// itself, because at that point typing is the only way forward.
    /// </summary>
    public bool ShowAddressEntry
    {
        get => _showAddressEntry;
        set => SetProperty(ref _showAddressEntry, value);
    }

    /// <summary>What the scan is doing, or what it found. Shown only when the list is empty.</summary>
    public string ScanState
    {
        get => _scanState;
        private set => SetProperty(ref _scanState, value);
    }

    /// <summary>The device the picker shows. Choosing one fills the address field.</summary>
    public DiscoveredDevice? SelectedDevice
    {
        get => _selectedDevice;
        set
        {
            if (!SetProperty(ref _selectedDevice, value) || value is null)
            {
                return;
            }

            Address = value.AddressText;
            Status = $"Tablet at {value.AddressText}";
        }
    }

    /// <summary>
    /// Fills the picker with fixed entries. Used by <c>--demo-device</c> so the window can be
    /// captured with no network; the app never calls this with real data.
    /// </summary>
    public void SeedDemoDevices(IEnumerable<DiscoveredDevice> devices)
    {
        MergeDevices(new DiscoveryResult(
            devices.ToList(),
            devices.Any(d => d.IsWired) ? LinkMap.WiredState.Tethering : LinkMap.WiredState.None,
            0));
        _scanned = true;
    }

    /// <summary>
    /// Looks for tablets once, automatically, the first time the window is shown. A network with
    /// one tablet then needs no clicks at all: the picker selects it.
    /// </summary>
    public async Task ScanOnceAsync()
    {
        if (_scanned || IsScanning)
        {
            return;
        }

        _scanned = true;
        await ScanAsync().ConfigureAwait(true);
    }

    public async Task ScanAsync(CancellationToken cancellationToken = default)
    {
        if (IsScanning)
        {
            return;
        }

        IsScanning = true;
        ScanState = "Looking for tablets";

        try
        {
            var progress = new Progress<DiscoveryProgress>(report => ScanState = report.Message);
            DiscoveryResult result = await _discovery
                .ScanAsync(probeWindow: null, progress, cancellationToken)
                .ConfigureAwait(true);

            MergeDevices(result);
        }
        catch (Exception ex) when (ex is SocketException or IOException or UnauthorizedAccessException)
        {
            // Scanning is a convenience. A network that forbids it must leave manual entry usable.
            ScanState = "Could not search the network: " + ex.Message;
        }
        finally
        {
            IsScanning = false;
            Raise(nameof(HasDevices));
        }
    }

    /// <summary>
    /// Replaces the list without losing the user's choice: the same tablet is re-selected if it is
    /// still there, the remembered one is preferred over an arbitrary first entry, and a single
    /// result is selected outright because there is nothing to choose between.
    /// </summary>
    private void MergeDevices(DiscoveryResult result)
    {
        IReadOnlyList<DiscoveredDevice> found = result.Devices;
        string? previousKey = _selectedDevice?.Key ?? _state?.LastDeviceAddress;
        string? previousAddress = _selectedDevice?.AddressText;

        _devices.Clear();

        // The cable goes first: it is the stable link, and on a tablet that answers on both it is
        // the one to use for a stream.
        foreach (DiscoveredDevice device in found
                     .OrderByDescending(d => d.IsWired)
                     .ThenBy(d => d.DisplayName, StringComparer.OrdinalIgnoreCase))
        {
            _devices.Add(device);
        }

        _wiredState = result.Wired;
        Raise(nameof(HasDevices));

        // One place decides the wording, and every branch of it names the next action: the old
        // "no tablet answered" told the user nothing about a cable that was plugged in.
        ScanState = DeviceDiscovery.Describe(found, result.Wired);

        if (found.Count == 0 && string.IsNullOrWhiteSpace(Address))
        {
            // Nothing to pick. Offer the only remaining route instead of leaving a dead list.
            ShowAddressEntry = true;
        }

        DiscoveredDevice? match =
            found.FirstOrDefault(d => previousKey is not null && d.Key == previousKey) ??
            found.FirstOrDefault(d => previousAddress is not null && d.AddressText == previousAddress);

        if (match is null && found.Count == 1)
        {
            match = found[0];
        }

        if (match is null)
        {
            // A cable that is present is the better default: no Wi-Fi contention, no router.
            DiscoveredDevice[] wired = found.Where(d => d.IsWired).ToArray();
            if (wired.Length == 1)
            {
                match = wired[0];
            }
        }

        if (match is null && !string.IsNullOrWhiteSpace(Address))
        {
            // The remembered address is not in the list; keep showing it rather than clearing it.
            match = found.FirstOrDefault(d => d.AddressText == Address);
        }

        if (match is not null)
        {
            SelectedDevice = match;
        }
        else if (_selectedDevice is not null)
        {
            // The tablet that was selected has gone. Keep its address in the field - the user may
            // still want to retry it - but stop claiming it is in the list.
            _selectedDevice = null;
            Raise(nameof(SelectedDevice));
        }
    }

    public string Status
    {
        get => _status;
        private set => SetProperty(ref _status, value);
    }

    /// <summary>Lets startup code say something once, e.g. that the state file could not be read.</summary>
    public void Report(string message) => Status = message;

    public bool IsRunning
    {
        get => _isRunning;
        private set
        {
            if (SetProperty(ref _isRunning, value))
            {
                Raise(nameof(StartLabel));
                Raise(nameof(IsIdle));
            }
        }
    }

    public bool IsIdle => !_isRunning;

    public string StartLabel => _isRunning ? "Stop" : "Start";

    public string DeviceText
    {
        get => _deviceText;
        private set => SetProperty(ref _deviceText, value);
    }

    public string FpsText
    {
        get => _fpsText;
        private set => SetProperty(ref _fpsText, value);
    }

    public string BitrateText
    {
        get => _bitrateText;
        private set => SetProperty(ref _bitrateText, value);
    }

    public string DroppedText
    {
        get => _droppedText;
        private set => SetProperty(ref _droppedText, value);
    }

    public string DecodeText
    {
        get => _decodeText;
        private set => SetProperty(ref _decodeText, value);
    }

    public string BackendText
    {
        get => _backendText;
        private set => SetProperty(ref _backendText, value);
    }

    /// <summary>
    /// How far behind the picture is, and what the receiver did about it. This is the number that
    /// answers "why is it laggy": frame rate can look perfect while the frame on screen is seconds
    /// old, and only the queue delay shows that.
    /// </summary>
    public string LagText
    {
        get => _lagText;
        private set => SetProperty(ref _lagText, value);
    }

    /// <summary>
    /// Whether the stream carries sound.
    ///
    /// The audio is rendered into a Windows playback device rather than handed to the mirror
    /// window, because no program can take audio from a window: OBS captures a device or a
    /// process. Point OBS at this device, or at Scrappy with Application Audio Capture, and the
    /// stream has sound. Turning this off is for people who want a silent mirror, not the way to
    /// mute it locally - the volume slider and the device picker do that.
    /// </summary>
    public bool AudioEnabled
    {
        get => _audioEnabled;
        set
        {
            if (SetProperty(ref _audioEnabled, value))
            {
                if (_state is not null)
                {
                    _state.AudioEnabled = value;
                }

                Raise(nameof(AudioSummary));
            }
        }
    }

    /// <summary>Applied in software, so it changes what is heard and not what is captured.</summary>
    public int AudioVolume
    {
        get => _audioVolume;
        set
        {
            if (SetProperty(ref _audioVolume, Math.Clamp(value, 0, 100)))
            {
                if (_audioSink is not null)
                {
                    _audioSink.Volume = _audioVolume;
                }

                if (_state is not null)
                {
                    _state.AudioVolume = _audioVolume;
                }

                Raise(nameof(AudioSummary));
            }
        }
    }

    /// <summary>Playback device for the mirrored audio. Empty means the Windows default.</summary>
    public string AudioDevice
    {
        get => _audioDevice;
        set
        {
            if (SetProperty(ref _audioDevice, value ?? ""))
            {
                if (_state is not null)
                {
                    _state.AudioDevice = _audioDevice;
                }
            }
        }
    }

    public IReadOnlyList<string> AudioDevices
    {
        get => _audioDevices;
        private set => SetProperty(ref _audioDevices, value);
    }

    /// <summary>One line under the controls: what the audio is doing right now.</summary>
    public string AudioSummary
    {
        get
        {
            if (!_audioEnabled)
            {
                return "off";
            }

            return _audioText;
        }
    }

    /// <summary>Live counters, for the status strip.</summary>
    public string AudioText
    {
        get => _audioText;
        private set => SetProperty(ref _audioText, value);
    }

    /// <summary>Which link the selected tablet is on, and what to do about it.</summary>
    public string LinkText
    {
        get => _linkText;
        private set => SetProperty(ref _linkText, value);
    }

    public string SettingsSummary
    {
        get
        {
            int set = _settings.ConfiguredCount;
            return set == 0 ? "all defaults" : $"{set} set";
        }
    }

    // --- the five daily choices, all of which write ordinary schema options ---------------

    public IReadOnlyList<DailyPresets.Choice> ResolutionChoices => DailyPresets.Resolution;

    public IReadOnlyList<DailyPresets.Choice> FrameRateChoices => DailyPresets.FrameRate;

    /// <summary>
    /// Bitrate in Mbps, typed rather than picked: the encoder accepts far more than any fixed list
    /// would offer, and capping the list would be a lie about what the setting does.
    /// </summary>
    public string BitrateMbps
    {
        get => _bitrateMbps;
        set
        {
            if (!SetProperty(ref _bitrateMbps, value))
            {
                return;
            }

            ApplyBitrate(value);
        }
    }

    /// <summary>Empty when the value is usable; otherwise says what is wrong or risky.</summary>
    public string BitrateWarning
    {
        get => _bitrateWarning;
        private set => SetProperty(ref _bitrateWarning, value);
    }

    private void ApplyBitrate(string text)
    {
        if (!double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out double mbps) || mbps <= 0)
        {
            BitrateWarning = "Enter a number of Mbps.";
            return;
        }

        if (mbps > MaxBitrateMbps)
        {
            BitrateWarning = $"The encoder's field is 32-bit; above {MaxBitrateMbps:0} Mbps it overflows.";
            return;
        }

        _settings.SetValue("video_bit_rate", ((long)Math.Round(mbps * 1_000_000)).ToString(CultureInfo.InvariantCulture));

        BitrateWarning = mbps > 200
            ? "Above what Wi-Fi usually sustains. The link will back up and the tablet will drop frames."
            : "";
    }

    /// <summary>2000 Mbps is 2 Gbps, and also exactly the largest value the encoder field holds.</summary>
    public const double MaxBitrateMbps = 2000;

    public IReadOnlyList<DailyPresets.Choice> CodecChoices => DailyPresets.Codec;

    public IReadOnlyList<DailyPresets.Choice> QualityChoices => DailyPresets.Quality;

    public DailyPresets.Choice? SelectedResolution
    {
        get => _selectedResolution;
        set
        {
            if (SetProperty(ref _selectedResolution, value) && value is not null && !_syncingPresets)
            {
                DailyPresets.Apply(_settings, value);
            }
        }
    }

    public DailyPresets.Choice? SelectedFrameRate
    {
        get => _selectedFrameRate;
        set
        {
            if (SetProperty(ref _selectedFrameRate, value) && value is not null && !_syncingPresets)
            {
                DailyPresets.Apply(_settings, value);
            }
        }
    }

    public DailyPresets.Choice? SelectedCodec
    {
        get => _selectedCodec;
        set
        {
            if (SetProperty(ref _selectedCodec, value) && value is not null && !_syncingPresets)
            {
                DailyPresets.Apply(_settings, value);
            }
        }
    }

    /// <summary>
    /// Choosing a preset only selects it. Applying is a separate click, because a preset changes
    /// what the tablet encodes and that is not something to do by accident.
    /// </summary>
    public DailyPresets.Choice? SelectedQuality
    {
        get => _selectedQuality;
        set
        {
            if (SetProperty(ref _selectedQuality, value))
            {
                Raise(nameof(PresetNote));
            }
        }
    }

    public string PresetNote => _selectedQuality?.Note ?? "";

    public void ApplyQualityPreset()
    {
        if (_selectedQuality is null)
        {
            return;
        }

        _syncingPresets = true;
        try
        {
            DailyPresets.Apply(_settings, _selectedQuality);
            SyncDailyFields();
        }
        finally
        {
            _syncingPresets = false;
        }

        Status = "Applied " + _selectedQuality.Label;
    }

    /// <summary>Pulls the four primary controls back from the store after a preset or profile moved them.</summary>
    private void SyncDailyFields()
    {
        string? size = _settings.GetValue("max_size");
        SelectedResolution = DailyPresets.Resolution.FirstOrDefault(c => size is not null && c.Values.TryGetValue("max_size", out string? v) && v == size);

        string? fps = _settings.GetValue("max_fps");
        SelectedFrameRate = DailyPresets.FrameRate.FirstOrDefault(c => fps is not null && c.Values.TryGetValue("max_fps", out string? v) && v == fps);

        string? codec = _settings.GetValue("video_codec");
        SelectedCodec = DailyPresets.Codec.FirstOrDefault(c => codec is not null && c.Values.TryGetValue("video_codec", out string? v) && v == codec);

        string? bitrate = _settings.GetValue("video_bit_rate");
        if (long.TryParse(bitrate, out long bps))
        {
            _bitrateMbps = (bps / 1_000_000.0).ToString("0.###", CultureInfo.InvariantCulture);
            Raise(nameof(BitrateMbps));
        }
    }

    private void RefreshPresets()
    {
        // Presets are derived from option values, so a change made in the settings list shows up
        // here. The flag stops the setters writing the same values straight back.
        _syncingPresets = true;
        try
        {
            SelectedResolution = DailyPresets.Match(_settings, DailyPresets.Resolution);
            SelectedFrameRate = DailyPresets.Match(_settings, DailyPresets.FrameRate);
            SelectedCodec = DailyPresets.Match(_settings, DailyPresets.Codec);
            SelectedQuality = DailyPresets.Match(_settings, DailyPresets.Quality);
            Raise(nameof(PresetNote));
            Raise(nameof(SettingsSummary));

            // The bitrate box is typed, not picked, so a restored value has to be written into it
            // here. Without this the box showed the hard-coded starting value while the stored
            // setting said otherwise - and pressing nothing kept the lie on screen.
            string? bitrate = _settings.GetValue("video_bit_rate");
            if (long.TryParse(bitrate, NumberStyles.Integer, CultureInfo.InvariantCulture, out long bits) && bits > 0)
            {
                string text = (bits / 1_000_000.0).ToString("0.###", CultureInfo.InvariantCulture);
                if (!string.Equals(_bitrateMbps, text, StringComparison.Ordinal))
                {
                    _bitrateMbps = text;
                    Raise(nameof(BitrateMbps));
                }
            }
        }
        finally
        {
            _syncingPresets = false;
        }
    }

    // --- session --------------------------------------------------------------------------

    public async Task ToggleAsync()
    {
        if (IsRunning)
        {
            await StopAsync();
        }
        else
        {
            await StartAsync();
        }
    }

    public async Task StartAsync()
    {
        if (IsRunning)
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(Address) && _devices.Count > 0)
        {
            // The list is right there; there is no reason to make the user read an error instead.
            SelectedDevice ??= _devices[0];
        }

        if (!TryParseAddress(Address, out string host, out int port, out string? error))
        {
            Status = error!;
            return;
        }

        if (IsLoopback(host))
        {
            // 127.0.0.1 is this PC. The tablet is a different machine, and this is the mistake the
            // old default address invited.
            Status = "That address is this PC. Pick the tablet from the list, or type its address, e.g. 192.168.1.23:27183.";
            return;
        }

        Status = $"Connecting to {host}:{port}";
        Presenter.ClearPending();
        _stats.Reset();

        // The sink is created per session so a device that was unplugged gets a clean open, and so
        // the volume slider has something live to talk to.
        _audioSink?.Dispose();
        IAudioSink? sink = null;

        if (AudioEnabled)
        {
            if (OperatingSystem.IsWindows())
            {
                sink = new WaveOutSink(AudioDevice) { Volume = AudioVolume };
                _audioSink = sink;
            }
            else
            {
                // Honest rather than silent: there is no Windows audio layer to render into here.
                AudioText = "no audio on this platform";
            }
        }
        else
        {
            AudioText = "off";
        }

        try
        {
            _session = await MirrorSession.ConnectAsync(
                host,
                port,
                Presenter,
                new MirrorOptions
                {
                    HardwareDecode = _settings.GetValue("hwdec") ?? "disabled",
                    LowLatency = false,
                    Request = BuildRequest(),
                    DumpPath = Program.Options.DumpPath,
                    Audio = new AudioOptions { Enabled = AudioEnabled && sink is not null },
                    AudioSink = sink,
                },
                _stats);
        }
        catch (DllNotFoundException)
        {
            // First run on a machine without the libraries: fetch them, then try once more. The
            // retry is the only honest way to report success, since the error can come from any
            // of the load paths.
            Status = "FFmpeg is missing, installing";
            try
            {
                var progress = new Progress<string>(message => Status = message);
                await FFmpegInstaller.EnsureAsync(progress);

                _session = await MirrorSession.ConnectAsync(
                    host, port, Presenter,
                    new MirrorOptions
                    {
                        HardwareDecode = _settings.GetValue("hwdec") ?? "disabled",
                        LowLatency = false,
                        Request = BuildRequest(),
                        DumpPath = Program.Options.DumpPath,
                        Audio = new AudioOptions { Enabled = AudioEnabled && sink is not null },
                        AudioSink = sink,
                    },
                    _stats);
            }
            catch (Exception installFailure)
            {
                Status = installFailure.Message;
                return;
            }
        }
        catch (Exception ex) when (ex is IOException or TimeoutException or InvalidOperationException or SocketException)
        {
            Status = ex.Message;
            return;
        }

        DeviceText = _session.DeviceName;
        Status = "Running";
        IsRunning = true;

        if (_state is not null)
        {
            // Remembered immediately, not on exit: a session that ends in a crash should still
            // bring the same tablet back next time.
            _state.LastDeviceName = _session.DeviceName;
            _state.LastDeviceAddress = $"{host}:{port}";
            _state.Flush();
        }

        if (_selectedDevice is null || _selectedDevice.AddressText != $"{host}:{port}")
        {
            _selectedDevice = _devices.FirstOrDefault(d => d.AddressText == $"{host}:{port}");
            Raise(nameof(SelectedDevice));
        }

        MirrorSession session = _session;
        _runTask = Task.Run(async () =>
        {
            await session.RunAsync().ConfigureAwait(false);

            // Ask the tablet what it was doing when the stream ended, off the UI thread. A session
            // that the user stopped is not a failure and is not worth a round trip.
            string reason = session.EndReason ?? "stopped";
            if (!string.Equals(reason, "stopped", StringComparison.Ordinal))
            {
                reason = await EndExplanation.DescribeAsync(session.Host, session.Port, reason)
                    .ConfigureAwait(false);
            }

            await Dispatcher.UIThread.InvokeAsync(async () =>
            {
                // The user may already have stopped and started again. A session that is no longer
                // the current one must never write to the status line: doing exactly that made a
                // fresh stream look dead the moment the old one finished unwinding.
                if (!ReferenceEquals(_session, session))
                {
                    await session.DisposeAsync();
                    return;
                }

                _session = null;
                _runTask = null;
                IsRunning = false;
                Status = reason;
                await session.DisposeAsync();
            });
        });
    }

    /// <summary>Translates the primary settings into the one line the tablet reads at connect.</summary>
    private SessionRequest BuildRequest()
    {
        int longestEdge = int.TryParse(_settings.GetValue("max_size"), out int size) && size > 0 ? size : 2560;
        int fps = int.TryParse(_settings.GetValue("max_fps"), out int f) && f > 0 ? f : 60;
        int bitrate = int.TryParse(_settings.GetValue("video_bit_rate"), out int b) && b > 0 ? b : 12_000_000;

        // 3:2 is the panel's shape. Facts from the tablet could refine this later; until then the
        // aspect is the one this project targets.
        (int width, int height) = SessionRequest.SizeForLongestEdge(longestEdge, 3200, 2136);

        return new SessionRequest
        {
            Width = width,
            Height = height,
            Codec = _settings.GetValue("video_codec") ?? "h265",
            Bitrate = bitrate,
            Fps = fps,
        };
    }

    public async Task StopAsync()
    {
        MirrorSession? session = _session;
        if (session is null)
        {
            return;
        }

        Status = "Stopping";

        // Stop closes the socket, not just the token. Without that the read loop stays blocked on
        // a connection the tablet still considers alive, the tablet keeps encoding, and neither
        // end can start again - the "it still says Connected" bug.
        session.Stop();

        if (_runTask is not null)
        {
            // Give the read loop a moment to unwind so the status line updates before the click
            // returns. The socket is already closed either way.
            await Task.WhenAny(_runTask, Task.Delay(1500));
        }

        // Disposing here as well as in the run continuation is safe: it is idempotent, and it
        // guarantees the reference is released even if the loop is wedged.
        await session.DisposeAsync();

        _session = null;
        _runTask = null;
        IsRunning = false;
        Status = "Stopped";
    }

    private static bool IsLoopback(string host) =>
        host is "127.0.0.1" or "localhost" or "::1";

    private static bool TryParseAddress(string address, out string host, out int port, out string? error)
    {
        host = "";
        port = 27183;
        error = null;

        string[] parts = address.Trim().Split(':');
        if (parts.Length == 0 || string.IsNullOrWhiteSpace(parts[0]))
        {
            error = "Pick a tablet from the list, or type its address, e.g. 192.168.1.23:27183.";
            return false;
        }

        host = parts[0].Trim();

        if (parts.Length > 1 && !int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out port))
        {
            error = "The port must be a number.";
            return false;
        }

        if (port is < 1 or > 65535)
        {
            error = "The port must be between 1 and 65535.";
            return false;
        }

        return true;
    }

    /// <summary>
    /// Fills the device list. Called at startup and by the refresh button, because devices appear
    /// and disappear while the app is running (a headset plugged in, a virtual cable installed
    /// later). Never throws: a machine without audio still gets a working window.
    /// </summary>
    public void RefreshAudioDevices()
    {
        try
        {
            AudioDevices = OperatingSystem.IsWindows() ? WaveOutSink.Devices() : ["Default device"];
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            AudioDevices = ["Default device"];
        }
    }

    private void OnSessionStarted(string deviceName, int width, int height)
    {
        Dispatcher.UIThread.Post(() =>
        {
            DeviceText = $"{deviceName}   {width} x {height}";
        });
    }

    /// <summary>
    /// Which link the selected tablet is on, in the terms that decide the experience: the cable
    /// does not share the Wi-Fi, and a Wi-Fi stream on a busy channel is the usual reason a mirror
    /// stutters while both ends look healthy.
    /// </summary>
    private string DescribeLink()
    {
        DiscoveredDevice? device = _selectedDevice;

        if (device is not null && device.IsWired)
        {
            return "over the cable";
        }

        if (device is not null && device.Transport == "wifi")
        {
            return _wiredState == LinkMap.WiredState.Tethering
                ? "over Wi-Fi - the cable is ready, pick the Wired entry to stop the stutter"
                : "over Wi-Fi";
        }

        return _wiredState switch
        {
            LinkMap.WiredState.Tethering => "cable ready",
            LinkMap.WiredState.CableWithoutTether => "cable in, USB tethering off",
            _ => "",
        };
    }

    private void UpdateStats()
    {
        _stats.RollWindow();

        FpsText = _stats.Fps > 0 ? $"{_stats.Fps:F1} fps" : "-";
        BitrateText = _stats.MegabitsPerSecond > 0 ? $"{_stats.MegabitsPerSecond:F1} Mbps" : "-";

        LagText = _stats.FramesReceived == 0
            ? "-"
            : _stats.QueueDelayMs < 1
                ? "in time"
                : $"lag {_stats.QueueDelayMs:F0} ms";

        // "Skipped", not "dropped": these frames arrived and decoded fine, then a newer frame
        // replaced them before the display could draw them. At a 60 Hz display and a 60 fps
        // source a few per second are normal and cost nothing. The receiver's own skips are a
        // different animal - the machine could not keep up - so they are named separately.
        long skipped = Presenter.DroppedUnrendered;
        long skippedByReceiver = _stats.FramesSkippedByReceiver;
        DroppedText = (skipped, skippedByReceiver) switch
        {
            (0, 0) => "none skipped",
            (_, 0) => $"{skipped} skipped",
            (0, _) => $"{skippedByReceiver} late, jumped to keyframe",
            _ => $"{skippedByReceiver} late, jumped to keyframe; {skipped} replaced",
        };

        DecodeText = _stats.FramesDecoded > 0 ? $"{_stats.DecodeMsAverage:F1} ms decode" : "-";
        BackendText = _stats.FramesDecoded > 0 ? $"{_stats.Codec}/{_stats.Backend}" : "-";
        LinkText = DescribeLink();

        // Three states worth telling apart: playing, switched off, and failed to open.
        AudioText = _session?.AudioStats?.State
                    ?? _stats.AudioError
                    ?? (_audioEnabled ? "waiting" : "off");
        Raise(nameof(SettingsSummary));
    }

    public void Dispose()
    {
        _ticker.Stop();
        _session?.Stop();
        _session?.DisposeAsync().AsTask().Wait(500);
        _state?.Flush();
    }
}
