using Microsoft.Win32;
using Tokenify.Core;
using Tokenify.Overlay;

namespace Tokenify.Tracker;

/// <summary>
/// Keeps the overlay window showing what the tracker knows: decides whether it is visible, where it sits, redraws
/// it only when something visible changed, and runs the two short animations (the total counting up, and the
/// "+42 CHESTS" line) on a timer that stops as soon as they are over. Everything here runs on the UI thread.
/// </summary>
internal sealed class OverlayController : IDisposable
{
    private readonly Core.Tracker _tracker;
    private readonly OverlayWindow _window = new();
    private readonly System.Windows.Forms.Timer _animation = new() { Interval = 16 };
    private readonly System.Windows.Forms.Timer _upkeep = new() { Interval = 1000 };

    private TrackerSnapshot? _snapshot;
    private string _drawn = "";
    private Point _panelLocation;
    private bool _positioned, _fortniteInFront, _ownInFront, _dragging;
    private string? _positionedFor;
    private int? _scaleOverride;
    private int _upkeepTicks;

    private string? _toast;
    private DateTime _toastStart;
    private int? _total;
    private int _rollFrom, _rollTo;
    private DateTime _rollStart = DateTime.MinValue;

    private static readonly TimeSpan ToastIn = TimeSpan.FromMilliseconds(250), ToastHold = TimeSpan.FromSeconds(5.5), ToastOut = TimeSpan.FromMilliseconds(700);
    private static readonly TimeSpan Roll = TimeSpan.FromMilliseconds(900);

    /// <summary>The overlay's window (for the self-test).</summary>
    internal OverlayWindow Window => _window;


    /// <summary>A settings window is open: show the overlay even in "only while Fortnite is in front" mode.</summary>
    public bool Previewing { get; set; }

    public OverlayController(Core.Tracker tracker)
    {
        _tracker = tracker;
        _window.PanelDragging += p => { _dragging = true; _panelLocation = p; };
        _window.PanelDropped += OnDropped;
        _window.ScaleDragged += OnScaleDragged;
        _window.Constrain = Constrain;
        _window.DpiChanged += (_, _) => Refresh(force: true);
        _animation.Tick += (_, _) => Refresh();
        _upkeep.Tick += (_, _) => Upkeep();
        _upkeep.Start();
        SystemEvents.DisplaySettingsChanged += OnDisplayChanged;
    }

    private static List<MonitorInfo> Monitors() => Screen.AllScreens.Select(s => new MonitorInfo(s.DeviceName, s.Bounds, s.Primary)).ToList();

    private void OnDisplayChanged(object? sender, EventArgs e)
    {
        // A monitor was added, removed or changed size: work out the position again (and recover if it is now off-screen).
        _positioned = false;
        if (_window.IsHandleCreated) _window.BeginInvoke(() => Refresh(force: true));
    }

    /// <summary>New numbers or settings from the tracker.</summary>
    public void Apply(TrackerSnapshot snapshot)
    {
        var total = snapshot.Stats.TotalChests;
        if (_total is int before && before != total && snapshot.Settings.Overlay.Enabled)
        {
            _rollFrom = before;
            _rollTo = total;
            _rollStart = DateTime.UtcNow;
            _animation.Start();
        }
        _total = total;
        _snapshot = snapshot;
        Refresh();
    }

    /// <summary>Shows a short line such as "+42 CHESTS" for a few seconds.</summary>
    public void Toast(string text)
    {
        _toast = text;
        _toastStart = DateTime.UtcNow;
        _animation.Start();
        Refresh();
    }

    private float ToastAlpha()
    {
        if (_toast is null) return 0;
        var t = DateTime.UtcNow - _toastStart;
        if (t < ToastIn) return (float)(t / ToastIn);
        if (t < ToastIn + ToastHold) return 1;
        if (t < ToastIn + ToastHold + ToastOut) return 1 - (float)((t - ToastIn - ToastHold) / ToastOut);
        _toast = null;
        return 0;
    }

    private string? RollingTotal()
    {
        var t = DateTime.UtcNow - _rollStart;
        if (t >= Roll) return null;
        var p = t / Roll;
        var eased = 1 - Math.Pow(1 - p, 3);
        return Fmt.Int(_rollFrom + (int)Math.Round((_rollTo - _rollFrom) * eased));
    }

