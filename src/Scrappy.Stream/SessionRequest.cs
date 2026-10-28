using System.Text.Json;
using System.Text.Json.Serialization;

namespace Scrappy.Stream;

/// <summary>
/// What the tablet should encode. Sent as one JSON line at connect, so the PC decides resolution,
/// frame rate, codec and bitrate - and the tablet holds no settings of its own.
/// </summary>
public sealed record SessionRequest
{
    [JsonPropertyName("width")] public int Width { get; init; }

    [JsonPropertyName("height")] public int Height { get; init; }

    [JsonPropertyName("codec")] public string Codec { get; init; } = "h265";

    [JsonPropertyName("bitrate")] public int Bitrate { get; init; } = 12_000_000;

    [JsonPropertyName("fps")] public int Fps { get; init; } = 60;

    public string ToJson() => JsonSerializer.Serialize(this);

    /// <summary>
    /// Frame height for a given longest edge, keeping the panel's 3:2 shape. 2560 gives the
    /// 2560x1708 this project settled on, and rounding is kept to even numbers because 4:2:0
    /// encoders reject odd sizes.
    /// </summary>
    public static (int Width, int Height) SizeForLongestEdge(int longestEdge, double aspectWidth, double aspectHeight)
    {
        if (longestEdge <= 0)
        {
            return (0, 0);
        }

        double height = longestEdge * aspectHeight / aspectWidth;
        return (longestEdge, Math.Max(2, (int)Math.Round(height / 2) * 2));
    }
}
