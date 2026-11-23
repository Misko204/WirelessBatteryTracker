using System.Text.Json;
using System.Text.Json.Serialization;

namespace BatteryTray;

public static class SettingsStore
{
    public static readonly string Folder = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "WirelessBatteryTracker");
    static readonly string FilePath = Path.Combine(Folder, "settings.json");

    static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    public static AppSettings Load()
    {
        try
        {
            // Earlier builds were called "Battery Tray"; pick up their settings once.
            var legacy = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "BatteryTray", "settings.json");
            var source = File.Exists(FilePath) ? FilePath : File.Exists(legacy) ? legacy : null;
            if (source != null)
                return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(source), Options) ?? new AppSettings();
        }
        catch (Exception ex) { Log.Write("Failed to load settings: " + ex.Message); }
        return new AppSettings();
    }

    public static void Save(AppSettings settings)
    {
        try
        {
            Directory.CreateDirectory(Folder);
            var tmp = FilePath + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(settings, Options));
            File.Move(tmp, FilePath, overwrite: true);
        }
        catch (Exception ex) { Log.Write("Failed to save settings: " + ex.Message); }
    }
}

public static class Log
{
    static readonly object Gate = new();
    static readonly string FilePath = Path.Combine(SettingsStore.Folder, "log.txt");

    public static void Write(string message)
    {
        lock (Gate)
        {
            try
            {
                Directory.CreateDirectory(SettingsStore.Folder);
                var fi = new FileInfo(FilePath);
                if (fi.Exists && fi.Length > 512 * 1024) fi.Delete();
                File.AppendAllText(FilePath, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {message}{Environment.NewLine}");
            }
            catch { /* logging must never crash the app */ }
        }
    }
}
