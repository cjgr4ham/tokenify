namespace Tokenify.Companion;

/// <summary>
/// First launch: 1) find Fortnite's replays, 2) link the TOKENIFY account with the one-time code from the website,
/// 3) start with Windows, confirm the Fortnite account, and choose whether to import older replays. Then the app
/// minimises to the notification area.
/// </summary>
internal sealed class SetupForm : Form
{
    private readonly CompanionApp _app;
    private readonly Engine _engine;
    private readonly bool _reconnect;
    private readonly Label _folderStatus;
    private readonly Label _folderHelp;
    private readonly TextBox _code;
    private readonly Button _connect;
    private readonly Label _connectStatus;
    private readonly CheckBox _startup;
    private readonly TableLayoutPanel _profilePanel;
    private readonly Label _profileText;
    private readonly CheckBox _profileMine;
    private readonly TableLayoutPanel _historyPanel;
    private readonly Label _historyText;
    private readonly RadioButton _importHistory;
    private readonly RadioButton _skipHistory;
    private readonly Button _finish;
    private (string Hash, string? Name)? _identity;
    private bool _connected;

    public SetupForm(CompanionApp app, Engine engine, bool reconnect)
    {
        _app = app;
        _engine = engine;
        _reconnect = reconnect;
        Text = reconnect ? "Reconnect TOKENIFY Companion" : "Welcome to TOKENIFY Companion";
        Icon = Embedded.AppIcon(32);
        Font = Ui.Body;
        AutoScaleMode = AutoScaleMode.Dpi;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;
        ShowInTaskbar = true;

        var s = Ui.Stack(new Padding(22, 18, 22, 16));
        s.Add(Ui.Text(reconnect ? "RECONNECT TO TOKENIFY" : "WELCOME TO TOKENIFY COMPANION", Ui.Title, Ui.Gold));
        s.Add(Ui.Text("Counts the chests you open in Fortnite from the replays Fortnite saves on this PC, and adds them to your TOKENIFY account after each match. It only reads replay files: it never touches the game."));

        // ---- 1 replays
        s.Add(Ui.Step(1, "Fortnite replays"));
        _folderStatus = Ui.Text("Checking…", Ui.Bold);
        s.Add(_folderStatus);
        _folderHelp = Ui.Muted("In Fortnite: Settings → Game → Replays → Record Replays → On. Your next match will then be saved, and counted.");
        s.Add(_folderHelp);
        s.Add(Ui.Row(
            Ui.Button("Choose folder…", (_, _) => ChooseFolder()),
            Ui.Button("Check again", (_, _) => { _engine.ScanNow(); Apply(_engine.Snapshot()); })));

        // ---- 2 account
        s.Add(Ui.Step(2, "Connect your TOKENIFY account"));
        s.Add(Ui.Text("On the TOKENIFY website, open Settings → Automatic PC tracking → LINK DEVICE (or press Open TOKENIFY below). Type the code it shows here. You never type your TOKENIFY password into this app."));
        _code = new TextBox { Width = 160, Font = new Font("Consolas", 14f), CharacterCasing = CharacterCasing.Upper, MaxLength = 9, Margin = new Padding(0, 4, 8, 4), PlaceholderText = "XXXX-XXXX" };
        _code.KeyDown += (_, e) => { if (e.KeyCode == Keys.Enter) { e.SuppressKeyPress = true; _ = Connect(); } };
        _connect = Ui.Button("Connect", (_, _) => _ = Connect(), primary: true);
        s.Add(Ui.Row(_code, _connect, Ui.Button("Open TOKENIFY", (_, _) => _app.OpenWebsite("#pc"))));
        _connectStatus = Ui.Text(reconnect ? "This replaces the current link (the old one is removed)." : "", Ui.Body);
        s.Add(_connectStatus);

        // ---- 3 options
        s.Add(Ui.Step(3, "Options"));
        _startup = new CheckBox
        {
            Text = Startup.Available ? "Start with Windows (recommended: matches are never missed)" : "Start with Windows (not available for a test copy)",
            AutoSize = true, Checked = Startup.Available, Enabled = Startup.Available, Margin = new Padding(0, 4, 0, 4),
        };
        s.Add(_startup);

        _profilePanel = Ui.Stack(new Padding(0));
        _profileText = Ui.Text("", Ui.Body);
        _profileMine = new CheckBox { Text = "This is my Fortnite account", AutoSize = true, Checked = true, Margin = new Padding(0, 2, 0, 6) };
        _profilePanel.Add(_profileText);
        _profilePanel.Add(_profileMine);
        _profilePanel.Add(Ui.Muted("Only this account's chests are sent. If someone else plays on this PC, their matches are never added to your TOKENIFY."));
        _profilePanel.Visible = false;
        s.Add(_profilePanel);

        _historyPanel = Ui.Stack(new Padding(0));
        _historyText = Ui.Text("", Ui.Body);
        _importHistory = new RadioButton { Text = "Import them into my TOKENIFY history (with their real dates)", AutoSize = true, Checked = true, Margin = new Padding(0, 2, 0, 2) };
        _skipHistory = new RadioButton { Text = "Only count matches from now on", AutoSize = true, Margin = new Padding(0, 2, 0, 6) };
        _historyPanel.Add(_historyText);
        _historyPanel.Add(_importHistory);
        _historyPanel.Add(_skipHistory);
        _historyPanel.Add(Ui.Muted("Only replays still on this PC can be imported (Fortnite keeps roughly the last 100). They go into your history, never into your current session."));
        _historyPanel.Visible = false;
        s.Add(_historyPanel);

        s.Add(Ui.Muted("TOKENIFY Companion is an independent fan tool, not made or endorsed by Epic Games. Replay-based counts arrive after each match, not during it."));

        // Content scrolls if the screen is small; Finish / Later always stay visible at the bottom.
        _finish = Ui.Button("Finish", (_, _) => Finish(), primary: true);
        _finish.Enabled = false;
        _footer = new FlowLayoutPanel { Dock = DockStyle.Bottom, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Padding = new Padding(20, 8, 20, 10) };
        _footer.Controls.Add(_finish);
        _footer.Controls.Add(Ui.Button(reconnect ? "Cancel" : "Later", (_, _) => Close()));
        s.Dock = DockStyle.None;
        s.Location = new Point(0, 0);
        _body = new Panel { Dock = DockStyle.Fill, AutoScroll = true };
        _body.Controls.Add(s);
        _stack = s;
        Controls.Add(_body);
        Controls.Add(_footer);
        AcceptButton = _connect;
        Load += (_, _) => FitToScreen();
        Shown += (_, _) => _code.Focus();
    }

