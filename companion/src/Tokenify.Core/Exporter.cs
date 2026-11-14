using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Tokenify.ReplayReader;

namespace Tokenify.Core;

/// <summary>
/// CSV and JSON exports of the match history, and the compact diagnostic file for one disputed match. Nothing
/// here contains a replay file or an Epic account id (only the name shown in game and a shortened one-way hash).
/// </summary>
public static class Exporter
{
    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    public static string DefaultPath(string kind, string extension)
    {
        Directory.CreateDirectory(AppPaths.Exports);
        return Path.Combine(AppPaths.Exports, $"tokenify-{kind}-{DateTime.Now:yyyy-MM-dd-HHmmss}.{extension}");
    }

    private static string Cell(object? value)
    {
        var s = value switch
        {
            null => "",
            bool b => b ? "yes" : "no",
            double d => d.ToString("0.##", Inv),
            IFormattable f => f.ToString(null, Inv),
            _ => value.ToString() ?? "",
        };
        // A cell starting with = + - @ would be run as a formula by spreadsheet programs.
        if (s.Length > 0 && s[0] is '=' or '+' or '-' or '@' && value is string) s = "'" + s;
        return s.Contains(',') || s.Contains('"') || s.Contains('\n') || s.Contains('\r') ? "\"" + s.Replace("\"", "\"\"") + "\"" : s;
    }

    /// <summary>One line per match, oldest first: readable in Excel or Google Sheets.</summary>
    public static string Csv(IEnumerable<MatchRow> matches, Settings settings)
    {
        var countDestroyed = settings.Tracking.CountDestroyed;
        var sb = new StringBuilder();
        sb.AppendLine("match,started_local,duration_minutes,fortnite_account,category,chests,replay_chests,searched,destroyed,ambiguous_not_counted,"
            + "your_correction,correction_reason,difference,in_statistics,left_out_by_you,your_manual_taps,fortnite_build,counting_rules,chest_registry,"
            + "match_id,replay_file,website");
        var n = 0;
        foreach (var m in matches.OrderBy(m => m.Started))
        {
            n++;
            var replay = m.ReplayCount(countDestroyed);
            sb.AppendLine(string.Join(",", new object?[]
            {
                n, m.StartedLocal.ToString("yyyy-MM-dd HH:mm", Inv), Math.Round(m.Length.TotalMinutes, 1), m.PlayerName ?? "", m.Category,
                m.Effective(countDestroyed), replay, m.Searched, m.Destroyed, m.Ambiguous, m.Corrected, m.CorrectionReason ?? "",
                m.Corrected is int c ? c - replay : null, m.Valid, m.Excluded, m.ManualTaps, m.Build ?? "", m.Parser, m.Registry,
                m.MatchKey, m.File, m.Upload,
            }.Select(Cell)));
        }
        return sb.ToString();
    }

    /// <summary>Everything, machine-readable: totals, every match with its chest events and its correction history.</summary>
    public static string JsonExport(TrackerSnapshot snapshot, IEnumerable<MatchRow> matchesWithEvents, Func<long, List<CorrectionRow>> corrections)
    {
        var settings = snapshot.Settings;
        var s = snapshot.Stats;
        var root = new JsonObject
        {
            ["exported_at"] = DateTime.UtcNow.ToString("O", Inv),
            ["app"] = $"{AppInfo.Name} {AppInfo.Version}",
            ["counting_rules"] = AppInfo.ParserVersion,
            ["chest_registry"] = ChestRegistry.Default.Version,
            ["fortnite_account"] = snapshot.ActiveProfile?.Name,
            ["totals"] = new JsonObject
            {
                ["total_chests"] = s.TotalChests, ["chests_counted_on_this_pc"] = s.TrackedChests, ["starting_total"] = s.StartingTotal,
                ["valid_games"] = s.Games, ["all_time_cpg"] = Round(s.Cpg), ["best_game"] = s.Best,
                ["last_10_cpg"] = Round(s.Last10.Cpg), ["last_10_games_used"] = s.Last10.Games,
                ["last_25_cpg"] = Round(s.Last25.Cpg), ["last_50_cpg"] = Round(s.Last50.Cpg),
                ["destroyed_chests_counted"] = settings.Tracking.CountDestroyed,
            },
            ["sessions"] = new JsonArray(Stats.AllAutoSessions(Tracker.ValidGames(matchesWithEvents, snapshot.ActiveProfile?.Hash, settings),
                    TimeSpan.FromMinutes(settings.Tracking.SessionGapMinutes))
                .Select(x => (JsonNode)new JsonObject
                {
                    ["started_utc"] = x.StartedUtc?.ToString("O", Inv), ["ended_utc"] = x.EndedUtc?.ToString("O", Inv), ["games"] = x.Games,
                    ["chests"] = x.Chests, ["cpg"] = Round(x.Cpg), ["match_minutes"] = Math.Round(x.MatchTime.TotalMinutes, 1),
                    ["span_minutes"] = Math.Round(x.Span.TotalMinutes, 1),
                    ["note"] = $"inferred: matches less than {settings.Tracking.SessionGapMinutes} minutes apart",
                }).ToArray()),
            ["matches"] = new JsonArray(matchesWithEvents.OrderBy(m => m.Started).Select(m => (JsonNode)MatchJson(m, settings, corrections(m.Id))).ToArray()),
        };
        return root.ToJsonString(Json);
    }

