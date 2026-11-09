using System.Diagnostics;
using System.Text.Json;

namespace Tokenify.Companion;

/// <summary>The companion's window: Status, Recent matches, Settings, Help &amp; diagnostics.</summary>
internal sealed class CompanionWindow : Form
{
    public enum Tab { Status, Matches, Settings, Help }

    private readonly CompanionApp _app;
    private readonly Engine _engine;
    private readonly TabControl _tabs = new() { Dock = DockStyle.Fill, Padding = new Point(14, 5) };

    // status tab
    private readonly Label _dot = new() { Text = "●", AutoSize = true, Font = new Font("Segoe UI", 16f), Margin = new Padding(0, 0, 6, 0) };
    private readonly Label _headline = Ui.Text("", Ui.Heading);
    private readonly Label _account = Ui.Text("");
    private readonly Label _folder = Ui.Text("");
    private readonly Label _lastMatch = Ui.Text("");
    private readonly Label _sync = Ui.Text("");
    private readonly TableLayoutPanel _attention = Ui.Stack(new Padding(0));
    private readonly ListView _profiles = new() { View = View.Details, FullRowSelect = true, HeaderStyle = ColumnHeaderStyle.Nonclickable, Height = 92, Width = 560, MultiSelect = false };
    private readonly Button _connectButton;
    private string _attentionKey = "";

    // matches tab
    private readonly ListView _matches = new() { View = View.Details, FullRowSelect = true, Dock = DockStyle.Fill, MultiSelect = false };
    private readonly Button _sendAnyway, _ignore;
    private string _matchesKey = "";

    // settings tab
    private readonly CheckBox _auto = new() { Text = "Count chests automatically after each match", AutoSize = true };
    private readonly CheckBox _startup = new() { Text = "Start with Windows (quietly, in the notification area)", AutoSize = true };
    private readonly CheckBox _notify = new() { Text = "Show a notification when a match is synced (\"37 CHESTS SYNCED TO TOKENIFY\")", AutoSize = true };
    private readonly Label _folderPath = Ui.Text("");
    private bool _loadingSettings;

    // help tab
    private readonly TableLayoutPanel _checks = Ui.Stack(new Padding(0));
    private string _checksKey = "";

    public CompanionWindow(CompanionApp app, Engine engine)
    {
        _app = app;
        _engine = engine;
        Text = "TOKENIFY Companion";
        Icon = Embedded.AppIcon(32);
        Font = Ui.Body;
        AutoScaleMode = AutoScaleMode.Dpi;
        StartPosition = FormStartPosition.CenterScreen;
        MinimumSize = new Size(680, 560);
        Size = new Size(720, 620);

        _connectButton = Ui.Button("Connect…", (_, _) => _app.ShowSetup(force: true));
        _sendAnyway = Ui.Button("Send anyway", (_, _) => ResolveSelected(send: true));
        _ignore = Ui.Button("Ignore", (_, _) => ResolveSelected(send: false));

        _tabs.TabPages.Add(StatusPage());
        _tabs.TabPages.Add(MatchesPage());
        _tabs.TabPages.Add(SettingsPage());
        _tabs.TabPages.Add(HelpPage());
        _tabs.SelectedIndexChanged += (_, _) => { if (_tabs.SelectedIndex == (int)Tab.Matches) LoadMatches(force: true); };
        Controls.Add(_tabs);

        // Closing the window keeps the companion running in the notification area.
        FormClosing += (_, e) =>
        {
            if (e.CloseReason == CloseReason.UserClosing)
            {
                e.Cancel = true;
                Hide();
            }
        };
    }

    public void Select(Tab tab) => _tabs.SelectedIndex = (int)tab;

    // ------------------------------------------------------------------------------------ pages

    private static TabPage Page(string title, Control content)
    {
        var page = new TabPage(title) { AutoScroll = true, UseVisualStyleBackColor = true };
        page.Controls.Add(content);
        return page;
    }

