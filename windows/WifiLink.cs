using System.Collections.Concurrent;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Remco;

/// <summary>
/// Wi-Fi link (laptop hotspot, phone hotspot or any shared network).
///
/// To avoid Windows Firewall prompts entirely, the PC never listens:
///   1. The PC broadcasts a small UDP "beacon" every second (outbound).
///   2. The phone answers with proof that it knows this PC's PIN
///      (Windows allows unicast answers to our own broadcasts).
///   3. The PC then opens an outbound TCP connection to the phone.
/// After that it's the same one-JSON-per-line protocol as Bluetooth.
/// </summary>
internal sealed class WifiLink : IDisposable
{
    public const int BeaconPort = 47801;

    public event Action<string>? Log;
    public event Action<string>? LineReceived;
    public event Action<bool, string>? ConnectionChanged;

    private readonly Settings _settings;
    public string BtAddress { get; set; } = "";

    private UdpClient? _udp;
    private volatile bool _running;
    private TcpClient? _client;
    private BlockingCollection<string>? _outbox;
    private int _gen;
    private int _connecting;
    private readonly LinkedList<string> _nonces = new();
    private readonly object _lock = new();

    public bool IsConnected => _client != null;
    public string PeerName { get; private set; } = "";

    public WifiLink(Settings settings) { _settings = settings; }

    public void Start()
    {
        _udp = new UdpClient(new IPEndPoint(IPAddress.Any, 0)) { EnableBroadcast = true };
        _running = true;
        new Thread(BeaconLoop) { IsBackground = true, Name = "wifi-beacon" }.Start();
        new Thread(ReceiveLoop) { IsBackground = true, Name = "wifi-replies" }.Start();
        Log?.Invoke("Wi-Fi: looking for the phone on " + string.Join(", ", LocalAddresses().Select(a => a.ToString())));
    }

    /// <summary>IPv4 addresses of this PC (for display).</summary>
    public static List<IPAddress> LocalAddresses()
    {
        var list = new List<IPAddress>();
        try
        {
            foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (ni.OperationalStatus != OperationalStatus.Up || ni.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
                foreach (var ua in ni.GetIPProperties().UnicastAddresses)
                    if (ua.Address.AddressFamily == AddressFamily.InterNetwork) list.Add(ua.Address);
            }
        }
        catch { }
        return list;
    }

    private static List<IPAddress> BroadcastTargets()
    {
        var list = new List<IPAddress> { IPAddress.Broadcast };
        try
        {
            foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (ni.OperationalStatus != OperationalStatus.Up || ni.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
                foreach (var ua in ni.GetIPProperties().UnicastAddresses)
                {
                    if (ua.Address.AddressFamily != AddressFamily.InterNetwork || ua.IPv4Mask == null) continue;
                    byte[] ip = ua.Address.GetAddressBytes(), mask = ua.IPv4Mask.GetAddressBytes();
                    if (mask.All(b => b == 0)) continue;
                    // On a phone hotspot the phone is this PC's gateway — beacon it directly too.
                    foreach (var gw in ni.GetIPProperties().GatewayAddresses)
                        if (gw.Address.AddressFamily == AddressFamily.InterNetwork && !gw.Address.Equals(IPAddress.Any)) list.Add(gw.Address);
                    var bc = new byte[4];
                    for (int i = 0; i < 4; i++) bc[i] = (byte)(ip[i] | ~mask[i]);
                    list.Add(new IPAddress(bc));
                }
            }
        }
        catch { }
        return list;
    }

    private void BeaconLoop()
    {
        while (_running)
        {
            try
            {
                string nonce = Convert.ToHexString(RandomNumberGenerator.GetBytes(6)).ToLowerInvariant();
                lock (_lock)
                {
                    _nonces.AddFirst(nonce);
                    while (_nonces.Count > 12) _nonces.RemoveLast();
                }
                var beacon = JsonSerializer.Serialize(new
                {
                    remco = 1, type = "beacon", id = _settings.Id, name = Environment.MachineName,
                    bt = BtAddress, nonce, connected = IsConnected
                });
                byte[] data = Encoding.UTF8.GetBytes(beacon);
                var targets = BroadcastTargets();
                if (IPAddress.TryParse(_settings.PhoneIp, out var manual)) targets.Add(manual);
                foreach (var t in targets.Distinct())
                {
                    try { _udp?.Send(data, data.Length, new IPEndPoint(t, BeaconPort)); } catch { }
                }
            }
            catch { }
            Thread.Sleep(1000);
        }
    }

