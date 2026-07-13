using System.IO;
using WaffleMeter.Data;

namespace WaffleMeter.Replay;

/// <summary>
/// Live integration facade: the single object the meter taps. It keeps a bounded rolling buffer of
/// decoded movement samples and, when a battle is logged, slices the buffer to that battle's window and
/// builds a <see cref="ReplayRecording"/> for every participant.
/// <para>
/// Design choices:
/// <list type="bullet">
/// <item>Rolling buffer + slice-on-log (rather than catching the battle-start edge) is robust to missed
/// start signals and naturally captures a short pre-pull lead-in.</item>
/// <item>Identity comes from the FROZEN saved-report contributors (their Nickname/Server/Job/IsExecutor),
/// so a delayed entity-id reuse can't repaint a participant — the same protection the saved DPS report
/// gets. Movers that resolve to a named user but weren't damage contributors (e.g. supports) are added
/// via the optional live identity source.</item>
/// <item>Fires for BOTH a cleared kill and a wipe (the 직전 전투), since the meter logs a battle on end
/// regardless of a kill; <see cref="ReplayRecording.BossDefeated"/> records which.</item>
/// </list>
/// All methods run on the single capture-consumer thread (same as the DPS parser); not internally locked.
/// </para>
/// </summary>
public sealed class MovementCaptureService : IReplayEngine
{
    private readonly MovementParser _parser;
    private readonly IReplayIdentitySource? _extraIdentity;
    private readonly string? _persistDir;
    private readonly Dictionary<int, List<MovementSample>> _buffer = new();
    private readonly Dictionary<long, ReplayRecording> _byBattleStart = new();
    private readonly long _retentionMs;
    private readonly int _maxEntities;
    private readonly int _maxSamplesPerEntity;
    private readonly int _maxRecordings;

    // Boss mechanics: a rolling list of casts (a fight is 100-300, so one flat list is plenty). Each cast
    // is stamped, AT CAPTURE TIME, with the caster's last-broadcast remaining HP — the saved report only
    // knows the battle's END state, which would label every cast identically.
    private readonly List<(CastSample Cast, long RemainHp)> _casts = new();
    private readonly Dictionary<int, (long Hp, long AtMs)> _lastHp = new();
    private readonly int _maxCasts;

    /// <summary>Cap on the HP table. Everything that takes damage broadcasts HP — a long dungeon session
    /// sees thousands of entities (5,584 in a measured 2 h corpus) — so this is pruned by age, NOT by
    /// refusing new ids: a hard "first N entities win" cap would lock the boss OUT of the table on any
    /// pull that started after enough trash, and its mechanics would lose their HP stamp.</summary>
    private const int MaxHpEntities = 4096;

    private long _latestAt;
    private long _sinceTrim;

    /// <summary>The most recently built recording = the 직전 전투 (or last cleared battle).</summary>
    public ReplayRecording? LastRecording { get; private set; }

    /// <param name="extraIdentity">Optional live resolver to include named movers who weren't damage
    /// contributors (supports). Pass a <see cref="DataManagerIdentitySource"/> in the app; null to include
    /// only the report's contributors.</param>
    /// <param name="persistDir">If set, each built recording is also written to
    /// <c>{persistDir}/replay-{startMs}.json</c> (survives restart; lets history be replayed and lets a
    /// recording be inspected offline). Null = in-memory only.</param>
    public MovementCaptureService(
        IReplayIdentitySource? extraIdentity = null,
        string? persistDir = null,
        long retentionMs = 35 * 60 * 1000L,
        int maxEntities = 512,
        int maxSamplesPerEntity = 20_000,
        int maxRecordings = 20,
        int maxCasts = 20_000)
    {
        _extraIdentity = extraIdentity;
        _persistDir = persistDir;
        _retentionMs = retentionMs;
        _maxEntities = maxEntities;
        _maxSamplesPerEntity = maxSamplesPerEntity;
        _maxRecordings = maxRecordings;
        _maxCasts = maxCasts;
        _parser = new MovementParser(OnSample, OnCast, OnHp);
    }

