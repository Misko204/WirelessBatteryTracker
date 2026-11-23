using System.Text;

namespace BatteryTray.Providers;

/// <summary>
/// Sony DualSense, DualSense Edge and DualShock 4 controllers over USB or Bluetooth.
/// The battery level is part of the controller's regular HID input report.
/// </summary>
public sealed class PlayStationProvider : IBatteryProvider
{
    public string Name => "PlayStation controllers";

    const ushort SonyVid = 0x054C;
    enum Model { DualSense, DualSenseEdge, DualShock4 }

    static Model? ModelOf(ushort pid) => pid switch
    {
        0x0CE6 => Model.DualSense,
        0x0DF2 => Model.DualSenseEdge,
        0x05C4 or 0x09CC or 0x0BA0 => Model.DualShock4,
        _ => null,
    };

    public async Task<IReadOnlyList<DeviceReading>> PollAsync(CancellationToken ct)
    {
        var result = new List<DeviceReading>();
        foreach (var path in Native.GetHidInterfacePaths())
        {
            if (!path.Contains("054c", StringComparison.OrdinalIgnoreCase)) continue;
            if (Native.GetHidIds(path) is not (SonyVid, var pid) || ModelOf(pid) is not Model model) continue;
            var caps = Native.GetHidCaps(path);
            if (caps is not { UsagePage: 0x01, Usage: 0x05 }) continue; // gamepad collection
            try
            {
                var r = await ReadAsync(path, model, caps.Value, ct);
                if (r != null && result.All(x => x.Id != r.Id)) result.Add(r);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception ex) { Log.Write($"PlayStation read failed: {ex.Message}"); }
        }
        return result;
    }

    static async Task<DeviceReading?> ReadAsync(string path, Model model, HIDP_CAPS caps, CancellationToken ct)
    {
        bool bluetooth = path.Contains("00001124-0000-1000-8000-00805f9b34fb", StringComparison.OrdinalIgnoreCase);
        using var h = Native.CreateFileW(path, Native.GENERIC_READ | Native.GENERIC_WRITE,
            Native.FILE_SHARE_READ | Native.FILE_SHARE_WRITE, IntPtr.Zero, Native.OPEN_EXISTING, Native.FILE_FLAG_OVERLAPPED, IntPtr.Zero);
        if (h.IsInvalid) return null;

        string id = ControllerId(h, model, path);
        using var stream = new FileStream(h, FileAccess.ReadWrite, 0, true);
        var buf = new byte[caps.InputReportByteLength];

        int? level = null; bool? charging = null;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(1.5));
        bool askedFullReports = false;
        try
        {
            for (int i = 0; i < 40 && level == null; i++)
            {
                int n = await stream.ReadAsync(buf, timeout.Token);
                if (n < 2) continue;
                byte reportId = buf[0];

                if (model == Model.DualShock4)
                {
                    int off = reportId == 0x01 ? 30 : reportId == 0x11 ? 32 : -1;
                    if (off < 0 || n <= off)
                    {
                        if (bluetooth && !askedFullReports) { askedFullReports = true; RequestFeature(h, 0x02, 37); }
                        continue;
                    }
                    int raw = buf[off] & 0x0F;
                    bool cable = (buf[off] & 0x10) != 0;
                    level = raw >= 10 ? 100 : raw * 10 + 5;
                    if (!cable) level = Math.Min(100, raw * 10 + 5);
                    charging = cable && raw < 11;
                }
                else
                {
                    // DualSense: USB report 0x01 (status at 53), Bluetooth full report 0x31 (status at 54).
                    int off = reportId == 0x01 && !bluetooth ? 53 : reportId == 0x31 ? 54 : -1;
                    if (off < 0 || n <= off)
                    {
                        // Over Bluetooth the controller starts in a short report mode without battery data;
                        // reading feature report 0x05 switches it to full reports (what Steam does too).
                        if (bluetooth && !askedFullReports) { askedFullReports = true; RequestFeature(h, 0x05, 41); }
                        continue;
                    }
                    int status = buf[off];
                    int cap = status & 0x0F, state = status >> 4;
                    level = Math.Min(100, cap * 10 + 5);
                    if (state == 0x2) level = 100;
                    charging = state == 0x1;
                }
            }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { }
        catch (IOException) { }

        if (level == null) return null;
        return new DeviceReading
        {
            Id = id,
            Name = model switch
            {
                Model.DualSense => "DualSense Wireless Controller",
                Model.DualSenseEdge => "DualSense Edge",
                _ => "DUALSHOCK 4",
            },
            Source = DeviceSource.PlayStation,
            Kind = DeviceKind.Controller,
            Level = level,
            IsCharging = charging,
            IsApproximate = true, // the controller reports 10% steps
        };
    }

    static void RequestFeature(Microsoft.Win32.SafeHandles.SafeFileHandle h, byte reportId, int length)
    {
        var f = new byte[length];
        f[0] = reportId;
        Native.HidD_GetFeature(h, f, f.Length);
    }

    /// <summary>
    /// Uses the controller's Bluetooth address so the same controller over USB and over
    /// Bluetooth is one device (and merges with Windows' own Bluetooth entry).
    /// </summary>
    static string ControllerId(Microsoft.Win32.SafeHandles.SafeFileHandle h, Model model, string path)
    {
        var sn = new byte[256];
        if (Native.HidD_GetSerialNumberString(h, sn, sn.Length))
        {
            var s = Encoding.Unicode.GetString(sn).TrimEnd('\0').Replace(":", "").Replace("-", "");
            if (s.Length == 12 && s.All(Uri.IsHexDigit)) return "bt:" + s.ToUpperInvariant();
        }
        // Pairing-info feature report holds the controller's MAC (little endian at bytes 1..6).
        var f = new byte[model == Model.DualShock4 ? 16 : 20];
        f[0] = model == Model.DualShock4 ? (byte)0x12 : (byte)0x09;
        if (Native.HidD_GetFeature(h, f, f.Length))
        {
            var mac = string.Concat(Enumerable.Range(1, 6).Reverse().Select(i => f[i].ToString("X2")));
            if (mac != "000000000000") return "bt:" + mac;
        }
        // Stable FNV-1a hash of the device path (string.GetHashCode is randomized per process).
        uint hash = 2166136261;
        foreach (char c in path.ToLowerInvariant()) { hash ^= c; hash *= 16777619; }
        return "ps:" + hash.ToString("X8");
    }
}
