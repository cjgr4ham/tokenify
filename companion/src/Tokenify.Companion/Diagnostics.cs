using System.Globalization;
using System.IO.Compression;
using System.Text;
using Tokenify.ReplayReader;

namespace Tokenify.Companion;

/// <summary>
/// Troubleshooting summary and the "Export diagnostics" zip. The export contains versions, folder state, counts,
/// recent errors, the log and the match list (dates, counts, states). It contains no device secret, no pairing code,
/// no Epic account id (only the first 8 characters of its hash) and no replay files.
/// </summary>
internal static class Diagnostics
{
    public sealed record Line(string Label, string Value, bool Ok);

    public static List<Line> Checks(Engine engine, EngineSnapshot s)
    {
        var parserOk = s.UnsupportedFiles == 0;
        return new List<Line>
        {
            new("Replay folder", s.Folder switch
            {
                FolderState.Ok or FolderState.Empty => "Detected: " + s.FolderPath,
                FolderState.Unreadable => "Can't be read: " + s.FolderPath,
                _ => "Missing: " + s.FolderPath,
            }, s.Folder is FolderState.Ok or FolderState.Empty),
            new("Fortnite replays", s.ReplayFiles > 0 ? $"Found {s.ReplayFiles}" : "None found (is Record Replays on?)", s.ReplayFiles > 0),
            new("Parser", parserOk ? $"Compatible (parser {AppInfo.ParserVersion}, chest registry v{ChestRegistry.Default.Version})"
                : $"Update required: {Ui.Plural(s.UnsupportedFiles, "replay", "replays")} from a newer Fortnite version", parserOk),
            new("TOKENIFY connection", !s.Paired ? "Not connected" : s.Revoked ? "Disconnected (reconnect)" : s.Online ? $"Connected as {s.Username}" : "Offline (will retry)",
                s.Paired && !s.Revoked && s.Online),
            new("Pending uploads", s.Queued == 0 ? "None" : Ui.Plural(s.Queued, "match", "matches"), s.Queued == 0 || s.Online),
        };
    }

    public static string Report(Engine engine)
    {
        var s = engine.Snapshot();
        var sb = new StringBuilder();
        sb.AppendLine($"TOKENIFY Companion {AppInfo.Version}");
        sb.AppendLine($"Parser {AppInfo.ParserVersion}, chest registry v{ChestRegistry.Default.Version}");
        sb.AppendLine($"Windows {Environment.OSVersion.Version}, {(Environment.Is64BitOperatingSystem ? "64-bit" : "32-bit")}");
        sb.AppendLine($"Generated {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        sb.AppendLine($"Server: {(AppInfo.CustomServer ? "custom (" + AppInfo.ApiUrl + ")" : "TOKENIFY")}");
        sb.AppendLine();
        foreach (var c in Checks(engine, s)) sb.AppendLine($"{(c.Ok ? "OK " : "!! ")} {c.Label}: {c.Value}");
        sb.AppendLine();
        sb.AppendLine($"Status: {s.Status} (paused: {s.Paused})");
        sb.AppendLine($"Season start: {s.SeasonStartUtc:yyyy-MM-dd HH:mm} UTC; replays from before it: {s.PreSeasonFiles} (never read or sent)");
        foreach (var a in s.Attention) sb.AppendLine("Needs attention: " + a);
        sb.AppendLine();
        var files = engine.Store.Files();
        sb.AppendLine("Replay files by state: " + string.Join(", ", files.GroupBy(f => f.State).Select(g => $"{g.Key}={g.Count()}")));
        foreach (var f in files.Where(f => f.Error is not null).Take(30)) sb.AppendLine($"  {f.Name}: {f.State}: {f.Error}");
        var matches = engine.Store.Matches(1000);
        sb.AppendLine("Matches by upload state: " + string.Join(", ", matches.GroupBy(m => m.Upload).Select(g => $"{g.Key}={g.Count()}")));
        sb.AppendLine("Fortnite accounts seen: " + string.Join(", ", engine.Store.Profiles().Select(p => $"{p.Hash[..8]}… {p.Decision}{(p.Claimed ? " (linked)" : "")}{(p.Error is null ? "" : " " + p.Error)}")));
        return sb.ToString();
    }

    public static void Export(Engine engine, string zipPath)
    {
        using var zip = ZipFile.Open(zipPath, ZipArchiveMode.Create);
        Write(zip, "report.txt", Report(engine));
        Write(zip, "companion.log", Log.Tail());
        var csv = new StringBuilder("started_local,ended_local,chests,tokenify_chests,status,upload,parser,registry,build,player,match_id,last_error\n");
        foreach (var m in engine.Store.Matches(1000))
        {
            csv.Append(CultureInfo.InvariantCulture, $"{m.StartedLocal:yyyy-MM-dd HH:mm},{DateTimeOffset.FromUnixTimeMilliseconds(m.Ended).LocalDateTime:HH:mm},{m.Chests},{m.ServerChests},{m.Status},{m.Upload},{m.Parser},{m.Registry},{Csv(m.Build)},{m.PlayerHash[..8]},{(m.SessionId is null ? "replay-id" : "match-id")},{Csv(m.LastError)}\n");
        }
        Write(zip, "matches.csv", csv.ToString());
    }

    private static string Csv(string? s) => s is null ? "" : "\"" + s.Replace("\"", "'") + "\"";

    private static void Write(ZipArchive zip, string name, string text)
    {
        var entry = zip.CreateEntry(name, CompressionLevel.Optimal);
        using var w = new StreamWriter(entry.Open(), new UTF8Encoding(false));
        w.Write(text);
    }
}
