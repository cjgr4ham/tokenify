using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;
using Tokenify.ReplayReader;

namespace Tokenify.Analyze;

/// <summary>
/// tokenify-analyze: reads Fortnite replay files and reports, per match, who recorded it, how many chests that
/// player searched and destroyed, what could not be attributed, and whether the replay was read completely.
/// </summary>
public static class Program
{
    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public static int Main(string[] args)
    {
        Console.OutputEncoding = System.Text.Encoding.UTF8;
        var flags = args.Where(a => a.StartsWith('-')).Select(a => a.TrimStart('-').ToLowerInvariant()).ToHashSet();
        string? expectFile = null;
        var targets = new List<string>();
        for (var i = 0; i < args.Length; i++)
        {
            if (args[i].TrimStart('-').Equals("expect", StringComparison.OrdinalIgnoreCase) && args[i].StartsWith('-') && i + 1 < args.Length)
            {
                expectFile = args[++i];
            }
            else if (!args[i].StartsWith('-'))
            {
                targets.Add(args[i]);
            }
        }

        if (targets.Count == 0 || flags.Contains("h") || flags.Contains("help") || flags.Contains("?"))
        {
            Console.WriteLine("""
                TOKENIFY replay analyser

                  tokenify-analyze <replay-file-or-folder> [more...] [options]

                  -events, -e     list every chest event with the evidence behind it
                  -json           print the results as JSON instead of text
                  -expect <file>  compare against hand-counted totals (see tests/fixtures/README.md)

                It only reads replay files. A replay that Fortnite is still writing, or that is damaged or from an
                unknown version, is reported as such and never as a zero-chest match.
                """);
            return targets.Count == 0 ? 2 : 0;
        }

        var files = new List<string>();
        foreach (var t in targets)
        {
            if (Directory.Exists(t)) files.AddRange(Directory.EnumerateFiles(t, "*.replay").OrderBy(f => f, StringComparer.OrdinalIgnoreCase));
            else if (File.Exists(t)) files.Add(t);
            else Console.Error.WriteLine($"not found: {t}");
        }
        if (files.Count == 0)
        {
            Console.Error.WriteLine("no replay files to read");
            return 2;
        }

        var reader = new ChestReader();
        var results = new List<(ScanResult Result, long Ms)>();
        foreach (var file in files)
        {
            var sw = Stopwatch.StartNew();
            var r = reader.Read(file);
            results.Add((r, sw.ElapsedMilliseconds));
        }

        var expected = expectFile is null ? null : Truth.Load(expectFile);

        if (flags.Contains("json"))
        {
            Console.WriteLine(JsonSerializer.Serialize(results.Select(x => Report.From(x.Result, x.Ms)), Json));
        }
        else
        {
            Console.WriteLine($"TOKENIFY replay analyser - counting rules {ParserInfo.Version}, chest registry v{ChestRegistry.Default.Version}");
            Console.WriteLine();
            var showEvents = flags.Contains("events") || flags.Contains("e");
            foreach (var (r, ms) in results) Print(r, ms, showEvents);
            if (results.Count > 1) PrintTotals(results.Select(x => x.Result).ToList());
        }

        return expected is null ? 0 : Truth.Compare(expected, results.Select(x => x.Result).ToList());
    }

    private static string Clock(float seconds) => $"{(int)seconds / 60:00}:{(int)seconds % 60:00}";

    private static string Completeness(ScanResult r) => r.Status switch
    {
        ScanStatus.Complete => $"COMPLETE - read to the end ({r.LengthMs / 60000} min {r.LengthMs / 1000 % 60:00} s)",
        ScanStatus.Partial => $"PARTIAL - reading stopped early, the totals below are a minimum and are not final ({r.Error})",
        ScanStatus.NotReady => $"NOT READY - Fortnite has not finished writing this replay ({r.Error})",
        ScanStatus.Corrupt => $"UNREADABLE - not a Fortnite replay, or damaged ({r.Error})",
        ScanStatus.Unsupported => $"UNSUPPORTED VERSION - kept for a retry after an update ({r.Error})",
        ScanStatus.NoRecorder => $"NO RECORDER - read, but the recording player could not be identified ({r.Error})",
        _ => r.Status.ToString(),
    };

