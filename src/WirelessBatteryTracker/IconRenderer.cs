using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using BatteryTray.Providers;
using Microsoft.Win32;

namespace BatteryTray;

/// <summary>An Icon that owns its native HICON and destroys it on dispose.</summary>
public sealed class OwnedIcon : IDisposable
{
    readonly IntPtr _handle;
    public Icon Icon { get; }
    public OwnedIcon(Bitmap bmp) { _handle = bmp.GetHicon(); Icon = Icon.FromHandle(_handle); }
    public void Dispose() { Icon.Dispose(); Native.DestroyIcon(_handle); }
}

/// <summary>Shared drawing helpers: Windows' icon font (Segoe Fluent Icons) and UI font (Segoe UI Variable).</summary>
public static class IconRenderer
{
    public static readonly string IconFont = FontExists("Segoe Fluent Icons") ? "Segoe Fluent Icons" : "Segoe MDL2 Assets";
    public static readonly string TextFont = FontExists("Segoe UI Variable Text") ? "Segoe UI Variable Text" : "Segoe UI";

    static bool FontExists(string name)
    {
        try { using var ff = new FontFamily(name); return true; } catch { return false; }
    }

    public static bool TaskbarIsLight()
    {
        try
        {
            using var k = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return k?.GetValue("SystemUsesLightTheme") is int v && v == 1;
        }
        catch { return false; }
    }

    public static bool AppsUseLight()
    {
        try
        {
            using var k = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return k?.GetValue("AppsUseLightTheme") is not int v || v == 1;
        }
        catch { return true; }
    }

    public static int IconSize => Math.Max(16, SystemInformation.SmallIconSize.Width);

    public static string Glyph(DeviceKind kind) => kind switch
    {
        DeviceKind.Mouse => "\uE962",
        DeviceKind.Keyboard => "\uE765",
        DeviceKind.Headphones => "\uE7F6",
        DeviceKind.Headset => "\uE95B",
        DeviceKind.Controller => "\uE7FC",
        DeviceKind.Speaker => "\uE7F5",
        DeviceKind.Touchpad => "\uEFA5",
        DeviceKind.Pen => "\uEDC6",
        DeviceKind.Phone => "\uE8EA",
        _ => "\uE772",
    };

    public static Color ChargingColor(bool light) => light ? Color.FromArgb(0x0F, 0x7B, 0x0F) : Color.FromArgb(0x6C, 0xCB, 0x5F);

    /// <summary>Clock-coloured normally; amber, then red as the battery gets low; grey when disconnected.</summary>
    public static Color LevelTextColor(int? level, bool connected, int low, bool light)
    {
        if (!connected) return light ? Color.FromArgb(0x8A, 0x8A, 0x8A) : Color.FromArgb(0x80, 0x80, 0x80);
        if (level is int l && l <= low) return light ? Color.FromArgb(0xC4, 0x2B, 0x1C) : Color.FromArgb(0xFF, 0x63, 0x63);
        if (level is int a && a <= low + 10) return light ? Color.FromArgb(0x9D, 0x5D, 0x00) : Color.FromArgb(0xFF, 0xB9, 0x00);
        return light ? Color.FromArgb(0x1A, 0x1A, 0x1A) : Color.White;
    }

    /// <summary>Draws text centred on its actual ink (not the font's line box).</summary>
    public static void DrawCentered(Graphics g, string text, Font font, Color color, RectangleF area)
    {
        var ink = Ink(text, font);
        float x = area.X + (area.Width - ink.Width) / 2 - ink.X;
        float y = area.Y + (area.Height - ink.Height) / 2 - ink.Y;
        using var b = new SolidBrush(color);
        g.DrawString(text, font, b, new PointF(MathF.Round(x), MathF.Round(y)), StringFormat.GenericTypographic);
    }

    /// <summary>Left-aligned, vertically centred on the digits' ink.</summary>
    public static void DrawCenteredVertically(Graphics g, string text, Font font, Color color, RectangleF area)
    {
        var ink = Ink("0", font); // digit height, so "55" and "100" sit on the same baseline
        float y = area.Y + (area.Height - ink.Height) / 2 - ink.Y;
        using var b = new SolidBrush(color);
        g.DrawString(text, font, b, new PointF(MathF.Round(area.X), MathF.Round(y)), StringFormat.GenericTypographic);
    }

    static RectangleF Ink(string text, Font font)
    {
        using var path = new GraphicsPath();
        path.AddString(text, font.FontFamily, (int)font.Style, font.Size, PointF.Empty, StringFormat.GenericTypographic);
        return path.GetBounds();
    }

    /// <summary>
    /// Fallback square tray icon: device symbol on top, battery percentage underneath.
    /// </summary>
    public static OwnedIcon RenderCombined(DeviceKind kind, int? level, bool connected, bool charging, int low)
    {
        bool light = TaskbarIsLight();
        int size = IconSize;
        using var bmp = new Bitmap(size, size, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
            g.Clear(Color.Transparent);
            var fg = connected ? (light ? Color.FromArgb(0x1A, 0x1A, 0x1A) : Color.White) : Color.Gray;

            float glyphH = size * 0.5f;
            using (var gf = new Font(IconFont, glyphH, GraphicsUnit.Pixel))
            {
                // Thin icon-font strokes look faint at this size: draw twice, half a pixel apart, for weight.
                var gc = charging && connected ? ChargingColor(light) : fg;
                DrawCentered(g, Glyph(kind), gf, gc, new RectangleF(0, 0, size, glyphH));
                DrawCentered(g, Glyph(kind), gf, gc, new RectangleF(0.5f, 0, size, glyphH));
            }

            string text = level?.ToString() ?? "–";
            var area = new RectangleF(0, glyphH + size * 0.04f, size, size - glyphH - size * 0.04f);
            // Fit the number to the remaining space.
            float px = size * 0.62f;
            Font font;
            while (true)
            {
                font = new Font(TextFont, px, FontStyle.Bold, GraphicsUnit.Pixel);
                var ink = Ink(text, font);
                if ((ink.Width <= size && ink.Height <= area.Height) || px < 5) break;
                font.Dispose();
                px -= 0.5f;
            }
            using (font) DrawCentered(g, text, font, LevelTextColor(level, connected, low, light), area);
        }
        return new OwnedIcon(bmp);
    }

    public static OwnedIcon RenderAppTrayIcon()
    {
        using var bmp = LogoRenderer.RenderTile(IconSize);
        return new OwnedIcon(bmp);
    }
}
