using System.Text.Json.Nodes;
using Microsoft.Data.Sqlite;
using Tokenify.Core;
using Tokenify.ReplayReader;

namespace Tokenify.Tests;

/// <summary>Statistics, sessions, the database, corrections, exports, and the tracker running on real replays.</summary>
public static partial class Tests
{
    private static IEnumerable<(string, Action)> CoreTests()
    {
        // ---- CPG and totals ----
        yield return ("CPG is chests in valid games divided by valid games", CpgBasic);
        yield return ("a real zero-chest game counts as a game", ZeroChestGameCounts);
        yield return ("with six games the recent average is LAST 6, not divided by ten", LastSix);
        yield return ("last 10 uses only the ten most recent games", LastTenWindow);
        yield return ("best game, last game and starting total", BestLastStarting);
        yield return ("trend needs enough games and a real difference", TrendRule);
        yield return ("partial, set-aside and other players' matches are not valid games", ValidityRules);
        yield return ("tracking start date leaves earlier matches out of statistics", StartDateRule);

        // ---- sessions ----
        yield return ("automatic session: games closer than the gap belong together", AutoSessionGroups);
        yield return ("automatic session ends after the gap; a match in progress starts a new one", AutoSessionEnds);
        yield return ("session time is match time, separate from the wall-clock span", SessionTimes);
        yield return ("manual session: only games after you started it", ManualSessions);

        // ---- Pirate Token figures ----
        yield return ("token chance follows 1 - (1 - 1/137500)^n", TokenChance);
        yield return ("average chests per token does not depend on chests already opened", TokenIndependence);

        // ---- display ----
        yield return ("overlay content: total is the big number, rows follow the chosen metrics", DisplayContent);
        yield return ("number and time formatting", Formatting);

        // ---- settings and shortcuts ----
        yield return ("settings survive a save and load, out-of-range values are clamped", SettingsRoundTrip);
        yield return ("shortcut text is parsed and normalised", HotkeyParsing);

        // ---- database ----
        yield return ("a database from TOKENIFY Companion 1.0 is upgraded without losing anything", MigrateV1);
        yield return ("the same match stored twice is one match (rename, copy, re-import)", StoreDedupe);
        yield return ("a correction keeps the replay count and its whole history", CorrectionHistory);
        yield return ("deleting history keeps settings; resetting settings keeps history", SeparateResets);

        // ---- exports and updates ----
        yield return ("CSV export: one line per match, cells safe for spreadsheets", CsvExport);
        yield return ("diagnostic file: events around a moment, never the replay or the account id", DiagnosticExport);
        yield return ("an update is accepted only with a valid signature", UpdateSignature);
        yield return ("update: an older signed build offered under a higher release number is refused", UpdateVersionBinding);
        yield return ("update: the release list is read safely (order, two-part tags, drafts, missing signature, junk)", UpdateReleaseList);
        yield return ("OBS page files are written and hold the current numbers", ObsFiles);

        // ---- the tracker itself, on real replays ----
        var current = Environment.GetEnvironmentVariable("TOKENIFY_REPLAY_CURRENT");
        if (string.IsNullOrEmpty(current) || !Directory.Exists(current))
        {
            yield return ("tracker end to end on real replays", () => throw new SkipException("set TOKENIFY_REPLAY_CURRENT to a folder of replays"));
        }
        else
        {
            yield return ("tracker end to end: import, duplicates, damaged files, corrections, statistics", () => TrackerEndToEnd(current));
        }
    }

    private static readonly DateTime T0 = new(2026, 11, 10, 12, 0, 0, DateTimeKind.Utc);

    private static GameStat G(int minutesFromStart, int chests, int lengthMinutes = 15, long id = 0) =>
        new(id == 0 ? minutesFromStart + 1 : id, T0.AddMinutes(minutesFromStart), T0.AddMinutes(minutesFromStart + lengthMinutes), chests);

    private static Stats S(IEnumerable<GameStat> games, StatsOptions? options = null, DateTime? now = null, IReadOnlyList<SessionRow>? manual = null) =>
        Stats.Compute(games, options ?? new StatsOptions(), now ?? T0.AddDays(1), manual);

    private static void Near(double expected, double? actual, string what)
    {
        if (actual is not double a || Math.Abs(a - expected) > 1e-9) throw new Exception($"{what}: expected {expected}, got {actual}");
    }

    // ------------------------------------------------------------------ CPG

    private static void CpgBasic()
    {
        var s = S([G(0, 40), G(20, 30), G(40, 50)]);
        Eq(3, s.Games, "games");
        Eq(120, s.TotalChests, "total");
        Near(40, s.Cpg, "cpg");
        True(Stats.Compute([], new StatsOptions(), T0).Cpg is null, "no games: no CPG (not zero)");
    }

    private static void ZeroChestGameCounts()
    {
        var s = S([G(0, 30), G(20, 0)]);
        Eq(2, s.Games, "games");
        Near(15, s.Cpg, "cpg");
        Eq(0, s.LastGame!.Chests, "last game");
    }

    private static void LastSix()
    {
        var s = S(Enumerable.Range(0, 6).Select(i => G(i * 20, 10 * (i + 1))));
        Eq(6, s.Last10.Games, "games used");
        Eq("LAST 6", s.Last10.Label, "label");
        Near(35, s.Last10.Cpg, "average of the six");
        Eq("LAST 6", s.Last25.Label, "last 25 label");
    }

    private static void LastTenWindow()
    {
        var games = Enumerable.Range(0, 14).Select(i => G(i * 20, i < 4 ? 100 : 20)).ToList();
        var s = S(games);
        Eq("LAST 10", s.Last10.Label, "label");
        Near(20, s.Last10.Cpg, "only the ten most recent");
        Near((4 * 100 + 10 * 20) / 14.0, s.Cpg, "all-time");
        Eq(14, s.Last25.Games, "last 25 uses all 14");
    }

