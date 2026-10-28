namespace Scrappy.Protocol;

/// <summary>
/// One encoded frame as it would leave the device encoder: an Annex-B access unit with its
/// start codes intact. <see cref="IsKeyFrame"/> is true when the unit contains an IDR slice.
/// </summary>
public readonly record struct AccessUnit(byte[] Payload, bool IsKeyFrame, bool ContainsParameterSets);

/// <summary>
/// Splits an Annex-B byte stream into access units.
///
/// scrcpy sends one encoded frame per packet, so the sender has to know where a frame ends.
/// The device encoder does not tell us: its output buffers are opaque. This splitter recovers
/// frame boundaries from the bitstream itself, using access unit delimiters when the encoder
/// emits them and falling back to "a new slice starts a new picture" when it does not.
///
/// This is test-fixture and probe code. The real receiver never splits anything - it trusts
/// the packet length in the 12-byte header.
/// </summary>
public static class AnnexB
{
    private const byte NalTypeIdr = 5;
    private const byte NalTypeSps = 7;
    private const byte NalTypePps = 8;
    private const byte NalTypeAud = 9;

    // HEVC numbers its NAL types in the top six bits of a two-byte header.
    private const int HevcVps = 32;
    private const int HevcSps = 33;
    private const int HevcPps = 34;
    private const int HevcAud = 35;

    /// <summary>Returns every start-code offset in <paramref name="data"/>, first byte of the 0x00 0x00 0x01 prefix.</summary>
    public static List<int> FindStartCodes(ReadOnlySpan<byte> data)
    {
        var offsets = new List<int>(capacity: data.Length / 4096 + 8);
        for (int i = 0; i + 3 < data.Length; i++)
        {
            if (data[i] == 0 && data[i + 1] == 0 && data[i + 2] == 1)
            {
                offsets.Add(i);
                i += 2;
            }
        }

        return offsets;
    }

    /// <summary>True when the stream's parameter sets look like HEVC rather than H.264.</summary>
    public static bool IsHevc(ReadOnlySpan<byte> data)
    {
        foreach (int offset in FindStartCodes(data))
        {
            int nalStart = offset + 3;
            if (nalStart >= data.Length)
            {
                break;
            }

            int type = data[nalStart] >> 1;
            if (type is HevcVps or HevcSps or HevcPps or HevcAud)
            {
                return true;
            }

            if ((data[nalStart] & 0x1F) is NalTypeSps or NalTypePps)
            {
                return false;
            }
        }

        return false;
    }

    /// <summary>
    /// Groups the stream into access units. Parameter sets that precede the first picture are
    /// folded into the first unit and flagged, so the caller can lift them into the config packet.
    /// </summary>
    public static IReadOnlyList<AccessUnit> SplitAccessUnits(ReadOnlySpan<byte> data)
    {
        List<int> offsets = FindStartCodes(data);
        if (offsets.Count == 0)
        {
            throw new InvalidDataException("No Annex-B start codes found; this is not an H.264 elementary stream.");
        }

        bool hevc = IsHevc(data);
        var units = new List<AccessUnit>(offsets.Count);
        int unitStart = offsets[0];
        bool unitHasSlice = false;
        bool unitHasParameterSets = false;

        for (int i = 0; i < offsets.Count; i++)
        {
            int nalStart = offsets[i] + 3;
            if (nalStart >= data.Length)
            {
                break;
            }

            byte header = data[nalStart];
            int nalType = hevc ? header >> 1 : header & 0x1F;

            // H.264 slices are 1-5; HEVC slices are 0-31, with 32-35 for parameter sets and
            // delimiters. An H.264 stream never produces a byte above 23 here, so the two cannot
            // be confused once the codec is known.
            bool isSlice = hevc ? nalType <= 31 : nalType is >= 1 and <= NalTypeIdr;
            bool isAud = hevc ? nalType == HevcAud : nalType == NalTypeAud;
            bool isParameterSet = hevc
                ? nalType is HevcVps or HevcSps or HevcPps
                : nalType is NalTypeSps or NalTypePps;

            bool startsUnit = isAud || (isSlice && unitHasSlice);

            if (startsUnit && unitHasSlice)
            {
                int end = offsets[i];
                units.Add(MakeUnit(data[unitStart..end], unitHasParameterSets, hevc));
                unitStart = offsets[i];
                unitHasSlice = false;
                unitHasParameterSets = false;
            }

            if (isParameterSet)
            {
                unitHasParameterSets = true;
            }

            if (isSlice)
            {
                unitHasSlice = true;
            }
        }

        if (unitHasSlice)
        {
            units.Add(MakeUnit(data[unitStart..], unitHasParameterSets, hevc));
        }

        return units;
    }

    /// <summary>
    /// Returns the first SPS+PPS pair found in the stream, with start codes, suitable for the
    /// codec-configuration packet. Empty when the stream carries no parameter sets.
    /// </summary>
    public static byte[] ExtractParameterSets(ReadOnlySpan<byte> data)
    {
        List<int> offsets = FindStartCodes(data);
        bool hevc = IsHevc(data);
        int spsStart = -1;
        int end = -1;

        for (int i = 0; i < offsets.Count; i++)
        {
            int nalStart = offsets[i] + 3;
            if (nalStart >= data.Length)
            {
                break;
            }

            int nalType = hevc ? data[nalStart] >> 1 : data[nalStart] & 0x1F;
            bool isFirstParameterSet = hevc ? nalType is HevcVps or HevcSps : nalType == NalTypeSps;
            bool isLastParameterSet = nalType == (hevc ? HevcPps : NalTypePps);

            if (isFirstParameterSet && spsStart < 0)
            {
                spsStart = offsets[i];
            }
            else if (isLastParameterSet && spsStart >= 0)
            {
                end = i + 1 < offsets.Count ? offsets[i + 1] : data.Length;
                break;
            }
        }

        return spsStart < 0 || end < 0 ? [] : data[spsStart..end].ToArray();
    }

    private static AccessUnit MakeUnit(ReadOnlySpan<byte> span, bool containsParameterSets, bool hevc)
    {
        // Trim trailing zero padding so a unit does not carry the next start code's zeros.
        int length = span.Length;
        while (length > 0 && span[length - 1] == 0)
        {
            length--;
        }

        bool isKey = ContainsKeyframe(span[..length], hevc);
        return new AccessUnit(span[..length].ToArray(), isKey, containsParameterSets);
    }

    private static bool ContainsKeyframe(ReadOnlySpan<byte> unit, bool hevc)
    {
        foreach (int offset in FindStartCodes(unit))
        {
            int nalStart = offset + 3;
            if (nalStart >= unit.Length)
            {
                continue;
            }

            if (hevc)
            {
                // 16-21 are the IRAP pictures: BLA, IDR and CRA. Any of them can start decoding.
                int type = unit[nalStart] >> 1;
                if (type is >= 16 and <= 21)
                {
                    return true;
                }
            }
            else if ((unit[nalStart] & 0x1F) == NalTypeIdr)
            {
                return true;
            }
        }

        return false;
    }
}