    private static void Print(ScanResult r, long ms, bool events)
    {
        Console.WriteLine(Path.GetFileName(r.FilePath));
        Console.WriteLine($"  Replay id          {r.ReplayGuid ?? "?"}");
        Console.WriteLine($"  Match id           {r.MatchSessionId ?? "? (not in the replay)"}");
        Console.WriteLine($"  Fortnite version   {r.Branch ?? "?"}{(r.Changelist > 0 ? $"  (build {r.Changelist})" : "")}");
        Console.WriteLine($"  Recorded by        {(r.RecorderFound ? $"{r.RecorderName ?? "(name not in replay)"}  Epic id {(r.RecorderAccountId is { Length: >= 8 } id ? id[..8] + "…" : "?")}{(r.RecorderTeam is int t ? $"  team {t}" : "")}" : "?")}");
        Console.WriteLine($"  Completeness       {Completeness(r)}");

        var usable = r.Status is ScanStatus.Complete or ScanStatus.Partial;
        if (!usable)
        {
            Console.WriteLine("  Chests             not counted (see completeness). This is NOT a zero-chest match.");
            Console.WriteLine();
            return;
        }

        Console.WriteLine($"  Searched chests    {r.Searched}");
        Console.WriteLine($"  Destroyed chests   {r.Destroyed}");
        Console.WriteLine($"  TOTAL COUNTED      {r.ChestsOpened}{(r.Corroborated > 0 ? $"   ({r.Confirmed} named by the game on the chest + {r.Corroborated} from your own recorded action)" : "")}{(r.Status == ScanStatus.Partial ? "   (minimum: partial replay)" : "")}");
        Console.WriteLine($"  Ambiguous          {r.Ambiguous}{(r.Ambiguous > 0 ? "   not counted - for review" : "")}");
        Console.WriteLine($"  No record          {r.Unobservable}{(r.Unobservable > 0 ? "   chest spots that vanished with no record of who or why (not counted)" : "")}");
        if (r.ChestsByKind() is { Count: > 0 } kinds) Console.WriteLine($"  By type            {string.Join(", ", kinds.OrderByDescending(k => k.Value).Select(k => $"{k.Key} {k.Value}"))}");
        if (r.OtherContainers.Count > 0) Console.WriteLine($"  Other containers   {string.Join(", ", r.OtherContainers.Select(k => $"{k.Key} {k.Value}"))}   (never counted as chests)");
        if (r.UnknownChestClasses.Count > 0) Console.WriteLine($"  Unverified classes {string.Join(", ", r.UnknownChestClasses)}");
        if (r.CorruptBlocksSkipped > 0) Console.WriteLine($"  Damaged blocks     {r.CorruptBlocksSkipped} skipped");
        Console.WriteLine($"  Read in            {ms} ms");

        if (events && r.Events.Count > 0)
        {
            Console.WriteLine("  Events");
            foreach (var e in r.Events)
            {
                var what = e.Signals.Contains(Signal.CharacterNotRecorded) ? "no data" : e.Action switch { ChestAction.Searched => "searched", ChestAction.Destroyed => "destroyed", _ => "vanished" };
                var cls = e.Evidence switch { Evidence.Confirmed => "confirmed", Evidence.Corroborated => "corroborated", Evidence.Ambiguous => "AMBIGUOUS", _ => "no record" };
                Console.WriteLine($"    {Clock(e.Time)}  {what,-9} {e.Kind,-10} {e.ClassName,-28} {cls,-12} {(e.Counted ? "counted" : "NOT counted"),-11} [{string.Join(", ", e.Signals)}]{(e.Holding is not null ? $"  holding {e.Holding}" : "")}{(e.Reopened ? "  (refilled, opened again)" : "")}{(e.Unverified ? "  (unverified class)" : "")}");
                if (e.Reason is not null) Console.WriteLine($"           {e.Reason}");
            }
        }
        Console.WriteLine();
    }

    private static void PrintTotals(List<ScanResult> all)
    {
        var counted = all.Where(r => r.Status == ScanStatus.Complete).ToList();
        Console.WriteLine($"TOTAL over {all.Count} replays: {counted.Sum(r => r.ChestsOpened)} confirmed chests in {counted.Count} complete matches"
            + $" (searched {counted.Sum(r => r.Searched)}, destroyed {counted.Sum(r => r.Destroyed)}), {counted.Sum(r => r.Ambiguous)} ambiguous, {counted.Sum(r => r.Unobservable)} with no record."
            + (counted.Count > 0 ? $"  Chests per game: {(double)counted.Sum(r => r.ChestsOpened) / counted.Count:0.00}" : ""));
        var skipped = all.Count - counted.Count;
        if (skipped > 0) Console.WriteLine($"{skipped} replay(s) could not be counted and are NOT included as zero-chest games: {string.Join(", ", all.Where(r => r.Status != ScanStatus.Complete).GroupBy(r => r.Status).Select(g => $"{g.Count()} {g.Key}"))}");
    }
}

