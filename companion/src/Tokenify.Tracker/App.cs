using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using Tokenify.Core;
using Tokenify.Overlay;
using Activity = Tokenify.Core.Activity;

namespace Tokenify.Tracker;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        // Child process: read one replay and exit (see ReplayWorker).
        if (args.Length >= 2 && args[0] == ReplayWorker.Arg)
        {
            return ReplayWorker.Run(args[1]);
        }

        // Headless linking (support and automated tests): TOKENIFY-Tracker.exe --pair XXXX-XXXX [--history import|skip]
        if (args.Length >= 2 && args[0] == "--pair")
        {
            return PairHeadless(args[1], args.SkipWhile(a => a != "--history").Skip(1).FirstOrDefault());
        }

        // Support: write a diagnostics report (no secrets) to a file. TOKENIFY-Tracker.exe --report <file>
        if (args.Length >= 2 && args[0] == "--report")
        {
            using var store = new Store(AppPaths.Database);
            using var tracker = new Core.Tracker(store);
            File.WriteAllText(args[1], Diagnostics.Report(tracker) + Environment.NewLine + Diagnostics.MatchTable(store, tracker.Settings));
            return 0;
        }

        if (args.Length >= 1 && args[0] == TestSupport.Arg) return TestSupport.Correct(args);   // test copies only

        // Support: read one replay exactly the way the running tracker does (in its separate reader process) and
        // write what came out to a file. No window, nothing stored. TOKENIFY-Tracker.exe --check-replay <replay> <file>
        if (args.Length >= 3 && args[0] == "--check-replay")
        {
            var read = ReplayWorker.Read(args[1], TimeSpan.FromSeconds(120), out var failure);
            File.WriteAllText(args[2], read is null
                ? "failed: " + failure
                : $"ok: {read.Status}, {read.ChestsOpened} chests ({read.Searched} searched, {read.Destroyed} destroyed), {read.Ambiguous} ambiguous, Fortnite {read.Branch}");
            return read is null ? 1 : 0;
        }

        var minimized = args.Contains("--minimized");
        // Has to come before anything that can create a window on this thread. Later is too late: in the optimised
        // build that made the tracker stop at start-up (found by the end-to-end run, which starts the real exe).
        try { Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException); }
        catch (InvalidOperationException) { /* a window already exists: errors are then reported the default way */ }
        ApplicationConfiguration.Initialize();
        try { Application.SetColorMode(SystemColorMode.Dark); } catch { /* older Windows: standard controls */ }
        Theme.InitScale();

        // Quality check with real windows, on a test data folder only: TOKENIFY-Tracker.exe --selftest <folder>
        if (args.Length >= 2 && args[0] == "--selftest")
        {
            using var testInstance = SingleInstance.TryAcquire();
            return testInstance is null ? 3 : SelfTest.Run(args[1], testInstance);
        }

        // Started by an update: the previous version is still shutting down and holds the lock for a moment
        // (longer if it was reading a replay). Wait for it instead of concluding "already running".
        var instance = SingleInstance.TryAcquire(args.Contains("--updated") ? TimeSpan.FromSeconds(25) : null);
        if (instance is null)
        {
            // Either the tracker is already running, or the older TOKENIFY Companion (same lock, same data) still is.
            if (SingleInstance.TrackerRunning() || !LegacyCompanion.Running())
            {
                SingleInstance.SignalExisting();   // bring the running tracker's window forward instead
                return 0;
            }
            if (!LegacyCompanion.AskAndClose()) return 0;
            instance = SingleInstance.TryAcquire(TimeSpan.FromSeconds(8));
            if (instance is null)
            {
                if (SingleInstance.TrackerRunning())
                {
                    SingleInstance.SignalExisting();
                    return 0;
                }
                MessageBox.Show("The older TOKENIFY Companion is still running. Close it from its tray icon (Exit), then start the tracker again.",
                    AppInfo.Name, MessageBoxButtons.OK, MessageBoxIcon.Information);
                return 0;
            }
        }

        using (instance)
        {
            Application.ThreadException += (_, e) => Log.Error("UI error", e.Exception);
            AppDomain.CurrentDomain.UnhandledException += (_, e) => Log.Error("Fatal error", e.ExceptionObject as Exception);
            try
            {
                using var app = new TrackerApp(minimized, instance, args.Contains("--updated"));
                Application.Run(app);
            }
            catch (Exception ex)
            {
                Log.Error("Could not start", ex);
                MessageBox.Show(AppInfo.Name + " could not start:\n\n" + ex.Message, AppInfo.Name, MessageBoxButtons.OK, MessageBoxIcon.Error);
                return 1;
            }
        }
        return 0;
    }

    private static int PairHeadless(string code, string? history)
    {
        using var store = new Store(AppPaths.Database);
        using var tracker = new Core.Tracker(store);
        var (ok, message) = tracker.Pair(new string(code.Where(char.IsAsciiLetterOrDigit).ToArray())).GetAwaiter().GetResult();
        if (ok && history is "import" or "skip") tracker.SetHistoryChoice(history == "import");
        if (ok) tracker.UpdateSettings(s => s.General.SetupDone = true);
        Log.Info("--pair: " + message);
        File.WriteAllText(Path.Combine(AppPaths.DataFolder, "pair-result.txt"), (ok ? "ok: " : "failed: ") + message);
        return ok ? 0 : 1;
    }
}

