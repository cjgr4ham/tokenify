using System.Drawing.Drawing2D;
using Tokenify.Core;
using Tokenify.ReplayReader;

namespace Tokenify.Tracker;

internal sealed class TrackingPage : Page
{
    private readonly Label _folder = Ui.Text("", 620);
    private readonly Label _folderState = Ui.Dim("", 620);
    private readonly TkToggle _paused, _destroyed, _useStart;
    private readonly TkSegment _sessionMode;
    private readonly TkSlider _gap;
    private readonly NumericUpDown _starting = new() { Minimum = 0, Maximum = 10_000_000, Width = Theme.Px(120), ThousandsSeparator = true, Font = Theme.Body(10.5f), BackColor = Theme.Bg1, ForeColor = Theme.Text, BorderStyle = BorderStyle.FixedSingle, Margin = Theme.Pad(0, 2, 8, 6) };
    private readonly TkDateBox _start = new();
    private readonly FlowLayoutPanel _accounts = Ui.Stack();
    private string _accountSignature = "";

    public TrackingPage(TrackerApp app) : base(app, "Tracking", "What is counted, and from where.")
    {
        _paused = new TkToggle("Pause tracking (replays are not read while paused)", false, on => Change(s => s.Tracking.Paused = on));
        _destroyed = new TkToggle("Count chests you destroy, not only the ones you open", true, on => Change(s => s.Tracking.CountDestroyed = on));
        _sessionMode = new TkSegment(["Automatic sessions", "Manual sessions"], 0, i => Change(s => s.Tracking.SessionMode = i == 1 ? "manual" : "auto")) { Width = Theme.Px(420) };
        _gap = new TkSlider(5, 240, 30, 5, v => v + " min", v => Change(s => s.Tracking.SessionGapMinutes = v)) { Width = Theme.Px(420) };
        _useStart = new TkToggle("Only count matches from this date on", false, on => Change(s => s.Tracking.StartDate = on ? _start.Value.Date : null));
        _start.ValueChanged += day => { if (_useStart.Checked) Change(s => s.Tracking.StartDate = day); };
        _starting.ValueChanged += (_, _) => Change(s => s.Tracking.StartingTotal = (int)_starting.Value);

        var registry = ChestRegistry.Default;
        Add(Ui.Card("Fortnite replays", _folder, _folderState,
                Ui.Row(new TkButton("Choose folder…", ButtonKind.Secondary, (_, _) => ChooseFolder()),
                    new TkButton("Use Fortnite's own folder", ButtonKind.Ghost, (_, _) => Change(s => s.Tracking.ReplayFolder = null)),
                    new TkButton("Rescan replays", ButtonKind.Secondary, (_, _) => App.Tracker.Rescan())),
                Ui.Faint("Rescan reads every replay still on this PC again. A match already in your history is recognised and updated in place, never added twice, and your corrections stay.", 620),
                _paused),
            Ui.Card("What counts", _destroyed,
                Ui.Dim("A destroyed chest counts when the game recorded that you broke it and its loot came out (pickaxe, explosives, the Gum-Gum Fruit, or the floor under it).", 620),
                Ui.Gap(4), Ui.Label("Chest types"),
                Ui.Dim("Counted: chests, rare chests, buried chests, Sprite vault chests and Sprite poster chests. Never counted: ammo boxes, food boxes and loot cannons. "
                       + $"A chest type the tracker has not seen before is counted and marked \"unverified\".  (Chest registry v{registry.Version}, counting rules {AppInfo.ParserVersion}.)", 620),
                Ui.Gap(4), Ui.Label("Chests you already had"), Ui.Row(_starting, Ui.Dim("added to your total only, never to CPG", 380)),
                _useStart, _start,
                Ui.Faint("Matches before that date stay in your history but are left out of every number.", 620)),
            Ui.Card("Sessions", _sessionMode, Ui.Label("A new session starts after this long without a match"), _gap,
                Ui.Dim("Automatic sessions are worked out from your matches, so their start and end are estimates. Session time counts time spent in matches, not queues or breaks. "
                       + "With manual sessions you start and end them yourself (Overview, or the tray menu).", 620)),
            Ui.Card("Fortnite accounts on this PC", Ui.Dim("Each Fortnite account keeps its own numbers. They are never added together.", 620), _accounts));
    }

