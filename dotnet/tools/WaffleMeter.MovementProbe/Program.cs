using System.Text;
using K4os.Compression.LZ4;
using WaffleMeter.Capture;
using WaffleMeter.Capture.Corpus;
using WaffleMeter.Data;

// Phase 0 spike: does a MOVEMENT (0x37xx) packet's leading entity id share the SAME id-space as the
// damage (0x3804) actor id == the user uid? If yes, a positional replay can attribute each moving
// entity to a known player via DataManager.User(id) with no new id-bridge — the make-or-break gate.
//
// Replays a packet-debug corpus through the REAL aligner+assembler+StreamProcessor (which populates a
// DataManager's identity from 0x3633/0x3645 and emits damage actor ids), AND mirrors the exact framing
// + FF-FF LZ4 decompression to extract the unparsed 0x37xx entity ids + float position triplets, then
// cross-references the two id sets. READ-ONLY; loads no catalogs; touches no app state.
// Usage: dotnet run --project <MovementProbe> -c Release -- <corpus.jsonl> [--samples N]

Console.OutputEncoding = Encoding.UTF8;
if (args.Length < 1)
{
    Console.Error.WriteLine("usage: dotnet run --project <MovementProbe> -c Release -- <corpus.jsonl> [--samples N]");
    return 1;
}

string path = args[0];
if (!File.Exists(path))
{
    Console.Error.WriteLine($"corpus not found: {path}");
    return 1;
}

int sampleN = 12;
for (int i = 1; i < args.Length - 1; i++)
{
    if (args[i] == "--samples" && int.TryParse(args[i + 1], out int n))
    {
        sampleN = n;
    }
}

var dm = new DataManager();
long simNow = 0;
dm.Clock = () => simNow;

var sink = new ProbeSink();
var processor = new StreamProcessor(sink, dm);
var scanner = new MovementScanner();

var aligner = new PacketAlignmenter();
var assembler = new StreamAssembler((packet, at) =>
{
    processor.OnPacketReceived(packet, at); // populate DataManager identity + collect damage actor ids
    scanner.Scan(packet, at);               // extract 0x37xx entity ids + float positions
});

string currentIp = "";
long segCount = 0;
foreach (CapturedSegment seg in CaptureCorpusReader.ReadCaptures(path))
{
    segCount++;
    simNow = seg.ArrivedAtMs;
    if (seg.SrcIp != currentIp)
    {
        currentIp = seg.SrcIp;
        aligner.Reset();
    }

    foreach (AlignedChunk chunk in aligner.Feed(seg.Seq, seg.Payload, seg.ArrivedAtMs))
    {
        assembler.ProcessChunk(chunk.Data, chunk.ArrivedAt);
    }
}

// ----------------------------------------------------------------------------- report
HashSet<int> damageActors = sink.ActorIds;
HashSet<int> damageTargets = sink.TargetIds;
HashSet<int> movementIds = scanner.AllIds;

var overlap = new HashSet<int>(movementIds);
overlap.IntersectWith(damageActors);

Console.WriteLine($"=== corpus: {Path.GetFileName(path)} ===");
Console.WriteLine($"segments={segCount}  dispatched={sink.Dispatched}  unknown={sink.Unknown}  " +
                  $"damageEvents={sink.DamageCount}  movementPkts={scanner.TotalPackets}  bundles={scanner.Bundles}");
Console.WriteLine();

Console.WriteLine("--- id sets ---");
Console.WriteLine($"  damage actor ids (0x3804) : count={damageActors.Count,-5} range [{Min(damageActors)} .. {Max(damageActors)}]");
Console.WriteLine($"  damage target ids (mobs)  : count={damageTargets.Count,-5} range [{Min(damageTargets)} .. {Max(damageTargets)}]");
Console.WriteLine($"  movement ids (0x37xx)     : count={movementIds.Count,-5} range [{Min(movementIds)} .. {Max(movementIds)}]");
Console.WriteLine();

Console.WriteLine("--- 0x37xx opcode breakdown (offset = bytes after the entity-id varint) ---");
Console.WriteLine($"  {"opcode",-8} {"pkts",8} {"ids",6} {"domOff",7} {"triplet%",9}");
foreach (KeyValuePair<int, OpcodeStat> kv in scanner.ByOpcode.OrderByDescending(k => k.Value.Count))
{
    OpcodeStat s = kv.Value;
    int domOff = s.OffsetHist.Count > 0 ? s.OffsetHist.OrderByDescending(o => o.Value).First().Key : -1;
    double pct = s.Count > 0 ? 100.0 * s.PlausiblePackets / s.Count : 0;
    Console.WriteLine($"  0x{kv.Key:X4} {s.Count,8} {s.Ids.Count,6} {domOff,7} {pct,8:F1}%");
}

