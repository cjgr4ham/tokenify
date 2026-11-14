using System.Drawing.Drawing2D;
using System.Drawing.Text;
using Tokenify.Core;

namespace Tokenify.Tracker;

/// <summary>The headline numbers, drawn as one card: the total on the left, CPG and the rest beside it.</summary>
internal sealed class StatsBanner : Control
{
    private Stats _stats = Stats.Empty;
    private string _account = "";

    public StatsBanner()
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        Size = Theme.Px(TkCard.MinWidth, 150);
        Margin = Theme.Pad(0, 0, 0, 14);
    }

    public void Show(Stats stats, string account)
    {
        _stats = stats;
        _account = account;
        Invalidate();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        var s = Theme.Scale;
        g.Clear(Theme.Backdrop(this));
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;
        var rect = new RectangleF(0.5f, 0.5f, Width - 1f, Height - 1f);
        using (var shape = Theme.Rounded(rect, 14 * s))
        using (var fill = new LinearGradientBrush(new PointF(0, 0), new PointF(0, Height), Theme.Bg3, Theme.Bg2))
        using (var edge = new Pen(Theme.Mix(Theme.Line, Theme.Gold, 0.35f), 1f))
        {
            g.FillPath(fill, shape);
            g.DrawPath(edge, shape);
        }

        using var gold = new SolidBrush(Theme.Gold);
        using var text = new SolidBrush(Theme.Text);
        using var dim = new SolidBrush(Theme.TextDim);
        using var faint = new SolidBrush(Theme.TextFaint);

        // total
        g.DrawString(Fmt.Int(_stats.TotalChests), Theme.Number(46f), text, 20 * s, 16 * s);
        g.DrawString("TOTAL CHESTS", Theme.Head(11f), gold, 26 * s, 80 * s);
        var note = _stats.StartingTotal > 0 ? $"{Fmt.Int(_stats.TrackedChests)} counted on this PC + {Fmt.Int(_stats.StartingTotal)} starting total" : _account;
        g.DrawString(note, Theme.Small, faint, 26 * s, 106 * s);

        // the rest, in a grid on the right
        var cells = new (string Label, string Value, bool Key, int? Trend)[]
        {
            ("ALL-TIME CPG", Fmt.Cpg(_stats.Cpg), true, null),
            (_stats.Last10.Label + " CPG", Fmt.Cpg(_stats.Last10.Cpg), false, _stats.Trend),
            ("LAST GAME", Fmt.Count(_stats.LastGame?.Chests), false, null),
            ("BEST GAME", Fmt.Count(_stats.Best), false, null),
            ("MATCHES", Fmt.Int(_stats.Games), false, null),
            ("SESSION", _stats.Session.Active && _stats.Session.Games > 0 ? Fmt.Int(_stats.Session.Chests) : "—", false, null),
        };
        var left = Math.Max(300 * s, Width * 0.42f);
        var cellW = (Width - left - 18 * s) / 3f;
        using var divider = new Pen(Theme.Line, 1f);
        g.DrawLine(divider, left - 14 * s, 22 * s, left - 14 * s, Height - 22 * s);
        for (var i = 0; i < cells.Length; i++)
        {
            var x = left + i % 3 * cellW;
            var y = (i / 3 == 0 ? 24 : 86) * s;
            g.DrawString(cells[i].Label, Theme.Head(9f), dim, x, y);
            var valueFont = Theme.Number(cells[i].Key ? 24f : 21f);
            g.DrawString(cells[i].Value, valueFont, cells[i].Key ? gold : text, x - 2 * s, y + 17 * s);
            if (cells[i].Trend is int t && t != 0)
            {
                var w = g.MeasureString(cells[i].Value, valueFont).Width;
                var tx = x + w + 4 * s;
                var ty = y + 38 * s;
                using var arrow = new SolidBrush(t > 0 ? Theme.Green : Theme.Coral);
                g.FillPolygon(arrow, t > 0
                    ? new[] { new PointF(tx, ty + 5 * s), new PointF(tx + 9 * s, ty + 5 * s), new PointF(tx + 4.5f * s, ty - 3 * s) }
                    : new[] { new PointF(tx, ty - 3 * s), new PointF(tx + 9 * s, ty - 3 * s), new PointF(tx + 4.5f * s, ty + 5 * s) });
            }
        }
    }
}

