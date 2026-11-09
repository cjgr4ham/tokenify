using Microsoft.Data.Sqlite;

namespace Tokenify.Companion;

/// <summary>What the companion knows about one replay file on disk.</summary>
/// <param name="State">new, recording, done, failed, unsupported, held (old replay awaiting the history choice), skipped.</param>
internal sealed record FileRow(string Name, long Size, long MTime, string State, long CheckedAt, int Attempts, string? Error,
    string? Parser, int Registry, long? MatchId, long? Started);

/// <summary>Upload states for a parsed match.</summary>
internal static class Upload
{
    public const string Pending = "pending";          // waiting to be sent
    public const string Sent = "sent";                // TOKENIFY has it
    public const string HeldProfile = "held-profile"; // whose Fortnite account is this? (asked once)
    public const string HeldPartial = "held-partial"; // only partly readable: sent only if the player chooses
    public const string HeldHistory = "held-history"; // an old replay: waits for the import choice
    public const string NotMine = "not-mine";         // another player's account on this PC: never sent
    public const string Conflict = "conflict";        // that Fortnite account is linked to another TOKENIFY account
    public const string Rejected = "rejected";        // the server refused it (shown with the reason)
    public const string Ignored = "ignored";          // the player dismissed a partial/failed match
}

internal sealed class MatchRow
{
    public long Id { get; set; }
    public string File { get; set; } = "";
    public string? ReplayGuid { get; set; }
    public string? SessionId { get; set; }
    public string PlayerHash { get; set; } = "";
    public string? PlayerName { get; set; }
    public long Started { get; set; }
    public long Ended { get; set; }
    public int Chests { get; set; }
    public string Breakdown { get; set; } = "{}";
    public string Times { get; set; } = "[]";
    public string Status { get; set; } = "complete";
    public string Parser { get; set; } = "";
    public int Registry { get; set; }
    public string? Build { get; set; }
    public bool Historical { get; set; }
    public string Upload { get; set; } = Companion.Upload.Pending;
    public string? ServerId { get; set; }
    public int? ServerChests { get; set; }
    public string? ServerStatus { get; set; }
    public int Attempts { get; set; }
    public long NextTry { get; set; }
    public string? LastError { get; set; }
    public long Created { get; set; }
    public long Updated { get; set; }

    public string MatchKey => SessionId is { Length: > 0 } s ? "fn:" + s.ToLowerInvariant() : "rg:" + (ReplayGuid ?? "").ToLowerInvariant();
    public DateTime StartedLocal => DateTimeOffset.FromUnixTimeMilliseconds(Started).LocalDateTime;
    /// <summary>The count TOKENIFY uses (a correction made on the website wins).</summary>
    public int ShownChests => ServerChests ?? Chests;
}

/// <param name="Decision">mine, not-mine or ask.</param>
internal sealed record ProfileRow(string Hash, string? Name, string Decision, bool Claimed, long FirstSeen, long LastSeen, string? Error);

/// <summary>
/// Local SQLite database (%LOCALAPPDATA%\TOKENIFY Companion\companion.db): processed replay files, parsed matches
/// and their upload queue, Fortnite account choices and settings. Matches stay here even after Fortnite deletes the
/// replay, so nothing already counted is ever lost. All access is serialised through one connection.
/// </summary>
internal sealed class Store : IDisposable
{
    private readonly SqliteConnection _db;
    private readonly object _gate = new();

    public Store(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
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

    // ------------------------------------------------------------------------------- settings
    public string? Get(string key) => Query("select value from kv where key = $k", r => S(r, "value"), ("$k", key)).FirstOrDefault();

    public void Set(string key, string? value)
    {
        if (value is null) Exec("delete from kv where key = $k", ("$k", key));
        else Exec("insert into kv (key, value) values ($k, $v) on conflict (key) do update set value = excluded.value", ("$k", key), ("$v", value));
    }

    public bool Flag(string key, bool fallback = false) => Get(key) is { } v ? v == "1" : fallback;
    public void SetFlag(string key, bool value) => Set(key, value ? "1" : "0");

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

    // ------------------------------------------------------------------------------- matches
    private static MatchRow MapMatch(SqliteDataReader r) => new()
    {
        Id = L(r, "id"), File = S(r, "file") ?? "", ReplayGuid = S(r, "replay_guid"), SessionId = S(r, "session_id"),
        PlayerHash = S(r, "player_hash")!, PlayerName = S(r, "player_name"), Started = L(r, "started"), Ended = L(r, "ended"),
        Chests = (int)L(r, "chests"), Breakdown = S(r, "breakdown") ?? "{}", Times = S(r, "times") ?? "[]", Status = S(r, "status")!,
        Parser = S(r, "parser")!, Registry = (int)L(r, "registry"), Build = S(r, "build"), Historical = L(r, "historical") != 0,
        Upload = S(r, "upload")!, ServerId = S(r, "server_id"), ServerChests = LN(r, "server_chests") is long sc ? (int)sc : null,
        ServerStatus = S(r, "server_status"), Attempts = (int)L(r, "attempts"), NextTry = L(r, "next_try"), LastError = S(r, "last_error"),
        Created = L(r, "created"), Updated = L(r, "updated"),
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
                insert into matches (file, replay_guid, session_id, match_key, player_hash, player_name, started, ended, chests,
                  breakdown, times, status, parser, registry, build, historical, upload, attempts, next_try, created, updated)
                values ($f, $g, $s, $k, $h, $n, $st, $en, $c, $b, $t, $status, $p, $r, $build, $hist, $u, 0, 0, $now, $now);
                select last_insert_rowid();
                """;
            AddMatchParams(cmd, m);
            return (long)cmd.ExecuteScalar()!;
        }
    }

    /// <summary>A re-read with a newer parser: replaces the parsed data and queues it to be sent again.</summary>
    public void UpdateParsed(MatchRow m)
    {
        lock (_gate)
        {
            using var cmd = _db.CreateCommand();
            cmd.CommandText = """
                update matches set file = $f, replay_guid = coalesce($g, replay_guid), session_id = coalesce($s, session_id),
                  match_key = $k, player_name = coalesce($n, player_name), started = $st, ended = $en, chests = $c, breakdown = $b,
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
        cmd.Parameters.AddWithValue("$k", m.MatchKey);
        cmd.Parameters.AddWithValue("$h", m.PlayerHash);
        cmd.Parameters.AddWithValue("$n", V(m.PlayerName));
        cmd.Parameters.AddWithValue("$st", m.Started);
        cmd.Parameters.AddWithValue("$en", m.Ended);
        cmd.Parameters.AddWithValue("$c", m.Chests);
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

    public void SetServerCount(string matchKey, string playerHash, int chests) =>
        Exec("update matches set server_chests = $c where match_key = $k and player_hash = $h", ("$c", chests), ("$k", matchKey), ("$h", playerHash));

    public List<MatchRow> Matches(int limit = 200) => Query("select * from matches order by started desc limit $l", MapMatch, ("$l", limit));

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
    /// <summary>"Clear local cache": forgets processed files and the local match list. Replays still on disk are read
    /// again and re-sent; TOKENIFY recognises matches it already has, so nothing is counted twice. The connection and
    /// the Fortnite account choices are kept.</summary>
    public void ClearCache() => Exec("delete from files; delete from matches;");

    public void Dispose()
    {
        lock (_gate)
        {
            _db.Dispose();
        }
    }
}
