using Windows.Devices.Enumeration;
using Windows.Media.Audio;

namespace Remco;

/// <summary>
/// 🔈 "Sound only on the PC": the PC becomes the phone's Bluetooth speaker
/// (Windows 10 2004+ "Bluetooth audio receiver"). Android then plays its media
/// sound through the PC and the phone's own speaker is silent — which works on
/// every phone, unlike muting (some phones also mute what they send).
/// The phone must be paired with this PC over Bluetooth.
/// </summary>
internal sealed class BtSpeaker : IDisposable
{
    private readonly Dictionary<string, DeviceInformation> _devices = new();
    private readonly object _lock = new();
    private DeviceWatcher? _watcher;
    private AudioPlaybackConnection? _conn;
    public event Action<bool>? StateChanged;

    public bool IsOn => _conn != null && _conn.State == AudioPlaybackConnectionState.Opened;

    public void StartWatching()
    {
        if (_watcher != null) return;
        try
        {
            _watcher = DeviceInformation.CreateWatcher(AudioPlaybackConnection.GetDeviceSelector());
            _watcher.Added += (_, d) => { lock (_lock) _devices[d.Id] = d; };
            _watcher.Removed += (_, u) => { lock (_lock) _devices.Remove(u.Id); };
            _watcher.Start();
        }
        catch { _watcher = null; }
    }

    /// <summary>Paired phones that can send their sound to this PC.</summary>
    public List<string> DeviceNames() { lock (_lock) return _devices.Values.Select(d => d.Name).ToList(); }

    public async Task<(bool ok, string message)> OpenAsync(string phoneName)
    {
        StartWatching();
        await Task.Delay(300);   // let the watcher list the paired phones
        DeviceInformation? dev;
        lock (_lock)
        {
            var all = _devices.Values.ToList();
            dev = all.FirstOrDefault(d => phoneName.Length > 0 && string.Equals(d.Name, phoneName, StringComparison.OrdinalIgnoreCase))
               ?? all.FirstOrDefault(d => phoneName.Length > 0 && d.Name.Contains(phoneName, StringComparison.OrdinalIgnoreCase))
               ?? (all.Count == 1 ? all[0] : null);
            if (dev == null && all.Count > 1) dev = all[0];
        }
        if (dev == null) return (false, "no-paired-phone");
        try
        {
            Close();
            var conn = AudioPlaybackConnection.TryCreateFromId(dev.Id);
            if (conn == null) return (false, "not-supported");
            conn.StateChanged += (_, _) => StateChanged?.Invoke(IsOn);
            await conn.StartAsync();
            var r = await conn.OpenAsync();
            if (r.Status != AudioPlaybackConnectionOpenResultStatus.Success)
            {
                conn.Dispose();
                return (false, r.Status.ToString());
            }
            _conn = conn;
            StateChanged?.Invoke(true);
            return (true, dev.Name);
        }
        catch (Exception ex) { return (false, ex.Message); }
    }

    public void Close()
    {
        var c = _conn;
        _conn = null;
        if (c != null) { try { c.Dispose(); } catch { } StateChanged?.Invoke(false); }
    }

    public void Dispose()
    {
        Close();
        try { _watcher?.Stop(); } catch { }
    }
}
