using Tokenify.Core;

namespace Tokenify.Tracker;

/// <summary>One section of the main window. Built once; <see cref="Apply"/> fills it from the tracker's snapshot.</summary>
internal abstract class Page : Panel
{
    protected TrackerApp App { get; }
    protected FlowLayoutPanel Body { get; }
    protected TrackerSnapshot? Snapshot { get; private set; }
    /// <summary>True while <see cref="Apply"/> is filling controls, so their change handlers do nothing.</summary>
    protected bool Filling { get; private set; }

    protected Page(TrackerApp app, string title, string? intro = null)
    {
        App = app;
        Dock = DockStyle.Fill;
        AutoScroll = true;
        BackColor = Theme.Bg0;
        Padding = Theme.Pad(30, 24, 30, 24);
        Body = Ui.Stack();
        Body.Location = new Point(Theme.Px(30), Theme.Px(24));
        Controls.Add(Body);
        Body.Controls.Add(Ui.Heading(title));
        if (intro is not null) Body.Controls.Add(Ui.Dim(intro, 700));
        Body.Controls.Add(Ui.Gap(10));
    }

    protected void Add(params Control[] controls) => Body.Controls.AddRange(controls);

    public void Fill(TrackerSnapshot snapshot)
    {
        Snapshot = snapshot;
        Filling = true;
        try { Apply(snapshot); }
        finally { Filling = false; }
    }

    protected abstract void Apply(TrackerSnapshot s);

    /// <summary>Changes a setting from a control's handler (ignored while the page is being filled).</summary>
    protected void Change(Action<Settings> change)
    {
        if (!Filling) App.Tracker.UpdateSettings(change);
    }

    /// <summary>Called when the page becomes the one on screen.</summary>
    public virtual void Shown() { }
}

/// <summary>
/// The tracker's window: an overview, the match history with its audit view, and the settings, grouped as
/// Appearance, Layout, Position, Tracking, Shortcuts, Account and Advanced. Closing it leaves the tracker running
/// in the notification area.
/// </summary>
internal sealed class MainWindow : TkForm
{
    private readonly TrackerApp _app;
    private readonly TkNav _nav;
    private readonly Panel _host = new() { Dock = DockStyle.Fill, BackColor = Theme.Bg0 };
    private readonly Dictionary<string, Page> _pages = new();
    private TrackerSnapshot _snapshot;

    /// <param name="centre">Where to put the window's middle (when it is rebuilt in place); otherwise the display with the mouse pointer.</param>
    public MainWindow(TrackerApp app, TrackerSnapshot snapshot, Point? centre = null)
    {
        _app = app;
        _snapshot = snapshot;
        Text = AppInfo.Name;
        // Wide enough for the navigation, one column of cards and a scroll bar.
        Place(1120, 760, centre, minimum: new Size(1060, 600));
        KeyPreview = true;

        _nav = new TkNav(
        [
            new("overview", "Overview"), new("matches", "Matches"),
            new("", "Overlay", Header: true), new("appearance", "Appearance"), new("layout", "Layout"), new("position", "Position"),
            new("", "Tracker", Header: true), new("tracking", "Tracking"), new("shortcuts", "Shortcuts"), new("account", "Account"), new("advanced", "Advanced"),
        ], "overview");
        _nav.Selected += Show;

        _pages["overview"] = new OverviewPage(app);
        _pages["matches"] = new MatchesPage(app);
        _pages["appearance"] = new AppearancePage(app);
        _pages["layout"] = new LayoutPage(app);
        _pages["position"] = new PositionPage(app);
        _pages["tracking"] = new TrackingPage(app);
        _pages["shortcuts"] = new ShortcutsPage(app);
        _pages["account"] = new AccountPage(app);
        _pages["advanced"] = new AdvancedPage(app);
        foreach (var page in _pages.Values)
        {
            page.Visible = false;
            _host.Controls.Add(page);
        }

        Controls.Add(_host);
        Controls.Add(_nav);
        Show("overview");
        Apply(snapshot);
    }

    /// <summary>The section on screen.</summary>
    public string CurrentKey => _nav.SelectedKey;
    internal Page CurrentPage => _pages[_nav.SelectedKey];

    protected override void RebuildForScale(float scale) => _app.RebuildWindow(this, scale);

    public void Apply(TrackerSnapshot snapshot)
    {
        _snapshot = snapshot;
        _nav.SetBadge("overview", snapshot.Activity is Activity.NeedsAttention or Activity.FolderMissing || snapshot.AskProfile is not null || snapshot.HistoryHeld > 0);
        _nav.SetBadge("matches", snapshot.NeedsReview > 0);
        _nav.SetBadge("account", snapshot.Sync == SyncState.Disconnected);
        _nav.SetBadge("advanced", _app.Update is not null);
        foreach (var page in _pages.Values)
        {
            if (page.Visible) page.Fill(snapshot);
        }
    }

    public void Show(string key)
    {
        if (!_pages.TryGetValue(key, out var target)) return;
        _nav.SelectedKey = key;
        foreach (var page in _pages.Values) page.Visible = page == target;
        target.Fill(_snapshot);
        target.Shown();
        // While the overlay's own settings are open it is shown, so changes can be seen as they are made.
        _app.Overlay.Previewing = key is "appearance" or "layout" or "position";
        _app.Overlay.Refresh();
    }

    public void ShowMatch(long? matchId)
    {
        Show("matches");
        if (_pages["matches"] is MatchesPage matches) matches.Select(matchId);
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        // Closing the window is not exiting: the tracker keeps counting from the notification area.
        if (e.CloseReason == CloseReason.UserClosing)
        {
            e.Cancel = true;
            Hide();
            _app.Overlay.Previewing = false;
            _app.Overlay.Refresh();
            _app.WindowHidden();
            return;
        }
        base.OnFormClosing(e);
    }

    protected override void OnVisibleChanged(EventArgs e)
    {
        base.OnVisibleChanged(e);
        if (Visible) Show(_nav.SelectedKey);
    }
}
