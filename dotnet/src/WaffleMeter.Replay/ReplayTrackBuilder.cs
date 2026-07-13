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

        keyframes = DropStrayClusters(DropSpikes(keyframes.OrderBy(s => s.AtMs).ToList()));
        if (keyframes.Count == 0)
        {
            return (new List<ReplayPoint>(), 0, -1);
        }

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

    /// <summary>
    /// Drop keyframes that can only be decode noise — an IMPOSSIBLE EXCURSION: the entity appears somewhere
    /// it could not have run to, and then reappears back where it was, having covered neither leg at a speed
    /// a character can move. (Measured: a handful of self keyframes landing at the world origin, 21k units
    /// out and back, in the middle of a boss fight.)
    /// <para>
    /// A real recall/blink is NOT an excursion — the entity STAYS at its new place — so those survive and
    /// the player snaps across them (<see cref="ReplayGeometry.IsTeleport"/>). The noise arrives in short
    /// runs, hence the look-ahead: a lone bad point and a run of them are equally impossible if the track
    /// returns.
    /// </para>
    /// <para>
    /// This is the safety net for every keyframe source: a byte-walk over a variable-length packet tail can
    /// always line up on garbage that passes a float sanity check, and one such point stretches the map view
    /// across the whole world.
    /// </para>
    /// </summary>
    private static List<MovementSample> DropSpikes(List<MovementSample> keyframes)
    {
        if (keyframes.Count < 3)
        {
            return keyframes;
        }

        const int MaxExcursion = 8; // how long a bogus run may be before we assume the entity really moved

        var kept = new List<MovementSample>(keyframes.Count) { keyframes[0] };
        for (int i = 1; i < keyframes.Count; i++)
        {
            if (Reachable(kept[^1], keyframes[i]))
            {
                kept.Add(keyframes[i]);
                continue;
            }

            // Unreachable. Does the track come BACK to where it was within a few samples? Then everything in
            // between is noise, not movement.
            int back = -1;
            for (int j = i + 1; j < keyframes.Count && j <= i + MaxExcursion; j++)
            {
                if (Reachable(kept[^1], keyframes[j]))
                {
                    back = j;
                    break;
                }
            }

            if (back < 0)
            {
                kept.Add(keyframes[i]); // a genuine jump — the entity stays over there
                continue;
            }

            kept.Add(keyframes[back]);
            i = back;
        }

        return kept;
    }

    /// <summary>
    /// Drop a stray cluster: a short run of keyframes sitting nowhere near the rest of the track, which the
    /// excursion test cannot see because the run never "returns" — it opens or closes the track. (Measured:
    /// 14 keyframes at the world origin at the HEAD of a 210-point track, 36k units from the fight.)
    /// <para>
    /// A run is dropped only when it is BOTH a small minority of the track AND far from where the track
    /// actually is — so a genuine relocation, which is either substantial or nearby, survives.
    /// </para>
    /// </summary>
    private static List<MovementSample> DropStrayClusters(List<MovementSample> keyframes)
    {
        if (keyframes.Count < 10)
        {
            return keyframes;
        }

        // Split into runs of mutually reachable samples: an unreachable step opens a new run.
        var runs = new List<List<MovementSample>> { new() { keyframes[0] } };
        for (int i = 1; i < keyframes.Count; i++)
        {
            if (!Reachable(keyframes[i - 1], keyframes[i]))
            {
                runs.Add(new List<MovementSample>());
            }

            runs[^1].Add(keyframes[i]);
        }

        if (runs.Count == 1)
        {
            return keyframes;
        }

        List<MovementSample> dominant = runs.MaxBy(r => r.Count)!;
        double cx = Median(dominant.Select(s => (double)s.X));
        double cy = Median(dominant.Select(s => (double)s.Y));
        double minority = keyframes.Count * StrayClusterShare;

        return runs
            .Where(r => ReferenceEquals(r, dominant) || r.Count >= minority || Near(r, cx, cy))
            .SelectMany(r => r)
            .ToList();
    }

    /// <summary>A run holding less than this share of the track's keyframes is a candidate for noise — if it
    /// is also nowhere near the rest of the track.</summary>
    private const double StrayClusterShare = 0.10;

    private static bool Near(List<MovementSample> run, double cx, double cy)
    {
        double dx = Median(run.Select(s => (double)s.X)) - cx;
        double dy = Median(run.Select(s => (double)s.Y)) - cy;
        return dx * dx + dy * dy <= MaxKeyframeJumpWorld * MaxKeyframeJumpWorld;
    }

    private static double Median(IEnumerable<double> values)
    {
        double[] sorted = values.OrderBy(v => v).ToArray();
        return sorted[sorted.Length / 2];
    }

    /// <summary>Locomotion ceiling for the spike test (world units per ms). ~2.5 u/ms = 2,500 u/s, comfortably
    /// above a measured sprint (~1,000-1,600 u/s) but far below the multi-second, tens-of-thousands-of-units
    /// jumps a decode artifact produces. Deliberately tighter than <see cref="ReplayGeometry"/>'s teleport
    /// threshold, which has to tolerate mounts and gliding: here a genuinely fast displacement is not lost,
    /// because the NEXT keyframe confirms it and the point is kept.</summary>
    private const double MaxKeyframeSpeedWorldPerMs = 2.5;

    /// <summary>Floor so the normal sub-second cadence can't trip the test on jitter.</summary>
    private const double MinKeyframeJumpWorld = 1500;

    /// <summary>Ceiling on what a long gap may excuse. Time alone would eventually excuse anything — a 10 s
    /// gap at the speed limit permits a 25,000-unit jump — and that is how a run of origin-ish garbage
    /// coordinates slipped through. Nobody crosses a boss room by 12k units and comes straight back, so a
    /// jump past this that RETURNS is noise. A jump past this that does NOT return is a real teleport (a
    /// phase, a recall) and is kept: see <see cref="DropSpikes"/>.</summary>
    private const double MaxKeyframeJumpWorld = 12_000;

    private static bool Reachable(in MovementSample a, in MovementSample b)
    {
        double dx = b.X - a.X, dy = b.Y - a.Y;
        double dtMs = Math.Max(1, b.AtMs - a.AtMs);
        double maxDist = Math.Min(
            MaxKeyframeJumpWorld,
            Math.Max(MinKeyframeJumpWorld, MaxKeyframeSpeedWorldPerMs * dtMs));

        return dx * dx + dy * dy <= maxDist * maxDist;
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
