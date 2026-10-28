using System.Net.Http.Json;
using Scrappy.Settings;
using System.Text.Json.Serialization;

namespace Scrappy.Discovery;

/// <summary>
/// What the tablet reports about itself. The tablet app answers <c>GET /status</c> on its one port,
/// on a connection that is then closed; a session uses a different connection to the same port.
/// Nothing here is guessed from the video stream.
/// </summary>
public sealed record DeviceFacts
{
    /// <summary>The name the tablet shows in its own settings, when it reports one.</summary>
    [JsonPropertyName("name")] public string Name { get; init; } = "";

    [JsonPropertyName("model")] public string Model { get; init; } = "unknown";

    [JsonPropertyName("android")] public string Android { get; init; } = "unknown";

    /// <summary>The port the tablet is listening on. Falls back to the default when absent.</summary>
    [JsonPropertyName("port")] public int Port { get; init; }

    /// <summary>Idle, Starting, Listening, Connected or Failed - the tablet's own session state.</summary>
    [JsonPropertyName("phase")] public string Phase { get; init; } = "";

    /// <summary>The tablet's own status line, verbatim. This is what explains a dead session.</summary>
    [JsonPropertyName("message")] public string Message { get; init; } = "";

    /// <summary>The last exception the tablet recorded, one line. Empty when it has none.</summary>
    [JsonPropertyName("lastError")] public string LastError { get; init; } = "";

    /// <summary>True when the tablet holds a USB-tethered address, i.e. the cable is a real path.</summary>
    [JsonPropertyName("wiredReady")] public bool WiredReady { get; init; }

    [JsonPropertyName("version")] public string Version { get; init; } = "";

    [JsonPropertyName("width")] public int Width { get; init; }

    [JsonPropertyName("height")] public int Height { get; init; }

    /// <summary>Refresh rate the panel is running at right now, in Hz.</summary>
    [JsonPropertyName("refreshHz")] public double RefreshHz { get; init; }

    /// <summary>Every mode the panel supports, in Hz.</summary>
    [JsonPropertyName("refreshModes")] public IReadOnlyList<double> RefreshModes { get; init; } = [];

    [JsonPropertyName("h264Hardware")] public bool H264Hardware { get; init; }

    [JsonPropertyName("h265Hardware")] public bool H265Hardware { get; init; }

    [JsonPropertyName("av1Hardware")] public bool Av1Hardware { get; init; }

    [JsonPropertyName("freeStorageMb")] public long FreeStorageMb { get; init; }
}

/// <summary>Asks the tablet app what it is.</summary>
public static class DeviceProbe
{
    public static async Task<DeviceFacts?> QueryAsync(
        string host,
        int port,
        int timeoutMs = 2500,
        CancellationToken cancellationToken = default)
    {
        using var client = new HttpClient { Timeout = TimeSpan.FromMilliseconds(timeoutMs) };

        try
        {
            return await client
                .GetFromJsonAsync<DeviceFacts>($"http://{host}:{port}/status", cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or NotSupportedException
                                       or System.Text.Json.JsonException)
        {
            return null;
        }
    }
}

/// <summary>
/// Turns "the device closed the stream" into something a person can act on.
///
/// The PC sees one thing when a session dies: a TCP FIN. That is true for at least four different
/// situations - the tablet's encoder failed to start, the tablet was stopped by hand, the app was
/// killed, the Wi-Fi went away - and it used to be reported the same way for all of them, while the
/// tablet's own screen said "Waiting for the PC" and looked healthy. The tablet already publishes
/// what happened on /status, so the honest thing to do is ask it once, after the stream is over, and
/// repeat its answer.
///
/// This is deliberately a separate call after the fact rather than a message in the video stream:
/// the wire format stays exactly scrcpy's, and a failure is explained even when it was severe
/// enough to break the stream.
/// </summary>
public static class EndExplanation
{
    public static async Task<string> DescribeAsync(string host, int port, string reason, int timeoutMs = 900)
    {
        DeviceFacts? facts;
        try
        {
            facts = await DeviceProbe.QueryAsync(host, port, timeoutMs).ConfigureAwait(false);
        }
        catch (Exception)
        {
            // Never let the diagnosis hide the original reason.
            return reason;
        }

        if (facts is null)
        {
            return $"{reason}. The tablet is not answering on port {port} any more - the app or its capture service has stopped.";
        }

        if (!string.IsNullOrWhiteSpace(facts.LastError))
        {
            // The tablet knows exactly what went wrong; say it in the tablet's words.
            return $"{reason}. The tablet reports: {facts.Message.TrimEnd('.')}.";
        }

        if (facts.Phase.Equals("Waiting for the PC", StringComparison.OrdinalIgnoreCase))
        {
            // The tablet ended the session and went straight back to listening, so the failure was
            // on its side of the handshake: usually the encoder never opened.
            return $"{reason}. The tablet is back to \"Waiting for the PC\" - it accepted the connection " +
                   "but never got as far as sending video. Tap Start on the tablet and try again.";
        }

        if (facts.Phase.Equals("Not started", StringComparison.OrdinalIgnoreCase))
        {
            return $"{reason}. Mirroring was stopped on the tablet.";
        }

        string phase = string.IsNullOrWhiteSpace(facts.Phase) ? "unknown state" : facts.Phase;
        string message = string.IsNullOrWhiteSpace(facts.Message) ? "" : $" - {facts.Message}";
        return $"{reason}. The tablet says: {phase}{message}.";
    }
}

/// <summary>
/// Suggests settings from what the tablet actually reported. Nothing is applied here: the caller
/// shows the suggestion and the user decides.
/// </summary>
public static class PresetAdvisor
{
    public sealed record Advice(DailyPresets.Choice Preset, string Reason);

