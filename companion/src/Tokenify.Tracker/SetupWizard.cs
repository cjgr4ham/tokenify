using System.Drawing.Drawing2D;
using System.Drawing.Text;
using Tokenify.Core;

namespace Tokenify.Tracker;

/// <summary>
/// First-run setup, six short steps: what the tracker does, Fortnite's replays, the overlay, starting with
/// Windows, the optional TOKENIFY link, done. Nothing is typed except (optionally) a pairing code.
/// </summary>
internal sealed class SetupWizard : TkForm
{
    private readonly TrackerApp _app;
    private readonly List<FlowLayoutPanel> _steps = new();
    private readonly string[] _titles = ["Welcome", "Fortnite replays", "Overlay", "Start-up", "TOKENIFY account", "All set"];
    private readonly TkButton _back, _next;
    private readonly Panel _header = new() { Dock = DockStyle.Top, Height = Theme.Px(96), BackColor = Theme.Bg1 };
    private readonly Panel _footer = new() { Dock = DockStyle.Bottom, Height = Theme.Px(64), BackColor = Theme.Bg1 };
    private readonly Panel _content = new() { Dock = DockStyle.Fill, BackColor = Theme.Bg0, AutoScroll = true, Padding = Theme.Pad(34, 22, 34, 12) };
    private int _step;
    private TrackerSnapshot _snapshot;

    // step 2
    private readonly Label _replayState = Ui.Text("", 560, Theme.Text, Theme.Head(13f));
    private readonly Label _replayHelp = Ui.Dim("", 560);
    private readonly Label _account = Ui.Text("", 560);
    private readonly FlowLayoutPanel _historyPanel;
    private readonly Label _historyText = Ui.Text("", 560);
    private readonly TkSegment _historyChoice = new(["Count them too", "Start from now"], 0) { Width = Theme.Px(360) };
    // step 3
    private readonly PreviewBox _preview = new() { Size = Theme.Px(590, 236) };
    private readonly TkToggle _overlayOn;
    private readonly TkSegment _preset, _style;
    // step 4
    private readonly TkToggle _startup;
    // step 5
    private readonly TkTextBox _code = new(190, placeholder: "CODE");
    private readonly Label _linkMessage = Ui.Text("", 560, Theme.GoldLight);
    private readonly Label _linkState = Ui.Text("", 560, Theme.Text, Theme.Head(13f));
    private readonly FlowLayoutPanel _linkForm;
    private readonly TkButton _link;
    // step 6
    private readonly FlowLayoutPanel _summary = Ui.Stack();
    private string _summaryText = "";