Console.WriteLine();
Console.WriteLine("--- CROSS-CHECK: movement entity-id  vs  damage actor-id (== uid) ---");
Console.WriteLine($"  overlap |M ∩ D|                         : {overlap.Count}");
Console.WriteLine($"  damage actors also seen moving          : {Pct(overlap.Count, damageActors.Count)}");
Console.WriteLine($"  movement ids that are damage actors     : {Pct(overlap.Count, movementIds.Count)}");
int movInTargets = movementIds.Count(damageTargets.Contains);
Console.WriteLine($"  movement ids that are damage targets    : {movInTargets} (bosses/mobs move too)");
int resolvedNamed = movementIds.Count(id => dm.User(id)?.Nickname is { Length: > 0 });
Console.WriteLine($"  movement ids resolving to a NAMED user  : {Pct(resolvedNamed, movementIds.Count)}");

int exec = dm.ExecutorId();
Console.WriteLine();
Console.WriteLine($"--- self (executor uid={exec}) ---");
Console.WriteLine($"  self in damage actors : {damageActors.Contains(exec)}");
Console.WriteLine($"  self in movement ids  : {movementIds.Contains(exec)}");

Console.WriteLine();
Console.WriteLine($"--- matched ids resolved via DataManager (up to {sampleN}) ---");
foreach (int id in overlap.OrderBy(x => x).Take(sampleN))
{
    Console.WriteLine($"  id={id,-8} {Label(dm.User(id))}");
    Console.WriteLine($"      {scanner.TrackSummary(id)}");
}

List<int> movementOnly = movementIds.Except(damageActors).OrderBy(x => x).ToList();
Console.WriteLine();
Console.WriteLine($"--- movement-only ids (move but never dealt damage): {movementOnly.Count} (up to {sampleN}) ---");
foreach (int id in movementOnly.Take(sampleN))
{
    User? u = dm.User(id);
    string tag = damageTargets.Contains(id) ? "[damage target — boss/mob]" : "";
    Console.WriteLine($"  id={id,-8} {Label(u)} {tag}");
    if (u?.Nickname is { Length: > 0 })
    {
        Console.WriteLine($"      {scanner.TrackSummary(id)}");
    }
}

// sample raw bodies for the dominant opcode (manual layout inspection if overlap is low)
KeyValuePair<int, OpcodeStat>? dominant = scanner.ByOpcode.Count > 0
    ? scanner.ByOpcode.OrderByDescending(k => k.Value.Count).First()
    : null;
if (dominant is { } dom)
{
    Console.WriteLine();
    Console.WriteLine($"--- sample bodies for 0x{dom.Key:X4} (hex from entity-id varint) ---");
    foreach (string h in dom.Value.SampleHex)
    {
        Console.WriteLine($"  {h}");
    }
}

Console.WriteLine();
double cover = damageActors.Count > 0 ? 100.0 * overlap.Count / damageActors.Count : 0;
// A single named-user resolution from a movement id is conclusive proof of a shared keyspace; the raw
// overlap % is only a lower bound because these debug corpora under-sample movement and reuse ids.
bool sameSpace = resolvedNamed > 0 || cover >= 60;
string verdict = sameSpace
    ? "SAME ID-SPACE — movement ids ARE damage/uid ids (movement id resolved to a named player). " +
      "Direct attribution via DataManager.User(id) is viable (GO)."
    : overlap.Count == 0
        ? "DISJOINT — no overlap in this corpus; re-test on a combat+identity-rich capture before concluding."
        : "PARTIAL — overlap present but no named-user resolution here; re-test on an identity-rich capture.";
Console.WriteLine($"VERDICT: {verdict}");
return 0;

// ----------------------------------------------------------------------------- helpers
static string Label(User? u) => u is null
    ? "<unresolved — no identity packet for this id>"
    : $"nick='{u.Nickname}' srv={u.Server} job={u.Job?.ClassName() ?? "?"} pow={u.Power}{(u.IsExecutor ? "  [SELF]" : "")}";

static string Pct(int part, int whole) => whole == 0 ? $"{part}/0 (n/a)" : $"{part}/{whole} ({100.0 * part / whole:F1}%)";

static string Min(HashSet<int> s) => s.Count == 0 ? "-" : s.Min().ToString();

static string Max(HashSet<int> s) => s.Count == 0 ? "-" : s.Max().ToString();

sealed class OpcodeStat
{
    public long Count;
    public long PlausiblePackets;
    public readonly HashSet<int> Ids = new();
    public readonly Dictionary<int, int> OffsetHist = new();
    public readonly List<string> SampleHex = new();
}

