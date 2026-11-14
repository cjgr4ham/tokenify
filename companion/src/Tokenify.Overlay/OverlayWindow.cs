using System.Runtime.InteropServices;
using Tokenify.Core;

namespace Tokenify.Overlay;

/// <summary>
/// The overlay's window: an ordinary top-level Windows window of this program, with per-pixel transparency
/// (a "layered" window), kept above other windows, never taking keyboard focus.
/// <list type="bullet">
/// <item>Locked: it also carries WS_EX_TRANSPARENT, so every click and key press goes straight through to
/// whatever is underneath (the game).</item>
/// <item>Unlocked: it takes mouse input so it can be dragged and resized, but still never activates.</item>
/// </list>
/// It is drawn by Windows' desktop compositor on top of other windows, which is why it shows over a game in
/// Windowed Fullscreen and cannot show over exclusive fullscreen. Nothing here touches another program.
/// </summary>
public sealed class OverlayWindow : Form
{
    private const int WsPopup = unchecked((int)0x80000000);
    private const int WsExLayered = 0x00080000, WsExTransparent = 0x00000020, WsExTopmost = 0x00000008,
        WsExToolWindow = 0x00000080, WsExNoActivate = 0x08000000;
    private const int GwlExStyle = -20;
    private const int WmMouseActivate = 0x0021, MaNoActivate = 3;
    private const int UlwAlpha = 2;
    private const byte AcSrcOver = 0, AcSrcAlpha = 1;
    private static readonly IntPtr HwndTopmost = new(-1);
    private const uint SwpNoSize = 0x0001, SwpNoMove = 0x0002, SwpNoActivate = 0x0010, SwpNoOwnerZOrder = 0x0200, SwpShowWindow = 0x0040;

