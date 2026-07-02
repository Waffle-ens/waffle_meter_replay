using WaffleMeter.Replay;
using Xunit;

namespace WaffleMeter.Replay.Tests;

/// <summary>Spec for <see cref="MovementRecorder"/>: battle-window scoping, per-entity dominant-layout
/// cleanup, participant inclusion (incl. empty tracks), self/target tracks, and the standby 직전 전투.</summary>
public class MovementRecorderTests
{
    private sealed class FakeIdentity : IReplayIdentitySource
    {
        public int SelfUid { get; set; }
        public readonly Dictionary<int, ReplayIdentity> Map = new();

        public ReplayIdentity Resolve(int uid) =>
            Map.TryGetValue(uid, out ReplayIdentity v) ? v : new ReplayIdentity(false, null, 0, null, uid != 0 && uid == SelfUid);
    }

    private static MovementSample Sample(int id, long at, float x, float y, float z, int opcode = 0x371C, int offset = 2)
        => new(id, at, x, y, z, opcode, offset);

    private static MovementSample Delta(int id, long at, float dx, float dy, float dz = 0)
        => new(id, at, dx, dy, dz, 0x371D, 0, MovementKind.Delta);

    [Fact]
    public void Buffers_only_samples_inside_the_battle_window()
    {
        var rec = new MovementRecorder(new FakeIdentity());
        rec.BeginBattle(epoch: 1, atMs: 1000, targetCode: null, targetName: null);

        rec.Observe(Sample(100, 500, 1f, 1f, 1f));   // before start -> dropped
        rec.Observe(Sample(100, 1500, 10f, 20f, 30f));
        rec.Observe(Sample(100, 2000, 11f, 21f, 31f));

        ReplayRecording r = rec.EndBattle(3000, bossDefeated: true, [new ReplayParticipant(100, 0)]);

        ReplayTrack t = Assert.Single(r.Tracks, x => x.Uid == 100);
        Assert.Equal(2, t.Points.Count);
        Assert.Equal(500, t.Points[0].TMs); // 1500 - 1000
        Assert.Equal(1000, t.Points[1].TMs);
    }

    [Fact]
    public void Keeps_only_the_dominant_opcode_offset_layout_per_entity()
    {
        var rec = new MovementRecorder(new FakeIdentity());
        rec.BeginBattle(1, 0, null, null);

        rec.Observe(Sample(100, 10, 100f, 100f, 100f, opcode: 0x371C, offset: 2));
        rec.Observe(Sample(100, 20, 101f, 100f, 100f, opcode: 0x371C, offset: 2));
        rec.Observe(Sample(100, 30, 102f, 100f, 100f, opcode: 0x371C, offset: 2));
        rec.Observe(Sample(100, 25, 9999f, 9999f, 9999f, opcode: 0x371A, offset: 5)); // stray minority layout

        ReplayRecording r = rec.EndBattle(100, false, [new ReplayParticipant(100, 0)]);

        ReplayTrack t = Assert.Single(r.Tracks);
        Assert.Equal(0x371C, t.SourceOpcode);
        Assert.Equal(2, t.SourceOffset);
        Assert.Equal(3, t.Points.Count); // the stray 0x371A sample is discarded
        Assert.DoesNotContain(t.Points, p => p.X > 9000);
    }

    [Fact]
    public void Participant_with_no_movement_still_gets_an_empty_track()
    {
        var rec = new MovementRecorder(new FakeIdentity());
        rec.BeginBattle(1, 0, null, null);
        rec.Observe(Sample(100, 10, 50f, 60f, 70f));

        ReplayRecording r = rec.EndBattle(100, true, [new ReplayParticipant(100, 1), new ReplayParticipant(999, 2)]);

        ReplayTrack missing = Assert.Single(r.Tracks, t => t.Uid == 999);
        Assert.Empty(missing.Points);
        Assert.Equal(2, missing.PartySlot);
    }

    [Fact]
    public void Includes_self_and_marks_it()
    {
        var id = new FakeIdentity { SelfUid = 7 };
        id.Map[7] = new ReplayIdentity(true, "나", 2003, "검성", IsSelf: true);
        var rec = new MovementRecorder(id);

        rec.BeginBattle(1, 0, null, null);
        rec.Observe(Sample(7, 10, 100f, 100f, 100f));
        ReplayRecording r = rec.EndBattle(100, true, []); // no explicit participants

        ReplayTrack self = Assert.Single(r.Tracks, t => t.Uid == 7);
        Assert.True(self.IsSelf);
        Assert.Equal("나", self.Nickname);
        Assert.Single(self.Points);
    }

    [Fact]
    public void Builds_a_target_track_when_target_uid_has_movement()
    {
        var rec = new MovementRecorder(new FakeIdentity());
        rec.BeginBattle(1, 0, null, "보스");
        rec.Observe(Sample(500, 10, 100f, 100f, 100f));

        ReplayRecording r = rec.EndBattle(100, true, [], targetUid: 500);

        ReplayTrack boss = Assert.Single(r.Tracks, t => t.IsTarget);
        Assert.Equal(500, boss.Uid);
        Assert.Single(boss.Points);
    }

    [Fact]
    public void Last_recording_is_the_standby_jikjeon_battle_even_without_a_kill()
    {
        var rec = new MovementRecorder(new FakeIdentity());
        Assert.Null(rec.LastRecording);

        rec.BeginBattle(1, 0, null, null);
        rec.Observe(Sample(100, 10, 100f, 100f, 100f));
        rec.EndBattle(100, bossDefeated: false, [new ReplayParticipant(100, 0)]);

        Assert.NotNull(rec.LastRecording);
        Assert.False(rec.LastRecording!.BossDefeated);
        Assert.False(rec.IsRecording);
    }