sealed class MovementScanner
{
    public long TotalPackets;
    public long Bundles;
    public readonly HashSet<int> AllIds = new();
    public readonly Dictionary<int, OpcodeStat> ByOpcode = new();
    private readonly Dictionary<int, List<(long At, float X, float Y, float Z, int Opcode, int K)>> _tracks = new();

    public void Scan(byte[] packet, long at)
    {
        try
        {
            ScanInner(packet, at);
        }
        catch
        {
            // best-effort spike: a malformed/short frame must never abort the replay
        }
    }

    private void ScanInner(byte[] packet, long at)
    {
        VarIntOutput lengthInfo = PacketPrimitives.ReadVarInt(packet);
        if (lengthInfo.Length < 0 || lengthInfo.Length >= packet.Length)
        {
            return;
        }

        int flagByte = packet[lengthInfo.Length];
        bool extraFlag = flagByte >= 0xF0 && flagByte < 0xFF;

        if (extraFlag)
        {
            if (lengthInfo.Length + 2 < packet.Length
                && packet[lengthInfo.Length + 1] == 0xFF
                && packet[lengthInfo.Length + 2] == 0xFF)
            {
                Decompress(packet, lengthInfo.Length, true, at);
                return;
            }
        }
        else if (lengthInfo.Length + 1 < packet.Length
                 && packet[lengthInfo.Length] == 0xFF
                 && packet[lengthInfo.Length + 1] == 0xFF)
        {
            Decompress(packet, lengthInfo.Length, false, at);
            return;
        }

        int opcodeOffset = lengthInfo.Length + (extraFlag ? 1 : 0);
        if (opcodeOffset + 1 >= packet.Length)
        {
            return;
        }

        int opcodeKey = (packet[opcodeOffset] & 0xFF) | ((packet[opcodeOffset + 1] & 0xFF) << 8);
        if ((opcodeKey & 0xFF00) == 0x3700)
        {
            Record(opcodeKey, packet, opcodeOffset + 2, at);
        }
    }

    // Mirror of StreamProcessor.DecompressPacket, but recurses into Scan instead of dispatching.
    private void Decompress(byte[] packet, int headerLength, bool extraFlag, long at)
    {
        Bundles++;
        int offset = headerLength + 2 + (extraFlag ? 1 : 0);
        if (offset + 4 > packet.Length)
        {
            return;
        }

        int originLength = PacketPrimitives.ParseUInt32Le(packet, offset);
        offset += 4;
        if (originLength <= 0 || originLength > 8_000_000)
        {
            return;
        }

        var restored = new byte[originLength];
        LZ4Codec.Decode(packet.AsSpan(offset, packet.Length - offset), restored.AsSpan(0, originLength));

        int innerOffset = 0;
        while (innerOffset < restored.Length)
        {
            VarIntOutput li = PacketPrimitives.ReadVarInt(restored, innerOffset);
            if (li.Value == 0)
            {
                innerOffset += 1;
                continue;
            }

            if (li.Value < 0)
            {
                break;
            }

            int realLength = li.Value + li.Length - 4;
            if (realLength <= 0 || innerOffset + realLength > restored.Length)
            {
                break;
            }

            ScanInner(restored[innerOffset..(innerOffset + realLength)], at);
            innerOffset += realLength;
        }
    }

    private void Record(int opcode, byte[] packet, int bodyStart, long at)
    {
        VarIntOutput idInfo = PacketPrimitives.ReadVarInt(packet, bodyStart);
        if (idInfo.Length < 0)
        {
            return;
        }

        int entityId = idInfo.Value;
        TotalPackets++;
        AllIds.Add(entityId);

        OpcodeStat s = GetStat(opcode);
        s.Count++;
        s.Ids.Add(entityId);
        if (s.SampleHex.Count < 4)
        {
            s.SampleHex.Add(HexFrom(packet, bodyStart, 32));
        }

        int afterId = bodyStart + idInfo.Length;
        for (int k = 0; k <= 8; k++)
        {
            int o = afterId + k;
            if (o + 12 > packet.Length)
            {
                break;
            }

            float x = ReadF(packet, o);
            float y = ReadF(packet, o + 4);
            float z = ReadF(packet, o + 8);
            if (Coord3(x, y, z))
            {
                s.OffsetHist[k] = s.OffsetHist.GetValueOrDefault(k) + 1;
                s.PlausiblePackets++;
                AddSample(entityId, at, x, y, z, opcode, k);
                break; // first plausible offset only — layout is fixed per opcode
            }
        }
    }

