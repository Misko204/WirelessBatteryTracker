using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.Runtime.InteropServices;

namespace BatteryTray;

public sealed record StripItem(string Id, DeviceKind Kind, int? Level, bool Connected, bool Charging, int LowThreshold, bool IsAppItem = false);

/// <summary>
/// A row of clickable items embedded in the Windows taskbar, just left of the notification area:
/// [device symbol  percentage] per device, each a single hover/click target like the language or
/// volume indicators. It is a per-pixel-alpha child window of the taskbar, so it moves with it
/// (including auto-hide).
///
/// It runs on its own UI thread: a child window of Explorer shares its input queue with our thread,
/// so this thread must never block — the rest of the app can't stall the taskbar.
/// </summary>
public sealed class TaskbarStrip : IDisposable
{
    readonly SynchronizationContext _ui;
    readonly Thread _thread;
    SynchronizationContext? _stripCtx;
    readonly ManualResetEventSlim _ready = new();
    StripWindow? _win;

    StripItem[] _items = Array.Empty<StripItem>();
    bool _showPercent = true;

    /// <summary>Raised on the main UI thread with the clicked item's screen rectangle.</summary>
    public event Action<string, Rectangle>? ItemClicked;
    public event Action<string, Point>? ItemRightClicked;
    /// <summary>Raised on the main UI thread when the strip gets embedded or loses its place in the taskbar.</summary>
    public event Action<bool>? EmbeddedChanged;

    public bool IsEmbedded { get; private set; }

    public TaskbarStrip()
    {
        _ui = SynchronizationContext.Current as WindowsFormsSynchronizationContext
              ?? throw new InvalidOperationException("TaskbarStrip must be created on the UI thread.");
        _thread = new Thread(ThreadMain) { IsBackground = true, Name = "Taskbar strip" };
        _thread.SetApartmentState(ApartmentState.STA);
        _thread.Start();
        _ready.Wait(3000);
    }

    void ThreadMain()
    {
        SynchronizationContext.SetSynchronizationContext(new WindowsFormsSynchronizationContext());
        _stripCtx = SynchronizationContext.Current;
        var timer = new System.Windows.Forms.Timer { Interval = 500 };
        timer.Tick += (_, _) => Tick();
        timer.Start();
        _ready.Set();
        Application.Run();
        timer.Dispose();
        _win?.DestroyHandle();
    }

    /// <summary>Thread-safe: replaces the items shown. An empty list hides the strip.</summary>
    public void SetItems(IReadOnlyList<StripItem> items, bool showPercent)
    {
        var copy = items.ToArray();
        _stripCtx?.Post(_ =>
        {
            _items = copy;
            _showPercent = showPercent;
            Tick(force: true);
        }, null);
    }

    int _themeCheck;
    bool _lastLight;

    void Tick(bool force = false)
    {
        try
        {
            if (_items.Length == 0)
            {
                if (_win?.Handle is IntPtr h && h != IntPtr.Zero) Win32.ShowWindow(h, 0);
                return;
            }

            IntPtr tray = Win32.FindWindowW("Shell_TrayWnd", null);
            if (tray == IntPtr.Zero) { SetEmbedded(false); return; }

            if (_win == null || _win.Parent != tray || !Win32.IsWindow(_win.Handle))
            {
                _win?.DestroyHandle();
                _win = new StripWindow(this, tray);
                if (_win.Handle == IntPtr.Zero) { _win = null; SetEmbedded(false); return; }
                force = true;
            }

            if (++_themeCheck % 4 == 0)
            {
                bool light = IconRenderer.TaskbarIsLight();
                if (light != _lastLight) { _lastLight = light; force = true; }
            }

            _win.Layout(_items, _showPercent, force);
            SetEmbedded(true);
        }
        catch (Exception ex)
        {
            Log.Write("Taskbar strip: " + ex.Message);
            SetEmbedded(false);
        }
    }

    void SetEmbedded(bool value)
    {
        if (IsEmbedded == value) return;
        IsEmbedded = value;
        _ui.Post(_ => EmbeddedChanged?.Invoke(value), null);
    }