    [StructLayout(LayoutKind.Sequential)] private struct NativePoint { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)] private struct NativeSize { public int Width, Height; }
    [StructLayout(LayoutKind.Sequential, Pack = 1)] private struct BlendFunction { public byte Op, Flags, SourceConstantAlpha, AlphaFormat; }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UpdateLayeredWindow(IntPtr hwnd, IntPtr hdcDst, ref NativePoint dst, ref NativeSize size, IntPtr hdcSrc, ref NativePoint src, int key, ref BlendFunction blend, int flags);
    [DllImport("user32.dll")] private static extern IntPtr GetDC(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern int ReleaseDC(IntPtr hwnd, IntPtr dc);
    [DllImport("gdi32.dll")] private static extern IntPtr CreateCompatibleDC(IntPtr dc);
    [DllImport("gdi32.dll")] private static extern bool DeleteDC(IntPtr dc);
    [DllImport("gdi32.dll")] private static extern IntPtr SelectObject(IntPtr dc, IntPtr obj);
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(IntPtr obj);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] private static extern IntPtr GetWindowLongPtr(IntPtr hwnd, int index);
    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")] private static extern IntPtr SetWindowLongPtr(IntPtr hwnd, int index, IntPtr value);
    [DllImport("user32.dll")] private static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int cx, int cy, uint flags);

    private bool _locked = true;
    private Rectangle _panel;   // inside the window
    private Point _panelOnScreen;
    private Rectangle _grip;
    private Size _bitmapSize;
    private enum Drag { None, Move, Resize }
    private Drag _drag;
    private Point _dragCursorStart, _dragPanelStart;
    private int _dragScaleStart, _dragWidthStart;
    private int _scale = 100;

    /// <summary>The panel is being dragged to this screen position (not yet released).</summary>
    public event Action<Point>? PanelDragging;
    /// <summary>The panel was dropped at this screen position.</summary>
    public event Action<Point>? PanelDropped;
    /// <summary>The corner grip is being dragged: the new scale in percent, and whether the mouse was released.</summary>
    public event Action<int, bool>? ScaleDragged;
    /// <summary>Adjusts a position while dragging (snapping); supplied by the owner.</summary>
    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public Func<Point, Size, Point>? Constrain { get; set; }

    public OverlayWindow()
    {
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.Manual;
        AutoScaleMode = AutoScaleMode.None;
        TopMost = true;
        Text = "TOKENIFY overlay";
        // The window is created now but stays hidden until the first picture is ready.
        _ = Handle;
    }

    protected override bool ShowWithoutActivation => true;

    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            cp.Style = WsPopup;
            cp.ExStyle |= WsExLayered | WsExTopmost | WsExToolWindow | WsExNoActivate;
            if (_locked) cp.ExStyle |= WsExTransparent;
            return cp;
        }
    }

    /// <summary>Locked = click-through. Changes the window style in place; the window is not recreated.</summary>
    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public bool Locked
    {
        get => _locked;
        set
        {
            if (_locked == value) return;
            _locked = value;
            _drag = Drag.None;
            Capture = false;
            if (!IsHandleCreated) return;
            var style = GetWindowLongPtr(Handle, GwlExStyle).ToInt64();
            style = value ? style | WsExTransparent : style & ~(long)WsExTransparent;
            SetWindowLongPtr(Handle, GwlExStyle, new IntPtr(style));
        }
    }

    /// <summary>Windows' error code when the last picture could not be shown (0 = it was).</summary>
    public int LastPresentError { get; private set; }

    /// <summary>The panel's size on screen (without the shadow around it).</summary>
    public Size PanelSize => _panel.Size;

    /// <summary>
    /// Where the panel's top-left corner is on screen. Kept here rather than read back from the window: a layered
    /// window moved together with its picture does not tell the form that it moved.
    /// </summary>
    public Point PanelLocation => _panelOnScreen;

    /// <summary>The overlay's scale setting, needed while the grip is dragged.</summary>
    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public int ScalePercent { get => _scale; set => _scale = value; }

    /// <summary>
    /// Shows a newly drawn overlay with its panel's top-left corner at <paramref name="panelTopLeft"/>. Size,
    /// position and picture change in one step. <paramref name="opacity"/> fades everything, numbers included.
    /// </summary>
    public void Present(OverlayFrame frame, Point panelTopLeft, int opacityPercent)
    {
        _panel = frame.Panel;
        _grip = frame.Grip;
        _bitmapSize = frame.Bitmap.Size;
        _panelOnScreen = panelTopLeft;
        var location = new Point(panelTopLeft.X - frame.Panel.Left, panelTopLeft.Y - frame.Panel.Top);

        var screen = GetDC(IntPtr.Zero);
        var memory = CreateCompatibleDC(screen);
        var bitmap = frame.Bitmap.GetHbitmap(Color.FromArgb(0));
        var previous = SelectObject(memory, bitmap);
        try
        {
            var dst = new NativePoint { X = location.X, Y = location.Y };
            var size = new NativeSize { Width = frame.Bitmap.Width, Height = frame.Bitmap.Height };
            var src = new NativePoint();
            var blend = new BlendFunction { Op = AcSrcOver, SourceConstantAlpha = (byte)Math.Clamp(opacityPercent * 255 / 100, 0, 255), AlphaFormat = AcSrcAlpha };
            if (!UpdateLayeredWindow(Handle, screen, ref dst, ref size, memory, ref src, 0, ref blend, UlwAlpha))
            {
                LastPresentError = Marshal.GetLastWin32Error();
            }
            else
            {
                LastPresentError = 0;
            }
        }
        finally
        {
            SelectObject(memory, previous);
            DeleteObject(bitmap);
            DeleteDC(memory);
            ReleaseDC(IntPtr.Zero, screen);
        }
    }

    /// <summary>Makes the window visible without activating it (the game keeps the keyboard).</summary>
    public void ShowQuietly()
    {
        if (Visible) return;
        SetWindowPos(Handle, HwndTopmost, 0, 0, 0, 0, SwpNoMove | SwpNoSize | SwpNoActivate | SwpNoOwnerZOrder | SwpShowWindow);
        Visible = true;
    }

    /// <summary>Puts the window back above other topmost windows, without activating it.</summary>
    public void KeepOnTop()
    {
        if (Visible) SetWindowPos(Handle, HwndTopmost, 0, 0, 0, 0, SwpNoMove | SwpNoSize | SwpNoActivate | SwpNoOwnerZOrder);
    }

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == WmMouseActivate)
        {
            // Clicking the unlocked overlay must not take the keyboard away from the game.
            m.Result = new IntPtr(MaNoActivate);
            return;
        }
        base.WndProc(ref m);
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        if (e.Button != MouseButtons.Left) return;
        if (BeginDrag(Cursor.Position, _grip.Contains(e.Location))) Capture = true;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (_locked) return;
        if (_drag == Drag.None) Cursor = _grip.Contains(e.Location) ? Cursors.SizeNWSE : Cursors.SizeAll;
        else ContinueDrag(Cursor.Position);
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        if (e.Button != MouseButtons.Left) return;
        Capture = false;
        EndDrag();
    }

    /// <summary>The window's extended styles as Windows reports them (for the self-test).</summary>
    public (bool Layered, bool Topmost, bool NoActivate, bool ToolWindow, bool ClickThrough) Styles()
    {
        var style = GetWindowLongPtr(Handle, GwlExStyle).ToInt64();
        return ((style & WsExLayered) != 0, (style & WsExTopmost) != 0, (style & WsExNoActivate) != 0, (style & WsExToolWindow) != 0, (style & WsExTransparent) != 0);
    }

    /// <summary>Starts moving (or, from the corner grip, resizing). Does nothing while locked.</summary>
    public bool BeginDrag(Point cursor, bool onGrip)
    {
        if (_locked) return false;
        _drag = onGrip ? Drag.Resize : Drag.Move;
        _dragCursorStart = cursor;
        _dragPanelStart = PanelLocation;
        _dragScaleStart = _scale;
        _dragWidthStart = Math.Max(1, _panel.Width);
        return true;
    }

    public void ContinueDrag(Point cursor)
    {
        switch (_drag)
        {
            case Drag.Move:
            {
                var target = new Point(_dragPanelStart.X + cursor.X - _dragCursorStart.X, _dragPanelStart.Y + cursor.Y - _dragCursorStart.Y);
                if (Constrain is not null) target = Constrain(target, _panel.Size);
                // Move at once, with the same picture; the owner redraws only if it needs to.
                SetWindowPos(Handle, HwndTopmost, target.X - _panel.Left, target.Y - _panel.Top, 0, 0, SwpNoSize | SwpNoActivate | SwpNoOwnerZOrder);
                _panelOnScreen = target;
                PanelDragging?.Invoke(target);
                break;
            }
            case Drag.Resize:
            {
                var grown = _dragWidthStart + (cursor.X - _dragCursorStart.X + cursor.Y - _dragCursorStart.Y) / 2.0;
                var scale = (int)Math.Round(Math.Clamp(_dragScaleStart * grown / _dragWidthStart, 50, 200));
                if (scale != _scale)
                {
                    _scale = scale;
                    ScaleDragged?.Invoke(scale, false);
                }
                break;
            }
        }
    }

    public void EndDrag()
    {
        if (_drag == Drag.None) return;
        var was = _drag;
        _drag = Drag.None;
        if (was == Drag.Move) PanelDropped?.Invoke(PanelLocation);
        else ScaleDragged?.Invoke(_scale, true);
    }

    protected override void OnPaintBackground(PaintEventArgs e)
    {
        // a layered window is never painted the ordinary way
    }
}