    /// <param name="previous">The same setup on a display with another scale: carry on where it was.</param>
    /// <param name="centre">Where to put the window's middle; otherwise the display with the mouse pointer.</param>
    public SetupWizard(TrackerApp app, TrackerSnapshot snapshot, SetupWizard? previous = null, Point? centre = null)
    {
        _app = app;
        _snapshot = snapshot;
        Text = AppInfo.Name + " setup";
        FormBorderStyle = FormBorderStyle.FixedSingle;
        MaximizeBox = false;
        Place(680, 760, centre);

        _header.Paint += PaintHeader;
        _back = new TkButton("Back", ButtonKind.Ghost, (_, _) => Go(_step - 1));
        _next = new TkButton("Next", ButtonKind.Primary, (_, _) => Next());
        var buttons = new FlowLayoutPanel { FlowDirection = FlowDirection.RightToLeft, Dock = DockStyle.Fill, Padding = Theme.Pad(22, 14, 26, 0), BackColor = Theme.Bg1 };
        buttons.Controls.Add(_next);
        buttons.Controls.Add(_back);
        _footer.Controls.Add(buttons);

        // Upgrading from TOKENIFY Companion, or running setup again: say what is already here, and only that.
        var hasHistory = app.Tracker.Store.AllMatchesLight().Count > 0;
        var kept = hasHistory && snapshot.Paired ? "Your match history and your TOKENIFY link are already on this PC. Both are kept."
            : hasHistory ? "Your match history is already on this PC. It is kept."
            : snapshot.Paired ? "This PC is already linked to your TOKENIFY account. That is kept."
            : null;

        // 1. welcome
        Step(Ui.Text("Counts your Fortnite chests for you.", 560, Theme.Text, Theme.Head(16f)),
            Ui.Gap(4),
            Bullet("After every match it reads the replay Fortnite saved and counts the chests you opened and the ones you destroyed."),
            Bullet("A small overlay shows your total and your chests per game (CPG) while you play. It updates when a match ends."),
            Bullet("You never press anything. It sits in the notification area next to the clock."),
            Ui.Gap(8),
            Ui.Dim("It only reads the replay files Fortnite writes to your own PC. It does not touch the game while it runs, read its memory, or change any of its files, "
                   + "and it cannot show anything about a match before that match is over.", 560),
            kept is not null ? Ui.Text(kept, 560, Theme.GoldLight) : Ui.Gap(1));

        // 2. replays
        _historyPanel = Ui.Stack(Ui.Gap(8), _historyText, _historyChoice,
            Ui.Faint("Only replays from the current season can be counted. Anything older is never read.", 560));
        Step(_replayState, _replayHelp, Ui.Gap(6), _account, _historyPanel,
            Ui.Row(new TkButton("Check again", ButtonKind.Secondary, (_, _) => { _app.Tracker.ScanNow(); Fill(); }),
                new TkButton("Choose the folder myself…", ButtonKind.Ghost, (_, _) => ChooseFolder())));

        // 3. overlay
        _overlayOn = new TkToggle("Show the overlay on top of Fortnite", true, on => Change(s => { s.Overlay.Enabled = on; s.Overlay.Visible = true; }));
        _preset = new TkSegment(["Minimal", "Standard", "Expanded"], 1, i => Change(s => s.Overlay.Preset = i switch { 0 => "minimal", 2 => "expanded", _ => "standard" })) { Width = Theme.Px(560) };
        _style = new TkSegment(["Frosted Dark", "Solid Dark", "HUD Only", "Gold Accent"], 0, i => Change(s => s.Overlay.Style = new[] { "frosted", "solid", "hud", "gold" }[i])) { Width = Theme.Px(560) };
        Step(_overlayOn, _preview, Ui.Label("How much it shows"), _preset, Ui.Label("Style"), _style,
            Ui.Dim("It is locked by default, so your clicks go straight through to the game. Ctrl+Shift+L unlocks it to drag or resize it; Ctrl+Shift+O hides it. "
                   + "Everything can be changed later in Settings.", 560),
            Ui.Faint("Fortnite must be in Windowed Fullscreen for anything to appear on top of it (Settings → Video → Window Mode).", 560));

        // 4. start-up
        _startup = new TkToggle("Start with Windows", Startup.Available);
        Step(Ui.Text("So you never have to remember to open it.", 560, Theme.Text, Theme.Head(14f)), Ui.Gap(4), _startup,
            Ui.Dim(Startup.Available
                ? "It starts quietly in the notification area and uses almost nothing until a match ends. You can turn this off under Advanced."
                : "Not available for this test copy.", 560),
            Ui.Gap(10),
            Ui.Text("Closing the window does not stop the tracker.", 560, Theme.Text, Theme.Bold()),
            Ui.Dim("It keeps running from the compass icon next to the clock. Right-click that icon for the menu; choose Exit there to stop it completely. "
                   + "A match played while the tracker is not running is still counted the next time it starts, as long as Fortnite has not deleted that replay.", 560));

        // 5. account
        _code.Box.CharacterCasing = CharacterCasing.Upper;
        _code.Box.MaxLength = 12;
        _code.Box.Font = Theme.Number(15f);
        _link = new TkButton("Link this PC", ButtonKind.Secondary, async (_, _) => await Link());
        _linkForm = Ui.Stack(
            Ui.Text("1.  On the TOKENIFY website, sign in and open Automatic PC tracking → Link a PC.", 560),
            Ui.Row(new TkButton("Open that page", ButtonKind.Secondary, (_, _) => Ui.OpenUrl(AppInfo.Website + "#pc"))),
            Ui.Text("2.  Type the code it shows.", 560),
            Ui.Row(_code, _link), _linkMessage);
        Step(Ui.Text("Optional: send your matches to your TOKENIFY account.", 560, Theme.Text, Theme.Head(14f)),
            Ui.Dim("The tracker works fully without it. Linked, your count on the website and in your parties updates by itself after each match.", 560),
            Ui.Gap(6), _linkState, _linkForm,
            Ui.Faint("You can skip this and link later under Account.", 560));

        // 6. done
        Step(Ui.Text("That's it. Go play.", 560, Theme.Text, Theme.Head(16f)), Ui.Gap(4), _summary);

        Controls.Add(_content);
        Controls.Add(_footer);
        Controls.Add(_header);
        AcceptButton = _next;
        _app.Overlay.Previewing = false;
        if (previous is not null)
        {
            _historyChoice.SetQuietly(previous._historyChoice.SelectedIndex);
            _startup.Checked = previous._startup.Checked;
            _code.Value = previous._code.Value;
            Go(previous._step);
            return;
        }
        Go(0);
        // Who recorded the newest replay? Read in the background so the window opens at once.
        _ = Task.Run(() =>
        {
            try { _app.Tracker.ScanNow(); } catch (Exception ex) { Log.Warn("Setup scan: " + ex.Message); }
        });
    }