    private void ChooseFolder()
    {
        using var dialog = new FolderBrowserDialog { Description = "Choose the folder where Fortnite saves replays", UseDescriptionForTitle = true, InitialDirectory = App.Tracker.ResolveFolder() };
        if (dialog.ShowDialog(FindForm()) == DialogResult.OK) Change(s => s.Tracking.ReplayFolder = dialog.SelectedPath);
    }

    protected override void Apply(TrackerSnapshot s)
    {
        var t = s.Settings.Tracking;
        _folder.Text = s.FolderPath;
        _folderState.Text = s.Folder switch
        {
            FolderState.Ok => $"{Ui.Plural(s.ReplayFiles, "replay", "replays")} found" + (t.ReplayFolder is null ? " (Fortnite's own folder)." : " (folder chosen by you)."),
            FolderState.Empty => "The folder exists but holds no replays yet. In Fortnite: Settings → Game → Replays → Record Replays: On.",
            FolderState.Unreadable => "This folder can't be read. Choose another one.",
            _ => "This folder doesn't exist. Turn on Record Replays in Fortnite, play a match, or choose the folder.",
        };
        _folderState.ForeColor = s.Folder == FolderState.Ok ? Theme.TextDim : Theme.GoldLight;
        if (_paused.Checked != t.Paused) _paused.Checked = t.Paused;
        if (_destroyed.Checked != t.CountDestroyed) _destroyed.Checked = t.CountDestroyed;
        _sessionMode.SetQuietly(t.SessionMode == "manual" ? 1 : 0);
        _gap.SetQuietly(t.SessionGapMinutes);
        _gap.Enabled = t.SessionMode != "manual";
        // not while it is being typed into: a snapshot arriving mid-number would wipe what was typed
        if (!_starting.ContainsFocus && _starting.Value != t.StartingTotal) _starting.Value = t.StartingTotal;
        if (_useStart.Checked != (t.StartDate is not null)) _useStart.Checked = t.StartDate is not null;
        _start.Enabled = t.StartDate is not null;
        if (t.StartDate is { } d) _start.SetQuietly(d.Date);

        var profiles = App.Tracker.Store.Profiles();
        var signature = string.Join("|", profiles.Select(p => p.Hash + p.Decision + p.Name)) + "|" + t.Profile + "|" + s.ActiveProfile?.Hash;
        if (signature == _accountSignature) return;
        _accountSignature = signature;
        _accounts.Controls.Clear();
        if (profiles.Count == 0)
        {
            _accounts.Controls.Add(Ui.Dim("None seen yet. The first account that plays a match here is taken to be yours.", 620));
            return;
        }
        foreach (var p in profiles)
        {
            var hash = p.Hash;
            var shown = s.ActiveProfile?.Hash == p.Hash;
            var state = p.Decision switch { "mine" => shown ? "yours  ·  its numbers are the ones shown" : "yours", "not-mine" => "not yours: never counted", _ => "waiting for your answer" };
            var row = Ui.Row(Ui.Text($"{p.Name ?? "(name not in replay)"}   ·   {state}", 470, p.Decision == "mine" ? Theme.Text : Theme.TextDim));
            if (p.Decision != "mine") row.Controls.Add(new TkButton("It's mine", ButtonKind.Secondary, (_, _) => App.Tracker.SetProfileDecision(hash, true)));
            if (p.Decision != "not-mine") row.Controls.Add(new TkButton("Not mine", ButtonKind.Ghost, (_, _) => App.Tracker.SetProfileDecision(hash, false)));
            if (p.Decision == "mine" && !shown) row.Controls.Add(new TkButton("Show its numbers", ButtonKind.Secondary, (_, _) => Change(x => x.Tracking.Profile = hash)));
            _accounts.Controls.Add(row);
        }
        if (profiles.Count(p => p.Decision == "mine") > 1)
        {
            _accounts.Controls.Add(Ui.Row(new TkButton(t.Profile == "auto" ? "Following whichever played last" : "Follow whichever played last",
                t.Profile == "auto" ? ButtonKind.Ghost : ButtonKind.Secondary, (_, _) => Change(x => x.Tracking.Profile = "auto"))));
        }
        foreach (Control c in _accounts.Controls) c.BackColor = Theme.Bg2;
    }
}

