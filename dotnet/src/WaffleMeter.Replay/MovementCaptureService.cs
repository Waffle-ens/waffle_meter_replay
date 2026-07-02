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
        int maxRecordings = 20)
    {
        _extraIdentity = extraIdentity;
        _persistDir = persistDir;
        _retentionMs = retentionMs;
        _maxEntities = maxEntities;
        _maxSamplesPerEntity = maxSamplesPerEntity;
        _maxRecordings = maxRecordings;
        _parser = new MovementParser(OnSample);
    }

    /// <summary>Tap point: feed one assembled application packet (same bytes the DPS parser receives).</summary>
    public void Scan(byte[] packet, long at) => _parser.Feed(packet, at);

    /// <summary>Build and store the recording for a just-logged battle. Wire to
    /// <c>DpsCalculator.OnBattleLogged</c>.</summary>
    /// <param name="partyMembers">When non-null and non-empty, only these (nickname, server) identities —
    /// the party/raid roster — plus self and the boss are included; all other combat contributors (e.g.
    /// random players on a shared field boss) are dropped. Null = include every contributor (CLI/tests).</param>
    public ReplayRecording OnBattleLogged(DpsLog log, IReadOnlyCollection<(string Nickname, int Server)>? partyMembers = null)
    {
        DpsReport report = log.Report;
        long start = report.BattleStart;
        long end = report.BattleEnd >= start ? report.BattleEnd : start;
        int? targetUid = report.Target?.Id;

        HashSet<(string, int)>? party = partyMembers is { Count: > 0 }
            ? new HashSet<(string, int)>(partyMembers)
            : null;

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

        // Supports who moved but dealt no damage: add if they resolve to a named user. Scoped to the party
        // roster when one is given (so non-party movers on a shared boss are not added).
        if (_extraIdentity is { } src)
        {
            foreach (int uid in _buffer.Keys)
            {
                if (included.Contains(uid) || uid == targetUid)
                {
                    continue;
                }

                ReplayIdentity id = src.Resolve(uid);
                if (id.Found && !string.IsNullOrEmpty(id.Nickname)
                    && (party == null || party.Contains((id.Nickname!, id.Server))))
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

    /// <summary>Clear all buffered movement + stored recordings (wire to the meter reset / flush).</summary>
    public void Reset()
    {
        _buffer.Clear();
        _byBattleStart.Clear();
        LastRecording = null;
        _latestAt = 0;
        _sinceTrim = 0;
    }

    private IEnumerable<MovementSample> Samples(int uid)
        => _buffer.TryGetValue(uid, out List<MovementSample>? list) ? list : Enumerable.Empty<MovementSample>();

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