    private static void BestLastStarting()
    {
        var s = S([G(0, 12), G(20, 47), G(40, 9)], new StatsOptions(StartingTotal: 1000));
        Eq(47, s.Best, "best");
        Eq(9, s.LastGame!.Chests, "last");
        Eq(1068, s.TotalChests, "total includes the starting total");
        Eq(68, s.TrackedChests, "tracked");
        Near(68 / 3.0, s.Cpg, "the starting total never changes CPG");
    }

    private static void TrendRule()
    {
        True(S(Enumerable.Range(0, 10).Select(i => G(i * 20, 50))).Trend is null, "too few games");
        var up = Enumerable.Range(0, 20).Select(i => G(i * 20, i < 10 ? 20 : 40)).ToList();
        Eq(1, S(up).Trend, "recent better");
        var down = Enumerable.Range(0, 20).Select(i => G(i * 20, i < 10 ? 40 : 20)).ToList();
        Eq(-1, S(down).Trend, "recent worse");
        Eq(0, S(Enumerable.Range(0, 20).Select(i => G(i * 20, 30))).Trend, "flat");
    }

    private static MatchRow M(long id, int chests, string status = "complete", string upload = Upload.Pending, string hash = "me", int destroyed = 0, int minute = 0) => new()
    {
        Id = id, PlayerHash = hash, Chests = chests, Searched = chests - destroyed, Destroyed = destroyed, Status = status, Upload = upload,
        Started = new DateTimeOffset(T0.AddMinutes(minute)).ToUnixTimeMilliseconds(), Ended = new DateTimeOffset(T0.AddMinutes(minute + 15)).ToUnixTimeMilliseconds(),
        SessionId = "session" + id.ToString("00000"), File = $"replay-{id}.replay", Parser = "2.0.0", Registry = 2,
    };

    private static void ValidityRules()
    {
        var settings = new Settings();
        var matches = new List<MatchRow>
        {
            M(1, 30), M(2, 0),                                   // valid, including a real zero
            M(3, 12, status: "partial", upload: Upload.HeldPartial), // partly read: not a game
            M(4, 25, upload: Upload.NotMine),                    // someone else's
            M(5, 40, hash: "other"),                             // another Fortnite account
            M(6, 18, upload: Upload.Ignored),
        };
        matches.Add(M(7, 22));
        matches[^1].Excluded = true;                             // set aside by the player
        var partialVouched = M(8, 12, status: "partial", upload: Upload.Pending);
        partialVouched.Corrected = 15;                           // partly read, but the player gave the count
        matches.Add(partialVouched);

        var games = Tracker.ValidGames(matches, "me", settings);
        Eq(3, games.Count, "valid games");
        Eq(45, games.Sum(g => g.Chests), "chests (30 + 0 + corrected 15)");
        Eq(Category.PartialReplay, matches[2].Category, "category of the partial");
        Eq(Category.UserCorrected, partialVouched.Category, "category once corrected");
        Eq(Category.ReplayConfirmed, matches[0].Category, "category of a normal match");

        var destroyed = M(9, 10, destroyed: 3);
        Eq(10, destroyed.Effective(countDestroyed: true), "destroyed counted");
        Eq(7, destroyed.Effective(countDestroyed: false), "destroyed not counted");
        destroyed.Corrected = 12;
        Eq(12, destroyed.Effective(countDestroyed: false), "a correction always wins");
    }

    private static void StartDateRule()
    {
        var settings = new Settings();
        var matches = new List<MatchRow> { M(1, 30, minute: 0), M(2, 20, minute: 60 * 24 * 3) };
        Eq(2, Tracker.ValidGames(matches, "me", settings).Count, "no start date");
        settings.Tracking.StartDate = matches[1].StartedLocal.AddMinutes(-5);
        var games = Tracker.ValidGames(matches, "me", settings);
        Eq(1, games.Count, "only the later match");
        Eq(20, games[0].Chests, "which");
    }

    // ------------------------------------------------------------------ sessions

    private static void AutoSessionGroups()
    {
        // Two sessions: three games in the morning, two in the evening.
        var games = new[] { G(0, 10), G(20, 20), G(45, 30), G(600, 5), G(620, 7) };
        var now = T0.AddMinutes(640);
        var s = S(games, new StatsOptions(SessionGapMinutes: 30), now);
        True(s.Session.Active && s.Session.Inferred, "active and inferred");
        Eq(2, s.Session.Games, "games in the current session");
        Eq(12, s.Session.Chests, "chests");
        Near(6, s.Session.Cpg, "session cpg");
        Eq(2, Stats.AllAutoSessions(games, TimeSpan.FromMinutes(30)).Count, "sessions in history");
        Eq(60, Stats.AllAutoSessions(games, TimeSpan.FromMinutes(30))[0].Chests, "first session");
    }

    private static void AutoSessionEnds()
    {
        var games = new[] { G(0, 10), G(20, 20) };
        var later = T0.AddMinutes(35 + 31);   // last match ended at +35
        var ended = S(games, new StatsOptions(SessionGapMinutes: 30), later);
        True(!ended.Session.Active, "over after the gap");
        Eq(0, ended.Session.Games, "no current session games");
        Eq(30, ended.TotalChests, "totals are untouched");

        var playing = S(games, new StatsOptions(SessionGapMinutes: 30, MatchInProgress: true), later);
        True(playing.Session.Active, "a match being played is a session");
        Eq(0, playing.Session.Games, "with no finished game yet");

        var within = S(games, new StatsOptions(SessionGapMinutes: 30), T0.AddMinutes(50));
        Eq(2, within.Session.Games, "still the same session inside the gap");
    }

