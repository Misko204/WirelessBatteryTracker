using BatteryTray.Providers;
using Windows.Devices.Bluetooth;
using Windows.Devices.Enumeration;

namespace BatteryTray;

public sealed class PollResult
{
    public List<DeviceReading> Readings { get; } = new();
    /// <summary>Sources whose providers ran without error; known devices from these sources that are missing are offline.</summary>
    public HashSet<DeviceSource> PolledSources { get; } = new();
}

/// <summary>
/// Polls all enabled providers on a background thread and raises <see cref="Updated"/> on the UI thread.
/// Also re-polls right away when Bluetooth devices connect/disconnect or USB devices are plugged in.
/// </summary>
public sealed class DeviceMonitor : IDisposable
{
    readonly Func<AppSettings> _settings;
    readonly SynchronizationContext? _ui;
    readonly CancellationTokenSource _cts = new();
    readonly SemaphoreSlim _wake = new(0, 1);
    readonly List<DeviceWatcher> _watchers = new();
    DateTime _lastWakeRequest;

    readonly BluetoothPnpProvider _bt = new();
    readonly BleGattProvider _gatt = new();
    readonly AppleProvider _apple = new();
    readonly PlayStationProvider _ps = new();
    readonly LogitechHidppProvider _logi = new();
    readonly XInputProvider _xbox = new();

    public event Action<PollResult>? Updated;

    public DeviceMonitor(Func<AppSettings> settings)
    {
        _settings = settings;
        _ui = SynchronizationContext.Current;
        _apple.NewDeviceHeard += () => RefreshSoon();
    }

    public void Start()
    {
        StartConnectionWatchers();
        _ = Task.Run(LoopAsync);
    }

    /// <summary>Poll now.</summary>
    public void RefreshNow()
    {
        try { if (_wake.CurrentCount == 0) _wake.Release(); } catch (SemaphoreFullException) { }
    }

    /// <summary>Poll after a short delay, coalescing bursts of device events.</summary>
    public void RefreshSoon()
    {
        var stamp = _lastWakeRequest = DateTime.UtcNow;
        _ = Task.Delay(1500).ContinueWith(_ => { if (_lastWakeRequest == stamp) RefreshNow(); });
    }

    void StartConnectionWatchers()
    {
        foreach (var selector in new[]
                 {
                     BluetoothDevice.GetDeviceSelectorFromPairingState(true),
                     BluetoothLEDevice.GetDeviceSelectorFromPairingState(true),
                 })
        {
            try
            {
                var w = DeviceInformation.CreateWatcher(selector, new[] { "System.Devices.Aep.IsConnected" },
                    DeviceInformationKind.AssociationEndpoint);
                bool enumerated = false;
                w.EnumerationCompleted += (_, _) => enumerated = true;
                w.Added += (_, _) => { if (enumerated) RefreshSoon(); };
                w.Removed += (_, _) => RefreshSoon();
                w.Updated += (_, u) =>
                {
                    if (u.Properties.ContainsKey("System.Devices.Aep.IsConnected")) RefreshSoon();
                };
                w.Start();
                _watchers.Add(w);
            }
            catch (Exception ex) { Log.Write("Device watcher failed: " + ex.Message); }
        }
    }

    async Task LoopAsync()
    {
        while (!_cts.IsCancellationRequested)
        {
            try
            {
                var result = await PollOnceAsync(_cts.Token);
                if (_ui != null) _ui.Post(_ => Updated?.Invoke(result), null);
                else Updated?.Invoke(result);
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex) { Log.Write("Poll failed: " + ex); }

            try { await _wake.WaitAsync(TimeSpan.FromSeconds(Math.Max(10, _settings().PollSeconds)), _cts.Token); }
            catch (OperationCanceledException) { break; }
        }
    }

    public async Task<PollResult> PollOnceAsync(CancellationToken ct)
    {
        var s = _settings();
        var result = new PollResult();

        async Task Run(IBatteryProvider p, params DeviceSource[] sources)
        {
            try
            {
                var readings = await p.PollAsync(ct).WaitAsync(TimeSpan.FromSeconds(45), ct);
                foreach (var r in readings)
                {
                    var i = result.Readings.FindIndex(x => x.Id == r.Id);
                    if (i < 0) { result.Readings.Add(r); continue; }
                    var existing = result.Readings[i];
                    // Prefer the reading that actually has a battery level; keep the better name/kind.
                    if (existing.Level == null && r.Level != null)
                    {
                        r.Kind ??= existing.Kind;
                        result.Readings[i] = r;
                    }
                    else existing.Kind ??= r.Kind;
                }
                foreach (var src in sources) result.PolledSources.Add(src);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception ex) { Log.Write($"{p.Name} failed: {ex.Message}"); }
        }

        if (s.EnableBluetooth) await Run(_bt, DeviceSource.Bluetooth, DeviceSource.BluetoothLE);
        if (s.EnableApple) await Run(_apple, DeviceSource.Apple);
        if (s.EnablePlayStation) await Run(_ps, DeviceSource.PlayStation);
        if (s.EnableBleGatt) await Run(_gatt, DeviceSource.BluetoothLE);
        if (s.EnableLogitech) await Run(_logi, DeviceSource.Logitech);
        if (s.EnableXbox) await Run(_xbox, DeviceSource.Xbox);
        return result;
    }

    public void Dispose()
    {
        _cts.Cancel();
        foreach (var w in _watchers) { try { w.Stop(); } catch { } }
        _apple.Dispose();
        _cts.Dispose();
    }
}