    private OpcodeStat GetStat(int opcode)
    {
        if (!ByOpcode.TryGetValue(opcode, out OpcodeStat? s))
        {
            s = new OpcodeStat();
            ByOpcode[opcode] = s;
        }

        return s;
    }

    private void AddSample(int id, long at, float x, float y, float z, int opcode, int k)
    {
        if (!_tracks.TryGetValue(id, out List<(long, float, float, float, int, int)>? list))
        {
            list = new List<(long, float, float, float, int, int)>();
            _tracks[id] = list;
        }

        if (list.Count < 4000)
        {
            list.Add((at, x, y, z, opcode, k));
        }
    }

    // Summarize using only the single (opcode, offset) layout this entity was seen at MOST often, so a
    // stray offset-by-one false-positive on another opcode can't smear the trajectory.
    public string TrackSummary(int id)
    {
        if (!_tracks.TryGetValue(id, out List<(long At, float X, float Y, float Z, int Opcode, int K)>? list) || list.Count == 0)
        {
            return "track: no decoded positions";
        }

        (int Opcode, int K) layout = list
            .GroupBy(p => (p.Opcode, p.K))
            .OrderByDescending(g => g.Count())
            .First().Key;

        List<(long At, float X, float Y, float Z, int Opcode, int K)> ordered = list
            .Where(p => p.Opcode == layout.Opcode && p.K == layout.K)
            .OrderBy(p => p.At)
            .ToList();

        var first = ordered[0];
        var last = ordered[^1];
        float minX = ordered.Min(p => p.X), maxX = ordered.Max(p => p.X);
        float minY = ordered.Min(p => p.Y), maxY = ordered.Max(p => p.Y);
        float minZ = ordered.Min(p => p.Z), maxZ = ordered.Max(p => p.Z);
        float maxStep = 0;
        for (int i = 1; i < ordered.Count; i++)
        {
            float dx = ordered[i].X - ordered[i - 1].X;
            float dy = ordered[i].Y - ordered[i - 1].Y;
            float dz = ordered[i].Z - ordered[i - 1].Z;
            maxStep = MathF.Max(maxStep, MathF.Sqrt((dx * dx) + (dy * dy) + (dz * dz)));
        }

        return $"track[0x{layout.Opcode:X4}+{layout.K}]: n={ordered.Count} first=({first.X:F0},{first.Y:F0},{first.Z:F0}) " +
               $"last=({last.X:F0},{last.Y:F0},{last.Z:F0}) " +
               $"bbox=({maxX - minX:F0}x{maxY - minY:F0}x{maxZ - minZ:F0}) maxStep={maxStep:F1}";
    }

    private static float ReadF(byte[] b, int o) => BitConverter.ToSingle(b.AsSpan(o, 4));

    private static bool Fin(float f) =>
        !float.IsNaN(f) && !float.IsInfinity(f) && MathF.Abs(f) < 5_000_000f && (f == 0f || MathF.Abs(f) > 1e-3f);

    private static bool Coord3(float x, float y, float z)
    {
        if (!(Fin(x) && Fin(y) && Fin(z)))
        {
            return false;
        }

        int big = 0;
        if (MathF.Abs(x) > 50f) big++;
        if (MathF.Abs(y) > 50f) big++;
        if (MathF.Abs(z) > 50f) big++;
        return big >= 2; // real world coords are large-magnitude; filters out flag-byte float coincidences
    }

    private static string HexFrom(byte[] b, int start, int n)
    {
        int end = Math.Min(start + n, b.Length);
        var sb = new StringBuilder((end - start) * 3);
        for (int i = start; i < end; i++)
        {
            if (i > start)
            {
                sb.Append(' ');
            }

            sb.Append(b[i].ToString("X2"));
        }

        return sb.ToString();
    }
}

sealed class ProbeSink : IStreamProcessorSink
{
    public long Dispatched;
    public long Unknown;
    public long DamageCount;
    public readonly HashSet<int> ActorIds = new();
    public readonly HashSet<int> TargetIds = new();

    public void Dispatch(int opcode, string? opcodeName, bool extraFlag, int len) => Dispatched++;

    public void UnknownOpcode(int opcode, bool extraFlag, int len) => Unknown++;

    public void Damage(string kind, ParsedDamagePacket packet, bool saved, string? reason, int? mobCode)
    {
        DamageCount++;
        ActorIds.Add(packet.ActorId);
        TargetIds.Add(packet.TargetId);
    }

    public void CompressedPacket(int len, bool extraFlag) { }
    public void ParserError(string stage, string reason) { }
    public void Meta(string type, params (string Key, object? Value)[] fields) { }
    public void Battle(int target, int toggle, int? mobCode, string? mobName, bool accepted, string? reason) { }
}