    private static void SessionTimes()
    {
        var games = new[] { G(0, 10, lengthMinutes: 15), G(30, 20, lengthMinutes: 20) };   // 10 minutes of queue between them
        var s = S(games, new StatsOptions(SessionGapMinutes: 30), T0.AddMinutes(55));
        Eq(TimeSpan.FromMinutes(35), s.Session.MatchTime, "time in matches");
        Eq(TimeSpan.FromMinutes(50), s.Session.Span, "first start to last end");
    }

    private static void ManualSessions()
    {
        var games = new[] { G(0, 10), G(20, 20), G(40, 30) };
        var none = S(games, new StatsOptions(SessionMode: "manual"), T0.AddMinutes(60), []);
        True(!none.Session.Active && !none.Session.Inferred, "no session started");

        var started = new DateTimeOffset(T0.AddMinutes(15)).ToUnixTimeMilliseconds();
        var open = S(games, new StatsOptions(SessionMode: "manual"), T0.AddMinutes(60), [new SessionRow(1, started, null, null)]);
        True(open.Session.Active && !open.Session.Inferred, "active, marked by the player");
        Eq(2, open.Session.Games, "games since it started");
        Eq(50, open.Session.Chests, "chests");
        Eq(TimeSpan.FromMinutes(45), open.Session.Span, "span runs to now");

        var closed = S(games, new StatsOptions(SessionMode: "manual"), T0.AddMinutes(60), [new SessionRow(1, started, started + 60_000, null)]);
        True(!closed.Session.Active, "ended session is not current");
    }

    // ------------------------------------------------------------------ token

    private static void TokenChance()
    {
        Near(0, TokenOdds.ChanceIn(0), "zero chests");
        Near(1 - Math.Pow(1 - 1.0 / 137500, 1000), TokenOdds.ChanceIn(1000), "1,000 chests");
        Near(1 - Math.Pow(1 - 1.0 / 137500, 137500), TokenOdds.ChanceIn(137500), "137,500 chests (about 63%, not 100%)");
        True(TokenOdds.ChanceIn(137500) is > 0.63 and < 0.64, "about 63.2%");
        True(TokenOdds.ChanceIn(1_000_000) is > 0.999 and < 1, "a million chests is still not certain");
        Near(TokenOdds.ChanceIn(1068), S([G(0, 68)], new StatsOptions(StartingTotal: 1000)).Odds.Chance, "uses the total");
    }

    private static void TokenIndependence()
    {
        Eq(137_500, TokenOdds.AverageChestsPerToken, "average per token");
        // The chance of the next chest alone is the same after 10 or 100,000 chests.
        double Next(long n) => (TokenOdds.ChanceIn(n + 1) - TokenOdds.ChanceIn(n)) / (1 - TokenOdds.ChanceIn(n));
        True(Math.Abs(Next(10) - TokenOdds.PerChest) < 1e-12 && Math.Abs(Next(100_000) - TokenOdds.PerChest) < 1e-12, "next-chest chance is constant");
        var s = S(Enumerable.Range(0, 5).Select(i => G(i * 20, 40, lengthMinutes: 15)));
        Near(160, s.Odds.ChestsPerHour, "pace from match time (40 chests per 15 minutes)");
        True(s.Odds.TimePerToken is { TotalHours: > 859 and < 860 }, "137,500 / 160 hours on average");
    }

    // ------------------------------------------------------------------ display

    private static TrackerSnapshot Snap(Stats stats, Settings? settings = null, Activity activity = Activity.Ready, string status = "READY",
        SyncState sync = SyncState.NotLinked, SquadInfo? squad = null, int taps = 0, int queued = 0) =>
        new(settings ?? new Settings(), stats, activity, status, sync, "", sync != SyncState.NotLinked, null, null, true, "", FolderState.Ok, 0, queued, [], null, null,
            0, null, null, 0, null, null, 0, Tracker.DefaultSeasonStartUtc, 0, squad, taps);

    private static void DisplayContent()
    {
        var stats = S(Enumerable.Range(0, 6).Select(i => G(i * 20, 10 * (i + 1))), new StatsOptions(StartingTotal: 1074), T0.AddMinutes(125));
        var c = Display.Build(Snap(stats), Metric.Standard);
        Eq("TOTAL CHESTS", c.Hero.Label, "hero label");
        Eq("1,284", c.Hero.Value, "hero value");
        Eq("LAST GAME,AVG CPG,LAST 6", string.Join(",", c.Rows.Select(r => r.Label)), "rows of the standard preset");
        Eq("60,35.0,35.0", string.Join(",", c.Rows.Select(r => r.Value)), "values");
        Eq("READY", c.Status, "status");

        var minimal = Display.Build(Snap(stats), Metric.Minimal);
        Eq(1, minimal.Rows.Count, "minimal: total and CPG only");
        True(minimal.Status is null, "minimal has no status line");

        var live = Display.Build(Snap(stats, activity: Activity.InMatch, status: "MATCH IN PROGRESS"), Metric.Standard);
        Eq(StatusKind.Live, live.StatusKind, "live");
        Eq("1,284", live.Hero.Value, "the total does not move during a match");

        var squadHalf = Display.Build(Snap(stats, squad: new SquadInfo(96, false, 4, 2, null)), [Metric.Total, Metric.Squad]);
        True(squadHalf.Rows[0].Dim && squadHalf.Rows[0].Value == "96+" && squadHalf.Rows[0].Label == "SQUAD (2/4)", "an incomplete squad total is marked as such");
        var squadFull = Display.Build(Snap(stats, squad: new SquadInfo(140, true, 4, 4, null)), [Metric.Total, Metric.Squad]);
        True(!squadFull.Rows[0].Dim && squadFull.Rows[0].Value == "140", "a complete one is shown plainly");

        var toast = Display.Build(Snap(stats), Metric.Minimal, "+42 CHESTS");
        Eq("+42 CHESTS", toast.Toast, "toast");
        True(toast.Signature != minimal.Signature, "signature changes with the toast");
    }