/// <summary>One copy per Windows user and data folder. The names are the ones TOKENIFY Companion 1.x used, on
/// purpose: the two share a database and must never run at the same time.</summary>
internal sealed class SingleInstance : IDisposable
{
    private readonly Mutex _mutex;
    private readonly EventWaitHandle _marker;
    public EventWaitHandle ShowSignal { get; }

    private SingleInstance(Mutex mutex, EventWaitHandle show, EventWaitHandle marker)
    {
        _mutex = mutex;
        ShowSignal = show;
        _marker = marker;
    }

    private static string Suffix => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(AppPaths.DataFolder.ToLowerInvariant())))[..16];
    /// <summary>Exists only while a Chest Tracker (not the old Companion) holds the lock for this data folder.</summary>
    private static string MarkerName => @"Local\TOKENIFY-Tracker-running-" + Suffix;

    public static SingleInstance? TryAcquire(TimeSpan? wait = null)
    {
        var until = DateTime.UtcNow + (wait ?? TimeSpan.Zero);
        while (true)
        {
            var mutex = new Mutex(true, @"Local\TOKENIFY-Companion-" + Suffix, out var created);
            if (created)
            {
                return new SingleInstance(mutex, new EventWaitHandle(false, EventResetMode.AutoReset, @"Local\TOKENIFY-Companion-show-" + Suffix),
                    new EventWaitHandle(false, EventResetMode.ManualReset, MarkerName));
            }
            mutex.Dispose();
            if (DateTime.UtcNow >= until) return null;
            Thread.Sleep(250);
        }
    }

    /// <summary>True when the copy holding the lock is a Chest Tracker. The old Companion never creates this marker.</summary>
    public static bool TrackerRunning()
    {
        if (!EventWaitHandle.TryOpenExisting(MarkerName, out var marker)) return false;
        marker.Dispose();
        return true;
    }

    public static void SignalExisting()
    {
        try
        {
            using var show = EventWaitHandle.OpenExisting(@"Local\TOKENIFY-Companion-show-" + Suffix);
            show.Set();
        }
        catch (WaitHandleCannotBeOpenedException)
        {
            // the other copy is just starting or exiting
        }
    }

    public void Dispose()
    {
        _marker.Dispose();
        try { _mutex.ReleaseMutex(); } catch (ApplicationException) { }
        _mutex.Dispose();
        ShowSignal.Dispose();
    }
}

/// <summary>The tracker replaces TOKENIFY Companion 1.x and uses its data: the old program has to stop first.</summary>
internal static class LegacyCompanion
{
    private const string ProcessName = "TOKENIFY-Companion";