    [Fact]
    public void Reconstructs_a_dense_path_by_integrating_deltas_between_keyframes()
    {
        var rec = new MovementRecorder(new FakeIdentity());
        rec.BeginBattle(1, 0, null, null);

        // two absolute keyframes 1s apart; a 10Hz +10-X delta stream that sums exactly to the +100 gap
        rec.Observe(Sample(100, 1000, 0f, 0f, 0f));
        for (long t = 1100; t <= 2000; t += 100)
        {
            rec.Observe(Delta(100, t, 10f, 0f));
        }

        rec.Observe(Sample(100, 2000, 100f, 0f, 0f));

        ReplayTrack t100 = Assert.Single(rec.EndBattle(3000, false, [new ReplayParticipant(100, 0)]).Tracks);

        // dense: a point per delta + the keyframes (~11), not just the 2 keyframes
        Assert.True(t100.Points.Count >= 10, $"expected a dense path, got {t100.Points.Count} points");
        // monotonic along X from ~0 to exactly 100 (endpoints pinned to the keyframes)
        Assert.Equal(0f, t100.Points[0].X, 3);
        Assert.Equal(100f, t100.Points[^1].X, 3);
        for (int i = 1; i < t100.Points.Count; i++)
        {
            Assert.True(t100.Points[i].X >= t100.Points[i - 1].X - 0.01f, "X should be non-decreasing");
            Assert.Equal(0f, t100.Points[i].Y, 3);
        }
    }

    [Fact]
    public void Affine_pins_endpoints_to_keyframes_even_when_delta_scale_is_off()
    {
        var rec = new MovementRecorder(new FakeIdentity());
        rec.BeginBattle(1, 0, null, null);

        // deltas sum to +50 but the keyframes say the entity moved +100 — the affine must rescale so the
        // path still lands exactly on the keyframes (independence from the exact quantization scale).
        rec.Observe(Sample(1, 0, 0f, 0f, 0f));
        for (long t = 100; t <= 1000; t += 100)
        {
            rec.Observe(Delta(1, t, 5f, 0f));
        }

        rec.Observe(Sample(1, 1000, 100f, 0f, 0f));

        ReplayTrack tr = Assert.Single(rec.EndBattle(2000, false, [new ReplayParticipant(1, 0)]).Tracks);
        Assert.Equal(0f, tr.Points[0].X, 2);
        Assert.Equal(100f, tr.Points[^1].X, 2);
        // a mid-run point (t=500, half the cumulative delta) should sit near the half-way world X (50)
        ReplayPoint mid = tr.Points.First(p => p.TMs == 500);
        Assert.Equal(50f, mid.X, 1);
    }

    [Fact]
    public void Reconstructs_an_L_shaped_path_across_two_segments()
    {
        var rec = new MovementRecorder(new FakeIdentity());
        rec.BeginBattle(1, 0, null, null);

        // K0(0,0) -> K1(100,0): +X leg ; K1(100,0) -> K2(100,100): +Y leg
        rec.Observe(Sample(1, 1000, 0f, 0f, 0f));
        for (long t = 1100; t <= 2000; t += 100) rec.Observe(Delta(1, t, 10f, 0f));
        rec.Observe(Sample(1, 2000, 100f, 0f, 0f));
        for (long t = 2100; t <= 3000; t += 100) rec.Observe(Delta(1, t, 0f, 10f));
        rec.Observe(Sample(1, 3000, 100f, 100f, 0f));

        ReplayTrack tr = Assert.Single(rec.EndBattle(4000, false, [new ReplayParticipant(1, 0)]).Tracks);

        // battle start is 0, so TMs == absolute time. Mid of leg 1 (t=1500): moving along X, Y still ~0.
        ReplayPoint a = tr.Points.First(p => p.TMs == 1500);
        Assert.InRange(a.X, 1f, 99f);
        Assert.Equal(0f, a.Y, 2);
        // mid of leg 2 (t=2500): X pinned at ~100, Y climbing
        ReplayPoint b = tr.Points.First(p => p.TMs == 2500);
        Assert.Equal(100f, b.X, 2);
        Assert.InRange(b.Y, 1f, 99f);
    }

    [Fact]
    public void Delta_only_entity_with_no_keyframe_has_no_placeable_path()
    {
        // deltas but no absolute anchor -> we cannot place it in world space, so the path is empty (the
        // track still exists so the participant is listed).
        var rec = new MovementRecorder(new FakeIdentity());
        rec.BeginBattle(1, 0, null, null);
        rec.Observe(Delta(1, 100, 10f, 10f));
        rec.Observe(Delta(1, 200, 10f, 10f));

        ReplayTrack tr = Assert.Single(rec.EndBattle(1000, false, [new ReplayParticipant(1, 0)]).Tracks);
        Assert.Empty(tr.Points);
    }

    [Fact]
    public void A_new_pull_auto_finalizes_the_previous_open_battle()
    {
        // Supersede finalizes with no participant list, so only named movers (and self) survive — mirror
        // the real path by giving the mover an identity.
        var id = new FakeIdentity();
        id.Map[100] = new ReplayIdentity(true, "갑", 2003, "검성", IsSelf: false);
        var rec = new MovementRecorder(id);
        rec.BeginBattle(1, 0, null, null);
        rec.Observe(Sample(100, 10, 100f, 100f, 100f));

        rec.BeginBattle(2, 1000, null, null); // supersede without an explicit end

        Assert.NotNull(rec.LastRecording);
        Assert.Equal(100, Assert.Single(rec.LastRecording!.Tracks).Uid);
        Assert.True(rec.IsRecording); // the new battle is active
    }
}
