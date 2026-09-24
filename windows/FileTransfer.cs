using System.Collections.Concurrent;
using System.Text.Json;

namespace Remco;

/// <summary>
/// Files both ways over the same Wi-Fi / Bluetooth link.
/// Protocol (phone→PC uses "c":"fs", PC→phone uses "e":"fs"), field "t":
///   start {id,name,size} · chunk {id,seq,d(base64)} · end {id} · ack {id,seq} · done {id,ok} · cancel {id}
/// The sender keeps at most 4 chunks in flight, so it never floods the link.
/// Received files go to  Downloads\Remco.
/// </summary>
internal sealed class FileTransfer
{
    private const int ChunkSize = 48 * 1024;
    private const int Window = 8;

    private readonly Action<object> _send;
    private readonly Action<string> _log;

    /// <summary>(name, percent 0-100, toPhone)</summary>
    public event Action<string, int, bool>? Progress;
    /// <summary>full path of a file received from the phone</summary>
    public event Action<string>? Received;
    /// <summary>a photo / video to show full-screen: (path, index in the batch)</summary>
    public event Action<string, int>? MediaReceived;
    /// <summary>a PDF / Word / PowerPoint file to present: (path)</summary>
    public event Action<string>? DocReceived;

    public static string Folder
    {
        get
        {
            // a folder named after the app, right on the Desktop
            string d = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), "UOR-RC");
            Directory.CreateDirectory(d);
            return d;
        }
    }

    // incoming (phone → PC)
    private sealed class Incoming { public FileStream Fs = null!; public string Path = ""; public string Name = ""; public long Size; public long Got; public bool Present; public int Index; public bool Doc; }
    private readonly Dictionary<string, Incoming> _in = new();

    // outgoing (PC → phone)
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _acks = new();
    private readonly ConcurrentDictionary<string, bool> _cancelled = new();
    private readonly SemaphoreSlim _oneAtATime = new(1, 1);

    public FileTransfer(Action<object> send, Action<string> log) { _send = send; _log = log; }

    /// <summary>A "fs" message from the phone.</summary>
    public void Handle(JsonElement m)
    {
        string t = S(m, "t"), id = S(m, "id");
        switch (t)
        {
            case "start":
            {
                string name = Safe(S(m, "name"));
                bool present = m.TryGetProperty("present", out var pv) && pv.ValueKind == JsonValueKind.True;
                bool doc = S(m, "kind") == "doc";
                string dir = present && !doc ? MediaViewer.Folder : Folder;   // documents are also kept in Downloads\Remco
                Directory.CreateDirectory(dir);
                string path = Unique(System.IO.Path.Combine(dir, name));
                try
                {
                    var inc = new Incoming
                    {
                        Fs = File.Create(path), Path = path, Name = System.IO.Path.GetFileName(path), Size = (long)N(m, "size"),
                        Present = present, Index = (int)N(m, "index"), Doc = doc
                    };
                    _in[id] = inc;
                    _log($"Receiving from phone: {inc.Name}");
                    Progress?.Invoke(inc.Name, 0, false);
                    _send(new { e = "fs", t = "ack", id, seq = -1 });
                }
                catch (Exception ex)
                {
                    _log("Cannot save file: " + ex.Message);
                    _send(new { e = "fs", t = "cancel", id });
                }
                break;
            }
            case "chunk":
                if (_in.TryGetValue(id, out var c))
                {
                    try
                    {
                        byte[] data = Convert.FromBase64String(S(m, "d"));
                        c.Fs.Write(data, 0, data.Length);
                        c.Got += data.Length;
                        if (c.Size > 0) Progress?.Invoke(c.Name, (int)(c.Got * 100 / c.Size), false);
                        _send(new { e = "fs", t = "ack", id, seq = (int)N(m, "seq") });
                    }
                    catch (Exception ex) { Abort(id, ex.Message); }
                }
                break;
            case "end":
                if (_in.Remove(id, out var e))
                {
                    e.Fs.Dispose();
                    if (!e.Present) _log($"Saved: {e.Path}");
                    Progress?.Invoke(e.Name, 100, false);
                    _send(new { e = "fs", t = "done", id, ok = true });
                    if (e.Doc) DocReceived?.Invoke(e.Path);
                    else if (e.Present) MediaReceived?.Invoke(e.Path, e.Index);
                    else Received?.Invoke(e.Path);
                }
                break;
            case "cancel":
                Abort(id, "cancelled on the phone", notify: false);
                _cancelled[id] = true;
                if (_acks.TryGetValue(id, out var sem)) sem.Release(Window);
                break;
            case "ack":
                if (_acks.TryGetValue(id, out var s2)) s2.Release();
                break;
            case "done":
                break;
        }
    }

    private void Abort(string id, string why, bool notify = true)
    {
        if (_in.Remove(id, out var inc))
        {
            try { inc.Fs.Dispose(); File.Delete(inc.Path); } catch { }
            _log($"File transfer stopped ({why}).");
            if (notify) _send(new { e = "fs", t = "cancel", id });
        }
    }

    public void CancelAll()
    {
        foreach (var id in _in.Keys.ToList()) Abort(id, "phone disconnected", notify: false);
        foreach (var kv in _acks) { _cancelled[kv.Key] = true; kv.Value.Release(Window); }
    }

    /// <summary>Send files to the phone (runs in the background, one after another).</summary>
    public void SendFiles(IEnumerable<string> paths)
    {
        var list = paths.Where(File.Exists).ToList();
        if (list.Count == 0) return;
        Task.Run(async () =>
        {
            foreach (var p in list)
            {
                await _oneAtATime.WaitAsync();
                try { await SendOne(p); }
                catch (Exception ex) { _log("Sending failed: " + ex.Message); }
                finally { _oneAtATime.Release(); }
            }
        });
    }

    private async Task SendOne(string path)
    {
        string id = Guid.NewGuid().ToString("N")[..10];
        var info = new FileInfo(path);
        var sem = new SemaphoreSlim(0, 1000);
        _acks[id] = sem;
        try
        {
            _log($"Sending to phone: {info.Name} ({info.Length / 1024} KB)");
            _send(new { e = "fs", t = "start", id, name = info.Name, size = info.Length });
            if (!await sem.WaitAsync(15000) || _cancelled.ContainsKey(id)) { _log("The phone didn't accept the file."); return; }
            sem.Release(Window);   // allow Window chunks in flight

            using var fs = File.OpenRead(path);
            var buf = new byte[ChunkSize];
            long sent = 0; int seq = 0, lastPct = -1;
            int n;
            while ((n = await fs.ReadAsync(buf, 0, buf.Length)) > 0)
            {
                if (!await sem.WaitAsync(20000) || _cancelled.ContainsKey(id)) { _log($"Stopped sending {info.Name}."); _send(new { e = "fs", t = "cancel", id }); return; }
                _send(new { e = "fs", t = "chunk", id, seq = seq++, d = Convert.ToBase64String(buf, 0, n) });
                sent += n;
                int pct = info.Length > 0 ? (int)(sent * 100 / info.Length) : 100;
                if (pct != lastPct) { lastPct = pct; Progress?.Invoke(info.Name, pct, true); }
            }
            _send(new { e = "fs", t = "end", id });
            _log($"Sent to phone: {info.Name}");
            Progress?.Invoke(info.Name, 100, true);
        }
        finally
        {
            _acks.TryRemove(id, out _);
            _cancelled.TryRemove(id, out _);
        }
    }

    private static string Safe(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) name = "file";
        foreach (char ch in System.IO.Path.GetInvalidFileNameChars()) name = name.Replace(ch, '_');
        return name.Length > 150 ? name[..150] : name;
    }

    private static string Unique(string path)
    {
        if (!File.Exists(path)) return path;
        string dir = System.IO.Path.GetDirectoryName(path)!, stem = System.IO.Path.GetFileNameWithoutExtension(path), ext = System.IO.Path.GetExtension(path);
        for (int i = 2; ; i++)
        {
            string p = System.IO.Path.Combine(dir, $"{stem} ({i}){ext}");
            if (!File.Exists(p)) return p;
        }
    }

    private static string S(JsonElement m, string k) =>
        m.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";
    private static double N(JsonElement m, string k) =>
        m.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetDouble() : 0;
}