    public void Refresh(bool force = false)
    {
        if (_snapshot is not { } s) return;
        var o = s.Settings.Overlay;
        var editing = !o.Locked;
        var visible = o.Enabled && o.Visible && (o.ShowWhen == "always" || editing || Previewing || _fortniteInFront || _ownInFront);
        if (!visible)
        {
            if (_window.Visible) _window.Hide();
            _drawn = "";
            _animation.Stop();
            return;
        }

        var alpha = ToastAlpha();
        var hero = RollingTotal();
        if (_toast is null && hero is null) _animation.Stop();

        var scale = _scaleOverride ?? o.Scale;
        var dpi = _window.DeviceDpi / 96f;
        var content = Display.Build(s, o.Metrics(), _toast);
        var signature = string.Join("|", content.Signature, o.Preset, o.Style, o.BackgroundOpacity, o.OverallOpacity, scale, o.FontScale, o.Accent,
            o.CornerRadius, o.Shadow, o.Padding, o.Align, editing, dpi, (int)(alpha * 40), hero, o.Monitor,
            o.Places.TryGetValue(o.Monitor ?? "", out var place) ? $"{place.X:0.####},{place.Y:0.####}" : "");
        if (signature == _drawn && !force)
        {
            _window.ShowQuietly();
            return;
        }

        OverlaySettings drawSettings = o;
        if (_scaleOverride is int temp && temp != o.Scale)
        {
            drawSettings = s.Settings.Clone().Overlay;
            drawSettings.Scale = temp;
        }

        using var frame = OverlayRenderer.Render(content, drawSettings, dpi, new RenderState(editing, alpha, hero));
        var monitors = Monitors();
        var positionKey = $"{o.Monitor}|{(o.Places.TryGetValue(o.Monitor ?? "", out var p2) ? $"{p2.X:0.####},{p2.Y:0.####}" : "")}|{monitors.Count}";
        if (!_dragging && (!_positioned || positionKey != _positionedFor))
        {
            _panelLocation = Placement.Resolve(o, monitors, frame.Panel.Size);
            _positioned = true;
            _positionedFor = positionKey;
        }
        else if (!_dragging)
        {
            // The panel may have grown (more rows, bigger scale): keep all of it on its monitor.
            _panelLocation = Placement.EnsureVisible(_panelLocation, frame.Panel.Size, Placement.MonitorAt(_panelLocation, frame.Panel.Size, monitors).Bounds);
        }

        _window.Locked = o.Locked;
        _window.ScalePercent = scale;
        _window.Present(frame, _panelLocation, o.OverallOpacity);
        _window.ShowQuietly();
        _drawn = signature;
    }

    private Point Constrain(Point target, Size panel)
    {
        var monitors = Monitors();
        var monitor = Placement.MonitorAt(target, panel, monitors);
        if (_snapshot?.Settings.Overlay.SnapToEdges == true) target = Placement.Snap(target, panel, monitor.Bounds);
        return Placement.EnsureVisible(target, panel, monitor.Bounds);
    }

    private void OnDropped(Point panelTopLeft)
    {
        _dragging = false;
        _panelLocation = panelTopLeft;
        var size = _window.PanelSize;
        var monitors = Monitors();
        _tracker.UpdateSettings(s => Placement.Remember(s.Overlay, panelTopLeft, size, monitors));
        // Remember() and Resolve() agree, so the saved position is where it already is.
        if (_snapshot?.Settings.Overlay is { } o)
        {
            _positionedFor = $"{o.Monitor}|{(o.Places.TryGetValue(o.Monitor ?? "", out var p) ? $"{p.X:0.####},{p.Y:0.####}" : "")}|{monitors.Count}";
        }
    }

    private void OnScaleDragged(int scale, bool released)
    {
        if (!released)
        {
            _scaleOverride = scale;
            Refresh();
            return;
        }
        _scaleOverride = null;
        _tracker.UpdateSettings(s => s.Overlay.Scale = scale);
    }

    /// <summary>Moves the overlay to a corner or the centre of its monitor (Settings → Position).</summary>
    public void MoveTo(string where)
    {
        if (_snapshot is not { } s) return;
        var monitors = Monitors();
        var monitor = Placement.Target(s.Settings.Overlay, monitors);
        var size = _window.PanelSize.IsEmpty ? new Size(220, 220) : _window.PanelSize;
        var point = Placement.Corner(where, size, monitor.Bounds);
        _tracker.UpdateSettings(x => Placement.Remember(x.Overlay, point, size, monitors));
    }

    /// <summary>Moves the overlay to another monitor, to where it last was there (or the default spot).</summary>
    public void MoveToMonitor(string deviceName)
    {
        _tracker.UpdateSettings(s => s.Overlay.Monitor = deviceName);
    }

    public static IReadOnlyList<(string Device, string Label)> MonitorChoices()
    {
        var screens = Screen.AllScreens;
        return screens.Select((s, i) => (s.DeviceName, $"Display {i + 1}{(s.Primary ? " (main)" : "")}  ·  {s.Bounds.Width} × {s.Bounds.Height}")).ToList();
    }

    private void Upkeep()
    {
        if (_snapshot is not { } s) return;
        var o = s.Settings.Overlay;
        if (!o.Enabled || !o.Visible)
        {
            return;
        }
        if (o.ShowWhen == "fortnite")
        {
            var (title, cls, own) = ForegroundWatch.InFront();
            var fortnite = ForegroundWatch.IsFortnite(title, cls);
            if (fortnite != _fortniteInFront || own != _ownInFront)
            {
                _fortniteInFront = fortnite;
                _ownInFront = own;
                Refresh();
            }
        }
        // Another always-on-top window may have been placed above: go back on top, without taking focus.
        if (++_upkeepTicks % 2 == 0) _window.KeepOnTop();
    }

    public void Dispose()
    {
        SystemEvents.DisplaySettingsChanged -= OnDisplayChanged;
        _animation.Dispose();
        _upkeep.Dispose();
        _window.Dispose();
    }
}
