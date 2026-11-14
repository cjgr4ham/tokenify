using System.Text.RegularExpressions;
using Tokenify.ReplayReader;

namespace Tokenify.Tests;

/// <summary>
/// Research tool: prints what a replay records about containers (every replicated field, channel opens and closes
/// with their reason), who caused each opening or destruction, and optionally other classes, spawned actors,
/// remote calls and player positions. Used to work out what destroyed chests look like before any counting rule
/// is written. It only ever reads replay files.
///   Tokenify.Tests.exe probe [file-or-folder] [--events] [--timelines] [--census] [--rpc-census] [--movement]
///        [--groups regex] [--field-filter regex] [--spawns regex] [--keywords regex] [--all-paths]
/// </summary>
public sealed class CountingLogger : Microsoft.Extensions.Logging.ILogger
{
    public sealed class Entry { public int Count; public float First = float.MaxValue, Last; public List<string> Examples = new(); public Dictionary<int, int> PerMinute = new(); }
    public Dictionary<string, Entry> Seen { get; } = new(StringComparer.Ordinal);
    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
    public bool IsEnabled(Microsoft.Extensions.Logging.LogLevel level) => level >= Microsoft.Extensions.Logging.LogLevel.Warning;
    public void Log<TState>(Microsoft.Extensions.Logging.LogLevel level, Microsoft.Extensions.Logging.EventId id, TState state, Exception? ex, Func<TState, Exception?, string> formatter)
    {
        if (!IsEnabled(level)) return;
        var template = state is IEnumerable<KeyValuePair<string, object?>> kv ? kv.FirstOrDefault(p => p.Key == "{OriginalFormat}").Value?.ToString() : null;
        var key = level + ": " + (template ?? formatter(state, ex)) + (ex is not null ? "  <" + ex.GetType().Name + ">" : "");
        if (!Seen.TryGetValue(key, out var e)) Seen[key] = e = new Entry();
        var now = Tokenify.ReplayReader.FortniteReplayBase.Clock;
        e.Count++; e.First = Math.Min(e.First, now); e.Last = Math.Max(e.Last, now);
        var minute = (int)(now / 60); e.PerMinute[minute] = e.PerMinute.GetValueOrDefault(minute) + 1;
        if (e.Examples.Count < 4) { var text = formatter(state, ex) + (ex is not null ? "  <" + ex.Message + ">" : ""); if (!e.Examples.Contains(text)) e.Examples.Add(text.Length > 260 ? text[..260] : text); }
    }
}

public static class Forensics
{
    private sealed record Event(float Time, uint Channel, uint Actor, string Class, string Key, string Who, uint? Pawn, bool Searched,
        bool Destroyed, bool AtOpen, bool Instant, string? Context, uint? Anim, double? X, double? Y);

