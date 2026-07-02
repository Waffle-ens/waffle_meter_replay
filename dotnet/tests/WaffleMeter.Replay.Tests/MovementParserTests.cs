using WaffleMeter.Replay;
using Xunit;

namespace WaffleMeter.Replay.Tests;

/// <summary>
/// Spec for <see cref="MovementParser"/> framing + 0x37xx position extraction. Packets are synthesized
/// with the same framing the assembler emits: [length var-int][opcode lo][opcode hi][entity var-int]
/// [optional filler][f32 X][f32 Y][f32 Z]. The length var-int's VALUE is irrelevant to the movement
/// extractor (only its byte length matters), matching <see cref="MovementParser"/>.
/// </summary>
public class MovementParserTests
{
    private static byte[] Varint(int v)
    {
        var bytes = new List<byte>();
        uint u = (uint)v;
        do
        {
            byte b = (byte)(u & 0x7F);
            u >>= 7;
            if (u != 0)
            {
                b |= 0x80;
            }

            bytes.Add(b);
        }
        while (u != 0);

        return bytes.ToArray();
    }

    private static byte[] PositionPacket(int opcode, int entityId, int filler, float x, float y, float z)
    {
        var p = new List<byte> { 0x10 }; // 1-byte length var-int, value ignored by the movement extractor
        p.Add((byte)(opcode & 0xFF));
        p.Add((byte)((opcode >> 8) & 0xFF));
        p.AddRange(Varint(entityId));
        for (int i = 0; i < filler; i++)
        {
            p.Add(0x00);
        }

        p.AddRange(BitConverter.GetBytes(x));
        p.AddRange(BitConverter.GetBytes(y));
        p.AddRange(BitConverter.GetBytes(z));
        return p.ToArray();
    }

    // A 0x371D dense-movement packet: [len][1D][37][entity][flag][subtype iff flag&1][ΔX s8 iff flag&2]
    // [ΔY s8 iff flag&4][ΔZ s8 iff flag&8][u16 K][u16 K'][0x01 iff !(flag&1)]. K/K'/trailer are inert to
    // the decoder (only the flag-implied length matters), so they're filled with zeros / 0x01.
    private static byte[] DeltaPacket(int entityId, int flag, sbyte dx = 0, sbyte dy = 0, sbyte dz = 0, byte subtype = 0x03)
    {
        var p = new List<byte> { 0x10, 0x1D, 0x37 };
        p.AddRange(Varint(entityId));
        p.Add((byte)flag);
        if ((flag & 0x01) != 0)
        {
            p.Add(subtype);
        }

        if ((flag & 0x02) != 0)
        {
            p.Add((byte)dx);
        }

        if ((flag & 0x04) != 0)
        {
            p.Add((byte)dy);
        }

        if ((flag & 0x08) != 0)
        {
            p.Add((byte)dz);
        }

        p.AddRange(new byte[] { 0, 0, 0, 0 }); // u16 K, u16 K'
        if ((flag & 0x01) == 0)
        {
            p.Add(0x01); // trailer only when no subtype
        }

        return p.ToArray();
    }

    [Fact]
    public void Extracts_position_triplet_for_0x37xx()
    {
        var samples = new List<MovementSample>();
        var parser = new MovementParser(samples.Add);

        parser.Feed(PositionPacket(0x371C, 12892, filler: 0, 1234.5f, -5678.25f, 900f), arrivedAt: 42);

        MovementSample s = Assert.Single(samples);
        Assert.Equal(12892, s.EntityId);
        Assert.Equal(0x371C, s.Opcode);
        Assert.Equal(0, s.Offset);
        Assert.Equal(42, s.AtMs);
        Assert.Equal(1234.5f, s.X);
        Assert.Equal(-5678.25f, s.Y);
        Assert.Equal(900f, s.Z);
        Assert.Equal(1, parser.PositionSamples);
    }

