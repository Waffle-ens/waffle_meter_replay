using K4os.Compression.LZ4;
using WaffleMeter.Capture;

namespace WaffleMeter.Replay;

/// <summary>
/// Extracts entity movement from the 0x37xx transform family that <see cref="StreamProcessor"/> discards
/// as unknown opcodes. Mirrors StreamProcessor's framing + FF-FF LZ4 decompression EXACTLY so it sees the
/// same inner packets, then decodes two shapes and emits a <see cref="MovementSample"/> per reading:
/// <list type="bullet">
/// <item><b>Absolute</b> keyframes — <c>[var-int entityId][..][f32 X][f32 Y][f32 Z]</c> from the sparse
/// transform opcodes (0x371C/0x372F/0x371A/0x371B).</item>
/// <item><b>Delta</b> stream — the dense 0x371D opcode (56% of movement, ~10 Hz), a flag-gated compact
/// record of signed int8 per-axis increments. Decoded structurally (see <see cref="DecodeDelta"/>);
/// integrating these between keyframes is what makes replay WCL-smooth.</item>
/// </list>
/// <para>
/// Designed to be fed the same assembled packets as the DPS parser (a parallel tap — see
/// docs/replay-feature-plan.md), so it adds no load to and cannot regress the parity-critical damage
/// path. Pure/allocation-light on the hot path; never throws out (a malformed frame is swallowed).
/// </para>
/// <para>
/// The absolute float offset per opcode is decoded at the first byte offset (after the id var-int) that
/// yields a plausible world-coordinate triplet, robust to the ±1 wobble across corpora; the chosen offset
/// travels on the sample so downstream can keep only an entity's dominant layout.
/// </para>
/// </summary>
public sealed class MovementParser
{
    /// <summary>The dense per-tick movement-delta opcode. Body layout (after the entity-id var-int):
    /// <c>[flag][subtype u8 iff flag&amp;0x01][ΔX s8 iff flag&amp;0x02][ΔY s8 iff flag&amp;0x04][ΔZ s8 iff
    /// flag&amp;0x08][u16 K][u16 K'][0x01 iff !(flag&amp;0x01)]</c>. The signed delta bytes are the
    /// movement (RE-confirmed 100% structural fit + r≈0.92-0.995 vs float anchors on controlled captures);
    /// the trailing u16 pair is not needed for reconstruction.</summary>
    private const int DeltaOpcode = 0x371D;

    private readonly Action<MovementSample> _onSample;
    private readonly int _maxOffsetScan;

    /// <summary>Total 0x37xx packets seen (position-bearing or not).</summary>
    public long MovementPackets { get; private set; }

    /// <summary>Absolute 0x37xx packets that yielded a plausible position triplet (keyframes).</summary>
    public long PositionSamples { get; private set; }

    /// <summary>0x371D packets decoded to a movement delta.</summary>
    public long DeltaSamples { get; private set; }

    /// <summary>FF-FF LZ4 bundles expanded.</summary>
    public long Bundles { get; private set; }

    /// <param name="onSample">Sink for each decoded position sample.</param>
    /// <param name="maxOffsetScan">How many byte offsets after the id var-int to probe for the triplet.</param>
    public MovementParser(Action<MovementSample> onSample, int maxOffsetScan = 8)
    {
        _onSample = onSample;
        _maxOffsetScan = maxOffsetScan;
    }

    /// <summary>Feed one assembled application packet (same bytes the DPS parser receives).</summary>
    public void Feed(byte[] packet, long arrivedAt)
    {
        try
        {
            FeedInner(packet, arrivedAt);
        }
        catch
        {
            // best-effort: a short/garbage frame must never disturb capture
        }
    }

    private void FeedInner(byte[] packet, long arrivedAt)
    {
        VarIntOutput lengthInfo = PacketPrimitives.ReadVarInt(packet);
        if (lengthInfo.Length < 0 || lengthInfo.Length >= packet.Length)
        {
            return;
        }

        int flagByte = packet[lengthInfo.Length];
        bool extraFlag = flagByte >= 0xF0 && flagByte < 0xFF;

        if (extraFlag)
        {
            if (lengthInfo.Length + 2 < packet.Length
                && packet[lengthInfo.Length + 1] == 0xFF
                && packet[lengthInfo.Length + 2] == 0xFF)
            {
                Decompress(packet, lengthInfo.Length, true, arrivedAt);
                return;
            }
        }
        else if (lengthInfo.Length + 1 < packet.Length
                 && packet[lengthInfo.Length] == 0xFF
                 && packet[lengthInfo.Length + 1] == 0xFF)
        {
            Decompress(packet, lengthInfo.Length, false, arrivedAt);
            return;
        }

        int opcodeOffset = lengthInfo.Length + (extraFlag ? 1 : 0);
        if (opcodeOffset + 1 >= packet.Length)
        {
            return;
        }

        int opcodeKey = (packet[opcodeOffset] & 0xFF) | ((packet[opcodeOffset + 1] & 0xFF) << 8);
        if ((opcodeKey & 0xFF00) != 0x3700)
        {
            return;
        }

        if (opcodeKey == DeltaOpcode)
        {
            DecodeDelta(packet, opcodeOffset + 2, arrivedAt);
        }
        else
        {
            Extract(opcodeKey, packet, opcodeOffset + 2, arrivedAt);
        }
    }

