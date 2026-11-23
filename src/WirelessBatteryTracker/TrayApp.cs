using System.Reflection;
using System.Text.Json;
using Microsoft.Win32;

namespace BatteryTray;

/// <summary>
/// App controller. Shows devices either embedded in the taskbar (one item per device) or,
/// as a fallback, as one square notification-area icon per device. Owns the flyout,
/// the Settings window and notifications.
/// </summary>
public sealed class TrayApp : ApplicationContext
{
    public const string AppName = "Wireless device battery tracker";
    const uint AppTrayId = 0;

    AppSettings _settings;
    readonly DeviceMonitor _monitor;
    readonly Notifier _notifier;
    readonly Dictionary<string, DeviceState> _states = new();
    List<DeviceReading> _others = new();
    readonly TrayHost _tray = new();
    readonly TaskbarStrip _strip = new();
    readonly ContextMenuStrip _menu = new();
    WebWindow? _settingsWin, _flyout;
    readonly System.Windows.Forms.Timer _promoteTimer = new() { Interval = 2500 };
    bool _hasPolled, _reAdding;

    public TrayApp()
    {
        _settings = SettingsStore.Load();
        EnsureTrayIds();
        foreach (var d in _settings.Devices)
            _states[d.Id] = new DeviceState { Settings = d, Level = d.LastLevel, IsConnected = false };

        _notifier = new Notifier((t, m) => _tray.All.FirstOrDefault(i => i.Visible)?.ShowBalloon(t, m));
        _promoteTimer.Tick += (_, _) => { _promoteTimer.Stop(); PromoteAndRefresh(); };

        _tray.LeftClick += _ => ShowFlyout(CursorAnchor());
        _tray.RightClick += _ => ShowMenu(Cursor.Position);
        _tray.DoubleClick += _ => OpenSettings();
        _tray.DeviceChanged += () => _monitor?.RefreshSoon();

        _strip.ItemClicked += (_, rect) => ShowFlyout(rect);
        _strip.ItemRightClicked += (_, pt) => ShowMenu(pt);
        _strip.EmbeddedChanged += _ => SyncDisplay();

        _menu.Renderer = new FluentMenuRenderer();
        _menu.ShowImageMargin = false;
        _menu.Font = new Font("Segoe UI", 9f);

        SystemEvents.UserPreferenceChanged += OnUserPreferenceChanged;
        SystemEvents.DisplaySettingsChanged += OnDisplayChanged;
        SyncDisplay();

        _monitor = new DeviceMonitor(() => _settings);
        _monitor.Updated += OnUpdated;
        _monitor.Start();
    }

    static Rectangle CursorAnchor() { var p = Cursor.Position; return new Rectangle(p.X - 12, p.Y - 12, 24, 24); }

    void EnsureTrayIds()
    {
        int next = _settings.Devices.Count == 0 ? 1 : Math.Max(1, _settings.Devices.Max(d => d.TrayId) + 1);
        foreach (var d in _settings.Devices.Where(d => d.TrayId <= 0)) d.TrayId = next++;
    }

    void OnUserPreferenceChanged(object? sender, UserPreferenceChangedEventArgs e)
    {
        if (e.Category is UserPreferenceCategory.General or UserPreferenceCategory.Color or UserPreferenceCategory.VisualStyle)
        {
            SyncDisplay();
            PushState();
        }
    }

    void OnDisplayChanged(object? sender, EventArgs e) => SyncDisplay();

