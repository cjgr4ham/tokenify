namespace Tokenify.Companion;

/// <summary>Small layout helpers so every window uses the same fonts, spacing and colours (and scales with DPI).</summary>
internal static class Ui
{
    public static readonly Color Gold = Color.FromArgb(232, 176, 75);
    public static readonly Color Good = Color.FromArgb(46, 160, 67);
    public static readonly Color Warn = Color.FromArgb(214, 137, 16);
    public static readonly Color Bad = Color.FromArgb(207, 34, 46);

    public static Font Body { get; } = new("Segoe UI", 9.75f);
    public static Font Small { get; } = new("Segoe UI", 8.75f);
    public static Font Bold { get; } = new("Segoe UI Semibold", 9.75f);
    public static Font Heading { get; } = new("Segoe UI Semibold", 12f);
    public static Font Title { get; } = new("Segoe UI Black", 15f);

    public static Label Text(string text, Font? font = null, Color? color = null, int maxWidth = 520) => new()
    {
        Text = text,
        Font = font ?? Body,
        AutoSize = true,
        MaximumSize = new Size(maxWidth, 0),
        Margin = new Padding(0, 2, 0, 4),
        ForeColor = color ?? SystemColors.ControlText,
    };

    public static Label Muted(string text, int maxWidth = 520) => Text(text, Small, SystemColors.GrayText, maxWidth);

    public static Button Button(string text, EventHandler onClick, bool primary = false)
    {
        var b = new Button
        {
            Text = text,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Padding = new Padding(10, 3, 10, 3),
            Margin = new Padding(0, 4, 8, 4),
            Font = primary ? Bold : Body,
            UseVisualStyleBackColor = true,
        };
        b.Click += onClick;
        return b;
    }

    public static FlowLayoutPanel Row(params Control[] controls)
    {
        var p = new FlowLayoutPanel { AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, WrapContents = true, Margin = new Padding(0), FlowDirection = FlowDirection.LeftToRight };
        p.Controls.AddRange(controls);
        return p;
    }

    /// <summary>A vertical stack that grows with its content.</summary>
    public static TableLayoutPanel Stack(Padding? padding = null)
    {
        var t = new TableLayoutPanel
        {
            ColumnCount = 1,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Dock = DockStyle.Top,
            Padding = padding ?? new Padding(16, 12, 16, 12),
        };
        t.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        return t;
    }

    public static void Add(this TableLayoutPanel t, Control c)
    {
        t.RowCount++;
        t.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        t.Controls.Add(c, 0, t.RowCount - 1);
    }

    public static Panel Separator() => new() { Height = 1, Dock = DockStyle.Top, BackColor = SystemColors.ControlLight, Margin = new Padding(0, 8, 0, 8), Width = 520 };

    /// <summary>Numbered step heading, e.g. "1  Fortnite replays".</summary>
    public static Control Step(int number, string title)
    {
        var n = new Label { Text = number.ToString(), Font = Bold, AutoSize = false, Size = new Size(24, 24), TextAlign = ContentAlignment.MiddleCenter, BackColor = Gold, ForeColor = Color.FromArgb(20, 20, 20), Margin = new Padding(0, 6, 8, 2) };
        var t = new Label { Text = title, Font = Heading, AutoSize = true, Margin = new Padding(0, 6, 0, 2) };
        return Row(n, t);
    }

    public static string Plural(int n, string one, string many) => $"{n} {(n == 1 ? one : many)}";
}
