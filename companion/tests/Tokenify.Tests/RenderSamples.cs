using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using Tokenify.Core;
using Tokenify.Overlay;

namespace Tokenify.Tests;

/// <summary>
/// Draws the overlay in every preset, style and size to PNG files, so its look can be checked by eye:
///   Tokenify.Tests.exe render [output-folder]
/// </summary>
public static class RenderSamples
{
    private static readonly DateTime T0 = new(2026, 11, 10, 12, 0, 0, DateTimeKind.Utc);

    public static Stats SampleStats(int games = 40)
    {
        var rng = new Random(7);
        var list = new List<GameStat>();
        for (var i = 0; i < games; i++)
        {
            var start = T0.AddMinutes(i < games - 4 ? i * 400 : (games - 4) * 400 + 2000 + (i - (games - 4)) * 22);
            list.Add(new GameStat(i + 1, start, start.AddMinutes(17), i == games - 1 ? 42 : 24 + rng.Next(28)));
        }
        var last = list[^1].EndedUtc;
        return Stats.Compute(list, new StatsOptions(StartingTotal: 1284 - list.Sum(g => g.Chests)), last.AddMinutes(3));
    }

    public static TrackerSnapshot Snapshot(Stats stats, Settings settings, Activity activity = Activity.Ready, string status = "READY", SquadInfo? squad = null, int taps = 0) =>
        new(settings, stats, activity, status, SyncState.UpToDate, "Synced with TOKENIFY", true, "CJGR4HAM", "Windows PC", true, "", FolderState.Ok, 11, 0, [],
            null, null, 0, null, null, 0, null, null, 0, Tracker.DefaultSeasonStartUtc, 0, squad, taps);

    public static int Run(string folder)
    {
        Directory.CreateDirectory(folder);
        var stats = SampleStats();

        // 1. presets x styles, on a bright scene
        var presets = new[] { "minimal", "standard", "expanded" };
        var styles = new[] { "frosted", "solid", "hud", "gold" };
        Sheet(Path.Combine(folder, "01-presets-and-styles.png"), 4, presets.SelectMany(p => styles.Select(st =>
        {
            var s = new Settings();
            s.Overlay.Preset = p;
            s.Overlay.Style = st;
            return (Label: $"{p} / {st}", Image: Shot(stats, s));
        })).ToList(), bright: true);

        // 2. background opacity: the panel fades, the numbers do not
        Sheet(Path.Combine(folder, "02-background-opacity.png"), 5, new[] { 0, 20, 45, 62, 90 }.Select(a =>
        {
            var s = new Settings();
            s.Overlay.BackgroundOpacity = a;
            return (Label: $"background {a}%", Image: Shot(stats, s));
        }).ToList(), bright: true);

        // 3. scale 50% to 200%
        Sheet(Path.Combine(folder, "03-scale.png"), 5, new[] { 50, 75, 100, 150, 200 }.Select(sc =>
        {
            var s = new Settings();
            s.Overlay.Scale = sc;
            return (Label: $"scale {sc}%", Image: Shot(stats, s));
        }).ToList(), bright: false);

        // 4. states: waiting, match in progress, reading, +42, needs attention, unlocked, few games, no games
        var states = new List<(string, Bitmap)>();
        var std = new Settings();
        states.Add(("ready", Shot(stats, std)));
        states.Add(("match in progress", Shot(stats, std, Activity.InMatch, "MATCH IN PROGRESS")));
        states.Add(("reading replay", Shot(stats, std, Activity.Processing, "READING REPLAY")));
        states.Add(("+42 chests", Shot(stats, std, toast: "+42 CHESTS", toastAlpha: 1f)));
        states.Add(("needs attention", Shot(stats, std, Activity.NeedsAttention, "NEEDS ATTENTION")));
        states.Add(("unlocked (editing)", Shot(stats, std, editing: true)));
        states.Add(("six games", Shot(RenderSamples.SampleStats(6), std)));
        states.Add(("no games yet", Shot(Stats.Empty, std, Activity.Waiting, "WAITING FOR A MATCH")));
        Sheet(Path.Combine(folder, "04-states.png"), 4, states, bright: true);

        // 5. options: centred, other accent, square corners, bigger text, custom metrics, squad, minimal toast
        var options = new List<(string, Bitmap)>();
        var c = new Settings(); c.Overlay.Align = "center"; options.Add(("centred", Shot(stats, c)));
        var a2 = new Settings(); a2.Overlay.Accent = "#6FD3FF"; a2.Overlay.Style = "gold"; options.Add(("accent #6FD3FF", Shot(stats, a2)));
        var sq = new Settings(); sq.Overlay.CornerRadius = 0; sq.Overlay.Shadow = false; options.Add(("square, no shadow", Shot(stats, sq)));
        var big = new Settings(); big.Overlay.FontScale = 130; options.Add(("text 130%", Shot(stats, big)));
        var custom = new Settings(); custom.Overlay.Preset = "custom";
        custom.Overlay.CustomMetrics = [Metric.Total, Metric.Cpg, Metric.SessionChests, Metric.SessionTime, Metric.Chance, Metric.Squad, Metric.Sync, Metric.Status];
        options.Add(("custom metrics", Shot(stats, custom, squad: new SquadInfo(96, false, 4, 2, null))));
        var min = new Settings(); min.Overlay.Preset = "minimal"; options.Add(("minimal +42", Shot(stats, min, toast: "+42 CHESTS", toastAlpha: 1f)));
        var hud = new Settings(); hud.Overlay.Style = "hud"; hud.Overlay.Preset = "expanded"; options.Add(("expanded hud, in match", Shot(stats, hud, Activity.InMatch, "MATCH IN PROGRESS")));
        var dpi = new Settings(); options.Add(("150% Windows scaling", Shot(stats, dpi, dpiScale: 1.5f)));
        Sheet(Path.Combine(folder, "05-options.png"), 4, options, bright: true);

        // 6. the standard overlay alone at 3x, to look at details
        var one = new Settings();
        one.Overlay.Scale = 200;
        using (var frame = OverlayRenderer.Preview(Display.Build(Snapshot(stats, one), one.Overlay.Metrics()), one.Overlay, 1.5f))
        {
            frame.Save(Path.Combine(folder, "06-standard-large.png"), ImageFormat.Png);
        }

        // 7. the compass and the tray icons
        using (var bmp = new Bitmap(520, 150, PixelFormat.Format32bppArgb))
        {
            using (var g = Graphics.FromImage(bmp))
            {
                g.Clear(Color.FromArgb(0x0A, 0x11, 0x20));
                var x = 20f;
                foreach (var size in new[] { 128, 64, 32, 24, 16 })
                {
                    Compass.Draw(g, x + size / 2f, 75, size);
                    x += size + 22;
                }
                foreach (var badge in new Color?[] { null, Color.FromArgb(0x57, 0xD9, 0x8A), Color.FromArgb(0xF6, 0xC2, 0x4A) })
                {
                    using var icon = Compass.ToIcon(32, badge);
                    g.DrawIcon(icon, (int)x, 59);
                    x += 44;
                }
            }
            bmp.Save(Path.Combine(folder, "07-compass.png"), ImageFormat.Png);
        }

        Console.WriteLine("rendered to " + folder);
        return 0;
    }

