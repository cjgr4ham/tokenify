using System.Drawing.Drawing2D;

namespace Tokenify.Overlay;

/// <summary>
/// The TOKENIFY compass, drawn as vectors so it stays sharp at every size (overlay, tray icon, windows). It
/// follows the website's logo (assets/brand/compass.svg): a dark dial in a gold ring, long two-tone cardinal
/// points with north the longest, short diagonal points and a centre jewel.
/// </summary>
public static class Compass
{
    private static readonly Color Ink = Color.FromArgb(0x2A, 0x19, 0x04);
    private static readonly Color Light = Color.FromArgb(0xFB, 0xE2, 0x9A);
    private static readonly Color Lightest = Color.FromArgb(0xFF, 0xF3, 0xC7);
    private static readonly Color Dark = Color.FromArgb(0xC9, 0x8A, 0x1E);
    private static readonly Color Mid = Color.FromArgb(0xE2, 0xA0, 0x2C);

    /// <summary>Draws the compass centred at (<paramref name="cx"/>, <paramref name="cy"/>) inside a square of <paramref name="size"/> pixels.</summary>
    /// <param name="tint">Recolours the gold (the overlay's accent colour); null keeps the logo's own gold.</param>
    public static void Draw(Graphics g, float cx, float cy, float size, Color? tint = null, float alpha = 1f)
    {
        // The artwork lives in a 140-unit box whose centre is slightly below the middle (north reaches further).
        var k = size / 140f;
        var state = g.Save();
        g.TranslateTransform(cx, cy + 2f * k);
        g.ScaleTransform(k, k);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.PixelOffsetMode = PixelOffsetMode.HighQuality;

        Color C(Color c)
        {
            if (tint is Color t)
            {
                // Keep the shading (how light or dark each facet is) and take the hue from the accent.
                var shade = (c.R * 0.299f + c.G * 0.587f + c.B * 0.114f) / 215f;
                c = Color.FromArgb(Clamp(t.R * shade), Clamp(t.G * shade), Clamp(t.B * shade));
            }
            return Color.FromArgb((int)(255 * alpha), c);
        }
        Color A(Color c, float a) => Color.FromArgb((int)(255 * alpha * a), c);
        var detailed = size >= 28;

        // ring: four arcs with gaps where the cardinal points pass through
        using (var ringShadow = new Pen(A(Ink, 1), 12.5f))
        using (var ring = new Pen(C(Color.FromArgb(0xE9, 0xB2, 0x3C)), 9f))
        {
            for (var i = 0; i < 4; i++)
            {
                g.DrawArc(ringShadow, -49.5f, -49.5f, 99f, 99f, -80.6f + i * 90, 71.2f);
                g.DrawArc(ring, -49.5f, -49.5f, 99f, 99f, -80.6f + i * 90, 71.2f);
            }
        }

        // dial
        using (var disc = new PathGradientBrush(new[] { new PointF(-44, -44), new PointF(44, -44), new PointF(44, 44), new PointF(-44, 44) })
               { CenterPoint = new PointF(0, -12), CenterColor = A(Color.FromArgb(0x1D, 0x2B, 0x47), 1), SurroundColors = new[] { A(Color.FromArgb(0x0A, 0x11, 0x1F), 1) } })
        {
            g.FillEllipse(disc, -44, -44, 88, 88);
        }
        using (var edge = new Pen(A(Ink, 1), 2f)) g.DrawEllipse(edge, -44, -44, 88, 88);
        if (detailed)
        {
            using var inner = new Pen(A(Color.FromArgb(0xE9, 0xB2, 0x3C), 0.35f), 1.4f);
            g.DrawEllipse(inner, -38.5f, -38.5f, 77, 77);
        }

        using var outline = new Pen(A(Ink, 1), detailed ? 2f : 1.2f) { LineJoin = LineJoin.Round };
        void Facet(Color fill, params float[] xy)
        {
            var pts = new PointF[xy.Length / 2];
            for (var i = 0; i < pts.Length; i++) pts[i] = new PointF(xy[i * 2], xy[i * 2 + 1]);
            using var brush = new SolidBrush(C(fill));
            g.FillPolygon(brush, pts);
            if (detailed) g.DrawPolygon(outline, pts);
        }

        if (detailed)
        {
            // diagonal (short) points
            Facet(Light, 0, 0, 23, -23, 0, -9); Facet(Dark, 0, 0, 23, -23, 9, 0);
            Facet(Light, 0, 0, 23, 23, 9, 0); Facet(Dark, 0, 0, 23, 23, 0, 9);
            Facet(Light, 0, 0, -23, 23, 0, 9); Facet(Dark, 0, 0, -23, 23, -9, 0);
            Facet(Light, 0, 0, -23, -23, -9, 0); Facet(Dark, 0, 0, -23, -23, 0, -9);
        }

        // cardinal (long) points; north is the longest
        Facet(Lightest, 0, 0, 0, -66, -11, -11); Facet(Mid, 0, 0, 0, -66, 11, -11);
        Facet(Light, 0, 0, 60, 0, 11, -11); Facet(Dark, 0, 0, 60, 0, 11, 11);
        Facet(Light, 0, 0, 0, 60, 11, 11); Facet(Dark, 0, 0, 0, 60, -11, 11);
        Facet(Light, 0, 0, -60, 0, -11, 11); Facet(Dark, 0, 0, -60, 0, -11, -11);

        // centre jewel
        using (var jewel = new SolidBrush(C(Color.FromArgb(0xF3, 0xC3, 0x4F)))) g.FillEllipse(jewel, -11, -11, 22, 22);
        using (var jewelEdge = new Pen(A(Ink, 1), 2f)) g.DrawEllipse(jewelEdge, -11, -11, 22, 22);
        using (var gem = new SolidBrush(C(Color.FromArgb(0xFF, 0xC5, 0x31)))) g.FillEllipse(gem, -7, -7, 14, 14);
        if (detailed)
        {
            using var glint = new SolidBrush(A(Color.White, 0.85f));
            g.FillEllipse(glint, -5f, -4.7f, 4.8f, 3f);
        }

        g.Restore(state);
    }

    private static int Clamp(float v) => (int)Math.Clamp(v, 0, 255);

    /// <summary>The compass as an icon (tray, windows) at the given pixel size.</summary>
    public static Icon ToIcon(int size, Color? badge = null)
    {
        using var bmp = new Bitmap(size, size, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(bmp))
        {
            g.Clear(Color.Transparent);
            Draw(g, size / 2f, size / 2f, size);
            if (badge is Color b)
            {
                // A small status dot, bottom right, for the tray.
                g.SmoothingMode = SmoothingMode.AntiAlias;
                var d = size * 0.42f;
                using var ring = new SolidBrush(Color.FromArgb(0x0A, 0x11, 0x1F));
                using var dot = new SolidBrush(b);
                g.FillEllipse(ring, size - d - 0.5f, size - d - 0.5f, d, d);
                g.FillEllipse(dot, size - d + 1.2f, size - d + 1.2f, d - 3.4f, d - 3.4f);
            }
        }
        var handle = bmp.GetHicon();
        try
        {
            using var temp = Icon.FromHandle(handle);
            return (Icon)temp.Clone();
        }
        finally
        {
            DestroyIcon(handle);
        }
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool DestroyIcon(IntPtr handle);
}