    /// <summary>The old Companion's processes in this Windows session only (another signed-in user's are not ours).</summary>
    private static List<Process> Mine()
    {
        var session = Process.GetCurrentProcess().SessionId;
        var mine = new List<Process>();
        foreach (var p in Process.GetProcessesByName(ProcessName))
        {
            var same = false;
            try { same = p.SessionId == session; } catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception) { }
            if (same) mine.Add(p); else p.Dispose();
        }
        return mine;
    }

    public static bool Running()
    {
        var found = Mine();
        foreach (var p in found) p.Dispose();
        return found.Count > 0;
    }

    public static bool AskAndClose()
    {
        var answer = MessageBox.Show(
            "The older TOKENIFY Companion is running on this PC.\n\nThe Chest Tracker replaces it and keeps its history and its link to your TOKENIFY account. "
            + "Close the Companion now and continue?",
            AppInfo.Name, MessageBoxButtons.OKCancel, MessageBoxIcon.Question);
        if (answer != DialogResult.OK) return false;
        var closed = 0;
        foreach (var p in Mine())
        {
            try
            {
                p.Kill();
                if (p.WaitForExit(5000)) closed++;
            }
            catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
            {
                Log.Warn("Could not close the older Companion: " + ex.Message);
            }
            finally
            {
                p.Dispose();
            }
        }
        if (closed > 0) Log.Info("Closed TOKENIFY Companion 1.x; the tracker takes over its data");
        return true;
    }
}

/// <summary>
/// The running tracker: the notification-area icon and its menu, the overlay, the shortcuts and the windows. It
/// keeps running when the window is closed; "Exit" in the tray menu stops it.
/// </summary>
internal sealed class TrackerApp : ApplicationContext
{
    private const int HotkeyLock = 1, HotkeyVisible = 2, HotkeyManual = 3;

    private readonly Store _store;
    private readonly SingleInstance _instance;
    private readonly Control _ui = new();
    private readonly NotifyIcon _tray;
    private readonly GlobalHotkeys _hotkeys = new();
    private readonly HashSet<int> _hotkeyProblems = new();
    private readonly RegisteredWaitHandle _showWait;
    private readonly Dictionary<string, Icon> _icons = new();
    private MainWindow? _window;
    private SetupWizard? _setup;
    private TrackerSnapshot _snapshot;
    private string _hotkeySignature = "";
    private bool _toldAboutTray, _exiting;

    public Core.Tracker Tracker { get; }
    public OverlayController Overlay { get; }
    /// <summary>A newer release found on GitHub, or null.</summary>
    public UpdateInfo? Update { get; private set; }
    public bool JustUpdated { get; }

    public TrackerApp(bool minimized, SingleInstance instance, bool justUpdated)
    {
        _instance = instance;
        JustUpdated = justUpdated;
        _ = _ui.Handle;   // a window handle on this thread, to hand work over from the tracker's threads
        _store = new Store(AppPaths.Database);
        Tracker = new Core.Tracker(_store);
        Overlay = new OverlayController(Tracker);
        _snapshot = Tracker.Snapshot();

        _tray = new NotifyIcon { Icon = TrayIcon("idle"), Text = "TOKENIFY", Visible = true, ContextMenuStrip = Theme.Menu() };
        _tray.ContextMenuStrip.Opening += (_, e) =>
        {
            BuildMenu();
            e.Cancel = false;   // it was empty when Windows asked, which WinForms treats as "do not open"
        };
        _tray.DoubleClick += (_, _) => ShowWindow();
        _tray.BalloonTipClicked += (_, _) => ShowWindow();

        Tracker.Changed += s => Post(() => OnSnapshot(s));
        Tracker.Notify += (title, text) => Post(() => Balloon(title, text));
        Tracker.MatchImported += (match, chests) => Post(() => OnImported(match, chests));
        _hotkeys.Pressed += OnHotkey;
        _showWait = ThreadPool.RegisterWaitForSingleObject(instance.ShowSignal, (_, _) => Post(() => ShowWindow()), null, Timeout.Infinite, false);

        OnSnapshot(_snapshot);
        Tracker.Start();

        if (!_snapshot.Settings.General.SetupDone) ShowSetup();
        else if (!minimized) ShowWindow();

        if (justUpdated) Log.Info($"Updated to {AppInfo.Version}");
        _ = Task.Run(async () =>
        {
            await Task.Delay(TimeSpan.FromSeconds(30));
            Updater.CleanUp();   // this version started properly: the previous exe is no longer needed
            if (Tracker.Settings.General.Updates == "offer") await CheckForUpdates();
        });
    }

