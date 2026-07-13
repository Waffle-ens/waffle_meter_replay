using WaffleMeter.Replay;
using Xunit;

namespace WaffleMeter.Replay.Tests;

/// <summary>
/// Spec for the boss-mechanic decode (0x3802 casts + 0x8D00 remaining HP). The fixtures are REAL frames
/// lifted byte-for-byte from a captured 붉은 연심의 거울 run — the layout was RE'd against those fights
/// (docs/replay-feature-plan.md §13.1), so a regression here means the mechanics stop rendering.
/// </summary>
public class CastParserTests
{
    private static MovementParser Parser(List<CastSample> casts, List<(int Id, long At, long Hp)>? hp = null)
        => new(_ => { }, casts.Add, (id, at, v) => hp?.Add((id, at, v)));

    // Framing: [length var-int][opcode lo][opcode hi][body…] — the length value is irrelevant to the tap.
    private static byte[] Frame(int opcode, string bodyHex)
    {
        var p = new List<byte> { 0x10, (byte)(opcode & 0xFF), (byte)((opcode >> 8) & 0xFF) };
        p.AddRange(Convert.FromHexString(bodyHex));
        return p.ToArray();
    }

    // 로타르 (uid 36306) casting 1608040 on the player uid 3482, who stood at (34359, 8665, 8308).
    private const string PlayerTargetedCast =
        "D29B02" +                       // actor var-int 36306
        "00688918003402" +               // flag, u32 skill 1608040, counter, byte
        "9A1B" +                         // target var-int 3482 (a PLAYER — this is a marked cast)
        "AC4FB442" +                     // f32 facing 90.155°
        "6E370647FF63074600D00146" +     // f32 X 34359.43, Y 8665.0 (the marked player's feet), Z 8308.0
        "904E01E807";                    // trailing fields (ignored)

    // 크로메데의 심연 (uid 17878) casting 1807051 on ITSELF — a self-centred mechanic; the XYZ is the
    // boss's own live position.
    private const string SelfCast =
        "D68B01" +                       // actor var-int 17878
        "00CB921B003702" +               // flag, u32 skill 1807051, counter, byte
        "D68B01" +                       // target var-int 17878 == the caster
        "0BF22B43" +                     // f32 facing 171.945°
        "F26895C6B09C0B4600B0BE45" +     // f32 X -19124.47, Y 8935.17, Z 6102.0 (the boss's position)
        "904E01E05D";

    [Fact]
    public void Decodes_a_player_targeted_cast_with_the_marked_players_feet()
    {
        var casts = new List<CastSample>();
        Parser(casts).Feed(Frame(0x3802, PlayerTargetedCast), arrivedAt: 1_000);

        CastSample c = Assert.Single(casts);
        Assert.Equal(36306, c.ActorId);       // the boss
        Assert.Equal(1608040, c.SkillCode);   // the key into the client's zone catalog
        Assert.Equal(1_000, c.AtMs);
        Assert.Equal(90.2f, c.FacingDeg, 1);  // the boss's facing at cast (degrees)

        CastTarget marked = Assert.Single(c.Targets);
        Assert.Equal(3482, marked.EntityId);  // a party member, not the boss => the zone sits on them
        Assert.Equal(34359f, marked.X, 0);
        Assert.Equal(8665f, marked.Y, 0);
        Assert.Equal(8308f, marked.Z, 0);
    }

    [Fact]
    public void Decodes_a_self_centred_cast_anchored_on_the_boss()
    {
        var casts = new List<CastSample>();
        Parser(casts).Feed(Frame(0x3802, SelfCast), arrivedAt: 2_000);

        CastSample c = Assert.Single(casts);
        Assert.Equal(1807051, c.SkillCode);
        Assert.Equal(171.9f, c.FacingDeg, 1);

        CastTarget anchor = Assert.Single(c.Targets);
        Assert.Equal(c.ActorId, anchor.EntityId); // anchored on the caster itself
        Assert.Equal(-19124f, anchor.X, 0);       // the boss's live position, straight from the cast
        Assert.Equal(8935f, anchor.Y, 0);
    }

    [Fact]
    public void Decodes_a_multi_target_spread_naming_every_marked_player()
    {
        // 로타르's 1608090 (Circle r=400, 2 s telegraph): the mechanic drops one circle on EVERY marked
        // player, and the frame names all of them with coordinates. Real 30-byte-header + target-list frame.
        const string spread =
            "D29B02" + "209A8918006502" + "CB5F" +      // actor 36306, flag 0x20 (long), skill 1608090, primary 12235
            "E7512B43" +                                 // facing 171.32°
            "68680547C59A044600D00146" +                 // the primary's feet
            "904E010005FD2A" +                           // interleaved non-target fields (must be skipped)
            "F891044785D9E74591DA0146" +
            "F341771F07475342024600D00146" +             // then the marked-player list: uid + feet …
            "D5566DA60547DF3BFD4500D00146" +
            "CB5F68680547C59A044600D00146" +             // (the primary appears again — one zone per player)
            "F16496BC0647661A1B4691DA0146";

        var casts = new List<CastSample>();
        Parser(casts).Feed(Frame(0x3802, spread), arrivedAt: 3_000);

        CastSample c = Assert.Single(casts);
        Assert.Equal(1608090, c.SkillCode);
        Assert.Equal(12235, c.Targets[0].EntityId); // the primary comes first

        // Five distinct players marked, each with their own feet — all clustered in the fight area.
        Assert.Equal(5, c.Targets.Count);
        Assert.Equal(c.Targets.Select(t => t.EntityId).Distinct().Count(), c.Targets.Count);
        Assert.Contains(c.Targets, t => t.EntityId == 5501);
        Assert.Contains(c.Targets, t => t.EntityId == 8435);
        Assert.Contains(c.Targets, t => t.EntityId == 11093);
        Assert.Contains(c.Targets, t => t.EntityId == 12913);
        Assert.All(c.Targets, t => Assert.InRange(t.X, 33_000f, 35_000f));
        Assert.All(c.Targets, t => Assert.InRange(t.Y, 7_000f, 10_000f));
    }

    [Fact]
    public void Ignores_a_frame_whose_fields_do_not_line_up()
    {
        var casts = new List<CastSample>();
        MovementParser p = Parser(casts);

        p.Feed(Frame(0x3802, "D29B02" + "00688918003402" + "9A1B" + "0000FA44" + "01020304"), arrivedAt: 1); // truncated
        p.Feed(Frame(0x3802, "D29B02" + "00688918003402" + "9A1B" + "0000C87F" + "6E37064700D0014600D00146904E01"), arrivedAt: 2); // facing = 1e38, not degrees

        Assert.Empty(casts); // a misframed packet must yield nothing, never a bogus mechanic
    }

    [Fact]
    public void Decodes_the_bosses_remaining_hp()
    {
        var casts = new List<CastSample>();
        var hp = new List<(int Id, long At, long Hp)>();

        // 0x8D00: [mob var-int][3 skipped var-ints][u32 LE remaining hp]
        Parser(casts, hp).Feed(Frame(0x8D00, "D29B02" + "01" + "02" + "03" + "40E20100"), arrivedAt: 5_000);

        (int id, long at, long value) = Assert.Single(hp);
        Assert.Equal(36306, id);
        Assert.Equal(5_000, at);
        Assert.Equal(123_456, value);
    }
}
