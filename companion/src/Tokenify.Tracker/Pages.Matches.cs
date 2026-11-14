using System.Drawing.Drawing2D;
using Tokenify.Core;
using Tokenify.ReplayReader;

namespace Tokenify.Tracker;

/// <summary>
/// The match history and, for the selected match, the audit view: every chest event the replay holds, how it was
/// classified and why, with the tools to correct a count. The replay's own count and evidence are never changed.
/// </summary>
internal sealed class MatchesPage : Page
{
    private sealed record Entry(MatchRow? Match, FileRow? File, int Number);

    private readonly Panel _split = new() { BackColor = Theme.Bg0 };
    private readonly ListBox _list = new()
    {
        DrawMode = DrawMode.OwnerDrawFixed, BorderStyle = BorderStyle.None, BackColor = Theme.Bg2, ForeColor = Theme.Text, IntegralHeight = false,
    };
    private readonly Panel _detail = new() { AutoScroll = true, BackColor = Theme.Bg0 };
    private readonly Label _title = Ui.Text("", 560, Theme.Text, Theme.Head(15f));
    private readonly Label _count = Ui.Text("", 560, Theme.Text, Theme.Number(34f));
    private readonly Label _breakdown = Ui.Text("", 560);
    private readonly Label _category = Ui.Text("", 560, Theme.Gold, Theme.Head(10.5f));
    private readonly Label _note = Ui.Dim("", 560);
    private readonly Label _history = Ui.Faint("", 560);
    private readonly TkButton _correct, _exclude, _reviewed, _addAmbiguous, _useTaps, _usePartial, _ignorePartial, _export;
    private readonly TkSegment _filter;
    private readonly ListBox _events = new()
    {
        DrawMode = DrawMode.OwnerDrawVariable, BorderStyle = BorderStyle.None, BackColor = Theme.Bg2, ForeColor = Theme.Text, IntegralHeight = false,
    };
    private readonly Label _eventsEmpty = Ui.Dim("", 560);
    private readonly FlowLayoutPanel _eventsBlock;
    private string _signature = "";
    private long? _selected;
    private MatchRow? _match;
    private List<ChestEvent> _allEvents = new();

    public MatchesPage(TrackerApp app) : base(app, "Matches", "Every finished match the tracker has read. Pick one to see its chests, one by one, and to correct its count.")
    {
        AutoScroll = false;
        _correct = new TkButton("Correct count…", ButtonKind.Primary, (_, _) => CorrectDialog());
        _exclude = new TkButton("Leave out of statistics", ButtonKind.Secondary, (_, _) => { if (_match is { } m) App.Tracker.SetExcluded(m.Id, !m.Excluded); });
        _reviewed = new TkButton("The count is right", ButtonKind.Secondary, (_, _) => { if (_match is { } m) App.Tracker.SetReviewed(m.Id, true); });
        _addAmbiguous = new TkButton("They were mine: add them", ButtonKind.Secondary, (_, _) => AddAmbiguous());
        _useTaps = new TkButton("Use my own count", ButtonKind.Secondary, (_, _) =>
        {
            if (_match is { ManualTaps: int taps } m) App.Tracker.Correct(m.Id, taps, "my own count while playing (manual shortcut)");
        });
        _usePartial = new TkButton("Use this count", ButtonKind.Primary, (_, _) => { if (_match is { } m) App.Tracker.ResolvePartial(m.Id, true); });
        _ignorePartial = new TkButton("Don't count this match", ButtonKind.Secondary, (_, _) => { if (_match is { } m) App.Tracker.ResolvePartial(m.Id, false); });
        _export = new TkButton("Save diagnostic file…", ButtonKind.Ghost, (_, _) => ExportDiagnostic());
        _filter = new TkSegment(["All", "Searched", "Destroyed", "Special", "Ambiguous"], 0, _ => ShowEvents()) { Width = Theme.Px(520) };

        _events.DrawItem += DrawEvent;
        _events.MeasureItem += MeasureEvent;
        _events.Size = Theme.Px(560, 300);
        _events.Margin = Theme.Pad(0, 0, 0, 8);
        _eventsBlock = Ui.Stack(Ui.Gap(8), Ui.Label("Chest events in this match"), _filter, _events, _eventsEmpty,
            Ui.Faint("\"Confirmed\" means the game itself named you on the chest. \"Ambiguous\" means the replay shows something happened but not who did it: "
                     + "those are never counted unless you add them.", 560), Ui.Row(_export));

        var stack = Ui.Stack(_title, _count, _breakdown, _category, _note,
            Ui.Row(_correct, _usePartial, _ignorePartial, _exclude), Ui.Row(_addAmbiguous, _reviewed, _useTaps), _history, _eventsBlock);
        stack.Location = new Point(Theme.Px(18), Theme.Px(4));
        _detail.Controls.Add(stack);

        _list.DrawItem += DrawEntry;
        _list.SelectedIndexChanged += (_, _) =>
        {
            if (Filling || _list.SelectedItem is not Entry entry) return;
            _selected = entry.Match?.Id;
            ShowDetail(entry);
        };
        _split.Controls.Add(_detail);
        _split.Controls.Add(_list);
        Controls.Add(_split);
        Resize += (_, _) => Arrange();
    }