    private void Post(Action action)
    {
        if (_exiting || !_ui.IsHandleCreated) return;
        try { _ui.BeginInvoke(action); } catch (InvalidOperationException) { /* shutting down */ }
    }

    // ------------------------------------------------------------------------------------- state

    private Icon TrayIcon(string state)
    {
        if (_icons.TryGetValue(state, out var icon)) return icon;
        var size = SystemInformation.SmallIconSize.Width;
        return _icons[state] = Compass.ToIcon(Math.Max(16, size), state switch
        {
            "live" => Theme.Green,
            "attention" => Theme.Gold,
            _ => null,
        });
    }

    private void OnSnapshot(TrackerSnapshot s)
    {
        _snapshot = s;
        Overlay.Apply(s);
        ObsPage.Update(s);
        RegisterHotkeys(s.Settings.Shortcuts);

        _tray.Icon = TrayIcon(s.Activity == Activity.InMatch ? "live" : s.Activity is Activity.NeedsAttention or Activity.FolderMissing ? "attention" : "idle");
        var tip = $"TOKENIFY  ·  {Fmt.Int(s.Stats.TotalChests)} chests  ·  CPG {Fmt.Cpg(s.Stats.Cpg)}\n{Sentence(s.Status)}";
        _tray.Text = tip.Length > 127 ? tip[..127] : tip;

        if (_window is { IsDisposed: false, Visible: true }) _window.Apply(s);
        if (_setup is { IsDisposed: false, Visible: true }) _setup.Apply(s);
    }

    private static string Sentence(string status) => status.Length == 0 ? "" : char.ToUpperInvariant(status[0]) + status[1..].ToLowerInvariant();

    private void OnImported(MatchRow match, int chests)
    {
        var text = $"+{Fmt.Int(chests)} {(chests == 1 ? "CHEST" : "CHESTS")}";
        Overlay.Toast(text);
        var o = _snapshot.Settings.Overlay;
        // With the overlay on screen the "+42 CHESTS" line is enough; a Windows notification would only get in the way.
        if (_snapshot.Settings.General.NotifyOnImport && !(o.Enabled && o.Visible))
        {
            Balloon("TOKENIFY", $"Match counted: {Fmt.Int(chests)} {(chests == 1 ? "chest" : "chests")}.");
        }
    }

    private void Balloon(string title, string text)
    {
        try { _tray.ShowBalloonTip(6000, title, text, ToolTipIcon.None); } catch (Exception ex) { Log.Warn("Notification failed: " + ex.Message); }
    }

    // ------------------------------------------------------------------------------------- shortcuts

    private void RegisterHotkeys(ShortcutSettings k)
    {
        var signature = $"{k.ToggleLock}|{k.ToggleVisible}|{k.ManualEnabled}|{k.ManualIncrement}";
        if (signature == _hotkeySignature) return;
        _hotkeySignature = signature;
        _hotkeyProblems.Clear();
        foreach (var id in new[] { HotkeyLock, HotkeyVisible, HotkeyManual }) _hotkeys.Unregister(id);
        void Set(int id, string? combo, bool enabled)
        {
            if (!enabled) return;
            if (!_hotkeys.Register(id, Hotkey.Parse(combo))) _hotkeyProblems.Add(id);
        }
        Set(HotkeyLock, k.ToggleLock, true);
        Set(HotkeyVisible, k.ToggleVisible, true);
        Set(HotkeyManual, k.ManualIncrement, k.ManualEnabled);
        foreach (var id in _hotkeyProblems) Log.Warn($"Shortcut {id} could not be registered (another program uses it)");
    }

    /// <summary>True when Windows refused a shortcut (already taken by another program).</summary>
    public bool ShortcutProblem(string which) => _hotkeyProblems.Contains(which switch { "lock" => HotkeyLock, "visible" => HotkeyVisible, _ => HotkeyManual });