/// <summary>A box that records the next key combination pressed while it has the focus.</summary>
internal sealed class HotkeyBox : Control
{
    private string _value = "";
    private bool _needsModifier;
    private string? _hint;

    public event Action<string>? Changed;

    public HotkeyBox(bool needsModifier)
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.Selectable | ControlStyles.ResizeRedraw, true);
        _needsModifier = needsModifier;
        TabStop = true;
        Size = Theme.Px(220, 36);
        Margin = Theme.Pad(0, 2, 10, 6);
        Cursor = Cursors.Hand;
        Font = Theme.Bold(10.5f);
    }

    public void Show(string value)
    {
        _value = value;
        Invalidate();
    }

    protected override void OnMouseDown(MouseEventArgs e) { Focus(); base.OnMouseDown(e); }
    protected override void OnGotFocus(EventArgs e) { _hint = null; Invalidate(); base.OnGotFocus(e); }
    protected override void OnLostFocus(EventArgs e) { _hint = null; Invalidate(); base.OnLostFocus(e); }
    protected override bool IsInputKey(Keys keyData) => true;

    protected override void OnKeyDown(KeyEventArgs e)
    {
        e.Handled = e.SuppressKeyPress = true;
        if (e.KeyCode is Keys.ControlKey or Keys.ShiftKey or Keys.Menu or Keys.LWin or Keys.RWin) return;
        if (e.KeyCode == Keys.Escape && !e.Control && !e.Alt && !e.Shift)
        {
            Parent?.SelectNextControl(this, true, true, true, true);
            return;
        }
        var name = Hotkey.NameOf((int)e.KeyCode);
        if (name is null)
        {
            _hint = "That key can't be used";
            Invalidate();
            return;
        }
        var hotkey = new Hotkey(e.Control, e.Alt, e.Shift, false, name);
        if (_needsModifier && !(hotkey.Ctrl || hotkey.Alt))
        {
            _hint = "Hold Ctrl or Alt as well";
            Invalidate();
            return;
        }
        _hint = null;
        _value = hotkey.ToString();
        Invalidate();
        Changed?.Invoke(_value);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        var s = Theme.Scale;
        g.Clear(Theme.Backdrop(this));
        g.SmoothingMode = SmoothingMode.AntiAlias;
        using (var shape = Theme.Rounded(new RectangleF(0.5f, 0.5f, Width - 1f, Height - 1f), 8 * s))
        using (var fill = new SolidBrush(Theme.Bg1))
        using (var edge = new Pen(Focused ? Theme.Gold : Theme.LineStrong, Focused ? 1.6f : 1f))
        {
            g.FillPath(fill, shape);
            g.DrawPath(edge, shape);
        }
        var text = _hint ?? (Focused ? "Press the keys…" : _value);
        TextRenderer.DrawText(g, text, _hint is null && !Focused ? Font : Theme.Body(9.5f), ClientRectangle,
            _hint is not null ? Theme.Coral : Focused ? Theme.TextDim : Enabled ? Theme.GoldLight : Theme.TextFaint,
            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine);
    }
}

internal sealed class ShortcutsPage : Page
{
    private readonly HotkeyBox _lock = new(true), _visible = new(true), _manual = new(false);
    private readonly Label _lockProblem = Ui.Text("", 420, Theme.Coral), _visibleProblem = Ui.Text("", 420, Theme.Coral), _manualNote = Ui.Dim("", 620);
    private readonly TkToggle _manualOn;