    // Verbatim mirror of StreamProcessor.DecompressPacket, recursing into FeedInner instead of dispatch.
    private void Decompress(byte[] packet, int headerLength, bool extraFlag, long arrivedAt)
    {
        Bundles++;
        int offset = headerLength + 2 + (extraFlag ? 1 : 0);
        if (offset + 4 > packet.Length)
        {
            return;
        }

        int originLength = PacketPrimitives.ParseUInt32Le(packet, offset);
        offset += 4;
        if (originLength <= 0 || originLength > 8_000_000)
        {
            return;
        }

        var restored = new byte[originLength];
        LZ4Codec.Decode(packet.AsSpan(offset, packet.Length - offset), restored.AsSpan(0, originLength));

        int innerOffset = 0;
        while (innerOffset < restored.Length)
        {
            VarIntOutput lengthInfo = PacketPrimitives.ReadVarInt(restored, innerOffset);
            if (lengthInfo.Value == 0)
            {
                innerOffset += 1;
                continue;
            }

            if (lengthInfo.Value < 0)
            {
                break;
            }

            int realLength = lengthInfo.Value + lengthInfo.Length - 4;
            if (realLength <= 0 || innerOffset + realLength > restored.Length)
            {
                break;
            }

            FeedInner(restored[innerOffset..(innerOffset + realLength)], arrivedAt);
            innerOffset += realLength;
        }
    }

    private void Extract(int opcode, byte[] packet, int bodyStart, long arrivedAt)
    {
        VarIntOutput idInfo = PacketPrimitives.ReadVarInt(packet, bodyStart);
        if (idInfo.Length < 0)
        {
            return;
        }

        MovementPackets++;
        int afterId = bodyStart + idInfo.Length;
        for (int k = 0; k <= _maxOffsetScan; k++)
        {
            int o = afterId + k;
            if (o + 12 > packet.Length)
            {
                break;
            }

            float x = ReadF(packet, o);
            float y = ReadF(packet, o + 4);
            float z = ReadF(packet, o + 8);
            if (LooksLikeCoord(x, y, z))
            {
                PositionSamples++;
                _onSample(new MovementSample(idInfo.Value, arrivedAt, x, y, z, opcode, k));
                return; // first plausible offset only; layout is fixed per opcode
            }
        }
    }

    // Decode a 0x371D dense-movement packet into a per-axis signed-delta sample. Body after the entity-id
    // var-int is [flag][subtype iff flag&0x01][ΔX s8 iff flag&0x02][ΔY s8 iff flag&0x04][ΔZ s8 iff
    // flag&0x08][u16 K][u16 K'][0x01 iff !(flag&0x01)]. A missing axis byte = no movement on that axis this
    // tick (delta 0). We validate the exact length the flags imply so a stray/short frame can't be
    // mis-decoded (RE showed 100% length fit on real captures).
    private void DecodeDelta(byte[] packet, int bodyStart, long arrivedAt)
    {
        VarIntOutput idInfo = PacketPrimitives.ReadVarInt(packet, bodyStart);
        if (idInfo.Length < 0)
        {
            return;
        }

        MovementPackets++;
        int o = bodyStart + idInfo.Length;
        if (o >= packet.Length)
        {
            return;
        }

        int flag = packet[o];
        bool hasSubtype = (flag & 0x01) != 0;
        int deltaBytes = System.Numerics.BitOperations.PopCount((uint)(flag & 0x0E));
        int pairPos = o + 1 + (hasSubtype ? 1 : 0) + deltaBytes;
        int expectedEnd = pairPos + 4 + (hasSubtype ? 0 : 1); // two u16 + optional 0x01 trailer
        if (expectedEnd != packet.Length)
        {
            return; // flags don't account for the exact body length — not a well-formed 0x371D
        }

        int p = o + 1 + (hasSubtype ? 1 : 0);
        float dx = (flag & 0x02) != 0 ? (sbyte)packet[p++] : 0f;
        float dy = (flag & 0x04) != 0 ? (sbyte)packet[p++] : 0f;
        float dz = (flag & 0x08) != 0 ? (sbyte)packet[p] : 0f;

        DeltaSamples++;
        _onSample(new MovementSample(idInfo.Value, arrivedAt, dx, dy, dz, DeltaOpcode, 0, MovementKind.Delta));
    }

    private static float ReadF(byte[] b, int o) => BitConverter.ToSingle(b.AsSpan(o, 4));

    private static bool Finite(float f) =>
        !float.IsNaN(f) && !float.IsInfinity(f) && MathF.Abs(f) < 5_000_000f && (f == 0f || MathF.Abs(f) > 1e-3f);

    // Real world coords are large-magnitude; require >=2 of 3 components to be clearly large so a triple
    // of small flag/zero bytes decoded as floats can't masquerade as a position.
    private static bool LooksLikeCoord(float x, float y, float z)
    {
        if (!(Finite(x) && Finite(y) && Finite(z)))
        {
            return false;
        }

        int big = 0;
        if (MathF.Abs(x) > 50f) big++;
        if (MathF.Abs(y) > 50f) big++;
        if (MathF.Abs(z) > 50f) big++;
        return big >= 2;
    }
}
