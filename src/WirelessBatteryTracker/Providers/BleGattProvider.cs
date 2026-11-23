using Windows.Devices.Bluetooth;
using Windows.Devices.Bluetooth.GenericAttributeProfile;
using Windows.Devices.Enumeration;
using Windows.Storage.Streams;

namespace BatteryTray.Providers;

/// <summary>
/// Reads the standard GATT Battery Service (0x180F) from paired, connected Bluetooth LE devices
/// that Windows does not already report a level for.
/// </summary>
public sealed class BleGattProvider : IBatteryProvider
{
    public string Name => "Bluetooth LE (GATT)";

    public async Task<IReadOnlyList<DeviceReading>> PollAsync(CancellationToken ct)
    {
        var result = new List<DeviceReading>();
        var selector = BluetoothLEDevice.GetDeviceSelectorFromPairingState(true);
        var infos = await DeviceInformation.FindAllAsync(selector, new[] { "System.Devices.Aep.IsConnected" });

        foreach (var info in infos)
        {
            ct.ThrowIfCancellationRequested();
            if (info.Properties.TryGetValue("System.Devices.Aep.IsConnected", out var c) && c is bool connected && !connected)
                continue;
            try
            {
                var reading = await ReadAsync(info.Id).WaitAsync(TimeSpan.FromSeconds(8), ct);
                if (reading != null) result.Add(reading);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception ex) { Log.Write($"GATT read failed for {info.Name}: {ex.Message}"); }
        }
        return result;
    }

    static async Task<DeviceReading?> ReadAsync(string deviceId)
    {
        using var dev = await BluetoothLEDevice.FromIdAsync(deviceId);
        if (dev == null || dev.ConnectionStatus != BluetoothConnectionStatus.Connected) return null;

        var services = await dev.GetGattServicesForUuidAsync(GattServiceUuids.Battery, BluetoothCacheMode.Uncached);
        if (services.Status != GattCommunicationStatus.Success || services.Services.Count == 0) return null;
        try
        {
            var svc = services.Services[0];
            var chars = await svc.GetCharacteristicsForUuidAsync(GattCharacteristicUuids.BatteryLevel, BluetoothCacheMode.Uncached);
            if (chars.Status != GattCommunicationStatus.Success || chars.Characteristics.Count == 0) return null;
            var value = await chars.Characteristics[0].ReadValueAsync(BluetoothCacheMode.Uncached);
            if (value.Status != GattCommunicationStatus.Success || value.Value.Length < 1) return null;
            int level = DataReader.FromBuffer(value.Value).ReadByte();
            return new DeviceReading
            {
                Id = BtAddress.ToId(dev.BluetoothAddress),
                Name = dev.Name,
                Source = DeviceSource.BluetoothLE,
                Kind = BtKind.FromAppearance(dev.Appearance?.RawValue ?? 0),
                Level = Math.Clamp(level, 0, 100),
                IsConnected = true,
            };
        }
        finally
        {
            foreach (var s in services.Services) s.Dispose();
        }
    }
}