    private float S => Theme.Scale;
    private int _detailWidth;

    /// <summary>Text wraps and button rows fold at the width the detail side has.</summary>
    private static void Fit(Control? control, int width)
    {
        if (control is null) return;
        if (control is Label or FlowLayoutPanel) control.MaximumSize = new Size(width, 0);
        foreach (Control child in control.Controls) Fit(child, width);
    }

    private void Arrange()
    {
        var top = Body.Bottom + (int)(4 * S);
        _split.SetBounds(Body.Left, top, Math.Max(200, ClientSize.Width - Body.Left * 2), Math.Max(120, ClientSize.Height - top - (int)(18 * S)));
        var listWidth = (int)(300 * S);
        _list.SetBounds(0, 0, listWidth, _split.Height);
        _list.ItemHeight = (int)(58 * S);
        _detail.SetBounds(listWidth + (int)(8 * S), 0, _split.Width - listWidth - (int)(8 * S), _split.Height);
        var width = Math.Max((int)(260 * S), _detail.ClientSize.Width - (int)(44 * S));
        if (width == _detailWidth) return;
        _detailWidth = width;
        Fit(_detail.Controls.Count > 0 ? _detail.Controls[0] : null, width);
        _events.Width = width;
        _filter.Width = Math.Min(width, (int)(520 * S));
        if (_match is not null) ShowEvents();   // rows are as tall as their text at this width
    }

    public override void Shown() => Arrange();

    public void Select(long? matchId)
    {
        if (matchId is null) return;
        _selected = matchId;
        _signature = "";
        if (Snapshot is { } s) Fill(s);
    }

    protected override void Apply(TrackerSnapshot s)
    {
        Arrange();
        var matches = App.Tracker.Store.AllMatchesLight();
        var files = App.Tracker.Store.Files().Where(f => f.State is "unsupported" or "failed" || (f.State == "recording" && f.Error is not null)).ToList();
        var signature = $"{matches.Count}|{(matches.Count > 0 ? matches.Max(m => m.Updated) : 0)}|{files.Count}|{s.Settings.Tracking.CountDestroyed}|{string.Join(",", matches.Select(m => m.Reviewed ? 1 : 0))}";
        if (signature == _signature) return;
        _signature = signature;

        var entries = new List<Entry>();
        var number = 0;
        foreach (var m in matches) entries.Add(new Entry(m, null, ++number));
        foreach (var f in files) entries.Add(new Entry(null, f, 0));
        entries.Sort((a, b) => When(b).CompareTo(When(a)));

        _list.BeginUpdate();
        _list.Items.Clear();
        foreach (var entry in entries) _list.Items.Add(entry);
        var index = _selected is long id ? entries.FindIndex(e => e.Match?.Id == id) : -1;
        if (index < 0 && entries.Count > 0) index = 0;
        _list.SelectedIndex = index;
        _list.EndUpdate();

        if (index >= 0)
        {
            _selected = entries[index].Match?.Id;
            ShowDetail(entries[index]);
        }
        else
        {
            _match = null;
            _title.Text = "No matches yet";
            _count.Text = "";
            _breakdown.Text = "Play a match with replays turned on. It appears here a few seconds after it ends.";
            foreach (var c in new Control[] { _category, _note, _history, _correct, _exclude, _reviewed, _addAmbiguous, _useTaps, _usePartial, _ignorePartial, _eventsBlock }) c.Visible = false;
        }
    }