    private static void Formatting()
    {
        Eq("1,284", Fmt.Int(1284), "thousands");
        Eq("36.8", Fmt.Cpg(36.84), "cpg");
        Eq("—", Fmt.Cpg(null), "no cpg");
        Eq("0.93%", Fmt.Percent(0.0093), "percent");
        Eq("<0.01%", Fmt.Percent(0.00001), "tiny percent");
        Eq("1h 05m", Fmt.Duration(TimeSpan.FromMinutes(65)), "duration");
        Eq("07:27", Exporter.Clock(447.1f), "clock");
    }

    // ------------------------------------------------------------------ settings

    private static void SettingsRoundTrip()
    {
        var s = new Settings();
        s.Overlay.Preset = "custom";
        s.Overlay.CustomMetrics = [Metric.Cpg, Metric.Best, "nonsense", Metric.Cpg];
        s.Overlay.BackgroundOpacity = 500;
        s.Overlay.Scale = 10;
        s.Overlay.Accent = "red";
        s.Overlay.Places["DISPLAY1"] = new OverlayPlace { X = 0.5, Y = double.NaN };
        s.Tracking.SessionGapMinutes = 1;
        s.Shortcuts.ToggleLock = "ctrl + shift + l";
        s.Shortcuts.ManualIncrement = "banana";
        var loaded = Settings.FromJson(s.ToJson());
        Eq(90, loaded.Overlay.BackgroundOpacity, "opacity clamped");
        Eq(50, loaded.Overlay.Scale, "scale clamped");
        Eq("#F6C24A", loaded.Overlay.Accent, "bad colour replaced");
        Eq("total,cpg,best", string.Join(",", loaded.Overlay.CustomMetrics), "metrics cleaned, total always present");
        Eq(0.30, loaded.Overlay.Places["DISPLAY1"].Y, "bad position replaced");
        Eq(5, loaded.Tracking.SessionGapMinutes, "gap clamped");
        Eq("Ctrl+Shift+L", loaded.Shortcuts.ToggleLock, "shortcut normalised");
        Eq("K", loaded.Shortcuts.ManualIncrement, "bad shortcut replaced with the default");
        True(new Settings().Overlay.Enabled, "the overlay is on by default");
        True(!new Settings().Shortcuts.ManualEnabled, "the manual key is off until the player turns it on");
        Eq("standard", Settings.FromJson("{ not json").Overlay.Preset, "unreadable settings fall back to defaults");
        Eq("standard", Settings.FromJson(null).Overlay.Preset, "no settings: defaults");
    }

    private static void HotkeyParsing()
    {
        var k = Hotkey.Parse("Ctrl+Shift+L")!;
        True(k is { Ctrl: true, Shift: true, Alt: false, Key: "L" } && k.VirtualKey == 'L', "ctrl+shift+l");
        Eq("Ctrl+Alt+F9", Hotkey.Parse("alt+ctrl+f9")!.ToString(), "order and case");
        Eq(0x78, Hotkey.Parse("F9")!.VirtualKey, "function key");
        True(!Hotkey.Parse("K")!.HasModifier, "a bare key has no modifier");
        True(Hotkey.Parse("Ctrl+") is null && Hotkey.Parse("Ctrl+A+B") is null && Hotkey.Parse("") is null, "nonsense is rejected");
        Eq("PageDown", Hotkey.NameOf(0x22), "name of a key code");
    }

    // ------------------------------------------------------------------ database