    private void OnHotkey(int id)
    {
        switch (id)
        {
            case HotkeyLock: ToggleLock(); break;
            case HotkeyVisible: ToggleOverlay(); break;
            case HotkeyManual: Tracker.ManualTap(); break;
        }
    }

    public void ToggleLock() => Tracker.UpdateSettings(s =>
    {
        s.Overlay.Locked = !s.Overlay.Locked;
        if (!s.Overlay.Locked)
        {
            // Unlocking is how the overlay is recovered: make sure it can be seen.
            s.Overlay.Enabled = true;
            s.Overlay.Visible = true;
        }
    });

    public void ToggleOverlay() => Tracker.UpdateSettings(s =>
    {
        var showing = s.Overlay.Enabled && s.Overlay.Visible;
        s.Overlay.Enabled = true;
        s.Overlay.Visible = !showing;
    });

    // ------------------------------------------------------------------------------------- tray menu

    private void BuildMenu()
    {
        var menu = _tray.ContextMenuStrip!;
        var s = _snapshot;
        var o = s.Settings.Overlay;
        menu.Items.Clear();
        ToolStripMenuItem Item(string text, Action onClick, bool check = false, bool enabled = true)
        {
            var item = new ToolStripMenuItem(text) { Checked = check, Enabled = enabled };
            item.Click += (_, _) => onClick();
            return item;
        }

        menu.Items.Add(new ToolStripMenuItem($"TOKENIFY  ·  {Fmt.Int(s.Stats.TotalChests)} chests") { Enabled = false, Font = Theme.Bold() });
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(Item("Show overlay", ToggleOverlay, check: o.Enabled && o.Visible));
        menu.Items.Add(Item(o.Locked ? "Unlock overlay (move and resize)" : "Lock overlay (click-through)", ToggleLock));
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(Item("Open TOKENIFY", () => ShowWindow("overview")));
        menu.Items.Add(Item("Open settings", () => ShowWindow("appearance")));
        menu.Items.Add(Item("View last match", () => ShowWindow("matches", s.LastMatch?.Id), enabled: s.LastMatch is not null));
        menu.Items.Add(Item("Rescan replays", Tracker.Rescan));
        if (s.Settings.Tracking.SessionMode == "manual")
        {
            menu.Items.Add(s.Stats.Session.Active ? Item("End session", Tracker.EndSession) : Item("Start session", Tracker.StartSession));
        }
        menu.Items.Add(Item("Pause tracking", () => Tracker.UpdateSettings(x => x.Tracking.Paused = !x.Tracking.Paused), check: s.Settings.Tracking.Paused));
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(Item("Open TOKENIFY website", () => Ui.OpenUrl(AppInfo.Website)));
        menu.Items.Add(Item("Sync: " + s.SyncText, () => ShowWindow("account")));
        if (Update is not null) menu.Items.Add(Item($"Update available: {Update.Version}", () => ShowWindow("advanced")));
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(Item("Exit", Exit));
    }

    // ------------------------------------------------------------------------------------- windows

    public void ShowWindow(string? page = null, long? matchId = null)
    {
        if (_setup is { IsDisposed: false, Visible: true })
        {
            _setup.Activate();
            return;
        }
        if (_window is null || _window.IsDisposed)
        {
            Theme.UseScale(Theme.ScaleOf());   // built for the display it opens on
            _window = new MainWindow(this, _snapshot);
        }
        if (!_window.Visible) _window.Show();
        if (_window.WindowState == FormWindowState.Minimized) _window.WindowState = FormWindowState.Normal;
        _window.Apply(_snapshot);
        if (page == "matches") _window.ShowMatch(matchId);
        else if (page is not null) _window.Show(page);
        _window.Activate();
    }

    /// <summary>The main window is now on a display with another scale: build it again for that scale, where it is.</summary>
    public void RebuildWindow(MainWindow old, float scale)
    {
        if (old != _window || old.IsDisposed) return;
        var centre = new Point(old.Left + old.Width / 2, old.Top + old.Height / 2);
        var key = old.CurrentKey;
        var visible = old.Visible;
        _window = null;
        old.Dispose();
        Theme.UseScale(scale);
        if (!visible) return;
        _window = new MainWindow(this, _snapshot, centre);
        _window.Show();
        _window.Show(key);
        _window.Activate();
    }

