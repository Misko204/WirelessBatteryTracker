using System.Collections.Concurrent;
using System.Text.RegularExpressions;
using Windows.Devices.Bluetooth;
using Windows.Devices.Bluetooth.Advertisement;
using Windows.Storage.Streams;

namespace BatteryTray.Providers;

/// <summary>
/// AirPods / Beats battery. Windows doesn't read it, but AirPods continuously broadcast their
/// battery (left, right, case, in 10% steps) in Apple "proximity pairing" Bluetooth LE adverts.
/// We listen to those adverts and match them to your paired, connected Apple headphones by model ID.
/// </summary>
public sealed partial class AppleProvider : IBatteryProvider, IDisposable
{
    public string Name => "AirPods / Beats";

    const ushort AppleCompanyId = 0x004C;

    sealed record Advert(int Left, int Right, int Case, bool LeftCharging, bool RightCharging, bool CaseCharging, short Rssi, DateTime Seen);

    readonly ConcurrentDictionary<ushort, Advert> _byModel = new();
    BluetoothLEAdvertisementWatcher? _watcher;

    /// <summary>Raised (at most every few seconds) when adverts from a new model start arriving.</summary>
    public event Action? NewDeviceHeard;

    [GeneratedRegex(@"VID&0001004C_PID&([0-9A-F]{4})\\.*?([0-9A-F]{12})_C", RegexOptions.IgnoreCase)]
    private static partial Regex AppleServiceNode();

    void EnsureWatcher()
    {
        if (_watcher != null) return;
        _watcher = new BluetoothLEAdvertisementWatcher { ScanningMode = BluetoothLEScanningMode.Passive };
        _watcher.Received += OnReceived;
        _watcher.Stopped += (_, e) => Log.Write($"AirPods advert watcher stopped: {e.Error}");
        _watcher.Start();
    }

    void OnReceived(BluetoothLEAdvertisementWatcher sender, BluetoothLEAdvertisementReceivedEventArgs args)
    {
        foreach (var md in args.Advertisement.ManufacturerData)
        {
            if (md.CompanyId != AppleCompanyId || md.Data.Length < 9) continue;
            var d = new byte[md.Data.Length];
            DataReader.FromBuffer(md.Data).ReadBytes(d);
            if (d[0] != 0x07 || d[1] < 0x09) continue; // 0x07 = proximity pairing message

            ushort model = (ushort)(d[3] | (d[4] << 8));
            int statusHi = d[5] >> 4;
            bool flipped = (statusHi & 0x02) == 0;
            int b1 = d[6] >> 4, b2 = d[6] & 0x0F;
            int charge = d[7] >> 4, caseLevel = d[7] & 0x0F;
            int left = flipped ? b1 : b2, right = flipped ? b2 : b1;
            static int Pct(int v) => v <= 10 ? v * 10 : -1; // 15 = not available

            var ad = new Advert(Pct(left), Pct(right), Pct(caseLevel),
                (charge & (flipped ? 0b0010 : 0b0001)) != 0,
                (charge & (flipped ? 0b0001 : 0b0010)) != 0,
                (charge & 0b0100) != 0,
                args.RawSignalStrengthInDBm, DateTime.UtcNow);

            // Keep the strongest recent signal per model (yours are closest to the PC).
            bool isNew = !_byModel.TryGetValue(model, out var prev);
            if (isNew || prev!.Rssi <= ad.Rssi + 6 || (DateTime.UtcNow - prev.Seen).TotalSeconds > 20)
                _byModel[model] = ad;
            if (isNew)
            {
                Log.Write($"AirPods/Beats broadcast heard: model {model:X4}, L {ad.Left} R {ad.Right} case {ad.Case}, RSSI {ad.Rssi}");
                NewDeviceHeard?.Invoke();
            }
        }
    }

    public async Task<IReadOnlyList<DeviceReading>> PollAsync(CancellationToken ct)
    {
        EnsureWatcher();
        var result = new List<DeviceReading>();

        // Paired Apple audio devices: the Bluetooth service nodes carry the Apple model ID as the PID.
        var found = new Dictionary<ulong, ushort>();
        foreach (var id in Native.GetDeviceIds("BTHENUM"))
        {
            var m = AppleServiceNode().Match(id);
            if (!m.Success) continue;
            found[BtAddress.Parse(m.Groups[2].Value)] = Convert.ToUInt16(m.Groups[1].Value, 16);
        }

        foreach (var (addr, model) in found)
        {
            ct.ThrowIfCancellationRequested();
            using var dev = await BluetoothDevice.FromBluetoothAddressAsync(addr);
            if (dev == null || dev.ConnectionStatus != BluetoothConnectionStatus.Connected) continue;
            if (!_byModel.TryGetValue(model, out var ad) || (DateTime.UtcNow - ad.Seen).TotalSeconds > 120) continue;

            var buds = new[] { ad.Left, ad.Right }.Where(v => v >= 0).ToList();
            int? level = buds.Count > 0 ? buds.Min() : ad.Case >= 0 ? ad.Case : null;
            var parts = new List<string>();
            if (ad.Left >= 0) parts.Add($"L {ad.Left}%{(ad.LeftCharging ? " ⚡" : "")}");
            if (ad.Right >= 0) parts.Add($"R {ad.Right}%{(ad.RightCharging ? " ⚡" : "")}");
            if (ad.Case >= 0) parts.Add($"Case {ad.Case}%{(ad.CaseCharging ? " ⚡" : "")}");

            result.Add(new DeviceReading
            {
                Id = BtAddress.ToId(addr),
                Name = dev.Name,
                Source = DeviceSource.Apple,
                Kind = DeviceKind.Headphones,
                Level = level,
                IsCharging = buds.Count > 0 ? (ad.LeftCharging || ad.Left < 0) && (ad.RightCharging || ad.Right < 0) : ad.CaseCharging,
                Detail = string.Join(" · ", parts),
            });
        }
        return result;
    }

    public void Dispose()
    {
        try { _watcher?.Stop(); } catch { }
    }
}
