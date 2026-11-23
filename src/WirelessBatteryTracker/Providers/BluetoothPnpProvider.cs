using System.Text.RegularExpressions;
using Windows.Devices.Bluetooth;

namespace BatteryTray.Providers;

/// <summary>
/// Reads the battery level Windows itself collects for Bluetooth devices (the value shown in
/// Settings &gt; Bluetooth &amp; devices). Covers headphones/earbuds over Hands-Free (e.g. AirPods)
/// and many Bluetooth LE mice/keyboards.
/// </summary>
public sealed partial class BluetoothPnpProvider : IBatteryProvider
{
    public string Name => "Bluetooth (Windows)";

    [GeneratedRegex(@"[_&]([0-9A-F]{12})(?=[_\\]|$)", RegexOptions.IgnoreCase)]
    private static partial Regex AddressRegex();

    public async Task<IReadOnlyList<DeviceReading>> PollAsync(CancellationToken ct)
    {
        var names = new Dictionary<ulong, string>();
        var levels = new Dictionary<ulong, (int level, bool le, string fallbackName)>();

        foreach (var enumerator in new[] { "BTHENUM", "BTHLE", "BTHLEDEVICE", "BTHHFENUM" })
        {
            foreach (var id in Native.GetDeviceIds(enumerator))
            {
                var m = AddressRegex().Matches(id);
                if (m.Count == 0) continue;
                ulong addr = BtAddress.Parse(m[^1].Groups[1].Value);
                if (Native.Locate(id) is not uint inst) continue;

                bool isDeviceNode = id.Contains(@"\DEV_", StringComparison.OrdinalIgnoreCase);
                if (isDeviceNode)
                {
                    var n = Native.GetStringProperty(inst, Native.DEVPKEY_Device_FriendlyName)
                            ?? Native.GetStringProperty(inst, Native.DEVPKEY_NAME);
                    if (!string.IsNullOrWhiteSpace(n)) names[addr] = n!;
                }

                var data = Native.GetProperty(inst, Native.DEVPKEY_Bluetooth_Battery, out uint type);
                if (data is { Length: > 0 } && type == 0x3 /* DEVPROP_TYPE_BYTE */)
                {
                    int lvl = Math.Clamp((int)data[0], 0, 100);
                    bool le = enumerator.StartsWith("BTHLE", StringComparison.Ordinal);
                    var fallback = Native.GetStringProperty(inst, Native.DEVPKEY_Device_FriendlyName) ?? "Bluetooth device";
                    // Several child nodes may carry the value; keep the first one we found.
                    levels.TryAdd(addr, (lvl, le, fallback));
                }
            }
        }

        var result = new List<DeviceReading>();
        foreach (var (addr, info) in levels)
        {
            ct.ThrowIfCancellationRequested();
            var (connected, kind) = await QueryAsync(addr, info.le);
            result.Add(new DeviceReading
            {
                Id = BtAddress.ToId(addr),
                Name = names.TryGetValue(addr, out var n) ? n : CleanName(info.fallbackName),
                Source = info.le ? DeviceSource.BluetoothLE : DeviceSource.Bluetooth,
                Kind = kind,
                Level = info.level,
                IsConnected = connected,
            });
        }

        // Also report connected paired devices that Windows has no battery value for, so they show up
        // in Settings as detected (other providers, e.g. AirPods or DualSense, may add the level).
        foreach (var (selector, le) in new[]
                 {
                     (BluetoothDevice.GetDeviceSelectorFromPairingState(true), false),
                     (BluetoothLEDevice.GetDeviceSelectorFromPairingState(true), true),
                 })
        {
            Windows.Devices.Enumeration.DeviceInformationCollection infos;
            try
            {
                infos = await Windows.Devices.Enumeration.DeviceInformation.FindAllAsync(selector,
                    new[] { "System.Devices.Aep.IsConnected", "System.Devices.Aep.DeviceAddress" });
            }
            catch { continue; }
            foreach (var di in infos)
            {
                if (!(di.Properties.TryGetValue("System.Devices.Aep.IsConnected", out var c) && c is true)) continue;
                if (di.Properties.TryGetValue("System.Devices.Aep.DeviceAddress", out var a) is false || a is not string s) continue;
                ulong addr = BtAddress.Parse(s.Replace(":", "").Replace("-", ""));
                if (levels.ContainsKey(addr) || result.Any(r => r.Id == BtAddress.ToId(addr))) continue;
                var (_, kind) = await QueryAsync(addr, le);
                result.Add(new DeviceReading
                {
                    Id = BtAddress.ToId(addr),
                    Name = string.IsNullOrWhiteSpace(di.Name) ? "Bluetooth device" : di.Name,
                    Source = le ? DeviceSource.BluetoothLE : DeviceSource.Bluetooth,
                    Kind = kind,
                    Level = null,
                    IsConnected = true,
                });
            }
        }
        return result;
    }

    static string CleanName(string n) =>
        n.Replace(" Hands-Free AG", "").Replace(" Hands-Free", "").Replace(" Avrcp Transport", "").Trim();

    static async Task<(bool connected, DeviceKind? kind)> QueryAsync(ulong addr, bool le)
    {
        try
        {
            if (le)
            {
                using var d = await BluetoothLEDevice.FromBluetoothAddressAsync(addr);
                return (d?.ConnectionStatus == BluetoothConnectionStatus.Connected, BtKind.FromAppearance(d?.Appearance?.RawValue ?? 0));
            }
            using var c = await BluetoothDevice.FromBluetoothAddressAsync(addr);
            return (c?.ConnectionStatus == BluetoothConnectionStatus.Connected, BtKind.FromClassOfDevice(c?.ClassOfDevice?.RawValue ?? 0));
        }
        catch
        {
            return (true, null); // Windows only publishes the value while connected; trust it if WinRT fails.
        }
    }
}