    public void ShowSetup() => OpenSetup(null, null);

    private void OpenSetup(SetupWizard? previous, Point? centre)
    {
        if (previous is null && _setup is { IsDisposed: false })
        {
            _setup.Activate();
            return;
        }
        _window?.Hide();
        if (previous is null) Theme.UseScale(Theme.ScaleOf());
        _setup = new SetupWizard(this, _snapshot, previous, centre);
        previous?.Dispose();
        _setup.FormClosed += (_, _) =>
        {
            Overlay.Previewing = false;
            Overlay.Refresh();
            if (Tracker.Settings.General.SetupDone) ShowWindow("overview");
        };
        _setup.Show();
        _setup.Activate();
    }

    /// <summary>The setup window is now on a display with another scale: build it again, on the step it was on.</summary>
    public void RebuildSetup(SetupWizard old, float scale)
    {
        if (old != _setup || old.IsDisposed) return;
        Theme.UseScale(scale);
        OpenSetup(old, new Point(old.Left + old.Width / 2, old.Top + old.Height / 2));
    }

    /// <summary>The main window was closed: say once that the tracker is still running.</summary>
    public void WindowHidden()
    {
        if (_toldAboutTray) return;
        _toldAboutTray = true;
        Balloon("TOKENIFY is still running", "It keeps counting from the notification area. Right-click the compass icon for the menu, or Exit to stop it.");
    }

    // ------------------------------------------------------------------------------------- updates

    /// <summary>Asks GitHub. When it cannot be reached, an update found earlier stays on offer.</summary>
    public async Task<UpdateCheck> CheckForUpdates()
    {
        UpdateCheck check;
        try
        {
            check = await Updater.Check();
        }
        catch (Exception ex)
        {
            Log.Warn("Update check failed: " + ex.Message);
            check = new UpdateCheck(false, null);
        }
        if (check.Succeeded) Update = check.Update;
        Post(() => OnSnapshot(_snapshot));
        return check;
    }

    /// <summary>Downloads the update, checks its signature and restarts into it. Returns what happened.</summary>
    public async Task<string> InstallUpdate(IProgress<double>? progress = null)
    {
        if (Update is not { } info) return "No update is waiting.";
        const string busy = "A match is being played or read. Update after it has been counted.";
        if (_snapshot.Activity is Activity.Processing or Activity.InMatch) return busy;
        if (!Updater.CanApply(out var reason)) return reason;   // before downloading anything
        var (path, message) = await Updater.Download(info, progress);
        if (path is null) return message;
        // A match may have started, or a replay may be being read, while the download ran.
        if (Tracker.Snapshot().Activity is Activity.Processing or Activity.InMatch) return busy + " The download is kept ready.";
        var (ok, result) = Updater.Apply(path);
        if (ok) Post(Exit);
        return result;
    }

    public void Exit()
    {
        if (_exiting) return;
        _exiting = true;
        _tray.Visible = false;
        ExitThread();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _exiting = true;
            _showWait.Unregister(null);
            _hotkeys.Dispose();
            _tray.Dispose();
            Overlay.Dispose();
            _window?.Dispose();
            _setup?.Dispose();
            Tracker.Dispose();
            _store.Dispose();
            foreach (var icon in _icons.Values) icon.Dispose();
            _ui.Dispose();
        }
        base.Dispose(disposing);
    }
}

/// <summary>Troubleshooting summary. No device secret, no pairing code, no Epic account id (only the start of its hash).</summary>
internal static class Diagnostics
{
    public sealed record Line(string Label, string Value, bool Ok);