    private readonly Panel _body;
    private readonly FlowLayoutPanel _footer;
    private readonly TableLayoutPanel _stack;

    /// <summary>Sizes the window to its content, but never taller than the screen (the content then scrolls).</summary>
    private void FitToScreen()
    {
        var area = Screen.FromControl(this).WorkingArea;
        var content = _stack.GetPreferredSize(Size.Empty);
        var chrome = Height - ClientSize.Height;
        var width = content.Width + SystemInformation.GetVerticalScrollBarWidthForDpi(DeviceDpi) + LogicalToDeviceUnits(12);
        var height = Math.Min(content.Height + _footer.GetPreferredSize(Size.Empty).Height, area.Height - chrome - 16);
        ClientSize = new Size(width, height);
        if (Bottom > area.Bottom - 8 || Top < area.Top) Top = Math.Max(area.Top + 8, area.Bottom - Height - 8);
        if (Right > area.Right) Left = Math.Max(area.Left, area.Right - Width - 8);
    }

    public void Apply(EngineSnapshot s)
    {
        switch (s.Folder)
        {
            case FolderState.Ok:
                _folderStatus.Text = $"✓ Found {Ui.Plural(s.ReplayFiles, "replay", "replays")} in {s.FolderPath}";
                _folderStatus.ForeColor = Ui.Good;
                _folderHelp.Text = "Keep Record Replays turned on in Fortnite (Settings → Game → Replays).";
                break;
            case FolderState.Empty:
                _folderStatus.Text = "✓ Replay folder found (no replays yet)";
                _folderStatus.ForeColor = Ui.Good;
                _folderHelp.Text = "Make sure Fortnite's Settings → Game → Replays → Record Replays is On. Your next match will be counted.";
                break;
            case FolderState.Unreadable:
                _folderStatus.Text = "✗ The replay folder can't be read: " + s.FolderPath;
                _folderStatus.ForeColor = Ui.Bad;
                break;
            default:
                _folderStatus.Text = "✗ No Fortnite replay folder found yet";
                _folderStatus.ForeColor = Ui.Warn;
                _folderHelp.Text = "Replays may be turned off. In Fortnite: Settings → Game → Replays → Record Replays → On, then play a match. If you moved Fortnite's data, choose the folder.";
                break;
        }
    }