    internal void RaiseClick(string id, Rectangle screen) => _ui.Post(_ => ItemClicked?.Invoke(id, screen), null);
    internal void RaiseRightClick(string id, Point screen) => _ui.Post(_ => ItemRightClicked?.Invoke(id, screen), null);

    public void Dispose()
    {
        _stripCtx?.Post(_ => { _win?.DestroyHandle(); _win = null; Application.ExitThread(); }, null);
        _thread.Join(2000);
    }

    // ------------------------------------------------------------------

    /// <summary>Renders the strip as it would appear on a taskbar of the given colour (for diagnostics).</summary>
    public static Bitmap Preview(StripItem[] items, bool showPercent, float scale, int barH, bool light, int hover = -1)
    {
        var (rects, size) = StripWindow.Measure(items, showPercent, scale, barH);
        using var strip = StripWindow.Draw(items, rects, size, showPercent, scale, light, hover, -1);
        var bmp = new Bitmap(size.Width + (int)(40 * scale), barH);
        using var g = Graphics.FromImage(bmp);
        g.Clear(light ? Color.FromArgb(0xEE, 0xEE, 0xEE) : Color.FromArgb(0x1C, 0x1C, 0x1C));
        g.DrawImage(strip, (int)(20 * scale), (barH - size.Height) / 2);
        return bmp;
    }

    sealed class StripWindow : NativeWindow
    {
        const int WS_CHILD = 0x40000000, WS_CLIPSIBLINGS = 0x04000000;
        const int WS_EX_LAYERED = 0x80000, WS_EX_TOOLWINDOW = 0x80, WS_EX_NOACTIVATE = 0x08000000;
        const int WM_MOUSEMOVE = 0x200, WM_LBUTTONDOWN = 0x201, WM_LBUTTONUP = 0x202, WM_RBUTTONUP = 0x205;
        const int WM_MOUSELEAVE = 0x2A3, WM_MOUSEACTIVATE = 0x21, WM_NCHITTEST = 0x84, WM_SETCURSOR = 0x20;

        readonly TaskbarStrip _owner;
        public IntPtr Parent { get; }
        StripItem[] _items = Array.Empty<StripItem>();
        Rectangle[] _rects = Array.Empty<Rectangle>();
        int _hover = -1, _pressed = -1;
        bool _tracking, _showPercent;
        Rectangle _bounds;
        string _renderKey = "";

        public StripWindow(TaskbarStrip owner, IntPtr parent)
        {
            _owner = owner;
            Parent = parent;
            CreateHandle(new CreateParams
            {
                Caption = "WirelessBatteryTracker.Strip",
                Parent = parent,
                Style = WS_CHILD | WS_CLIPSIBLINGS,
                ExStyle = WS_EX_LAYERED | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE,
                X = 0, Y = 0, Width = 1, Height = 1,
            });
        }

        float Scale => Win32.GetDpiForWindow(Parent) is var d and > 0 ? d / 96f : 1f;

        public void Layout(StripItem[] items, bool showPercent, bool force)
        {
            _items = items;
            _showPercent = showPercent;
            float s = Scale;

            Win32.GetClientRect(Parent, out var pc);
            int barH = pc.Bottom - pc.Top;
            var (rects, size) = Measure(items, showPercent, s, barH);
            int totalW = size.Width, itemH = size.Height;

            // Place just left of the notification area (TrayNotifyWnd tracks its width, chevron included).
            IntPtr notify = Win32.FindWindowExW(Parent, IntPtr.Zero, "TrayNotifyWnd", null);
            int right;
            if (notify != IntPtr.Zero)
            {
                Win32.GetWindowRect(notify, out var nr);
                var pt = new Win32.POINT { X = nr.Left, Y = nr.Top };
                Win32.ScreenToClient(Parent, ref pt);
                right = pt.X - (int)(4 * s);
            }
            else right = pc.Right - (int)(300 * s);

            var bounds = new Rectangle(right - totalW, (barH - itemH) / 2, totalW, itemH);
            _rects = rects;

            if (bounds != _bounds || !Win32.IsWindowVisible(Handle))
            {
                _bounds = bounds;
                Win32.SetWindowPos(Handle, IntPtr.Zero, bounds.X, bounds.Y, bounds.Width, bounds.Height,
                    0x0010 /*NOACTIVATE*/ | 0x0040 /*SHOWWINDOW*/);
                force = true;
            }
            Render(force);
        }

