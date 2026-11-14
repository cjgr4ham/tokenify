using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Tokenify.ReplayReader;

namespace Tokenify.Core;

public enum FolderState { Ok, Empty, Missing, Unreadable }

/// <summary>What the tracker is doing right now, in the order the overlay cares about.</summary>
public enum Activity { Paused, FolderMissing, InMatch, Processing, NeedsAttention, Waiting, Ready }

public enum SyncState { NotLinked, Disconnected, Offline, Syncing, Queued, UpToDate }

/// <summary>The party's total for the last squad game, as far as TOKENIFY has it.</summary>
public sealed record SquadInfo(int Chests, bool Complete, int Members, int Reported, DateTime? EndedUtc);

/// <summary>Everything the overlay, the tray and the windows show, as one immutable snapshot.</summary>
public sealed record TrackerSnapshot(
    Settings Settings,
    Stats Stats,
    Activity Activity,
    string Status,
    SyncState Sync,
    string SyncText,
    bool Paired,
    string? Username,
    string? DeviceName,
    bool Online,
    string FolderPath,
    FolderState Folder,
    int ReplayFiles,
    int Queued,
    IReadOnlyList<string> Attention,
    MatchRow? LastMatch,
    DateTime? LastSyncUtc,
    int HistoryHeld,
    DateTime? HistoryFrom,
    DateTime? HistoryTo,
    int UnsupportedFiles,
    ProfileRow? AskProfile,
    ProfileRow? ActiveProfile,
    int PreSeasonFiles,
    DateTime SeasonStartUtc,
    int NeedsReview,
    SquadInfo? Squad,
    int ManualTaps)
{
    /// <summary>Chests the player's party members opened in squad games with them this season (known counts only).</summary>
    public int PartyChests { get; init; }
    /// <summary>How many squad games that covers.</summary>
    public int PartyGames { get; init; }
    /// <summary>The player's own total plus <see cref="PartyChests"/>.</summary>
    public int SquadTotal => Stats.TotalChests + PartyChests;
}

/// <summary>
/// The tracker's background work, on two low-priority threads:
/// <list type="bullet">
/// <item>Scanner: watches the replay folder (file notifications + a light periodic listing), waits until a replay is
/// finished (Fortnite writes its decryption key only when the match ends, and the file must stop changing), reads it
/// in a child process and stores the result with its evidence. Files are only ever opened read-only. It works
/// with or without a TOKENIFY account.</item>
/// <item>Sync (only when linked): checks in with TOKENIFY, confirms the player's Fortnite account, and uploads one
/// summary per match and any corrections, retrying with back-off while offline. Uploads are idempotent on the
/// server, so retries never double count.</item>
/// </list>
/// Nothing here reads, opens or talks to the game: only the replay files it leaves on disk.
/// </summary>
public sealed class Tracker : IDisposable
{
    private static readonly TimeSpan ParseTimeout = TimeSpan.FromSeconds(120);
    private static readonly TimeSpan HelloEvery = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan ReconcileEvery = TimeSpan.FromMinutes(30);
    private static readonly JsonSerializerOptions EventJson = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly Store _store;
    private readonly ApiClient _api = new();
    private readonly CancellationTokenSource _cts = new();
    private readonly AutoResetEvent _scanWake = new(false);
    private readonly AutoResetEvent _syncWake = new(false);
    private readonly object _gate = new();
    private Thread? _scanThread, _syncThread;
    private FileSystemWatcher? _watcher;
    private string? _watchedFolder;
    private Settings _settings;

    // live state (guarded by _gate)
    private FolderState _folder = FolderState.Missing;
    private string _folderPath = "";
    private int _replayFiles;
    private bool _anyLive, _processing, _syncing, _online = true, _revoked, _statusDirty = true, _correctionsUnsupported;
    private DateTime? _lastSyncUtc;
    private DateTime _nextHello = DateTime.MinValue, _nextReconcile = DateTime.MinValue;
    private long _liveSince;
    /// <summary>When the replay reader last failed to start (0 = it works). See ReplayWorker.UnavailablePrefix.</summary>
    private long _readerBrokenAt;
    private string? _username, _deviceName;
    private readonly HashSet<string> _notified = new();
    private readonly List<long> _taps = new();

    public event Action<TrackerSnapshot>? Changed;
    /// <summary>(title, text): a balloon the tray may show.</summary>
    public event Action<string, string>? Notify;
    /// <summary>A match that has just been counted for the first time (not history): the match and its chest count.</summary>
    public event Action<MatchRow, int>? MatchImported;