    public ShortcutsPage(TrackerApp app) : base(app, "Shortcuts", "These work while Fortnite has the keyboard. Click a box, then press the keys you want.")
    {
        _lock.Changed += v => Change(s => s.Shortcuts.ToggleLock = v);
        _visible.Changed += v => Change(s => s.Shortcuts.ToggleVisible = v);
        _manual.Changed += v => Change(s => s.Shortcuts.ManualIncrement = v);
        _manualOn = new TkToggle("Count by hand with a key while I play", false, on => Change(s => s.Shortcuts.ManualEnabled = on));

        Add(Ui.Card("Overlay",
                Ui.Label("Lock or unlock the overlay"), Ui.Row(_lock, _lockProblem),
                Ui.Label("Show or hide the overlay"), Ui.Row(_visible, _visibleProblem),
                Ui.Faint("If a shortcut ever fails, the same actions are in the tray menu (right-click the compass icon near the clock).", 620)),
            Ui.Card("Manual count (optional)", _manualOn, Ui.Row(_manual), _manualNote,
                Ui.Dim("You don't need this: matches are counted from the replay. If you like to count as you go, each press adds one to your own count for the match being played. "
                       + "It is shown on the overlay as YOUR COUNT and, after the match, next to the replay's count so you can compare. It never adds to your total by itself, so nothing is counted twice.", 620)),
            Ui.Card("Reset", Ui.Row(new TkButton("Reset shortcuts", ButtonKind.Secondary, (_, _) => Change(s => s.Shortcuts = new ShortcutSettings { ManualEnabled = s.Shortcuts.ManualEnabled })))));
    }

    protected override void Apply(TrackerSnapshot s)
    {
        var k = s.Settings.Shortcuts;
        _lock.Show(k.ToggleLock);
        _visible.Show(k.ToggleVisible);
        _manual.Show(k.ManualIncrement);
        _manual.Enabled = k.ManualEnabled;
        if (_manualOn.Checked != k.ManualEnabled) _manualOn.Checked = k.ManualEnabled;
        const string taken = "Another program already uses this. Pick a different one.";
        _lockProblem.Text = App.ShortcutProblem("lock") ? taken : "";
        _visibleProblem.Text = App.ShortcutProblem("visible") ? taken : "";
        var bare = Hotkey.Parse(k.ManualIncrement) is { HasModifier: false };
        _manualNote.Text = !k.ManualEnabled ? ""
            : App.ShortcutProblem("manual") ? taken
            : bare ? $"While this is on, the {k.ManualIncrement} key is taken by the tracker and does not reach Fortnite. Choose a key you don't use in the game, or add Ctrl or Alt."
            : "";
        _manualNote.ForeColor = App.ShortcutProblem("manual") ? Theme.Coral : Theme.GoldLight;
        _manualNote.Visible = _manualNote.Text.Length > 0;
    }
}

internal sealed class AccountPage : Page
{
    private readonly FlowLayoutPanel _linkPanel, _linkedPanel;
    private readonly TkTextBox _code = new(190, placeholder: "CODE") { };
    private readonly Label _message = Ui.Text("", 620, Theme.GoldLight);
    private readonly Label _linked = Ui.Text("", 620, Theme.Text, Theme.Head(13f));
    private readonly Label _state = Ui.Dim("", 620);
    private readonly TkButton _link;

