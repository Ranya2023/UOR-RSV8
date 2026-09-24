using System.Text.Json;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace Remco;

/// <summary>
/// Full-screen photo / video player on the projector (🖼 Show photos &amp; videos).
/// The phone sends the real file, so the PC shows it at full quality, landscape,
/// with the video's sound on the PC — no phone frame, nothing that looks like a phone.
/// Uses the Edge WebView2 engine that ships with Windows 10/11.
/// </summary>
internal sealed class MediaViewer : Form
{
    public static string Folder { get; } = Path.Combine(Path.GetTempPath(), "UOR-RC-media");

    private readonly Action<object> _send;
    private readonly Action<string> _log;
    private readonly WebView2 _web = new() { Dock = DockStyle.Fill };
    private readonly List<string> _items = new();
    private int _index = -1;
    private bool _ready, _failed;
    private bool _fill;
    private int _rot;

    public bool IsOpen => Visible;
    public int CurrentIndex => _index;
    public string CurrentName => _index >= 0 && _index < _items.Count ? Path.GetFileName(_items[_index]) : "";
    /// <summary>raised when the photo / video changes (so tools & drawings follow)</summary>
    public event Action? ItemChanged;

    public MediaViewer(Action<object> send, Action<string> log)
    {
        _send = send; _log = log;
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.Manual;
        TopMost = true;
        BackColor = Color.Black;
        Text = "UOR-RC – media";
        Icon = MainForm.AppIcon;
        _web.DefaultBackgroundColor = Color.Black;
        Controls.Add(_web);
        try { if (Directory.Exists(Folder)) Directory.Delete(Folder, true); } catch { }
        Directory.CreateDirectory(Folder);
    }

    protected override bool ShowWithoutActivation => true;

    private async Task<bool> EnsureReady()
    {
        if (_ready) return true;
        if (_failed) return false;
        try
        {
            File.WriteAllText(Path.Combine(Folder, "player.html"), PlayerHtml);
            var opts = new CoreWebView2EnvironmentOptions(
                // play with sound straight away; keep video inside normal window composition so Remco's
                // laser / spotlight / pen layer can always be drawn on top of it
                "--autoplay-policy=no-user-gesture-required --disable-direct-composition-video-overlays");
            var env = await CoreWebView2Environment.CreateAsync(null, Path.Combine(Settings.DataDir, "webview"), opts);
            await _web.EnsureCoreWebView2Async(env);
            var core = _web.CoreWebView2;
            core.Settings.AreDefaultContextMenusEnabled = false;
            core.Settings.AreDevToolsEnabled = false;
            core.Settings.IsStatusBarEnabled = false;
            core.SetVirtualHostNameToFolderMapping("remco.media", Folder, CoreWebView2HostResourceAccessKind.Allow);
            core.WebMessageReceived += (_, e) =>
            {
                try
                {
                    using var doc = JsonDocument.Parse(e.TryGetWebMessageAsString());
                    var m = doc.RootElement;
                    _send(new
                    {
                        e = "media", open = true, index = _index, count = _items.Count,
                        kind = m.GetProperty("kind").GetString(), playing = m.GetProperty("playing").GetBoolean(),
                        pos = m.GetProperty("pos").GetDouble(), dur = m.GetProperty("dur").GetDouble(),
                        name = _index >= 0 ? Path.GetFileName(_items[_index]) : "", fill = _fill,
                        muted = m.TryGetProperty("muted", out var mu) && mu.GetBoolean()
                    });
                }
                catch { }
            };
            var loaded = new TaskCompletionSource<bool>();
            core.NavigationCompleted += (_, _) => loaded.TrySetResult(true);
            core.Navigate("https://remco.media/player.html");
            await Task.WhenAny(loaded.Task, Task.Delay(5000));
            _ready = true;
            return true;
        }
        catch (Exception ex)
        {
            _failed = true;
            _log("Full-screen player not available (" + ex.Message + "). Opening with the Windows app instead.");
            return false;
        }
    }

