namespace WaffleMeter.Replay;

/// <summary>
/// Turns an entity's mixed movement samples — sparse <see cref="MovementKind.Absolute"/> keyframes plus
/// the dense <see cref="MovementKind.Delta"/> stream (0x371D) — into a dense absolute path.
/// <para>
/// Between two consecutive keyframes the signed per-tick deltas are cumulatively integrated and then
/// <b>affine-pinned</b> so the run's endpoints land exactly on the keyframes: per axis,
/// <c>pos = K0 + (K1 - K0) · (cum / cumEnd)</c> when that axis moved, else linear in time. This keeps the
/// dense 0x371D shape while the keyframes cancel drift and remove any dependence on the exact quantization
/// scale (empirically ≈1.0 world unit/count). Deltas before the first / after the last keyframe are
/// dead-reckoned at unit scale, capped to <see cref="MaxDeadReckonMs"/> so an out-of-range (AoI) gap can't
/// draw a long wandering tail.
/// </para>
/// Validated on controlled captures (scripted axis-aligned walk + an L-shaped path): held-out-keyframe
/// median error 3-9% of the path bbox, shape r≈0.92-0.995. See docs/replay-feature-plan.md §10.
/// </summary>
internal static class MovementReconstructor
{
    /// <summary>How far past the last keyframe (or before the first) to dead-reckon unpinned deltas.</summary>
    private const long MaxDeadReckonMs = 2500;

    /// <summary>World units per delta count (0x371D). ≈1.0 empirically; affine pinning makes the exact
    /// value irrelevant between keyframes — it only affects the short dead-reckoned head/tail.</summary>
    private const double Scale = 1.0;

    /// <param name="keyframes">Absolute samples of ONE entity at its dominant layout, ascending by time.</param>
    /// <param name="deltas">The entity's 0x371D delta samples, ascending by time.</param>
    /// <param name="startMs">Battle start; output <see cref="ReplayPoint.TMs"/> is relative to it.</param>
    public static List<ReplayPoint> Reconstruct(
        List<MovementSample> keyframes, List<MovementSample> deltas, long startMs)
    {
        if (keyframes.Count == 0)
        {
            return new List<ReplayPoint>(); // no world anchor — can't place the delta path
        }

        if (deltas.Count == 0)
        {
            return ToPoints(keyframes, startMs); // absolute-only (e.g. self / sparse mover)
        }

        var abs = new SortedDictionary<long, (double X, double Y, double Z)>();
        void Put(long t, double x, double y, double z) => abs[t] = (x, y, z);

        foreach (MovementSample k in keyframes)
        {
            Put(k.AtMs, k.X, k.Y, k.Z);
        }

        // main body: pin each inter-keyframe run of deltas to its bracketing keyframes
        for (int i = 0; i + 1 < keyframes.Count; i++)
        {
            MovementSample k0 = keyframes[i];
            MovementSample k1 = keyframes[i + 1];
            List<MovementSample> run = InRange(deltas, k0.AtMs, k1.AtMs);
            if (run.Count == 0)
            {
                continue;
            }

            double cx = 0, cy = 0, cz = 0;
            var cum = new List<(long T, double X, double Y, double Z)>(run.Count);
            foreach (MovementSample d in run)
            {
                cx += d.X * Scale;
                cy += d.Y * Scale;
                cz += d.Z * Scale;
                cum.Add((d.AtMs, cx, cy, cz));
            }

            (double ex, double ey, double ez) = (cum[^1].X, cum[^1].Y, cum[^1].Z);

            // The affine pin divides by the run's NET motion, so it needs the run to actually go somewhere
            // on that axis. How far it wandered (the peak) tells us whether it did.
            double sx = Peak(cum, c => c.X), sy = Peak(cum, c => c.Y), sz = Peak(cum, c => c.Z);

            foreach ((long t, double px, double py, double pz) in cum)
            {
                double f = (double)(t - k0.AtMs) / Math.Max(1, k1.AtMs - k0.AtMs);
                Put(t,
                    Axis(k0.X, k1.X, px, ex, sx, f),
                    Axis(k0.Y, k1.Y, py, ey, sy, f),
                    Axis(k0.Z, k1.Z, pz, ez, sz, f));
            }
        }

        DeadReckon(abs, deltas, keyframes[0], before: true);
        DeadReckon(abs, deltas, keyframes[^1], before: false);

        var points = new List<ReplayPoint>(abs.Count);
        foreach ((long t, (double x, double y, double z)) in abs)
        {
            points.Add(new ReplayPoint((int)(t - startMs), (float)x, (float)y, (float)z));
        }

        return points;
    }