    private static Bitmap Shot(Stats stats, Settings s, Activity activity = Activity.Ready, string status = "READY", string? toast = null, float toastAlpha = 0f,
        bool editing = false, SquadInfo? squad = null, float dpiScale = 1f)
    {
        var content = Display.Build(Snapshot(stats, s, activity, status, squad), s.Overlay.Metrics(), toast);
        using var frame = OverlayRenderer.Render(content, s.Overlay, dpiScale, new RenderState(editing, toastAlpha));
        return new Bitmap(frame.Bitmap);
    }

    private static void Sheet(string path, int columns, List<(string Label, Bitmap Image)> shots, bool bright)
    {
        var cellW = shots.Max(s => s.Image.Width) + 36;
        var cellH = shots.Max(s => s.Image.Height) + 54;
        var rows = (int)Math.Ceiling(shots.Count / (double)columns);
        using var sheet = new Bitmap(cellW * columns, cellH * rows, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(sheet))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAlias;
            // a busy, bright stand-in for a game scene: the worst case for readability
            using (var sky = new LinearGradientBrush(new Point(0, 0), new Point(sheet.Width, sheet.Height),
                       bright ? Color.FromArgb(0x86, 0xC8, 0xF0) : Color.FromArgb(0x16, 0x22, 0x3A), bright ? Color.FromArgb(0xF2, 0xE2, 0xA4) : Color.FromArgb(0x0A, 0x10, 0x1E)))
            {
                g.FillRectangle(sky, 0, 0, sheet.Width, sheet.Height);
            }
            var rng = new Random(3);
            for (var i = 0; i < 60; i++)
            {
                using var blob = new SolidBrush(Color.FromArgb(bright ? 90 : 40, rng.Next(256), rng.Next(256), rng.Next(256)));
                g.FillEllipse(blob, rng.Next(sheet.Width), rng.Next(sheet.Height), 40 + rng.Next(160), 30 + rng.Next(120));
            }
            using var font = new Font("Segoe UI", 10f, FontStyle.Bold, GraphicsUnit.Point);
            using var ink = new SolidBrush(bright ? Color.FromArgb(0x10, 0x18, 0x28) : Color.FromArgb(0xE8, 0xEC, 0xF4));
            for (var i = 0; i < shots.Count; i++)
            {
                var x = i % columns * cellW;
                var y = i / columns * cellH;
                g.DrawString(shots[i].Label, font, ink, x + 14, y + 8);
                g.DrawImageUnscaled(shots[i].Image, x + 18, y + 34);
                shots[i].Image.Dispose();
            }
        }
        sheet.Save(path, ImageFormat.Png);
    }
}