    /// <summary>Tap point: feed one assembled application packet (same bytes the DPS parser receives).</summary>
    public void Scan(byte[] packet, long at) => _parser.Feed(packet, at);

    /// <summary>Build and store the recording for a just-logged battle. Wire to
    /// <c>DpsCalculator.OnBattleLogged</c>.</summary>
    /// <param name="partyMembers">Non-null: only these (nickname, server) identities — the party/raid
    /// roster — plus self and the boss are included; all other combat contributors (e.g. random players on
    /// a shared field boss) are dropped. An EMPTY roster means "not in a party (or roster unknown)" and
    /// keeps self + boss only — never every nearby random. Null = include every contributor (CLI/tests
    /// escape hatch; the live app always passes its roster, possibly empty).</param>
    public ReplayRecording OnBattleLogged(DpsLog log, IReadOnlyCollection<(string Nickname, int Server)>? partyMembers = null)
    {
        DpsReport report = log.Report;
        long start = report.BattleStart;
        long end = report.BattleEnd >= start ? report.BattleEnd : start;
        int? targetUid = report.Target?.Id;

        HashSet<(string, int)>? party = partyMembers is null
            ? null
            : new HashSet<(string, int)>(partyMembers);

        var tracks = new List<ReplayTrack>();
        var included = new HashSet<int>();

        foreach (User u in report.Contributors)
        {
            bool isSelf = u.IsExecutor || (report.ExecutorId != 0 && u.Id == report.ExecutorId);
            // Party/raid-only scoping: keep self always; otherwise require roster membership by name+server.
            if (party != null && !isSelf && !party.Contains((u.Nickname ?? string.Empty, u.Server)))
            {
                continue;
            }

            if (!included.Add(u.Id))
            {
                continue;
            }

            tracks.Add(ReplayTrackBuilder.BuildTrack(
                u.Id, Samples(u.Id), start, end, FromUser(u, report.ExecutorId), isTarget: false,
                report.PartySlots.GetValueOrDefault(u.Id)));
        }

        // Party/raid supports who moved but dealt no damage: added ONLY when a party roster is known and they
        // are in it. Without a roster (e.g. an open-field boss), we do NOT add nearby named movers — otherwise
        // every random player in view of the fight would appear. Damage contributors (above) already cover
        // everyone who actually fought the boss.
        if (_extraIdentity is { } src && party != null)
        {
            foreach (int uid in _buffer.Keys)
            {
                if (included.Contains(uid) || uid == targetUid)
                {
                    continue;
                }

                ReplayIdentity id = src.Resolve(uid);
                if (id.Found && !string.IsNullOrEmpty(id.Nickname) && party.Contains((id.Nickname!, id.Server)))
                {
                    included.Add(uid);
                    tracks.Add(ReplayTrackBuilder.BuildTrack(uid, Samples(uid), start, end, id, isTarget: false, 0));
                }
            }
        }

        if (targetUid is { } tuid && _buffer.ContainsKey(tuid))
        {
            ReplayIdentity tid = _extraIdentity?.Resolve(tuid) ?? default;
            tracks.Add(ReplayTrackBuilder.BuildTrack(tuid, Samples(tuid), start, end, tid, isTarget: true, 0));
        }

        var rec = new ReplayRecording
        {
            BattleEpoch = 0,
            StartMs = start,
            EndMs = end,
            BossDefeated = report.Target is { MaxHp: > 0, RemainHp: <= 0 },
            TargetCode = report.Target?.Mob.Code,
            TargetName = report.Target?.Mob.Name,
            Tracks = tracks,
            Casts = BuildCasts(targetUid, start, end, report.Target?.MaxHp ?? 0),
        };

        LastRecording = rec;
        if (start > 0)
        {
            _byBattleStart[start] = rec;
            TrimRecordings();
        }

        Persist(rec);
        return rec;
    }

