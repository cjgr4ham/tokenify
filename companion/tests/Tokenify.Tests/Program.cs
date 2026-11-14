using System.Diagnostics;
using Tokenify.ReplayReader;

namespace Tokenify.Tests;

public static class Program
{
    public static int Main(string[] args)
    {
        // Tests never touch a real install: logs, settings and exports go to a throwaway folder.
        if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("TOKENIFY_DATA")))
        {
            Environment.SetEnvironmentVariable("TOKENIFY_DATA", Path.Combine(Path.GetTempPath(), "tokenify-tests", "data-" + Environment.ProcessId));
        }

        if (args.Length >= 2 && args[0] == "scan")
        {
            return Scan(args[1], args.Contains("-v"));
        }

        if (args.Length >= 2 && args[0] == "probe")
        {
            Console.OutputEncoding = System.Text.Encoding.UTF8;
            return Forensics.Run(args[1], args);
        }

        if (args.Length >= 1 && args[0] == "render")
        {
            return RenderSamples.Run(args.Length >= 2 ? args[1] : Path.Combine(Path.GetTempPath(), "tokenify-tests", "render"));
        }

        // Release tooling: the private key never goes into the repository.
        if (args.Length >= 2 && args[0] == "keygen")
        {
            if (File.Exists(args[1])) { Console.Error.WriteLine("refusing to overwrite an existing key: " + args[1]); return 1; }
            var (pem, pub) = Tokenify.Core.Updater.NewKey();
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(args[1]))!);
            File.WriteAllText(args[1], pem);
            Console.WriteLine(pub);
            return 0;
        }

        if (args.Length >= 3 && args[0] == "sign")
        {
            var signature = Tokenify.Core.Updater.Sign(args[1], File.ReadAllText(args[2]));
            File.WriteAllText(args[1] + ".sig", signature);
            Console.WriteLine(Tokenify.Core.Updater.Verify(args[1], signature)
                ? "signed and verified against the key built into the app: " + args[1] + ".sig"
                : "SIGNED, BUT IT DOES NOT MATCH THE KEY BUILT INTO THE APP");
            return 0;
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