    private static long When(Entry e) => e.Match?.Started ?? e.File?.Started ?? e.File?.MTime ?? 0;

    private bool CountDestroyed => Snapshot?.Settings.Tracking.CountDestroyed ?? true;

    private void ShowDetail(Entry entry)
    {
        if (entry.File is { } file)
        {
            _match = null;
            var when = DateTimeOffset.FromUnixTimeMilliseconds(file.Started ?? file.MTime).LocalDateTime;
            _title.Text = $"Replay from {when:MMM d, h:mm tt}";
            _count.Text = "—";
            _category.Text = file.State == "unsupported" ? Category.UnsupportedReplay.ToUpperInvariant() : "NOT READABLE";
            _category.ForeColor = Theme.Coral;
            _breakdown.Text = file.State switch
            {
                "unsupported" => "This replay comes from a Fortnite version the tracker can't read yet. It is kept and will be read after an update of the tracker.",
                "recording" => "Fortnite never finished writing this replay (the game closed or crashed mid-match), so it can't be read.",
                _ => "This file could not be read as a replay.",
            };
            _note.Text = $"It is not counted, and not as a zero-chest game either.  ({file.Name}: {file.Error})";
            foreach (var c in new Control[] { _history, _correct, _exclude, _reviewed, _addAmbiguous, _useTaps, _usePartial, _ignorePartial, _eventsBlock }) c.Visible = false;
            _category.Visible = _note.Visible = true;
            return;
        }

        var m = App.Tracker.Store.GetMatch(entry.Match!.Id) ?? entry.Match;
        _match = m;
        var replay = m.ReplayCount(CountDestroyed);
        var effective = m.Effective(CountDestroyed);
        _title.Text = $"Match {entry.Number}  ·  {m.StartedLocal:MMM d, h:mm tt}  ·  {Math.Max(1, (int)Math.Round(m.Length.TotalMinutes))} min";
        _count.Text = $"{Fmt.Int(effective)} {(effective == 1 ? "chest" : "chests")}";
        _category.Visible = true;
        _category.Text = m.Category.ToUpperInvariant() + (m.Excluded ? "  ·  LEFT OUT OF STATISTICS" : "")
            + (m.Upload == Upload.NotMine ? "  ·  ANOTHER PLAYER'S ACCOUNT" : m.Upload == Upload.HeldProfile ? "  ·  WAITING: IS THIS ACCOUNT YOURS?" : "");
        _category.ForeColor = m.Category == Category.ReplayConfirmed ? Theme.Green : m.Category == Category.UserCorrected ? Theme.Gold : Theme.Coral;

        var parts = new List<string> { $"{m.Searched} searched", $"{m.Destroyed} destroyed{(CountDestroyed ? "" : " (not counted: your setting)")}" };
        if (m.Ambiguous > 0) parts.Add($"{m.Ambiguous} ambiguous (not counted)");
        _breakdown.Text = (m.Corrected is int corrected ? $"Replay detected: {replay}.  You observed: {corrected}.  Difference: {corrected - replay:+0;-0;0}.   " : "The replay shows: ")
            + string.Join("  ·  ", parts);

        var notes = new List<string>();
        if (m.Partial && m.Corrected is null) notes.Add("Only part of this replay could be read, so the real count may be higher. It is not in your statistics unless you choose to use it.");
        if (m.ManualTaps is int taps) notes.Add($"Your own count while playing (manual shortcut): {taps}.");
        if (m.PlayerName is not null) notes.Add($"Fortnite account: {m.PlayerName}.");
        if (m.NoRecord > 0) notes.Add($"{Ui.Plural(m.NoRecord, "chest spot", "chest spots")} also vanished with no record of who or why, unrelated to anything you did: not counted.");
        notes.Add(m.Upload switch
        {
            Upload.Sent => "On your TOKENIFY account.",
            Upload.Pending => Snapshot?.Paired == true ? "Waiting to be sent to TOKENIFY." : "Kept on this PC (not linked to TOKENIFY).",
            Upload.LocalOnly => "Kept on this PC only: this replay has no match id the website accepts.",
            Upload.Rejected => "TOKENIFY refused it: " + m.LastError,
            Upload.Conflict => "Not sent: this Fortnite account is linked to a different TOKENIFY account.",
            _ => "",
        });
        _note.Text = string.Join("  ", notes.Where(n => n.Length > 0));
        _note.Visible = _note.Text.Length > 0;

        var corrections = App.Tracker.Store.Corrections(m.Id);
        _history.Visible = corrections.Count > 0;
        _history.Text = "Corrections:  " + string.Join("   ", corrections.Select(h =>
            $"{DateTimeOffset.FromUnixTimeMilliseconds(h.At).LocalDateTime:MMM d h:mm tt}: {(h.NewCount is int n ? $"set to {n}" : "removed")} by {(h.Source == "website" ? "the website" : "you")}"
            + (h.Reason is null ? "" : $" (\"{h.Reason}\")")));

        var pendingPartial = m.Partial && m.Corrected is null && m.Upload is Upload.HeldPartial;
        _usePartial.Visible = _ignorePartial.Visible = pendingPartial;
        _correct.Visible = true;
        _correct.Kind = pendingPartial ? ButtonKind.Secondary : ButtonKind.Primary;
        _exclude.Visible = !pendingPartial;
        _exclude.Text = m.Excluded ? "Put back in statistics" : "Leave out of statistics";
        _addAmbiguous.Visible = m.Ambiguous > 0 && m.Corrected is null;
        _addAmbiguous.Text = m.Ambiguous == 1 ? "The ambiguous chest was mine: add it" : $"The {m.Ambiguous} ambiguous chests were mine: add them";
        _reviewed.Visible = m.Ambiguous > 0 && m.Corrected is null && !m.Reviewed;
        _useTaps.Visible = m.ManualTaps is int t && t != effective;

        _allEvents = Core.Tracker.EventsOf(m);
        _eventsBlock.Visible = true;
        ShowEvents();
    }