    public AccountPage(TrackerApp app) : base(app, "Account", "Linking is optional. Linked, each finished match goes to your TOKENIFY account and your parties.")
    {
        _code.Box.CharacterCasing = CharacterCasing.Upper;
        _code.Box.MaxLength = 12;
        _code.Box.Font = Theme.Number(15f);
        _link = new TkButton("Link this PC", ButtonKind.Primary, async (_, _) => await Link());
        _code.Box.KeyDown += async (_, e) => { if (e.KeyCode == Keys.Enter) { e.SuppressKeyPress = true; await Link(); } };

        _linkPanel = Ui.Stack(
            Ui.Text("1.  On the TOKENIFY website, sign in and open Automatic PC tracking → Link a PC. It shows a code that lasts 10 minutes.", 620),
            Ui.Row(new TkButton("Open that page", ButtonKind.Secondary, (_, _) => Ui.OpenUrl(AppInfo.Website + "#pc"))),
            Ui.Text("2.  Type the code here.", 620),
            Ui.Row(_code, _link), _message,
            Ui.Faint("Your website password is never entered here. This PC gets its own key, which you can remove on the website at any time.", 620));
        _linkedPanel = Ui.Stack(_linked, _state,
            Ui.Row(new TkButton("Sync now", ButtonKind.Secondary, (_, _) => App.Tracker.SyncNow()),
                new TkButton("Open website", ButtonKind.Ghost, (_, _) => Ui.OpenUrl(AppInfo.Website)),
                new TkButton("Disconnect this PC", ButtonKind.Danger, async (_, _) =>
                {
                    if (!Ui.Confirm(FindForm()!, "Disconnect this PC", "Stop sending matches from this PC to TOKENIFY?\n\nYour history on this PC and everything already on the website stay as they are.")) return;
                    await App.Tracker.Disconnect();
                })));

        Add(Ui.Card("TOKENIFY website", _linkedPanel, _linkPanel),
            Ui.Card("What is sent",
                Ui.Dim("For each finished match: its match id, start and end time, your chest count with the opened/destroyed split and the time of each chest, the Fortnite version, "
                       + "the counting-rules version, and whether the replay was read completely. Corrections you make here are sent too.", 620),
                Ui.Dim("Never sent: the replay file, other players, your Epic account id (only a one-way code made from it, so the website can tell your matches apart), your PC's name.", 620)));
    }

    private async Task Link()
    {
        var code = new string(_code.Value.Where(char.IsAsciiLetterOrDigit).ToArray());
        if (code.Length < 6)
        {
            _message.Text = "Type the code shown on the website.";
            return;
        }
        _link.Enabled = false;
        _message.Text = "Linking…";
        try
        {
            var (ok, message) = await App.Tracker.Pair(code);
            _message.Text = message;
            if (ok) _code.Value = "";
        }
        finally
        {
            _link.Enabled = true;
        }
    }

    protected override void Apply(TrackerSnapshot s)
    {
        var linked = s.Paired && s.Sync != SyncState.Disconnected;
        _linkedPanel.Visible = s.Paired;
        _linkPanel.Visible = !linked;
        if (!s.Paired) return;
        _linked.Text = s.Sync == SyncState.Disconnected ? "This PC was disconnected from TOKENIFY" : $"Linked as {s.Username ?? "(account)"}";
        _linked.ForeColor = s.Sync == SyncState.Disconnected ? Theme.Coral : Theme.Text;
        var parts = new List<string> { s.SyncText };
        if (s.DeviceName is not null) parts.Add($"this PC is called \"{s.DeviceName}\" on the website");
        if (s.LastSyncUtc is { } t) parts.Add($"last match sent {Ui.Ago(t)}");
        if (s.PartyChests > 0)
        {
            parts.Add($"squad total: {Fmt.Int(s.SquadTotal)} (yours {Fmt.Int(s.Stats.TotalChests)} + {Fmt.Int(s.PartyChests)} by party members in {s.PartyGames} squad game{(s.PartyGames == 1 ? "" : "s")})");
        }
        if (s.Squad is { } q)
        {
            parts.Add($"last squad game: {q.Chests}{(q.Complete ? "" : "+")} chests"
                + (q.Complete ? "" : q.Members > q.Reported ? $" ({q.Reported} of {q.Members} players counted so far)" : " (the squad is not confirmed on the website yet)"));
        }
        _state.Text = string.Join("  ·  ", parts);
    }
}

internal sealed class AdvancedPage : Page
{
    private readonly Label _checks = Ui.Text("", 640);
    private readonly TkToggle _obs, _obsSquad, _startup, _notify;
    private readonly TkSegment _obsLayout, _updates;
    private readonly Label _obsPath = Ui.Dim("", 640);
    private readonly Label _updateState = Ui.Text("", 640);
    private readonly TkButton _install, _check;
    private bool _busy;