    private TabPage StatusPage()
    {
        var s = Ui.Stack();
        s.Add(Ui.Row(_dot, _headline));
        s.Add(_account);
        s.Add(Ui.Row(_connectButton));
        s.Add(Ui.Separator());
        s.Add(Ui.Text("Fortnite replays", Ui.Bold));
        s.Add(_folder);
        s.Add(Ui.Text("Last match", Ui.Bold));
        s.Add(_lastMatch);
        s.Add(Ui.Text("Sync", Ui.Bold));
        s.Add(_sync);
        s.Add(_attention);
        s.Add(Ui.Separator());
        s.Add(Ui.Text("Fortnite accounts on this PC", Ui.Bold));
        _profiles.Columns.Add("Fortnite player", 220);
        _profiles.Columns.Add("Counted for you?", 170);
        _profiles.Columns.Add("Last match", 150);
        s.Add(_profiles);
        s.Add(Ui.Row(
            Ui.Button("It's my account", (_, _) => SetSelectedProfile(true)),
            Ui.Button("Not my account", (_, _) => SetSelectedProfile(false))));
        s.Add(Ui.Separator());
        s.Add(Ui.Row(
            Ui.Button("Scan replays now", (_, _) => { _engine.ScanNow(); _engine.SyncNow(); }, primary: true),
            Ui.Button("Open TOKENIFY", (_, _) => _app.OpenWebsite())));
        return Page("Status", s);
    }

