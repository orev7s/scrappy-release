using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Scrappy.Stream;

/// <summary>
/// Plays decoded audio into a Windows playback device, so that OBS (or any other capture program)
/// can take it from there.
///
/// Why a playback device and not "just hand OBS the audio": OBS cannot take audio from a window.
/// Window capture is a video path, and no amount of work on this side changes that. What OBS can
/// capture is a device (Desktop Audio, or a capture device pointing at a virtual cable) or a
/// process (Application Audio Capture). Both of those need the audio to be rendered through the
/// Windows audio engine, which is what this class does.
///
/// It uses waveOut rather than WASAPI on purpose: waveOut gives device enumeration, playback and
/// an honest error code through six P/Invokes and no COM interop, and this file cannot be tested on
/// the machine that wrote it. Fewer moving parts that cannot be run is the only defensible choice.
///
/// Volume is applied in software before the samples are handed to the driver, so it never touches
/// the system mixer. That is deliberate: turning Scrappy down to hear nothing should not be the
/// same action as turning off what OBS receives.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class WaveOutSink : IAudioSink
{
    private const int WaveMapper = unchecked((int)0xFFFFFFFF);
    private const int MmSysErrNoError = 0;

    /// <summary>Three buffers of about 40 ms each: 120 ms of slack, which is one Bluetooth hop.</summary>
    private const int BufferCount = 3;

    /// <summary>Slots are this many bytes. One AAC frame at 48 kHz is 4096 bytes, so this fits one.</summary>
    private const int BufferBytes = 4096;

    private const uint WhdrDone = 0x00000001;
    private const uint WhdrPrepared = 0x00000002;

    /// <summary>How long a slot may take to drain before its audio is dropped instead of delayed.</summary>
    private const int DrainTimeoutMs = 40;

    private readonly string _deviceName;
    private readonly object _gate = new();

    private IntPtr _handle;
    private GCHandle _arenaPin;
    private GCHandle _headerPin;
    private byte[] _arena = [];
    private WaveHeader[] _headers = [];

    private int _slot;
    private int _fill;
    private bool _started;
    private int _volume = 100;
    private string? _lastError;

    /// <summary><paramref name="deviceName"/> is matched case-insensitively; empty means the default device.</summary>
    public WaveOutSink(string? deviceName = null)
    {
        _deviceName = deviceName ?? "";
    }

    public int Volume
    {
        get => Volatile.Read(ref _volume);
        set => Volatile.Write(ref _volume, Math.Clamp(value, 0, 100));
    }

    public string Describe { get; private set; } = "not started";

    public string? LastError => _lastError;

    public long DroppedChunks { get; private set; }

    /// <summary>Playback devices, in the order waveOut numbers them. Index 0 is the default device.</summary>
    public static IReadOnlyList<string> Devices()
    {
        if (!OperatingSystem.IsWindows())
        {
            return [];
        }

        var names = new List<string> { "Default device" };
        int count = waveOutGetNumDevs();

        for (int i = 0; i < count; i++)
        {
            var caps = new WaveOutCaps();
            int result = waveOutGetDevCapsW(new IntPtr(i), ref caps, Marshal.SizeOf<WaveOutCaps>());
            names.Add(result == MmSysErrNoError ? caps.szPname : $"Device {i}");
        }

        return names;
    }

    public void Start(int sampleRate, int channels)
    {
        lock (_gate)
        {
            if (_started)
            {
                return;
            }

            try
            {
                IntPtr device = ResolveDevice(_deviceName);

                var format = new WaveFormatEx
                {
                    wFormatTag = 1, // WAVE_FORMAT_PCM
                    nChannels = (ushort)channels,
                    nSamplesPerSec = (uint)sampleRate,
                    wBitsPerSample = 16,
                    nBlockAlign = (ushort)(channels * 2),
                    nAvgBytesPerSec = (uint)(sampleRate * channels * 2),
                    cbSize = 0,
                };

                int opened = waveOutOpen(out _handle, device, ref format, IntPtr.Zero, IntPtr.Zero, 0);
                if (opened != MmSysErrNoError)
                {
                    throw new AudioException(
                        $"The audio device refused to open (waveOut error {opened}). " +
                        "Check that a playback device exists and is not disabled.");
                }

                _arena = new byte[BufferCount * BufferBytes];
                _arenaPin = GCHandle.Alloc(_arena, GCHandleType.Pinned);

                _headers = new WaveHeader[BufferCount];
                for (int i = 0; i < BufferCount; i++)
                {
                    _headers[i] = new WaveHeader
                    {
                        lpData = _arenaPin.AddrOfPinnedObject() + (i * BufferBytes),
                        dwBufferLength = BufferBytes,
                    };
                }

                _headerPin = GCHandle.Alloc(_headers, GCHandleType.Pinned);

                _slot = 0;
                _fill = 0;
                _started = true;
                _lastError = null;
                Describe = _deviceName.Length == 0 ? $"default device, {sampleRate} Hz" : $"{_deviceName}, {sampleRate} Hz";
            }
            catch (Exception ex) when (ex is AudioException or DllNotFoundException or EntryPointNotFoundException)
            {
                _lastError = ex.Message;
                Describe = "unavailable";
                Cleanup();
                throw;
            }
        }
    }

    public bool Write(ReadOnlySpan<short> samples)
    {
        lock (_gate)
        {
            if (!_started || samples.Length == 0)
            {
                return false;
            }

            int taken = 0;

            while (taken < samples.Length)
            {
                int space = BufferBytes - _fill;
                int shorts = Math.Min(space / 2, samples.Length - taken);

                if (shorts <= 0)
                {
                    if (!FlushSlot())
                    {
                        return false;
                    }

                    continue;
                }

                int offset = _slot * BufferBytes + _fill;
                int volume = Volume;

                for (int i = 0; i < shorts; i++)
                {
                    short scaled = volume == 100
                        ? samples[taken + i]
                        : (short)(samples[taken + i] * volume / 100);

                    _arena[offset + i * 2] = (byte)(scaled & 0xFF);
                    _arena[offset + i * 2 + 1] = (byte)((scaled >> 8) & 0xFF);
                }

                _fill += shorts * 2;
                taken += shorts;

                if (_fill >= BufferBytes)
                {
                    if (!FlushSlot())
                    {
                        return false;
                    }
                }
            }

            return true;
        }
    }

    /// <summary>Hands the accumulated slot to the driver. False when the device is behind.</summary>
    private bool FlushSlot()
    {
        if (_fill == 0)
        {
            return true;
        }

        if (!WaitForSlot(_slot))
        {
            // Dropped rather than queued: audio that arrives late is worse than a gap, and a
            // growing queue would put the sound behind the picture for the rest of the session.
            DroppedChunks++;
            _fill = 0;
            _slot = (_slot + 1) % BufferCount;
            return false;
        }

        _headers[_slot].dwBufferLength = (uint)_fill;
        _headers[_slot].dwFlags = 0;

        IntPtr header = _headerPin.AddrOfPinnedObject() + (_slot * Marshal.SizeOf<WaveHeader>());
        int size = Marshal.SizeOf<WaveHeader>();

        int prepared = waveOutPrepareHeader(_handle, header, size);
        if (prepared != MmSysErrNoError)
        {
            _lastError = $"waveOutPrepareHeader failed ({prepared})";
            _fill = 0;
            _slot = (_slot + 1) % BufferCount;
            return false;
        }

        int written = waveOutWrite(_handle, header, size);
        if (written != MmSysErrNoError)
        {
            waveOutUnprepareHeader(_handle, header, size);
            _lastError = $"waveOutWrite failed ({written})";
            _fill = 0;
            _slot = (_slot + 1) % BufferCount;
            return false;
        }

        _fill = 0;
        _slot = (_slot + 1) % BufferCount;
        return true;
    }

    /// <summary>
    /// True once the slot is free. A slot that was never written counts as free; one still being
    /// played is waited for briefly and then given up on.
    /// </summary>
    private bool WaitForSlot(int slot)
    {
        if (_headers[slot].lpData == IntPtr.Zero || _headers[slot].dwFlags == 0)
        {
            return true;
        }

        int waited = 0;
        while ((_headers[slot].dwFlags & WhdrDone) == 0)
        {
            if (waited >= DrainTimeoutMs)
            {
                // Still playing. Reset is the only way to reclaim it without losing the whole
                // session; the slot is unprepared and reused.
                IntPtr header = _headerPin.AddrOfPinnedObject() + (slot * Marshal.SizeOf<WaveHeader>());
                waveOutUnprepareHeader(_handle, header, Marshal.SizeOf<WaveHeader>());
                _headers[slot].dwFlags = 0;
                return true;
            }

            Thread.Sleep(2);
            waited += 2;
        }

        IntPtr done = _headerPin.AddrOfPinnedObject() + (slot * Marshal.SizeOf<WaveHeader>());
        waveOutUnprepareHeader(_handle, done, Marshal.SizeOf<WaveHeader>());
        _headers[slot].dwFlags = 0;
        return true;
    }

    private static IntPtr ResolveDevice(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return new IntPtr(WaveMapper);
        }

        IReadOnlyList<string> devices = Devices();
        for (int i = 1; i < devices.Count; i++)
        {
            if (string.Equals(devices[i], name, StringComparison.OrdinalIgnoreCase))
            {
                return new IntPtr(i - 1);
            }
        }

        // The named device is gone: fall back to the default rather than refusing to play at all.
        return new IntPtr(WaveMapper);
    }

    public void Stop()
    {
        lock (_gate)
        {
            if (_handle != IntPtr.Zero)
            {
                waveOutReset(_handle);
                waveOutClose(_handle);
                _handle = IntPtr.Zero;
            }

            _started = false;
            _fill = 0;
            Cleanup();
        }
    }

    private void Cleanup()
    {
        if (_headerPin.IsAllocated)
        {
            _headerPin.Free();
        }

        if (_arenaPin.IsAllocated)
        {
            _arenaPin.Free();
        }

        _arena = [];
        _headers = [];
    }

    public void Dispose() => Stop();

    // --- winmm ---------------------------------------------------------------------------

    [StructLayout(LayoutKind.Sequential)]
    private struct WaveFormatEx
    {
        public ushort wFormatTag;
        public ushort nChannels;
        public uint nSamplesPerSec;
        public uint nAvgBytesPerSec;
        public ushort nBlockAlign;
        public ushort wBitsPerSample;
        public ushort cbSize;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WaveHeader
    {
        public IntPtr lpData;
        public uint dwBufferLength;
        public uint dwBytesRecorded;
        public IntPtr dwUser;
        public uint dwFlags;
        public uint dwLoops;
        public IntPtr lpNext;
        public IntPtr reserved;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WaveOutCaps
    {
        public ushort wMid;
        public ushort wPid;
        public uint vDriverVersion;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string szPname;

        public uint dwFormats;
        public ushort wChannels;
        public ushort wReserved1;
        public uint dwSupport;
    }

    [DllImport("winmm.dll")]
    private static extern int waveOutGetNumDevs();

    [DllImport("winmm.dll", CharSet = CharSet.Unicode)]
    private static extern int waveOutGetDevCapsW(IntPtr uDeviceID, ref WaveOutCaps caps, int size);

    [DllImport("winmm.dll")]
    private static extern int waveOutOpen(
        out IntPtr handle, IntPtr uDeviceID, ref WaveFormatEx format, IntPtr callback, IntPtr instance, int flags);

    [DllImport("winmm.dll")]
    private static extern int waveOutPrepareHeader(IntPtr handle, IntPtr header, int size);

    [DllImport("winmm.dll")]
    private static extern int waveOutUnprepareHeader(IntPtr handle, IntPtr header, int size);

    [DllImport("winmm.dll")]
    private static extern int waveOutWrite(IntPtr handle, IntPtr header, int size);

    [DllImport("winmm.dll")]
    private static extern int waveOutReset(IntPtr handle);

    [DllImport("winmm.dll")]
    private static extern int waveOutClose(IntPtr handle);
}