    /// <summary>
    /// Place one axis of one delta tick between its bracketing keyframes.
    /// <para>
    /// The affine pin — <c>w0 + (w1-w0)·(cum/cumEnd)</c> — is scale-free (it cancels the delta quantization
    /// unit entirely), but it DIVIDES BY THE RUN'S NET MOTION. When a character walks out and back along an
    /// axis, that net motion is ~0 while the intermediate offsets are not, and the ratio explodes: measured
    /// live, a player marched 14,000 units per 100 ms tick straight off the map (a perfectly straight line,
    /// the other axis frozen — the signature of this division).
    /// </para>
    /// <para>
    /// So the pin is used only when the run actually WENT somewhere on that axis — its net motion is a
    /// decent share of how far it wandered. Otherwise the delta shape is integrated at unit scale and the
    /// leftover error to the closing keyframe is spread linearly over the run (a loop closure). Both land
    /// exactly on w1; the second cannot blow up, because it never divides by the net.
    /// </para>
    /// </summary>
    /// <param name="peak">The farthest the cumulative offset got from the start of the run on this axis.</param>
    private static double Axis(double w0, double w1, double cum, double cumEnd, double peak, double timeFrac)
    {
        if (Math.Abs(cumEnd) >= MinNetShareOfPeak * peak && Math.Abs(cumEnd) > 1e-9)
        {
            return w0 + (w1 - w0) * (cum / cumEnd); // went somewhere: scale-free pin
        }

        return w0 + cum + (w1 - w0 - cumEnd) * timeFrac; // wandered and came back: integrate + close the loop
    }

    /// <summary>How much of a run's wandering must be NET motion before the scale-free pin is trusted. At
    /// 0.5 the pin can stretch a tick by at most 2x, which bounds the artifact it used to produce.</summary>
    private const double MinNetShareOfPeak = 0.5;

    private static double Peak(List<(long T, double X, double Y, double Z)> cum, Func<(long T, double X, double Y, double Z), double> axis)
    {
        double peak = 0;
        foreach ((long T, double X, double Y, double Z) c in cum)
        {
            peak = Math.Max(peak, Math.Abs(axis(c)));
        }

        return peak;
    }

    // deltas outside the keyframe span have only a single anchor, so integrate at unit scale from that
    // keyframe (backward for the head, forward for the tail), capped in time to bound drift.
    private static void DeadReckon(
        SortedDictionary<long, (double X, double Y, double Z)> abs,
        List<MovementSample> deltas, MovementSample anchor, bool before)
    {
        (double x, double y, double z) = (anchor.X, anchor.Y, anchor.Z);
        if (before)
        {
            for (int i = deltas.Count - 1; i >= 0; i--)
            {
                MovementSample d = deltas[i];
                if (d.AtMs >= anchor.AtMs || anchor.AtMs - d.AtMs > MaxDeadReckonMs)
                {
                    continue;
                }

                // stepping back in time: undo this tick's delta to get the earlier position
                x -= d.X * Scale;
                y -= d.Y * Scale;
                z -= d.Z * Scale;
                abs.TryAdd(d.AtMs, (x, y, z));
            }
        }
        else
        {
            foreach (MovementSample d in deltas)
            {
                if (d.AtMs <= anchor.AtMs || d.AtMs - anchor.AtMs > MaxDeadReckonMs)
                {
                    continue;
                }

                x += d.X * Scale;
                y += d.Y * Scale;
                z += d.Z * Scale;
                abs.TryAdd(d.AtMs, (x, y, z));
            }
        }
    }

    private static List<MovementSample> InRange(List<MovementSample> deltas, long lo, long hi)
    {
        var run = new List<MovementSample>();
        foreach (MovementSample d in deltas)
        {
            if (d.AtMs > lo && d.AtMs <= hi)
            {
                run.Add(d);
            }
        }

        return run;
    }

    private static List<ReplayPoint> ToPoints(List<MovementSample> keyframes, long startMs)
    {
        var points = new List<ReplayPoint>(keyframes.Count);
        long last = long.MinValue;
        foreach (MovementSample k in keyframes)
        {
            if (k.AtMs == last)
            {
                continue;
            }

            last = k.AtMs;
            points.Add(new ReplayPoint((int)(k.AtMs - startMs), k.X, k.Y, k.Z));
        }

        return points;
    }
}