    private static string TempFile(string name)
    {
        var dir = Path.Combine(Path.GetTempPath(), "tokenify-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return Path.Combine(dir, name);
    }

    private static void MigrateV1()
    {
        var path = TempFile("companion.db");
        using (var db = new SqliteConnection($"Data Source={path};Pooling=False"))
        {
            db.Open();
            using var cmd = db.CreateCommand();
            // The schema TOKENIFY Companion 1.0.0 created, with two matches in it.
            cmd.CommandText = """
                create table kv (key text primary key, value text);
                create table files (name text primary key, size integer not null, mtime integer not null, state text not null,
                  checked_at integer not null, attempts integer not null default 0, error text, parser text,
                  registry integer not null default 0, match_id integer, started integer);
                create table matches (id integer primary key autoincrement, file text, replay_guid text, session_id text, match_key text not null,
                  player_hash text not null, player_name text, started integer not null, ended integer not null,
                  chests integer not null, breakdown text not null, times text not null, status text not null,
                  parser text not null, registry integer not null, build text, historical integer not null default 0,
                  upload text not null, server_id text, server_chests integer, server_status text,
                  attempts integer not null default 0, next_try integer not null default 0, last_error text,
                  created integer not null, updated integer not null, unique (player_hash, match_key));
                create table profiles (hash text primary key, name text, decision text not null, claimed integer not null default 0,
                  first_seen integer not null, last_seen integer not null, error text);
                insert into kv values ('device.id', 'd0a7f6f0-0000-4000-8000-000000000001'), ('demos.folder', 'D:\Replays'), ('tracking.paused', '1'), ('account.username', 'TESTER');
                insert into matches (file, session_id, match_key, player_hash, started, ended, chests, breakdown, times, status, parser, registry, upload, created, updated)
                values ('a.replay', 'aaaaaaaa1', 'fn:aaaaaaaa1', 'hash1', 1000, 2000, 11, '{"buried":9,"relic":1,"regular":1,"destroyed":1}', '[1,2]', 'complete', '1.0.0', 1, 'sent', 1, 1),
                       ('b.replay', 'bbbbbbbb2', 'fn:bbbbbbbb2', 'hash1', 3000, 4000, 0, '{}', '[]', 'complete', '1.0.0', 1, 'sent', 1, 1);
                insert into profiles values ('hash1', 'CJ', 'mine', 1, 1, 1, null);
                """;
            cmd.ExecuteNonQuery();
        }

        using var store = new Store(path);
        var matches = store.AllMatches();
        Eq(2, matches.Count, "matches kept");
        Eq(11, matches[0].Chests, "count kept");
        Eq(1, matches[0].Destroyed, "destroyed taken from the old breakdown");
        Eq(10, matches[0].Searched, "searched worked out");
        True(matches[0].Events is null && matches[0].Corrected is null && matches[0].CorrectionSynced, "new fields start empty");
        Eq(Upload.Sent, matches[0].Upload, "upload state kept");
        Eq("d0a7f6f0-0000-4000-8000-000000000001", store.Get("device.id"), "website link kept");
        var settings = store.LoadSettings();
        Eq(@"D:\Replays", settings.Tracking.ReplayFolder, "replay folder carried over");
        True(settings.Tracking.Paused, "paused carried over");
        Eq("mine", store.Profiles().Single().Decision, "Fortnite account choice kept");
        store.Dispose();
        using var again = new Store(path);   // opening twice must not migrate twice
        Eq(2, again.AllMatches().Count, "second open");
    }

    private static void StoreDedupe()
    {
        using var store = new Store(TempFile("t.db"));
        var m = M(0, 12);
        m.ReplayGuid = "guid-0001-abcd";
        var id = store.InsertMatch(m);
        // The same recording found again under another file name: recognised by its match id...
        var bySession = store.FindMatch("me", m.MatchKey, null);
        Eq(id, bySession!.Id, "found by match id");
        // ...or, if the match id were missing this time, by the replay's own id.
        Eq(id, store.FindMatch("me", "rg:guid-0001-abcd", "GUID-0001-ABCD")!.Id, "found by replay id");
        True(store.FindMatch("someone-else", m.MatchKey, null) is null, "another player's recording of the same match is a different record");
        var threw = false;
        try { store.InsertMatch(m); } catch (SqliteException) { threw = true; }
        True(threw, "the database itself refuses a second row for the same player and match");
        Eq(1, store.AllMatches().Count, "still one");

        var noIds = M(0, 3);
        noIds.SessionId = null;
        noIds.Fingerprint = "feedfacefeedfacefeedfacefeedface";
        Eq("fp:feedfacefeedfacefeedfacefeedface", noIds.MatchKey, "a replay without ids is known by its fingerprint");
        True(!noIds.HasServerIdentity, "and is kept local");
    }

    private static void CorrectionHistory()
    {
        using var store = new Store(TempFile("t.db"));
        var id = store.InsertMatch(M(0, 42));
        store.Correct(id, 43, "saw one more behind the wall", "you", 42);
        var m = store.GetMatch(id)!;
        Eq(42, m.Chests, "replay count untouched");
        Eq(43, m.Corrected, "corrected");
        Eq(43, m.Effective(true), "effective");
        Eq(Category.UserCorrected, m.Category, "category");
        True(!m.CorrectionSynced, "waits to be sent to the website");

        store.Correct(id, 44, null, "you", 42);
        store.Correct(id, null, "changed my mind", "you", 42);
        var history = store.Corrections(id);
        Eq(3, history.Count, "every change is kept");
        Eq("42>43,43>44,44>", string.Join(",", history.Select(h => $"{h.OldCount ?? h.ReplayCount}>{h.NewCount}")), "the trail");
        Eq(1, history[0].Difference, "difference from the replay");
        Eq("saw one more behind the wall", history[0].Reason, "reason");
        var back = store.GetMatch(id)!;
        True(back.Corrected is null && back.Effective(true) == 42 && back.Category == Category.ReplayConfirmed, "removing the correction returns to the replay count");

        // Statistics follow corrections.
        store.Correct(id, 50, null, "you", 42);
        var games = Tracker.ValidGames(store.AllMatchesLight(), "me", new Settings());
        Eq(50, games.Single().Chests, "statistics use the corrected count");
        True(games.Single().Corrected, "and know it was corrected");
    }

    private static void SeparateResets()
    {
        using var store = new Store(TempFile("t.db"));
        var settings = store.LoadSettings();
        settings.Overlay.Scale = 150;
        store.SaveSettings(settings);
        store.InsertMatch(M(0, 12));
        store.Set("device.id", "abc");

        store.ResetSettings();
        Eq(100, store.LoadSettings().Overlay.Scale, "settings back to default");
        Eq(1, store.AllMatches().Count, "history kept");

        settings.Overlay.Scale = 150;
        store.SaveSettings(settings);
        store.DeleteHistory();
        Eq(0, store.AllMatches().Count, "history gone");
        Eq(150, store.LoadSettings().Overlay.Scale, "settings kept");
        Eq("abc", store.Get("device.id"), "website link kept");
    }

    // ------------------------------------------------------------------ exports and updates

    private static void CsvExport()
    {
        var a = M(1, 42, destroyed: 3);
        a.PlayerName = "=cmd|calc";            // a name that a spreadsheet would run as a formula
        a.Corrected = 43;
        a.CorrectionReason = "one more, \"behind\" the wall";
        var b = M(2, 0, minute: 30);
        var csv = Exporter.Csv([b, a], new Settings());
        var lines = csv.Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(l => l.TrimEnd('\r')).ToList();
        Eq(3, lines.Count, "header and two matches");
        True(lines[0].StartsWith("match,started_local", StringComparison.Ordinal), "header");
        True(lines[1].StartsWith("1,", StringComparison.Ordinal) && lines[2].StartsWith("2,", StringComparison.Ordinal), "oldest first, numbered");
        True(lines[1].Contains(",'=cmd|calc,", StringComparison.Ordinal), "formula made harmless");
        True(lines[1].Contains("\"one more, \"\"behind\"\" the wall\"", StringComparison.Ordinal), "quotes and commas escaped");
        True(lines[1].Contains(",User Corrected,43,42,39,3,", StringComparison.Ordinal), "category, count, replay count, searched, destroyed");
        True(lines[2].Contains(",Replay Confirmed,0,0,", StringComparison.Ordinal), "a zero-chest game is a line like any other");
    }

    private static void DiagnosticExport()
    {
        var m = M(1, 2);
        m.PlayerHash = new string('a', 64);
        m.PlayerName = "CJ";
        m.Events = """
            [{"time":437.9,"kind":"regular","className":"AlwaysSpawn_NormalChest","chestId":"k1","action":"searched","evidence":"confirmed","counted":true,"signals":["named-you","opened-state"],"holding":"Gum-Gum Fruit"},
             {"time":447.1,"kind":"regular","className":"Tiered_Chest_Athena","chestId":"k2","action":"removed","evidence":"ambiguous","counted":false,"signals":["no-detail","ability-active"],"reason":"It disappeared while you were attacking."},
             {"time":900,"kind":"relic","className":"B_TieredChest_Relics","chestId":"k3","action":"destroyed","evidence":"confirmed","counted":true,"signals":["named-you","destroyed-state"]}]
            """;
        var events = Tracker.EventsOf(m);
        Eq(3, events.Count, "events read back");
        Eq(Evidence.Ambiguous, events[1].Evidence, "evidence class kept");

        var text = Exporter.Diagnostic(m, [], new Settings(), aroundSeconds: 447f, windowSeconds: 60);
        var o = JsonNode.Parse(text)!.AsObject();
        Eq(2, o["events"]!.AsArray().Count, "only the events around that moment");
        Eq("07:27", o["events"]![1]!["time"]!.GetValue<string>(), "readable time");
        Eq("ambiguous", o["events"]![1]!["evidence"]!.GetValue<string>(), "evidence");
        True(o["events"]![0]!["rule"]!.GetValue<string>().Contains("names you", StringComparison.Ordinal), "the rule that classified it");
        Eq(12, o["fortnite_account_hash"]!.GetValue<string>().Length, "account hash is shortened");
        True(!text.Contains(new string('a', 64), StringComparison.Ordinal), "the full account hash is not in the file");

        var empty = JsonNode.Parse(Exporter.Diagnostic(m, [], new Settings(), aroundSeconds: 600f, windowSeconds: 30))!;
        Eq(0, empty["events"]!.AsArray().Count, "an interval with no record says so");
        True(empty["diagnostic"]!["interval_note"] is not null, "and does not invent one");
    }

    private static void UpdateVersionBinding()
    {
        var running = new Version(2, 0, 0);
        True(Updater.AcceptableVersion(new Version(2, 0, 1), new Version(2, 0, 1), running), "a newer build offered as itself is accepted");
        True(!Updater.AcceptableVersion(new Version(2, 0, 0), new Version(9, 9, 9), running), "the running version re-offered under a higher number");
        True(!Updater.AcceptableVersion(new Version(1, 0, 0), new Version(9, 9, 9), running), "an old build under a higher number");
        True(!Updater.AcceptableVersion(new Version(2, 0, 1), new Version(2, 0, 2), running), "a build that is not the version it is offered as");
        True(!Updater.AcceptableVersion(new Version(1, 5, 0), new Version(1, 5, 0), running), "an honestly labelled older build");
        True(!Updater.AcceptableVersion(null, new Version(2, 0, 1), running), "a file with no version in it");
        True(Updater.AcceptableVersion(new Version(2, 1, 0, 0), new Version(2, 1), running), "2.1.0.0 in the file is release 2.1");
        Eq(new Version(2, 1, 0), Updater.Normalize(new Version(2, 1)), "two parts become three");
        Eq(new Version(2, 1, 3), Updater.Normalize(new Version(2, 1, 3, 9)), "a fourth part is dropped");
        // the number built into a real program file is read; this test program is built with the app's own number
        Eq(Updater.CurrentVersion, Updater.VersionOf(Environment.ProcessPath!), "version read from a program file");
        Eq(null, Updater.VersionOf(TempFile("not-a-program.exe")), "a missing file has no version");
    }

    private static void UpdateReleaseList()
    {
        static JsonObject Release(string tag, bool draft = false, bool pre = false, bool sig = true, bool exe = true)
        {
            var assets = new JsonArray();
            if (exe) assets.Add(new JsonObject { ["name"] = AppInfo.ExeName, ["browser_download_url"] = "https://github.com/x/" + tag + ".exe", ["size"] = 50_000_000 });
            if (sig) assets.Add(new JsonObject { ["name"] = AppInfo.ExeName + ".sig", ["browser_download_url"] = "https://github.com/x/" + tag + ".sig", ["size"] = 96 });
            return new JsonObject { ["tag_name"] = tag, ["draft"] = draft, ["prerelease"] = pre, ["body"] = "notes", ["html_url"] = "https://github.com/x/releases/" + tag, ["assets"] = assets };
        }
        var current = new Version(2, 0, 0);
        // through JSON text first, exactly as GitHub's answer arrives
        static UpdateInfo? Pick(Version current, params JsonNode?[] releases) =>
            Updater.Newest((JsonArray)JsonNode.Parse(new JsonArray(releases).ToJsonString())!, current);
        Eq(null, Pick(current)?.Version, "no releases");
        Eq(null, Pick(current, Release("tracker-v2.0.0"), Release("tracker-v1.9.0"), Release("companion-v9.0.0"))?.Version,
            "nothing newer; another program's releases are ignored");
        Eq("2.1.0", Pick(current, Release("tracker-v2.0.1"), Release("tracker-v2.1.0"), Release("tracker-v2.0.5"))?.Version, "the newest wins, whatever the order");
        Eq("2.1.0", Pick(current, Release("tracker-v2.1"))?.Version, "a two-part tag is 2.1.0, not a crash");
        Eq("2.0.1", Pick(current, Release("tracker-v3.0.0", draft: true), Release("tracker-v2.5.0", pre: true), Release("tracker-v2.2.0", sig: false),
            Release("tracker-v2.3.0", exe: false), Release("tracker-v2.0.1"))?.Version, "drafts, pre-releases and releases without both files are not offered");
        Eq("2.0.1", Pick(current, JsonValue.Create("junk"), new JsonObject { ["tag_name"] = 7 }, new JsonObject { ["tag_name"] = "tracker-v2.9.0", ["assets"] = "none" },
            Release("tracker-vX.Y"), Release("tracker-v2.0.1"))?.Version, "entries that make no sense are skipped");
        var found = Pick(current, Release("tracker-v2.0.1"))!;
        True(found.ExeUrl.EndsWith(".exe", StringComparison.Ordinal) && found.SignatureUrl.EndsWith(".sig", StringComparison.Ordinal) && found.Size == 50_000_000, "both files and the size are taken from the release");
    }

    private static void UpdateSignature()
    {
        var file = TempFile("TOKENIFY-Tracker.exe");
        File.WriteAllBytes(file, [1, 2, 3, 4, 5, 6, 7, 8]);
        var (pem, pub) = Updater.NewKey();
        var signature = Updater.Sign(file, pem);
        True(Updater.Verify(file, signature, pub), "a file signed with the key is accepted");
        True(!Updater.Verify(file, signature), "the same signature does not match the release key built into the app");
        File.WriteAllBytes(file, [1, 2, 3, 4, 5, 6, 7, 9]);
        True(!Updater.Verify(file, signature, pub), "a changed file is refused");
        True(!Updater.Verify(file, "not a signature", pub) && !Updater.Verify(file, "", pub), "garbage is refused");
        var (_, otherPub) = Updater.NewKey();
        True(!Updater.Verify(file, Updater.Sign(file, pem), otherPub), "a signature from another key is refused");
        True(Updater.PublicKey.Length > 80 && !Updater.PublicKey.Contains('@'), "the release key is built in");
    }

    private static void ObsFiles()
    {
        var settings = new Settings();
        settings.General.ObsPage = true;
        var stats = S([G(0, 42), G(20, 30)], new StatsOptions(StartingTotal: 1212), T0.AddMinutes(40));
        ObsPage.Update(Snap(stats, settings));
        True(File.Exists(AppPaths.ObsPage) && File.Exists(ObsPage.DataPath), "both files exist");
        var data = File.ReadAllText(ObsPage.DataPath);
        True(data.StartsWith("window.TOKENIFY_DATA = {", StringComparison.Ordinal), "data file shape");
        True(data.Contains("\"1,284\"", StringComparison.Ordinal) && data.Contains("LAST 2", StringComparison.Ordinal), "current numbers");
        var page = File.ReadAllText(AppPaths.ObsPage);
        True(page.Contains("background: transparent", StringComparison.Ordinal), "transparent background");
        True(!page.Contains("http://", StringComparison.Ordinal) && !page.Contains("https://", StringComparison.Ordinal), "the page loads nothing from the internet");
    }

    // ------------------------------------------------------------------ the tracker on real replays

    private static void WaitFor(Func<bool> condition, int seconds, string what, Func<string>? detail = null)
    {
        var until = DateTime.UtcNow.AddSeconds(seconds);
        while (DateTime.UtcNow < until)
        {
            if (condition()) return;
            Thread.Sleep(200);
        }
        throw new Exception("timed out waiting for " + what + (detail is null ? "" : " [" + detail() + "]"));
    }

    private static void TrackerEndToEnd(string replays)
    {
        // Samples of the current season (see tests/fixtures/README.md), copied under the kind of name Fortnite gives.
        string Src(string number) => Path.Combine(replays, $"season43-{number}.replay");
        string Demo(string number) => $"UnsavedReplay-2026.11.10-10.00.{number}.replay";
        foreach (var n in new[] { "03", "08", "01", "05" })
        {
            if (!File.Exists(Src(n))) throw new SkipException("replay not present: season43-" + n);
        }

        var demos = Path.GetDirectoryName(TempFile("x"))!;
        File.Copy(Src("03"), Path.Combine(demos, Demo("03")));   // 11 chests (1 destroyed)
        File.Copy(Src("08"), Path.Combine(demos, Demo("08")));   // 0 chests
        File.Copy(Src("01"), Path.Combine(demos, Demo("01")));   // 2 chests (1 destroyed), 4 ambiguous

        using var store = new Store(TempFile("companion.db"));
        store.Set("history.cutoff", "0");   // these replays are "new", not history
        var settings = store.LoadSettings();
        settings.Tracking.ReplayFolder = demos;
        settings.General.SetupDone = true;
        store.SaveSettings(settings);

        using var tracker = new Tracker(store);
        var imported = new List<(string File, int Chests)>();
        tracker.MatchImported += (m, n) => { lock (imported) imported.Add((m.File, n)); };
        tracker.Start();

        // 1. every finished replay becomes exactly one match, with no account and no website
        WaitFor(() => store.AllMatchesLight().Count == 3, 90, "three matches");
        var snap = tracker.Snapshot();
        Eq(3, snap.Stats.Games, "valid games (the zero-chest game is one of them)");
        Eq(13, snap.Stats.TotalChests, "total chests");
        Near(13 / 3.0, snap.Stats.Cpg, "CPG");
        Eq(11, snap.Stats.Best, "best game");
        Eq("LAST 3", snap.Stats.Last10.Label, "recent label");
        Eq(SyncState.NotLinked, snap.Sync, "works without a TOKENIFY account");
        Eq(0, snap.Queued, "nothing queued without an account");
        Eq(1, snap.NeedsReview, "the match with ambiguous chests is flagged for review");
        True(snap.ActiveProfile is { Decision: "mine" }, "the Fortnite account is recognised");
        lock (imported) Eq(3, imported.Count, "each match announced once");
        var byFile = store.AllMatches().ToDictionary(m => m.File[^9..^7]);   // the sample's number
        Eq(4, byFile["01"].Ambiguous, "ambiguous chests are stored");
        Eq(2, byFile["01"].Chests, "and are not in the count");
        Eq(6, Tracker.EventsOf(byFile["01"]).Count, "the events are kept with the match");
        Eq(1, byFile["03"].Destroyed, "destroyed chests are told apart");

        // 2. a renamed copy and a rescan never add a match
        File.Copy(Src("03"), Path.Combine(demos, "my best game (copy).replay"));
        tracker.Rescan();
        WaitFor(() => store.Files().Count(f => f.State == "done") == 4, 90, "the copy and the rescan to be read");
        Eq(3, store.AllMatchesLight().Count, "still three matches");
        Eq(13, tracker.Snapshot().Stats.TotalChests, "total unchanged");
        lock (imported) Eq(3, imported.Count, "nothing announced twice");

        // 3. a damaged file and a cut-off file are never a zero-chest game
        File.WriteAllBytes(Path.Combine(demos, "garbage.replay"), new byte[5000]);
        File.SetLastWriteTimeUtc(Path.Combine(demos, "garbage.replay"), DateTime.UtcNow.AddHours(-1));
        var cut = File.ReadAllBytes(Src("03"));
        var cutPath = Path.Combine(demos, Demo("05"));
        File.WriteAllBytes(cutPath, File.ReadAllBytes(Src("05"))[..(cut.Length / 2)]);
        File.SetLastWriteTimeUtc(cutPath, new DateTime(2026, 11, 10, 19, 5, 0, DateTimeKind.Utc));
        tracker.ScanNow();
        WaitFor(() => store.GetFile("garbage.replay")?.State == "failed" && store.GetFile(Path.GetFileName(cutPath)) is { State: "done" or "failed" }, 90, "the damaged files to be handled",
            () => string.Join("; ", store.Files().Select(f => $"{(f.Name.Length > 22 ? f.Name[^22..] : f.Name)}={f.State}{(f.Error is null ? "" : " (" + f.Error + ")")}")));
        snap = tracker.Snapshot();
        Eq(3, snap.Stats.Games, "damaged files add no game");
        Near(13 / 3.0, snap.Stats.Cpg, "CPG unchanged");
        var partial = store.AllMatchesLight().FirstOrDefault(m => m.Partial);
        if (partial is not null)
        {
            Eq(Category.PartialReplay, partial.Category, "a cut-off replay is a partial replay");
            True(!partial.Valid, "and not a valid game until the player decides");
        }

        // 4. corrections change the statistics, never the replay's own count
        var two = byFile["01"];
        tracker.Correct(two.Id, 6, "the four ship chests were mine");
        snap = tracker.Snapshot();
        Eq(17, snap.Stats.TotalChests, "total follows the correction");
        Eq(0, snap.NeedsReview, "a corrected match is reviewed");
        Eq(2, store.GetMatch(two.Id)!.Chests, "replay count kept");
        Eq(Category.UserCorrected, store.GetMatch(two.Id)!.Category, "category");
        tracker.Correct(two.Id, null, null);
        Eq(13, tracker.Snapshot().Stats.TotalChests, "removing it restores the replay count");

        // 5. choices: leave a match out, don't count destroyed chests
        tracker.SetExcluded(byFile["08"].Id, true);
        Eq(2, tracker.Snapshot().Stats.Games, "a match set aside is not a game");
        tracker.SetExcluded(byFile["08"].Id, false);
        tracker.UpdateSettings(s => s.Tracking.CountDestroyed = false);
        Eq(11, tracker.Snapshot().Stats.TotalChests, "without destroyed chests: 10 + 0 + 1");
        tracker.UpdateSettings(s => s.Tracking.CountDestroyed = true);
        Eq(13, tracker.Snapshot().Stats.TotalChests, "and back");

        // 6. everything survives a restart
        tracker.Dispose();
        using var reopened = new Tracker(store);
        var after = reopened.Snapshot();
        Eq(13, after.Stats.TotalChests, "total after restart");
        Eq(3, after.Stats.Games, "games after restart");
        Eq(demos, after.Settings.Tracking.ReplayFolder, "settings after restart");

        // 7. history survives the replays being deleted
        foreach (var f in Directory.GetFiles(demos, "*.replay")) File.Delete(f);
        Eq(13, reopened.Snapshot().Stats.TotalChests, "total after the replays are gone");
        Eq(6, Tracker.EventsOf(store.GetMatch(two.Id)!).Count, "evidence still there");
    }
}
