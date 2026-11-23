using System.Drawing.Drawing2D;
using System.Drawing.Imaging;

namespace BatteryTray;

/// <summary>Builds the app icon, installer images and UI logo from Assets/logo.png (embedded).</summary>
public static class LogoRenderer
{
    static Bitmap? _logo;

    public static Bitmap Logo
    {
        get
        {
            if (_logo != null) return _logo;
            using var s = typeof(LogoRenderer).Assembly.GetManifestResourceStream("BatteryTray.Assets.logo.png")!;
            return _logo = new Bitmap(s);
        }
    }

    // Regions of the 512x512 artwork (outer frame and title text left out).
    static readonly Rectangle FullArt = new(112, 78, 288, 288);   // battery, waves and the orbiting devices
    static readonly Rectangle CoreArt = new(176, 116, 200, 210);  // battery and waves only, for tiny sizes

    static readonly Color TealDark = Color.FromArgb(0x3F, 0x6B, 0x70);
    static readonly Color TealLight = Color.FromArgb(0x6A, 0x9A, 0x92);

    /// <summary>White rounded tile with a teal frame and the artwork, like the supplied logo.</summary>
    public static Bitmap RenderTile(int size)
    {
        var bmp = new Bitmap(size, size, PixelFormat.Format32bppArgb);
        using var g = Graphics.FromImage(bmp);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.InterpolationMode = InterpolationMode.HighQualityBicubic;
        g.PixelOffsetMode = PixelOffsetMode.HighQuality;
        g.Clear(Color.Transparent);

        float stroke = Math.Max(1f, size / 28f);
        var rc = new RectangleF(stroke / 2, stroke / 2, size - stroke, size - stroke);
        using (var path = Rounded(rc, size * 0.2f))
        {
            using (var fill = new SolidBrush(Color.White)) g.FillPath(fill, path);
            using var pen = new Pen(new LinearGradientBrush(rc, TealDark, TealLight, 45f), stroke);
            g.DrawPath(pen, path);
        }

        var art = size <= 32 ? CoreArt : FullArt;
        float pad = size <= 32 ? size * 0.14f : size * 0.1f;
        var dest = new RectangleF(pad, pad, size - pad * 2, size - pad * 2);
        g.DrawImage(Logo, dest, art, GraphicsUnit.Pixel);
        return bmp;
    }

    /// <summary>Logo mark without a tile (transparent), for the UI header.</summary>
    public static Bitmap RenderMark(int size)
    {
        var bmp = new Bitmap(size, size, PixelFormat.Format32bppArgb);
        using var g = Graphics.FromImage(bmp);
        g.InterpolationMode = InterpolationMode.HighQualityBicubic;
        g.PixelOffsetMode = PixelOffsetMode.HighQuality;
        g.DrawImage(Logo, new Rectangle(0, 0, size, size), FullArt, GraphicsUnit.Pixel);
        return bmp;
    }

    public static string MarkDataUri(int size)
    {
        using var bmp = RenderTile(size);
        using var ms = new MemoryStream();
        bmp.Save(ms, ImageFormat.Png);
        return "data:image/png;base64," + Convert.ToBase64String(ms.ToArray());
    }

    static GraphicsPath Rounded(RectangleF r, float radius)
    {
        var p = new GraphicsPath();
        float d = radius * 2;
        p.AddArc(r.X, r.Y, d, d, 180, 90);
        p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        p.CloseFigure();
        return p;
    }

    /// <summary>Multi-resolution .ico with PNG entries.</summary>
    public static void SaveIco(string path)
    {
        int[] sizes = { 16, 20, 24, 32, 40, 48, 64, 128, 256 };
        var pngs = sizes.Select(s =>
        {
            using var bmp = RenderTile(s);
            using var ms = new MemoryStream();
            bmp.Save(ms, ImageFormat.Png);
            return ms.ToArray();
        }).ToList();
        using var fs = File.Create(path);
        using var w = new BinaryWriter(fs);
        w.Write((ushort)0); w.Write((ushort)1); w.Write((ushort)sizes.Length);
        int offset = 6 + 16 * sizes.Length;
        for (int i = 0; i < sizes.Length; i++)
        {
            w.Write((byte)(sizes[i] >= 256 ? 0 : sizes[i]));
            w.Write((byte)(sizes[i] >= 256 ? 0 : sizes[i]));
            w.Write((byte)0); w.Write((byte)0);
            w.Write((ushort)1); w.Write((ushort)32);
            w.Write(pngs[i].Length); w.Write(offset);
            offset += pngs[i].Length;
        }
        foreach (var p in pngs) w.Write(p);
    }

    /// <summary>Installer wizard images (BMP, white background).</summary>
    public static void SaveWizardImages(string folder)
    {
        using (var small = new Bitmap(110, 110))
        {
            using (var g = Graphics.FromImage(small)) { g.Clear(Color.White); using var t = RenderTile(100); g.DrawImage(t, 5, 5); }
            small.Save(Path.Combine(folder, "wizard-small.bmp"), ImageFormat.Bmp);
        }
        using (var large = new Bitmap(328, 628))
        {
            using (var g = Graphics.FromImage(large))
            {
                g.Clear(Color.White);
                g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                g.DrawImage(Logo, new Rectangle(4, 154, 320, 320));
            }
            large.Save(Path.Combine(folder, "wizard-large.bmp"), ImageFormat.Bmp);
        }
    }
}
