using WaffleMeter.Capture;
using WaffleMeter.Data;
using WaffleMeter.Replay;
using Xunit;

namespace WaffleMeter.Replay.Tests;

/// <summary>Spec for the live <see cref="MovementCaptureService"/>: rolling-buffer slice on battle-logged,
/// identity from the frozen report contributors, and the kill-vs-wipe (직전 전투) flag.</summary>
public class MovementCaptureServiceTests
{
    private static byte[] Varint(int v)
    {
        var bytes = new List<byte>();
        uint u = (uint)v;
        do
        {
            byte b = (byte)(u & 0x7F);
            u >>= 7;
            if (u != 0)
            {
                b |= 0x80;
            }

            bytes.Add(b);
        }
        while (u != 0);

        return bytes.ToArray();
    }

    private static byte[] PositionPacket(int opcode, int entityId, float x, float y, float z)
    {
        var p = new List<byte> { 0x10, (byte)(opcode & 0xFF), (byte)((opcode >> 8) & 0xFF) };
        p.AddRange(Varint(entityId));
        p.AddRange(BitConverter.GetBytes(x));
        p.AddRange(BitConverter.GetBytes(y));
        p.AddRange(BitConverter.GetBytes(z));
        return p.ToArray();
    }

    private static User Contributor(int id, string nick, JobClass? job = null, bool exec = false)
    {
        var u = new User(id) { Nickname = nick, Server = 2003, IsExecutor = exec };
        if (job is { } j)
        {
            u.Job = j;
        }

        return u;
    }

    [Fact]
    public void Builds_a_recording_from_buffered_movement_on_battle_log()
    {
        var svc = new MovementCaptureService();

        // movement for two players inside the (eventual) battle window
        svc.Scan(PositionPacket(0x371C, 100, 1000f, 2000f, 50f), at: 1000);
        svc.Scan(PositionPacket(0x371C, 100, 1010f, 2000f, 50f), at: 1200);
        svc.Scan(PositionPacket(0x371C, 200, -500f, 600f, 70f), at: 1100);

        var report = new DpsReport
        {
            BattleStart = 900,
            BattleEnd = 1500,
            ExecutorId = 100,
            Contributors = [Contributor(100, "나", exec: true), Contributor(200, "동료")],
            Target = new MobInfo(999, new Mob(2301059, "보스", true), remainHp: 0, maxHp: 1000),
        };
        ReplayRecording rec = svc.OnBattleLogged(new DpsLog { Report = report });

        Assert.Equal(900, rec.StartMs);
        Assert.True(rec.BossDefeated); // RemainHp 0 of MaxHp 1000
        Assert.Equal("보스", rec.TargetName);

        ReplayTrack me = Assert.Single(rec.Tracks, t => t.Uid == 100);
        Assert.True(me.IsSelf);
        Assert.Equal("나", me.Nickname);
        Assert.Equal(2, me.Points.Count);
        Assert.Equal(100, me.Points[0].TMs); // 1000 - 900

        ReplayTrack ally = Assert.Single(rec.Tracks, t => t.Uid == 200);
        Assert.Single(ally.Points);
        Assert.Same(rec, svc.LastRecording);
    }

    [Fact]
    public void Marks_a_wipe_as_not_defeated_the_jikjeon_battle()
    {
        var svc = new MovementCaptureService();
        svc.Scan(PositionPacket(0x371C, 100, 1000f, 2000f, 50f), at: 1000);

        var report = new DpsReport
        {
            BattleStart = 900,
            BattleEnd = 1500,
            Contributors = [Contributor(100, "나")],
            Target = new MobInfo(999, new Mob(2301059, "보스", true), remainHp: 450, maxHp: 1000), // survived
        };
        ReplayRecording rec = svc.OnBattleLogged(new DpsLog { Report = report });

        Assert.False(rec.BossDefeated);
        Assert.NotNull(svc.LastRecording);
    }

    [Fact]
    public void Excludes_movement_outside_the_battle_window()
    {
        var svc = new MovementCaptureService();
        svc.Scan(PositionPacket(0x371C, 100, 1000f, 2000f, 50f), at: 100);  // long before
        svc.Scan(PositionPacket(0x371C, 100, 1010f, 2000f, 50f), at: 1200); // inside

        var report = new DpsReport
        {
            BattleStart = 1000,
            BattleEnd = 1500,
            Contributors = [Contributor(100, "나")],
        };
        ReplayRecording rec = svc.OnBattleLogged(new DpsLog { Report = report });

        Assert.Equal(1, Assert.Single(rec.Tracks).Points.Count);
    }

