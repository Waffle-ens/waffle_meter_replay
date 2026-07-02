namespace WaffleMeter.Replay;

/// <summary>How a <see cref="MovementSample"/> carries position.</summary>
public enum MovementKind
{
    /// <summary>Absolute world position (an IEEE-754 X/Y/Z triplet from a 0x371C/0x372F-style transform).</summary>
    Absolute,

    /// <summary>Per-tick signed movement delta (from the dense 0x371D feed). X/Y/Z hold the signed
    /// per-axis increment since the entity's previous 0x371D packet (0 on an axis with no bit set).</summary>
    Delta,
}

/// <summary>
/// One decoded entity movement observation, extracted from the 0x37xx transform family that the DPS
/// parser discards.
/// <para>
/// The family has two shapes, both reverse-engineered against scripted + controlled captures
/// (see docs/replay-feature-plan.md §10):
/// <list type="bullet">
/// <item><b>Absolute</b> (0x371C/0x372F/0x371A/0x371B, sparse ~1-2 Hz in-battle): a leading var-int entity
/// id then an IEEE-754 X/Y/Z world triplet a few bytes later. These are the keyframes.</item>
/// <item><b>Delta</b> (0x371D, dense ~10 Hz, 56% of all movement packets): a flag-gated compact record
/// carrying signed int8 per-axis increments (bit 0x02→ΔX, 0x04→ΔY, 0x08→ΔZ). Integrating these between
/// keyframes reconstructs the dense path (scale ≈ 1.0 world unit/count; endpoint-pinned to the keyframes
/// to bound drift). This is what makes replay WCL-smooth.</item>
/// </list>
/// For <see cref="MovementKind.Absolute"/> the <see cref="Opcode"/>/<see cref="Offset"/> lock the layout so
/// the recorder can keep only an entity's dominant one and drop offset-by-one false positives.
/// </para>
/// </summary>
/// <param name="EntityId">Leading var-int id of the moving entity. Proven to share the damage actor-id /
/// user-uid keyspace, so it resolves directly via <c>DataManager.User(id)</c>.</param>
/// <param name="AtMs">Capture wall-clock (the segment's arrivedAt), the timeline reference.</param>
/// <param name="X">World X (Absolute) or signed ΔX this tick (Delta).</param>
/// <param name="Y">World Y (Absolute) or signed ΔY this tick (Delta).</param>
/// <param name="Z">World Z / height (Absolute) or signed ΔZ this tick (Delta).</param>
/// <param name="Opcode">The 0x37xx opcode this sample came from.</param>
/// <param name="Offset">Byte offset (after the id var-int) an Absolute triplet was read at (0 for Delta).</param>
/// <param name="Kind">Absolute keyframe vs per-tick delta.</param>
public readonly record struct MovementSample(
    int EntityId,
    long AtMs,
    float X,
    float Y,
    float Z,
    int Opcode,
    int Offset,
    MovementKind Kind = MovementKind.Absolute);
