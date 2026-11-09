using System.Diagnostics;
using Tokenify.ReplayReader;

namespace Tokenify.Tests;

public static class Program
{
    public static int Main(string[] args)
    {
        if (args.Length >= 2 && args[0] == "scan")
        {
            return Scan(args[1], args.Contains("-v"));
        }

        if (args.Length >= 1 && args[0] == "test")
        {
            var fixtures = args.Length >= 2 ? args[1] : Environment.GetEnvironmentVariable("TOKENIFY_REPLAY_FIXTURES");
            return Tests.RunAll(fixtures);
        }

        Console.WriteLine("usage: Tokenify.Tests test [fixtures-folder] | scan <file-or-folder> [-v]");
        return 2;
    }

    private static int Scan(string target, bool verbose)
    {
        var files = Directory.Exists(target)
            ? Directory.EnumerateFiles(target, "*.replay").OrderBy(f => f, StringComparer.OrdinalIgnoreCase).ToList()
            : new List<string> { target };
        Console.WriteLine($"{"file",-34} {"status",-11} {"chests",6} {"anyone",6} {"secs",5} {"build",-32} match/recorder");
        foreach (var file in files)
        {
            var sw = Stopwatch.StartNew();
            var r = new ChestReader().Read(file);
            sw.Stop();
            var who = r.RecorderAccountId is { } id ? "rec " + id[..Math.Min(8, id.Length)] + "…" : "rec ?";
            var name = r.RecorderName is { } n ? $" \"{n}\"" : "";
            Console.WriteLine($"{Path.GetFileName(file),-34} {r.Status,-11} {r.ChestsOpened,6} {r.ChestsOpenedByAnyone,6} {r.LengthMs / 1000,5} {Trunc(r.Branch, 32),-32} {(r.MatchSessionId is { } s ? "match " + s[..Math.Min(8, s.Length)] + "…" : "match ?")} {who}{name} team={r.RecorderTeam?.ToString() ?? "?"} ({sw.ElapsedMilliseconds} ms)");
            if (r.Error != null) Console.WriteLine($"    note: {r.Error}");
            if (r.UnknownChestClasses.Count > 0) Console.WriteLine($"    unknown chest classes: {string.Join(", ", r.UnknownChestClasses)}");
            if (r.OtherContainers.Count > 0) Console.WriteLine($"    other containers: {string.Join(", ", r.OtherContainers.Select(kv => kv.Key + "=" + kv.Value))}");
            if (verbose)
            {
                foreach (var c in r.Chests)
                {
                    Console.WriteLine($"    {TimeSpan.FromSeconds(c.Time):mm\\:ss}  {c.Kind,-8} {c.ClassName}{(c.Destroyed ? " (destroyed)" : "")}{(c.Reopened ? " (reopened after refill)" : "")}{(c.Unverified ? " (unverified class)" : "")}");
                }
            }
        }

        return 0;
    }

    private static string Trunc(string? s, int n) => s is null ? "?" : s.Length <= n ? s : s[..n];
}