    /// <summary>The BOSS's mechanic casts inside the battle window, in time order. Only the target's own
    /// casts are kept — a player's abilities are not mechanics and would swamp the recording. Each cast
    /// carries the boss's HP fraction at that moment (from the 0x8D00 broadcast, so "the pattern he does at
    /// 70 %" groups); the fraction is -1 when the battle never reported a max HP.</summary>
    private List<ReplayCast> BuildCasts(int? targetUid, long start, long end, long maxHp)
    {
        var casts = new List<ReplayCast>();
        if (targetUid is not { } boss)
        {
            return casts;
        }

        // The report only knows a boss's max HP once the meter has seen it; on a fight the capture joined
        // late it can be 0. Fall back to the highest HP the boss broadcast during the window, so the HP
        // fractions stay meaningful (relative to the fight's own peak) instead of collapsing to "unknown".
        if (maxHp <= 0)
        {
            foreach ((CastSample c, long remainHp) in _casts)
            {
                if (c.ActorId == boss && c.AtMs >= start && c.AtMs <= end && remainHp > maxHp)
                {
                    maxHp = remainHp;
                }
            }
        }

        foreach ((CastSample c, long remainHp) in _casts)
        {
            if (c.ActorId != boss || c.AtMs < start || c.AtMs > end)
            {
                continue;
            }

            float hp = maxHp > 0 && remainHp >= 0
                ? Math.Clamp((float)((double)remainHp / maxHp), 0f, 1f)
                : -1f;

            casts.Add(new ReplayCast
            {
                TMs = (int)(c.AtMs - start),
                SkillCode = c.SkillCode,
                FacingDeg = c.FacingDeg,
                HpFraction = hp,
                Targets = c.Targets.Select(t => new ReplayCastTarget(t.EntityId, t.X, t.Y, t.Z)).ToList(),
            });
        }

        casts.Sort((a, b) => a.TMs.CompareTo(b.TMs));
        return casts;
    }

    // Best-effort: write the recording to {persistDir}/replay-{startMs}.json (survives restart + offline-inspectable).
    private void Persist(ReplayRecording rec)
    {
        if (_persistDir is null || rec.PointCount == 0)
        {
            return;
        }

        try
        {
            Directory.CreateDirectory(_persistDir);
            File.WriteAllText(Path.Combine(_persistDir, $"replay-{rec.StartMs}.json"), ReplaySerializer.Serialize(rec));
        }
        catch
        {
            // persistence is a convenience; never let it disturb capture
        }
    }

    /// <summary>Look up a stored recording by its battle-start (matches a saved <c>DpsReport.BattleStart</c>).</summary>
    public bool TryGetForBattle(long battleStartMs, out ReplayRecording? recording)
        => _byBattleStart.TryGetValue(battleStartMs, out recording);

    /// <summary>Clear all buffered movement + casts + stored recordings (wire to the meter reset / flush).</summary>
    public void Reset()
    {
        _buffer.Clear();
        _casts.Clear();
        _lastHp.Clear();
        _byBattleStart.Clear();
        LastRecording = null;
        _latestAt = 0;
        _sinceTrim = 0;
    }

    private IEnumerable<MovementSample> Samples(int uid)
        => _buffer.TryGetValue(uid, out List<MovementSample>? list) ? list : Enumerable.Empty<MovementSample>();

    // A skill cast. Kept regardless of caster (the battle's boss isn't known until it is logged); the
    // recorder filters to the target's own casts when it builds the recording.
    private void OnCast(CastSample c)
    {
        if (c.AtMs > _latestAt)
        {
            _latestAt = c.AtMs;
        }

        if (_casts.Count >= _maxCasts)
        {
            _casts.RemoveRange(0, _casts.Count / 2); // drop the oldest half — a bounded rolling window
        }

        _casts.Add((c, _lastHp.TryGetValue(c.ActorId, out (long Hp, long AtMs) hp) ? hp.Hp : -1));

        // A cast packet also states WHERE its anchor entities were standing — the caster on a self-cast,
        // the marked player on a targeted one (RE-verified: within ~1 unit of the boss's own keyframes,
        // within 0-290 units of a marked player's tracked position). That makes casts a position source
        // for the two entities the 0x371D broadcast leaves sparse:
        //   - SELF, which the server never echoes back (you know where you are), and
        //   - the BOSS, which only gets occasional keyframes,
        // both of which cast constantly. Feed them in as ordinary absolute keyframes.
        foreach (CastTarget t in c.Targets)
        {
            OnSample(new MovementSample(t.EntityId, c.AtMs, t.X, t.Y, t.Z, CastOpcode, 0));
        }
    }