    private void ShowEvents()
    {
        IEnumerable<ChestEvent> shown = _filter.SelectedIndex switch
        {
            1 => _allEvents.Where(e => e.Action == ChestAction.Searched),
            2 => _allEvents.Where(e => e.Action == ChestAction.Destroyed),
            3 => _allEvents.Where(e => e.Kind != "regular"),
            4 => _allEvents.Where(e => e.Evidence == Evidence.Ambiguous),
            _ => _allEvents,
        };
        var list = shown.ToList();
        _events.BeginUpdate();
        _events.Items.Clear();
        foreach (var e in list) _events.Items.Add(e);
        _events.EndUpdate();
        // Every event is shown in full; the whole right side scrolls, not a list inside it.
        var height = 2;
        for (var i = 0; i < list.Count; i++) height += _events.GetItemHeight(i);
        _events.Height = Math.Clamp(height, (int)(40 * S), 30_000);
        _events.Visible = list.Count > 0;
        _eventsEmpty.Visible = list.Count == 0;
        _eventsEmpty.Text = _allEvents.Count == 0
            ? (_match?.Events is null ? "This match was counted by an earlier version that kept no event list. Rescan replays (Tracking) to rebuild it while the replay is still on this PC."
                : "The replay holds no chest event for you in this match.")
            : "No event of this kind in this match.";
    }