    [Fact]
    public void Party_filter_keeps_self_and_roster_members_drops_others()
    {
        var svc = new MovementCaptureService();
        svc.Scan(PositionPacket(0x371C, 100, 1000f, 2000f, 50f), at: 1000); // self
        svc.Scan(PositionPacket(0x371C, 200, -500f, 600f, 70f), at: 1100);  // party ally

        var report = new DpsReport
        {
            BattleStart = 900,
            BattleEnd = 1500,
            ExecutorId = 100,
            Contributors = [Contributor(100, "나", exec: true), Contributor(200, "동료"), Contributor(300, "막공인")],
        };

        // roster lists only the ally (server 2003); self is kept by the executor rule, 막공인 is not in the party.
        ReplayRecording rec = svc.OnBattleLogged(new DpsLog { Report = report }, new[] { ("동료", 2003) });

        Assert.Contains(rec.Tracks, t => t.Uid == 100 && t.IsSelf);
        Assert.Contains(rec.Tracks, t => t.Uid == 200);
        Assert.DoesNotContain(rec.Tracks, t => t.Uid == 300); // non-party field-boss contributor dropped
    }

    [Fact]
    public void No_party_filter_includes_all_contributors()
    {
        var svc = new MovementCaptureService();
        var report = new DpsReport
        {
            BattleStart = 900,
            BattleEnd = 1500,
            Contributors = [Contributor(100, "나"), Contributor(300, "막공인")],
        };

        ReplayRecording rec = svc.OnBattleLogged(new DpsLog { Report = report }); // partyMembers = null

        Assert.Contains(rec.Tracks, t => t.Uid == 100);
        Assert.Contains(rec.Tracks, t => t.Uid == 300);
    }

    [Fact]
    public void Empty_roster_keeps_only_self_and_boss()
    {
        // Not in a party (or the 0x9702 roster was never seen): a solo player at a shared field boss.
        // The recording must show self + the boss ONLY — never the random contributors around them.
        var svc = new MovementCaptureService();
        svc.Scan(PositionPacket(0x371C, 100, 1000f, 2000f, 50f), at: 1000); // self
        svc.Scan(PositionPacket(0x371C, 300, -500f, 600f, 70f), at: 1100);  // random player
        svc.Scan(PositionPacket(0x371C, 999, 1200f, 1900f, 55f), at: 1200); // boss

        var report = new DpsReport
        {
            BattleStart = 900,
            BattleEnd = 1500,
            ExecutorId = 100,
            Contributors = [Contributor(100, "나", exec: true), Contributor(300, "막공인")],
            Target = new MobInfo(999, new Mob(2600089, "필드보스", true), remainHp: 0, maxHp: 1000),
        };

        ReplayRecording rec = svc.OnBattleLogged(new DpsLog { Report = report }, Array.Empty<(string, int)>());

        Assert.Contains(rec.Tracks, t => t.Uid == 100 && t.IsSelf);
        Assert.Contains(rec.Tracks, t => t.Uid == 999 && t.IsTarget);
        Assert.DoesNotContain(rec.Tracks, t => t.Uid == 300);
        Assert.Equal(2, rec.Tracks.Count);
    }

    // 0x3802: [len][op lo][op hi][actor v][flag][u32 skill][ctr][b][target v][f32 facing][f32 X][f32 Y][f32 Z]
    private static byte[] CastPacket(int actorId, int skill, int targetId, float facing, float x, float y, float z)
    {
        var p = new List<byte> { 0x10, 0x02, 0x38 };
        p.AddRange(Varint(actorId));
        p.Add(0x00);
        p.AddRange(BitConverter.GetBytes(skill));
        p.Add(0x00);
        p.Add(0x02);
        p.AddRange(Varint(targetId));
        p.AddRange(BitConverter.GetBytes(facing));
        p.AddRange(BitConverter.GetBytes(x));
        p.AddRange(BitConverter.GetBytes(y));
        p.AddRange(BitConverter.GetBytes(z));
        return p.ToArray();
    }

    // 0x8D00: [mob v][v][v][v][u32 remaining hp]
    private static byte[] RemainHpPacket(int mobId, int hp)
    {
        var p = new List<byte> { 0x10, 0x00, 0x8D };
        p.AddRange(Varint(mobId));
        p.AddRange([(byte)0x01, (byte)0x02, (byte)0x03]);
        p.AddRange(BitConverter.GetBytes(hp));
        return p.ToArray();
    }

    [Fact]
    public void Records_the_bosses_mechanics_with_the_hp_they_fired_at()
    {
        var svc = new MovementCaptureService();

        svc.Scan(RemainHpPacket(999, 800), at: 1_000);                                   // boss at 80 %
        svc.Scan(CastPacket(999, 1806450, 999, -117.5f, 5000f, 6000f, 50f), at: 1_100);  // a line, self-anchored
        svc.Scan(RemainHpPacket(999, 300), at: 2_000);                                   // boss down to 30 %
        svc.Scan(CastPacket(999, 1807111, 100, 12f, 1000f, 2000f, 50f), at: 2_100);      // a marker on the player
        svc.Scan(CastPacket(100, 17400058, 999, 0f, 1000f, 2000f, 50f), at: 2_200);      // a PLAYER's skill

        var report = new DpsReport
        {
            BattleStart = 900,
            BattleEnd = 3_000,
            ExecutorId = 100,
            Contributors = [Contributor(100, "나", exec: true)],
            Target = new MobInfo(999, new Mob(2300334, "로타르", true), remainHp: 0, maxHp: 1000),
        };
        ReplayRecording rec = svc.OnBattleLogged(new DpsLog { Report = report }, new[] { ("나", 2003) });

        Assert.Equal(2, rec.Casts.Count); // the player's own skill is not a mechanic

        ReplayCast line = rec.Casts[0];
        Assert.Equal(200, line.TMs); // 1100 - 900, relative to the battle start
        Assert.Equal(1806450, line.SkillCode);
        Assert.Equal(-117.5f, line.FacingDeg); // rotates the line on the map
        Assert.Equal(0.8f, line.HpFraction, 3); // the HP it fired at — NOT the battle's end state
        Assert.Equal(999, Assert.Single(line.Targets).Uid); // anchored on the boss itself

        ReplayCast mark = rec.Casts[1];
        Assert.Equal(1807111, mark.SkillCode);
        Assert.Equal(0.3f, mark.HpFraction, 3);
        CastTargetIsPlayer(mark, uid: 100, x: 1000f, y: 2000f);
    }

