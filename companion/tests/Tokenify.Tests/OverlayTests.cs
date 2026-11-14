using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using Tokenify.Core;
using Tokenify.Overlay;

namespace Tokenify.Tests;

/// <summary>
/// The overlay without a screen: where it is placed (several displays, an unplugged display, a saved position that
/// is now off-screen) and what is drawn (size against scale, the panel against background opacity, no clipped text).
/// </summary>
public static partial class Tests
{
    private static IEnumerable<(string, Action)> OverlayTests()
    {
        yield return ("overlay position: first run sits fully on the main display", PlaceDefault);
        yield return ("overlay position: a dragged position is remembered and found again", PlaceRoundTrip);
        yield return ("overlay position: works on a display left of or above the main one (negative coordinates)", PlaceNegative);
        yield return ("overlay position: each display keeps its own remembered place", PlacePerDisplay);
        yield return ("overlay position: an unplugged display falls back to the main one and is not forgotten", PlaceUnplugged);
        yield return ("overlay position: a saved position off the screen is brought back into view", PlaceOffScreen);
        yield return ("overlay position: survives a change of resolution", PlaceResolution);
        yield return ("overlay position: a panel larger than the display starts at its top-left", PlaceTooLarge);
        yield return ("overlay position: snaps to edges and centre only when close", PlaceSnap);
        yield return ("overlay position: the corner buttons keep the whole panel on the display", PlaceCorners);
        yield return ("overlay drawing: size follows the scale from 50% to 200%", RenderScale);
        yield return ("overlay drawing: background opacity changes the panel, never the numbers", RenderOpacity);
        yield return ("overlay drawing: no text touches the panel edge in any layout, style or size", RenderNoClipping);
        yield return ("overlay drawing: the same content always draws the same picture", RenderDeterministic);
        yield return ("overlay drawing: minimal is the smallest layout, expanded the tallest", RenderPresets);
        yield return ("overlay drawing: long numbers and labels still fit", RenderLongValues);
    }

    // ------------------------------------------------------------------ placement

    private static readonly MonitorInfo Main = new(@"\\.\DISPLAY1", new Rectangle(0, 0, 1920, 1080), true);
    private static readonly MonitorInfo Right4K = new(@"\\.\DISPLAY2", new Rectangle(1920, -400, 3840, 2160), false);
    private static readonly MonitorInfo LeftSmall = new(@"\\.\DISPLAY3", new Rectangle(-1280, 200, 1280, 720), false);
    private static readonly Size Panel = new(236, 248);

    private static void Inside(Rectangle bounds, Point topLeft, Size panel, string what)
    {
        var r = new Rectangle(topLeft, panel);
        if (!bounds.Contains(r)) throw new Exception($"{what}: {r} is not inside {bounds}");
    }

    private static void PlaceDefault()
    {
        var o = new OverlaySettings();
        var p = Placement.Resolve(o, [Main], Panel);
        Inside(Main.Bounds, p, Panel, "single display");
        p = Placement.Resolve(o, [Right4K, Main, LeftSmall], Panel);
        Inside(Main.Bounds, p, Panel, "three displays: the main one is used");
        // no display reported at all (a remote session starting up): still a sane answer, no exception
        p = Placement.Resolve(o, [], Panel);
        Inside(new Rectangle(0, 0, 1920, 1080), p, Panel, "no display reported");
    }

    private static void PlaceRoundTrip()
    {
        var monitors = new[] { Main, Right4K, LeftSmall };
        foreach (var target in new[] { new Point(40, 60), new Point(1500, 700), new Point(1920 + 3000, 1200), new Point(-900, 500) })
        {
            var o = new OverlaySettings();
            Placement.Remember(o, target, Panel, monitors);
            var back = Placement.Resolve(o, monitors, Panel);
            True(Math.Abs(back.X - target.X) <= 1 && Math.Abs(back.Y - target.Y) <= 1, $"dropped at {target}, found at {back}");
        }
    }

    private static void PlaceNegative()
    {
        var monitors = new[] { Main, LeftSmall, new MonitorInfo(@"\\.\DISPLAY4", new Rectangle(0, -1440, 2560, 1440), false) };
        var o = new OverlaySettings();
        Placement.Remember(o, new Point(-1200, 260), Panel, monitors);
        Eq(@"\\.\DISPLAY3", o.Monitor, "display on the left");
        Inside(LeftSmall.Bounds, Placement.Resolve(o, monitors, Panel), Panel, "left display");
        Placement.Remember(o, new Point(900, -1300), Panel, monitors);
        Eq(@"\\.\DISPLAY4", o.Monitor, "display above");
        Inside(monitors[2].Bounds, Placement.Resolve(o, monitors, Panel), Panel, "display above");
    }