    private TabPage MatchesPage()
    {
        _matches.Columns.Add("Match", 150);
        _matches.Columns.Add("Chests", 70, HorizontalAlignment.Right);
        _matches.Columns.Add("Types", 170);
        _matches.Columns.Add("TOKENIFY", 150);
        _matches.Columns.Add("Note", 220);
        _matches.SelectedIndexChanged += (_, _) => UpdateMatchButtons();
        var panel = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 3, Padding = new Padding(12) };
        panel.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        panel.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        panel.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        panel.Controls.Add(Ui.Muted("Every Fortnite match this PC recorded, newest first. Counts are final once they show as synced; a count you correct on the website is shown here too.", 640), 0, 0);
        panel.Controls.Add(_matches, 0, 1);
        panel.Controls.Add(Ui.Row(_sendAnyway, _ignore, Ui.Button("Refresh", (_, _) => LoadMatches(force: true)),
            Ui.Button("Open replay folder", (_, _) => OpenFolder(_engine.ResolveFolder()))), 0, 2);
        UpdateMatchButtons();
        var page = new TabPage("Recent matches") { UseVisualStyleBackColor = true };
        page.Controls.Add(panel);
        return page;
    }

    private TabPage SettingsPage()
    {
        var s = Ui.Stack();
        s.Add(Ui.Text("Tracking", Ui.Heading));
        _auto.CheckedChanged += (_, _) => { if (!_loadingSettings) _engine.SetPaused(!_auto.Checked); };
        _startup.CheckedChanged += (_, _) => { if (!_loadingSettings) Startup.Set(_startup.Checked); };
        _notify.CheckedChanged += (_, _) => { if (!_loadingSettings) _engine.Store.SetFlag("notify.synced", _notify.Checked); };
        s.Add(_auto);
        s.Add(_startup);
        s.Add(_notify);
        s.Add(Ui.Separator());
        s.Add(Ui.Text("Replay folder", Ui.Heading));
        s.Add(_folderPath);
        s.Add(Ui.Row(
            Ui.Button("Choose folder…", (_, _) => ChooseFolder()),
            Ui.Button("Use Fortnite's default", (_, _) => { _engine.SetFolder(null); _engine.Publish(); }),
            Ui.Button("Open folder", (_, _) => OpenFolder(_engine.ResolveFolder()))));
        s.Add(Ui.Separator());
        s.Add(Ui.Text("What TOKENIFY receives", Ui.Heading));
        s.Add(Ui.Text("For each finished match: the number of chests you opened and when (seconds into the match), the chest types, the match start and end time, Fortnite's match id (so squad-mates' results line up), the game version and the parser version, and a one-way hash of your Fortnite account id (plus its display name once, so you can recognise it on the website). This PC is listed as \"Windows PC\" (rename it on the website) with its status: waiting, in a match, syncing. Never the replay file, other players' names or data, your Fortnite password, or anything else from this PC.", maxWidth: 600));
        s.Add(Ui.Separator());
        s.Add(Ui.Text("This PC", Ui.Heading));
        s.Add(Ui.Row(
            Ui.Button("Clear local cache…", (_, _) => ClearCache()),
            Ui.Button("Disconnect this PC…", (_, _) => _ = Disconnect())));
        s.Add(Ui.Muted("Clearing the cache reads the replays still on disk again; TOKENIFY recognises matches it already has, so nothing is counted twice. Disconnecting stops all uploads from this PC (you can also remove it on the website).", 600));
        return Page("Settings", s);
    }

    private TabPage HelpPage()
    {
        var s = Ui.Stack();
        s.Add(Ui.Text("Diagnostics", Ui.Heading));
        s.Add(_checks);
        s.Add(Ui.Row(
            Ui.Button("Export diagnostics…", (_, _) => ExportDiagnostics()),
            Ui.Button("Open data folder", (_, _) => OpenFolder(AppPaths.DataFolder))));
        s.Add(Ui.Separator());
        s.Add(Ui.Text("How it works", Ui.Heading));
        s.Add(Ui.Text("1. Fortnite saves a replay of each match (Settings → Game → Replays → Record Replays → On).\n" +
                      "2. When the match ends, Fortnite finishes the replay file. The companion then reads it (read-only, in the background, at low priority) and finds the chests you opened: searched, or broken open, each counted once.\n" +
                      "3. The result goes to your TOKENIFY account. Your chest total, CPG, Last 10 and token chance update, and your party sees your contribution.", maxWidth: 600));
        s.Add(Ui.Text("Good to know", Ui.Heading));
        s.Add(Ui.Text("• Only matches from this season count (since Nov 1, 2026): the Pirate Token didn't exist before, so older replays are never read or sent.\n" +
                      "• Counts arrive after each match, never during it: Fortnite only makes a replay readable when the match is over.\n" +
                      "• A Fortnite update can change the replay format. If replays stop being read, update the companion; unread replays are kept and retried.\n" +
                      "• Your own replay only covers your chests. Each PC player in a squad adds their own; Switch and console players keep counting on the website.\n" +
                      "• The companion only reads replay files. It never touches the Fortnite program, its memory, its files or your input.\n" +
                      "• TOKENIFY Companion is an independent fan tool, not made or endorsed by Epic Games.", maxWidth: 600));
        s.Add(Ui.Muted($"Version {AppInfo.Version} · parser {AppInfo.ParserVersion} · open source (GPL-3.0) · data folder {AppPaths.DataFolder}", 600));
        return Page("Help & diagnostics", s);
    }

    // ------------------------------------------------------------------------------------ updates

    public void Apply(EngineSnapshot s)
    {
        if (IsDisposed) return;
        _headline.Text = s.Status;
        _dot.ForeColor = s.Activity switch
        {
            Activity.UpToDate or Activity.Waiting => Ui.Good,
            Activity.InMatch or Activity.Processing or Activity.Syncing => Ui.Gold,
            Activity.Offline or Activity.Paused => Ui.Warn,
            _ => Ui.Bad,
        };
        _account.Text = !s.Paired ? "Not connected to TOKENIFY yet."
            : s.Revoked ? $"This PC was disconnected from TOKENIFY ({s.Username}). Reconnect to keep syncing."
            : $"Connected to TOKENIFY as {s.Username}{(s.DeviceName is { } d ? $" · this PC: {d}" : "")}{(s.Tracking == "manual" ? " · your account is set to manual counting, so imports are kept but not added (change it on the website)" : "")}";
        _connectButton.Text = s.Paired && !s.Revoked ? "Reconnect…" : "Connect…";
        _folder.Text = s.Folder switch
        {
            FolderState.Ok => $"{Ui.Plural(s.ReplayFiles, "replay", "replays")} in {s.FolderPath}" +
                              (s.PreSeasonFiles > 0 ? $" ({s.PreSeasonFiles} from before this season: not counted)" : ""),
            FolderState.Empty => $"Folder found, no replays yet ({s.FolderPath}). Is Record Replays on in Fortnite?",
            FolderState.Unreadable => $"Can't read {s.FolderPath}",
            _ => "Replay folder not found. In Fortnite: Settings → Game → Replays → Record Replays → On.",
        };
        _lastMatch.Text = s.LastMatch is { } m
            ? $"{m.ShownChests} chests · {m.StartedLocal:ddd MMM d, h:mm tt} · {(m.Upload == Upload.Sent ? "synced to TOKENIFY" : "waiting to sync")}{(m.ServerChests is { } sc && sc != m.Chests ? $" (corrected from {m.Chests} on the website)" : "")}"
            : "None yet. Play a match: it's counted a few seconds after it ends.";
        _sync.Text = (s.Queued == 0 ? "All matches synced" : $"{Ui.Plural(s.Queued, "match", "matches")} waiting to sync{(s.Online ? "" : " (offline: will retry)")}")
                     + (s.LastSyncUtc is { } t ? $" · last sync {t.ToLocalTime():MMM d, h:mm tt}" : "");

        RenderAttention(s);
        RenderProfiles();
        LoadSettings(s);
        RenderChecks(s);
        if (_tabs.SelectedIndex == (int)Tab.Matches) LoadMatches(force: false);
    }

    private void RenderAttention(EngineSnapshot s)
    {
        var key = string.Join("|", s.Attention) + "|" + s.AskProfile?.Hash + "|" + s.HistoryHeld;
        if (key == _attentionKey) return;
        _attentionKey = key;
        _attention.SuspendLayout();
        _attention.Controls.Clear();
        _attention.RowCount = 0;
        _attention.RowStyles.Clear();
        if (s.Attention.Count > 0)
        {
            _attention.Add(Ui.Text("Needs attention", Ui.Bold, Ui.Warn));
            foreach (var a in s.Attention) _attention.Add(Ui.Text("• " + a, maxWidth: 600));
            var buttons = new List<Control>();
            if (s.AskProfile is { } p)
            {
                buttons.Add(Ui.Button($"\"{p.Name ?? "unknown"}\" is mine", (_, _) => _engine.SetProfileDecision(p.Hash, true), primary: true));
                buttons.Add(Ui.Button("Not mine", (_, _) => _engine.SetProfileDecision(p.Hash, false)));
            }
            if (s.HistoryHeld > 0 && s.Paired)
            {
                var range = s.HistoryFrom is { } f && s.HistoryTo is { } t ? $" ({f:MMM d} – {t:MMM d})" : "";
                buttons.Add(Ui.Button($"Import {Ui.Plural(s.HistoryHeld, "older replay", "older replays")}{range}", (_, _) => _engine.SetHistoryChoice(true)));
                buttons.Add(Ui.Button("Skip them", (_, _) => _engine.SetHistoryChoice(false)));
            }
            if (s.Revoked) buttons.Add(Ui.Button("Reconnect…", (_, _) => _app.ShowSetup(force: true), primary: true));
            if (s.Folder is FolderState.Missing or FolderState.Unreadable) buttons.Add(Ui.Button("Choose folder…", (_, _) => ChooseFolder()));
            if (buttons.Count > 0) _attention.Add(Ui.Row(buttons.ToArray()));
        }
        _attention.ResumeLayout();
    }

    private void RenderProfiles()
    {
        var profiles = _engine.Store.Profiles();
        var key = string.Join("|", profiles.Select(p => p.Hash + p.Decision + p.Claimed + p.Error + p.LastSeen));
        if ((string?)_profiles.Tag == key) return;
        _profiles.Tag = key;
        _profiles.BeginUpdate();
        _profiles.Items.Clear();
        foreach (var p in profiles)
        {
            var state = p.Error == "conflict" ? "Linked to another TOKENIFY account"
                : p.Decision switch { "mine" => p.Claimed ? "Yes (linked)" : "Yes", "not-mine" => "No, never sent", _ => "Waiting for your answer" };
            var item = new ListViewItem(new[] { p.Name ?? "(name not readable)", state, DateTimeOffset.FromUnixTimeMilliseconds(p.LastSeen).LocalDateTime.ToString("MMM d, h:mm tt") }) { Tag = p.Hash };
            _profiles.Items.Add(item);
        }
        if (profiles.Count == 0) _profiles.Items.Add(new ListViewItem(new[] { "None yet: the first replay tells us", "", "" }));
        _profiles.EndUpdate();
    }

    private void SetSelectedProfile(bool mine)
    {
        if (_profiles.SelectedItems.Count == 0 || _profiles.SelectedItems[0].Tag is not string hash)
        {
            MessageBox.Show(this, "Select a Fortnite account in the list first.", "TOKENIFY Companion", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }
        if (!mine && MessageBox.Show(this, "Matches from this Fortnite account will never be sent to your TOKENIFY account (matches already sent stay; you can exclude them on the website). Continue?",
                "TOKENIFY Companion", MessageBoxButtons.OKCancel, MessageBoxIcon.Question) != DialogResult.OK)
        {
            return;
        }
        _engine.SetProfileDecision(hash, mine);
    }

    private void LoadSettings(EngineSnapshot s)
    {
        _loadingSettings = true;
        _auto.Checked = !s.Paused;
        _startup.Checked = Startup.IsEnabled();
        _startup.Enabled = Startup.Available;
        _notify.Checked = _engine.Store.Flag("notify.synced");
        _folderPath.Text = s.FolderPath + (_engine.Store.Get("demos.folder") is null ? "  (Fortnite's default)" : "  (chosen by you)");
        _loadingSettings = false;
    }

    private void RenderChecks(EngineSnapshot s)
    {
        var lines = Diagnostics.Checks(_engine, s);
        var key = string.Join("|", lines.Select(l => l.Label + l.Value + l.Ok));
        if (key == _checksKey) return;
        _checksKey = key;
        _checks.SuspendLayout();
        _checks.Controls.Clear();
        _checks.RowCount = 0;
        _checks.RowStyles.Clear();
        foreach (var l in lines)
        {
            _checks.Add(Ui.Row(
                Ui.Text(l.Ok ? "✓" : "!", Ui.Bold, l.Ok ? Ui.Good : Ui.Warn, 24),
                Ui.Text(l.Label + ":", Ui.Bold, null, 170),
                Ui.Text(l.Value, maxWidth: 420)));
        }
        _checks.ResumeLayout();
    }

    private void LoadMatches(bool force)
    {
        var rows = _engine.Store.Matches(300);
        var key = string.Join("|", rows.Select(r => r.Id + r.Upload + r.ServerChests + r.Updated));
        if (!force && key == _matchesKey) return;
        _matchesKey = key;
        var selected = _matches.SelectedItems.Count > 0 ? _matches.SelectedItems[0].Tag : null;
        _matches.BeginUpdate();
        _matches.Items.Clear();
        foreach (var m in rows)
        {
            var types = "";
            try
            {
                var d = JsonSerializer.Deserialize<Dictionary<string, int>>(m.Breakdown) ?? new();
                string[] flags = ["destroyed", "reopened", "unverified"];
                types = string.Join(", ", d.Where(kv => kv.Value > 0 && !flags.Contains(kv.Key)).Select(kv => $"{kv.Value} {kv.Key}"));
                var notes = new List<string>();
                if (d.GetValueOrDefault("destroyed") is > 0 and var x) notes.Add($"{x} broken open");
                if (d.GetValueOrDefault("reopened") is > 0 and var y) notes.Add($"{y} refilled");
                if (d.GetValueOrDefault("unverified") is > 0 and var z) notes.Add($"{z} new type");
                if (notes.Count > 0) types += " (" + string.Join(", ", notes) + ")";
            }
            catch (JsonException) { }
            var tokenify = m.Upload switch
            {
                Upload.Sent => m.ServerChests is { } sc && sc != m.Chests ? $"Synced (corrected to {sc})" : "Synced",
                Upload.Pending => m.Attempts > 0 ? "Waiting (will retry)" : "Waiting to sync",
                Upload.HeldPartial => "Not sent: partly read",
                Upload.HeldProfile => "Waiting: whose account?",
                Upload.HeldHistory => "Older replay: not imported",
                Upload.NotMine => "Not sent (not your account)",
                Upload.Conflict => "Not sent (another TOKENIFY account)",
                Upload.Rejected => "Refused by TOKENIFY",
                Upload.Ignored => "Ignored",
                _ => m.Upload,
            };
            var note = m.Upload is Upload.Rejected or Upload.HeldPartial ? m.LastError ?? "" : m.Historical ? "from history" : m.SessionId is null ? "no Fortnite match id" : "";
            var item = new ListViewItem(new[] { m.StartedLocal.ToString("ddd MMM d, h:mm tt"), m.ShownChests.ToString(), types, tokenify, note }) { Tag = m.Id };
            _matches.Items.Add(item);
            if (Equals(selected, m.Id)) item.Selected = true;
        }
        var failures = _engine.Store.Files().Where(f => f.State is "failed" or "unsupported" or "recording" && f.Error is not null).Take(30);
        foreach (var f in failures)
        {
            var when = f.Started is long st ? DateTimeOffset.FromUnixTimeMilliseconds(st).LocalDateTime : DateTimeOffset.FromUnixTimeMilliseconds(f.MTime).LocalDateTime;
            _matches.Items.Add(new ListViewItem(new[] { when.ToString("ddd MMM d, h:mm tt"), "–", "", f.State == "unsupported" ? "Can't read yet (update needed)" : f.State == "recording" ? "Incomplete replay" : "Couldn't read", f.Error ?? "" }) { ForeColor = SystemColors.GrayText });
        }
        _matches.EndUpdate();
        UpdateMatchButtons();
    }

    private void UpdateMatchButtons()
    {
        var m = _matches.SelectedItems.Count > 0 && _matches.SelectedItems[0].Tag is long id ? _engine.Store.GetMatch(id) : null;
        _sendAnyway.Enabled = m?.Upload is Upload.HeldPartial or Upload.Rejected or Upload.Ignored;
        _ignore.Enabled = m?.Upload is Upload.HeldPartial or Upload.Pending or Upload.Rejected;
    }

    private void ResolveSelected(bool send)
    {
        if (_matches.SelectedItems.Count == 0 || _matches.SelectedItems[0].Tag is not long id) return;
        if (send && MessageBox.Show(this, "Send this match to TOKENIFY with the count shown? If the replay was only read partly, some chests may be missing; you can correct the count on the website.",
                "TOKENIFY Companion", MessageBoxButtons.OKCancel, MessageBoxIcon.Question) != DialogResult.OK)
        {
            return;
        }
        _engine.ResolvePartial(id, send);
        LoadMatches(force: true);
    }

    // ------------------------------------------------------------------------------------ actions

    private void ChooseFolder()
    {
        using var dlg = new FolderBrowserDialog { Description = "Choose the folder Fortnite saves replays in (…\\FortniteGame\\Saved\\Demos)", UseDescriptionForTitle = true, SelectedPath = _engine.ResolveFolder() };
        if (dlg.ShowDialog(this) == DialogResult.OK) _engine.SetFolder(dlg.SelectedPath);
    }

    private void ClearCache()
    {
        if (MessageBox.Show(this, "Read every replay still on this PC again? Matches TOKENIFY already has are recognised, so nothing is counted twice. Matches whose replays Fortnite has deleted stay safe on TOKENIFY.",
                "Clear local cache", MessageBoxButtons.OKCancel, MessageBoxIcon.Question) == DialogResult.OK)
        {
            _engine.ClearCache();
        }
    }

    private async Task Disconnect()
    {
        if (MessageBox.Show(this, "Disconnect this PC from your TOKENIFY account? It stops sending matches until you connect it again. Matches already sent stay in your history.",
                "Disconnect this PC", MessageBoxButtons.OKCancel, MessageBoxIcon.Warning) != DialogResult.OK)
        {
            return;
        }
        await _engine.Disconnect();
        Hide();
        _app.ShowSetup(force: false);
    }

    private void ExportDiagnostics()
    {
        using var dlg = new SaveFileDialog
        {
            FileName = $"TOKENIFY-Companion-diagnostics-{DateTime.Now:yyyyMMdd-HHmm}.zip",
            Filter = "Zip file|*.zip",
            InitialDirectory = Environment.GetFolderPath(Environment.SpecialFolder.Desktop),
        };
        if (dlg.ShowDialog(this) != DialogResult.OK) return;
        try
        {
            if (File.Exists(dlg.FileName)) File.Delete(dlg.FileName);
            Diagnostics.Export(_engine, dlg.FileName);
            MessageBox.Show(this, "Saved. It contains the log, versions and your match list, with no passwords, secrets or replay files.", "TOKENIFY Companion", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, "Couldn't save: " + ex.Message, "TOKENIFY Companion", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private static void OpenFolder(string path)
    {
        try
        {
            if (Directory.Exists(path)) Process.Start(new ProcessStartInfo("explorer.exe", $"\"{path}\"") { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Log.Error("Could not open folder", ex);
        }
    }
}
