using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Tokenify.ReplayReader;

namespace Tokenify.Companion;

internal enum FolderState { Ok, Empty, Missing, Unreadable }

internal enum Activity { NotConnected, Paused, FolderMissing, InMatch, Processing, Syncing, NeedsAttention, Waiting, UpToDate, Offline }

/// <summary>Everything the tray and the windows show, as one immutable snapshot.</summary>
internal sealed record EngineSnapshot(
    bool Paired,
    string? Username,
    string? DeviceName,
    string Tracking,
    bool Paused,
    bool Revoked,
    string FolderPath,
    FolderState Folder,
    int ReplayFiles,
    Activity Activity,
    string Status,
    int Queued,
    IReadOnlyList<string> Attention,
    MatchRow? LastMatch,
    DateTime? LastSyncUtc,
    bool Online,
    int HistoryHeld,
    DateTime? HistoryFrom,
    DateTime? HistoryTo,
    int UnsupportedFiles,
    ProfileRow? AskProfile,
    int PreSeasonFiles,
    DateTime SeasonStartUtc);

/// <summary>
/// The companion's background work, on two low-priority threads:
/// <list type="bullet">
/// <item>Scanner: watches the replay folder (file notifications + a light periodic listing), waits until a replay is
/// finished (Fortnite writes its decryption key only when the match ends, and the file must stop changing), reads it
/// in a child process and stores the result. Files are only ever opened read-only.</item>
/// <item>Sync: checks in with TOKENIFY, confirms the player's Fortnite account, and uploads one summary per match,
/// retrying with back-off while offline. Uploads are idempotent on the server, so retries never double count.</item>
/// </list>
/// </summary>
internal sealed class Engine : IDisposable
{
    private static readonly TimeSpan ParseTimeout = TimeSpan.FromSeconds(120);
    private static readonly TimeSpan HelloEvery = TimeSpan.FromMinutes(5);

    private readonly Store _store;
    private readonly ApiClient _api = new();
    private readonly CancellationTokenSource _cts = new();
    private readonly AutoResetEvent _scanWake = new(false);
    private readonly AutoResetEvent _syncWake = new(false);
    private readonly object _gate = new();
    private Thread? _scanThread, _syncThread;
    private FileSystemWatcher? _watcher;
    private string? _watchedFolder;

    // live state (guarded by _gate)
    private FolderState _folder = FolderState.Missing;
    private string _folderPath = "";
    private int _replayFiles;
    private bool _anyLive, _processing, _syncing, _online = true, _revoked, _statusDirty = true;
    private DateTime? _lastSyncUtc;
    private DateTime _nextHello = DateTime.MinValue;
    private string? _username, _deviceName;
    private string _tracking = "auto";
    private readonly HashSet<string> _notified = new();

    public event Action<EngineSnapshot>? Changed;
    /// <summary>(title, text): a balloon the tray may show.</summary>
    public event Action<string, string>? Notify;

    public Engine(Store store)
    {
        _store = store;
        _username = store.Get("account.username");
        _deviceName = store.Get("device.name");
        _tracking = store.Get("account.tracking") ?? "auto";
        if (store.Get("history.cutoff") is null)
        {
            store.Set("history.cutoff", Store.Now().ToString(CultureInfo.InvariantCulture));
        }
        _folderPath = ResolveFolder();
        (_folder, var files) = ListReplays(_folderPath);
        _replayFiles = files.Count;
    }

    public Store Store => _store;
    public bool Paired => Credentials.Load(_store) is not null;
    public bool Paused => _store.Flag("tracking.paused");

    /// <summary>
    /// Start of the current season (Chapter 7 Season 3: Nov 1, 2026, 12:00 AM Eastern). The Pirate Token didn't
    /// exist before it, so older replays are never read or sent. TOKENIFY sends the date with every check-in (and
    /// refuses older matches itself); this built-in value is only used until the first check-in.
    /// </summary>
    public static readonly DateTime DefaultSeasonStartUtc = new(2026, 11, 1, 4, 0, 0, DateTimeKind.Utc);

    public DateTime SeasonStartUtc =>
        DateTime.TryParse(_store.Get("season.start"), CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var s)
            ? s : DefaultSeasonStartUtc;

    /// <summary>Stores the season start TOKENIFY reports; if it moved, replays skipped as "earlier season" are looked at again.</summary>
    private void SetSeasonStart(string? iso)
    {
        if (!DateTime.TryParse(iso, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var s)) return;
        if (s == SeasonStartUtc && _store.Get("season.start") is not null) return;
        _store.Set("season.start", s.ToString("O", CultureInfo.InvariantCulture));
        _store.SetFileState("new", "pre-season");
        Log.Info($"Season start: {s:yyyy-MM-dd HH:mm} UTC (matches before it are not counted)");
        ScanNow();
    }

    public void Start()
    {
        _scanThread = new Thread(ScanLoop) { IsBackground = true, Name = "Replay scanner", Priority = ThreadPriority.BelowNormal };
        _syncThread = new Thread(SyncLoop) { IsBackground = true, Name = "TOKENIFY sync", Priority = ThreadPriority.BelowNormal };
        _scanThread.Start();
        _syncThread.Start();
        Log.Info($"TOKENIFY Companion {AppInfo.Version} started (parser {AppInfo.ParserVersion}, chest registry v{ChestRegistry.Default.Version})");
    }

    public void ScanNow() => _scanWake.Set();

