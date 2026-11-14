using Microsoft.Data.Sqlite;

namespace Tokenify.Core;

/// <summary>What the tracker knows about one replay file on disk.</summary>
/// <param name="State">new, recording, done, failed, unsupported, held (old replay awaiting the history choice), skipped, pre-season.</param>
public sealed record FileRow(string Name, long Size, long MTime, string State, long CheckedAt, int Attempts, string? Error,
    string? Parser, int Registry, long? MatchId, long? Started);

/// <summary>Upload states for a parsed match.</summary>
public static class Upload
{
    public const string Pending = "pending";          // waiting to be sent (or simply kept, when no website is linked)
    public const string Sent = "sent";                // TOKENIFY has it
    public const string HeldProfile = "held-profile"; // whose Fortnite account is this? (asked once)
    public const string HeldPartial = "held-partial"; // only partly readable: used only if the player chooses
    public const string HeldHistory = "held-history"; // an old replay: waits for the import choice
    public const string NotMine = "not-mine";         // another player's account on this PC: never counted or sent
    public const string Conflict = "conflict";        // that Fortnite account is linked to another TOKENIFY account
    public const string Rejected = "rejected";        // the server refused it (shown with the reason)
    public const string Ignored = "ignored";          // the player dismissed a partial/failed match
    public const string LocalOnly = "local-only";     // has no id the website accepts: counted here, never sent
}

/// <summary>How a match's count came about. Shown next to every match; never blurred together.</summary>
public static class Category
{
    public const string ReplayConfirmed = "Replay Confirmed";
    public const string UserCorrected = "User Corrected";
    public const string PartialReplay = "Partial Replay";
    public const string UnsupportedReplay = "Unsupported Replay";
}

public sealed class MatchRow
{
    public long Id { get; set; }
    public string File { get; set; } = "";
    public string? ReplayGuid { get; set; }
    public string? SessionId { get; set; }
    /// <summary>Hash of the replay's own header, used as identity only when it carries neither a match nor a replay id.</summary>
    public string? Fingerprint { get; set; }
    public string PlayerHash { get; set; } = "";
    public string? PlayerName { get; set; }
    public long Started { get; set; }
    public long Ended { get; set; }
    /// <summary>What the replay shows: searched + destroyed.</summary>
    public int Chests { get; set; }
    public int Searched { get; set; }
    public int Destroyed { get; set; }
    public int Ambiguous { get; set; }
    public int NoRecord { get; set; }
    /// <summary>JSON array of the chest events with their evidence. Kept after Fortnite deletes the replay.</summary>
    public string? Events { get; set; }
    public string Breakdown { get; set; } = "{}";
    public string Times { get; set; } = "[]";
    public string Status { get; set; } = "complete";
    public string Parser { get; set; } = "";
    public int Registry { get; set; }
    public string? Build { get; set; }
    public bool Historical { get; set; }
    public string Upload { get; set; } = Core.Upload.Pending;
    public string? ServerId { get; set; }
    public int? ServerChests { get; set; }
    public string? ServerStatus { get; set; }
    public int Attempts { get; set; }
    public long NextTry { get; set; }
    public string? LastError { get; set; }
    public long Created { get; set; }
    public long Updated { get; set; }

    /// <summary>The player's own count for this match, when they corrected it. The replay count is never overwritten.</summary>
    public int? Corrected { get; set; }
    public long? CorrectedAt { get; set; }
    public string? CorrectionReason { get; set; }
    /// <summary>you (made here) or website.</summary>
    public string? CorrectionSource { get; set; }
    public bool CorrectionSynced { get; set; } = true;
    public bool Excluded { get; set; }
    /// <summary>The player has looked at this match's ambiguous events and decided.</summary>
    public bool Reviewed { get; set; }
    /// <summary>Taps of the optional manual shortcut during this match: the player's own running count, for comparison only.</summary>
    public int? ManualTaps { get; set; }

    public string MatchKey => SessionId is { Length: > 0 } s ? "fn:" + s.ToLowerInvariant()
        : ReplayGuid is { Length: > 0 } g ? "rg:" + g.ToLowerInvariant()
        : "fp:" + (Fingerprint ?? "");