/// <summary>The JSON shape: no raw account id, only its one-way hash.</summary>
public sealed record Report(
    string File, string Status, string? Note, string? ReplayId, string? MatchId, string? FortniteVersion, uint Build,
    string? RecorderName, string? RecorderHash, int? RecorderTeam, DateTime? StartedUtc, uint LengthSeconds,
    int Searched, int Destroyed, int TotalConfirmed, int Confirmed, int Corroborated, int Ambiguous, int NoRecord,
    Dictionary<string, int> ByType, Dictionary<string, int> OtherContainers, List<ChestEvent> Events,
    string CountingRules, int RegistryVersion, long ReadMs)
{
    public static Report From(ScanResult r, long ms) => new(
        Path.GetFileName(r.FilePath), r.Status.ToString(), r.Error, r.ReplayGuid, r.MatchSessionId, r.Branch, r.Changelist,
        r.RecorderName, r.RecorderAccountId is { } id ? ParserInfo.PlayerHash(id) : null, r.RecorderTeam, r.StartedUtc, r.LengthMs / 1000,
        r.Searched, r.Destroyed, r.ChestsOpened, r.Confirmed, r.Corroborated, r.Ambiguous, r.Unobservable,
        r.ChestsByKind(), r.OtherContainers, r.Events, r.ParserVersion, r.RegistryVersion, ms);
}

/// <summary>
/// Hand-counted totals to check the parser against: { "replays": [ { "file": "...", "chests": 12,
/// "searched": 9, "destroyed": 3, "note": "..." } ] }. Only the fields present are compared.
/// </summary>
public sealed class Truth
{
    public List<Entry> Replays { get; set; } = new();

    public sealed class Entry
    {
        public string File { get; set; } = "";
        public int? Chests { get; set; }
        public int? Searched { get; set; }
        public int? Destroyed { get; set; }
        public string? Note { get; set; }
    }

    public static Truth Load(string path) => JsonSerializer.Deserialize<Truth>(File.ReadAllText(path),
        new JsonSerializerOptions { PropertyNameCaseInsensitive = true, ReadCommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true }) ?? new Truth();

    public static int Compare(Truth truth, List<ScanResult> results)
    {
        Console.WriteLine();
        Console.WriteLine("Comparison with hand-counted totals");
        int truePositives = 0, expectedTotal = 0, foundTotal = 0, failed = 0, compared = 0;
        foreach (var e in truth.Replays)
        {
            var r = results.FirstOrDefault(x => Path.GetFileName(x.FilePath).Equals(e.File, StringComparison.OrdinalIgnoreCase));
            if (r is null)
            {
                Console.WriteLine($"  ?     {e.File}: not among the replays read");
                continue;
            }
            var problems = new List<string>();
            if (r.Status != ScanStatus.Complete) problems.Add($"status {r.Status}");
            if (e.Chests is int c && c != r.ChestsOpened) problems.Add($"chests: counted {r.ChestsOpened}, hand count {c} ({r.ChestsOpened - c:+0;-0})");
            if (e.Searched is int s && s != r.Searched) problems.Add($"searched: {r.Searched} vs {s}");
            if (e.Destroyed is int d && d != r.Destroyed) problems.Add($"destroyed: {r.Destroyed} vs {d}");
            if (e.Chests is int truthCount)
            {
                compared++;
                expectedTotal += truthCount;
                foundTotal += r.ChestsOpened;
                truePositives += Math.Min(truthCount, r.ChestsOpened);
            }
            if (problems.Count > 0) failed++;
            Console.WriteLine($"  {(problems.Count == 0 ? "ok  " : "FAIL")}  {e.File}: {(problems.Count == 0 ? $"{r.ChestsOpened} chests" : string.Join("; ", problems))}{(r.Ambiguous > 0 ? $"  ({r.Ambiguous} ambiguous not counted)" : "")}");
        }
        if (compared > 0)
        {
            // Match-level totals can only bound precision and recall; event-level truth is checked by the test suite.
            Console.WriteLine($"  {compared} matches compared: hand count {expectedTotal}, counted {foundTotal}."
                + $"  Recall >= {(expectedTotal == 0 ? 1 : (double)truePositives / expectedTotal):P1}, precision >= {(foundTotal == 0 ? 1 : (double)truePositives / foundTotal):P1} (from totals).");
        }
        return failed == 0 ? 0 : 1;
    }
}