    public void SyncNow()
    {
        lock (_gate) { _statusDirty = true; }
        _syncWake.Set();
    }

    public void SetPaused(bool paused)
    {
        _store.SetFlag("tracking.paused", paused);
        Log.Info(paused ? "Automatic tracking paused" : "Automatic tracking resumed");
        ScanNow();
        SyncNow();
        Publish();
    }

    public void SetFolder(string? folder)
    {
        _store.Set("demos.folder", string.IsNullOrWhiteSpace(folder) ? null : folder);
        ScanNow();
    }

    public string ResolveFolder() => _store.Get("demos.folder") is { Length: > 0 } custom ? custom : AppPaths.DefaultDemos;

    // ======================================================================================= scanner

    private void ScanLoop()
    {
        while (!_cts.IsCancellationRequested)
        {
            try
            {
                ScanPass();
            }
            catch (Exception ex)
            {
                Log.Error("Scan failed", ex);
            }

            Publish();
            bool live;
            lock (_gate) { live = _anyLive; }
            WaitHandle.WaitAny(new[] { _cts.Token.WaitHandle, _scanWake }, live ? TimeSpan.FromSeconds(3) : TimeSpan.FromSeconds(15));
        }
    }

    public static (FolderState State, List<FileInfo> Files) ListReplays(string folder)
    {
        try
        {
            if (!Directory.Exists(folder)) return (FolderState.Missing, new());
            var files = new DirectoryInfo(folder).EnumerateFiles("*.replay").ToList();
            return (files.Count == 0 ? FolderState.Empty : FolderState.Ok, files);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
        {
            return (FolderState.Unreadable, new());
        }
    }

    private static long Ms(DateTime utc) => new DateTimeOffset(DateTime.SpecifyKind(utc, DateTimeKind.Utc)).ToUnixTimeMilliseconds();

    /// <summary>True while another program (Fortnite) has the file open for writing.</summary>
    private static bool IsBeingWritten(string path)
    {
        try
        {
            using var _ = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            return false;
        }
        catch (IOException)
        {
            return true;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }

    private void ScanPass()
    {
        var folder = ResolveFolder();
        var (state, files) = ListReplays(folder);
        lock (_gate)
        {
            _folder = state;
            _folderPath = folder;
            _replayFiles = files.Count;
        }

        if (state is FolderState.Missing or FolderState.Unreadable)
        {
            lock (_gate) { _anyLive = false; }
            return;
        }

        EnsureWatcher(folder);
        if (Paused || !Paired)
        {
            lock (_gate) { _anyLive = false; }
            return;
        }

        var cutoff = long.Parse(_store.Get("history.cutoff") ?? "0", CultureInfo.InvariantCulture);
        var historyChoice = _store.Get("history.choice");   // null = not decided yet, "import" or "skip"
        var registry = ChestRegistry.Default;
        var anyLive = false;
        var now = Store.Now();
        var season = Ms(SeasonStartUtc);

        foreach (var f in files.OrderByDescending(f => f.LastWriteTimeUtc))
        {
            if (_cts.IsCancellationRequested) return;
            f.Refresh();
            if (!f.Exists) continue;
            var mtime = Ms(f.LastWriteTimeUtc);
            var row = _store.GetFile(f.Name);
            var unchanged = row is not null && row.Size == f.Length && row.MTime == mtime;
            var currentParser = row?.Parser == AppInfo.ParserVersion && row.Registry == registry.Version;
            if (unchanged && row!.State is "done" or "skipped" && currentParser) continue;
            if (unchanged && row!.State is "pre-season") continue;
            if (unchanged && row!.State is "unsupported" && row.Parser == AppInfo.ParserVersion) continue;   // retried after an update
            if (unchanged && row!.State is "failed" && (row.Attempts >= 3 || now - row.CheckedAt < 60_000)) continue;

            // A match from before this season (no Pirate Token then) is never read or sent, not even as history.
            // A replay is written while its match is played, so a file last written before the season start is
            // older still; otherwise the start time in its header decides.
            var header = mtime < season ? null : ReplayProbe.Probe(f.FullName);
            if (mtime < season || header?.TimestampUtc is { } hts && Ms(hts) < season)
            {
                _store.PutFile(new FileRow(f.Name, f.Length, mtime, "pre-season", now, 0, "from before this season: not counted", null, 0, null,
                    header?.TimestampUtc is { } st0 ? Ms(st0) : mtime));
                continue;
            }

            var isHistory = mtime < cutoff;
            if (isHistory && historyChoice == "skip")
            {
                _store.PutFile(new FileRow(f.Name, f.Length, mtime, "skipped", now, 0, null, AppInfo.ParserVersion, registry.Version, row?.MatchId, row?.Started));
                continue;
            }

            if (isHistory && historyChoice is null)
            {
                // An older replay found at first launch: only its date is read until the player decides whether to import history.
                if (!unchanged || row!.State != "held")
                {
                    _store.PutFile(new FileRow(f.Name, f.Length, mtime, "held", now, 0, null, null, 0, null,
                        header!.TimestampUtc is { } ts ? Ms(ts) : mtime));
                }
                continue;
            }

            var probe = header!;
            switch (probe.State)
            {
                case ProbeState.Recording:
                {
                    var fresh = now - mtime < 10 * 60_000 || IsBeingWritten(f.FullName);
                    if (fresh) anyLive = true;
                    if (row?.State != "recording" || !unchanged)
                    {
                        _store.PutFile(new FileRow(f.Name, f.Length, mtime, "recording", now, 0, fresh ? null : "incomplete: Fortnite never finished this replay",
                            null, 0, null, probe.TimestampUtc is { } ts ? Ms(ts) : null));
                    }
                    continue;
                }
                case ProbeState.Invalid:
                    if (now - mtime < 60_000) { anyLive = true; continue; }   // probably still being created
                    _store.PutFile(new FileRow(f.Name, f.Length, mtime, "failed", now, (row?.Attempts ?? 0) + 1, "not a readable replay: " + probe.Reason,
                        AppInfo.ParserVersion, registry.Version, row?.MatchId, row?.Started));
                    continue;
                case ProbeState.UnsupportedVersion:
                    _store.PutFile(new FileRow(f.Name, f.Length, mtime, "unsupported", now, 0, probe.Reason, AppInfo.ParserVersion, registry.Version, row?.MatchId, row?.Started));
                    continue;
            }

            // Finished: wait until the file has stopped changing and Fortnite has let go of it.
            if (now - mtime < 4000 || IsBeingWritten(f.FullName))
            {
                anyLive = true;
                continue;
            }

            Parse(f, mtime, row, isHistory);
        }

        lock (_gate) { _anyLive = anyLive; }
    }

    private void Parse(FileInfo f, long mtime, FileRow? row, bool historical)
    {
        var registry = ChestRegistry.Default;
        lock (_gate) { _processing = true; _statusDirty = true; }
        _syncWake.Set();
        Publish();
        ScanResult? result;
        string? failure;
        try
        {
            result = ReplayWorker.Read(f.FullName, ParseTimeout, out failure);
        }
        finally
        {
            lock (_gate) { _processing = false; _statusDirty = true; }
        }

        var now = Store.Now();
        if (result is null)
        {
            Log.Warn($"{f.Name}: {failure}");
            _store.PutFile(new FileRow(f.Name, f.Length, mtime, "failed", now, (row?.Attempts ?? 0) + 1, failure, AppInfo.ParserVersion, registry.Version, row?.MatchId, row?.Started));
            return;
        }

        var startedUtc = result.StartedUtc ?? f.LastWriteTimeUtc.AddMilliseconds(-result.LengthMs);
        var started = Ms(startedUtc);
        if (startedUtc < SeasonStartUtc)
        {
            _store.PutFile(new FileRow(f.Name, f.Length, mtime, "pre-season", now, 0, "from before this season: not counted", null, 0, null, started));
            return;
        }

        switch (result.Status)
        {
            case ScanStatus.NotReady:
                return;   // look again on the next pass
            case ScanStatus.Unsupported:
                Log.Warn($"{f.Name}: unsupported replay ({result.Branch}): {result.Error}");
                _store.PutFile(new FileRow(f.Name, f.Length, mtime, "unsupported", now, 0, result.Error, AppInfo.ParserVersion, registry.Version, row?.MatchId, started));
                return;
            case ScanStatus.Corrupt or ScanStatus.NoRecorder:
                Log.Warn($"{f.Name}: {result.Status}: {result.Error}");
                _store.PutFile(new FileRow(f.Name, f.Length, mtime, "failed", now, 3, Describe(result), AppInfo.ParserVersion, registry.Version, row?.MatchId, started));
                return;
        }

        if (!result.RecorderFound || string.IsNullOrEmpty(result.RecorderAccountId))
        {
            _store.PutFile(new FileRow(f.Name, f.Length, mtime, "failed", now, 3, "the recording player could not be identified", AppInfo.ParserVersion, registry.Version, row?.MatchId, started));
            return;
        }

        var hash = ParserInfo.PlayerHash(result.RecorderAccountId!);
        var decision = DecideProfile(hash, result.RecorderName);
        var match = new MatchRow
        {
            File = f.Name,
            ReplayGuid = CleanGuid(result.ReplayGuid),
            SessionId = result.MatchSessionId,
            PlayerHash = hash,
            PlayerName = result.RecorderName,
            Started = started,
            Ended = started + result.LengthMs,
            Chests = result.ChestsOpened,
            Breakdown = JsonSerializer.Serialize(Breakdown(result)),
            Times = JsonSerializer.Serialize(result.Chests.Select(c => Math.Round(c.Time, 1)).ToArray()),
            Status = result.Status == ScanStatus.Complete ? "complete" : "partial",
            Parser = AppInfo.ParserVersion,
            Registry = registry.Version,
            Build = result.Branch,
            Historical = historical,
        };
        match.Upload = decision switch
        {
            "not-mine" => Upload.NotMine,
            "ask" => Upload.HeldProfile,
            _ when match.Status == "partial" => Upload.HeldPartial,
            _ => Upload.Pending,
        };
        if (match.SessionId is null && match.ReplayGuid is null)
        {
            _store.PutFile(new FileRow(f.Name, f.Length, mtime, "failed", now, 3, "the replay has no match id", AppInfo.ParserVersion, registry.Version, row?.MatchId, started));
            return;
        }

        var existing = _store.FindMatch(hash, match.MatchKey, match.ReplayGuid);
        long id;
        if (existing is null)
        {
            id = _store.InsertMatch(match);
            Log.Info($"{f.Name}: {match.Chests} chests ({match.Status}){(historical ? ", history" : "")}");
        }
        else
        {
            id = existing.Id;
            var changed = existing.Chests != match.Chests || existing.Times != match.Times || existing.Parser != match.Parser
                          || existing.Registry != match.Registry || existing.Status != match.Status || existing.MatchKey != match.MatchKey;
            if (changed)
            {
                match.Id = existing.Id;
                if (existing.Upload is Upload.NotMine or Upload.Ignored) match.Upload = existing.Upload;
                match.Historical = existing.Historical;
                _store.UpdateParsed(match);
                Log.Info($"{f.Name}: re-read with parser {match.Parser}: {existing.Chests} -> {match.Chests} chests");
            }
        }

        _store.PutFile(new FileRow(f.Name, f.Length, mtime, "done", now, 0, result.Status == ScanStatus.Partial ? result.Error : null,
            AppInfo.ParserVersion, registry.Version, id, started));
        _syncWake.Set();
    }

    private static string Describe(ScanResult r) => r.Status switch
    {
        ScanStatus.Corrupt => "damaged or not a replay: " + r.Error,
        ScanStatus.NoRecorder => "the recording player could not be identified",
        _ => r.Error ?? r.Status.ToString(),
    };

    private static string? CleanGuid(string? guid)
    {
        if (guid is null) return null;
        var clean = new string(guid.Where(c => char.IsAsciiLetterOrDigit(c) || c == '-').ToArray()).ToLowerInvariant();
        return clean.Length is >= 8 and <= 64 ? clean : null;
    }

    private static Dictionary<string, int> Breakdown(ScanResult r)
    {
        var d = r.ChestsByKind();
        var destroyed = r.Chests.Count(c => c.Destroyed);
        var reopened = r.Chests.Count(c => c.Reopened);
        var unverified = r.Chests.Count(c => c.Unverified);
        if (destroyed > 0) d["destroyed"] = destroyed;
        if (reopened > 0) d["reopened"] = reopened;
        if (unverified > 0) d["unverified"] = unverified;
        return d;
    }

    /// <summary>
    /// Whose Fortnite account recorded this replay? The first account ever seen on this PC is taken to be the
    /// owner's (they are told, and can change it); any further account is asked about once before anything of it is
    /// sent. A "not mine" account is never uploaded.
    /// </summary>
    private string DecideProfile(string hash, string? name)
    {
        var now = Store.Now();
        var existing = _store.GetProfile(hash);
        if (existing is not null)
        {
            _store.PutProfile(existing with { Name = name ?? existing.Name, LastSeen = now });
            return existing.Decision;
        }

        var first = _store.Profiles().Count == 0;
        var decision = first ? "mine" : "ask";
        _store.PutProfile(new ProfileRow(hash, name, decision, false, now, now, null));
        if (first)
        {
            RaiseNotify("TOKENIFY Companion", $"Counting chests for Fortnite player {name ?? "(unknown name)"}. Not you? Open the companion to change it.");
        }
        else
        {
            RaiseNotify("TOKENIFY Companion", $"Another Fortnite account ({name ?? "unknown"}) played on this PC. Open the companion to say whether it's yours.");
        }
        _syncWake.Set();
        return decision;
    }

    /// <summary>The player says whether a Fortnite account is theirs.</summary>
    public void SetProfileDecision(string hash, bool mine)
    {
        var p = _store.GetProfile(hash);
        if (p is null) return;
        _store.PutProfile(p with { Decision = mine ? "mine" : "not-mine", Error = null });
        if (mine)
        {
            _store.SetUploadForProfile(hash, Upload.HeldProfile, Upload.Pending);
            _store.SetUploadForProfile(hash, Upload.NotMine, Upload.Pending);
            _store.SetUploadForProfile(hash, Upload.Conflict, Upload.Pending);
        }
        else
        {
            _store.SetUploadForProfile(hash, Upload.HeldProfile, Upload.NotMine);
            _store.SetUploadForProfile(hash, Upload.Pending, Upload.NotMine);
        }
        Log.Info($"Fortnite account {hash[..8]}… marked as {(mine ? "mine" : "not mine")}");
        SyncNow();
        Publish();
    }

    /// <summary>History choice (setup): import the replays that were already on the PC, or start from now.</summary>
    public void SetHistoryChoice(bool import)
    {
        _store.Set("history.choice", import ? "import" : "skip");
        _store.ReleaseHistory(import);
        if (!import) _store.SetFileState("skipped", "held");
        else _store.SetFileState("new", "held");
        Log.Info(import ? "Importing replays that were already on this PC" : "Skipping replays from before setup");
        ScanNow();
        Publish();
    }

    /// <summary>Older replays that could be imported: only this season's (earlier seasons are counted separately, never imported).</summary>
    public (int Count, DateTime? From, DateTime? To, int Processed, int PreSeason) HistoryPreview()
    {
        var folderFiles = ListReplays(ResolveFolder()).Files;
        var cutoff = long.Parse(_store.Get("history.cutoff") ?? "0", CultureInfo.InvariantCulture);
        var season = SeasonStartUtc;
        var older = folderFiles.Where(f => Ms(f.LastWriteTimeUtc) < cutoff).ToList();
        int count = 0, processed = 0, preSeason = 0;
        DateTime? from = null, to = null;
        foreach (var f in older)
        {
            var row = _store.GetFile(f.Name);
            var whenUtc = f.LastWriteTimeUtc < season ? f.LastWriteTimeUtc
                : row?.Started is long s ? DateTimeOffset.FromUnixTimeMilliseconds(s).UtcDateTime
                : ReplayProbe.Probe(f.FullName).TimestampUtc ?? f.LastWriteTimeUtc;
            if (whenUtc < season) { preSeason++; continue; }
            count++;
            if (row?.State == "done") processed++;
            var when = whenUtc.ToLocalTime();
            from = from is null || when < from ? when : from;
            to = to is null || when > to ? when : to;
        }
        return (count, from, to, processed, preSeason);
    }

    /// <summary>The player chooses to send a match that could only be read partly (or to ignore it).</summary>
    public void ResolvePartial(long matchId, bool send)
    {
        _store.SetUpload(matchId, send ? Upload.Pending : Upload.Ignored);
        SyncNow();
        Publish();
    }

    /// <summary>Re-read every replay still on disk (e.g. after a Fortnite update) and re-send what changed.</summary>
    public void ClearCache()
    {
        _store.ClearCache();
        Log.Info("Local cache cleared; replays still on disk will be read again");
        ScanNow();
        Publish();
    }

    /// <summary>Identifies the recording player of the newest finished replay (setup: "is this you?").</summary>
    public (string Hash, string? Name)? IdentifyNewestReplay()
    {
        foreach (var f in ListReplays(ResolveFolder()).Files.OrderByDescending(f => f.LastWriteTimeUtc).Take(5))
        {
            if (ReplayProbe.Probe(f.FullName).State != ProbeState.Ready) continue;
            var r = ReplayWorker.Read(f.FullName, ParseTimeout, out _);
            if (r?.RecorderAccountId is { Length: > 0 } id) return (ParserInfo.PlayerHash(id), r.RecorderName);
        }
        return null;
    }

    public void RememberProfile(string hash, string? name, bool mine)
    {
        var now = Store.Now();
        _store.PutProfile(new ProfileRow(hash, name, mine ? "mine" : "not-mine", false, now, now, null));
    }

    private void EnsureWatcher(string folder)
    {
        if (_watcher is not null && string.Equals(_watchedFolder, folder, StringComparison.OrdinalIgnoreCase)) return;
        try
        {
            _watcher?.Dispose();
            _watcher = new FileSystemWatcher(folder, "*.replay")
            {
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.Size | NotifyFilters.LastWrite,
            };
            _watcher.Created += (_, _) => _scanWake.Set();
            _watcher.Changed += (_, _) => _scanWake.Set();
            _watcher.Renamed += (_, _) => _scanWake.Set();
            _watcher.Error += (_, e) => Log.Warn("Folder watcher error, polling only: " + e.GetException().Message);
            _watcher.EnableRaisingEvents = true;
            _watchedFolder = folder;
        }
        catch (Exception ex)
        {
            Log.Warn("Folder watcher unavailable, polling only: " + ex.Message);
            _watcher?.Dispose();
            _watcher = null;
            _watchedFolder = null;
        }
    }

    // ======================================================================================= pairing

    public async Task<(bool Ok, string Message)> Pair(string code)
    {
        const string name = "Windows PC";   // not the computer's own name (it can contain a person's name); rename it on the website
        var r = await _api.Call("tk_device_pair", new JsonObject { ["p_code"] = code, ["p_name"] = name, ["p_app_version"] = AppInfo.Version }, _cts.Token);
        if (!r.Ok)
        {
            return (false, r.Error switch
            {
                "bad_code" => "That code didn't work. Codes last 10 minutes and work once: make a new one on the website.",
                "too_many_devices" => "This TOKENIFY account already has 5 linked PCs. Remove one on the website first.",
                "rate_limited" => "Too many tries. Wait a few minutes and try again.",
                "network" => "Can't reach TOKENIFY. Check your internet connection and try again.",
                _ => "Linking failed (" + r.Error + "). Try again in a moment.",
            });
        }

        if (!Guid.TryParse(r.Str("device_id"), out var id) || r.Str("secret") is not { Length: >= 32 } secret)
        {
            return (false, "TOKENIFY sent an unexpected answer. Please try again.");
        }

        // Reconnecting replaces the previous link, which is removed so it can't upload any more.
        if (Credentials.Load(_store) is { } old && old.Id != id)
        {
            await _api.Call("tk_device_disconnect", new JsonObject { ["p_device"] = old.Id.ToString(), ["p_secret"] = old.Secret }, _cts.Token);
        }

        Credentials.Save(_store, id, secret);
        SetSeasonStart(r.Str("season_start"));
        var username = ApiResult.Text(r.Body?["account"]?["username"]);
        _store.Set("account.username", username);
        _store.Set("device.name", name);
        _store.Set("account.tracking", r.Str("tracking") ?? "auto");
        lock (_gate)
        {
            _username = username;
            _deviceName = name;
            _tracking = r.Str("tracking") ?? "auto";
            _revoked = false;
            _nextHello = DateTime.MinValue;
            _statusDirty = true;
        }
        // Anything waiting for a decision about this Fortnite account is re-checked against the new account.
        foreach (var p in _store.Profiles()) _store.PutProfile(p with { Claimed = false, Error = null });
        _store.SetUploadForProfileAll(Upload.Conflict, Upload.Pending);
        Log.Info($"Linked to TOKENIFY account {username}");
        ScanNow();
        SyncNow();
        Publish();
        return (true, $"Connected to TOKENIFY as {username}.");
    }

    public async Task Disconnect()
    {
        var dev = Credentials.Load(_store);
        if (dev is not null)
        {
            await _api.Call("tk_device_disconnect", new JsonObject { ["p_device"] = dev.Id.ToString(), ["p_secret"] = dev.Secret }, _cts.Token);
        }
        Credentials.Clear(_store);
        lock (_gate) { _username = null; _deviceName = null; _revoked = false; }
        Log.Info("Disconnected from TOKENIFY");
        Publish();
    }

    // ======================================================================================= sync

    private void SyncLoop()
    {
        var delay = TimeSpan.FromSeconds(5);
        while (!_cts.IsCancellationRequested)
        {
            try
            {
                delay = SyncPass().GetAwaiter().GetResult();
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception ex)
            {
                Log.Error("Sync failed", ex);
                delay = TimeSpan.FromMinutes(1);
            }

            Publish();
            WaitHandle.WaitAny(new[] { _cts.Token.WaitHandle, _syncWake }, delay);
        }
    }

    private static readonly int[] BackoffSeconds = { 30, 60, 120, 300, 600, 1800 };

    private async Task<TimeSpan> SyncPass()
    {
        var dev = Credentials.Load(_store);
        if (dev is null) return TimeSpan.FromMinutes(1);
        var ct = _cts.Token;

        bool helloDue;
        lock (_gate) { helloDue = _statusDirty || DateTime.UtcNow >= _nextHello; }
        if (helloDue && !await Hello(dev, ct)) return TimeSpan.FromSeconds(60);
        lock (_gate) { if (_revoked) return TimeSpan.FromMinutes(10); }
        if (Paused) return HelloEvery;

        // confirm this player's Fortnite account(s) on TOKENIFY before anything of theirs is sent
        foreach (var p in _store.Profiles().Where(p => p.Decision == "mine" && !p.Claimed && p.Error is null))
        {
            var c = await _api.Call("tk_device_profile_claim", new JsonObject
            {
                ["p_device"] = dev.Id.ToString(), ["p_secret"] = dev.Secret, ["p_player_hash"] = p.Hash, ["p_label"] = p.Name,
            }, ct);
            if (c.Ok)
            {
                _store.PutProfile(p with { Claimed = true });
            }
            else if (c.Error == "profile_conflict")
            {
                _store.PutProfile(p with { Error = "conflict" });
                _store.SetUploadForProfile(p.Hash, Upload.Pending, Upload.Conflict);
                RaiseNotify("TOKENIFY Companion", $"The Fortnite account {p.Name ?? ""} is linked to a different TOKENIFY account, so its matches aren't sent here.");
            }
            else if (HandleDeviceError(c))
            {
                return TimeSpan.FromMinutes(10);
            }
            else
            {
                MarkOnline(false);
                return TimeSpan.FromSeconds(60);
            }
        }

        var claimed = _store.Profiles().Where(p => p.Decision == "mine" && p.Claimed).Select(p => p.Hash).ToHashSet();
        var due = _store.DueUploads(Store.Now(), 25).Where(m => claimed.Contains(m.PlayerHash)).ToList();
        if (due.Count == 0)
        {
            MarkOnline(true);
            return _store.Count(Upload.Pending) > 0 ? TimeSpan.FromSeconds(30) : TimeSpan.FromMinutes(2);
        }

        lock (_gate) { _syncing = true; _statusDirty = true; }
        Publish();
        try
        {
            foreach (var m in due)
            {
                var r = await _api.Call("tk_device_import", new JsonObject
                {
                    ["p_device"] = dev.Id.ToString(), ["p_secret"] = dev.Secret, ["p_match"] = Payload(m),
                }, ct);
                if (r.Ok)
                {
                    var server = r.Body?["match"];
                    _store.MarkSent(m.Id, ApiResult.Text(server?["id"]), ApiResult.Int(server?["chests"]), r.Str("status"));
                    MarkOnline(true);
                    lock (_gate) { _lastSyncUtc = DateTime.UtcNow; }
                    _store.Set("sync.last", DateTime.UtcNow.ToString("O"));
                    Log.Info($"Sent {m.File}: {m.Chests} chests ({r.Str("status")})");
                    if (!m.Historical && r.Str("status") == "created" && _store.Flag("notify.synced"))
                    {
                        RaiseNotify("TOKENIFY", $"{m.Chests} CHESTS SYNCED TO TOKENIFY", once: false);
                    }
                    continue;
                }

                switch (r.Error)
                {
                    case "profile_unclaimed":
                        if (_store.GetProfile(m.PlayerHash) is { } p) _store.PutProfile(p with { Claimed = false });
                        return TimeSpan.FromSeconds(2);
                    case "profile_conflict":
                        if (_store.GetProfile(m.PlayerHash) is { } pc) _store.PutProfile(pc with { Error = "conflict" });
                        _store.SetUploadForProfile(m.PlayerHash, Upload.Pending, Upload.Conflict);
                        continue;
                    case "invalid_match" when r.Str("detail") == "before_season":
                        _store.SetUpload(m.Id, Upload.Ignored, "from before this season: not counted");
                        Log.Info($"{m.File}: from before this season, not counted");
                        continue;
                    case "invalid_match":
                        _store.SetUpload(m.Id, Upload.Rejected, "TOKENIFY refused it: " + (r.Str("detail") ?? "invalid"));
                        Log.Warn($"{m.File}: refused by TOKENIFY ({r.Str("detail")})");
                        continue;
                }

                if (HandleDeviceError(r)) return TimeSpan.FromMinutes(10);
                var attempts = m.Attempts + 1;
                var wait = BackoffSeconds[Math.Min(attempts - 1, BackoffSeconds.Length - 1)];
                _store.Retry(m.Id, attempts, Store.Now() + wait * 1000L, r.Error ?? "error");
                MarkOnline(!r.Network);
                return TimeSpan.FromSeconds(Math.Min(wait, 60));
            }
        }
        finally
        {
            lock (_gate) { _syncing = false; _statusDirty = true; }
        }

        return TimeSpan.FromSeconds(1);
    }

    /// <summary>Returns true (and stops syncing) when TOKENIFY no longer accepts this PC.</summary>
    private bool HandleDeviceError(ApiResult r)
    {
        if (r.Error is not ("device_revoked" or "device_unknown" or "unauthorized")) return false;
        lock (_gate) { _revoked = true; }
        Log.Warn("TOKENIFY no longer accepts this PC (" + r.Error + "); reconnect to keep syncing");
        RaiseNotify("TOKENIFY Companion", "This PC was disconnected from TOKENIFY. Open the companion and reconnect to keep syncing.");
        return true;
    }

    private void MarkOnline(bool online)
    {
        lock (_gate) { _online = online; }
    }

    private async Task<bool> Hello(Credentials.Device dev, CancellationToken ct)
    {
        var snap = Snapshot();
        var last = _store.Matches(1).FirstOrDefault(m => m.Upload == Upload.Sent);
        var status = new JsonObject
        {
            ["state"] = snap.Activity switch
            {
                Activity.Paused => "paused",
                Activity.InMatch => "in_match",
                Activity.Processing => "processing",
                Activity.Syncing => "syncing",
                Activity.NeedsAttention or Activity.FolderMissing => "needs_attention",
                Activity.UpToDate => "up_to_date",
                _ => "waiting",
            },
            ["folder"] = snap.Folder switch { FolderState.Ok => "ok", FolderState.Empty => "empty", FolderState.Missing => "missing", _ => "unreadable" },
            ["queued"] = snap.Queued,
            ["attention"] = snap.Attention.Count,
            ["parser"] = AppInfo.ParserVersion,
            ["auto"] = !snap.Paused,
        };
        if (last is not null)
        {
            status["last_match_chests"] = last.ShownChests;
            status["last_match_at"] = DateTimeOffset.FromUnixTimeMilliseconds(last.Ended).UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ss.fffZ", CultureInfo.InvariantCulture);
        }

        var r = await _api.Call("tk_device_hello", new JsonObject
        {
            ["p_device"] = dev.Id.ToString(), ["p_secret"] = dev.Secret, ["p_status"] = status, ["p_app_version"] = AppInfo.Version,
        }, ct);
        if (!r.Ok)
        {
            if (HandleDeviceError(r)) return true;
            MarkOnline(false);
            return false;
        }

        MarkOnline(true);
        var username = ApiResult.Text(r.Body?["account"]?["username"]);
        var tracking = r.Str("tracking") ?? "auto";
        _store.Set("account.username", username);
        _store.Set("device.name", r.Str("device_name"));
        _store.Set("account.tracking", tracking);
        if (r.Str("rotate_secret") is { Length: >= 32 } next)
        {
            Credentials.Save(_store, dev.Id, next);   // the old secret stays valid until the new one is first used
            Log.Info("Device secret renewed");
        }
        SetSeasonStart(r.Str("season_start"));

        var serverProfiles = (r.Body?["profiles"] as JsonArray)?.Select(p => ApiResult.Text(p?["player_hash"])).Where(h => h is not null).ToHashSet() ?? new();
        foreach (var p in _store.Profiles().Where(p => p.Decision == "mine"))
        {
            var claimedThere = serverProfiles.Contains(p.Hash);
            if (p.Claimed != claimedThere) _store.PutProfile(p with { Claimed = claimedThere });
        }

        if (ApiResult.Bool(r.Body?["rescan"]))
        {
            Log.Info("Rescan requested from the website");
            ScanNow();
        }

        lock (_gate)
        {
            _username = username;
            _deviceName = r.Str("device_name");
            _tracking = tracking;
            _revoked = false;
            _statusDirty = false;
            _nextHello = DateTime.UtcNow + HelloEvery;
        }
        return true;
    }

    private static JsonObject Payload(MatchRow m)
    {
        var times = JsonNode.Parse(m.Times) as JsonArray ?? new JsonArray();
        var breakdown = JsonNode.Parse(m.Breakdown) as JsonObject ?? new JsonObject();
        return new JsonObject
        {
            ["player_hash"] = m.PlayerHash,
            ["session_id"] = m.SessionId,
            ["replay_guid"] = m.ReplayGuid,
            ["started_at"] = DateTimeOffset.FromUnixTimeMilliseconds(m.Started).UtcDateTime.ToString("O", CultureInfo.InvariantCulture),
            ["ended_at"] = DateTimeOffset.FromUnixTimeMilliseconds(m.Ended).UtcDateTime.ToString("O", CultureInfo.InvariantCulture),
            ["build"] = m.Build is { Length: > 80 } b ? b[..80] : m.Build,
            ["parser_version"] = m.Parser,
            ["registry_version"] = m.Registry,
            ["chests"] = m.Chests,
            ["chest_times"] = times,
            ["breakdown"] = breakdown,
            ["historical"] = m.Historical,
            ["partial"] = m.Status == "partial",
        };
    }

    // ======================================================================================= state

    private void RaiseNotify(string title, string text, bool once = true)
    {
        if (once)
        {
            lock (_gate)
            {
                if (!_notified.Add(text)) return;
            }
        }
        try { Notify?.Invoke(title, text); } catch { /* UI gone */ }
    }

    public void Publish()
    {
        try { Changed?.Invoke(Snapshot()); } catch { /* UI gone */ }
    }

    public EngineSnapshot Snapshot()
    {
        var paired = Credentials.Load(_store) is not null;
        bool paused = Paused, anyLive, processing, syncing, online, revoked;
        FolderState folder;
        string folderPath, tracking;
        int files;
        string? username, deviceName;
        DateTime? lastSync;
        lock (_gate)
        {
            anyLive = _anyLive; processing = _processing; syncing = _syncing; online = _online; revoked = _revoked;
            folder = _folder; folderPath = _folderPath.Length > 0 ? _folderPath : ResolveFolder(); files = _replayFiles;
            username = _username; deviceName = _deviceName; tracking = _tracking; lastSync = _lastSyncUtc;
        }
        lastSync ??= DateTime.TryParse(_store.Get("sync.last"), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var ls) ? ls : null;

        var queued = _store.Count(Upload.Pending);
        var attention = new List<string>();
        if (revoked) attention.Add("This PC was disconnected from TOKENIFY. Reconnect to keep syncing.");
        if (folder == FolderState.Missing) attention.Add("Fortnite's replay folder wasn't found. Turn on Settings → Game → Replays → Record Replays in Fortnite, or choose the folder.");
        if (folder == FolderState.Unreadable) attention.Add("The replay folder can't be read. Check its permissions or choose another folder.");
        var profiles = _store.Profiles();
        var ask = profiles.FirstOrDefault(p => p.Decision == "ask");
        if (ask is not null) attention.Add($"Is the Fortnite account \"{ask.Name ?? "unknown"}\" yours? Its matches wait until you answer.");
        foreach (var c in profiles.Where(p => p.Error == "conflict")) attention.Add($"\"{c.Name ?? "A Fortnite account"}\" is linked to a different TOKENIFY account.");
        var partial = _store.Count(Upload.HeldPartial);
        if (partial > 0) attention.Add($"{partial} match{(partial == 1 ? "" : "es")} could only be read partly. Check them in Recent matches.");
        var rejected = _store.Count(Upload.Rejected);
        if (rejected > 0) attention.Add($"TOKENIFY refused {rejected} match{(rejected == 1 ? "" : "es")}. See Recent matches.");
        var fileRows = _store.Files();
        var unsupported = fileRows.Count(f => f.State == "unsupported");
        if (unsupported > 0) attention.Add($"{unsupported} replay{(unsupported == 1 ? " uses" : "s use")} a Fortnite version this companion can't read yet. Update the companion.");
        var held = fileRows.Count(f => f.State == "held");
        DateTime? hFrom = null, hTo = null;
        if (held > 0)
        {
            var dates = fileRows.Where(f => f.State == "held" && f.Started is not null).Select(f => DateTimeOffset.FromUnixTimeMilliseconds(f.Started!.Value).LocalDateTime).ToList();
            if (dates.Count > 0) { hFrom = dates.Min(); hTo = dates.Max(); }
        }
        if (held > 0 && paired) attention.Add($"{held} older replay{(held == 1 ? " is" : "s are")} on this PC. Choose whether to import them (Status tab).");

        var lastMatch = _store.Matches(10).FirstOrDefault(m => m.Upload is Upload.Sent or Upload.Pending && !m.Historical);
        Activity activity;
        string status;
        if (!paired) { activity = Activity.NotConnected; status = "Not connected"; }
        else if (revoked) { activity = Activity.NeedsAttention; status = "Disconnected: reconnect"; }
        else if (paused) { activity = Activity.Paused; status = "Automatic tracking paused"; }
        else if (folder is FolderState.Missing or FolderState.Unreadable) { activity = Activity.FolderMissing; status = "Replays off / folder not found"; }
        else if (processing) { activity = Activity.Processing; status = "Processing replay…"; }
        else if (syncing) { activity = Activity.Syncing; status = "Syncing…"; }
        else if (anyLive) { activity = Activity.InMatch; status = "Match in progress"; }
        else if (attention.Count > 0) { activity = Activity.NeedsAttention; status = "Needs attention"; }
        else if (queued > 0 && !online) { activity = Activity.Offline; status = $"{queued} match{(queued == 1 ? "" : "es")} waiting to sync"; }
        else if (queued > 0) { activity = Activity.Syncing; status = $"{queued} match{(queued == 1 ? "" : "es")} waiting to sync"; }
        else if (lastMatch is not null) { activity = Activity.UpToDate; status = "All matches synced"; }
        else { activity = Activity.Waiting; status = "Waiting for a match"; }

        return new EngineSnapshot(paired, username, deviceName, tracking, paused, revoked, folderPath, folder, files, activity, status,
            queued, attention, lastMatch, lastSync, online, held, hFrom, hTo, unsupported, ask,
            fileRows.Count(f => f.State == "pre-season"), SeasonStartUtc);
    }

    public void Dispose()
    {
        _cts.Cancel();
        _scanThread?.Join(TimeSpan.FromSeconds(3));
        _syncThread?.Join(TimeSpan.FromSeconds(3));
        _watcher?.Dispose();
        _api.Dispose();
        _scanWake.Dispose();
        _syncWake.Dispose();
    }
}