    void OnUpdated(PollResult result)
    {
        _hasPolled = true;
        bool changed = false;
        var seen = new HashSet<string>();
        var others = new List<DeviceReading>();

        foreach (var r in result.Readings)
        {
            seen.Add(r.Id);
            if (!_states.TryGetValue(r.Id, out var st))
            {
                if (r.Level == null) { others.Add(r); continue; } // connected, but no battery info (yet)
                var ds = new DeviceSettings { Id = r.Id, Name = r.Name, Source = r.Source };
                _settings.Devices.Add(ds);
                EnsureTrayIds();
                _states[r.Id] = st = new DeviceState { Settings = ds };
                changed = true;
                Log.Write($"New device: {r.Name} ({r.Source}, {r.Id})");
            }
            if (!string.IsNullOrWhiteSpace(r.Name) && st.Settings.Name != r.Name && r.Level != null) { st.Settings.Name = r.Name; changed = true; }
            if (r.Kind != null && st.Settings.DetectedKind != r.Kind) { st.Settings.DetectedKind = r.Kind; changed = true; }
            if (r.Level != null && st.Settings.Source != r.Source) { st.Settings.Source = r.Source; changed = true; }

            st.IsConnected = r.IsConnected;
            st.IsCharging = r.IsCharging;
            st.IsApproximate = r.IsApproximate;
            st.Detail = r.Detail;
            if (r.Level != null)
            {
                st.Level = r.Level;
                if (st.Settings.LastLevel != r.Level) { st.Settings.LastLevel = r.Level; changed = true; }
            }
            if (r.IsConnected) st.Settings.LastSeen = DateTime.Now;
        }
        _others = others;

        // Known devices that no provider returned are offline (keep the last level for display).
        foreach (var st in _states.Values)
            if (!seen.Contains(st.Settings.Id) && (result.PolledSources.Contains(st.Settings.Source) || st.Settings.Source is DeviceSource.Apple or DeviceSource.PlayStation))
            {
                st.IsConnected = false;
                st.IsCharging = null;
                st.Detail = null;
            }

        foreach (var st in _states.Values) _notifier.Evaluate(st, _settings.CriticalLevel);

        if (changed) SettingsStore.Save(_settings);
        SyncDisplay();
        PushState();
    }

    IEnumerable<DeviceState> Ordered => _settings.Devices.Select(d => _states[d.Id]);

    bool ShowsOnTaskbar(DeviceState s) =>
        s.Settings.Display != DisplayMode.Hidden
        && (s.IsConnected || !s.Settings.HideWhenDisconnected)
        && (s.Level != null || s.Settings.LastLevel != null);

    // ------------------------------------------------------------ display

    void SyncDisplay()
    {
        var visible = Ordered.Where(ShowsOnTaskbar).ToList();
        bool inline = _settings.TaskbarMode == TaskbarMode.Inline;

        if (inline)
        {
            var items = visible.Select(s => new StripItem(s.Settings.Id, s.Settings.Kind, s.Level, s.IsConnected,
                s.IsCharging == true && s.IsConnected, s.Settings.LowThreshold)).ToList();
            if (items.Count == 0) items.Add(new StripItem("app", DeviceKind.Generic, null, true, false, 0, IsAppItem: true));
            _strip.SetItems(items, _settings.ShowPercentSign);
        }
        else _strip.SetItems(Array.Empty<StripItem>(), _settings.ShowPercentSign);

        // Tray icons: used in tray mode, or whenever the taskbar strip couldn't be embedded.
        bool useTray = !inline || !_strip.IsEmbedded;
        var wanted = new HashSet<uint>();
        bool added = false;
        if (useTray)
        {
            foreach (var s in visible)
            {
                var icon = _tray.Get((uint)s.Settings.TrayId);
                added |= !icon.Visible;
                icon.Update(IconRenderer.RenderCombined(s.Settings.Kind, s.Level, s.IsConnected, s.IsCharging == true, s.Settings.LowThreshold),
                    $"{s.Settings.DisplayName}: {s.Describe()}" + (s.Detail != null && s.IsConnected ? $"\n{s.Detail}" : ""));
                wanted.Add(icon.Id);
            }
            if (visible.Count == 0)
            {
                var icon = _tray.Get(AppTrayId);
                added |= !icon.Visible;
                icon.Update(IconRenderer.RenderAppTrayIcon(), _hasPolled ? $"{AppName}\nNo devices with battery info yet" : $"{AppName}\nLooking for devices…");
                wanted.Add(icon.Id);
            }
        }
        foreach (var icon in _tray.All.Where(i => i.Visible && !wanted.Contains(i.Id)).ToList()) icon.Remove();
        if (added && !_reAdding && _settings.KeepIconsVisible) { _promoteTimer.Stop(); _promoteTimer.Start(); }
    }

    /// <summary>
    /// Marks our tray icons "show on taskbar". Explorer only reads that flag when an icon is added,
    /// so icons are removed and added again afterwards.
    /// </summary>
    void PromoteAndRefresh()
    {
        if (!_settings.KeepIconsVisible || !_tray.All.Any(i => i.Visible)) return;
        TaskbarHelper.PromoteOwnIcons();
        foreach (var icon in _tray.All.Where(i => i.Visible).ToList()) icon.Remove();
        _reAdding = true;
        try { SyncDisplay(); } finally { _reAdding = false; }
    }

    // ------------------------------------------------------------ UI

