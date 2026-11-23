using System.Text.Json;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace BatteryTray;

/// <summary>
/// Hosts the HTML interface (Assets/ui.html) in WebView2. Two flavours:
/// the Settings window (Mica backdrop, normal title bar) and the flyout (borderless pop-up above the taskbar).
/// </summary>
public sealed class WebWindow : Form
{
    public enum Kind { Settings, Flyout }

    readonly Kind _kind;
    readonly WebView2 _web = new() { Dock = DockStyle.Fill };
    bool _loaded;
    string? _pendingState;
    public event Action<JsonElement>? MessageReceived;
    public event Action? Loaded;

    static CoreWebView2Environment? _env;
    static string? _html;

    public WebWindow(Kind kind)
    {
        _kind = kind;
        AutoScaleMode = AutoScaleMode.None; // sized explicitly from the monitor's DPI below
        Text = TrayApp.AppName;
        try { Icon = Icon.ExtractAssociatedIcon(Environment.ProcessPath!); } catch { }
        bool light = IconRenderer.AppsUseLight();
        float s = DeviceDpi / 96f; // sizes below are in 100%-scale pixels

        if (kind == Kind.Settings)
        {
            StartPosition = FormStartPosition.CenterScreen;
            var work = Screen.PrimaryScreen!.WorkingArea;
            ClientSize = new Size(Math.Min((int)(920 * s), work.Width - 80), Math.Min((int)(760 * s), work.Height - 80));
            MinimumSize = new Size((int)(560 * s), (int)(480 * s));
            BackColor = Color.Black; // becomes Mica where the frame is extended
            _web.DefaultBackgroundColor = Color.Transparent;
        }
        else
        {
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            TopMost = true;
            StartPosition = FormStartPosition.Manual;
            ClientSize = new Size((int)(380 * s), (int)(200 * s));
            BackColor = light ? Color.FromArgb(0xF3, 0xF3, 0xF3) : Color.FromArgb(0x24, 0x24, 0x24);
            _web.DefaultBackgroundColor = BackColor;
            // WBT_PIN_FLYOUT=1 keeps the flyout open when it loses focus (for screenshots/testing).
            if (Environment.GetEnvironmentVariable("WBT_PIN_FLYOUT") != "1")
                Deactivate += (_, _) => { HiddenAt = DateTime.UtcNow; Hide(); };
        }
        Controls.Add(_web);
    }

    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            if (_kind == Kind.Flyout) cp.ExStyle |= 0x80; // WS_EX_TOOLWINDOW: no Alt+Tab entry
            return cp;
        }
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        int dark = IconRenderer.AppsUseLight() ? 0 : 1;
        Win32.DwmSetWindowAttribute(Handle, 20, ref dark, 4);              // DWMWA_USE_IMMERSIVE_DARK_MODE
        int corner = 2;
        Win32.DwmSetWindowAttribute(Handle, 33, ref corner, 4);            // DWMWA_WINDOW_CORNER_PREFERENCE = round
        if (_kind == Kind.Settings)
        {
            int mica = 2;
            Win32.DwmSetWindowAttribute(Handle, 38, ref mica, 4);          // DWMWA_SYSTEMBACKDROP_TYPE = Mica
            var m = new Win32.MARGINS { Left = -1, Right = -1, Top = -1, Bottom = -1 };
            Win32.DwmExtendFrameIntoClientArea(Handle, ref m);
        }
        _ = InitAsync();
    }

    async Task InitAsync()
    {
        try
        {
            _env ??= await CoreWebView2Environment.CreateAsync(null,
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WirelessBatteryTracker", "WebView2"));
            await _web.EnsureCoreWebView2Async(_env);
            var s = _web.CoreWebView2.Settings;
            s.AreDefaultContextMenusEnabled = false;
            s.AreDevToolsEnabled = false;
            s.IsStatusBarEnabled = false;
            s.IsZoomControlEnabled = false;
            s.AreBrowserAcceleratorKeysEnabled = false;
            _web.CoreWebView2.WebMessageReceived += (_, e) =>
            {
                using var doc = JsonDocument.Parse(e.WebMessageAsJson);
                var root = doc.RootElement.Clone();
                if (root.TryGetProperty("type", out var t) && t.GetString() == "ready")
                {
                    _loaded = true;
                    if (_pendingState != null) { _web.CoreWebView2.PostWebMessageAsJson(_pendingState); _pendingState = null; }
                    Loaded?.Invoke();
                    return;
                }
                MessageReceived?.Invoke(root);
            };
            _web.CoreWebView2.NavigateToString(Html().Replace("{{VIEW}}", _kind == Kind.Flyout ? "flyout" : "settings"));
        }
        catch (Exception ex)
        {
            Log.Write("WebView2 failed: " + ex.Message);
            MessageBox.Show("The interface needs the Microsoft Edge WebView2 Runtime, which is part of Windows 11.\n\n" + ex.Message,
                TrayApp.AppName, MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    static string Html()
    {
        if (_html != null) return _html;
        using var s = typeof(WebWindow).Assembly.GetManifestResourceStream("BatteryTray.Assets.ui.html")!;
        using var r = new StreamReader(s);
        return _html = r.ReadToEnd().Replace("{{LOGO}}", LogoRenderer.MarkDataUri(128));
    }

    public bool IsLoaded => _loaded;
    public DateTime HiddenAt { get; private set; }

    public void PostState(object state)
    {
        var json = JsonSerializer.Serialize(state, JsonOpts);
        if (_loaded && _web.CoreWebView2 != null) _web.CoreWebView2.PostWebMessageAsJson(json);
        else _pendingState = json;
    }

    public static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() },
    };

    /// <summary>Positions the flyout above the taskbar, right-aligned to the anchor.</summary>
    public void ShowFlyout(Rectangle anchor)
    {
        var screen = Screen.FromPoint(new Point(anchor.X + anchor.Width / 2, anchor.Y));
        var bar = Win32.TaskbarRect();
        int bottom = bar.IsEmpty || bar.Top <= screen.Bounds.Top ? screen.WorkingArea.Bottom : Math.Min(bar.Top, screen.WorkingArea.Bottom);
        float s = DeviceDpi / 96f;
        int margin = (int)(12 * s);
        int x = Math.Clamp(anchor.Right - Width + (int)(40 * s), screen.WorkingArea.Left + margin, screen.WorkingArea.Right - Width - margin);
        int y = bottom - Height - margin;
        Location = new Point(x, y);
        Show();
        Activate();
        Win32.SetForegroundWindow(Handle);
    }

    public void SetContentHeight(int cssPx)
    {
        float s = DeviceDpi / 96f;
        int h = (int)Math.Ceiling(cssPx * s);
        if (Math.Abs(Height - h) < 2) return;
        int bottom = Bottom;
        Height = h;
        if (Visible) Top = bottom - h;
    }
}
