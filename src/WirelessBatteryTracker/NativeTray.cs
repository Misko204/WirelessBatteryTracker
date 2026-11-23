using System.Runtime.InteropServices;

namespace BatteryTray;

/// <summary>
/// Notification-area icons through Shell_NotifyIcon directly, so every icon gets a fixed ID.
/// Windows remembers position and "show on taskbar" per ID, so stable IDs keep each device's
/// icon pair in place across restarts.
/// </summary>
public sealed class TrayHost : NativeWindow, IDisposable
{
    const int WM_TRAY = 0x8000 + 1;
    const int WM_LBUTTONUP = 0x202, WM_LBUTTONDBLCLK = 0x203, WM_RBUTTONUP = 0x205, WM_CONTEXTMENU = 0x7B;
    const int NIN_SELECT = 0x400, NIN_KEYSELECT = 0x401;
    static readonly int WM_TASKBARCREATED = RegisterWindowMessageW("TaskbarCreated");

    readonly Dictionary<uint, TrayIcon> _icons = new();
    readonly System.Windows.Forms.Timer _clickTimer = new();
    uint _pendingClickId;

    public event Action<uint>? LeftClick;
    public event Action<uint>? RightClick;
    public event Action<uint>? DoubleClick;
    /// <summary>A device was plugged in or removed (WM_DEVICECHANGE).</summary>
    public event Action? DeviceChanged;

    public TrayHost()
    {
        CreateHandle(new CreateParams { Caption = "WirelessBatteryTracker.Tray" });
        _clickTimer.Interval = SystemInformation.DoubleClickTime;
        _clickTimer.Tick += (_, _) => { _clickTimer.Stop(); LeftClick?.Invoke(_pendingClickId); };
    }

    public TrayIcon Get(uint id)
    {
        if (!_icons.TryGetValue(id, out var icon)) _icons[id] = icon = new TrayIcon(this, id);
        return icon;
    }

    public IEnumerable<TrayIcon> All => _icons.Values;

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == WM_TRAY)
        {
            int ev = (int)((long)m.LParam & 0xFFFF);
            uint id = (uint)(((long)m.LParam >> 16) & 0xFFFF);
            switch (ev)
            {
                case WM_LBUTTONUP:
                case NIN_KEYSELECT:
                    _pendingClickId = id; _clickTimer.Stop(); _clickTimer.Start();
                    break;
                case WM_LBUTTONDBLCLK:
                    _clickTimer.Stop(); DoubleClick?.Invoke(id);
                    break;
                case WM_CONTEXTMENU:
                    RightClick?.Invoke(id);
                    break;
            }
            return;
        }
        if (m.Msg == 0x0219) // WM_DEVICECHANGE
        {
            DeviceChanged?.Invoke();
        }
        if (m.Msg == WM_TASKBARCREATED)
        {
            // Explorer restarted: icons must be added again.
            foreach (var i in _icons.Values) i.ReAdd();
            return;
        }
        base.WndProc(ref m);
    }

    public void Dispose()
    {
        foreach (var i in _icons.Values) i.Remove();
        _clickTimer.Dispose();
        DestroyHandle();
    }

    // ---- interop ----

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    internal struct NOTIFYICONDATAW
    {
        public int cbSize;
        public IntPtr hWnd;
        public uint uID;
        public uint uFlags;
        public uint uCallbackMessage;
        public IntPtr hIcon;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string szTip;
        public uint dwState;
        public uint dwStateMask;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string szInfo;
        public uint uVersion;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string szInfoTitle;
        public uint dwInfoFlags;
        public Guid guidItem;
        public IntPtr hBalloonIcon;
    }

    internal const uint NIM_ADD = 0, NIM_MODIFY = 1, NIM_DELETE = 2, NIM_SETVERSION = 4;
    internal const uint NIF_MESSAGE = 1, NIF_ICON = 2, NIF_TIP = 4, NIF_INFO = 0x10, NIF_SHOWTIP = 0x80;

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    internal static extern bool Shell_NotifyIconW(uint msg, ref NOTIFYICONDATAW data);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    static extern int RegisterWindowMessageW(string msg);

    [DllImport("user32.dll")]
    public static extern bool SetForegroundWindow(IntPtr hWnd);

    internal NOTIFYICONDATAW NewData(uint id) => new()
    {
        cbSize = Marshal.SizeOf<NOTIFYICONDATAW>(),
        hWnd = Handle,
        uID = id,
        uCallbackMessage = WM_TRAY,
        szTip = "", szInfo = "", szInfoTitle = "",
    };
}

public sealed class TrayIcon
{
    readonly TrayHost _host;
    public uint Id { get; }
    OwnedIcon? _image;
    string _tip = "";
    bool _added;

    internal TrayIcon(TrayHost host, uint id) { _host = host; Id = id; }

    public bool Visible => _added;

    /// <summary>Sets image and tooltip, adding the icon to the tray if needed. Takes ownership of the image.</summary>
    public void Update(OwnedIcon image, string tip)
    {
        var old = _image;
        _image = image;
        _tip = tip.Length > 127 ? tip[..124] + "..." : tip;
        if (_added) Send(TrayHost.NIM_MODIFY);
        else Add();
        old?.Dispose();
    }

    void Add()
    {
        _added = Send(TrayHost.NIM_ADD);
        if (!_added) Log.Write($"Tray icon {Id}: NIM_ADD failed (error {Marshal.GetLastWin32Error()})");
        if (_added)
        {
            var d = _host.NewData(Id);
            d.uVersion = 4; // NOTIFYICON_VERSION_4
            TrayHost.Shell_NotifyIconW(TrayHost.NIM_SETVERSION, ref d);
        }
    }

    internal void ReAdd() { if (_added) { _added = false; Add(); } }

    public void Remove()
    {
        if (_added)
        {
            var d = _host.NewData(Id);
            TrayHost.Shell_NotifyIconW(TrayHost.NIM_DELETE, ref d);
            _added = false;
        }
        _image?.Dispose();
        _image = null;
    }

    public void ShowBalloon(string title, string text)
    {
        if (!_added) return;
        var d = _host.NewData(Id);
        d.uFlags = TrayHost.NIF_INFO;
        d.szInfoTitle = title.Length > 63 ? title[..63] : title;
        d.szInfo = string.IsNullOrEmpty(text) ? " " : text;
        d.dwInfoFlags = 2; // NIIF_WARNING
        TrayHost.Shell_NotifyIconW(TrayHost.NIM_MODIFY, ref d);
    }

    bool Send(uint msg)
    {
        var d = _host.NewData(Id);
        d.uFlags = TrayHost.NIF_MESSAGE | TrayHost.NIF_ICON | TrayHost.NIF_TIP | TrayHost.NIF_SHOWTIP;
        d.hIcon = _image?.Icon.Handle ?? IntPtr.Zero;
        d.szTip = _tip;
        return TrayHost.Shell_NotifyIconW(msg, ref d);
    }
}