    private static void PlacePerDisplay()
    {
        var monitors = new[] { Main, Right4K };
        var o = new OverlaySettings();
        Placement.Remember(o, new Point(100, 900 - Panel.Height), Panel, monitors);
        var onMain = Placement.Resolve(o, monitors, Panel);
        Placement.Remember(o, new Point(1920 + 3400, 100), Panel, monitors);
        var onRight = Placement.Resolve(o, monitors, Panel);
        Inside(Right4K.Bounds, onRight, Panel, "second display");
        Eq(2, o.Places.Count, "one remembered place per display");
        o.Monitor = Main.Name;   // "Display 1" chosen again in Settings
        var again = Placement.Resolve(o, monitors, Panel);
        True(Math.Abs(again.X - onMain.X) <= 1 && Math.Abs(again.Y - onMain.Y) <= 1, $"back on the main display at its own place ({again} vs {onMain})");
    }

    private static void PlaceUnplugged()
    {
        var both = new[] { Main, Right4K };
        var o = new OverlaySettings();
        Placement.Remember(o, new Point(1920 + 3000, 1500), Panel, both);
        Eq(Right4K.Name, o.Monitor, "remembered on the second display");
        var alone = Placement.Resolve(o, [Main], Panel);
        Inside(Main.Bounds, alone, Panel, "second display unplugged");
        Eq(Right4K.Name, o.Monitor, "its own display is not forgotten");
        var back = Placement.Resolve(o, both, Panel);
        Inside(Right4K.Bounds, back, Panel, "second display plugged in again");
        True(Math.Abs(back.X - (1920 + 3000)) <= 1 && Math.Abs(back.Y - 1500) <= 1, $"and at the same place ({back})");
        // the main display itself is gone and another one is now the only one
        var onlyOther = Placement.Resolve(new OverlaySettings { Monitor = Main.Name }, [LeftSmall with { Primary = true }], Panel);
        Inside(LeftSmall.Bounds, onlyOther, Panel, "only a different display is left");
    }

    private static void PlaceOffScreen()
    {
        foreach (var (x, y) in new[] { (1.2, 1.2), (-0.5, 0.3), (0.3, -2.0), (5.0, -5.0), (0.999, 0.999) })
        {
            var o = new OverlaySettings { Monitor = Main.Name };
            o.Places[Main.Name] = new OverlayPlace { X = x, Y = y };
            Inside(Main.Bounds, Placement.Resolve(o, [Main], Panel), Panel, $"saved at {x:0.###},{y:0.###}");
        }
        var far = new OverlaySettings { Monitor = Main.Name };
        far.Places[Main.Name] = new OverlayPlace { X = 1.2, Y = 1.2 };
        var p = Placement.Resolve(far, [Main], Panel);
        Eq(new Point(1920 - Panel.Width, 1080 - Panel.Height), p, "it comes back in the corner nearest to where it was");
        Eq(new Point(0, 0), Placement.EnsureVisible(new Point(-5000, -5000), Panel, Main.Bounds), "far beyond the top-left");
    }

    private static void PlaceResolution()
    {
        var o = new OverlaySettings();
        Placement.Remember(o, new Point(1600, 800), Panel, [Main]);
        // the same display, now 1280 x 720: the same relative place, and still completely visible
        var smaller = Main with { Bounds = new Rectangle(0, 0, 1280, 720) };
        var p = Placement.Resolve(o, [smaller], Panel);
        Inside(smaller.Bounds, p, Panel, "smaller resolution");
        // ...and 3840 x 2160: proportionally the same place
        var larger = Main with { Bounds = new Rectangle(0, 0, 3840, 2160) };
        var q = Placement.Resolve(o, [larger], Panel);
        True(Math.Abs(q.X - 3200) <= 2 && Math.Abs(q.Y - 1600) <= 2, $"larger resolution keeps the relative place ({q})");
    }

    private static void PlaceTooLarge()
    {
        var tiny = new MonitorInfo("tiny", new Rectangle(100, 100, 200, 150), true);
        var p = Placement.Resolve(new OverlaySettings(), [tiny], new Size(400, 300));
        Eq(new Point(100, 100), p, "top-left of the display");
        Eq(new Point(100, 100), Placement.Corner("bottom-right", new Size(400, 300), tiny.Bounds), "corner button with an oversized panel");
    }