    private void AddAmbiguous()
    {
        if (_match is not { } m) return;
        var n = m.Ambiguous;
        App.Tracker.Correct(m.Id, m.ReplayCount(CountDestroyed) + n, $"added {n} {(n == 1 ? "chest" : "chests")} the replay could not attribute");
    }

    private void CorrectDialog()
    {
        if (_match is not { } m) return;
        using var dialog = new CorrectionDialog(m, CountDestroyed);
        if (dialog.ShowDialog(FindForm()) != DialogResult.OK) return;
        App.Tracker.Correct(m.Id, dialog.Count, dialog.Reason);
    }

    private void ExportDiagnostic()
    {
        if (_match is not { } m) return;
        var around = _events.SelectedItem as ChestEvent;
        using var save = new SaveFileDialog
        {
            Title = "Save diagnostic file",
            Filter = "JSON file (*.json)|*.json",
            FileName = Path.GetFileName(Exporter.DefaultPath($"match-{m.StartedLocal:yyyyMMdd-HHmm}", "json")),
            InitialDirectory = AppPaths.Exports,
        };
        Directory.CreateDirectory(AppPaths.Exports);
        if (save.ShowDialog(FindForm()) != DialogResult.OK) return;
        var full = App.Tracker.Store.GetMatch(m.Id) ?? m;
        Exporter.Write(save.FileName, Exporter.Diagnostic(full, App.Tracker.Store.Corrections(m.Id), App.Tracker.Settings, around?.Time));
        MessageBox.Show(FindForm(), (around is null ? "Saved every chest event of this match." : $"Saved the chest events within a minute of {Exporter.Clock(around.Time)}.")
            + "\n\nThe file holds what was read from the replay, not the replay itself.", "TOKENIFY", MessageBoxButtons.OK, MessageBoxIcon.Information);
    }

    // ------------------------------------------------------------------------------------------- drawing

