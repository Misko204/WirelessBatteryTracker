using System.Text.Json.Serialization;

namespace BatteryTray;

public enum DeviceSource { Bluetooth, BluetoothLE, Logitech, Xbox, PlayStation, Apple }

public enum DeviceKind { Generic, Mouse, Keyboard, Headphones, Headset, Controller, Speaker, Touchpad, Pen, Phone }

/// <summary>One battery reading produced by a provider.</summary>
public sealed class DeviceReading
{
    public required string Id { get; init; }
    public required string Name { get; set; }
    public DeviceSource Source { get; set; }
    public DeviceKind? Kind { get; set; }
    public int? Level { get; set; }
    public bool IsConnected { get; set; } = true;
    public bool? IsCharging { get; set; }
    /// <summary>True when the device only reports coarse levels (e.g. Xbox: empty/low/medium/full).</summary>
    public bool IsApproximate { get; init; }
    /// <summary>Extra detail, e.g. AirPods "L 80% · R 70% · Case 40%".</summary>
    public string? Detail { get; set; }
}

public enum DisplayMode { OwnIcon, Grouped, Hidden }

/// <summary>Inline = items embedded in the taskbar; TrayIcons = one square notification-area icon per device.</summary>
public enum TaskbarMode { Inline, TrayIcons }

public sealed class DeviceSettings
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string? CustomName { get; set; }
    public DeviceSource Source { get; set; }
    /// <summary>Stable number used for this device's tray icon ID (keeps its position across restarts).</summary>
    public int TrayId { get; set; }
    public DeviceKind? DetectedKind { get; set; }
    public DeviceKind? KindOverride { get; set; }
    public DisplayMode Display { get; set; } = DisplayMode.OwnIcon;
    public bool HideWhenDisconnected { get; set; } = true;
    public int LowThreshold { get; set; } = 20;
    public bool NotifyLow { get; set; } = true;
    public bool NotifyFull { get; set; } = false;
    public bool NotifyConnection { get; set; } = false;
    public int? LastLevel { get; set; }
    public DateTime? LastSeen { get; set; }

    [JsonIgnore] public string DisplayName => string.IsNullOrWhiteSpace(CustomName) ? Name : CustomName!;
    [JsonIgnore] public DeviceKind Kind => KindOverride ?? DetectedKind ?? DeviceKind.Generic;
}

public sealed class AppSettings
{
    public int PollSeconds { get; set; } = 30;
    public TaskbarMode TaskbarMode { get; set; } = TaskbarMode.Inline;
    public bool ShowPercentSign { get; set; } = true;
    public bool KeepIconsVisible { get; set; } = true;
    public bool EnableBluetooth { get; set; } = true;
    public bool EnableBleGatt { get; set; } = true;
    public bool EnableApple { get; set; } = true;
    public bool EnablePlayStation { get; set; } = true;
    public bool EnableLogitech { get; set; } = true;
    public bool EnableXbox { get; set; } = true;
    public int CriticalLevel { get; set; } = 5;
    public List<DeviceSettings> Devices { get; set; } = new();
}

/// <summary>Live state of a known device, combining settings with the latest reading.</summary>
public sealed class DeviceState
{
    public required DeviceSettings Settings { get; init; }
    public int? Level { get; set; }
    public bool IsConnected { get; set; }
    public bool? IsCharging { get; set; }
    public bool IsApproximate { get; set; }
    public string? Detail { get; set; }

    public string Describe()
    {
        if (!IsConnected)
            return Level is int l ? $"Disconnected · last {l}%" : "Disconnected";
        if (Level is not int lvl) return "Connected · no battery info";
        var s = IsApproximate ? $"About {lvl}%" : $"{lvl}%";
        if (IsCharging == true) s += " · charging";
        return s;
    }
}