    public static List<Line> Checks(TrackerSnapshot s)
    {
        var parserOk = s.UnsupportedFiles == 0;
        return
        [
            new("Replay folder", s.Folder switch
            {
                FolderState.Ok or FolderState.Empty => "Found: " + s.FolderPath,
                FolderState.Unreadable => "Can't be read: " + s.FolderPath,
                _ => "Missing: " + s.FolderPath,
            }, s.Folder is FolderState.Ok or FolderState.Empty),
            new("Fortnite replays", s.ReplayFiles > 0 ? $"{s.ReplayFiles} on this PC" : "None found (is Record Replays on in Fortnite?)", s.ReplayFiles > 0),
            new("Replay compatibility", parserOk ? $"Every replay could be read (counting rules {AppInfo.ParserVersion}, chest registry v{ReplayReader.ChestRegistry.Default.Version})"
                : $"{Ui.Plural(s.UnsupportedFiles, "replay is", "replays are")} from a Fortnite version this tracker can't read yet: update the tracker", parserOk),
            new("Fortnite account", s.ActiveProfile is { } p ? $"Counting for {p.Name ?? "(name not in replay)"}" : "Not known yet (play a match with replays on)", s.ActiveProfile is not null),
            new("TOKENIFY website", s.SyncText, s.Sync is SyncState.UpToDate or SyncState.Syncing or SyncState.Queued or SyncState.NotLinked),
        ];
    }

    public static string Report(Core.Tracker tracker)
    {
        var s = tracker.Snapshot();
        var sb = new StringBuilder();
        sb.AppendLine($"{AppInfo.Name} {AppInfo.Version}");
        sb.AppendLine($"Counting rules {AppInfo.ParserVersion}, chest registry v{ReplayReader.ChestRegistry.Default.Version}");
        sb.AppendLine($"Windows {Environment.OSVersion.Version}, {(Environment.Is64BitOperatingSystem ? "64-bit" : "32-bit")}");
        sb.AppendLine($"Generated {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        sb.AppendLine($"Server: {(AppInfo.CustomServer ? "custom (" + AppInfo.ApiUrl + ")" : "TOKENIFY")}");
        sb.AppendLine();
        foreach (var c in Checks(s)) sb.AppendLine($"{(c.Ok ? "OK " : "!! ")} {c.Label}: {c.Value}");
        sb.AppendLine();
        sb.AppendLine($"Status: {s.Status}; sync: {s.SyncText}; queued {s.Queued}");
        sb.AppendLine($"Totals: {s.Stats.TotalChests} chests in {s.Stats.Games} valid games, CPG {Fmt.Cpg(s.Stats.Cpg)}, {s.Stats.Last10.Label} {Fmt.Cpg(s.Stats.Last10.Cpg)}, best {s.Stats.Best}");
        sb.AppendLine($"Season start: {s.SeasonStartUtc:yyyy-MM-dd HH:mm} UTC; replays from before it: {s.PreSeasonFiles} (never read or sent)");
        foreach (var a in s.Attention) sb.AppendLine("Needs attention: " + a);
        sb.AppendLine();
        var files = tracker.Store.Files();
        sb.AppendLine("Replay files by state: " + string.Join(", ", files.GroupBy(f => f.State).Select(g => $"{g.Key}={g.Count()}")));
        foreach (var f in files.Where(f => f.Error is not null).Take(30)) sb.AppendLine($"  {f.Name}: {f.State}: {f.Error}");
        var matches = tracker.Store.AllMatchesLight();
        sb.AppendLine("Matches by state: " + string.Join(", ", matches.GroupBy(m => m.Upload).Select(g => $"{g.Key}={g.Count()}")));
        sb.AppendLine("Fortnite accounts seen: " + string.Join(", ", tracker.Store.Profiles().Select(p => $"{p.Hash[..8]}… {p.Decision}{(p.Claimed ? " (linked)" : "")}{(p.Error is null ? "" : " " + p.Error)}")));
        return sb.ToString();
    }

    public static string MatchTable(Store store, Settings settings) => string.Join(Environment.NewLine,
        store.Matches(60).Select(m => $"{m.StartedLocal:yyyy-MM-dd HH:mm} {m.Effective(settings.Tracking.CountDestroyed),3} chests ({m.Searched}s+{m.Destroyed}d, {m.Ambiguous} ambiguous) {m.Status,-8} {m.Category,-16} {m.Upload,-13} tries={m.Attempts} {m.PlayerHash[..8]} {m.LastError}")
            .Concat(store.Profiles().Select(p => $"profile {p.Hash[..8]} {p.Name} {p.Decision} claimed={p.Claimed} error={p.Error}")));
}
