using Microsoft.Toolkit.Uwp.Notifications;

namespace BatteryTray;

/// <summary>Decides when to alert and shows Windows toast notifications.</summary>
public sealed class Notifier
{
    sealed class AlertState
    {
        public bool LowSent, CriticalSent, FullSent;
        public bool? WasConnected;
    }

    readonly Dictionary<string, AlertState> _state = new();
    readonly Action<string, string> _fallback;

    public Notifier(Action<string, string> fallback) => _fallback = fallback;

    public void Evaluate(DeviceState d, int criticalLevel)
    {
        var s = d.Settings;
        if (!_state.TryGetValue(s.Id, out var st)) _state[s.Id] = st = new AlertState();
        string name = s.DisplayName;

        if (st.WasConnected is bool was && was != d.IsConnected && s.NotifyConnection)
            Show(d.IsConnected ? $"{name} connected" : $"{name} disconnected",
                 d.IsConnected && d.Level is int cl ? $"Battery at {cl}%" : "");
        st.WasConnected = d.IsConnected;

        if (!d.IsConnected || d.Level is not int level) return;
        bool charging = d.IsCharging == true;

        if (charging || level > s.LowThreshold + 5) st.LowSent = false;
        if (charging || level > criticalLevel + 3) st.CriticalSent = false;
        if (level < 95) st.FullSent = false;

        if (s.NotifyLow && !charging)
        {
            if (level <= criticalLevel && !st.CriticalSent)
            {
                st.CriticalSent = st.LowSent = true;
                Show($"{name} battery critical", $"Only {level}% left — charge it now.");
            }
            else if (level <= s.LowThreshold && !st.LowSent)
            {
                st.LowSent = true;
                Show($"{name} battery low", $"{level}% remaining.");
            }
        }

        if (s.NotifyFull && level >= 100 && !st.FullSent && d.IsCharging != false)
        {
            st.FullSent = true;
            Show($"{name} fully charged", "You can unplug it now.");
        }
    }

    public void Show(string title, string message)
    {
        Log.Write($"Notify: {title} — {message}");
        try
        {
            var b = new ToastContentBuilder().AddText(title);
            if (!string.IsNullOrEmpty(message)) b.AddText(message);
            b.Show();
        }
        catch (Exception ex)
        {
            Log.Write("Toast failed, using balloon: " + ex.Message);
            _fallback(title, message);
        }
    }
}