internal sealed class OverviewPage : Page
{
    private readonly StatsBanner _banner = new();
    private readonly Label _status = Ui.Text("", 700, Theme.Text, Theme.Head(13f));
    private readonly Label _account = Ui.Dim("", 700);
    private readonly Label _folder = Ui.Dim("", 700);
    private readonly Label _attention = Ui.Text("", 700, Theme.GoldLight);
    private readonly FlowLayoutPanel _askProfile, _history, _replaysOff;
    private readonly Label _askProfileText = Ui.Text("", 700);
    private readonly Label _historyText = Ui.Text("", 700);
    private readonly Label _session = Ui.Text("", 700);
    private readonly Label _sessionNote = Ui.Faint("", 700);
    private readonly TkButton _sessionButton;
    private readonly Label _sync = Ui.Text("", 700);
    private readonly TkButton _syncButton;
    private readonly TkCard _updateCard;
    private readonly Label _updateText = Ui.Text("", 700);

    public OverviewPage(TrackerApp app) : base(app, "Overview")
    {
        _askProfile = Ui.Stack(_askProfileText, Ui.Row(
            new TkButton("Yes, it's mine", ButtonKind.Primary, (_, _) => Decide(true)),
            new TkButton("No, someone else's", ButtonKind.Secondary, (_, _) => Decide(false))), Ui.Gap(6));
        _history = Ui.Stack(_historyText, Ui.Row(
            new TkButton("Count them", ButtonKind.Primary, (_, _) => App.Tracker.SetHistoryChoice(true)),
            new TkButton("Start from now", ButtonKind.Secondary, (_, _) => App.Tracker.SetHistoryChoice(false))), Ui.Gap(6));
        _replaysOff = Ui.Stack(
            Ui.Text("Fortnite has to record replays for anything to be counted.", 700, Theme.GoldLight),
            Ui.Dim("In Fortnite: Settings → Game → Replays → turn on Record Replays (and Record Large Team Replays if you play those). Then play a match.", 700),
            Ui.Row(new TkButton("Choose another folder…", ButtonKind.Secondary, (_, _) => App.ShowWindow("tracking"))), Ui.Gap(6));
        _sessionButton = new TkButton("Start session", ButtonKind.Secondary, (_, _) =>
        {
            if (Snapshot?.Stats.Session.Active == true) App.Tracker.EndSession(); else App.Tracker.StartSession();
        });
        _syncButton = new TkButton("Link to TOKENIFY", ButtonKind.Secondary, (_, _) => App.ShowWindow("account"));
        _updateCard = Ui.Card("Update available", _updateText, Ui.Row(new TkButton("See the update", ButtonKind.Primary, (_, _) => App.ShowWindow("advanced"))));

        Add(_banner,
            Ui.Card("Right now", _status, _account, _folder, _attention, _replaysOff, _askProfile, _history,
                Ui.Row(new TkButton("Review matches", ButtonKind.Secondary, (_, _) => App.ShowWindow("matches")),
                    new TkButton("Rescan replays", ButtonKind.Ghost, (_, _) => App.Tracker.Rescan()))),
            Ui.Card("Session", _session, _sessionNote, Ui.Row(_sessionButton)),
            Ui.Card("TOKENIFY website", _sync, Ui.Row(_syncButton, new TkButton("Open website", ButtonKind.Ghost, (_, _) => Ui.OpenUrl(AppInfo.Website)))),
            _updateCard);
    }

    private void Decide(bool mine)
    {
        if (Snapshot?.AskProfile is { } p) App.Tracker.SetProfileDecision(p.Hash, mine);
    }

