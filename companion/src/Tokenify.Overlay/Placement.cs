using Tokenify.Core;

namespace Tokenify.Overlay;

/// <summary>One connected display: its Windows device name and its whole area in screen pixels.</summary>
public readonly record struct MonitorInfo(string Name, Rectangle Bounds, bool Primary);

/// <summary>
/// Where the overlay goes. Pure arithmetic on rectangles, so every case is tested without a screen: several
/// monitors, different resolutions, a monitor that has been unplugged, a saved position that is now off-screen.
///
/// The position is remembered per monitor as a fraction of that monitor's area (so it survives a resolution or
/// scaling change), and always refers to the top-left corner of the visible panel.
/// </summary>
public static class Placement
{
    public const int SnapDistance = 14;

    public static MonitorInfo Target(OverlaySettings o, IReadOnlyList<MonitorInfo> monitors)
    {
        if (monitors.Count == 0) return new MonitorInfo("", new Rectangle(0, 0, 1920, 1080), true);
        foreach (var m in monitors)
        {
            if (m.Name == o.Monitor) return m;
        }
        // The overlay's monitor is not connected: use the main one. The saved monitor is kept, so the overlay goes
        // back to it when it returns.
        foreach (var m in monitors)
        {
            if (m.Primary) return m;
        }
        return monitors[0];
    }

    /// <summary>The panel's top-left corner in screen pixels, always fully on a connected monitor.</summary>
    public static Point Resolve(OverlaySettings o, IReadOnlyList<MonitorInfo> monitors, Size panel)
    {
        var monitor = Target(o, monitors);
        var place = o.Places.TryGetValue(monitor.Name, out var saved) ? saved : new OverlayPlace();
        var point = new Point(
            monitor.Bounds.Left + (int)Math.Round(place.X * monitor.Bounds.Width),
            monitor.Bounds.Top + (int)Math.Round(place.Y * monitor.Bounds.Height));
        return EnsureVisible(point, panel, monitor.Bounds);
    }

    /// <summary>Moves a position back inside <paramref name="bounds"/> so the whole panel can be seen.</summary>
    public static Point EnsureVisible(Point point, Size panel, Rectangle bounds)
    {
        var maxX = Math.Max(bounds.Left, bounds.Right - panel.Width);
        var maxY = Math.Max(bounds.Top, bounds.Bottom - panel.Height);
        return new Point(Math.Clamp(point.X, bounds.Left, maxX), Math.Clamp(point.Y, bounds.Top, maxY));
    }

    /// <summary>The monitor a panel is on: the one holding its centre, or the nearest one.</summary>
    public static MonitorInfo MonitorAt(Point panelTopLeft, Size panel, IReadOnlyList<MonitorInfo> monitors)
    {
        var centre = new Point(panelTopLeft.X + panel.Width / 2, panelTopLeft.Y + panel.Height / 2);
        MonitorInfo? best = null;
        long bestDistance = long.MaxValue;
        foreach (var m in monitors)
        {
            if (m.Bounds.Contains(centre)) return m;
            long dx = Math.Max(Math.Max(m.Bounds.Left - centre.X, 0), centre.X - m.Bounds.Right);
            long dy = Math.Max(Math.Max(m.Bounds.Top - centre.Y, 0), centre.Y - m.Bounds.Bottom);
            var d = dx * dx + dy * dy;
            if (d < bestDistance) { bestDistance = d; best = m; }
        }
        return best ?? new MonitorInfo("", new Rectangle(0, 0, 1920, 1080), true);
    }

    /// <summary>Remembers a position the player dragged the overlay to: which monitor, and where on it.</summary>
    public static void Remember(OverlaySettings o, Point panelTopLeft, Size panel, IReadOnlyList<MonitorInfo> monitors)
    {
        var monitor = MonitorAt(panelTopLeft, panel, monitors);
        var visible = EnsureVisible(panelTopLeft, panel, monitor.Bounds);
        o.Monitor = monitor.Name;
        o.Places[monitor.Name] = new OverlayPlace
        {
            X = monitor.Bounds.Width > 0 ? (double)(visible.X - monitor.Bounds.Left) / monitor.Bounds.Width : 0,
            Y = monitor.Bounds.Height > 0 ? (double)(visible.Y - monitor.Bounds.Top) / monitor.Bounds.Height : 0,
        };
    }

    /// <summary>While dragging: sticks to the monitor's edges and centre lines when close to them.</summary>
    public static Point Snap(Point point, Size panel, Rectangle bounds, int distance = SnapDistance)
    {
        // The nearest line within reach wins: an edge and its margin line are only 12 px apart.
        int Near(int value, params int[] targets)
        {
            var best = value;
            var bestDistance = distance + 1;
            foreach (var t in targets)
            {
                var d = Math.Abs(value - t);
                if (d < bestDistance)
                {
                    bestDistance = d;
                    best = t;
                }
            }
            return best;
        }
        var margin = 12;
        var x = Near(point.X, bounds.Left, bounds.Left + margin, bounds.Right - panel.Width - margin, bounds.Right - panel.Width,
            bounds.Left + (bounds.Width - panel.Width) / 2);
        var y = Near(point.Y, bounds.Top, bounds.Top + margin, bounds.Bottom - panel.Height - margin, bounds.Bottom - panel.Height,
            bounds.Top + (bounds.Height - panel.Height) / 2);
        return new Point(x, y);
    }

    /// <summary>A preset corner or edge, for "Reset position" and the position buttons in Settings.</summary>
    public static Point Corner(string where, Size panel, Rectangle bounds, int margin = 24)
    {
        var x = where.Contains("right", StringComparison.Ordinal) ? bounds.Right - panel.Width - margin
            : where.Contains("left", StringComparison.Ordinal) ? bounds.Left + margin
            : bounds.Left + (bounds.Width - panel.Width) / 2;
        var y = where.Contains("bottom", StringComparison.Ordinal) ? bounds.Bottom - panel.Height - margin
            : where.Contains("top", StringComparison.Ordinal) ? bounds.Top + margin
            : bounds.Top + (bounds.Height - panel.Height) / 2;
        return EnsureVisible(new Point(x, y), panel, bounds);
    }
}
