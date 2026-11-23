using System.Text;

namespace BatteryTray.Providers;

/// <summary>
/// Logitech devices behind a USB receiver (Bolt, Unifying, Lightspeed) or connected directly,
/// using the HID++ 2.0 protocol. Supports battery features 0x1004 (Unified Battery),
/// 0x1000 (Battery Status) and 0x1001 (Battery Voltage).
/// </summary>
public sealed class LogitechHidppProvider : IBatteryProvider
{
    public string Name => "Logitech receiver (HID++)";
    public static bool Verbose;

    const ushort LogitechVid = 0x046D;
    const byte LongReportId = 0x11;
    const byte SwId = 0x0B;

    const ushort FeatDeviceInfo = 0x0003, FeatDeviceName = 0x0005;
    const ushort FeatBatteryStatus = 0x1000, FeatBatteryVoltage = 0x1001, FeatUnifiedBattery = 0x1004;

    public async Task<IReadOnlyList<DeviceReading>> PollAsync(CancellationToken ct)
    {
        var result = new List<DeviceReading>();
        foreach (var path in Native.GetHidInterfacePaths())
        {
            if (!path.Contains("vid_046d", StringComparison.OrdinalIgnoreCase)) continue;
            var caps = Native.GetHidCaps(path);
            if (Verbose) Log.Write($"HID {path} page={caps?.UsagePage:X4} usage={caps?.Usage:X4} in={caps?.InputReportByteLength} out={caps?.OutputReportByteLength}");
            // HID++ long-report collection: vendor usage page 0xFF00, usage 0x0002, 20-byte reports.
            if (caps is not { UsagePage: 0xFF00, Usage: 0x0002, InputReportByteLength: 20 }) continue;

            try
            {
                using var dev = HidppChannel.Open(path, caps.Value);
                if (dev == null) continue;
                // IRoot.ping (feature 0, function 1) to every receiver slot at once, plus 0xFF for
                // directly connected devices. Only online HID++ 2.0 devices reply.
                var pings = await dev.BroadcastAsync(new byte[] { 1, 2, 3, 4, 5, 6, 0xFF }, 0, 1,
                    new byte[] { 0, 0, 0x5A }, TimeSpan.FromSeconds(2.5), ct);
                foreach (var (idx, ping) in pings.OrderBy(p => p.Key))
                {
                    if (Verbose) Log.Write($"HID++ ping idx={idx}: {Convert.ToHexString(ping)}");
                    if (ping[4] < 2) continue; // HID++ 1.0 (old device or the receiver itself)
                    if (idx == 0xFF && pings.Count > 1) continue;
                    var r = await PollDeviceAsync(dev, idx, ct);
                    if (r != null) result.Add(r);
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception ex) { Log.Write($"HID++ error on {path}: {ex.Message}"); }
        }
        return result;
    }

    static async Task<DeviceReading?> PollDeviceAsync(HidppChannel dev, byte idx, CancellationToken ct)
    {
        async Task<byte> FeatureIndex(ushort feature)
        {
            var r = await dev.RequestAsync(idx, 0, 0, new[] { (byte)(feature >> 8), (byte)feature }, ct);
            return r?[4] ?? 0;
        }

        // Battery
        int? level = null; bool? charging = null;
        byte fi;
        if ((fi = await FeatureIndex(FeatUnifiedBattery)) != 0)
        {
            var r = await dev.RequestAsync(idx, fi, 1, Array.Empty<byte>(), ct);
            if (r != null)
            {
                int soc = r[4]; byte levelFlags = r[5]; byte chargeStatus = r[6];
                level = soc > 0 ? soc
                    : (levelFlags & 8) != 0 ? 100 : (levelFlags & 4) != 0 ? 50 : (levelFlags & 2) != 0 ? 20 : (levelFlags & 1) != 0 ? 5 : null;
                charging = chargeStatus is 1 or 2;
            }
        }
        else if ((fi = await FeatureIndex(FeatBatteryStatus)) != 0)
        {
            var r = await dev.RequestAsync(idx, fi, 0, Array.Empty<byte>(), ct);
            if (r != null)
            {
                // status: 0 discharging, 1 recharging, 2 almost full, 3 full, 4 slow recharge
                level = r[6] == 3 ? 100 : r[4];
                charging = r[6] is 1 or 2 or 4;
            }
        }
        else if ((fi = await FeatureIndex(FeatBatteryVoltage)) != 0)
        {
            var r = await dev.RequestAsync(idx, fi, 0, Array.Empty<byte>(), ct);
            if (r != null)
            {
                int mv = (r[4] << 8) | r[5];
                level = VoltageToPercent(mv);
                charging = (r[6] & 0x80) != 0;
            }
        }
        else return null; // no battery feature — e.g. a wired keyboard

        // Name
        string name = $"Logitech device {idx}";
        DeviceKind? kind = null;
        if ((fi = await FeatureIndex(FeatDeviceName)) != 0)
        {
            var countResp = await dev.RequestAsync(idx, fi, 0, Array.Empty<byte>(), ct);
            int count = countResp?[4] ?? 0;
            var sb = new StringBuilder();
            while (sb.Length < count)
            {
                var part = await dev.RequestAsync(idx, fi, 1, new[] { (byte)sb.Length }, ct);
                if (part == null) break;
                int take = Math.Min(16, count - sb.Length);
                sb.Append(Encoding.UTF8.GetString(part, 4, take).TrimEnd('\0'));
                if (part[4] == 0) break;
            }
            if (sb.Length > 0) name = sb.ToString().Trim();

            var typeResp = await dev.RequestAsync(idx, fi, 2, Array.Empty<byte>(), ct);
            if (typeResp != null)
                kind = typeResp[4] switch
                {
                    0 or 2 => DeviceKind.Keyboard,      // keyboard, numpad
                    3 or 5 => DeviceKind.Mouse,         // mouse, trackball
                    4 => DeviceKind.Touchpad,
                    8 => DeviceKind.Headset,
                    _ => DeviceKind.Generic,
                };
        }

        // Stable id: unit serial from DeviceInformation if available, else name.
        string id = "logi:" + name.ToLowerInvariant().Replace(' ', '-');
        if ((fi = await FeatureIndex(FeatDeviceInfo)) != 0)
        {
            var info = await dev.RequestAsync(idx, fi, 0, Array.Empty<byte>(), ct);
            if (info != null)
            {
                uint unit = (uint)(info[5] << 24 | info[6] << 16 | info[7] << 8 | info[8]);
                if (unit != 0) id = "logi:" + unit.ToString("X8");
            }
        }

        return new DeviceReading
        {
            Id = id,
            Name = name,
            Source = DeviceSource.Logitech,
            Kind = kind,
            Level = level is int l ? Math.Clamp(l, 0, 100) : null,
            IsCharging = charging,
            IsConnected = true,
        };
    }

    static readonly (int mv, int pct)[] VoltageCurve =
    {
        (4186, 100), (4067, 90), (3989, 80), (3922, 70), (3859, 60), (3811, 50),
        (3778, 40), (3751, 30), (3717, 20), (3671, 10), (3646, 5), (3579, 2), (3500, 0),
    };

    static int VoltageToPercent(int mv)
    {
        if (mv >= VoltageCurve[0].mv) return 100;
        for (int i = 1; i < VoltageCurve.Length; i++)
        {
            var (hiV, hiP) = VoltageCurve[i - 1];
            var (loV, loP) = VoltageCurve[i];
            if (mv >= loV) return loP + (hiP - loP) * (mv - loV) / (hiV - loV);
        }
        return 0;
    }

    /// <summary>Request/response channel over a HID++ long-report collection.</summary>
    sealed class HidppChannel : IDisposable
    {
        readonly FileStream _stream;
        readonly int _inLen, _outLen;

        HidppChannel(FileStream s, int inLen, int outLen) { _stream = s; _inLen = inLen; _outLen = outLen; }

        public static HidppChannel? Open(string path, HIDP_CAPS caps)
        {
            var h = Native.CreateFileW(path, Native.GENERIC_READ | Native.GENERIC_WRITE,
                Native.FILE_SHARE_READ | Native.FILE_SHARE_WRITE, IntPtr.Zero, Native.OPEN_EXISTING,
                Native.FILE_FLAG_OVERLAPPED, IntPtr.Zero);
            if (h.IsInvalid) { h.Dispose(); return null; }
            return new HidppChannel(new FileStream(h, FileAccess.ReadWrite, 0, true),
                caps.InputReportByteLength, caps.OutputReportByteLength);
        }

        byte[] BuildRequest(byte devIdx, byte featIdx, byte function, byte[] args)
        {
            var req = new byte[_outLen];
            req[0] = LongReportId; req[1] = devIdx; req[2] = featIdx; req[3] = (byte)((function << 4) | SwId);
            Array.Copy(args, 0, req, 4, Math.Min(args.Length, _outLen - 4));
            return req;
        }

        /// <summary>Sends the same request to several device indices and collects replies until the wait elapses.</summary>
        public async Task<Dictionary<byte, byte[]>> BroadcastAsync(byte[] indices, byte featIdx, byte function, byte[] args,
            TimeSpan wait, CancellationToken ct)
        {
            var replies = new Dictionary<byte, byte[]>();
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(wait);
            byte funcSw = (byte)((function << 4) | SwId);
            try
            {
                foreach (var idx in indices)
                    await _stream.WriteAsync(BuildRequest(idx, featIdx, function, args), timeout.Token);
                var buf = new byte[_inLen];
                while (replies.Count < indices.Length)
                {
                    int n = await _stream.ReadAsync(buf, timeout.Token);
                    if (n >= 7 && buf[0] == LongReportId && buf[2] == featIdx && buf[3] == funcSw && indices.Contains(buf[1]))
                        replies[buf[1]] = (byte[])buf.Clone();
                }
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested) { }
            catch (IOException) { }
            return replies;
        }

        /// <summary>Sends a request and waits for the matching reply. Returns null on error or timeout.</summary>
        public async Task<byte[]?> RequestAsync(byte devIdx, byte featIdx, byte function, byte[] args, CancellationToken ct)
        {
            var req = BuildRequest(devIdx, featIdx, function, args);

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromMilliseconds(2000));
            try
            {
                await _stream.WriteAsync(req, timeout.Token);
                var buf = new byte[_inLen];
                while (true)
                {
                    int n = await _stream.ReadAsync(buf, timeout.Token);
                    if (Verbose) Log.Write($"  rx {Convert.ToHexString(buf, 0, n)}");
                    if (n < 7 || buf[0] != LongReportId || buf[1] != devIdx) continue;
                    if (buf[2] == 0xFF && buf[3] == featIdx) return null;          // HID++ 2.0 error
                    if (buf[2] == 0x8F) return null;                                // HID++ 1.0 error (device offline etc.)
                    if (buf[2] == featIdx && buf[3] == req[3]) return (byte[])buf.Clone();
                }
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested) { return null; }
            catch (IOException) { return null; }
        }

        public void Dispose() => _stream.Dispose();
    }
}