    private void DrawEntry(object? sender, DrawItemEventArgs e)
    {
        if (e.Index < 0 || _list.Items[e.Index] is not Entry entry) return;
        var g = e.Graphics;
        var selected = (e.State & DrawItemState.Selected) != 0;
        var r = e.Bounds;
        using (var back = new SolidBrush(selected ? Theme.Bg4 : Theme.Bg2)) g.FillRectangle(back, r);
        if (selected)
        {
            using var bar = new SolidBrush(Theme.Gold);
            g.FillRectangle(bar, r.Left, r.Top + 8 * S, 3.5f * S, r.Height - 16 * S);
        }
        using (var line = new Pen(Theme.Line)) g.DrawLine(line, r.Left + 12 * S, r.Bottom - 1, r.Right - 8 * S, r.Bottom - 1);

        var m = entry.Match;
        var when = DateTimeOffset.FromUnixTimeMilliseconds(When(entry)).LocalDateTime;
        var title = $"{when:MMM d}  ·  {when:h:mm tt}";
        string sub;
        Color subColor;
        string number;
        if (m is null)
        {
            sub = entry.File!.State == "unsupported" ? "Unsupported replay: not counted" : "Could not be read: not counted";
            subColor = Theme.Coral;
            number = "—";
        }
        else
        {
            var tags = new List<string> { m.Category };
            if (m.Excluded) tags.Add("left out");
            if (m.Upload == Upload.NotMine) tags.Add("another account");
            if (m.Ambiguous > 0 && !m.Reviewed && m.Corrected is null) tags.Add($"{m.Ambiguous} to review");
            sub = string.Join("  ·  ", tags);
            subColor = m.Ambiguous > 0 && !m.Reviewed && m.Corrected is null ? Theme.Gold : m.Partial && m.Corrected is null ? Theme.Coral : Theme.TextDim;
            number = m.Partial && m.Corrected is null ? m.ReplayCount(CountDestroyed) + "?" : Fmt.Int(m.Effective(CountDestroyed));
        }
        var textLeft = r.Left + (int)(16 * S);
        var numberWidth = (int)(70 * S);
        TextRenderer.DrawText(g, title, Theme.Bold(10f), new Rectangle(textLeft, r.Top + (int)(9 * S), r.Width - numberWidth - (int)(24 * S), (int)(20 * S)),
            m is { Valid: false } ? Theme.TextDim : Theme.Text, TextFormatFlags.NoPadding | TextFormatFlags.EndEllipsis);
        TextRenderer.DrawText(g, sub, Theme.Small, new Rectangle(textLeft, r.Top + (int)(31 * S), r.Width - numberWidth - (int)(24 * S), (int)(18 * S)),
            subColor, TextFormatFlags.NoPadding | TextFormatFlags.EndEllipsis);
        TextRenderer.DrawText(g, number, Theme.Number(19f), new Rectangle(r.Right - numberWidth - (int)(10 * S), r.Top, numberWidth, r.Height),
            m is { Valid: true } ? Theme.Text : Theme.TextFaint, TextFormatFlags.Right | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
    }

    private static string EventDetail(ChestEvent ev)
    {
        var detail = ev.Reason ?? string.Join("  ·  ", ev.Signals.Select(Exporter.SignalText));
        return ev.Holding is null ? detail : detail + $"  ·  holding {ev.Holding}";
    }

    private void MeasureEvent(object? sender, MeasureItemEventArgs e)
    {
        if (e.Index < 0 || _events.Items[e.Index] is not ChestEvent ev) return;
        var width = Math.Max((int)(120 * S), _events.Width - (int)(24 * S));
        var text = TextRenderer.MeasureText(EventDetail(ev), Theme.Small, new Size(width, int.MaxValue), TextFormatFlags.NoPadding | TextFormatFlags.WordBreak);
        e.ItemHeight = Math.Clamp((int)(28 * S) + text.Height + (int)(9 * S), (int)(52 * S), 255);
    }

    private void DrawEvent(object? sender, DrawItemEventArgs e)
    {
        if (e.Index < 0 || _events.Items[e.Index] is not ChestEvent ev) return;
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        var r = e.Bounds;
        var selected = (e.State & DrawItemState.Selected) != 0;
        using (var back = new SolidBrush(selected ? Theme.Bg4 : Theme.Bg2)) g.FillRectangle(back, r);
        using (var line = new Pen(Theme.Line)) g.DrawLine(line, r.Left + 10 * S, r.Bottom - 1, r.Right - 8 * S, r.Bottom - 1);

        var what = ev.Action switch { ChestAction.Searched => "Opened", ChestAction.Destroyed => "Destroyed", _ => "Vanished" };
        var kind = ev.Kind switch { "regular" => "Chest", "rare" => "Rare chest", "buried" => "Buried chest", "relic" => "Sprite vault chest", "poster" => "Sprite poster chest", _ => "Chest" };
        var (chip, chipColor) = ev.Evidence switch
        {
            Evidence.Confirmed => ("CONFIRMED", Theme.Green),
            Evidence.Corroborated => ("CORROBORATED", Theme.GoldLight),
            Evidence.Ambiguous => ("AMBIGUOUS · NOT COUNTED", Theme.Gold),
            _ => ("NO RECORD", Theme.TextFaint),
        };
        var chipWidth = TextRenderer.MeasureText(chip, Theme.Head(8.5f)).Width + (int)(14 * S);
        var chipRect = new RectangleF(r.Right - chipWidth - 10 * S, r.Top + 8 * S, chipWidth, 18 * S);
        using (var shape = Theme.Rounded(chipRect, 9 * S))
        using (var fill = new SolidBrush(Theme.Mix(Theme.Bg2, chipColor, 0.16f)))
        using (var edge = new Pen(Theme.Mix(Theme.Bg2, chipColor, 0.6f)))
        {
            g.FillPath(fill, shape);
            g.DrawPath(edge, shape);
        }
        TextRenderer.DrawText(g, chip, Theme.Head(8.5f), Rectangle.Round(chipRect), chipColor, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);

        var left = r.Left + (int)(12 * S);
        var width = r.Width - chipWidth - (int)(34 * S);
        TextRenderer.DrawText(g, $"{Exporter.Clock(ev.Time)}   {what}  ·  {kind}{(ev.Reopened ? "  ·  refilled, opened again" : "")}{(ev.Unverified ? "  ·  unverified type" : "")}",
            Theme.Bold(9.75f), new Rectangle(left, r.Top + (int)(7 * S), width, (int)(20 * S)), ev.Counted ? Theme.Text : Theme.TextDim, TextFormatFlags.NoPadding | TextFormatFlags.EndEllipsis);
        TextRenderer.DrawText(g, EventDetail(ev), Theme.Small, new Rectangle(left, r.Top + (int)(28 * S), r.Width - (int)(24 * S), r.Height - (int)(34 * S)),
            ev.Evidence == Evidence.Ambiguous ? Theme.GoldLight : Theme.TextDim, TextFormatFlags.NoPadding | TextFormatFlags.WordBreak);
    }
}

/// <summary>"Correct count": the replay's count stays on record next to what the player enters.</summary>
internal sealed class CorrectionDialog : TkForm
{
    private readonly NumericUpDown _number = new() { Minimum = 0, Maximum = 400, Width = Theme.Px(90), Font = Theme.Number(16f), BackColor = Theme.Bg1, ForeColor = Theme.Text, BorderStyle = BorderStyle.FixedSingle };
    private readonly TkTextBox _reason = new(360, placeholder: "Why? (optional, for example: one behind a wall was missed)");