        void Render(bool force)
        {
            if (_bounds.Width <= 0 || _bounds.Height <= 0) return;
            bool light = IconRenderer.TaskbarIsLight();
            string key = string.Join("|", _items.Select(i => i.ToString())) + $"{_hover}{_pressed}{light}{_bounds}{_showPercent}";
            if (!force && key == _renderKey) return;
            _renderKey = key;
            using var bmp = Draw(_items, _rects, _bounds.Size, _showPercent, Scale, light, _hover, _pressed);
            Push(bmp);
        }

        public static string Text(StripItem it, bool showPercent) => it.Level is int l ? (showPercent ? $"{l}%" : l.ToString()) : "–";

        /// <summary>Item rectangles (strip-relative) and total strip size.</summary>
        public static (Rectangle[] rects, Size size) Measure(StripItem[] items, bool showPercent, float s, int barH)
        {
            int itemH = (int)Math.Min(barH - 8 * s, 40 * s);
            using var measureBmp = new Bitmap(1, 1);
            using var mg = Graphics.FromImage(measureBmp);
            using var textFont = new Font(IconRenderer.TextFont, 12f * s, GraphicsUnit.Pixel);
            int pad = (int)(8 * s), gap = (int)(6 * s), glyph = (int)(16 * s), between = (int)(2 * s);
            var rects = new Rectangle[items.Length];
            int x = 0;
            for (int i = 0; i < items.Length; i++)
            {
                var it = items[i];
                int w = it.IsAppItem ? pad * 2 + glyph
                    : pad * 2 + glyph + gap + Math.Max((int)Math.Ceiling(mg.MeasureString(Text(it, showPercent), textFont, PointF.Empty, StringFormat.GenericTypographic).Width), (int)(18 * s));
                rects[i] = new Rectangle(x, 0, w, itemH);
                x += w + between;
            }
            return (rects, new Size(Math.Max(1, x - between), itemH));
        }

        public static Bitmap Draw(StripItem[] items, Rectangle[] rects, Size size, bool showPercent, float s, bool light, int hover, int pressed)
        {
            var bmp = new Bitmap(size.Width, size.Height, PixelFormat.Format32bppArgb);
            using (var g = Graphics.FromImage(bmp))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
                // Alpha 1 everywhere: invisible, but keeps the whole strip clickable.
                g.Clear(Color.FromArgb(1, 0, 0, 0));

                using var glyphFont = new Font(IconRenderer.IconFont, 16f * s, GraphicsUnit.Pixel);
                using var textFont = new Font(IconRenderer.TextFont, 12f * s, GraphicsUnit.Pixel);
                using var boltFont = new Font(IconRenderer.IconFont, 9f * s, GraphicsUnit.Pixel);
                var fg = light ? Color.FromArgb(0x1A, 0x1A, 0x1A) : Color.White;

                for (int i = 0; i < items.Length; i++)
                {
                    var it = items[i];
                    var r = rects[i];
                    if (i == hover || i == pressed)
                    {
                        int a = i == pressed ? (light ? 0x0A : 0x0F) : (light ? 0x0F : 0x15);
                        using var hb = new SolidBrush(Color.FromArgb(a, light ? 0 : 255, light ? 0 : 255, light ? 0 : 255));
                        using var path = RoundRect(r, 4 * s);
                        g.FillPath(hb, path);
                    }

                    var color = it.Connected ? fg : Color.FromArgb(light ? 0x8A : 0x80, 0x80, 0x80, 0x80);
                    int pad = (int)(8 * s), glyph = (int)(16 * s), gap = (int)(6 * s);
                    var glyphRect = new RectangleF(r.X + pad, r.Y + (r.Height - glyph) / 2f, glyph, glyph);

                    if (it.IsAppItem)
                    {
                        using var logo = LogoRenderer.RenderTile(glyph);
                        g.DrawImage(logo, glyphRect);
                        continue;
                    }

                    IconRenderer.DrawCentered(g, IconRenderer.Glyph(it.Kind), glyphFont, color, glyphRect);

                    if (it.Charging && it.Connected)
                    {
                        // small lightning bolt badge at the glyph's lower right
                        var br = new RectangleF(glyphRect.Right - 7 * s, glyphRect.Bottom - 7 * s, 9 * s, 9 * s);
                        IconRenderer.DrawCentered(g, "\uE945", boltFont, IconRenderer.ChargingColor(light), br);
                    }

                    var textColor = IconRenderer.LevelTextColor(it.Level, it.Connected, it.LowThreshold, light);
                    var textRect = new RectangleF(glyphRect.Right + gap, r.Y, r.Right - pad - (glyphRect.Right + gap), r.Height);
                    // Vertically centre on the digits' ink like the taskbar clock.
                    IconRenderer.DrawCenteredVertically(g, Text(it, showPercent), textFont, textColor, textRect);
                }
            }
            return bmp;
        }

