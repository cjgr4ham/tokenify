using System.Diagnostics;

namespace Tokenify.Companion;

/// <summary>
/// The app lives in the notification area. It starts quietly (never steals focus from the game), shows the setup
/// window only when it isn't linked yet and was opened by the player, and shows balloons only for things that need
/// the player (per-match "chests synced" balloons are off unless turned on).
/// </summary>
internal sealed class CompanionApp : ApplicationContext
{
    private readonly Store _store;
    private readonly Engine _engine;
    private readonly NotifyIcon _tray;
    private readonly ToolStripMenuItem _status, _lastMatch, _lastSync, _reconnect;
    private readonly SynchronizationContext _ui;
    private readonly SingleInstance _instance;
    private readonly Thread _showListener;
    private CompanionWindow? _main;
    private SetupForm? _setup;
    private EngineSnapshot? _snap;
    private bool _exiting;

    public CompanionApp(bool minimized, SingleInstance instance)
    {
        _instance = instance;
        _ui = SynchronizationContext.Current ?? new WindowsFormsSynchronizationContext();
        _store = new Store(AppPaths.Database);
        _engine = new Engine(_store);

        var menu = new ContextMenuStrip { ShowImageMargin = false };
        var title = new ToolStripMenuItem("TOKENIFY Companion") { Enabled = false, Font = new Font(SystemFonts.MenuFont ?? Control.DefaultFont, FontStyle.Bold) };
        _status = new ToolStripMenuItem("Starting…") { Enabled = false };
        _lastMatch = new ToolStripMenuItem("Last match: none yet") { Enabled = false };
        _lastSync = new ToolStripMenuItem("Last sync: never") { Enabled = false };
        _reconnect = new ToolStripMenuItem("Reconnect account…", null, (_, _) => ShowSetup(force: true));
        menu.Items.AddRange(new ToolStripItem[]
        {
            title, _status, _lastMatch, _lastSync, new ToolStripSeparator(),
            new ToolStripMenuItem("Open TOKENIFY", null, (_, _) => OpenWebsite()),
            new ToolStripMenuItem("Scan replays now", null, (_, _) => { _engine.ScanNow(); _engine.SyncNow(); }),
            new ToolStripMenuItem("Settings", null, (_, _) => ShowMain(CompanionWindow.Tab.Settings)),
            new ToolStripMenuItem("View recent matches", null, (_, _) => ShowMain(CompanionWindow.Tab.Matches)),
            _reconnect,
            new ToolStripSeparator(),
            new ToolStripMenuItem("Exit", null, (_, _) => ExitApp()),
        });

        _tray = new NotifyIcon
        {
            Icon = Embedded.AppIcon(16),
            Text = "TOKENIFY Companion",
            ContextMenuStrip = menu,
            Visible = true,
        };
        _tray.DoubleClick += (_, _) => ShowMain(CompanionWindow.Tab.Status);
        _tray.BalloonTipClicked += (_, _) => ShowMain(CompanionWindow.Tab.Status);

        _engine.Changed += snap => _ui.Post(_ => Apply(snap), null);
        _engine.Notify += (t, text) => _ui.Post(_ => Balloon(t, text), null);
        _engine.Start();

        // a second launch of the exe brings this window forward
        _showListener = new Thread(() =>
        {
            while (!_exiting)
            {
                try
                {
                    if (_instance.ShowSignal.WaitOne(TimeSpan.FromSeconds(1))) _ui.Post(_ => ShowMain(CompanionWindow.Tab.Status), null);
                }
                catch (ObjectDisposedException)
                {
                    return;
                }
            }
        }) { IsBackground = true, Name = "Single instance" };
        _showListener.Start();

        Apply(_engine.Snapshot());
        if (!_engine.Paired)
        {
            if (minimized) Balloon("TOKENIFY Companion", "Finish setting up: click here to connect your TOKENIFY account.");
            else ShowSetup(force: false);
        }
        else if (!minimized)
        {
            ShowMain(CompanionWindow.Tab.Status);
        }
    }

    public Engine Engine => _engine;

    private void Apply(EngineSnapshot s)
    {
        _snap = s;
        _status.Text = "Status: " + s.Status;
        _lastMatch.Text = s.LastMatch is { } m ? $"Last match: {m.ShownChests} chests ({m.StartedLocal:MMM d, h:mm tt})" : "Last match: none yet";
        _lastSync.Text = s.LastSyncUtc is { } t ? $"Last sync: {(s.Online ? "successful" : "offline")}, {t.ToLocalTime():MMM d, h:mm tt}" : (s.Paired ? "Last sync: not yet" : "Last sync: not connected");
        _reconnect.Text = s.Paired ? "Reconnect account…" : "Connect account…";
        var text = "TOKENIFY Companion: " + s.Status;
        _tray.Text = text.Length > 127 ? text[..127] : text;
        _main?.Apply(s);
        _setup?.Apply(s);
    }

    private void Balloon(string title, string text)
    {
        try
        {
            _tray.BalloonTipTitle = title;
            _tray.BalloonTipText = text;
            _tray.BalloonTipIcon = ToolTipIcon.Info;
            _tray.ShowBalloonTip(8000);
        }
        catch
        {
            // notifications disabled: fine
        }
    }

    public void ShowMain(CompanionWindow.Tab tab)
    {
        if (!_engine.Paired && _main is null)
        {
            ShowSetup(force: false);
            return;
        }

        if (_main is null || _main.IsDisposed)
        {
            _main = new CompanionWindow(this, _engine);
            _main.FormClosed += (_, _) => _main = null;
        }
        _main.Show();
        _main.Select(tab);
        if (_snap is not null) _main.Apply(_snap);
        if (_main.WindowState == FormWindowState.Minimized) _main.WindowState = FormWindowState.Normal;
        _main.Activate();
    }

    public void ShowSetup(bool force)
    {
        if (_setup is null || _setup.IsDisposed)
        {
            _setup = new SetupForm(this, _engine, reconnect: force && _engine.Paired);
            _setup.FormClosed += (_, _) =>
            {
                _setup = null;
                if (_engine.Paired && !_exiting)
                {
                    Balloon("TOKENIFY Companion", "Running in the notification area. Play Fortnite normally: chests are added after each match.");
                }
            };
        }
        _setup.Show();
        if (_snap is not null) _setup.Apply(_snap);
        _setup.Activate();
    }

    public void OpenWebsite(string hash = "")
    {
        try
        {
            Process.Start(new ProcessStartInfo(AppInfo.Website + hash) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Log.Error("Could not open the website", ex);
        }
    }

    public void ExitApp()
    {
        _exiting = true;
        _tray.Visible = false;
        _main?.Close();
        _setup?.Close();
        ExitThread();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _exiting = true;
            _tray.Dispose();
            _engine.Dispose();
            _store.Dispose();
        }
        base.Dispose(disposing);
    }
}
