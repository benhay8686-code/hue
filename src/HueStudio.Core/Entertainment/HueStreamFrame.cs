using System.Buffers.Binary;
using System.Text;

namespace HueStudio.Core.Entertainment;

public readonly record struct ChannelColor(byte ChannelId, double R, double G, double B);

/// <summary>
/// Builds HueStream v2 packets for the Entertainment API. Packets are sent ~50-60 times a second
/// over DTLS 1.2 (PSK = the client key from pairing, identity = the application key) to UDP 2100
/// on the bridge, after the entertainment_configuration has been set to "start".
///
/// Layout: "HueStream" | 0x02 0x00 (v2.0) | seq | 0x00 0x00 | colour space | 0x00 |
///         36-char configuration id | up to 20 x (channel id, 3 x uint16 big-endian).
/// </summary>
public static class HueStreamFrame
{
    public const int MaxChannels = 20;
    private static readonly byte[] Magic = "HueStream"u8.ToArray();

    public static byte[] Build(string entertainmentConfigurationId, IReadOnlyList<ChannelColor> channels, byte sequence = 0)
    {
        if (entertainmentConfigurationId.Length != 36)
            throw new ArgumentException("Entertainment configuration id must be a 36-character UUID.", nameof(entertainmentConfigurationId));
        if (channels.Count > MaxChannels)
            throw new ArgumentException($"At most {MaxChannels} channels per frame.", nameof(channels));

        var buffer = new byte[16 + 36 + channels.Count * 7];
        var span = buffer.AsSpan();
        Magic.CopyTo(span);
        span[9] = 0x02;  // major version
        span[10] = 0x00; // minor version
        span[11] = sequence;
        span[12] = 0x00; span[13] = 0x00; // reserved
        span[14] = 0x00; // colour space: RGB
        span[15] = 0x00; // reserved
        Encoding.ASCII.GetBytes(entertainmentConfigurationId, span.Slice(16, 36));

        var offset = 52;
        foreach (var c in channels)
        {
            span[offset] = c.ChannelId;
            BinaryPrimitives.WriteUInt16BigEndian(span[(offset + 1)..], To16(c.R));
            BinaryPrimitives.WriteUInt16BigEndian(span[(offset + 3)..], To16(c.G));
            BinaryPrimitives.WriteUInt16BigEndian(span[(offset + 5)..], To16(c.B));
            offset += 7;
        }
        return buffer;
    }

    private static ushort To16(double unit) => (ushort)Math.Round(Math.Clamp(unit, 0, 1) * ushort.MaxValue);
}
