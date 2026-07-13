namespace WaffleMeter.Replay;

/// <summary>One entity a cast is anchored to, and exactly where it stood at cast time.</summary>
public readonly record struct CastTarget(int EntityId, float X, float Y, float Z);

/// <summary>
/// One decoded skill cast (0x3802) — the raw half of a boss mechanic. The ZONE it paints (circle / donut /
/// cone / line, its radius/angle and telegraph time) is NOT in the packet: it comes from the client's shape
/// catalog keyed by <see cref="SkillCode"/>. What only the packet knows is here.
/// <para>
/// Body layout after the actor var-int (RE'd against real fights — see docs/replay-feature-plan.md §13.1):
/// <code>
/// [flag][u32 skillCode][u8 counter][u8][varint targetId][f32 facingDeg][f32 X][f32 Y][f32 Z][varint …]
///    … long frames (flag 0x20) then repeat [varint uid][f32 X][f32 Y][f32 Z] per ADDITIONAL target
/// </code>
/// Key properties, measured:
/// <list type="bullet">
/// <item><b>targetId + XYZ = the anchor and its exact position.</b> Self-cast (targetId == caster): the XYZ
/// is the boss's own live position (median 1 unit from its 0x372F keyframes — a better position source than
/// the keyframe stream). Player-targeted: the XYZ is that player's feet (median 0-290 units from their
/// tracked position, vs 587-1894 for the other party members).</item>
/// <item><b>facingDeg = the caster's facing at cast</b> (degrees, atan2(dy,dx) convention): mean circular
/// error 9.9° against the direction from the boss to its target (random = 90°). This rotates the
/// directional zones (cone / line); no continuous boss-facing feed is needed.</item>
/// <item><b>Long frames carry a per-target list</b> — a spread marks several players at once and the packet
/// names every one of them with coordinates.</item>
/// </list>
/// </para>
/// </summary>
/// <param name="ActorId">The caster (matched against the battle's boss/target uid).</param>
/// <param name="AtMs">Capture wall-clock (the segment's arrivedAt), same timeline as movement.</param>
/// <param name="SkillCode">The cast's skill code — the key into the client shape catalog.</param>
/// <param name="FacingDeg">The caster's facing at cast, in degrees.</param>
/// <param name="Targets">The anchor(s): the primary target first, then any additional marked players.</param>
public readonly record struct CastSample(
    int ActorId,
    long AtMs,
    int SkillCode,
    float FacingDeg,
    IReadOnlyList<CastTarget> Targets);
