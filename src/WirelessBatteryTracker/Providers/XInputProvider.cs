using System.Runtime.InteropServices;

namespace BatteryTray.Providers;

/// <summary>Xbox / XInput controllers (wireless adapter or Bluetooth). Windows reports only 4 coarse levels.</summary>
public sealed class XInputProvider : IBatteryProvider
{
    public string Name => "Xbox controllers";

    [StructLayout(LayoutKind.Sequential)]
    struct XINPUT_BATTERY_INFORMATION { public byte BatteryType; public byte BatteryLevel; }

    [StructLayout(LayoutKind.Sequential)]
    struct XINPUT_STATE { public uint dwPacketNumber; public ushort wButtons; public byte bLeftTrigger, bRightTrigger; public short sThumbLX, sThumbLY, sThumbRX, sThumbRY; }

    [DllImport("xinput1_4.dll")] static extern int XInputGetState(int userIndex, out XINPUT_STATE state);
    [DllImport("xinput1_4.dll")] static extern int XInputGetBatteryInformation(int userIndex, byte devType, out XINPUT_BATTERY_INFORMATION info);

    const byte BATTERY_TYPE_DISCONNECTED = 0, BATTERY_TYPE_WIRED = 1;

    public Task<IReadOnlyList<DeviceReading>> PollAsync(CancellationToken ct)
    {
        var result = new List<DeviceReading>();
        try
        {
            for (int i = 0; i < 4; i++)
            {
                if (XInputGetState(i, out _) != 0) continue;
                if (XInputGetBatteryInformation(i, 0, out var info) != 0) continue;
                if (info.BatteryType is BATTERY_TYPE_DISCONNECTED or BATTERY_TYPE_WIRED) continue;
                result.Add(new DeviceReading
                {
                    Id = "xinput:" + i,
                    Name = $"Xbox controller {i + 1}",
                    Source = DeviceSource.Xbox,
                    Kind = DeviceKind.Controller,
                    Level = info.BatteryLevel switch { 0 => 5, 1 => 25, 2 => 60, _ => 100 },
                    IsApproximate = true,
                });
            }
        }
        catch (DllNotFoundException) { }
        return Task.FromResult<IReadOnlyList<DeviceReading>>(result);
    }
}