    public AdvancedPage(TrackerApp app) : base(app, "Advanced")
    {
        _obs = new TkToggle("Write an overlay page for OBS", false, on => Change(s => s.General.ObsPage = on));
        _obsLayout = new TkSegment(["Minimal", "Standard", "Expanded"], 1, i => Change(s => s.General.ObsLayout = i switch { 0 => "minimal", 2 => "expanded", _ => "standard" })) { Width = Theme.Px(360) };
        _obsSquad = new TkToggle("Include the squad total of the last game", false, on => Change(s => s.General.ObsSquad = on));
        _startup = new TkToggle("Start with Windows, quietly in the notification area", false, on =>
        {
            if (Filling) return;
            Startup.Set(on);
            Change(s => s.General.Startup = on ? "windows" : "none");
        });
        _notify = new TkToggle("Tell me when a match has been counted (when the overlay is off)", true, on => Change(s => s.General.NotifyOnImport = on));
        _updates = new TkSegment(["Tell me about updates", "Never check"], 0, i => Change(s => s.General.Updates = i == 1 ? "off" : "offer")) { Width = Theme.Px(360) };
        _check = new TkButton("Check now", ButtonKind.Secondary, async (_, _) => await CheckNow());
        _install = new TkButton("Download and install", ButtonKind.Primary, async (_, _) => await Install());

        Add(Ui.Card("Health check", _checks,
                Ui.Row(new TkButton("Open the log", ButtonKind.Ghost, (_, _) => OpenFile(AppPaths.LogFile)),
                    new TkButton("Open the data folder", ButtonKind.Ghost, (_, _) => Ui.OpenUrl(AppPaths.DataFolder)),
                    new TkButton("Run setup again", ButtonKind.Ghost, (_, _) => App.ShowSetup()))),
            Ui.Card("Export", Ui.Dim("Your whole match history, with every correction. CSV opens in Excel or Google Sheets; JSON also holds every chest event.", 640),
                Ui.Row(new TkButton("Export CSV…", ButtonKind.Secondary, (_, _) => Export("csv")), new TkButton("Export JSON…", ButtonKind.Secondary, (_, _) => Export("json")))),
            Ui.Card("OBS overlay", _obs, Ui.Label("Layout"), _obsLayout, _obsSquad, _obsPath,
                Ui.Row(new TkButton("Copy the file's path", ButtonKind.Secondary, (_, _) => { try { Clipboard.SetText(AppPaths.ObsPage); } catch (Exception) { } }),
                    new TkButton("Show the file", ButtonKind.Ghost, (_, _) => Ui.OpenUrl(AppPaths.DataFolder))),
                Ui.Dim("In OBS: Sources → + → Browser → tick \"Local file\" and choose that file. Width 320, height 260. The background is transparent and the numbers update by themselves after each match. "
                       + "Nothing is put online and nothing listens on your network; it is a file on this PC.", 640)),
            Ui.Card("Start-up and notifications", _startup, _notify,
                Ui.Faint("Start with Windows adds an ordinary entry for your Windows account; nothing is installed as a service and nothing of Fortnite's is touched.", 640)),
            Ui.Card("Updates", _updates, _updateState, Ui.Row(_check, _install),
                Ui.Faint("An update is installed only when you click, and only if its signature matches TOKENIFY's release key. Otherwise the download is deleted. Your history and settings are kept.", 640)),
            Ui.Card("Start over",
                Ui.Row(new TkButton("Reset settings…", ButtonKind.Danger, (_, _) =>
                    {
                        if (Ui.Confirm(FindForm()!, "Reset settings", "Put every setting back to its default?\n\nYour match history, your corrections and the link to TOKENIFY are kept."))
                        {
                            App.Tracker.ResetSettings();
                        }
                    }),
                    new TkButton("Delete my history…", ButtonKind.Danger, (_, _) =>
                    {
                        if (Ui.Confirm(FindForm()!, "Delete history", "Delete every match, correction and session stored on this PC?\n\nThis cannot be undone. Settings and the link to TOKENIFY are kept, "
                                                                      + "and matches already on the website stay there. Replays still on this PC are not counted again unless you rescan."))
                        {
                            App.Tracker.DeleteHistory();
                        }
                    })),
                Ui.Faint("These are separate on purpose: resetting settings never touches your history, and deleting history never touches your settings. Removing the program itself (deleting the .exe) leaves both in place.", 640)));
    }

