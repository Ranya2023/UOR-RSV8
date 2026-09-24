using System.Collections.Concurrent;
using System.Text;
using Windows.Devices.Bluetooth.Rfcomm;
using Windows.Networking.Sockets;
using Windows.Storage.Streams;

namespace Remco;

/// <summary>
/// Classic Bluetooth RFCOMM server using Windows' built-in Bluetooth stack.
/// Advertises a service with <see cref="ServiceUuid"/> so the phone can find
/// it on the paired PC. One phone at a time; a new connection replaces the old.
/// Protocol: one JSON object per line (UTF-8), both directions.
/// </summary>
internal sealed class BtServer : IDisposable
{
    /// <summary>Must match BtLink.SERVICE_UUID in the Android app.</summary>
    public static readonly Guid ServiceUuid = new("7a1c2e90-5b3d-4f6a-9c1e-2d4b8f0a6e31");
    private const string ServiceName = "UOR-RC";

    public event Action<string>? Log;
    public event Action<string>? LineReceived;
    /// <summary>(connected, deviceName)</summary>
    public event Action<bool, string>? ConnectionChanged;

    private RfcommServiceProvider? _provider;
    private StreamSocketListener? _listener;
    private StreamSocket? _socket;
    private BlockingCollection<string>? _outbox;
    private int _generation;

    public bool IsConnected => _socket != null;

    /// <summary>This PC's Bluetooth address ("AA:BB:CC:DD:EE:FF"), shared in Wi-Fi beacons so the phone knows both paths lead to the same PC.</summary>
    public string Address { get; private set; } = "";

    public async Task StartAsync()
    {
        try
        {
            var adapter = await global::Windows.Devices.Bluetooth.BluetoothAdapter.GetDefaultAsync();
            if (adapter != null)
            {
                string hex = adapter.BluetoothAddress.ToString("X12");
                Address = string.Join(":", Enumerable.Range(0, 6).Select(i => hex.Substring(i * 2, 2)));
            }
        }
        catch { }

        _provider = await RfcommServiceProvider.CreateAsync(RfcommServiceId.FromUuid(ServiceUuid));

        _listener = new StreamSocketListener();
        _listener.ConnectionReceived += OnConnectionReceived;
        await _listener.BindServiceNameAsync(
            _provider.ServiceId.AsString(),
            SocketProtectionLevel.BluetoothEncryptionAllowNullAuthentication);

        // SDP "Service Name" attribute (0x100), so the service shows a friendly name.
        var w = new DataWriter();
        byte[] nameBytes = Encoding.UTF8.GetBytes(ServiceName);
        w.WriteByte((4 << 3) | 5); // type = text string, size = next byte holds length
        w.WriteByte((byte)nameBytes.Length);
        w.WriteBytes(nameBytes);
        _provider.SdpRawAttributes.Add(0x100, w.DetachBuffer());

        _provider.StartAdvertising(_listener, true);
        Log?.Invoke("Bluetooth service is advertising. Waiting for the phone…");
    }

    private void OnConnectionReceived(StreamSocketListener sender, StreamSocketListenerConnectionReceivedEventArgs args)
    {
        // Replace any previous phone.
        DropCurrent();

        var socket = args.Socket;
        int gen = Interlocked.Increment(ref _generation);
        _socket = socket;
        var outbox = new BlockingCollection<string>(new ConcurrentQueue<string>());
        _outbox = outbox;

        string name = "phone";
        try { name = socket.Information.RemoteHostName.DisplayName; } catch { }

        ConnectionChanged?.Invoke(true, name);
        Log?.Invoke($"Phone connected: {name}");

        // Writer
        var output = socket.OutputStream.AsStreamForWrite();
        var writerThread = new Thread(() =>
        {
            try
            {
                foreach (var line in outbox.GetConsumingEnumerable())
                {
                    byte[] bytes = Encoding.UTF8.GetBytes(line + "\n");
                    output.Write(bytes, 0, bytes.Length);
                    output.Flush();
                }
            }
            catch (Exception ex)
            {
                if (gen == _generation) Log?.Invoke("Send failed: " + ex.Message);
            }
        }) { IsBackground = true, Name = "bt-writer" };
        writerThread.Start();

        // Reader (runs on this callback's thread pool thread)
        _ = Task.Run(async () =>
        {
            try
            {
                using var reader = new StreamReader(socket.InputStream.AsStreamForRead(), Encoding.UTF8);
                while (gen == _generation)
                {
                    string? line = await reader.ReadLineAsync();
                    if (line == null) break;
                    if (line.Length > 0) LineReceived?.Invoke(line);
                }
            }
            catch (Exception ex)
            {
                if (gen == _generation) Log?.Invoke("Connection closed: " + ex.Message);
            }
            if (gen == _generation)
            {
                DropCurrent();
                ConnectionChanged?.Invoke(false, name);
                Log?.Invoke("Phone disconnected. Waiting for it to reconnect…");
            }
        });
    }

    public void Send(string json)
    {
        var box = _outbox;
        if (box == null || box.IsAddingCompleted) return;
        try { box.Add(json); } catch (InvalidOperationException) { }
    }

    private void DropCurrent()
    {
        Interlocked.Increment(ref _generation);
        try { _outbox?.CompleteAdding(); } catch { }
        _outbox = null;
        try { _socket?.Dispose(); } catch { }
        _socket = null;
    }

    public void Dispose()
    {
        DropCurrent();
        try { _provider?.StopAdvertising(); } catch { }
        try { _listener?.Dispose(); } catch { }
    }
}