    public bool HasServerIdentity => SessionId is { Length: > 0 } || ReplayGuid is { Length: > 0 };
    public DateTime StartedLocal => DateTimeOffset.FromUnixTimeMilliseconds(Started).LocalDateTime;
    public DateTime StartedUtc => DateTimeOffset.FromUnixTimeMilliseconds(Started).UtcDateTime;
    public DateTime EndedUtc => DateTimeOffset.FromUnixTimeMilliseconds(Ended).UtcDateTime;
    public TimeSpan Length => TimeSpan.FromMilliseconds(Math.Max(0, Ended - Started));
    public bool Partial => Status != "complete";

    /// <summary>The replay's count under the player's "count destroyed chests" choice.</summary>
    public int ReplayCount(bool countDestroyed) => countDestroyed ? Chests : Math.Max(0, Chests - Destroyed);

    /// <summary>The count used everywhere: the player's correction when there is one, otherwise the replay's.</summary>
    public int Effective(bool countDestroyed) => Corrected ?? ReplayCount(countDestroyed);

    public string Category => Corrected is not null ? Core.Category.UserCorrected : Partial ? Core.Category.PartialReplay : Core.Category.ReplayConfirmed;

    /// <summary>
    /// A game that belongs in the statistics: the player's own, not set aside, and either read completely or
    /// vouched for by the player. A partly read replay is never a valid game on its own, and never a zero.
    /// </summary>
    public bool Valid => !Excluded && Upload is not (Core.Upload.NotMine or Core.Upload.HeldProfile or Core.Upload.Ignored or Core.Upload.HeldHistory)
        && (!Partial || Corrected is not null);
}

/// <param name="Decision">mine, not-mine or ask.</param>
public sealed record ProfileRow(string Hash, string? Name, string Decision, bool Claimed, long FirstSeen, long LastSeen, string? Error);

/// <summary>One change to a match's count. Nothing here is ever edited or deleted with the match's replay data.</summary>
public sealed record CorrectionRow(long Id, long MatchId, int ReplayCount, int? OldCount, int? NewCount, string? Reason, string Source, long At)
{
    public int? Difference => NewCount is int n ? n - ReplayCount : null;
}

/// <summary>A session the player started (and ended) by hand.</summary>
public sealed record SessionRow(long Id, long Started, long? Ended, string? Note);

/// <summary>
/// Local SQLite database: processed replay files, parsed matches with their evidence, corrections, sessions,
/// Fortnite account choices, the upload queue and settings. Matches stay here after Fortnite deletes the replay,
/// so nothing already counted is ever lost. All access is serialised through one connection.
/// </summary>
public sealed class Store : IDisposable
{
    private const int SchemaVersion = 2;
    private readonly SqliteConnection _db;
    private readonly object _gate = new();