    [Fact]
    public void Finds_triplet_after_filler_bytes_via_offset_scan()
    {
        var samples = new List<MovementSample>();
        var parser = new MovementParser(samples.Add);

        // two zero filler bytes between the id var-int and the triplet -> extractor must scan to offset 2
        parser.Feed(PositionPacket(0x371A, 7110, filler: 2, 20896f, 59152f, -3412f), arrivedAt: 7);

        MovementSample s = Assert.Single(samples);
        Assert.Equal(7110, s.EntityId);
        Assert.Equal(2, s.Offset);
        Assert.Equal(20896f, s.X);
        Assert.Equal(-3412f, s.Z);
    }

    [Fact]
    public void Ignores_non_0x37_opcodes()
    {
        var samples = new List<MovementSample>();
        var parser = new MovementParser(samples.Add);

        parser.Feed(PositionPacket(0x3804, 12892, filler: 0, 100f, 200f, 300f), arrivedAt: 1);

        Assert.Empty(samples);
        Assert.Equal(0, parser.MovementPackets);
    }

    [Fact]
    public void No_sample_when_0x371D_length_does_not_match_its_flags()
    {
        var samples = new List<MovementSample>();
        var parser = new MovementParser(samples.Add);

        // a 0x371D packet whose body is too short for the u16 pair the flag implies -> rejected
        byte[] p = { 0x10, 0x1D, 0x37, 0xDC, 0x64, 0x01, 0x02 };
        parser.Feed(p, arrivedAt: 1);

        Assert.Empty(samples);
        Assert.Equal(1, parser.MovementPackets); // counted as a 0x37xx packet, but not well-formed
        Assert.Equal(0, parser.DeltaSamples);
        Assert.Equal(0, parser.PositionSamples);
    }

    [Fact]
    public void Decodes_0x371D_signed_per_axis_deltas()
    {
        var samples = new List<MovementSample>();
        var parser = new MovementParser(samples.Add);

        // flag 0x0E = ΔX, ΔY, ΔZ present, no subtype
        parser.Feed(DeltaPacket(500, flag: 0x0E, dx: 10, dy: -5, dz: 2), arrivedAt: 77);

        MovementSample s = Assert.Single(samples);
        Assert.Equal(MovementKind.Delta, s.Kind);
        Assert.Equal(500, s.EntityId);
        Assert.Equal(0x371D, s.Opcode);
        Assert.Equal(77, s.AtMs);
        Assert.Equal(10f, s.X);
        Assert.Equal(-5f, s.Y);
        Assert.Equal(2f, s.Z);
        Assert.Equal(1, parser.DeltaSamples);
        Assert.Equal(0, parser.PositionSamples);
    }

    [Fact]
    public void Missing_axis_bytes_decode_as_zero_delta()
    {
        var samples = new List<MovementSample>();
        var parser = new MovementParser(samples.Add);

        // flag 0x04 = ΔY only; X and Z are absent -> 0
        parser.Feed(DeltaPacket(1, flag: 0x04, dy: 7), arrivedAt: 1);

        MovementSample s = Assert.Single(samples);
        Assert.Equal(0f, s.X);
        Assert.Equal(7f, s.Y);
        Assert.Equal(0f, s.Z);
    }

    [Fact]
    public void Subtype_byte_is_skipped_when_flag_bit0_set()
    {
        var samples = new List<MovementSample>();
        var parser = new MovementParser(samples.Add);

        // flag 0x03 = subtype + ΔX; the subtype byte must not be read as the delta
        parser.Feed(DeltaPacket(9, flag: 0x03, dx: -12, subtype: 0x55), arrivedAt: 3);

        MovementSample s = Assert.Single(samples);
        Assert.Equal(-12f, s.X);
        Assert.Equal(0f, s.Y);
    }

    [Fact]
    public void Rejects_small_byte_triples_that_are_not_world_coords()
    {
        var samples = new List<MovementSample>();
        var parser = new MovementParser(samples.Add);

        // floats all near zero -> fails the "world coordinate magnitude" guard
        parser.Feed(PositionPacket(0x371C, 100, filler: 0, 0.1f, 0.2f, 0.3f), arrivedAt: 1);

        Assert.Empty(samples);
    }
}
