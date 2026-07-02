using System.Text;
using WaffleMeter.Capture;
using WaffleMeter.Capture.Corpus;
using WaffleMeter.Data;
using WaffleMeter.Replay;

// Generate per-battle ReplayRecording JSON from a packet-debug corpus, proving the WaffleMeter.Replay
// backend end-to-end: real aligner+assembler+StreamProcessor (identity via 0x3633/0x3645) with a parallel
// MovementParser tap -> MovementRecorder -> ReplaySerializer.
//
// NOTE: battles here are segmented by DAMAGE-ACTIVITY GAPS (catalog-free, so this dev tool needs no
// skills/mobs json). The live app instead drives BeginBattle/EndBattle from the meter's real battle
// lifecycle (0x8D21 + boss HP) — see docs/replay-feature-plan.md. The parser/recorder/serializer under
// test are identical either way.
// Usage: dotnet run --project <ReplayGenCli> -c Release [bare-sep] <corpus.jsonl> [outDir]

Console.OutputEncoding = Encoding.UTF8;
if (args.Length < 1)
{
    Console.Error.WriteLine("usage: <corpus.jsonl> [outDir]");
    return 1;
}

string corpus = args[0];
if (!File.Exists(corpus))
{
    Console.Error.WriteLine($"corpus not found: {corpus}");
    return 1;
}

string outDir = args.Length >= 2 ? args[1] : Path.Combine(Path.GetDirectoryName(Path.GetFullPath(corpus))!, "replays");
Directory.CreateDirectory(outDir);

const long battleGapMs = 15_000;

var dm = new DataManager();
long simNow = 0;
dm.Clock = () => simNow;

var recorder = new MovementRecorder(new DataManagerIdentitySource(dm));
var recordings = new List<ReplayRecording>();
var actors = new HashSet<int>();
var targetCounts = new Dictionary<int, int>();
bool active = false;
long lastDmg = 0;
long now = 0;

void EndCurrent(long at)
{
    if (!active)
    {
        return;
    }

    int? boss = targetCounts.Count > 0 ? targetCounts.OrderByDescending(k => k.Value).First().Key : null;
    ReplayParticipant[] parts = actors.Select(a => new ReplayParticipant(a, 0)).ToArray();
    recordings.Add(recorder.EndBattle(at, bossDefeated: false, parts, boss));
    active = false;
}

var sink = new DamageSink((actor, target) =>
{
    if (!active || now - lastDmg > battleGapMs)
    {
        EndCurrent(lastDmg + 1);
        recorder.BeginBattle(dm.CurrentEpoch(), now, null, null);
        active = true;
        actors.Clear();
        targetCounts.Clear();
    }

    actors.Add(actor);
    targetCounts[target] = targetCounts.GetValueOrDefault(target) + 1;
    lastDmg = now;
});

var processor = new StreamProcessor(sink, dm);
var movement = new MovementParser(s => recorder.Observe(s));

// Per-src-IP aligner+assembler (a direct-connect corpus interleaves several IPs/directions; a single
// shared assembler shreds framing and yields 0 movement). Feed globally in arrival-time order.
var aligners = new Dictionary<string, PacketAlignmenter>();
var assemblers = new Dictionary<string, StreamAssembler>();
void Pump(byte[] packet, long at)
{
    now = at;
    processor.OnPacketReceived(packet, at);
    movement.Feed(packet, at);
}

foreach (CapturedSegment seg in CaptureCorpusReader.ReadCaptures(corpus)
             .OrderBy(s => s.ArrivedAtMs).ThenBy(s => s.Seq))
{
    simNow = seg.ArrivedAtMs;
    if (!aligners.TryGetValue(seg.SrcIp, out PacketAlignmenter? aligner))
    {
        aligner = aligners[seg.SrcIp] = new PacketAlignmenter();
        assemblers[seg.SrcIp] = new StreamAssembler(Pump);
    }

    StreamAssembler assembler = assemblers[seg.SrcIp];
    foreach (AlignedChunk chunk in aligner.Feed(seg.Seq, seg.Payload, seg.ArrivedAtMs))
    {
        assembler.ProcessChunk(chunk.Data, chunk.ArrivedAt);
    }
}

EndCurrent(now);