    public Store(string path)
    {
        if (Path.GetDirectoryName(path) is { Length: > 0 } dir) Directory.CreateDirectory(dir);
        _db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Mode = SqliteOpenMode.ReadWriteCreate, Cache = SqliteCacheMode.Private }.ToString());
        _db.Open();
        Exec("pragma journal_mode = wal; pragma synchronous = normal; pragma busy_timeout = 5000;");
        Exec("""
            create table if not exists kv (key text primary key, value text);
            create table if not exists files (
              name text primary key, size integer not null, mtime integer not null, state text not null,
              checked_at integer not null, attempts integer not null default 0, error text, parser text,
              registry integer not null default 0, match_id integer, started integer);
            create table if not exists matches (
              id integer primary key autoincrement, file text, replay_guid text, session_id text, match_key text not null,
              player_hash text not null, player_name text, started integer not null, ended integer not null,
              chests integer not null, breakdown text not null, times text not null, status text not null,
              parser text not null, registry integer not null, build text, historical integer not null default 0,
              upload text not null, server_id text, server_chests integer, server_status text,
              attempts integer not null default 0, next_try integer not null default 0, last_error text,
              created integer not null, updated integer not null,
              unique (player_hash, match_key));
            create index if not exists matches_upload on matches (upload, next_try);
            create table if not exists profiles (
              hash text primary key, name text, decision text not null, claimed integer not null default 0,
              first_seen integer not null, last_seen integer not null, error text);
            """);
        Migrate();
    }

    /// <summary>Brings a database made by an older version (TOKENIFY Companion 1.x) up to date without losing anything.</summary>
    private void Migrate()
    {
        var version = Query("pragma user_version", r => Convert.ToInt32(r[0])).First();
        if (version >= SchemaVersion) return;

        foreach (var column in new[]
                 {
                     "searched integer", "destroyed integer not null default 0", "ambiguous integer not null default 0",
                     "no_record integer not null default 0", "events text", "fingerprint text", "corrected integer", "corrected_at integer",
                     "correction_reason text", "correction_source text", "correction_synced integer not null default 1",
                     "excluded integer not null default 0", "manual_taps integer", "reviewed integer not null default 0",
                 })
        {
            try { Exec("alter table matches add column " + column); }
            catch (SqliteException ex) when (ex.Message.Contains("duplicate column", StringComparison.OrdinalIgnoreCase)) { }
        }

        Exec("""
            update matches set destroyed = coalesce(json_extract(breakdown, '$.destroyed'), 0) where searched is null;
            update matches set searched = max(0, chests - destroyed) where searched is null;
            create table if not exists corrections (
              id integer primary key autoincrement, match_id integer not null, replay_count integer not null,
              old_count integer, new_count integer, reason text, source text not null, at integer not null);
            create index if not exists corrections_match on corrections (match_id, at);
            create table if not exists sessions (id integer primary key autoincrement, started integer not null, ended integer, note text);
            create index if not exists matches_started on matches (started);
            """);
        Exec($"pragma user_version = {SchemaVersion}");
    }

    public static long Now() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

    private void Exec(string sql, params (string, object?)[] args)
    {
        lock (_gate)
        {
            using var cmd = _db.CreateCommand();
            cmd.CommandText = sql;
            foreach (var (name, value) in args) cmd.Parameters.AddWithValue(name, value ?? DBNull.Value);
            cmd.ExecuteNonQuery();
        }
    }

    private List<T> Query<T>(string sql, Func<SqliteDataReader, T> map, params (string, object?)[] args)
    {
        lock (_gate)
        {
            using var cmd = _db.CreateCommand();
            cmd.CommandText = sql;
            foreach (var (name, value) in args) cmd.Parameters.AddWithValue(name, value ?? DBNull.Value);
            using var r = cmd.ExecuteReader();
            var list = new List<T>();
            while (r.Read()) list.Add(map(r));
            return list;
        }
    }

    private static string? S(SqliteDataReader r, string c) => r[c] is DBNull ? null : Convert.ToString(r[c]);
    private static long L(SqliteDataReader r, string c) => r[c] is DBNull ? 0 : Convert.ToInt64(r[c]);
    private static long? LN(SqliteDataReader r, string c) => r[c] is DBNull ? null : Convert.ToInt64(r[c]);
    private static int? IN(SqliteDataReader r, string c) => r[c] is DBNull ? null : Convert.ToInt32(r[c]);

    // ------------------------------------------------------------------------------- settings
    public string? Get(string key) => Query("select value from kv where key = $k", r => S(r, "value"), ("$k", key)).FirstOrDefault();

    public void Set(string key, string? value)
    {
        if (value is null) Exec("delete from kv where key = $k", ("$k", key));
        else Exec("insert into kv (key, value) values ($k, $v) on conflict (key) do update set value = excluded.value", ("$k", key), ("$v", value));
    }

    public bool Flag(string key, bool fallback = false) => Get(key) is { } v ? v == "1" : fallback;
    public void SetFlag(string key, bool value) => Set(key, value ? "1" : "0");

    private const string SettingsKey = "settings.v2";

    public Settings LoadSettings()
    {
        var saved = Get(SettingsKey);
        var s = Settings.FromJson(saved);
        if (saved is null)
        {
            // First run after TOKENIFY Companion 1.x: carry over the choices it had.
            s.Tracking.ReplayFolder = Get("demos.folder") is { Length: > 0 } f ? f : null;
            s.Tracking.Paused = Flag("tracking.paused");
            s.General.NotifyOnImport = Flag("notify.synced", true);
        }
        return s;
    }

    public void SaveSettings(Settings settings) => Set(SettingsKey, settings.Sanitized().ToJson());

    /// <summary>Back to defaults. History, the website link and Fortnite account choices are untouched.</summary>
    public void ResetSettings() => Set(SettingsKey, null);

    // ------------------------------------------------------------------------------- files
    private static FileRow MapFile(SqliteDataReader r) => new(S(r, "name")!, L(r, "size"), L(r, "mtime"), S(r, "state")!, L(r, "checked_at"),
        (int)L(r, "attempts"), S(r, "error"), S(r, "parser"), (int)L(r, "registry"), LN(r, "match_id"), LN(r, "started"));

    public FileRow? GetFile(string name) => Query("select * from files where name = $n", MapFile, ("$n", name)).FirstOrDefault();
    public List<FileRow> Files() => Query("select * from files order by mtime desc", MapFile);

    public void PutFile(FileRow f) => Exec("""
        insert into files (name, size, mtime, state, checked_at, attempts, error, parser, registry, match_id, started)
        values ($n, $s, $m, $st, $c, $a, $e, $p, $r, $id, $start)
        on conflict (name) do update set size = excluded.size, mtime = excluded.mtime, state = excluded.state,
          checked_at = excluded.checked_at, attempts = excluded.attempts, error = excluded.error, parser = excluded.parser,
          registry = excluded.registry, match_id = excluded.match_id, started = coalesce(excluded.started, files.started)
        """, ("$n", f.Name), ("$s", f.Size), ("$m", f.MTime), ("$st", f.State), ("$c", f.CheckedAt), ("$a", f.Attempts),
        ("$e", f.Error), ("$p", f.Parser), ("$r", f.Registry), ("$id", f.MatchId), ("$start", f.Started));

    public void SetFileState(string state, string fromState) =>
        Exec("update files set state = $to where state = $from", ("$to", state), ("$from", fromState));

    /// <summary>
    /// "Rescan replays": forgets which files were read, nothing else. Replays still on disk are read again; a match
    /// already in the history is recognised by its id and updated in place, never added twice. Corrections stay.
    /// </summary>
    public void ForgetFiles() => Exec("delete from files where state not in ('held', 'skipped')");

    // ------------------------------------------------------------------------------- matches
    private static MatchRow MapMatch(SqliteDataReader r) => new()
    {
        Id = L(r, "id"), File = S(r, "file") ?? "", ReplayGuid = S(r, "replay_guid"), SessionId = S(r, "session_id"), Fingerprint = S(r, "fingerprint"),
        PlayerHash = S(r, "player_hash")!, PlayerName = S(r, "player_name"), Started = L(r, "started"), Ended = L(r, "ended"),
        Chests = (int)L(r, "chests"), Searched = (int)L(r, "searched"), Destroyed = (int)L(r, "destroyed"), Ambiguous = (int)L(r, "ambiguous"),
        NoRecord = (int)L(r, "no_record"), Events = S(r, "events"),
        Breakdown = S(r, "breakdown") ?? "{}", Times = S(r, "times") ?? "[]", Status = S(r, "status")!,
        Parser = S(r, "parser")!, Registry = (int)L(r, "registry"), Build = S(r, "build"), Historical = L(r, "historical") != 0,
        Upload = S(r, "upload")!, ServerId = S(r, "server_id"), ServerChests = IN(r, "server_chests"),
        ServerStatus = S(r, "server_status"), Attempts = (int)L(r, "attempts"), NextTry = L(r, "next_try"), LastError = S(r, "last_error"),
        Created = L(r, "created"), Updated = L(r, "updated"),
        Corrected = IN(r, "corrected"), CorrectedAt = LN(r, "corrected_at"), CorrectionReason = S(r, "correction_reason"),
        CorrectionSource = S(r, "correction_source"), CorrectionSynced = L(r, "correction_synced") != 0,
        Excluded = L(r, "excluded") != 0, ManualTaps = IN(r, "manual_taps"), Reviewed = L(r, "reviewed") != 0,
    };

    public MatchRow? GetMatch(long id) => Query("select * from matches where id = $id", MapMatch, ("$id", id)).FirstOrDefault();

    /// <summary>The stored match for this recording: same player and match key, or the same replay id.</summary>
    public MatchRow? FindMatch(string playerHash, string matchKey, string? replayGuid) => Query("""
        select * from matches where player_hash = $h and (match_key = $k or ($g is not null and replay_guid = $g))
        order by (match_key = $k) desc limit 1
        """, MapMatch, ("$h", playerHash), ("$k", matchKey), ("$g", replayGuid?.ToLowerInvariant())).FirstOrDefault();

    public long InsertMatch(MatchRow m)
    {
        lock (_gate)
        {
            using var cmd = _db.CreateCommand();
            cmd.CommandText = """
                insert into matches (file, replay_guid, session_id, fingerprint, match_key, player_hash, player_name, started, ended, chests,
                  searched, destroyed, ambiguous, no_record, events, breakdown, times, status, parser, registry, build, historical, upload,
                  attempts, next_try, created, updated)
                values ($f, $g, $s, $fp, $k, $h, $n, $st, $en, $c, $se, $de, $am, $nr, $ev, $b, $t, $status, $p, $r, $build, $hist, $u, 0, 0, $now, $now);
                select last_insert_rowid();
                """;
            AddMatchParams(cmd, m);
            return (long)cmd.ExecuteScalar()!;
        }
    }

    /// <summary>A re-read: replaces what the replay shows and queues it to be sent again. A correction is kept.</summary>
    public void UpdateParsed(MatchRow m)
    {
        lock (_gate)
        {
            using var cmd = _db.CreateCommand();
            cmd.CommandText = """
                update matches set file = $f, replay_guid = coalesce($g, replay_guid), session_id = coalesce($s, session_id),
                  fingerprint = coalesce($fp, fingerprint), match_key = $k, player_name = coalesce($n, player_name), started = $st, ended = $en,
                  chests = $c, searched = $se, destroyed = $de, ambiguous = $am, no_record = $nr, events = $ev, breakdown = $b,
                  times = $t, status = $status, parser = $p, registry = $r, build = $build, historical = $hist, upload = $u,
                  attempts = 0, next_try = 0, last_error = null, updated = $now
                where id = $id
                """;
            AddMatchParams(cmd, m);
            cmd.Parameters.AddWithValue("$id", m.Id);
            cmd.ExecuteNonQuery();
        }
    }

    private static void AddMatchParams(SqliteCommand cmd, MatchRow m)
    {
        object V(object? v) => v ?? DBNull.Value;
        cmd.Parameters.AddWithValue("$f", m.File);
        cmd.Parameters.AddWithValue("$g", V(m.ReplayGuid?.ToLowerInvariant()));
        cmd.Parameters.AddWithValue("$s", V(m.SessionId));
        cmd.Parameters.AddWithValue("$fp", V(m.Fingerprint));
        cmd.Parameters.AddWithValue("$k", m.MatchKey);
        cmd.Parameters.AddWithValue("$h", m.PlayerHash);
        cmd.Parameters.AddWithValue("$n", V(m.PlayerName));
        cmd.Parameters.AddWithValue("$st", m.Started);
        cmd.Parameters.AddWithValue("$en", m.Ended);
        cmd.Parameters.AddWithValue("$c", m.Chests);
        cmd.Parameters.AddWithValue("$se", m.Searched);
        cmd.Parameters.AddWithValue("$de", m.Destroyed);
        cmd.Parameters.AddWithValue("$am", m.Ambiguous);
        cmd.Parameters.AddWithValue("$nr", m.NoRecord);
        cmd.Parameters.AddWithValue("$ev", V(m.Events));
        cmd.Parameters.AddWithValue("$b", m.Breakdown);
        cmd.Parameters.AddWithValue("$t", m.Times);
        cmd.Parameters.AddWithValue("$status", m.Status);
        cmd.Parameters.AddWithValue("$p", m.Parser);
        cmd.Parameters.AddWithValue("$r", m.Registry);
        cmd.Parameters.AddWithValue("$build", V(m.Build));
        cmd.Parameters.AddWithValue("$hist", m.Historical ? 1 : 0);
        cmd.Parameters.AddWithValue("$u", m.Upload);
        cmd.Parameters.AddWithValue("$now", Now());
    }

    public void SetUpload(long id, string upload, string? error = null) =>
        Exec("update matches set upload = $u, last_error = $e, updated = $now where id = $id", ("$u", upload), ("$e", error), ("$now", Now()), ("$id", id));

    public void MarkSent(long id, string? serverId, int? serverChests, string? serverStatus) => Exec("""
        update matches set upload = 'sent', server_id = $sid, server_chests = $sc, server_status = $ss, attempts = 0, next_try = 0,
          last_error = null, updated = $now where id = $id
        """, ("$sid", serverId), ("$sc", serverChests), ("$ss", serverStatus), ("$now", Now()), ("$id", id));

    public void Retry(long id, int attempts, long nextTry, string error) =>
        Exec("update matches set attempts = $a, next_try = $n, last_error = $e, updated = $now where id = $id",
            ("$a", attempts), ("$n", nextTry), ("$e", error), ("$now", Now()), ("$id", id));

    /// <summary>Newest first.</summary>
    public List<MatchRow> Matches(int limit = 200) => Query("select * from matches order by started desc limit $l", MapMatch, ("$l", limit));

    public List<MatchRow> AllMatches() => Query("select * from matches order by started", MapMatch);

    public List<MatchRow> DueUploads(long now, int limit) =>
        Query("select * from matches where upload = 'pending' and next_try <= $now order by started limit $l", MapMatch, ("$now", now), ("$l", limit));

    public int Count(string upload) => Query("select count(*) n from matches where upload = $u", r => (int)L(r, "n"), ("$u", upload)).First();

    public void SetUploadForProfileAll(string from, string to) =>
        Exec("update matches set upload = $to, updated = $now where upload = $from", ("$to", to), ("$from", from), ("$now", Now()));

    public void SetUploadForProfile(string hash, string from, string to) =>
        Exec("update matches set upload = $to, updated = $now where player_hash = $h and upload = $from", ("$to", to), ("$h", hash), ("$from", from), ("$now", Now()));

    public void ReleaseHistory(bool import) => Exec(import
        ? "update matches set upload = 'pending', next_try = 0 where upload = 'held-history'"
        : "update matches set upload = 'ignored' where upload = 'held-history'");

    /// <summary>Everything already sent goes out again (after a counting choice changed what is sent).</summary>
    public void RequeueSent(bool onlyWithDestroyed) => Exec(onlyWithDestroyed
        ? "update matches set upload = 'pending', next_try = 0, attempts = 0 where upload = 'sent' and destroyed > 0"
        : "update matches set upload = 'pending', next_try = 0, attempts = 0 where upload = 'sent'");

    // ------------------------------------------------------------------------------- corrections
    /// <summary>
    /// Records the player's own count for a match (null removes it). The replay's count and evidence are kept as
    /// they are, and every change is added to the match's correction history.
    /// </summary>
    public void Correct(long matchId, int? count, string? reason, string source, int replayCount)
    {
        var existing = GetMatch(matchId);
        if (existing is null) return;
        var now = Now();
        Exec("""
            update matches set corrected = $c, corrected_at = $at, correction_reason = $r, correction_source = $s,
              correction_synced = $synced, updated = $at where id = $id
            """, ("$c", count), ("$at", now), ("$r", string.IsNullOrWhiteSpace(reason) ? null : reason.Trim()), ("$s", count is null ? null : source),
            ("$synced", source == "website" ? 1 : 0), ("$id", matchId));
        Exec("insert into corrections (match_id, replay_count, old_count, new_count, reason, source, at) values ($m, $rc, $o, $n, $r, $s, $at)",
            ("$m", matchId), ("$rc", replayCount), ("$o", existing.Corrected), ("$n", count), ("$r", string.IsNullOrWhiteSpace(reason) ? null : reason.Trim()),
            ("$s", source), ("$at", now));
    }

    public List<CorrectionRow> Corrections(long matchId) => Query("select * from corrections where match_id = $m order by at", r =>
        new CorrectionRow(L(r, "id"), L(r, "match_id"), (int)L(r, "replay_count"), IN(r, "old_count"), IN(r, "new_count"), S(r, "reason"), S(r, "source")!, L(r, "at")),
        ("$m", matchId));

    public List<MatchRow> UnsyncedCorrections() => Query("select * from matches where correction_synced = 0 and upload = 'sent' order by started", MapMatch);

    public void MarkCorrectionSynced(long id) => Exec("update matches set correction_synced = 1 where id = $id", ("$id", id));

    public void SetExcluded(long id, bool excluded) =>
        Exec("update matches set excluded = $e, correction_synced = 0, updated = $now where id = $id", ("$e", excluded ? 1 : 0), ("$now", Now()), ("$id", id));

    public void SetReviewed(long id, bool reviewed) => Exec("update matches set reviewed = $r where id = $id", ("$r", reviewed ? 1 : 0), ("$id", id));

    /// <summary>The website changed whether this match counts: taken over without sending it back.</summary>
    public void SetExcludedFromServer(long id, bool excluded) =>
        Exec("update matches set excluded = $e, updated = $now where id = $id", ("$e", excluded ? 1 : 0), ("$now", Now()), ("$id", id));

    public MatchRow? FindByKey(string matchKey) => Query("select * from matches where match_key = $k order by started desc limit 1", MapMatch, ("$k", matchKey)).FirstOrDefault();

    /// <summary>Every match without its event list (which can be large): what statistics and lists need.</summary>
    public List<MatchRow> AllMatchesLight() => Query("""
        select id, file, replay_guid, session_id, fingerprint, match_key, player_hash, player_name, started, ended, chests, searched,
          destroyed, ambiguous, no_record, null as events, breakdown, times, status, parser, registry, build, historical, upload,
          server_id, server_chests, server_status, attempts, next_try, last_error, created, updated, corrected, corrected_at,
          correction_reason, correction_source, correction_synced, excluded, manual_taps, reviewed
        from matches order by started
        """, MapMatch);

    public void SetManualTaps(long id, int? taps) => Exec("update matches set manual_taps = $t where id = $id", ("$t", taps), ("$id", id));

    // ------------------------------------------------------------------------------- sessions (started by hand)
    private static SessionRow MapSession(SqliteDataReader r) => new(L(r, "id"), L(r, "started"), LN(r, "ended"), S(r, "note"));

    public List<SessionRow> Sessions() => Query("select * from sessions order by started", MapSession);
    public SessionRow? OpenSession() => Query("select * from sessions where ended is null order by started desc limit 1", MapSession).FirstOrDefault();

    public long StartSession(long at)
    {
        Exec("update sessions set ended = $at where ended is null", ("$at", at));
        lock (_gate)
        {
            using var cmd = _db.CreateCommand();
            cmd.CommandText = "insert into sessions (started) values ($at); select last_insert_rowid();";
            cmd.Parameters.AddWithValue("$at", at);
            return (long)cmd.ExecuteScalar()!;
        }
    }

    public void EndSession(long at) => Exec("update sessions set ended = $at where ended is null", ("$at", at));

    // ------------------------------------------------------------------------------- profiles
    private static ProfileRow MapProfile(SqliteDataReader r) => new(S(r, "hash")!, S(r, "name"), S(r, "decision")!, L(r, "claimed") != 0,
        L(r, "first_seen"), L(r, "last_seen"), S(r, "error"));

    public ProfileRow? GetProfile(string hash) => Query("select * from profiles where hash = $h", MapProfile, ("$h", hash)).FirstOrDefault();
    public List<ProfileRow> Profiles() => Query("select * from profiles order by last_seen desc", MapProfile);

    public void PutProfile(ProfileRow p) => Exec("""
        insert into profiles (hash, name, decision, claimed, first_seen, last_seen, error) values ($h, $n, $d, $c, $f, $l, $e)
        on conflict (hash) do update set name = coalesce(excluded.name, profiles.name), decision = excluded.decision,
          claimed = excluded.claimed, last_seen = max(profiles.last_seen, excluded.last_seen), error = excluded.error
        """, ("$h", p.Hash), ("$n", p.Name), ("$d", p.Decision), ("$c", p.Claimed ? 1 : 0), ("$f", p.FirstSeen), ("$l", p.LastSeen), ("$e", p.Error));

    // ------------------------------------------------------------------------------- maintenance
    /// <summary>
    /// "Delete my history": every match, correction and session on this PC. Settings, the website link and the
    /// Fortnite account choices stay. Replays still in Fortnite's folder would be read again on the next scan.
    /// </summary>
    public void DeleteHistory() => Exec("delete from corrections; delete from sessions; delete from matches; delete from files;");

    public void Dispose()
    {
        lock (_gate)
        {
            _db.Dispose();
        }
    }
}
