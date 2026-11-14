using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;

namespace Tokenify.Tracker;

/// <summary>TOKENIFY's colours and type, the same as the website: deep navy, off-white, warm gold.</summary>
internal static class Theme
{
    public static readonly Color Bg0 = Color.FromArgb(0x06, 0x0A, 0x14);
    public static readonly Color Bg1 = Color.FromArgb(0x0A, 0x11, 0x20);
    public static readonly Color Bg2 = Color.FromArgb(0x0F, 0x18, 0x2B);
    public static readonly Color Bg3 = Color.FromArgb(0x15, 0x20, 0x39);
    public static readonly Color Bg4 = Color.FromArgb(0x1C, 0x2A, 0x47);
    public static readonly Color Line = Color.FromArgb(0x22, 0x2E, 0x4A);
    public static readonly Color LineStrong = Color.FromArgb(0x35, 0x44, 0x66);
    public static readonly Color Gold = Color.FromArgb(0xF6, 0xC2, 0x4A);
    public static readonly Color GoldLight = Color.FromArgb(0xFF, 0xE5, 0x8F);
    public static readonly Color GoldDeep = Color.FromArgb(0xE0, 0x9A, 0x2C);
    public static readonly Color GoldInk = Color.FromArgb(0x2B, 0x17, 0x00);
    public static readonly Color Text = Color.FromArgb(0xF4, 0xEC, 0xD9);
    public static readonly Color TextDim = Color.FromArgb(0xB4, 0xBC, 0xCD);
    public static readonly Color TextFaint = Color.FromArgb(0x7D, 0x87, 0x9E);
    public static readonly Color Green = Color.FromArgb(0x57, 0xD9, 0x8A);
    public static readonly Color Coral = Color.FromArgb(0xE9, 0x78, 0x5F);

    private static readonly Dictionary<string, Font> Fonts = new();
    private static readonly string HeadFamily = Pick("Bahnschrift SemiBold SemiConden", "Bahnschrift SemiCondensed", "Segoe UI Semibold");
    private static readonly string NumberFamily = Pick("Bahnschrift SemiBold Condensed", "Bahnschrift Condensed", "Segoe UI Semibold");

    private static string Pick(params string[] names)
    {
        foreach (var name in names)
        {
            try
            {
                using var family = new FontFamily(name);
                return name;
            }
            catch (ArgumentException)
            {
                // not installed
            }
        }
        return "Segoe UI";
    }

    // ------------------------------------------------------------------------------------- display scale

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromPoint(Point point, uint flags);

    [DllImport("shcore.dll")]
    private static extern int GetDpiForMonitor(IntPtr monitor, int type, out uint dpiX, out uint dpiY);

    /// <summary>
    /// The scale the tracker's windows are built at: 1 at 100% display scaling, 1.25 at 125%, and so on. Every
    /// size and every font in those windows goes through it, so text and layout always agree with each other.
    /// (The overlay is separate: it follows the display it sits on.)
    /// </summary>
    public static float Scale { get; private set; } = 1f;

    /// <summary>True when the scale was fixed from outside (tests); the display's own scale is then ignored.</summary>
    public static bool ScaleFixed { get; private set; }

    /// <summary>A size written for 100% scaling, in pixels at the current scale.</summary>
    public static int Px(double logical) => (int)Math.Round(logical * Scale);
    public static Size Px(int width, int height) => new(Px(width), Px(height));
    public static Padding Pad(int left, int top, int right, int bottom) => new(Px(left), Px(top), Px(right), Px(bottom));

    /// <summary>The scaling Windows uses on a display (the one holding the mouse pointer when none is given).</summary>
    public static float ScaleOf(Screen? screen = null)
    {
        try
        {
            var bounds = (screen ?? Screen.FromPoint(Cursor.Position)).Bounds;
            var monitor = MonitorFromPoint(new Point(bounds.Left + bounds.Width / 2, bounds.Top + bounds.Height / 2), 2 /* nearest */);
            if (monitor != IntPtr.Zero && GetDpiForMonitor(monitor, 0 /* effective */, out var dpi, out _) == 0 && dpi >= 96) return dpi / 96f;
        }
        catch (Exception)
        {
            // very old Windows: no per-display scaling to ask about
        }
        return 1f;
    }

    /// <summary>Windows built from now on are made for this scale.</summary>
    public static void UseScale(float scale)
    {
        if (!ScaleFixed) Scale = Math.Clamp(scale, 1f, 4f);
    }

    /// <summary>Start-up: the main display's scale, or the fixed one a test copy asks for (TOKENIFY_UI_SCALE=150).</summary>
    public static void InitScale()
    {
        if (Core.AppInfo.TestCopy && int.TryParse(Environment.GetEnvironmentVariable("TOKENIFY_UI_SCALE"), out var percent) && percent is >= 100 and <= 400)
        {
            Scale = percent / 100f;
            ScaleFixed = true;
            return;
        }
        UseScale(ScaleOf(Screen.PrimaryScreen));
    }

