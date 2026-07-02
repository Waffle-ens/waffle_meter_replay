namespace WaffleMeter.Replay;

/// <summary>
/// Shared logic that turns an entity's raw <see cref="MovementSample"/>s into a clean
/// <see cref="ReplayTrack"/>: window-slice, split absolute keyframes from the dense 0x371D delta stream,
/// keep only the entity's dominant absolute (opcode, offset) layout (drops offset-by-one false positives
/// on stray opcodes), then reconstruct a dense absolute path by integrating the deltas between keyframes
/// (<see cref="MovementReconstructor"/>). Used by both <see cref="MovementRecorder"/> (explicit battle
/// scoping) and the live <see cref="MovementCaptureService"/> (rolling buffer sliced on battle-logged).
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

        // dominant layout is chosen among ABSOLUTES only — the dense delta stream would otherwise swamp
        // the sparse keyframes and hide the real world coordinates.
        (int Opcode, int Offset) layout = absolutes
            .GroupBy(s => (s.Opcode, s.Offset))
            .OrderByDescending(g => g.Count())
            .First().Key;

        List<MovementSample> keyframes = absolutes
            .Where(s => s.Opcode == layout.Opcode && s.Offset == layout.Offset)
            .OrderBy(s => s.AtMs)
            .ToList();

        List<ReplayPoint> points = MovementReconstructor.Reconstruct(keyframes, deltas, startMs);
        return (points, layout.Opcode, layout.Offset);
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