    private static void OpenFile(string path)
    {
        if (File.Exists(path)) Ui.OpenUrl(path);
    }

    private void Export(string kind)
    {
        if (Snapshot is not { } s) return;
        Directory.CreateDirectory(AppPaths.Exports);
        using var save = new SaveFileDialog
        {
            Title = "Export match history",
            Filter = kind == "csv" ? "CSV file (*.csv)|*.csv" : "JSON file (*.json)|*.json",
            FileName = Path.GetFileName(Exporter.DefaultPath("matches", kind)),
            InitialDirectory = AppPaths.Exports,
        };
        if (save.ShowDialog(FindForm()) != DialogResult.OK) return;
        var store = App.Tracker.Store;
        var mine = s.ActiveProfile?.Hash;
        Exporter.Write(save.FileName, kind == "csv"
            ? Exporter.Csv(store.AllMatchesLight().Where(m => mine is null || m.PlayerHash == mine), s.Settings)
            : Exporter.JsonExport(s, store.AllMatches().Where(m => mine is null || m.PlayerHash == mine), store.Corrections));
    }

    private async Task CheckNow()
    {
        if (_busy) return;
        _busy = true;
        _updateState.Text = "Checking…";
        var result = "Could not check for updates. Try again later.";
        try
        {
            var check = await App.CheckForUpdates();
            result = !check.Succeeded
                ? "Could not check for updates: GitHub did not answer (offline, or too many requests from your network). "
                  + (App.Update is { } kept ? $"Version {kept.Version} was found earlier and is still on offer." : "Try again later.")
                : check.Update is { } found ? $"Version {found.Version} is available (you have {AppInfo.Version})."
                : $"You have the latest version ({AppInfo.Version}).";
        }
        finally
        {
            _busy = false;
            if (!IsDisposed) _updateState.Text = result;
        }
    }

    private async Task Install()
    {
        if (_busy || App.Update is null) return;
        _busy = true;
        _install.Enabled = _check.Enabled = false;
        try
        {
            var progress = new Progress<double>(p => _updateState.Text = $"Downloading… {p:P0}");
            _updateState.Text = await App.InstallUpdate(progress);
        }
        finally
        {
            _busy = false;
            _install.Enabled = _check.Enabled = true;
        }
    }

    protected override void Apply(TrackerSnapshot s)
    {
        var g = s.Settings.General;
        _checks.Text = string.Join(Environment.NewLine, Diagnostics.Checks(s).Select(c => $"{(c.Ok ? "✓" : "!")}   {c.Label}:  {c.Value}"))
            + $"{Environment.NewLine}{Environment.NewLine}{AppInfo.Name} {AppInfo.Version}  ·  counting rules {AppInfo.ParserVersion}  ·  chest registry v{ChestRegistry.Default.Version}";
        if (_obs.Checked != g.ObsPage) _obs.Checked = g.ObsPage;
        _obsLayout.SetQuietly(g.ObsLayout switch { "minimal" => 0, "expanded" => 2, _ => 1 });
        if (_obsSquad.Checked != g.ObsSquad) _obsSquad.Checked = g.ObsSquad;
        _obsLayout.Enabled = _obsSquad.Enabled = g.ObsPage;
        _obsPath.Text = g.ObsPage ? AppPaths.ObsPage : "Turn it on to create the file.";
        _startup.Enabled = Startup.Available;
        var startsWithWindows = Startup.IsEnabled();
        if (_startup.Checked != startsWithWindows) _startup.Checked = startsWithWindows;
        if (_notify.Checked != g.NotifyOnImport) _notify.Checked = g.NotifyOnImport;
        _updates.SetQuietly(g.Updates == "off" ? 1 : 0);
        _install.Visible = App.Update is not null;
        if (!_busy)
        {
            _updateState.Text = App.Update is { } u ? $"Version {u.Version} is available (you have {AppInfo.Version})."
                : g.Updates == "off" ? $"Version {AppInfo.Version}. Update checks are off: the tracker never contacts GitHub."
                : $"Version {AppInfo.Version}." + (App.JustUpdated ? " Just updated." : "");
        }
    }
}