    public Tracker(Store store)
    {
        _store = store;
        _settings = store.LoadSettings();
        _username = store.Get("account.username");
        _deviceName = store.Get("device.name");
        if (store.Get("history.cutoff") is null)
        {
            store.Set("history.cutoff", Store.Now().ToString(CultureInfo.InvariantCulture));
        }
        try
        {
            if (JsonSerializer.Deserialize<List<long>>(store.Get("manual.taps") ?? "[]") is { } saved) _taps.AddRange(saved);
        }
        catch (JsonException) { }
        _folderPath = ResolveFolder();
        (_folder, var files) = ListReplays(_folderPath);
        _replayFiles = files.Count;
    }

    public Store Store => _store;
    public Settings Settings => _settings;
    public bool Paired => Credentials.Load(_store) is not null;
    public bool Paused => _settings.Tracking.Paused;

    /// <summary>Changes settings: applies the change to a copy, saves it and tells everyone.</summary>
    public void UpdateSettings(Action<Settings> change)
    {
        Settings before, after;
        lock (_gate)
        {
            before = _settings;
            after = before.Clone();
            change(after);
            after.Sanitized();
            _settings = after;
        }
        _store.SaveSettings(after);

        if (before.Tracking.CountDestroyed != after.Tracking.CountDestroyed)
        {
            // What is sent for a match with destroyed chests changes with this choice.
            _store.RequeueSent(onlyWithDestroyed: true);
            Log.Info(after.Tracking.CountDestroyed ? "Destroyed chests are counted" : "Destroyed chests are not counted");
            SyncNow();
        }
        if (before.Tracking.Paused != after.Tracking.Paused)
        {
            Log.Info(after.Tracking.Paused ? "Tracking paused" : "Tracking resumed");
            ScanNow();
            SyncNow();
        }
        if (before.Tracking.ReplayFolder != after.Tracking.ReplayFolder) ScanNow();
        Publish();
    }

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
        Log.Info($"{AppInfo.Name} {AppInfo.Version} started (counting rules {AppInfo.ParserVersion}, chest registry v{ChestRegistry.Default.Version})");
    }

    public void ScanNow() => _scanWake.Set();

    public void SyncNow()
    {
        lock (_gate) { _statusDirty = true; }
        _syncWake.Set();
    }

    public string ResolveFolder() => _settings.Tracking.ReplayFolder is { Length: > 0 } custom ? custom : AppPaths.DefaultDemos;

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
                SetLive(false);   // an aborted pass must not leave "match in progress" on the overlay
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

    /// <summary>True while another program (Fortnite) has the file open for writing. Only ever asks to read.</summary>
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
            SetLive(false);
            return;
        }

        EnsureWatcher(folder);
        if (Paused)
        {
            SetLive(false);
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
                // An older replay found at first launch: only its date is read until the player decides whether to count history.
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

            // The reader could not be started a moment ago: try again once a minute, not for every file on every pass.
            long brokenAt;
            lock (_gate) { brokenAt = _readerBrokenAt; }
            if (brokenAt != 0 && now - brokenAt < 60_000) break;

            Parse(f, mtime, row, isHistory);
            lock (_gate) { brokenAt = _readerBrokenAt; }
            if (brokenAt != 0) break;
        }

        SetLive(anyLive);
    }

    private void SetLive(bool live)
    {
        lock (_gate)
        {
            if (live && !_anyLive) _liveSince = Store.Now();
            _anyLive = live;
        }
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
        if (result is null && failure is not null && failure.StartsWith(ReplayWorker.UnavailablePrefix, StringComparison.Ordinal))
        {
            // Nothing is wrong with the replay: the tracker's own program file was moved, renamed or blocked while
            // it runs, so the reader cannot start. No attempt is held against the file; it is read as soon as the
            // tracker runs from a working file again.
            bool first;
            lock (_gate)
            {
                first = _readerBrokenAt == 0;
                _readerBrokenAt = now;
            }
            if (first) Log.Error($"{f.Name}: {failure}. Exit the tracker and start it again from where its file is now.");
            return;
        }
        lock (_gate) { _readerBrokenAt = 0; }
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
            Searched = result.Searched,
            Destroyed = result.Destroyed,
            Ambiguous = result.Ambiguous,
            NoRecord = result.Unobservable,
            Events = JsonSerializer.Serialize(result.Events, EventJson),
            Breakdown = JsonSerializer.Serialize(Breakdown(result)),
            Times = JsonSerializer.Serialize(result.Chests.Select(c => Math.Round(c.Time, 1)).ToArray()),
            Status = result.Status == ScanStatus.Complete ? "complete" : "partial",
            Parser = AppInfo.ParserVersion,
            Registry = registry.Version,
            Build = result.Branch,
            Historical = historical,
        };
        // Always kept, also when the match has ids: it is what ties a later read to this row if the ids differ.
        match.Fingerprint = Fingerprint(f.FullName);
        match.Upload = decision switch
        {
            "not-mine" => Upload.NotMine,
            "ask" => Upload.HeldProfile,
            _ when match.Status == "partial" => Upload.HeldPartial,
            _ when !match.HasServerIdentity => Upload.LocalOnly,
            _ => Upload.Pending,
        };
        if (!match.HasServerIdentity && match.Fingerprint is null)
        {
            _store.PutFile(new FileRow(f.Name, f.Length, mtime, "failed", now, 3, "the replay has nothing to identify the match by", AppInfo.ParserVersion, registry.Version, row?.MatchId, started));
            return;
        }

        var existing = _store.FindMatch(hash, match.MatchKey, match.ReplayGuid, match.Fingerprint);
        long id;
        if (existing is null)
        {
            id = _store.InsertMatch(match);
            match.Id = id;
            Log.Info($"{f.Name}: {match.Chests} chests ({match.Searched} searched, {match.Destroyed} destroyed, {match.Ambiguous} ambiguous; {match.Status}){(historical ? ", history" : "")}");
            AttachTaps(match);
            if (!historical && decision == "mine" && match.Status == "complete")
            {
                var count = match.Effective(_settings.Tracking.CountDestroyed);
                try { MatchImported?.Invoke(match, count); } catch { /* UI gone */ }
            }
        }
        else
        {
            id = existing.Id;
            var changed = existing.Chests != match.Chests || existing.Times != match.Times || existing.Parser != match.Parser
                          || existing.Registry != match.Registry || existing.Status != match.Status || existing.MatchKey != match.MatchKey
                          || existing.Events is null || existing.Ambiguous != match.Ambiguous || existing.Destroyed != match.Destroyed;
            if (changed)
            {
                match.Id = existing.Id;
                // A re-read never changes whose match it is or a decision the player already made about it.
                match.Upload = existing.Upload is Upload.NotMine or Upload.Ignored ? existing.Upload
                    // (a changed key is sent again, so the website learns the match's new id and later corrections find it)
                    : existing.Upload == Upload.Sent && existing.Chests == match.Chests && existing.Times == match.Times && existing.Status == match.Status
                      && existing.MatchKey == match.MatchKey ? Upload.Sent
                    : match.Upload;
                match.Historical = existing.Historical;
                _store.UpdateParsed(match);
                if (match.Upload == Upload.Sent) _store.MarkSent(match.Id, existing.ServerId, existing.ServerChests, existing.ServerStatus);
                if (existing.Chests != match.Chests) Log.Info($"{f.Name}: re-read with counting rules {match.Parser}: {existing.Chests} -> {match.Chests} chests");
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

    /// <summary>Identity of last resort for a replay with no ids: a hash of its header, the same for any copy or rename.</summary>
    private static string? Fingerprint(string path)
    {
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            var buffer = new byte[(int)Math.Min(16384, fs.Length)];
            fs.ReadExactly(buffer);
            return Convert.ToHexStringLower(SHA256.HashData(buffer))[..32];
        }
        catch (IOException)
        {
            return null;
        }
    }

    private static Dictionary<string, int> Breakdown(ScanResult r)
    {
        var d = r.ChestsByKind();
        void Add(string key, int value) { if (value > 0) d[key] = value; }
        Add("searched", r.Searched);
        Add("destroyed", r.Destroyed);
        Add("corroborated", r.Corroborated);
        Add("ambiguous", r.Ambiguous);
        Add("reopened", r.Chests.Count(c => c.Reopened));
        Add("unverified", r.Chests.Count(c => c.Unverified));
        return d;
    }

    /// <summary>
    /// Whose Fortnite account recorded this replay? The first account ever seen on this PC is taken to be the
    /// owner's (they are told, and can change it); any further account is asked about once before anything of it is
    /// counted or sent. A "not mine" account is never counted.
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
            RaiseNotify("TOKENIFY", $"Counting chests for Fortnite player {name ?? "(unknown name)"}. Not you? Open the tracker to change it.");
        }
        else
        {
            RaiseNotify("TOKENIFY", $"Another Fortnite account ({name ?? "unknown"}) played on this PC. Open the tracker to say whether it's yours.");
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
            _store.SetSentExcludedForProfile(hash, false);
        }
        else
        {
            _store.SetUploadForProfile(hash, Upload.HeldProfile, Upload.NotMine);
            _store.SetUploadForProfile(hash, Upload.Pending, Upload.NotMine);
            _store.SetUploadForProfile(hash, Upload.LocalOnly, Upload.NotMine);
            // Matches of that account that already went to TOKENIFY are left out here and there.
            _store.SetSentExcludedForProfile(hash, true);
            if (Credentials.Load(_store) is not null && _store.AllMatchesLight().Any(m => m.PlayerHash == hash && m.Upload == Upload.Sent))
            {
                RaiseNotify("TOKENIFY", "That account's matches are being taken out of your TOKENIFY totals. To unlink the Fortnite account itself, use the website: Automatic PC tracking, Fortnite accounts.");
            }
        }
        Log.Info($"Fortnite account {hash[..8]}… marked as {(mine ? "mine" : "not mine")}");
        SyncNow();
        Publish();
    }

    /// <summary>History choice (setup): count the replays that were already on the PC, or start from now.</summary>
    public void SetHistoryChoice(bool import)
    {
        _store.Set("history.choice", import ? "import" : "skip");
        _store.ReleaseHistory(import);
        if (!import) _store.SetFileState("skipped", "held");
        else _store.SetFileState("new", "held");
        Log.Info(import ? "Counting replays that were already on this PC" : "Skipping replays from before setup");
        ScanNow();
        Publish();
    }

    /// <summary>Older replays that could be counted: only this season's (earlier seasons are never counted).</summary>
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

    /// <summary>"Rescan replays": every replay still on disk is read again. Nothing already counted is duplicated.</summary>
    public void Rescan()
    {
        _store.ForgetFiles();
        Log.Info("Rescan: replays still on disk will be read again");
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

    // ======================================================================================= corrections and sessions

    /// <summary>The player's own count for a match (null takes the correction away again). The replay's count stays on record.</summary>
    public void Correct(long matchId, int? count, string? reason)
    {
        var m = _store.GetMatch(matchId);
        if (m is null) return;
        if (count is int c) count = Math.Clamp(c, 0, 400);
        _store.Correct(matchId, count, reason, "you", m.ReplayCount(_settings.Tracking.CountDestroyed));
        if (m.Upload == Upload.HeldPartial && count is not null) _store.SetUpload(matchId, m.HasServerIdentity ? Upload.Pending : Upload.LocalOnly);
        _store.SetReviewed(matchId, true);
        Log.Info(count is null ? $"Correction removed from {m.File}" : $"{m.File} corrected: replay {m.ReplayCount(_settings.Tracking.CountDestroyed)}, you {count}");
        SyncNow();
        Publish();
    }

    public void SetExcluded(long matchId, bool excluded)
    {
        _store.SetExcluded(matchId, excluded);
        SyncNow();
        Publish();
    }

    public void SetReviewed(long matchId, bool reviewed)
    {
        _store.SetReviewed(matchId, reviewed);
        Publish();
    }

    /// <summary>A partly read match: use its count as it is (recorded as the player's decision), or leave it out.</summary>
    public void ResolvePartial(long matchId, bool use)
    {
        var m = _store.GetMatch(matchId);
        if (m is null) return;
        if (use) Correct(matchId, m.ReplayCount(_settings.Tracking.CountDestroyed), "partly read replay, count accepted as it is");
        else
        {
            _store.SetUpload(matchId, Upload.Ignored);
            Publish();
        }
    }

    public void StartSession()
    {
        _store.StartSession(Store.Now());
        Log.Info("Session started");
        Publish();
    }

    public void EndSession()
    {
        _store.EndSession(Store.Now());
        Log.Info("Session ended");
        Publish();
    }

    /// <summary>
    /// One tap of the optional manual shortcut: the player's own running count for the match being played. It is
    /// only ever shown next to the replay's count afterwards; it never adds to the total by itself.
    /// </summary>
    public void ManualTap()
    {
        lock (_gate)
        {
            var now = Store.Now();
            if (_taps.Count > 0 && now - _taps[^1] < 150) return;   // a held key or a bounce is one tap
            _taps.Add(now);
            _taps.RemoveAll(t => now - t > 3 * 3600_000L);
            _store.Set("manual.taps", JsonSerializer.Serialize(_taps));
        }
        Publish();
    }

    public void UndoManualTap()
    {
        lock (_gate)
        {
            if (_taps.Count == 0) return;
            _taps.RemoveAt(_taps.Count - 1);
            _store.Set("manual.taps", JsonSerializer.Serialize(_taps));
        }
        Publish();
    }

    private void AttachTaps(MatchRow match)
    {
        int count;
        lock (_gate)
        {
            count = _taps.Count(t => t >= match.Started && t <= match.Ended + 120_000);
            if (count == 0) return;
            _taps.RemoveAll(t => t <= match.Ended + 120_000);
            _store.Set("manual.taps", JsonSerializer.Serialize(_taps));
        }
        _store.SetManualTaps(match.Id, count);
        match.ManualTaps = count;
    }

    public void DeleteHistory()
    {
        _store.DeleteHistory();
        lock (_gate) { _taps.Clear(); }
        _store.Set("manual.taps", null);
        // Replays already on disk are history again: they are not silently counted back in.
        _store.Set("history.cutoff", Store.Now().ToString(CultureInfo.InvariantCulture));
        _store.Set("history.choice", "skip");
        Log.Info("History deleted on this PC");
        ScanNow();
        Publish();
    }

    public void ResetSettings()
    {
        var countedDestroyed = _settings.Tracking.CountDestroyed;
        _store.ResetSettings();
        lock (_gate) { _settings = _store.LoadSettings(); }
        _settings.General.SetupDone = true;
        _store.SaveSettings(_settings);
        if (_settings.Tracking.CountDestroyed != countedDestroyed)
        {
            // what was sent used the old rule: send those matches again so the website shows the same numbers
            _store.RequeueSent(true);
            SyncNow();
        }
        Log.Info("Settings reset to defaults");
        Publish();
    }

    /// <summary>The chest events stored with a match (empty when it was counted by a version that kept none).</summary>
    public static List<ChestEvent> EventsOf(MatchRow match)
    {
        if (string.IsNullOrEmpty(match.Events)) return new();
        try
        {
            return JsonSerializer.Deserialize<List<ChestEvent>>(match.Events, EventJson) ?? new();
        }
        catch (JsonException)
        {
            return new();
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
        lock (_gate)
        {
            _username = username;
            _deviceName = name;
            _revoked = false;
            _nextHello = DateTime.MinValue;
            _nextReconcile = DateTime.MinValue;
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
        _store.Set("squad.last", null);
        _store.Set("squad.season", null);
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
                RaiseNotify("TOKENIFY", $"The Fortnite account {p.Name ?? ""} is linked to a different TOKENIFY account, so its matches aren't sent here.");
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
            await SendCorrections(dev, ct);
            bool reconcileDue;
            lock (_gate) { reconcileDue = DateTime.UtcNow >= _nextReconcile; }
            if (reconcileDue) await Reconcile(dev, ct);
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
                    Log.Info($"Sent {m.File}: {m.ReplayCount(_settings.Tracking.CountDestroyed)} chests ({r.Str("status")})");
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

    /// <summary>Sends corrections and "leave this match out" choices made here, so the website shows the same count.</summary>
    private async Task SendCorrections(Credentials.Device dev, CancellationToken ct)
    {
        lock (_gate) { if (_correctionsUnsupported) return; }
        foreach (var m in _store.UnsyncedCorrections().Take(20))
        {
            if (!m.HasServerIdentity)
            {
                _store.MarkCorrectionSynced(m.Id);
                continue;
            }
            var r = await _api.Call("tk_device_correct", new JsonObject
            {
                ["p_device"] = dev.Id.ToString(), ["p_secret"] = dev.Secret, ["p_player_hash"] = m.PlayerHash, ["p_match_key"] = m.MatchKey,
                ["p_chests"] = m.Corrected, ["p_excluded"] = m.Excluded, ["p_reason"] = m.CorrectionReason,
            }, ct);
            if (r.Ok || r.Error == "not_found")
            {
                _store.MarkCorrectionSynced(m.Id);
            }
            else if (r.Error == "unsupported")
            {
                // The website's backend is older than this tracker: corrections stay here until it is updated.
                lock (_gate) { _correctionsUnsupported = true; }
                Log.Info("TOKENIFY does not accept corrections from the tracker yet; they are kept on this PC");
                return;
            }
            else
            {
                if (HandleDeviceError(r)) return;
                MarkOnline(!r.Network);
                return;
            }
        }
    }

    /// <summary>Takes over corrections and exclusions made on the website, so both show the same count.</summary>
    private async Task Reconcile(Credentials.Device dev, CancellationToken ct)
    {
        lock (_gate) { _nextReconcile = DateTime.UtcNow + ReconcileEvery; }
        var r = await _api.Call("tk_device_matches", new JsonObject { ["p_device"] = dev.Id.ToString(), ["p_secret"] = dev.Secret, ["p_limit"] = 300 }, ct);
        if (!r.Ok || r.Body?["matches"] is not JsonArray rows) return;
        var countDestroyed = _settings.Tracking.CountDestroyed;
        foreach (var row in rows)
        {
            if (ApiResult.Text(row?["match_key"]) is not { } key || _store.FindByKey(key) is not { } local) continue;
            if (!local.CorrectionSynced) continue;   // a change made here is still on its way: it wins
            var corrected = ApiResult.Bool(row?["corrected"]);
            var serverCount = ApiResult.Int(row?["chests"]);
            if (corrected && serverCount is int c && local.Corrected != c)
            {
                _store.Correct(local.Id, c, "changed on the website", "website", local.ReplayCount(countDestroyed));
            }
            else if (!corrected && local.Corrected is not null && local.CorrectionSource == "website")
            {
                _store.Correct(local.Id, null, "correction removed on the website", "website", local.ReplayCount(countDestroyed));
            }
            var excluded = ApiResult.Bool(row?["excluded"]);
            if (excluded != local.Excluded) _store.SetExcludedFromServer(local.Id, excluded);
        }
    }

    /// <summary>Returns true (and stops syncing) when TOKENIFY no longer accepts this PC.</summary>
    private bool HandleDeviceError(ApiResult r)
    {
        if (r.Error is not ("device_revoked" or "device_unknown" or "unauthorized")) return false;
        lock (_gate) { _revoked = true; }
        Log.Warn("TOKENIFY no longer accepts this PC (" + r.Error + "); reconnect to keep syncing");
        RaiseNotify("TOKENIFY", "This PC was disconnected from TOKENIFY. Open the tracker and reconnect to keep syncing.");
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
                Activity.NeedsAttention or Activity.FolderMissing => "needs_attention",
                Activity.Ready => snap.Sync == SyncState.Syncing ? "syncing" : "up_to_date",
                _ => "waiting",
            },
            ["folder"] = snap.Folder switch { FolderState.Ok => "ok", FolderState.Empty => "empty", FolderState.Missing => "missing", _ => "unreadable" },
            ["queued"] = snap.Queued,
            ["attention"] = snap.Attention.Count,
            ["parser"] = AppInfo.ParserVersion,
            ["auto"] = !Paused,
        };
        if (last is not null)
        {
            status["last_match_chests"] = last.Effective(_settings.Tracking.CountDestroyed);
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
        _store.Set("account.username", username);
        _store.Set("device.name", r.Str("device_name"));
        if (r.Str("rotate_secret") is { Length: >= 32 } next)
        {
            Credentials.Save(_store, dev.Id, next);   // the old secret stays valid until the new one is first used
            Log.Info("Device secret renewed");
        }
        SetSeasonStart(r.Str("season_start"));
        _store.Set("squad.last", r.Body?["squad"] is JsonObject squad ? squad.ToJsonString() : null);
        _store.Set("squad.season", r.Body?["squad_season"] is JsonObject season ? season.ToJsonString() : null);

        var serverProfiles = (r.Body?["profiles"] as JsonArray)?.Select(p => ApiResult.Text(p?["player_hash"])).Where(h => h is not null).ToHashSet() ?? new();
        foreach (var p in _store.Profiles().Where(p => p.Decision == "mine"))
        {
            var claimedThere = serverProfiles.Contains(p.Hash);
            if (p.Claimed != claimedThere) _store.PutProfile(p with { Claimed = claimedThere });
        }

        if (ApiResult.Bool(r.Body?["rescan"]))
        {
            Log.Info("Rescan requested from the website");
            Rescan();
        }

        lock (_gate)
        {
            _username = username;
            _deviceName = r.Str("device_name");
            _revoked = false;
            _statusDirty = false;
            _nextHello = DateTime.UtcNow + HelloEvery;
        }
        return true;
    }

    /// <summary>The summary sent for one match. Never the replay itself, and no Epic account id (only its one-way hash).</summary>
    private JsonObject Payload(MatchRow m)
    {
        var countDestroyed = _settings.Tracking.CountDestroyed;
        var count = m.ReplayCount(countDestroyed);
        JsonArray times;
        var full = _store.GetMatch(m.Id) ?? m;
        var events = EventsOf(full).Where(e => e.Counted && (countDestroyed || e.Action != ChestAction.Destroyed)).ToList();
        if (events.Count == count)
        {
            times = new JsonArray(events.Select(e => (JsonNode)Math.Round(e.Time, 1)).ToArray());
        }
        else
        {
            var all = JsonNode.Parse(m.Times) as JsonArray ?? new JsonArray();
            times = new JsonArray(all.Take(count).Select(t => (JsonNode)(t?.GetValue<double>() ?? 0)).ToArray());
            while (times.Count < count) times.Add(0);
        }

        var breakdown = JsonNode.Parse(m.Breakdown) as JsonObject ?? new JsonObject();
        if (!countDestroyed) breakdown.Remove("destroyed");
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
            ["chests"] = count,
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

    /// <summary>The Fortnite account whose numbers are shown: the chosen one, or the one that played most recently.</summary>
    private ProfileRow? ActiveProfile(List<ProfileRow> profiles, List<MatchRow> matches)
    {
        var mine = profiles.Where(p => p.Decision == "mine").ToList();
        if (mine.Count == 0) return null;
        var chosen = _settings.Tracking.Profile;
        if (chosen != "auto" && mine.FirstOrDefault(p => p.Hash == chosen) is { } fixedProfile) return fixedProfile;
        var hashes = mine.Select(p => p.Hash).ToHashSet();
        var lastHash = matches.LastOrDefault(m => hashes.Contains(m.PlayerHash))?.PlayerHash;
        return mine.FirstOrDefault(p => p.Hash == lastHash) ?? mine[0];
    }

    /// <summary>The valid games of one Fortnite account, as the statistics need them.</summary>
    public static List<GameStat> ValidGames(IEnumerable<MatchRow> matches, string? playerHash, Settings settings)
    {
        var from = settings.Tracking.StartDate;
        return matches
            .Where(m => m.PlayerHash == playerHash && m.Valid && (from is null || m.StartedLocal >= from.Value))
            .Select(m => new GameStat(m.Id, m.StartedUtc, m.EndedUtc, m.Effective(settings.Tracking.CountDestroyed), m.Corrected is not null))
            .ToList();
    }

    public TrackerSnapshot Snapshot()
    {
        var settings = _settings;
        var paired = Credentials.Load(_store) is not null;
        bool paused = settings.Tracking.Paused, anyLive, processing, syncing, online, revoked, readerBroken;
        FolderState folder;
        string folderPath;
        int files, taps;
        long liveSince;
        string? username, deviceName;
        DateTime? lastSync;
        lock (_gate)
        {
            anyLive = _anyLive; processing = _processing; syncing = _syncing; online = _online; revoked = _revoked; readerBroken = _readerBrokenAt != 0;
            folder = _folder; folderPath = _folderPath.Length > 0 ? _folderPath : ResolveFolder(); files = _replayFiles;
            username = _username; deviceName = _deviceName; lastSync = _lastSyncUtc; liveSince = _liveSince;
            taps = anyLive ? _taps.Count(t => t >= liveSince - 60_000) : 0;
        }
        lastSync ??= DateTime.TryParse(_store.Get("sync.last"), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var ls) ? ls : null;

        var matches = _store.AllMatchesLight();
        var profiles = _store.Profiles();
        var active = ActiveProfile(profiles, matches);
        var games = ValidGames(matches, active?.Hash, settings);
        var stats = Stats.Compute(games, new StatsOptions(settings.Tracking.StartingTotal, settings.Tracking.SessionMode, settings.Tracking.SessionGapMinutes, anyLive),
            DateTime.UtcNow, _store.Sessions());
        var lastMatch = stats.LastGame is { } lg ? matches.FirstOrDefault(m => m.Id == lg.Id) : null;

        var queued = paired ? matches.Count(m => m.Upload == Upload.Pending) : 0;
        var attention = new List<string>();
        if (readerBroken) attention.Add("The tracker can't read replays right now: its program file was moved, renamed or blocked while it was running. Exit the tracker (tray menu) and start it again.");
        if (revoked) attention.Add("This PC was disconnected from TOKENIFY. Reconnect to keep syncing.");
        if (folder == FolderState.Missing) attention.Add("Fortnite's replay folder wasn't found. Turn on Settings → Game → Replays → Record Replays in Fortnite, or choose the folder.");
        if (folder == FolderState.Unreadable) attention.Add("The replay folder can't be read. Check its permissions or choose another folder.");
        var ask = profiles.FirstOrDefault(p => p.Decision == "ask");
        if (ask is not null) attention.Add($"Is the Fortnite account \"{ask.Name ?? "unknown"}\" yours? Its matches wait until you answer.");
        foreach (var c in profiles.Where(p => p.Error == "conflict")) attention.Add($"\"{c.Name ?? "A Fortnite account"}\" is linked to a different TOKENIFY account.");
        var partial = matches.Count(m => m.Upload == Upload.HeldPartial && m.Corrected is null);
        if (partial > 0) attention.Add($"{partial} match{(partial == 1 ? "" : "es")} could only be read partly and {(partial == 1 ? "is" : "are")} not counted yet. Check {(partial == 1 ? "it" : "them")} in Matches.");
        var rejected = matches.Count(m => m.Upload == Upload.Rejected);
        if (rejected > 0) attention.Add($"TOKENIFY refused {rejected} match{(rejected == 1 ? "" : "es")}. See Matches.");
        var fileRows = _store.Files();
        var unsupported = fileRows.Count(f => f.State == "unsupported");
        if (unsupported > 0) attention.Add($"{unsupported} replay{(unsupported == 1 ? " uses" : "s use")} a Fortnite version this tracker can't read yet. {(unsupported == 1 ? "It is" : "They are")} kept and not counted as zero. Update the tracker.");
        var held = fileRows.Count(f => f.State == "held");
        DateTime? hFrom = null, hTo = null;
        if (held > 0)
        {
            var dates = fileRows.Where(f => f.State == "held" && f.Started is not null).Select(f => DateTimeOffset.FromUnixTimeMilliseconds(f.Started!.Value).LocalDateTime).ToList();
            if (dates.Count > 0) { hFrom = dates.Min(); hTo = dates.Max(); }
        }
        if (held > 0 && settings.General.SetupDone) attention.Add($"{held} older replay{(held == 1 ? " is" : "s are")} on this PC. Choose whether to count {(held == 1 ? "it" : "them")} (Overview).");
        // Everything above stops something from being counted or synced. What follows is only worth a look, so it
        // is listed in the window but never puts a warning over the game.
        var blocking = attention.Count;
        var review = matches.Count(m => m.PlayerHash == active?.Hash && m.Ambiguous > 0 && !m.Reviewed && m.Corrected is null && m.Valid);
        if (review > 0) attention.Add($"{review} match{(review == 1 ? " has" : "es have")} chests the replay could not attribute. They are not counted; review {(review == 1 ? "it" : "them")} in Matches.");

        Activity activity;
        string status;
        if (paused) { activity = Activity.Paused; status = "TRACKING PAUSED"; }
        else if (folder is FolderState.Missing or FolderState.Unreadable) { activity = Activity.FolderMissing; status = "REPLAYS NOT FOUND"; }
        else if (processing) { activity = Activity.Processing; status = "READING REPLAY"; }
        else if (anyLive) { activity = Activity.InMatch; status = "MATCH IN PROGRESS"; }
        else if (blocking > 0) { activity = Activity.NeedsAttention; status = "NEEDS ATTENTION"; }
        else if (stats.Games > 0) { activity = Activity.Ready; status = "READY"; }
        else { activity = Activity.Waiting; status = "WAITING FOR A MATCH"; }

        SyncState sync;
        string syncText;
        if (!paired) { sync = SyncState.NotLinked; syncText = "Not linked to TOKENIFY"; }
        else if (revoked) { sync = SyncState.Disconnected; syncText = "Disconnected: reconnect"; }
        else if (syncing) { sync = SyncState.Syncing; syncText = "Syncing…"; }
        else if (queued > 0 && !online) { sync = SyncState.Offline; syncText = $"Offline: {queued} match{(queued == 1 ? "" : "es")} waiting"; }
        else if (queued > 0) { sync = SyncState.Queued; syncText = $"{queued} match{(queued == 1 ? "" : "es")} waiting to sync"; }
        else if (!online) { sync = SyncState.Offline; syncText = "Offline"; }
        else { sync = SyncState.UpToDate; syncText = "Synced with TOKENIFY"; }

        return new TrackerSnapshot(settings, stats, activity, status, sync, syncText, paired, username, deviceName, online, folderPath, folder, files,
            queued, attention, lastMatch, lastSync, held, hFrom, hTo, unsupported, ask, active,
            fileRows.Count(f => f.State == "pre-season"), SeasonStartUtc, review, ReadSquad(), taps)
        {
            PartyChests = paired ? ReadSeason("mates_chests") : 0,
            PartyGames = paired ? ReadSeason("games") : 0,
        };
    }

    private int ReadSeason(string key)
    {
        if (_store.Get("squad.season") is not { Length: > 0 } json) return 0;
        try
        {
            return Math.Max(0, ApiResult.Int((JsonNode.Parse(json) as JsonObject)?[key]) ?? 0);
        }
        catch (JsonException)
        {
            return 0;
        }
    }

    private SquadInfo? ReadSquad()
    {
        if (_store.Get("squad.last") is not { Length: > 0 } json) return null;
        try
        {
            var o = JsonNode.Parse(json) as JsonObject;
            if (o is null || ApiResult.Int(o["chests"]) is not int chests) return null;
            DateTime? ended = DateTime.TryParse(ApiResult.Text(o["ended_at"]), CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var e) ? e : null;
            return new SquadInfo(chests, ApiResult.Bool(o["complete"]), ApiResult.Int(o["members"]) ?? 0, ApiResult.Int(o["reported"]) ?? 0, ended);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public void Dispose()
    {
        _cts.Cancel();
        ReplayWorker.KillRunning();   // a read in progress is simply done again next time
        _scanThread?.Join(TimeSpan.FromSeconds(3));
        _syncThread?.Join(TimeSpan.FromSeconds(3));
        _watcher?.Dispose();
        _api.Dispose();
        _scanWake.Dispose();
        _syncWake.Dispose();
    }
}