    protected override void RebuildForScale(float scale) => _app.RebuildSetup(this, scale);

    private static Control Bullet(string text)
    {
        var row = Ui.Row(Ui.Text("◆", 20, Theme.Gold), Ui.Text(text, 520));
        row.WrapContents = false;
        return row;
    }

    private void Step(params Control[] controls)
    {
        var stack = Ui.Stack(controls);
        stack.Location = new Point(Theme.Px(34), Theme.Px(22));
        stack.Visible = false;
        _steps.Add(stack);
        _content.Controls.Add(stack);
    }

    /// <summary>Self-test: jump to a step.</summary>
    internal void GoTo(int step) => Go(step);

    internal int StepCount => _steps.Count;

    private void PaintHeader(object? sender, PaintEventArgs e)
    {
        var g = e.Graphics;
        var s = Theme.Scale;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;
        Overlay.Compass.Draw(g, 50 * s, 48 * s, 52 * s);
        using var gold = new SolidBrush(Theme.Gold);
        using var text = new SolidBrush(Theme.Text);
        using var faint = new SolidBrush(Theme.TextFaint);
        g.DrawString(_titles[_step], Theme.Head(19f), text, 88 * s, 20 * s);
        g.DrawString($"STEP {_step + 1} OF {_titles.Length}", Theme.Head(9f), gold, 91 * s, 56 * s);
        // progress
        var x = 190 * s;
        for (var i = 0; i < _titles.Length; i++)
        {
            using var dot = new SolidBrush(i <= _step ? Theme.Gold : Theme.Bg4);
            using var shape = Theme.Rounded(new RectangleF(x + i * 34 * s, 62 * s, 26 * s, 5 * s), 2.5f * s);
            g.FillPath(dot, shape);
        }
        using var line = new Pen(Theme.Line);
        g.DrawLine(line, 0, _header.Height - 1, _header.Width, _header.Height - 1);
    }

    private void Change(Action<Settings> change) => _app.Tracker.UpdateSettings(change);

    private void ChooseFolder()
    {
        using var dialog = new FolderBrowserDialog { Description = "Choose the folder where Fortnite saves replays", UseDescriptionForTitle = true };
        if (dialog.ShowDialog(this) == DialogResult.OK) Change(s => s.Tracking.ReplayFolder = dialog.SelectedPath);
    }

    private async Task Link()
    {
        var code = new string(_code.Value.Where(char.IsAsciiLetterOrDigit).ToArray());
        if (code.Length < 6)
        {
            _linkMessage.Text = "Type the code shown on the website.";
            return;
        }
        _link.Enabled = false;
        _linkMessage.Text = "Linking…";
        try
        {
            var (_, message) = await _app.Tracker.Pair(code);
            _linkMessage.Text = message;
        }
        finally
        {
            _link.Enabled = true;
        }
    }

    private void Go(int step)
    {
        _step = Math.Clamp(step, 0, _steps.Count - 1);
        for (var i = 0; i < _steps.Count; i++) _steps[i].Visible = i == _step;
        _back.Visible = _step > 0;
        _next.Text = _step == _steps.Count - 1 ? "Finish" : _step == 4 && !_snapshot.Paired ? "Skip" : "Next";
        _header.Invalidate();
        // The real overlay is shown while its step is open, so the choice can be seen on the screen itself.
        _app.Overlay.Previewing = _step == 2;
        _app.Overlay.Refresh();
        Fill();
    }

    private void Next()
    {
        if (_step < _steps.Count - 1)
        {
            Go(_step + 1);
            return;
        }
        if (_snapshot.HistoryHeld > 0) _app.Tracker.SetHistoryChoice(_historyChoice.SelectedIndex == 0);
        else if (_app.Tracker.Store.Get("history.choice") is null) _app.Tracker.Store.Set("history.choice", "import");
        if (Startup.Available) Startup.Set(_startup.Checked);
        Change(s =>
        {
            s.General.SetupDone = true;
            s.General.Startup = _startup.Checked && Startup.Available ? "windows" : "none";
        });
        Close();
    }