    void ShowMenu(Point at)
    {
        _menu.Items.Clear();
        _menu.Items.Add("Show battery levels", null, (_, _) => ShowFlyout(new Rectangle(at, new Size(1, 1))));
        _menu.Items.Add("Refresh now", null, (_, _) => _monitor.RefreshNow());
        _menu.Items.Add("Settings", null, (_, _) => OpenSettings());
        _menu.Items.Add(new ToolStripSeparator());
        _menu.Items.Add("Quit", null, (_, _) => ExitThread());
        Win32.SetForegroundWindow(_tray.Handle);
        _menu.Show(at, ToolStripDropDownDirection.AboveLeft);
    }

    void ShowFlyout(Rectangle anchor)
    {
        if (_flyout is { Visible: true }) { _flyout.Hide(); return; }
        // Clicking the taskbar item while the flyout is open first deactivates (hides) it;
        // don't immediately reopen it on that same click.
        if (_flyout != null && (DateTime.UtcNow - _flyout.HiddenAt).TotalMilliseconds < 400) return;
        if (_flyout == null || _flyout.IsDisposed)
        {
            _flyout = new WebWindow(WebWindow.Kind.Flyout);
            _flyout.MessageReceived += OnUiMessage;
            _flyout.Loaded += () => _flyout.ShowFlyout(_flyoutAnchor);
            _flyoutAnchor = anchor;
            // WebView2 only loads in a shown window: show it off-screen, then move it into place once loaded.
            _flyout.Location = new Point(-32000, -32000);
            _flyout.Show();
            PushState();
            return;
        }
        _flyoutAnchor = anchor;
        PushState();
        _flyout.ShowFlyout(anchor);
    }
    Rectangle _flyoutAnchor;

    public void OpenSettings() => OpenSettings(null);

    /// <summary>Opens the flyout anchored at the right end of the taskbar (used by "--flyout").</summary>
    public void ShowFlyoutAtTaskbar()
    {
        var bar = Win32.TaskbarRect();
        var screen = Screen.PrimaryScreen!.Bounds;
        var anchor = bar.IsEmpty ? new Rectangle(screen.Right - 200, screen.Bottom - 48, 40, 40)
                                 : new Rectangle(bar.Right - 300, Math.Min(bar.Top, screen.Bottom - bar.Height), 40, bar.Height);
        ShowFlyout(anchor);
    }

    void OpenSettings(string? focusId)
    {
        _flyout?.Hide();
        if (_settingsWin is { IsDisposed: false })
        {
            if (_settingsWin.WindowState == FormWindowState.Minimized) _settingsWin.WindowState = FormWindowState.Normal;
            _settingsWin.Activate();
            PushState(focusId);
            return;
        }
        _settingsWin = new WebWindow(WebWindow.Kind.Settings);
        _settingsWin.MessageReceived += OnUiMessage;
        _settingsWin.FormClosed += (_, _) => _settingsWin = null;
        PushState(focusId);
        _settingsWin.Show();
        _settingsWin.Activate();
    }

