namespace WaffleMeter.Replay;

/// <summary>A combat participant the recording must include (even with no captured movement).</summary>
/// <param name="Uid">Entity uid.</param>
/// <param name="PartySlot">8-인 공대 slot 1-8, else 0.</param>
public readonly record struct ReplayParticipant(int Uid, int PartySlot);

/// <summary>Tunables for buffering bounds (guards a runaway long fight / flooded stream).</summary>
public sealed class ReplayRecorderOptions
{
    /// <summary>Max distinct entities buffered in one battle.</summary>
    public int MaxEntities { get; init; } = 256;

    /// <summary>Max samples per entity before uniform decimation halves the path.</summary>
    public int MaxSamplesPerEntity { get; init; } = 12_000;

    /// <summary>Hard cap on battle duration buffered (ms); samples past it are ignored.</summary>
    public long MaxBattleMs { get; init; } = 30 * 60 * 1000L;

    /// <summary>Include buffered movers that resolve to a named user but weren't in the participant list
    /// (catches support players who moved but dealt no damage). Integration narrows this with the roster.</summary>
    public bool IncludeUnlistedNamedMovers { get; init; } = true;
}

/// <summary>
/// Buffers <see cref="MovementSample"/>s during a battle and, on battle end, finalizes a
/// <see cref="ReplayRecording"/> for every participant — supporting both an ENDED (cleared) battle and
/// the standby "직전 전투" (stopped without a kill), distinguished only by <c>bossDefeated</c>.
/// <para>
/// Per-entity layout cleanup: an entity's samples are grouped by (opcode, offset) and only the dominant
/// group is kept, so an offset-by-one false positive on a stray opcode can't smear a path. Identity is
/// resolved at finalize time (not while buffering) so late-arriving nickname packets are reflected, and
/// so entity-id reuse is bounded to the battle window.
/// </para>
/// Thread-affinity: all methods are expected on the single capture-consumer thread, matching the meter's
/// pipeline; no internal locking.
/// </summary>
public sealed class MovementRecorder(IReplayIdentitySource identity, ReplayRecorderOptions? options = null)
{
    private readonly ReplayRecorderOptions _opt = options ?? new ReplayRecorderOptions();
    private readonly Dictionary<int, List<MovementSample>> _buffer = new();

    private bool _active;
    private long _epoch;
    private long _startMs;
    private int? _targetCode;
    private string? _targetName;

    /// <summary>The most recently finalized recording = the 직전 전투. Null until the first battle ends.</summary>
    public ReplayRecording? LastRecording { get; private set; }

    public bool IsRecording => _active;

    /// <summary>Begin a battle window. Auto-finalizes a still-open previous battle (superseded re-pull).</summary>
    public void BeginBattle(long epoch, long atMs, int? targetCode, string? targetName)
    {
        if (_active)
        {
            // a new pull began without an explicit end — finalize what we have as the 직전 전투
            LastRecording = Finalize(false, atMs, Array.Empty<ReplayParticipant>(), null);
        }

        _buffer.Clear();
        _active = true;
        _epoch = epoch;
        _startMs = atMs;
        _targetCode = targetCode;
        _targetName = targetName;
    }

    /// <summary>Buffer one decoded sample if a battle is active and it falls inside the window.</summary>
    public void Observe(in MovementSample s)
    {
        if (!_active)
        {
            return;
        }

        if (s.AtMs < _startMs || s.AtMs - _startMs > _opt.MaxBattleMs)
        {
            return;
        }

        if (!_buffer.TryGetValue(s.EntityId, out List<MovementSample>? list))
        {
            if (_buffer.Count >= _opt.MaxEntities)
            {
                return;
            }

            list = new List<MovementSample>();
            _buffer[s.EntityId] = list;
        }

        if (list.Count >= _opt.MaxSamplesPerEntity)
        {
            Decimate(list); // halve resolution, keep full span
        }

        list.Add(s);
    }

    /// <summary>End the battle and store the finalized recording as <see cref="LastRecording"/>.</summary>
    /// <param name="bossDefeated">True = cleared/ended; false = wipe/stop (still a valid 직전 전투).</param>
    public ReplayRecording EndBattle(
        long atMs,
        bool bossDefeated,
        IReadOnlyCollection<ReplayParticipant> participants,
        int? targetUid = null)
    {
        ReplayRecording rec = Finalize(bossDefeated, atMs, participants, targetUid);
        LastRecording = rec;
        _active = false;
        _buffer.Clear();
        return rec;
    }

    /// <summary>Snapshot the in-progress battle without ending it (live replay of the current fight).</summary>
    public ReplayRecording? SnapshotCurrent(long atMs, IReadOnlyCollection<ReplayParticipant> participants, int? targetUid = null)
        => _active ? Finalize(false, atMs, participants, targetUid) : null;

    private ReplayRecording Finalize(
        bool bossDefeated,
        long endMs,
        IReadOnlyCollection<ReplayParticipant> participants,
        int? targetUid)
    {
        var slotByUid = new Dictionary<int, int>();
        foreach (ReplayParticipant p in participants)
        {
            slotByUid[p.Uid] = p.PartySlot;
        }

        var include = new HashSet<int>(slotByUid.Keys);
        int self = identity.SelfUid;
        if (self != 0)
        {
            include.Add(self);
        }

        if (_opt.IncludeUnlistedNamedMovers)
        {
            foreach (int uid in _buffer.Keys)
            {
                if (!include.Contains(uid) && uid != targetUid && identity.Resolve(uid).Found)
                {
                    include.Add(uid);
                }
            }
        }

        var tracks = new List<ReplayTrack>();
        foreach (int uid in include)
        {
            tracks.Add(BuildTrack(uid, isTarget: false, slotByUid.GetValueOrDefault(uid)));
        }

        if (targetUid is { } tuid && _buffer.ContainsKey(tuid))
        {
            tracks.Add(BuildTrack(tuid, isTarget: true, 0));
        }

        return new ReplayRecording
        {
            BattleEpoch = _epoch,
            StartMs = _startMs,
            EndMs = endMs,
            BossDefeated = bossDefeated,
            TargetCode = _targetCode,
            TargetName = _targetName,
            Tracks = tracks,
        };
    }

    private ReplayTrack BuildTrack(int uid, bool isTarget, int partySlot)
    {
        IEnumerable<MovementSample> samples = _buffer.GetValueOrDefault(uid) ?? Enumerable.Empty<MovementSample>();
        // _buffer is already window-bounded at Observe time, so slice with an open upper bound.
        return ReplayTrackBuilder.BuildTrack(uid, samples, _startMs, long.MaxValue, identity.Resolve(uid), isTarget, partySlot);
    }

    private static void Decimate(List<MovementSample> list)
    {
        int w = 0;
        for (int r = 0; r < list.Count; r += 2)
        {
            list[w++] = list[r];
        }

        list.RemoveRange(w, list.Count - w);
    }
}