    /// <summary>The opcode cast-derived position keyframes are tagged with, so the track builder can trust
    /// them alongside an entity's dominant transform layout instead of competing with it.</summary>
    internal const int CastOpcode = 0x3802;

    // Remaining-HP broadcast for an entity. Only the latest per entity is kept (this is the "what HP was
    // the boss at" stamp, not a timeline).
    private void OnHp(int entityId, long atMs, long hp)
    {
        if (atMs > _latestAt)
        {
            _latestAt = atMs;
        }

        if (_lastHp.Count >= MaxHpEntities && !_lastHp.ContainsKey(entityId))
        {
            PruneHp();
        }

        _lastHp[entityId] = (hp, atMs);
    }

    // Drop entities whose HP hasn't been broadcast inside the retention window (dead trash, a previous
    // instance). If that frees nothing — a burst of live entities — halve the table by age so a new boss
    // can always get in.
    private void PruneHp()
    {
        long cutoff = _latestAt - _retentionMs;
        List<int> stale = _lastHp.Where(kv => kv.Value.AtMs < cutoff).Select(kv => kv.Key).ToList();
        if (stale.Count == 0)
        {
            stale = _lastHp.OrderBy(kv => kv.Value.AtMs).Take(_lastHp.Count / 2).Select(kv => kv.Key).ToList();
        }

        foreach (int id in stale)
        {
            _lastHp.Remove(id);
        }
    }

    private void OnSample(MovementSample s)
    {
        if (s.AtMs > _latestAt)
        {
            _latestAt = s.AtMs;
        }

        if (!_buffer.TryGetValue(s.EntityId, out List<MovementSample>? list))
        {
            if (_buffer.Count >= _maxEntities)
            {
                return;
            }

            list = new List<MovementSample>();
            _buffer[s.EntityId] = list;
        }

        if (list.Count >= _maxSamplesPerEntity)
        {
            DecimateInPlace(list);
        }

        list.Add(s);

        if (++_sinceTrim >= 4096)
        {
            _sinceTrim = 0;
            TrimOld();
        }
    }

    // Drop samples older than the retention window so a long session can't grow unbounded.
    private void TrimOld()
    {
        long cutoff = _latestAt - _retentionMs;

        int keepCast = 0;
        for (int i = 0; i < _casts.Count; i++)
        {
            if (_casts[i].Cast.AtMs >= cutoff)
            {
                _casts[keepCast++] = _casts[i];
            }
        }

        _casts.RemoveRange(keepCast, _casts.Count - keepCast);

        var empties = new List<int>();
        foreach ((int uid, List<MovementSample> list) in _buffer)
        {
            int keep = 0;
            for (int i = 0; i < list.Count; i++)
            {
                if (list[i].AtMs >= cutoff)
                {
                    list[keep++] = list[i];
                }
            }

            list.RemoveRange(keep, list.Count - keep);
            if (list.Count == 0)
            {
                empties.Add(uid);
            }
        }

        foreach (int uid in empties)
        {
            _buffer.Remove(uid);
        }
    }

    private void TrimRecordings()
    {
        while (_byBattleStart.Count > _maxRecordings)
        {
            long oldest = long.MaxValue;
            foreach (long k in _byBattleStart.Keys)
            {
                if (k < oldest)
                {
                    oldest = k;
                }
            }

            _byBattleStart.Remove(oldest);
        }
    }

    private static void DecimateInPlace(List<MovementSample> list)
    {
        int w = 0;
        for (int r = 0; r < list.Count; r += 2)
        {
            list[w++] = list[r];
        }

        list.RemoveRange(w, list.Count - w);
    }

    private static ReplayIdentity FromUser(User u, int executorId)
        => new(true, u.Nickname, u.Server, u.Job?.ClassName(), u.IsExecutor || (executorId != 0 && u.Id == executorId));
}