    void OnUiMessage(JsonElement msg)
    {
        string type = msg.GetProperty("type").GetString() ?? "";
        switch (type)
        {
            case "size":
                _flyout?.SetContentHeight((int)Math.Ceiling(msg.GetProperty("height").GetDouble()));
                break;
            case "close":
                _flyout?.Hide();
                break;
            case "settings":
                OpenSettings(msg.TryGetProperty("id", out var fid) ? fid.GetString() : null);
                break;
            case "refresh":
                _monitor.RefreshNow();
                break;
            case "test":
                _notifier.Show(AppName, "Notifications are working.");
                break;
            case "exit":
                ExitThread();
                break;
            case "forget":
            {
                var id = msg.GetProperty("id").GetString();
                _settings.Devices.RemoveAll(d => d.Id == id);
                if (id != null) _states.Remove(id);
                SettingsStore.Save(_settings);
                SyncDisplay();
                PushState();
                break;
            }
            case "device":
            {
                var id = msg.GetProperty("id").GetString();
                if (id == null || !_states.TryGetValue(id, out var st)) break;
                var d = st.Settings;
                foreach (var p in msg.GetProperty("patch").EnumerateObject())
                {
                    switch (p.Name)
                    {
                        case "customName":
                            var n = p.Value.GetString()?.Trim();
                            d.CustomName = string.IsNullOrEmpty(n) || n == d.Name ? null : n;
                            break;
                        case "kindOverride":
                            d.KindOverride = p.Value.ValueKind == JsonValueKind.Null ? null : Enum.Parse<DeviceKind>(p.Value.GetString()!);
                            break;
                        case "showOnTaskbar": d.Display = p.Value.GetBoolean() ? DisplayMode.OwnIcon : DisplayMode.Hidden; break;
                        case "hideWhenDisconnected": d.HideWhenDisconnected = p.Value.GetBoolean(); break;
                        case "lowThreshold": d.LowThreshold = Math.Clamp(p.Value.GetInt32(), 1, 99); break;
                        case "notifyLow": d.NotifyLow = p.Value.GetBoolean(); break;
                        case "notifyFull": d.NotifyFull = p.Value.GetBoolean(); break;
                        case "notifyConnection": d.NotifyConnection = p.Value.GetBoolean(); break;
                    }
                }
                SettingsStore.Save(_settings);
                SyncDisplay();
                PushState();
                break;
            }
            case "general":
            {
                var s = _settings;
                bool repoll = false;
                foreach (var p in msg.GetProperty("patch").EnumerateObject())
                {
                    switch (p.Name)
                    {
                        case "pollSeconds": s.PollSeconds = Math.Clamp(p.Value.GetInt32(), 10, 3600); repoll = true; break;
                        case "taskbarMode": s.TaskbarMode = Enum.Parse<TaskbarMode>(p.Value.GetString()!); break;
                        case "showPercentSign": s.ShowPercentSign = p.Value.GetBoolean(); break;
                        case "keepIconsVisible": s.KeepIconsVisible = p.Value.GetBoolean(); break;
                        case "criticalLevel": s.CriticalLevel = Math.Clamp(p.Value.GetInt32(), 1, 50); break;
                        case "startWithWindows": TaskbarHelper.SetStartWithWindows(p.Value.GetBoolean()); break;
                        case "enableBluetooth": s.EnableBluetooth = p.Value.GetBoolean(); repoll = true; break;
                        case "enableBleGatt": s.EnableBleGatt = p.Value.GetBoolean(); repoll = true; break;
                        case "enableApple": s.EnableApple = p.Value.GetBoolean(); repoll = true; break;
                        case "enablePlayStation": s.EnablePlayStation = p.Value.GetBoolean(); repoll = true; break;
                        case "enableLogitech": s.EnableLogitech = p.Value.GetBoolean(); repoll = true; break;
                        case "enableXbox": s.EnableXbox = p.Value.GetBoolean(); repoll = true; break;
                    }
                }
                SettingsStore.Save(s);
                SyncDisplay();
                PushState();
                if (repoll) _monitor.RefreshNow();
                break;
            }
        }
    }

    static readonly (DeviceKind kind, string label)[] KindLabels =
    {
        (DeviceKind.Mouse, "Mouse"), (DeviceKind.Keyboard, "Keyboard"), (DeviceKind.Headphones, "Headphones / earbuds"),
        (DeviceKind.Headset, "Headset"), (DeviceKind.Controller, "Game controller"), (DeviceKind.Speaker, "Speaker"),
        (DeviceKind.Touchpad, "Touchpad"), (DeviceKind.Pen, "Pen"), (DeviceKind.Phone, "Phone"), (DeviceKind.Generic, "Other device"),
    };

    static string SourceLabel(DeviceSource s) => s switch
    {
        DeviceSource.Bluetooth => "Bluetooth",
        DeviceSource.BluetoothLE => "Bluetooth LE",
        DeviceSource.Logitech => "Logitech USB receiver",
        DeviceSource.Xbox => "Xbox wireless",
        DeviceSource.PlayStation => "PlayStation controller",
        DeviceSource.Apple => "Bluetooth (AirPods)",
        _ => s.ToString(),
    };

    void PushState(string? openId = null)
    {
        if (_settingsWin == null && _flyout == null) return;
        bool light = IconRenderer.AppsUseLight();
        var (accent, onAccent) = Accent(light);
        var state = new
        {
            type = "state",
            light,
            accent,
            onAccent,
            openId,
            embedded = _strip.IsEmbedded,
            version = Assembly.GetExecutingAssembly().GetName().Version?.ToString(3),
            settings = new
            {
                _settings.PollSeconds, _settings.TaskbarMode, _settings.ShowPercentSign, _settings.KeepIconsVisible,
                _settings.CriticalLevel, StartWithWindows = TaskbarHelper.IsStartWithWindows(),
                _settings.EnableBluetooth, _settings.EnableBleGatt, _settings.EnableApple, _settings.EnablePlayStation,
                _settings.EnableLogitech, _settings.EnableXbox,
            },
            devices = Ordered.Select(s => new
            {
                id = s.Settings.Id,
                name = s.Settings.Name,
                customName = s.Settings.CustomName,
                displayName = s.Settings.DisplayName,
                kindOverride = s.Settings.KindOverride?.ToString(),
                detectedKind = (s.Settings.DetectedKind ?? DeviceKind.Generic).ToString(),
                glyph = IconRenderer.Glyph(s.Settings.Kind),
                sourceLabel = SourceLabel(s.Settings.Source),
                level = s.Level,
                connected = s.IsConnected,
                charging = s.IsCharging == true,
                approx = s.IsApproximate,
                detail = s.Detail,
                showOnTaskbar = s.Settings.Display != DisplayMode.Hidden,
                hideWhenDisconnected = s.Settings.HideWhenDisconnected,
                lowThreshold = s.Settings.LowThreshold,
                notifyLow = s.Settings.NotifyLow,
                notifyFull = s.Settings.NotifyFull,
                notifyConnection = s.Settings.NotifyConnection,
            }),
            others = _others.Select(o => new { id = o.Id, name = o.Name, glyph = IconRenderer.Glyph(o.Kind ?? DeviceKind.Generic), sourceLabel = SourceLabel(o.Source) }),
            kinds = KindLabels.Select(k => new { value = k.kind.ToString(), k.label }),
        };
        _settingsWin?.PostState(state);
        _flyout?.PostState(state);
    }