/// <summary>
/// Shortcuts that work while another program (the game) has the keyboard, using Windows' own registered
/// hot-key feature. Windows itself delivers the key press to this program; nothing watches or intercepts the
/// keyboard. A registered combination is not passed on to the program in front.
/// </summary>
public sealed class GlobalHotkeys : NativeWindow, IDisposable
{
    private const int WmHotkey = 0x0312;
    private const uint ModAlt = 1, ModControl = 2, ModShift = 4, ModWin = 8, ModNoRepeat = 0x4000;
    private static readonly IntPtr MessageOnly = new(-3);
    private readonly HashSet<int> _ids = new();

    [DllImport("user32.dll", SetLastError = true)] private static extern bool RegisterHotKey(IntPtr hwnd, int id, uint modifiers, uint key);
    [DllImport("user32.dll")] private static extern bool UnregisterHotKey(IntPtr hwnd, int id);

    public event Action<int>? Pressed;

    public GlobalHotkeys() => CreateHandle(new CreateParams { Parent = MessageOnly });

    /// <summary>False when Windows refuses, usually because another program already uses that combination.</summary>
    public bool Register(int id, Hotkey? key)
    {
        Unregister(id);
        if (key is null || key.VirtualKey == 0) return false;
        var mods = (key.Ctrl ? ModControl : 0) | (key.Alt ? ModAlt : 0) | (key.Shift ? ModShift : 0) | (key.Win ? ModWin : 0) | ModNoRepeat;
        if (!RegisterHotKey(Handle, id, mods, (uint)key.VirtualKey)) return false;
        _ids.Add(id);
        return true;
    }

    public void Unregister(int id)
    {
        if (_ids.Remove(id)) UnregisterHotKey(Handle, id);
    }

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == WmHotkey)
        {
            try { Pressed?.Invoke(m.WParam.ToInt32()); } catch (Exception ex) { Log.Error("Shortcut handler failed", ex); }
            return;
        }
        base.WndProc(ref m);
    }

    public void Dispose()
    {
        foreach (var id in _ids.ToArray()) Unregister(id);
        DestroyHandle();
    }
}

/// <summary>
/// "Only while Fortnite is in front": asks Windows for the title and class of whichever window is in front.
/// It reads nothing from the game itself and opens nothing of it.
/// </summary>
public static class ForegroundWatch
{
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowTextW(IntPtr hwnd, char[] text, int max);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassNameW(IntPtr hwnd, char[] name, int max);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint processId);

    public static (string Title, string Class, bool Own) InFront()
    {
        var hwnd = GetForegroundWindow();
        if (hwnd == IntPtr.Zero) return ("", "", false);
        var buffer = new char[256];
        var title = new string(buffer, 0, Math.Max(0, GetWindowTextW(hwnd, buffer, buffer.Length)));
        var cls = new string(buffer, 0, Math.Max(0, GetClassNameW(hwnd, buffer, buffer.Length)));
        GetWindowThreadProcessId(hwnd, out var pid);
        return (title, cls, pid == (uint)Environment.ProcessId);
    }

    public static bool IsFortnite(string title, string windowClass) =>
        windowClass == "UnrealWindow" && title.TrimStart().StartsWith("Fortnite", StringComparison.OrdinalIgnoreCase);
}