    private static double? Round(double? v) => v is double d ? Math.Round(d, 2) : null;

    private static JsonObject MatchJson(MatchRow m, Settings settings, List<CorrectionRow> corrections)
    {
        var countDestroyed = settings.Tracking.CountDestroyed;
        var o = new JsonObject
        {
            ["match_id"] = m.MatchKey,
            ["started_utc"] = m.StartedUtc.ToString("O", Inv),
            ["duration_seconds"] = (int)m.Length.TotalSeconds,
            ["fortnite_account"] = m.PlayerName,
            ["fortnite_account_hash"] = m.PlayerHash.Length >= 12 ? m.PlayerHash[..12] : m.PlayerHash,
            ["category"] = m.Category,
            ["chests"] = m.Effective(countDestroyed),
            ["replay_chests"] = m.ReplayCount(countDestroyed),
            ["searched"] = m.Searched,
            ["destroyed"] = m.Destroyed,
            ["ambiguous_not_counted"] = m.Ambiguous,
            ["vanished_with_no_record"] = m.NoRecord,
            ["in_statistics"] = m.Valid,
            ["left_out_by_you"] = m.Excluded,
            ["your_manual_taps"] = m.ManualTaps,
            ["replay_status"] = m.Status,
            ["fortnite_build"] = m.Build,
            ["counting_rules"] = m.Parser,
            ["chest_registry"] = m.Registry,
            ["replay_file"] = m.File,
            ["website"] = m.Upload,
        };
        if (corrections.Count > 0)
        {
            o["corrections"] = new JsonArray(corrections.Select(c => (JsonNode)new JsonObject
            {
                ["at_utc"] = DateTimeOffset.FromUnixTimeMilliseconds(c.At).UtcDateTime.ToString("O", Inv), ["by"] = c.Source,
                ["replay_count"] = c.ReplayCount, ["from"] = c.OldCount, ["to"] = c.NewCount, ["difference"] = c.Difference, ["reason"] = c.Reason,
            }).ToArray());
        }
        var events = Tracker.EventsOf(m);
        if (events.Count > 0) o["events"] = JsonSerializer.SerializeToNode(events.Select(EventJson), Json);
        else if (m.Events is null) o["events_note"] = "counted by an earlier version that did not keep the chest events";
        return o;
    }

    private static object EventJson(ChestEvent e) => new
    {
        time = Clock(e.Time), seconds = Math.Round(e.Time, 1), chest_type = e.Kind, chest_class = e.ClassName, chest_id = e.ChestId,
        what = e.Action.ToString().ToLowerInvariant(), evidence = e.Evidence.ToString().ToLowerInvariant(), counted = e.Counted,
        signals = e.Signals, rule = Rule(e), holding = e.Holding, reason = e.Reason, reopened_after_refill = e.Reopened ? true : (bool?)null,
        unverified_class = e.Unverified ? true : (bool?)null,
    };

    public static string Clock(float seconds) => $"{(int)seconds / 60:00}:{(int)seconds % 60:00}";