    public void Apply(TrackerSnapshot snapshot)
    {
        _snapshot = snapshot;
        Fill();
    }

    private void Fill()
    {
        var s = _snapshot = _app.Tracker.Snapshot();
        switch (_step)
        {
            case 1:
                var ok = s.Folder == FolderState.Ok;
                _replayState.Text = ok ? $"✓  Found {Ui.Plural(s.ReplayFiles, "replay", "replays")}" : s.Folder == FolderState.Empty ? "Replay folder found, but no replays in it yet" : "Fortnite's replay folder wasn't found";
                _replayState.ForeColor = ok ? Theme.Green : Theme.GoldLight;
                _replayHelp.Text = ok
                    ? s.FolderPath + (s.PreSeasonFiles > 0 ? $"\n{s.PreSeasonFiles} of them are from before this season and are not counted." : "")
                    : "Turn replays on in Fortnite: Settings (the gear) → Game → Replays → Record Replays: On. Also turn on Record Large Team Replays if you play those modes. "
                      + "Then play one match and press \"Check again\". You can also finish setup now; the tracker starts counting as soon as replays appear.";
                _account.Text = s.ActiveProfile is { } p ? $"Fortnite account on this PC: {p.Name ?? "(name not in replay)"}. Its chests are the ones counted."
                    : ok ? "The Fortnite account is read from your first finished replay." : "";
                _account.Visible = _account.Text.Length > 0;
                _historyPanel.Visible = s.HistoryHeld > 0;
                if (s.HistoryHeld > 0)
                {
                    var range = s.HistoryFrom is { } from && s.HistoryTo is { } to ? $" (played {from:MMM d} to {to:MMM d})" : "";
                    _historyText.Text = $"{Ui.Plural(s.HistoryHeld, "match", "matches")} from this season {(s.HistoryHeld == 1 ? "is" : "are")} already on this PC{range}.";
                }
                break;
            case 2:
                var o = s.Settings.Overlay;
                _preview.Show(s);
                if (_overlayOn.Checked != o.Enabled) _overlayOn.Checked = o.Enabled;
                _preset.SetQuietly(o.Preset switch { "minimal" => 0, "expanded" => 2, _ => 1 });
                _style.SetQuietly(Math.Max(0, Array.IndexOf(new[] { "frosted", "solid", "hud", "gold" }, o.Style)));
                _preset.Enabled = _style.Enabled = o.Enabled;
                break;
            case 4:
                _linkForm.Visible = !s.Paired;
                _linkState.Text = s.Paired ? $"✓  Linked as {s.Username}" : "Not linked";
                _linkState.ForeColor = s.Paired ? Theme.Green : Theme.TextDim;
                _next.Text = s.Paired ? "Next" : "Skip";
                break;
            case 5:
                var lines = new List<string>
                {
                    s.Folder == FolderState.Ok ? $"Replays: found ({s.ReplayFiles})." : "Replays: not found yet. Turn on Record Replays in Fortnite; counting starts by itself.",
                    s.Settings.Overlay.Enabled ? $"Overlay: on, {s.Settings.Overlay.Preset} layout. {s.Settings.Shortcuts.ToggleLock} to move it, {s.Settings.Shortcuts.ToggleVisible} to hide it." : "Overlay: off (turn it on under Position).",
                    _startup.Checked && Startup.Available ? "Starts with Windows." : "Does not start by itself: open it before you play.",
                    s.Paired ? $"Linked to TOKENIFY as {s.Username}." : "Not linked to TOKENIFY (you can link under Account).",
                    s.HistoryHeld > 0 ? (_historyChoice.SelectedIndex == 0 ? $"The {Ui.Plural(s.HistoryHeld, "match", "matches")} already on this PC will be counted now." : "Counting starts with your next match.") : "",
                };
                var summary = string.Join("|", lines);
                if (summary == _summaryText) break;
                _summaryText = summary;
                _summary.SuspendLayout();
                while (_summary.Controls.Count > 0) _summary.Controls[0].Dispose();
                foreach (var line in lines.Where(l => l.Length > 0)) _summary.Controls.Add(Bullet(line));
                _summary.Controls.Add(Ui.Gap(8));
                _summary.Controls.Add(Ui.Dim("After each match, give it a few seconds: Fortnite finishes the replay, the tracker reads it, and the overlay shows the new total.", 560));
                _summary.ResumeLayout();
                break;
        }
    }
}