    private static Font Get(string family, float size, FontStyle style = FontStyle.Regular)
    {
        var key = $"{family}|{size}|{style}|{Scale}";
        if (!Fonts.TryGetValue(key, out var font))
        {
            // Sizes are written in points at 100%. The font is made in pixels for the scale the windows are built
            // at, so its size never depends on what Windows reports for the device it happens to be drawn on.
            Fonts[key] = font = new Font(family, size * Scale * 96f / 72f, style, GraphicsUnit.Pixel);
        }
        return font;
    }

    public static Font Body(float size = 9.75f) => Get("Segoe UI", size);
    public static Font Bold(float size = 9.75f) => Get("Segoe UI Semibold", size);
    public static Font Small => Get("Segoe UI", 8.75f);
    /// <summary>Condensed headings and labels, as on the overlay.</summary>
    public static Font Head(float size = 12f) => Get(HeadFamily, size);
    public static Font Number(float size = 22f) => Get(NumberFamily, size);

    public static GraphicsPath Rounded(RectangleF r, float radius)
    {
        var path = new GraphicsPath();
        var d = Math.Min(radius * 2, Math.Min(r.Width, r.Height));
        if (d <= 0.5f)
        {
            path.AddRectangle(r);
            return path;
        }
        path.AddArc(r.X, r.Y, d, d, 180, 90);
        path.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        path.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }

    /// <summary>The colour actually behind a control: the nearest parent that has a solid one.</summary>
    public static Color Backdrop(Control control)
    {
        for (var p = control.Parent; p is not null; p = p.Parent)
        {
            if (p.BackColor.A == 255) return p.BackColor;
        }
        return Bg0;
    }

    public static Color Mix(Color a, Color b, float t) => Color.FromArgb(
        (int)(a.R + (b.R - a.R) * t), (int)(a.G + (b.G - a.G) * t), (int)(a.B + (b.B - a.B) * t));

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    /// <summary>Dark title bar and border for a window of this program (Windows 10 2004+ and 11).</summary>
    public static void DarkTitleBar(Form form)
    {
        try
        {
            var on = 1;
            DwmSetWindowAttribute(form.Handle, 20, ref on, sizeof(int));          // immersive dark mode
            var caption = Bg1.R | (Bg1.G << 8) | (Bg1.B << 16);
            DwmSetWindowAttribute(form.Handle, 35, ref caption, sizeof(int));     // caption colour (Windows 11)
        }
        catch
        {
            // older Windows: the standard title bar
        }
    }

    public static Icon AppIcon(int size = 32)
    {
        using var stream = typeof(Theme).Assembly.GetManifestResourceStream("tokenify.ico");
        return stream is null ? SystemIcons.Application : new Icon(stream, size, size);
    }

    /// <summary>The dark, gold-accented look for menus (tray menu, drop-downs).</summary>
    public sealed class MenuRenderer() : ToolStripProfessionalRenderer(new MenuColors())
    {
        protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e)
        {
            e.TextColor = !e.Item.Enabled ? TextFaint : e.Item.Selected ? GoldLight : Text;
            base.OnRenderItemText(e);
        }

        protected override void OnRenderArrow(ToolStripArrowRenderEventArgs e)
        {
            e.ArrowColor = TextDim;
            base.OnRenderArrow(e);
        }

        protected override void OnRenderItemCheck(ToolStripItemImageRenderEventArgs e)
        {
            var r = e.ImageRectangle;
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            using var pen = new Pen(Gold, 2f) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round };
            e.Graphics.DrawLines(pen, new[]
            {
                new PointF(r.Left + r.Width * 0.22f, r.Top + r.Height * 0.52f), new PointF(r.Left + r.Width * 0.44f, r.Top + r.Height * 0.72f),
                new PointF(r.Left + r.Width * 0.80f, r.Top + r.Height * 0.30f),
            });
        }
    }

    private sealed class MenuColors : ProfessionalColorTable
    {
        public override Color ToolStripDropDownBackground => Bg2;
        public override Color MenuBorder => LineStrong;
        public override Color MenuItemBorder => Bg4;
        public override Color MenuItemSelected => Bg4;
        public override Color MenuItemSelectedGradientBegin => Bg4;
        public override Color MenuItemSelectedGradientEnd => Bg4;
        public override Color ImageMarginGradientBegin => Bg2;
        public override Color ImageMarginGradientMiddle => Bg2;
        public override Color ImageMarginGradientEnd => Bg2;
        public override Color SeparatorDark => Line;
        public override Color SeparatorLight => Line;
        public override Color CheckBackground => Bg3;
        public override Color CheckSelectedBackground => Bg4;
        public override Color CheckPressedBackground => Bg4;
    }

    public static ContextMenuStrip Menu() => new() { Renderer = new MenuRenderer(), BackColor = Bg2, ForeColor = Text, Font = Body(), ShowImageMargin = false, ShowCheckMargin = true };
}