    protected override void Apply(TrackerSnapshot s)
    {
        _banner.Show(s.Stats, s.ActiveProfile is { } ap ? $"Fortnite account: {ap.Name ?? "(name not in replay)"}" : "Play a match with replays on to get started");

        _status.Text = s.Activity switch
        {
            Activity.InMatch => "●  Match in progress. It is counted when the match ends.",
            Activity.Processing => "●  Reading the replay of your last match…",
            Activity.Paused => "●  Tracking is paused",
            Activity.FolderMissing => "●  Fortnite's replay folder wasn't found",
            Activity.NeedsAttention => "●  Something needs a look",
            Activity.Ready => "●  Ready. Matches are counted by themselves when they end.",
            _ => "●  Waiting for your first match",
        };
        _status.ForeColor = s.Activity switch
        {
            Activity.InMatch => Theme.Green,
            Activity.Processing or Activity.NeedsAttention or Activity.FolderMissing => Theme.Gold,
            Activity.Paused => Theme.TextDim,
            _ => Theme.Text,
        };
        _account.Text = s.ActiveProfile is { } p ? $"Counting for the Fortnite account {p.Name ?? "(name not in replay)"}." : "No Fortnite account seen yet.";
        _folder.Text = s.Folder switch
        {
            FolderState.Ok => $"{Ui.Plural(s.ReplayFiles, "replay", "replays")} in {s.FolderPath}"
                + (s.PreSeasonFiles > 0 ? $"  ·  {s.PreSeasonFiles} from before this season (not counted)" : ""),
            FolderState.Empty => $"No replays yet in {s.FolderPath}",
            FolderState.Unreadable => $"Can't read {s.FolderPath}",
            _ => $"Not found: {s.FolderPath}",
        };
        _replaysOff.Visible = s.Folder is FolderState.Missing or FolderState.Empty;

        var notes = s.Attention.Where(a => !a.StartsWith("Is the Fortnite account", StringComparison.Ordinal) && !a.Contains("older replay", StringComparison.Ordinal)
                                           && !a.StartsWith("Fortnite's replay folder", StringComparison.Ordinal)).ToList();
        _attention.Text = string.Join(Environment.NewLine, notes.Select(a => "•  " + a));
        _attention.Visible = notes.Count > 0;

        _askProfile.Visible = s.AskProfile is not null;
        if (s.AskProfile is { } ask)
        {
            _askProfileText.Text = $"Another Fortnite account played on this PC: \"{ask.Name ?? "unknown"}\". Is it yours? Its matches are not counted until you answer.";
        }
        _history.Visible = s.HistoryHeld > 0;
        if (s.HistoryHeld > 0)
        {
            var range = s.HistoryFrom is { } from && s.HistoryTo is { } to ? $" ({from:MMM d} to {to:MMM d})" : "";
            _historyText.Text = $"{Ui.Plural(s.HistoryHeld, "replay", "replays")} from this season {(s.HistoryHeld == 1 ? "was" : "were")} already on this PC{range}. Count {(s.HistoryHeld == 1 ? "it" : "them")} too?";
        }

        var session = s.Stats.Session;
        var manual = s.Settings.Tracking.SessionMode == "manual";
        if (session.Active && session.Games > 0)
        {
            _session.Text = $"{Ui.Plural(session.Chests, "chest", "chests")} in {Ui.Plural(session.Games, "game", "games")}  ·  CPG {Fmt.Cpg(session.Cpg)}  ·  {Fmt.Duration(session.MatchTime)} in matches";
            _sessionNote.Text = session.Inferred
                ? $"Worked out from your matches: games less than {s.Settings.Tracking.SessionGapMinutes} minutes apart. From first match start to last match end: {Fmt.Duration(session.Span)}."
                : $"Started by you {Ui.Ago(session.StartedUtc ?? DateTime.UtcNow)}. Time since then: {Fmt.Duration(session.Span)}.";
        }
        else if (session.Active)
        {
            _session.Text = manual ? "Session running. No finished match in it yet." : "A new session starts with the match being played.";
            _sessionNote.Text = "";
        }
        else
        {
            _session.Text = manual ? "No session running." : "No session right now.";
            _sessionNote.Text = manual ? "Start one when you sit down to play; end it when you stop."
                : $"A session starts by itself with your next match and ends after {s.Settings.Tracking.SessionGapMinutes} minutes without one.";
        }
        _sessionNote.Visible = _sessionNote.Text.Length > 0;
        _sessionButton.Visible = manual;
        _sessionButton.Text = session.Active ? "End session" : "Start session";

        _sync.Text = s.Sync switch
        {
            SyncState.NotLinked => "Not linked. The tracker works on its own; link it to send your matches to your TOKENIFY account and parties.",
            SyncState.Disconnected => "This PC was disconnected from TOKENIFY. Link it again to keep syncing.",
            SyncState.UpToDate => $"Linked as {s.Username}. Everything is synced{(s.LastSyncUtc is { } t ? $" (last match sent {Ui.Ago(t)})" : "")}.",
            _ => $"Linked as {s.Username}. {s.SyncText}.",
        };
        _syncButton.Text = s.Sync is SyncState.NotLinked or SyncState.Disconnected ? "Link to TOKENIFY" : "Account";
        _syncButton.Kind = s.Sync is SyncState.NotLinked or SyncState.Disconnected ? ButtonKind.Primary : ButtonKind.Secondary;
        _syncButton.Invalidate();

        _updateCard.Visible = App.Update is not null;
        if (App.Update is { } u) _updateText.Text = $"Version {u.Version} is available (you have {AppInfo.Version}).";
    }
}