    private static void PlaceSnap()
    {
        var b = Main.Bounds;
        Eq(new Point(0, 0), Placement.Snap(new Point(5, 4), Panel, b), "near the top-left corner");
        Eq(new Point(12, 12), Placement.Snap(new Point(10, 11), Panel, b), "nearer to the margin line than to the edge: the margin line");
        Eq(new Point(b.Right - Panel.Width, b.Bottom - Panel.Height), Placement.Snap(new Point(b.Right - Panel.Width - 4, b.Bottom - Panel.Height + 8), Panel, b), "near the bottom-right corner");
        Eq(new Point(b.Right - Panel.Width - 12, b.Bottom - Panel.Height - 12), Placement.Snap(new Point(b.Right - Panel.Width - 15, b.Bottom - Panel.Height - 20), Panel, b), "near the bottom-right margin lines");
        Eq((b.Width - Panel.Width) / 2, Placement.Snap(new Point((b.Width - Panel.Width) / 2 + 10, 400), Panel, b).X, "near the centre line");
        Eq(new Point(500, 400), Placement.Snap(new Point(500, 400), Panel, b), "nowhere near anything: left alone");
        Eq(new Point(300 + Placement.SnapDistance + 40, 300), Placement.Snap(new Point(300 + Placement.SnapDistance + 40, 300), Panel, b), "just outside the snap distance");
        // on a display that does not start at 0,0
        var s = Placement.Snap(new Point(Right4K.Bounds.Left + 4, Right4K.Bounds.Top + 3), Panel, Right4K.Bounds);
        Eq(new Point(Right4K.Bounds.Left, Right4K.Bounds.Top), s, "second display's own corner");
    }

    private static void PlaceCorners()
    {
        foreach (var bounds in new[] { Main.Bounds, Right4K.Bounds, LeftSmall.Bounds })
        {
            foreach (var where in new[] { "top-left", "top-right", "left", "right", "bottom-left", "bottom-right", "centre" })
            {
                Inside(bounds, Placement.Corner(where, Panel, bounds), Panel, $"{where} on {bounds}");
            }
            True(Placement.Corner("top-left", Panel, bounds).X < Placement.Corner("top-right", Panel, bounds).X, "left is left of right");
            True(Placement.Corner("top-left", Panel, bounds).Y < Placement.Corner("bottom-left", Panel, bounds).Y, "top is above bottom");
            Eq(Placement.Corner("left", Panel, bounds).Y, Placement.Corner("right", Panel, bounds).Y, "the two middle positions share a height");
        }
    }

    // ------------------------------------------------------------------ drawing

    private static OverlayFrame Draw(Settings s, Stats? stats = null, float dpi = 1f, string? toast = null, float toastAlpha = 0f, Activity activity = Activity.Ready, string status = "READY",
        SquadInfo? squad = null)
    {
        var content = Display.Build(RenderSamples.Snapshot(stats ?? RenderSamples.SampleStats(), s, activity, status, squad), s.Overlay.Metrics(), toast);
        return OverlayRenderer.Render(content, s.Overlay, dpi, new RenderState(false, toastAlpha));
    }