Console.WriteLine($"=== {Path.GetFileName(corpus)} === streams={aligners.Count}");
Console.WriteLine($"movement: pkts={movement.MovementPackets} keyframes={movement.PositionSamples} deltas={movement.DeltaSamples} bundles={movement.Bundles}");
Console.WriteLine($"battles (damage-gap segmented): {recordings.Count}");
Console.WriteLine();
Console.WriteLine($"  {"#",-3} {"durMs",9} {"tracks",7} {"named",6} {"points",7}  topTrack");

int idx = 0, written = 0;
foreach (ReplayRecording rec in recordings.OrderByDescending(r => r.PointCount))
{
    int named = rec.Tracks.Count(t => !string.IsNullOrEmpty(t.Nickname));
    ReplayTrack? top = rec.Tracks.Where(t => t.Points.Count > 0).OrderByDescending(t => t.Points.Count).FirstOrDefault();
    string topStr = top is null ? "-" : (string.IsNullOrEmpty(top.Nickname) ? $"uid {top.Uid}" : $"{top.Nickname}") + $" ({top.Points.Count}pt)";
    Console.WriteLine($"  {idx,-3} {rec.DurationMs,9} {rec.Tracks.Count,7} {named,6} {rec.PointCount,7}  {topStr}");

    if (rec.PointCount > 0 && written < 5)
    {
        string file = Path.Combine(outDir, $"replay-{Path.GetFileNameWithoutExtension(corpus)}-{idx}.json");
        File.WriteAllText(file, ReplaySerializer.Serialize(rec, indented: true));
        written++;
        if (written == 1)
        {
            Console.WriteLine($"      -> wrote {file}");
            foreach (ReplayTrack t in rec.Tracks.Where(t => t.Points.Count > 0).OrderByDescending(t => t.Points.Count).Take(10))
            {
                string who = string.IsNullOrEmpty(t.Nickname) ? $"uid {t.Uid}" : $"{t.Nickname}({t.Job})";
                (float MinX, float MinY, float MaxX, float MaxY)? b = new ReplayRecording { Tracks = new[] { t } }.Bounds();
                string bbox = b is { } bb ? $"{bb.MaxX - bb.MinX:F0}x{bb.MaxY - bb.MinY:F0}" : "-";
                string tag = t.IsSelf ? " [SELF]" : t.IsTarget ? " [BOSS]" : "";
                Console.WriteLine($"        {who,-24} pts={t.Points.Count,-4} bbox={bbox,-13} src=0x{t.SourceOpcode:X4}+{t.SourceOffset}{tag}");
            }
        }
    }

    idx++;
}

ReplayRecording? best = recordings.OrderByDescending(r => r.PointCount).FirstOrDefault(r => r.PointCount > 0);
if (best != null)
{
    string json = ReplaySerializer.Serialize(best);
    ReplayRecording back = ReplaySerializer.Deserialize(json);
    bool ok = back.PointCount == best.PointCount && back.Tracks.Count == best.Tracks.Count && back.StartMs == best.StartMs;
    Console.WriteLine();
    Console.WriteLine($"serializer round-trip: {(ok ? "OK" : "MISMATCH")} ({json.Length} bytes for {best.PointCount} points)");
}

// Smoothness: per-track p90 gap between consecutive path points, across all tracks that actually moved.
// This is the WCL-smoothness metric — 0x371D deltas should pull it from tens of seconds to sub-second.
var gaps = new List<int>();
int movedTracks = 0;
foreach (ReplayRecording rec in recordings)
{
    foreach (ReplayTrack t in rec.Tracks.Where(t => t.Points.Count >= 3))
    {
        movedTracks++;
        for (int i = 1; i < t.Points.Count; i++)
        {
            gaps.Add(t.Points[i].TMs - t.Points[i - 1].TMs);
        }
    }
}

if (gaps.Count > 0)
{
    gaps.Sort();
    Console.WriteLine();
    Console.WriteLine($"path-point gaps across {movedTracks} moving tracks ({gaps.Count} gaps): " +
                      $"median={gaps[gaps.Count / 2]}ms p90={gaps[(int)(gaps.Count * 0.9)]}ms p99={gaps[(int)(gaps.Count * 0.99)]}ms max={gaps[^1]}ms");
}

return 0;

sealed class DamageSink(Action<int, int> onDamage) : IStreamProcessorSink
{
    public void Damage(string kind, ParsedDamagePacket packet, bool saved, string? reason, int? mobCode)
        => onDamage(packet.ActorId, packet.TargetId);

    public void Dispatch(int opcode, string? opcodeName, bool extraFlag, int len) { }
    public void UnknownOpcode(int opcode, bool extraFlag, int len) { }
    public void CompressedPacket(int len, bool extraFlag) { }
    public void ParserError(string stage, string reason) { }
    public void Meta(string type, params (string Key, object? Value)[] fields) { }
    public void Battle(int target, int toggle, int? mobCode, string? mobName, bool accepted, string? reason) { }
}