    public int? Count { get; private set; }
    public string? Reason => string.IsNullOrWhiteSpace(_reason.Value) ? null : _reason.Value.Trim();

    public CorrectionDialog(MatchRow match, bool countDestroyed)
    {
        Text = "Correct count";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = MinimizeBox = false;
        StartPosition = FormStartPosition.CenterParent;
        ClientSize = Theme.Px(440, 300);

        var replay = match.ReplayCount(countDestroyed);
        _number.Value = Math.Clamp(match.Corrected ?? replay, 0, 400);
        if (match.CorrectionReason is not null) _reason.Value = match.CorrectionReason;

        var save = new TkButton("Save", ButtonKind.Primary, (_, _) => { Count = (int)_number.Value; DialogResult = DialogResult.OK; });
        var remove = new TkButton("Remove my correction", ButtonKind.Danger, (_, _) => { Count = null; DialogResult = DialogResult.OK; }) { Visible = match.Corrected is not null };
        var cancel = new TkButton("Cancel", ButtonKind.Ghost, (_, _) => DialogResult = DialogResult.Cancel);
        var stack = Ui.Stack(
            Ui.Text($"Match of {match.StartedLocal:MMM d, h:mm tt}", 400, Theme.Text, Theme.Head(13f)),
            Ui.Text($"Replay detected: {replay} {(replay == 1 ? "chest" : "chests")}.", 400, Theme.TextDim),
            Ui.Gap(6), Ui.Label("You observed"), _number, Ui.Gap(4), _reason,
            Ui.Faint("The replay's own count and its events stay on record; this match is then shown as \"User Corrected\".", 400),
            Ui.Gap(8), Ui.Row(save, remove, cancel));
        stack.Location = new Point(Theme.Px(22), Theme.Px(18));
        Controls.Add(stack);
        AcceptButton = save;
        CancelButton = cancel;
    }
}
