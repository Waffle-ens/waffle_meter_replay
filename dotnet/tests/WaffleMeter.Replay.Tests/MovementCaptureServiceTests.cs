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