    /// <summary>Every pixel of a frame as straight (not premultiplied) ARGB.</summary>
    private static int[] Pixels(Bitmap bitmap)
    {
        var rect = new Rectangle(0, 0, bitmap.Width, bitmap.Height);
        var data = bitmap.LockBits(rect, ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        try
        {
            var pixels = new int[bitmap.Width * bitmap.Height];
            for (var y = 0; y < bitmap.Height; y++) Marshal.Copy(data.Scan0 + y * data.Stride, pixels, y * bitmap.Width, bitmap.Width);
            return pixels;
        }
        finally
        {
            bitmap.UnlockBits(data);
        }
    }

    private static int A(int argb) => (argb >> 24) & 255;

    /// <summary>The light, solid pixels of numbers and labels (not the panel, its edge or the shadow).</summary>
    private static bool Ink(int argb) => A(argb) >= 235 && ((argb >> 16) & 255) >= 200 && ((argb >> 8) & 255) >= 195 && (argb & 255) >= 180;

    /// <summary>The fully solid middle of the white digits only: no smoothed edge pixels, which blend with whatever is behind.</summary>
    private static int SolidWhite(OverlayFrame frame) =>
        Pixels(frame.Bitmap).Count(p => A(p) == 255 && ((p >> 16) & 255) >= 236 && ((p >> 8) & 255) >= 232 && (p & 255) >= 218);

    private static Rectangle InkBounds(OverlayFrame frame, out int count)
    {
        var px = Pixels(frame.Bitmap);
        int w = frame.Bitmap.Width, h = frame.Bitmap.Height, left = w, top = h, right = -1, bottom = -1;
        count = 0;
        for (var y = 0; y < h; y++)
        {
            for (var x = 0; x < w; x++)
            {
                if (!Ink(px[y * w + x])) continue;
                count++;
                if (x < left) left = x;
                if (x > right) right = x;
                if (y < top) top = y;
                if (y > bottom) bottom = y;
            }
        }
        return count == 0 ? Rectangle.Empty : Rectangle.FromLTRB(left, top, right + 1, bottom + 1);
    }

    private static void RenderScale()
    {
        Size At(int scale, float dpi = 1f)
        {
            var s = new Settings();
            s.Overlay.Scale = scale;
            using var frame = Draw(s, dpi: dpi);
            True(new Rectangle(Point.Empty, frame.Bitmap.Size).Contains(frame.Panel), $"the panel lies inside its picture at {scale}%");
            return frame.Panel.Size;
        }
        var normal = At(100);
        True(normal.Width is > 150 and < 420 && normal.Height is > 120 and < 420, $"a sensible size at 100% ({normal})");
        var half = At(50);
        var twice = At(200);
        True(Math.Abs(twice.Width / (double)normal.Width - 2) < 0.08 && Math.Abs(twice.Height / (double)normal.Height - 2) < 0.08, $"200% is twice the size ({normal} to {twice})");
        True(Math.Abs(half.Width / (double)normal.Width - 0.5) < 0.06 && Math.Abs(half.Height / (double)normal.Height - 0.5) < 0.06, $"50% is half the size ({normal} to {half})");
        // Windows display scaling multiplies on top: 100% overlay on a 150% display is as large as a 150% overlay on a 100% display
        var onScaledDisplay = At(100, 1.5f);
        var bigger = At(150);
        True(Math.Abs(onScaledDisplay.Width - bigger.Width) <= 1 && Math.Abs(onScaledDisplay.Height - bigger.Height) <= 1, $"display scaling and overlay scale combine ({onScaledDisplay} vs {bigger})");
    }

    private static void RenderOpacity()
    {
        // the share of the panel's inside that is completely see-through
        static double ClearShare(OverlayFrame frame)
        {
            var px = Pixels(frame.Bitmap);
            int w = frame.Bitmap.Width, clear = 0, total = 0;
            var inner = Rectangle.Inflate(frame.Panel, -14, -14);
            for (var y = inner.Top; y < inner.Bottom; y++)
            {
                for (var x = inner.Left; x < inner.Right; x++)
                {
                    total++;
                    if (A(px[y * w + x]) < 6) clear++;
                }
            }
            return clear / (double)Math.Max(1, total);
        }
        (double Clear, int Ink, int Solid, Size Panel) At(int opacity)
        {
            var s = new Settings();
            s.Overlay.BackgroundOpacity = opacity;
            s.Overlay.Shadow = false;
            using var frame = Draw(s);
            InkBounds(frame, out var ink);
            return (ClearShare(frame), ink, SolidWhite(frame), frame.Panel.Size);
        }
        var none = At(0);
        var mid = At(45);
        var most = At(90);
        True(none.Clear > 0.45, $"at 0% most of the panel is see-through ({none.Clear:P0})");
        True(mid.Clear < 0.01 && most.Clear < 0.01, $"with a background nothing inside the panel is fully see-through ({mid.Clear:P1}, {most.Clear:P1})");
        True(none.Ink > 400 && mid.Ink > 400 && most.Ink > 400, $"the numbers are solid at every opacity ({none.Ink}, {mid.Ink}, {most.Ink} pixels)");
        True(none.Solid > 250 && Math.Abs(mid.Solid - most.Solid) <= most.Solid * 0.03 && Math.Abs(none.Solid - most.Solid) <= most.Solid * 0.10,
            $"the digits are equally solid at 0%, 45% and 90% ({none.Solid}, {mid.Solid}, {most.Solid} fully solid pixels)");
        Eq(most.Panel, none.Panel, "the panel keeps its size whatever its opacity");
        // HUD Only is "no panel" by design, whatever the opacity setting says
        var hud = new Settings();
        hud.Overlay.Style = "hud";
        hud.Overlay.BackgroundOpacity = 90;
        using var hudFrame = Draw(hud);
        var hudClear = ClearShare(hudFrame);
        True(hudClear > 0.45, $"HUD Only draws no panel behind the numbers ({hudClear:P0} see-through)");
    }

    private static void RenderNoClipping()
    {
        var stats = RenderSamples.SampleStats();
        var checkedCount = 0;
        foreach (var preset in new[] { "minimal", "standard", "expanded" })
        {
            foreach (var style in new[] { "frosted", "solid", "hud", "gold" })
            {
                foreach (var scale in new[] { 50, 100, 200 })
                {
                    foreach (var text in new[] { 80, 100, 130 })
                    {
                        foreach (var padding in new[] { 6, 12, 24 })
                        {
                            var s = new Settings();
                            s.Overlay.Preset = preset;
                            s.Overlay.Style = style;
                            s.Overlay.Scale = scale;
                            s.Overlay.FontScale = text;
                            s.Overlay.Padding = padding;
                            using var frame = Draw(s, stats, toast: "+42 CHESTS", toastAlpha: 1f, activity: Activity.InMatch, status: "MATCH IN PROGRESS");
                            var ink = InkBounds(frame, out var count);
                            var what = $"{preset}/{style} at {scale}%, text {text}%, padding {padding}";
                            True(count > 25, $"{what}: something is drawn ({count} pixels)");
                            // text must keep clear of the panel's own edge (2 px at 100%, scaled)
                            var keep = Math.Max(1, (int)Math.Floor(2 * scale / 100.0));
                            var safe = Rectangle.Inflate(frame.Panel, -keep, -keep);
                            True(safe.Contains(ink), $"{what}: text {ink} reaches the edge of the panel {frame.Panel}");
                            checkedCount++;
                        }
                    }
                }
            }
        }
        Eq(3 * 4 * 3 * 3 * 3, checkedCount, "combinations checked");
    }

    private static void RenderDeterministic()
    {
        var s = new Settings();
        s.Overlay.Preset = "expanded";
        using var a = Draw(s);
        using var b = Draw(s);
        Eq(a.Bitmap.Size, b.Bitmap.Size, "size");
        True(Pixels(a.Bitmap).AsSpan().SequenceEqual(Pixels(b.Bitmap)), "pixels");
    }

    private static void RenderPresets()
    {
        Size Of(string preset)
        {
            var s = new Settings();
            s.Overlay.Preset = preset;
            using var frame = Draw(s);
            return frame.Panel.Size;
        }
        var minimal = Of("minimal");
        var standard = Of("standard");
        var expanded = Of("expanded");
        True(minimal.Height < standard.Height * 0.6, $"minimal ({minimal}) is much shorter than standard ({standard})");
        True(minimal.Width * minimal.Height < standard.Width * standard.Height, "minimal covers less of the screen than standard");
        True(expanded.Height > standard.Height, $"expanded ({expanded}) is taller than standard ({standard})");
        True(standard.Width * standard.Height < 1920 * 1080 * 0.04, $"standard covers under 4% of a 1080p screen ({standard})");
    }

    private static void RenderLongValues()
    {
        // a seven-digit total, three-digit games and every optional line at once
        var games = Enumerable.Range(0, 30).Select(i => new GameStat(i + 1, T0.AddMinutes(i * 25), T0.AddMinutes(i * 25 + 20), 380 + i)).ToList();
        var stats = Stats.Compute(games, new StatsOptions(StartingTotal: 9_876_543), games[^1].EndedUtc.AddMinutes(2));
        foreach (var preset in new[] { "minimal", "standard", "expanded", "custom" })
        {
            var s = new Settings();
            s.Overlay.Preset = preset;
            if (preset == "custom") s.Overlay.CustomMetrics = [.. Metric.All.Select(m => m.Id)];
            using var frame = Draw(s, stats, squad: new SquadInfo(1234, false, 4, 2, null));
            var ink = InkBounds(frame, out var count);
            True(count > 100, $"{preset}: drawn");
            True(Rectangle.Inflate(frame.Panel, -2, -2).Contains(ink), $"{preset}: long values {ink} stay inside the panel {frame.Panel}");
            True(frame.Panel.Width < 700, $"{preset}: the panel does not grow absurdly wide ({frame.Panel.Width} px)");
        }
    }
}