    private static void CastTargetIsPlayer(ReplayCast cast, int uid, float x, float y)
    {
        ReplayCastTarget t = Assert.Single(cast.Targets);
        Assert.Equal(uid, t.Uid);
        Assert.Equal(x, t.X);
        Assert.Equal(y, t.Y);
    }

    [Fact]
    public void Mechanics_outside_the_battle_window_are_dropped_and_a_reset_clears_them()
    {
        var svc = new MovementCaptureService();
        svc.Scan(CastPacket(999, 1806450, 999, 0f, 5000f, 6000f, 50f), at: 100);   // long before the pull
        svc.Scan(CastPacket(999, 1806450, 999, 0f, 5000f, 6000f, 50f), at: 1_200); // inside

        var report = new DpsReport
        {
            BattleStart = 1_000,
            BattleEnd = 1_500,
            Contributors = [Contributor(100, "나", exec: true)],
            Target = new MobInfo(999, new Mob(2300334, "로타르", true), remainHp: 5, maxHp: 1000),
        };

        Assert.Single(svc.OnBattleLogged(new DpsLog { Report = report }).Casts);

        svc.Reset();
        Assert.Empty(svc.OnBattleLogged(new DpsLog { Report = report }).Casts);
    }

    [Fact]
    public void Hp_is_unknown_rather_than_wrong_when_the_boss_never_broadcast_it()
    {
        var svc = new MovementCaptureService();
        svc.Scan(CastPacket(999, 1806450, 999, 0f, 5000f, 6000f, 50f), at: 1_200);

        ReplayRecording rec = svc.OnBattleLogged(new DpsLog
        {
            Report = new DpsReport
            {
                BattleStart = 1_000,
                BattleEnd = 1_500,
                Contributors = [Contributor(100, "나", exec: true)],
                Target = new MobInfo(999, new Mob(2300334, "로타르", true), remainHp: 0, maxHp: 1000),
            },
        });

        Assert.Equal(-1f, Assert.Single(rec.Casts).HpFraction); // -1 = unknown, never a fabricated 0 %
    }

    [Fact]
    public void Hp_falls_back_to_the_peak_the_boss_broadcast_when_the_report_lacks_a_max()
    {
        // A fight the capture joined without ever learning the boss's max HP (report MaxHp = 0). The
        // fractions must still be meaningful — measured against the highest HP the boss broadcast.
        var svc = new MovementCaptureService();
        svc.Scan(RemainHpPacket(999, 900), at: 1_100);
        svc.Scan(CastPacket(999, 1806450, 999, 0f, 5000f, 6000f, 50f), at: 1_150);
        svc.Scan(RemainHpPacket(999, 450), at: 1_300);
        svc.Scan(CastPacket(999, 1806450, 999, 0f, 5000f, 6000f, 50f), at: 1_350);

        ReplayRecording rec = svc.OnBattleLogged(new DpsLog
        {
            Report = new DpsReport
            {
                BattleStart = 1_000,
                BattleEnd = 1_500,
                Contributors = [Contributor(100, "나", exec: true)],
                Target = new MobInfo(999, new Mob(2300334, "로타르", true), remainHp: 450, maxHp: 0),
            },
        });

        Assert.Equal(1f, rec.Casts[0].HpFraction);   // the peak seen in the fight
        Assert.Equal(0.5f, rec.Casts[1].HpFraction); // half of it
    }

    [Fact]
    public void Lookup_by_battle_start_and_reset()
    {
        var svc = new MovementCaptureService();
        svc.Scan(PositionPacket(0x371C, 100, 1000f, 2000f, 50f), at: 1000);
        svc.OnBattleLogged(new DpsLog
        {
            Report = new DpsReport { BattleStart = 900, BattleEnd = 1500, Contributors = [Contributor(100, "나")] },
        });

        Assert.True(svc.TryGetForBattle(900, out ReplayRecording? found));
        Assert.NotNull(found);

        svc.Reset();
        Assert.Null(svc.LastRecording);
        Assert.False(svc.TryGetForBattle(900, out _));
    }
}