    private void ChooseFolder()
    {
        using var dlg = new FolderBrowserDialog { Description = "Choose the folder Fortnite saves replays in (…\\FortniteGame\\Saved\\Demos)", UseDescriptionForTitle = true };
        if (dlg.ShowDialog(this) == DialogResult.OK)
        {
            _engine.SetFolder(dlg.SelectedPath);
            Apply(_engine.Snapshot());
        }
    }

    private async Task Connect()
    {
        var code = new string(_code.Text.Where(char.IsAsciiLetterOrDigit).ToArray());
        if (code.Length != 8)
        {
            _connectStatus.Text = "The code has 8 letters and numbers, like K7QX-M2PA.";
            _connectStatus.ForeColor = Ui.Warn;
            return;
        }

        _connect.Enabled = false;
        _connectStatus.Text = "Connecting…";
        _connectStatus.ForeColor = SystemColors.ControlText;
        var (ok, message) = await _engine.Pair(code);
        _connectStatus.Text = (ok ? "✓ " : "✗ ") + message;
        _connectStatus.ForeColor = ok ? Ui.Good : Ui.Bad;
        if (!ok)
        {
            _connect.Enabled = true;
            return;
        }

        _connected = true;
        _code.Enabled = false;
        _finish.Enabled = true;
        AcceptButton = _finish;
        ActiveControl = _finish;   // Enter now means Finish (not the next button in line)

        // Who plays on this PC, and is there history to import? (Read in the background: a second or two.)
        var identityTask = Task.Run(() => _engine.Store.Profiles().Count == 0 ? _engine.IdentifyNewestReplay() : null);
        var history = await Task.Run(() => _engine.Store.Get("history.choice") is null ? _engine.HistoryPreview() : (0, null, null, 0, 0));
        if (history.Count > 0)
        {
            var range = history.From is { } f && history.To is { } t
                ? f.Date == t.Date ? $" from {f:MMM d, yyyy}" : $" from {f:MMM d, yyyy} to {t:MMM d, yyyy}"
                : "";
            _historyText.Text = $"{Ui.Plural(history.Count, "older replay", "older replays")} from this season {(history.Count == 1 ? "is" : "are")} on this PC{range}" +
                                (history.Processed > 0 ? $" ({history.Processed} already processed)" : "") + "." +
                                (history.PreSeason > 0 ? $" {Ui.Plural(history.PreSeason, "replay", "replays")} from earlier seasons {(history.PreSeason == 1 ? "is" : "are")} ignored: the Pirate Token didn't exist then." : "");
            _historyPanel.Visible = true;
            FitToScreen();
        }
        else if (history.PreSeason > 0)
        {
            _connectStatus.Text += $"\n{Ui.Plural(history.PreSeason, "older replay is", "older replays are")} from earlier seasons, so {(history.PreSeason == 1 ? "it isn't" : "they aren't")} counted (no Pirate Token then).";
            FitToScreen();
        }

        _identity = await identityTask;
        if (_identity is { } id)
        {
            _profileText.Text = $"Fortnite account found in your replays: {id.Name ?? "(name not readable)"}";
            _profilePanel.Visible = true;
            FitToScreen();
        }
    }

    private void Finish()
    {
        if (!_connected) return;
        Startup.Set(_startup.Checked);
        if (_identity is { } id && _engine.Store.GetProfile(id.Hash) is null)
        {
            _engine.RememberProfile(id.Hash, id.Name, _profileMine.Checked);
        }
        if (_historyPanel.Visible)
        {
            _engine.SetHistoryChoice(_importHistory.Checked);
        }
        else if (_engine.Store.Get("history.choice") is null)
        {
            _engine.SetHistoryChoice(false);
        }
        _engine.ScanNow();
        _engine.SyncNow();
        Close();
    }
}