        void Push(Bitmap bmp)
        {
            IntPtr screenDc = Win32.GetDC(IntPtr.Zero);
            IntPtr memDc = Win32.CreateCompatibleDC(screenDc);
            IntPtr hBmp = bmp.GetHbitmap(Color.FromArgb(0));
            IntPtr old = Win32.SelectObject(memDc, hBmp);
            try
            {
                var size = new Win32.SIZE { cx = bmp.Width, cy = bmp.Height };
                var src = new Win32.POINT();
                var blend = new Win32.BLENDFUNCTION { BlendOp = 0, SourceConstantAlpha = 255, AlphaFormat = 1 };
                if (!Win32.UpdateLayeredWindow(Handle, screenDc, IntPtr.Zero, ref size, memDc, ref src, 0, ref blend, 2))
                    Log.Write("UpdateLayeredWindow failed: " + Marshal.GetLastWin32Error());
            }
            finally
            {
                Win32.SelectObject(memDc, old);
                Win32.DeleteObject(hBmp);
                Win32.DeleteDC(memDc);
                Win32.ReleaseDC(IntPtr.Zero, screenDc);
            }
        }

        static GraphicsPath RoundRect(Rectangle r, float radius)
        {
            var p = new GraphicsPath();
            float d = radius * 2;
            p.AddArc(r.X, r.Y, d, d, 180, 90);
            p.AddArc(r.Right - d - 1, r.Y, d, d, 270, 90);
            p.AddArc(r.Right - d - 1, r.Bottom - d - 1, d, d, 0, 90);
            p.AddArc(r.X, r.Bottom - d - 1, d, d, 90, 90);
            p.CloseFigure();
            return p;
        }

        int HitTest(IntPtr lParam)
        {
            int x = (short)((long)lParam & 0xFFFF), y = (short)(((long)lParam >> 16) & 0xFFFF);
            for (int i = 0; i < _rects.Length; i++) if (_rects[i].Contains(x, y)) return i;
            return -1;
        }

        Rectangle ScreenRect(int i)
        {
            Win32.GetWindowRect(Handle, out var wr);
            var r = _rects[i];
            return new Rectangle(wr.Left + r.X, wr.Top + r.Y, r.Width, r.Height);
        }

        protected override void WndProc(ref Message m)
        {
            switch (m.Msg)
            {
                case WM_MOUSEACTIVATE:
                    m.Result = (IntPtr)3; // MA_NOACTIVATE
                    return;
                case WM_NCHITTEST:
                    m.Result = (IntPtr)1; // HTCLIENT
                    return;
                case WM_SETCURSOR:
                    Win32.SetCursor(Win32.LoadCursorW(IntPtr.Zero, 32512)); // arrow
                    m.Result = (IntPtr)1;
                    return;
                case WM_MOUSEMOVE:
                {
                    if (!_tracking)
                    {
                        var tme = new Win32.TRACKMOUSEEVENT { cbSize = Marshal.SizeOf<Win32.TRACKMOUSEEVENT>(), dwFlags = 2 /*LEAVE*/, hwndTrack = Handle };
                        _tracking = Win32.TrackMouseEvent(ref tme);
                    }
                    int h = HitTest(m.LParam);
                    if (h != _hover) { _hover = h; Render(false); }
                    return;
                }
                case WM_MOUSELEAVE:
                    _tracking = false;
                    _hover = -1; _pressed = -1;
                    Render(false);
                    return;
                case WM_LBUTTONDOWN:
                    _pressed = HitTest(m.LParam);
                    Render(false);
                    return;
                case WM_LBUTTONUP:
                {
                    int h = HitTest(m.LParam);
                    if (h >= 0 && h == _pressed) _owner.RaiseClick(_items[h].Id, ScreenRect(h));
                    _pressed = -1;
                    Render(false);
                    return;
                }
                case WM_RBUTTONUP:
                {
                    int h = HitTest(m.LParam);
                    Win32.GetCursorPos(out var p);
                    _owner.RaiseRightClick(h >= 0 ? _items[h].Id : "", new Point(p.X, p.Y));
                    return;
                }
            }
            base.WndProc(ref m);
        }
    }
}

