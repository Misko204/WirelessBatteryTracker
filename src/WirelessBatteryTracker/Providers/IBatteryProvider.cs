namespace BatteryTray.Providers;

public interface IBatteryProvider
{
    string Name { get; }
    /// <summary>Returns readings for devices this provider can currently see.</summary>
    Task<IReadOnlyList<DeviceReading>> PollAsync(CancellationToken ct);
}

internal static class BtAddress
{
    public static string ToId(ulong address) => "bt:" + address.ToString("X12");

    public static ulong Parse(string hex) => Convert.ToUInt64(hex, 16);
}

/// <summary>Maps Bluetooth device class / LE appearance codes to a device kind for the tray glyph.</summary>
internal static class BtKind
{
    public static DeviceKind? FromClassOfDevice(uint cod)
    {
        if (cod == 0) return null;
        uint major = (cod >> 8) & 0x1F, minor = (cod >> 2) & 0x3F;
        return major switch
        {
            2 => DeviceKind.Phone,
            4 => minor switch // audio/video
            {
                1 or 2 => DeviceKind.Headset,  // wearable headset, hands-free
                6 => DeviceKind.Headphones,
                5 or 7 => DeviceKind.Speaker,  // loudspeaker, portable audio
                _ => DeviceKind.Headphones,
            },
            5 => ((minor >> 4) & 3) switch // peripheral
            {
                1 => DeviceKind.Keyboard,
                2 => DeviceKind.Mouse,
                3 => DeviceKind.Keyboard,
                _ => (minor & 0xF) is 1 or 2 ? DeviceKind.Controller : (minor & 0xF) == 5 ? DeviceKind.Pen : DeviceKind.Generic,
            },
            _ => DeviceKind.Generic,
        };
    }

    public static DeviceKind? FromAppearance(ushort appearance)
    {
        if (appearance == 0) return null;
        int category = appearance >> 6, sub = appearance & 0x3F;
        return category switch
        {
            0x01 => DeviceKind.Phone,
            0x0F => sub switch // HID
            {
                1 => DeviceKind.Keyboard,
                2 => DeviceKind.Mouse,
                3 or 4 => DeviceKind.Controller,
                5 or 7 => DeviceKind.Pen,
                9 => DeviceKind.Touchpad,
                _ => DeviceKind.Generic,
            },
            0x25 => sub == 2 ? DeviceKind.Headset : DeviceKind.Headphones, // wearable audio (earbud/headset/headphones)
            0x21 => DeviceKind.Speaker,
            _ => DeviceKind.Generic,
        };
    }
}