    /// <summary>The rule that classified an event, in one sentence.</summary>
    public static string Rule(ChestEvent e) => e.Evidence switch
    {
        Evidence.Confirmed => "The chest's own record shows it " + (e.Action == ChestAction.Destroyed ? "being destroyed" : "being opened") + " and names you.",
        Evidence.Corroborated when e.Signals.Contains(Signal.NoChestRecord) =>
            "Your own character is recorded searching a container here, and the replay holds no chest record for it. "
            + "Counted once. The replay does not say what kind of container it was.",
        Evidence.Corroborated when e.Signals.Contains(Signal.YourSearch) =>
            "The chest's record names nobody, but your own character is recorded searching at the moment it opened.",
        Evidence.Corroborated => "The chest's record names nobody, but your own hit on this chest is recorded just before it "
            + (e.Action == ChestAction.Destroyed ? "broke" : "opened") + ", and nobody else's is.",
        Evidence.Ambiguous => e.Reason ?? "The replay does not say who did this.",
        _ when e.Signals.Contains(Signal.CharacterNotRecorded) => e.Reason ?? "The replay holds almost nothing about your character for this stretch.",
        _ => "The replay holds no record for this chest.",
    };

    /// <summary>What a signal id means, for the audit viewer and the diagnostic file.</summary>
    public static string SignalText(string signal) => signal switch
    {
        Signal.NamedYou => "the game named you on the chest",
        Signal.NobodyNamed => "the game named nobody on the chest",
        Signal.OpenedState => "its state changed to opened",
        Signal.DestroyedState => "its state changed to destroyed",
        Signal.SeenLive => "seen as it happened",
        Signal.ArrivedWithChest => "the chest was not being tracked before; its state arrived with it",
        Signal.SearchAnimation => "its loot animation counter went up",
        Signal.Instant => "destroyed in one go (for example with what it stood on)",
        Signal.DamageRecord => "destroyed by ability or effect damage",
        Signal.YourHit => "your own hit on it is recorded just before",
        Signal.AbilityActive => "you were attacking with an area weapon or power",
        Signal.NoDetail => "the replay only shows it is gone",
        Signal.SeenIntact => "it had been seen closed earlier",
        Signal.CharacterNotRecorded => "the replay holds almost nothing about your character for this stretch",
        Signal.YourSearch => "your own character is recorded searching",
        Signal.NoChestRecord => "the replay has no chest record for it (type not recorded)",
        _ => signal,
    };

    /// <summary>
    /// A small file for one disputed match: what was read from the replay around a moment, and how each event was
    /// classified. Meant to be sent to whoever maintains the counting rules. It does not contain the replay.
    /// </summary>
    public static string Diagnostic(MatchRow match, List<CorrectionRow> corrections, Settings settings, float? aroundSeconds = null, float windowSeconds = 60)
    {
        var o = MatchJson(match, settings, corrections);
        o["diagnostic"] = new JsonObject
        {
            ["app"] = $"{AppInfo.Name} {AppInfo.Version}",
            ["made_at_utc"] = DateTime.UtcNow.ToString("O", Inv),
            ["note"] = "Chest events read from one replay. Not the replay itself. The counted total uses confirmed and corroborated events only.",
        };
        if (aroundSeconds is float at)
        {
            var events = Tracker.EventsOf(match).Where(e => Math.Abs(e.Time - at) <= windowSeconds).ToList();
            o["events"] = JsonSerializer.SerializeToNode(events.Select(EventJson), Json);
            o["diagnostic"]!["interval"] = $"{Clock(Math.Max(0, at - windowSeconds))} to {Clock(at + windowSeconds)}";
            o["diagnostic"]!["events_in_interval"] = events.Count;
            if (events.Count == 0) o["diagnostic"]!["interval_note"] = "The replay holds no chest record in this interval.";
        }
        return o.ToJsonString(Json);
    }

    public static void Write(string path, string text)
    {
        if (Path.GetDirectoryName(path) is { Length: > 0 } dir) Directory.CreateDirectory(dir);
        var temp = path + ".tmp";
        File.WriteAllText(temp, text, new UTF8Encoding(encoderShouldEmitUTF8Identifier: path.EndsWith(".csv", StringComparison.OrdinalIgnoreCase)));
        File.Move(temp, path, overwrite: true);
    }
}