    /// <summary>
    /// Used when the suggestion is not exactly one of the named presets, so the preset picker can
    /// say something truthful instead of showing "Custom" right after applying its own advice.
    /// </summary>
    public const string SuggestedLabel = "Suggested";

    /// <summary>
    /// Builds the suggestion from what the tablet reported. It never throws: this runs from a
    /// button on the main window, and an exception there takes the whole app down.
    /// </summary>
    public static Advice Recommend(DeviceFacts facts)
    {
        // H.265 only pays off when the tablet encodes it in hardware. Without that it costs the
        // tablet real frames, and the PC has to decode it in software, where H.264 is roughly
        // three times cheaper for the same size.
        bool hevc = facts.H265Hardware;

        // Never upscale beyond the panel, and stay at or below 2560 on the long edge unless the
        // panel is smaller than that.
        int longestEdge = Math.Max(facts.Width, facts.Height);
        string size = longestEdge >= 3200 ? "3200"
            : longestEdge >= 2560 ? "2560"
            : Math.Max(1280, longestEdge / 2).ToString();

        var values = new Dictionary<string, string>(5)
        {
            ["video_codec"] = hevc ? "h265" : "h264",
            // H.264 needs more bits than H.265 for the same picture, and without a hardware HEVC
            // encoder the tablet has cycles to spare for it.
            ["video_bit_rate"] = hevc ? "12000000" : "16000000",
            ["max_size"] = size,
            ["max_fps"] = "60",
            ["video_buffer"] = "30",
        };

        // Keep a name when the suggestion happens to be exactly one of the named presets.
        DailyPresets.Choice? named = DailyPresets.Quality.FirstOrDefault(p =>
            p.Values.Count == values.Count &&
            p.Values.All(kv => values.TryGetValue(kv.Key, out string? mine) && mine == kv.Value));

        var preset = new DailyPresets.Choice(named?.Label ?? SuggestedLabel, values);

        string refresh = facts.RefreshModes.Count > 0
            ? $"panel runs {facts.RefreshHz:0.#} Hz (modes {string.Join(", ", facts.RefreshModes.Select(m => m.ToString("0.#")))})"
            : $"panel runs {facts.RefreshHz:0.#} Hz";

        string codec = hevc
            ? "H.265 has a hardware encoder"
            : facts.H264Hardware
                ? "only H.264 has a hardware encoder, so H.265 would be encoded in software"
                : "no hardware video encoder was found, so the lightest setting wins";

        string reason = $"{refresh}; {codec}. Suggested {values["video_codec"].ToUpperInvariant()} at " +
                        $"{size} on the long edge, {values["max_fps"]} fps, {int.Parse(values["video_bit_rate"]) / 1_000_000} Mbps.";

        return new Advice(preset, reason);
    }
}