    /// <summary>Windows accent colour, picked the way Windows 11 controls use it in light/dark mode.</summary>
    static (string accent, string onAccent) Accent(bool light)
    {
        try
        {
            var ui = new Windows.UI.ViewManagement.UISettings();
            var c = ui.GetColorValue(light ? Windows.UI.ViewManagement.UIColorType.AccentDark1 : Windows.UI.ViewManagement.UIColorType.AccentLight2);
            return ($"#{c.R:X2}{c.G:X2}{c.B:X2}", light ? "#ffffff" : "#000000");
        }
        catch { return light ? ("#005fb8", "#ffffff") : ("#60cdff", "#000000"); }
    }

    protected override void ExitThreadCore()
    {
        SystemEvents.UserPreferenceChanged -= OnUserPreferenceChanged;
        SystemEvents.DisplaySettingsChanged -= OnDisplayChanged;
        _monitor.Dispose();
        _strip.Dispose();
        _tray.Dispose();
        _settingsWin?.Close();
        _flyout?.Close();
        base.ExitThreadCore();
    }
}

/// <summary>Context menu styled like Windows 11 menus (rounded highlight, theme colours).</summary>
sealed class FluentMenuRenderer : ToolStripProfessionalRenderer
{
    static bool Light => IconRenderer.AppsUseLight();
    static Color Bg => Light ? Color.FromArgb(0xF9, 0xF9, 0xF9) : Color.FromArgb(0x2C, 0x2C, 0x2C);
    static Color Fg => Light ? Color.FromArgb(0x1A, 0x1A, 0x1A) : Color.White;
    static Color Hover => Light ? Color.FromArgb(0xEA, 0xEA, 0xEA) : Color.FromArgb(0x3D, 0x3D, 0x3D);

    protected override void OnRenderToolStripBackground(ToolStripRenderEventArgs e) { using var b = new SolidBrush(Bg); e.Graphics.FillRectangle(b, e.AffectedBounds); }
    protected override void OnRenderToolStripBorder(ToolStripRenderEventArgs e)
    {
        using var p = new Pen(Light ? Color.FromArgb(0xE0, 0xE0, 0xE0) : Color.FromArgb(0x45, 0x45, 0x45));
        e.Graphics.DrawRectangle(p, 0, 0, e.AffectedBounds.Width - 1, e.AffectedBounds.Height - 1);
    }
    protected override void OnRenderMenuItemBackground(ToolStripItemRenderEventArgs e)
    {
        if (!e.Item.Selected) return;
        e.Graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
        var r = new Rectangle(4, 1, e.Item.Width - 8, e.Item.Height - 2);
        using var path = new System.Drawing.Drawing2D.GraphicsPath();
        int d = 8;
        path.AddArc(r.X, r.Y, d, d, 180, 90); path.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90); path.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        path.CloseFigure();
        using var b = new SolidBrush(Hover);
        e.Graphics.FillPath(b, path);
    }
    protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e) { e.TextColor = Fg; base.OnRenderItemText(e); }
    protected override void OnRenderSeparator(ToolStripSeparatorRenderEventArgs e)
    {
        using var p = new Pen(Light ? Color.FromArgb(0xE5, 0xE5, 0xE5) : Color.FromArgb(0x40, 0x40, 0x40));
        int y = e.Item.Height / 2;
        e.Graphics.DrawLine(p, 8, y, e.Item.Width - 8, y);
    }
}