    public static int Run(string target, string[] args)
    {
        string? Arg(string name) { var i = Array.IndexOf(args, name); return i >= 0 && i + 1 < args.Length ? args[i + 1] : null; }
        Regex? Rx(string name) => Arg(name) is { } v ? new Regex(v, RegexOptions.IgnoreCase) : null;
        var keywords = Rx("--keywords");
        var census = args.Contains("--census");
        var rpcCensus = args.Contains("--rpc-census");
        var showTimelines = args.Contains("--timelines");
        var showEvents = args.Contains("--events") || showTimelines;
        var movement = args.Contains("--movement");
        var onlyMine = args.Contains("--mine");
        var hex = args.Contains("--hex");
        var from = Arg("--from") is { } fs0 ? float.Parse(fs0, System.Globalization.CultureInfo.InvariantCulture) : float.MinValue;
        var to = Arg("--to") is { } ts0 ? float.Parse(ts0, System.Globalization.CultureInfo.InvariantCulture) : float.MaxValue;
        var files = Directory.Exists(target)
            ? Directory.EnumerateFiles(target, "*.replay").OrderBy(f => f, StringComparer.OrdinalIgnoreCase).ToList()
            : new List<string> { target };

        foreach (var file in files)
        {
            var reader = new ChestReader
            {
                Trace = new TraceOptions
                {
                    Containers = true, ExtraGroups = Rx("--groups"), FieldFilter = Rx("--field-filter"), Keywords = keywords,
                    Census = census, RpcCensus = rpcCensus, Spawns = Rx("--spawns"), Movement = movement, RpcCapture = Rx("--rpc"), Actors = Rx("--actors"),
                },
            };
            var log = args.Contains("--log-census") ? new CountingLogger() : null;
            if (log is not null) reader.UseLogger(log);
            var r = reader.Read(file);
            var t = reader.LastTrace!;
            if (log is not null)
            {
                Console.WriteLine("-- what the replay library could not read (message, count, first and last time, per-minute counts):");
                foreach (var (k, v) in log.Seen.OrderByDescending(kv => kv.Value.Count))
                {
                    Console.WriteLine($"   {v.Count,7} × {k}   [{v.First:0}s .. {v.Last:0}s]  per minute: {string.Join(",", v.PerMinute.OrderBy(m => m.Key).Select(m => m.Key + ":" + m.Value))}");
                    foreach (var e in v.Examples) Console.WriteLine("             e.g. " + e);
                }
            }
            var (mine, mates) = reader.Pawns();
            Console.WriteLine($"==================== {Path.GetFileName(file)}  {r.Status}  build {r.Branch}  {r.LengthMs / 1000}s  counted for recorder: {r.ChestsOpened}");
            Console.WriteLine($"recorder pawns: {string.Join(",", mine)}  teammate pawns: {string.Join(",", mates)}");

            string Who(uint? pawn) => pawn is not uint p || p == 0 ? "none" : mine.Contains(p) ? "ME" : mates.Contains(p) ? "MATE" : "other#" + p;
            string Label(uint guid) => mine.Contains(guid) ? "ME" : mates.Contains(guid) ? "MATE" : t.Paths.TryGetValue(guid, out var p) ? Short(p) : "#" + guid;
            string Show(TraceField f)
            {
                if (f.Note is not null) return $"{f.Name}{{{f.Note}}}";
                if (f.Bits == 1) return $"{f.Name}={(f.Raw & 1)}";
                if (f.Bits == 32 && f.Name.Contains("Magnitude", StringComparison.Ordinal)) return $"{f.Name}={BitConverter.Int32BitsToSingle((int)f.Raw):0.#}";
                if (f.Bits > 64) return hex && f.Data is not null ? $"{f.Name}=<{f.Bits}b:{Convert.ToHexString(f.Data)}>" : $"{f.Name}=<{f.Bits}b>";
                if (f.Name is "SearchingPawn" or "PlayerState" or "Owner" or "Instigator" or "CurrentWeapon" or "HitActor" or "NonPlayerHitActor"
                    || (f.AsNetGuid() is uint g2 && (mine.Contains(g2) || mates.Contains(g2) || t.Paths.ContainsKey(g2))))
                {
                    return f.AsNetGuid() is uint gg ? $"{f.Name}->{(gg == 0 ? "none" : Label(gg))}" : $"{f.Name}=0x{f.Raw:X}/{f.Bits}b";
                }
                return $"{f.Name}=0x{f.Raw:X}/{f.Bits}b";
            }

            if (t.KeywordPaths.Count > 0)
            {
                Console.WriteLine($"-- object names matching keywords ({t.KeywordPaths.Count}):");
                foreach (var p in t.KeywordPaths.Take(args.Contains("--all-paths") ? int.MaxValue : 400)) Console.WriteLine("   " + p);
            }

            Console.WriteLine("-- traced classes and their replicated fields:");
            foreach (var (path, fields) in t.GroupFields.OrderBy(kv => kv.Key))
            {
                Console.WriteLine($"   {Short(path)}  [{string.Join(", ", fields)}]");
            }

            // ---- container channels: one record per channel lifetime, and the events on it
            var opens = t.Channels.Where(c => c.Kind == "open").ToList();
            var closes = t.Channels.Where(c => c.Kind == "close").ToList();
            var events = new List<Event>();
            var outcomes = new Dictionary<string, int>(StringComparer.Ordinal);
            var bareCloses = new Dictionary<string, int>(StringComparer.Ordinal);
            var bareLines = new List<string>();
            foreach (var open in opens)
            {
                var cls = Short(open.Class ?? "?");
                var close = closes.FirstOrDefault(c => c.Channel == open.Channel && c.Actor == open.Actor && c.Time >= open.Time);
                if (open.Bare)
                {
                    var k = $"{ClassOfInstance(cls)} closed {close?.Reason ?? "?"}";
                    bareCloses[k] = bareCloses.GetValueOrDefault(k) + 1;
                    if (showEvents && ChestRegistry.Default.Counts(ChestRegistry.Default.Classify(ClassOfInstance(cls))))
                    {
                        bareLines.Add($"   {open.Time,7:F1}s  no-state chest channel, closed {close?.Reason ?? "?"} after {(close?.Time ?? open.Time) - open.Time:F1}s  {ShortKey(open.Key)}");
                    }
                    continue;
                }
                if (t.GroupFields.Keys.All(g => Short(g) != cls) || !IsContainerClass(open.Class)) continue;

                var ups = t.Updates.Where(u => u.Channel == open.Channel && u.Actor == open.Actor && u.Time >= open.Time && (close is null || u.Time <= close.Time)).ToList();
                uint? pawn = null; bool searched = false, destroyed = false; string outcome = "untouched";
                foreach (var u in ups)
                {
                    bool? s = null, d = null, instant = null; uint? sp = null, anim = null; string? ctx = null;
                    foreach (var f in u.Fields)
                    {
                        switch (f.Name)
                        {
                            case "SearchingPawn": sp = f.AsNetGuid(); break;
                            case "bAlreadySearched": s = (f.Raw & 1) != 0; break;
                            case "bDestroyed": d = (f.Raw & 1) != 0; break;
                            case "bInstantDeath": instant = (f.Raw & 1) != 0; break;
                            case "SearchAnimationCount": anim = (uint)f.Raw; break;
                            case "EffectContext": ctx = f.Note; break;
                        }
                    }
                    if (sp is > 0) pawn = sp;
                    var atOpen = u.Time - open.Time < 0.5f;
                    if (s == false) searched = false;
                    var newSearch = s == true && !searched;
                    var newDestroy = d == true && !destroyed;
                    if (newSearch) searched = true;
                    if (newDestroy) destroyed = true;
                    if (newSearch || newDestroy)
                    {
                        events.Add(new Event(u.Time, open.Channel, open.Actor, cls, open.Key ?? "?", Who(sp is > 0 ? sp : newDestroy && !newSearch ? sp : pawn), sp is > 0 ? sp : pawn,
                            newSearch, newDestroy, atOpen, instant == true, ctx, anim, open.X, open.Y));
                    }
                }
                outcome = destroyed && searched ? "searched+destroyed" : destroyed ? "destroyed" : searched ? "searched" : "untouched";
                var key = $"{cls} {outcome} by {(outcome == "untouched" ? "-" : Who(pawn).StartsWith("other") ? "other" : Who(pawn))}{(close is not null ? ", closed " + close.Reason : "")}";
                outcomes[key] = outcomes.GetValueOrDefault(key) + 1;

                if (showTimelines && (searched || destroyed || close?.Reason == "Destroyed"))
                {
                    Console.WriteLine($"-- ch{open.Channel} actor#{open.Actor} {cls} key={ShortKey(open.Key)}  => {outcome} by {Who(pawn)}");
                    Console.WriteLine($"     {open.Time,7:F1}s  OPEN @{open.X:F0},{open.Y:F0},{open.Z:F0}");
                    foreach (var u in ups) Console.WriteLine($"     {u.Time,7:F1}s  {string.Join(" ", u.Fields.Select(Show))}");
                    if (close is not null) Console.WriteLine($"     {close.Time,7:F1}s  CLOSE {close.Reason}");
                }
            }

            Console.WriteLine("-- container channels by class and outcome:");
            foreach (var (k, n) in outcomes.OrderByDescending(kv => kv.Value)) Console.WriteLine($"   {n,4} × {k}");
            if (bareCloses.Count > 0)
            {
                Console.WriteLine("-- container channels that opened and closed with no state at all:");
                foreach (var (k, n) in bareCloses.OrderByDescending(kv => kv.Value)) Console.WriteLine($"   {n,4} × {k}");
                foreach (var line in bareLines) Console.WriteLine(line);
            }

            // ---- events: who opened or destroyed what, and how the replay showed it
            events.Sort((a, b) => a.Time.CompareTo(b.Time));
            TracePosition? Near(HashSet<uint> pawns, float time)
            {
                TracePosition? best = null; var gap = 5f;
                foreach (var p in t.Positions)
                {
                    if (!pawns.Contains(p.Actor)) continue;
                    var d = Math.Abs(p.Time - time);
                    if (d < gap) { gap = d; best = p; }
                }
                return best;
            }
            // --where 110,798.5,...  : where the recording player was at those moments (needs --movement)
            if (Arg("--where") is { } whereTimes)
            {
                foreach (var part in whereTimes.Split(',', StringSplitOptions.RemoveEmptyEntries))
                {
                    var time = float.Parse(part, System.Globalization.CultureInfo.InvariantCulture);
                    var at = Near(mine, time);
                    Console.WriteLine(at is { } p ? $"WHERE {time:0.0} {p.X:0} {p.Y:0} {p.Time:0.0}" : $"WHERE {time:0.0} none");
                }
            }
            string Distance(Event e)
            {
                if (!movement || e.X is not double x || e.Y is not double y || Near(mine, e.Time) is not { } me) return "";
                var metres = Math.Sqrt((me.X - x) * (me.X - x) + (me.Y - y) * (me.Y - y)) / 100;
                return $"  {metres,5:F0} m from me";
            }

            var live = events.Where(e => !e.AtOpen).ToList();
            int Count(Func<Event, bool> f) => events.Count(f);
            Console.WriteLine($"-- events: {events.Count}   searched: me {Count(e => e.Searched && e.Who == "ME")}, mates {Count(e => e.Searched && e.Who == "MATE")}, others {Count(e => e.Searched && e.Who.StartsWith("other"))}, nobody named {Count(e => e.Searched && e.Who == "none")}"
                + $"   destroyed (not searched): me {Count(e => e.Destroyed && !e.Searched && e.Who == "ME")}, mates {Count(e => e.Destroyed && !e.Searched && e.Who == "MATE")}, others {Count(e => e.Destroyed && !e.Searched && e.Who.StartsWith("other"))}, NOBODY NAMED {Count(e => e.Destroyed && !e.Searched && e.Who == "none")}"
                + $"   seen live {live.Count}, arrived with the channel {events.Count - live.Count}");
            if (showEvents)
            {
                foreach (var e in events)
                {
                    var what = e.Searched && e.Destroyed ? "SEARCHED+DESTROYED" : e.Destroyed ? "DESTROYED" : "searched";
                    Console.WriteLine($"   {e.Time,7:F1}s  {what,-18} {e.Who,-12} {(e.AtOpen ? "at-open" : "live   ")} {e.Class,-34} {ShortKey(e.Key),-46}{(e.Instant ? " instant" : "")}{(e.Anim is uint a ? $" anim={a}" : "")}{(e.Context is not null ? " ctx{" + e.Context + "}" : "")}{Distance(e)}");
                }
            }

            // ---- several containers by the same player at (nearly) the same moment
            foreach (var g in events.Where(e => e.Pawn is > 0).GroupBy(e => e.Pawn!.Value))
            {
                var list = g.OrderBy(e => e.Time).ToList();
                for (var i = 0; i < list.Count;)
                {
                    var j = i + 1;
                    while (j < list.Count && list[j].Time - list[j - 1].Time <= 1.5f) j++;
                    if (j - i >= 2)
                    {
                        Console.WriteLine($"-- cluster: {j - i} containers by {Who(g.Key)} within {list[j - 1].Time - list[i].Time:F1}s at {list[i].Time:F1}s: "
                            + string.Join(", ", list.Skip(i).Take(j - i).Select(e => $"{e.Class}{(e.Destroyed ? "(destroyed)" : "")}{(e.AtOpen ? "(at-open)" : "")}")));
                    }
                    i = j;
                }
            }

            var closesByReason = closes.GroupBy(c => c.Reason).Select(g2 => $"{g2.Key}×{g2.Count()}");
            Console.WriteLine("-- container channel closes by reason: " + string.Join("  ", closesByReason));

            if (t.Spawns.Count > 0)
            {
                Console.WriteLine($"-- spawned actors matching --spawns ({t.Spawns.Count}):");
                foreach (var s in t.Spawns.Where(s => s.Time >= from && s.Time <= to).Take(20000))
                {
                    var me = movement ? Near(mine, s.Time) : null;
                    var far = me is { } q ? $"  {Math.Sqrt((q.X - s.X) * (q.X - s.X) + (q.Y - s.Y) * (q.Y - s.Y)) / 100,6:F0} m" : "";
                    Console.WriteLine($"   {s.Time,7:F1}s  {Short(s.Archetype),-52} @{s.X:F0},{s.Y:F0},{s.Z:F0}{far}");
                }
            }

            if (Rx("--actors") is not null)
            {
                Console.WriteLine("-- channel opens and closes of actors matching --actors:");
                foreach (var c in t.Channels.Where(c => c.Class is not null && Rx("--actors")!.IsMatch(c.Class) && !c.Bare).OrderBy(c => c.Time))
                {
                    Console.WriteLine($"   {c.Time,7:F1}s  ch{c.Channel} {c.Kind,-5} {c.Reason,-10} {ShortKey(c.Class)}");
                }
            }

            if (t.Updates.Any(u => !IsContainerClass(u.Group)))
            {
                Console.WriteLine("-- updates of extra traced classes:");
                foreach (var u in t.Updates.Where(u => !IsContainerClass(u.Group) && u.Time >= from && u.Time <= to && (!onlyMine || mine.Contains(u.Actor))).Take(3000))
                {
                    Console.WriteLine($"   {u.Time,7:F1}s  ch{u.Channel} #{u.Actor} {Label(u.Actor)}<{Short(u.Group)}>  {string.Join(" ", u.Fields.Select(Show))}");
                }
            }

            if (t.Rpcs.Count > 0)
            {
                Console.WriteLine("-- remote calls / custom fields on traced channels: " + string.Join("  ", t.Rpcs.GroupBy(x => Short(x.Cache) + "." + x.Field).OrderByDescending(x => x.Count()).Take(60).Select(x => $"{x.Key}×{x.Count()}")));
            }

            if (census)
            {
                Console.WriteLine("-- busiest classes: " + string.Join("  ", t.Census.OrderByDescending(kv => kv.Value).Take(60).Select(kv => $"{Short(kv.Key)}×{kv.Value}")));
                Console.WriteLine("-- most spawned: " + string.Join("  ", t.SpawnCensus.OrderByDescending(kv => kv.Value).Take(120).Select(kv => $"{Short(kv.Key)}×{kv.Value}")));
            }
            if (rpcCensus)
            {
                Console.WriteLine("-- remote calls / custom fields by class:");
                foreach (var (k, n) in t.RpcCensus.OrderByDescending(kv => kv.Value).Take(250)) Console.WriteLine($"   {n,6} × {Short(k[..k.LastIndexOf('.')])}.{k[(k.LastIndexOf('.') + 1)..]}");
            }
            if (args.Contains("--track"))
            {
                var last = float.MinValue;
                foreach (var p in t.Positions.Where(p => mine.Contains(p.Actor)).OrderBy(p => p.Time))
                {
                    if (p.Time - last < 2f) continue;
                    last = p.Time;
                    Console.WriteLine($"TRACK {p.Time:0.0} #{p.Actor} {p.X:0} {p.Y:0}");
                }
            }
            if (movement) Console.WriteLine($"-- positions recorded: {t.Positions.Count} (recorder: {t.Positions.Count(p => mine.Contains(p.Actor))})");
            Console.WriteLine();
        }
        return 0;

        bool IsContainerClass(string? path) => path is not null && ChestRegistry.Default.Classify(path).Role != ContainerRole.None;
    }

    private static string Short(string path)
    {
        var s = path;
        var slash = s.LastIndexOf('/');
        if (slash >= 0) s = s[(slash + 1)..];
        var dot = s.LastIndexOf('.');
        if (dot >= 0) s = s[(dot + 1)..];
        return s;
    }

    /// <summary>"Tiered_Chest_Athena_C_UAID_E04F..." -> "Tiered_Chest_Athena_C".</summary>
    private static string ClassOfInstance(string name)
    {
        var uaid = name.IndexOf("_UAID_", StringComparison.Ordinal);
        if (uaid > 0) return name[..uaid];
        return Regex.Replace(name, @"_\d+$", "");
    }

    private static string ShortKey(string? key) => key is null ? "?" : key.Length > 44 ? "…" + key[^42..] : key;
}
