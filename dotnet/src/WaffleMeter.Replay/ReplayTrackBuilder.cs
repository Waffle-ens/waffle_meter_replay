namespace WaffleMeter.Replay;

/// <summary>
/// Shared logic that turns an entity's raw <see cref="MovementSample"/>s into a clean
/// <see cref="ReplayTrack"/>: window-slice, split absolute keyframes from the dense 0x371D delta stream,
/// keep the entity's dominant transform layout (dropping offset-by-one false positives on stray opcodes)
/// PLUS every cast-derived keyframe (exact by construction), then reconstruct a dense absolute path by
/// integrating the deltas between keyframes (<see cref="MovementReconstructor"/>). Used by both
/// <see cref="MovementRecorder"/> (explicit battle scoping) and the live
/// <see cref="MovementCaptureService"/> (rolling buffer sliced on battle-logged).
/// </summary>
internal static class ReplayTrackBuilder
{
    private const int DeltaOpcode = 0x371D;

    public static (List<ReplayPoint> Points, int Opcode, int Offset) CleanPoints(
        IEnumerable<MovementSample> samples, long startMs, long endMs)
    {
        List<MovementSample> window = samples.Where(s => s.AtMs >= startMs && s.AtMs <= endMs).ToList();
        if (window.Count == 0)
        {
            return (new List<ReplayPoint>(), 0, -1);
        }

        List<MovementSample> deltas = window
            .Where(s => s.Kind == MovementKind.Delta)
            .OrderBy(s => s.AtMs)
            .ToList();

        List<MovementSample> absolutes = window.Where(s => s.Kind == MovementKind.Absolute).ToList();
        if (absolutes.Count == 0)
        {
            // deltas but no world anchor: keep the track (its identity) but with no placeable path
            return (MovementReconstructor.Reconstruct(absolutes, deltas, startMs), DeltaOpcode, 0);
        }

        // Cast-derived keyframes (0x3802) are exact by construction — the packet states the entity's
        // position — so they are never subject to the layout vote; they are simply trusted. They are what
        // gives SELF and the BOSS a path at all: neither appears in the dense 0x371D broadcast, and both
        // cast constantly.
        List<MovementSample> casts = absolutes.Where(s => s.Opcode == MovementCaptureService.CastOpcode).ToList();
        List<MovementSample> transforms = absolutes.Where(s => s.Opcode != MovementCaptureService.CastOpcode).ToList();

        // Among the TRANSFORM opcodes, keep only the entity's dominant (opcode, offset) layout — the float
        // triplet's byte offset wobbles across opcodes, and the runners-up are offset-by-one false positives.
        List<MovementSample> keyframes = casts;
        if (transforms.Count > 0)
        {
            (int Opcode, int Offset) layout = transforms
                .GroupBy(s => (s.Opcode, s.Offset))
                .OrderByDescending(g => g.Count())
                .First().Key;

            keyframes = keyframes
                .Concat(transforms.Where(s => s.Opcode == layout.Opcode && s.Offset == layout.Offset))
                .ToList();
        }

        keyframes = keyframes.OrderBy(s => s.AtMs).ToList();

        // Diagnostics: which layout the path was decoded at (the dominant transform, or the cast feed when
        // that is all this entity has — which is the normal case for self and the boss).
        MovementSample dominant = keyframes
            .GroupBy(s => (s.Opcode, s.Offset))
            .OrderByDescending(g => g.Count())
            .First()
            .First();

        List<ReplayPoint> points = MovementReconstructor.Reconstruct(keyframes, deltas, startMs);
        return (points, dominant.Opcode, dominant.Offset);
    }

    public static ReplayTrack BuildTrack(
        int uid,
        IEnumerable<MovementSample> samples,
        long startMs,
        long endMs,
        ReplayIdentity id,
        bool isTarget,
        int partySlot)
    {
        (List<ReplayPoint> points, int opcode, int offset) = CleanPoints(samples, startMs, endMs);
        return new ReplayTrack
        {
            Uid = uid,
            Nickname = id.Nickname,
            Server = id.Server,
            Job = id.Job,
            IsSelf = id.IsSelf,
            IsTarget = isTarget,
            PartySlot = partySlot,
            Points = points,
            SourceOpcode = opcode,
            SourceOffset = offset,
        };
    }
}