    private void ReceiveLoop()
    {
        while (_running)
        {
            try
            {
                IPEndPoint? ep = null;
                byte[] data = _udp!.Receive(ref ep);
                if (ep == null) continue;
                using var doc = JsonDocument.Parse(data);
                var m = doc.RootElement;
                if (Str(m, "type") != "reply" || Str(m, "id") != _settings.Id) continue;
                string proof = Str(m, "proof");
                bool ok;
                lock (_lock) ok = _nonces.Any(n => Proof(_settings.Pin, n) == proof);
                if (!ok) continue;
                if (IsConnected || Interlocked.CompareExchange(ref _connecting, 1, 0) != 0) continue;
                int port = m.TryGetProperty("port", out var pv) && pv.TryGetInt32(out int p) ? p : 47802;
                string phone = Str(m, "phone");
                string pnonce = Str(m, "pnonce");
                var addr = ep.Address;
                new Thread(() => Connect(addr, port, phone, pnonce)) { IsBackground = true }.Start();
            }
            catch (ObjectDisposedException) { break; }
            catch (SocketException) { if (!_running) break; }
            catch { }
        }
    }

    public static string Proof(string pin, string nonce)
    {
        byte[] h = SHA256.HashData(Encoding.UTF8.GetBytes(pin + ":" + nonce));
        return Convert.ToHexString(h).ToLowerInvariant()[..16];
    }

    private void Connect(IPAddress addr, int port, string phone, string pnonce)
    {
        TcpClient? c = null;
        try
        {
            c = new TcpClient { NoDelay = true };
            var task = c.ConnectAsync(addr, port);
            if (!task.Wait(3000) || !c.Connected) { c.Dispose(); return; }
            c.ReceiveTimeout = 8000;  // phone pings every 2 s; silence = Wi-Fi lost
            var stream = c.GetStream();
            var hello = JsonSerializer.Serialize(new
            {
                e = "pc_hello", id = _settings.Id, name = Environment.MachineName, bt = BtAddress,
                proof = Proof(_settings.Pin, pnonce)
            }) + "\n";
            byte[] hb = Encoding.UTF8.GetBytes(hello);
            stream.Write(hb, 0, hb.Length);
            stream.Flush();
            Session(c, stream, $"{(phone.Length > 0 ? phone : "phone")} ({addr})");
            c = null; // owned by Session now
        }
        catch (Exception ex) { Log?.Invoke("Wi-Fi connect failed: " + ex.Message); }
        finally
        {
            c?.Dispose();
            Interlocked.Exchange(ref _connecting, 0);
        }
    }

    private void Session(TcpClient c, NetworkStream stream, string name)
    {
        int gen = Interlocked.Increment(ref _gen);
        var outbox = new BlockingCollection<string>(new ConcurrentQueue<string>());
        _outbox = outbox;
        _client = c;
        PeerName = name;
        ConnectionChanged?.Invoke(true, name);
        Log?.Invoke("Phone connected over Wi-Fi: " + name);

        var writer = new Thread(() =>
        {
            try
            {
                foreach (var line in outbox.GetConsumingEnumerable())
                {
                    byte[] b = Encoding.UTF8.GetBytes(line + "\n");
                    stream.Write(b, 0, b.Length);
                }
            }
            catch { }
        }) { IsBackground = true, Name = "wifi-writer" };
        writer.Start();

        // Reader runs on a background thread so the caller (Connect) returns.
        new Thread(() =>
        {
            try
            {
                using var reader = new StreamReader(stream, Encoding.UTF8);
                while (gen == _gen)
                {
                    string? line = reader.ReadLine();
                    if (line == null) break;
                    if (line.Length > 0) LineReceived?.Invoke(line);
                }
            }
            catch { }
            if (gen == _gen)
            {
                Drop();
                ConnectionChanged?.Invoke(false, name);
                Log?.Invoke("Wi-Fi connection closed. Waiting for the phone…");
            }
            try { c.Dispose(); } catch { }
        }) { IsBackground = true, Name = "wifi-reader" }.Start();
    }

    public void Send(string json)
    {
        var box = _outbox;
        if (box == null || box.IsAddingCompleted) return;
        try { box.Add(json); } catch (InvalidOperationException) { }
    }

    private void Drop()
    {
        Interlocked.Increment(ref _gen);
        try { _outbox?.CompleteAdding(); } catch { }
        _outbox = null;
        try { _client?.Dispose(); } catch { }
        _client = null;
    }

    private static string Str(JsonElement m, string k) =>
        m.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";

    public void Dispose()
    {
        _running = false;
        Drop();
        try { _udp?.Dispose(); } catch { }
    }
}