    /// <summary>A file arrived from the phone: add it; the first one is shown straight away.</summary>
    public async void Add(string path, Rectangle area)
    {
        _items.Add(path);
        if (!await EnsureReady())
        {
            try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(path) { UseShellExecute = true }); } catch { }
            return;
        }
        if (_index < 0 || !Visible) { Open(area); await ShowItem(_items.Count - 1); }
        else SendState();
    }

    public void NewBatch() { if (!Visible) { _items.Clear(); _index = -1; } }

    private void Open(Rectangle area)
    {
        if (Bounds != area) Bounds = area;
        if (!Visible) Show();
        ItemChanged?.Invoke();
    }

    private async Task ShowItem(int i)
    {
        if (i < 0 || i >= _items.Count) return;
        _index = i;
        string file = Path.GetFileName(_items[i]);
        string ext = Path.GetExtension(file).ToLowerInvariant();
        bool video = ext is ".mp4" or ".m4v" or ".mov" or ".webm" or ".mkv" or ".3gp" or ".avi" or ".wmv";
        string url = "https://remco.media/" + Uri.EscapeDataString(file);
        await Js($"show({JsonSerializer.Serialize(url)}, {(video ? "true" : "false")}, {(_fill ? "true" : "false")}, {_rot})");
        SendState();
        ItemChanged?.Invoke();
    }

    private void SendState() => _ = Js("report()");

    private async Task Js(string script)
    {
        try { if (_ready) await _web.CoreWebView2.ExecuteScriptAsync(script); } catch { }
    }

    // ── remote control from the phone ──────────────────────────────────
    public async void Command(string a, double value)
    {
        switch (a)
        {
            case "next": if (_index + 1 < _items.Count) await ShowItem(_index + 1); else SendState(); break;
            case "prev": if (_index > 0) await ShowItem(_index - 1); else SendState(); break;
            case "toggle": await Js("toggle()"); break;
            case "seek": await Js($"seek({value.ToString(System.Globalization.CultureInfo.InvariantCulture)})"); break;
            case "rel": await Js($"rel({value.ToString(System.Globalization.CultureInfo.InvariantCulture)})"); break;
            case "vol": await Js($"vol({value.ToString(System.Globalization.CultureInfo.InvariantCulture)})"); break;
            case "fill": _fill = !_fill; await Js($"setFill({(_fill ? "true" : "false")})"); break;
            case "rotate": _rot = (_rot + 90) % 360; await Js($"setRot({_rot})"); break;
            case "close": Close2(); break;
            case "hide": Hide2(); break;
            case "mute": await Js("mute()"); break;
        }
    }

    /// <summary>Switching to PowerPoint / desktop / phone screen: pause and hide, but keep the photos &amp; videos.</summary>
    public void Hide2()
    {
        if (!Visible) return;
        _ = Js("vid.pause()");
        Hide();
        _send(new { e = "media", open = false, count = _items.Count });
    }

    /// <summary>Back to the photos &amp; videos that were hidden.</summary>
    public async void ShowAgain(Rectangle area)
    {
        if (_items.Count == 0 || !_ready) { _send(new { e = "media", open = false, count = 0 }); return; }
        Open(area);
        await ShowItem(Math.Max(0, _index));
    }

    public void Close2()
    {
        _ = Js("stop()");
        if (Visible) Hide();
        _items.Clear();
        _index = -1;
        _send(new { e = "media", open = false });
    }

    public void Next() => Command("next", 0);
    public void Prev() => Command("prev", 0);

    protected override void Dispose(bool disposing)
    {
        if (disposing) { try { _web.Dispose(); } catch { } }
        base.Dispose(disposing);
        try { Directory.Delete(Folder, true); } catch { }
    }

    private const string PlayerHtml = """
<!doctype html><html><head><meta charset="utf-8"><style>
html,body{margin:0;height:100%;background:#000;overflow:hidden;cursor:none}
.m{position:absolute;inset:0;width:100%;height:100%;object-fit:contain;opacity:0;transition:opacity .35s ease;background:#000}
.m.on{opacity:1}
</style></head><body>
<img id="img" class="m"><video id="vid" class="m" playsinline></video>
<script>
const img=document.getElementById('img'), vid=document.getElementById('vid');
let isVideo=false, rot=0, fill=false;
function apply(el){el.style.objectFit=fill?'cover':'contain';
  const side=rot%180!==0; el.style.transform='rotate('+rot+'deg)'+(side?' scale('+(innerHeight/innerWidth)+')':'');}
function show(url,video,f,r){fill=f;rot=r;isVideo=video;
  if(video){img.classList.remove('on');vid.src=url;apply(vid);vid.classList.add('on');vid.currentTime=0;vid.play().catch(()=>{});}
  else{vid.pause();vid.classList.remove('on');vid.removeAttribute('src');vid.load();
       img.classList.remove('on');img.onload=()=>{apply(img);img.classList.add('on');report();};img.src=url;}
  report();}
function toggle(){if(!isVideo)return;if(vid.paused)vid.play();else vid.pause();report();}
function seek(t){if(isVideo){vid.currentTime=Math.max(0,Math.min(vid.duration||0,t));report();}}
function rel(d){if(isVideo)seek(vid.currentTime+d);}
function vol(d){vid.volume=Math.max(0,Math.min(1,vid.volume+d));}
function mute(){vid.muted=!vid.muted;report();}
function setFill(f){fill=f;apply(img);apply(vid);}
function setRot(r){rot=r;apply(img);apply(vid);}
function stop(){vid.pause();vid.removeAttribute('src');vid.load();img.classList.remove('on');vid.classList.remove('on');}
function report(){try{chrome.webview.postMessage(JSON.stringify({kind:isVideo?'video':'image',playing:isVideo&&!vid.paused,
  pos:isVideo?(vid.currentTime||0):0,dur:isVideo?(vid.duration||0):0,muted:vid.muted}));}catch(e){}}
vid.addEventListener('play',report);vid.addEventListener('pause',report);vid.addEventListener('ended',report);
vid.addEventListener('loadedmetadata',report);setInterval(()=>{if(isVideo&&!vid.paused)report();},1000);
</script></body></html>
""";
}