internal static class Win32
{
    [StructLayout(LayoutKind.Sequential)] public struct RECT { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] public struct POINT { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)] public struct SIZE { public int cx, cy; }
    [StructLayout(LayoutKind.Sequential, Pack = 1)] public struct BLENDFUNCTION { public byte BlendOp, BlendFlags, SourceConstantAlpha, AlphaFormat; }
    [StructLayout(LayoutKind.Sequential)] public struct TRACKMOUSEEVENT { public int cbSize; public uint dwFlags; public IntPtr hwndTrack; public uint dwHoverTime; }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern IntPtr FindWindowW(string cls, string? name);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern IntPtr FindWindowExW(IntPtr parent, IntPtr after, string cls, string? name);
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
    [DllImport("user32.dll")] public static extern bool GetClientRect(IntPtr h, out RECT r);
    [DllImport("user32.dll")] public static extern bool ScreenToClient(IntPtr h, ref POINT p);
    [DllImport("user32.dll")] public static extern bool IsWindow(IntPtr h);
    [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr h);
    [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr h, int cmd);
    [DllImport("user32.dll")] public static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int cx, int cy, uint flags);
    [DllImport("user32.dll")] public static extern uint GetDpiForWindow(IntPtr h);
    [DllImport("user32.dll")] public static extern bool GetCursorPos(out POINT p);
    [DllImport("user32.dll")] public static extern IntPtr SetCursor(IntPtr c);
    [DllImport("user32.dll")] public static extern IntPtr LoadCursorW(IntPtr inst, int id);
    [DllImport("user32.dll")] public static extern bool TrackMouseEvent(ref TRACKMOUSEEVENT e);
    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool UpdateLayeredWindow(IntPtr h, IntPtr dst, IntPtr pptDst, ref SIZE size, IntPtr src, ref POINT pptSrc, int key, ref BLENDFUNCTION blend, int flags);
    [DllImport("user32.dll")] public static extern IntPtr GetDC(IntPtr h);
    [DllImport("user32.dll")] public static extern int ReleaseDC(IntPtr h, IntPtr dc);
    [DllImport("gdi32.dll")] public static extern IntPtr CreateCompatibleDC(IntPtr dc);
    [DllImport("gdi32.dll")] public static extern bool DeleteDC(IntPtr dc);
    [DllImport("gdi32.dll")] public static extern IntPtr SelectObject(IntPtr dc, IntPtr obj);
    [DllImport("gdi32.dll")] public static extern bool DeleteObject(IntPtr obj);
    [DllImport("dwmapi.dll")] public static extern int DwmSetWindowAttribute(IntPtr h, int attr, ref int value, int size);
    [StructLayout(LayoutKind.Sequential)] public struct MARGINS { public int Left, Right, Top, Bottom; }
    [DllImport("dwmapi.dll")] public static extern int DwmExtendFrameIntoClientArea(IntPtr h, ref MARGINS m);
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);

    public static Rectangle TaskbarRect()
    {
        var tray = FindWindowW("Shell_TrayWnd", null);
        if (tray == IntPtr.Zero || !GetWindowRect(tray, out var r)) return Rectangle.Empty;
        return Rectangle.FromLTRB(r.Left, r.Top, r.Right, r.Bottom);
    }
}
