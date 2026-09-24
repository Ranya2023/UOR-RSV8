using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;

namespace Remco;

/// <summary>
/// 🗳️ Live quiz / poll: a tiny web server on the PC. Students on the same Wi-Fi
/// (the laptop hotspot or the classroom router) open it in their phone's browser —
/// no app, no internet — and tap A/B/C/D. One vote per phone (can change while open).
/// </summary>
internal sealed class QuizServer : IDisposable
{
    public int Port { get; private set; }
    public event Action? Changed;

    private TcpListener? _listener;
    private volatile bool _running;
    private readonly object _lock = new();
    private readonly Dictionary<string, int> _votes = new();

    // ── the quiz ───────────────────────────────────────────────────────
    private sealed class Player { public string Name = ""; public int Score; public int Last; public bool LastCorrect; public int Answered; }
    private readonly Dictionary<string, Player> _players = new();
    private readonly Dictionary<string, (int choice, long at)> _answers = new();
    private long _askedAt;

    public string Qid { get; private set; } = "";
    public string Question { get; private set; } = "";
    public List<string> Options { get; private set; } = new();
    public int OptionCount => Math.Max(2, Options.Count);
    public bool IsOpen { get; private set; }
    public bool Reveal { get; private set; }
    public int Correct { get; private set; } = -1;
    public int Secs { get; private set; }          // 0 = no time limit
    public int Left { get { lock (_lock) return Secs <= 0 ? -1 : Math.Max(0, Secs - (int)((Environment.TickCount64 - _askedAt) / 1000)); } }
    private System.Threading.Timer? _clock;
    public int QIndex { get; private set; }
    public int QCount { get; private set; }
    public int PlayerCount { get { lock (_lock) return _players.Count; } }

    public bool Start()
    {
        if (_running) return true;
        for (int p = 8088; p <= 8095; p++)
        {
            try
            {
                var l = new TcpListener(IPAddress.Any, p);
                l.Start(64);
                _listener = l; Port = p; _running = true;
                _ = AcceptLoop();
                return true;
            }
            catch { }
        }
        return false;
    }

    public void NewQuestion(string question, List<string> options, int correct, int index, int count, int secs = 0)
    {
        lock (_lock)
        {
            Secs = Math.Clamp(secs, 0, 600);
            Qid = Guid.NewGuid().ToString("N")[..8];
            Question = question.Trim();
            Options = options.Count >= 2 ? options : new List<string> { "", "" };
            Correct = correct >= 0 && correct < Options.Count ? correct : -1;
            QIndex = index; QCount = count;
            _answers.Clear();
            IsOpen = true; Reveal = false;
            _askedAt = Environment.TickCount64;
        }
        _clock?.Dispose(); _clock = null;
        if (Secs > 0)
        {
            string qid = Qid;
            // time's up → answers close and the results appear by themselves
            _clock = new System.Threading.Timer(_ => { if (Qid == qid && IsOpen) ShowResults(); }, null, Secs * 1000 + 300, System.Threading.Timeout.Infinite);
            // tick so the projector and the phones count down
            _ = Task.Run(async () => { while (Qid == qid && IsOpen) { await Task.Delay(1000); if (Qid == qid) Changed?.Invoke(); } });
        }
        Changed?.Invoke();
    }

    /// <summary>Show the answers and give points — faster correct answers score more (like Kahoot).</summary>
    public void ShowResults()
    {
        lock (_lock)
        {
            if (Reveal) return;
            Reveal = true; IsOpen = false;
            foreach (var (id, a) in _answers)
            {
                if (!_players.TryGetValue(id, out var p)) continue;
                p.Answered++;
                p.LastCorrect = Correct >= 0 && a.choice == Correct;
                p.Last = 0;
                if (p.LastCorrect)
                {
                    double secs = Math.Max(0, (a.at - _askedAt) / 1000.0);
                    double span = Secs > 0 ? Secs : 30.0;                                   // over the time limit, or 30 s
                    p.Last = (int)Math.Round(1000 * (1 - Math.Min(1, secs / span) / 2));    // 1000 fast … 500 slow
                    p.Score += p.Last;
                }
            }
        }
        Changed?.Invoke();
    }

    public void SetCorrect(int i)
    {
        lock (_lock) Correct = i >= 0 && i < OptionCount ? i : -1;
        ShowResults();
        Changed?.Invoke();
    }

    public void Close() { lock (_lock) { IsOpen = false; } Changed?.Invoke(); }
    public void ResetScores() { lock (_lock) { foreach (var p in _players.Values) { p.Score = 0; p.Last = 0; p.Answered = 0; } } Changed?.Invoke(); }

    public int[] Counts()
    {
        lock (_lock)
        {
            var c = new int[OptionCount];
            foreach (var v in _answers.Values) if (v.choice >= 0 && v.choice < c.Length) c[v.choice]++;
            return c;
        }
    }

    public int Total { get { lock (_lock) return _answers.Count; } }

    /// <summary>Best students so far: (name, score, points just won).</summary>
    public List<(string Name, int Score, int Last)> Top(int n)
    {
        lock (_lock)
            return _players.Values.Where(p => p.Name.Length > 0)
                .OrderByDescending(p => p.Score).ThenBy(p => p.Name)
                .Take(n).Select(p => (p.Name, p.Score, p.Last)).ToList();
    }

    // ── HTTP ───────────────────────────────────────────────────────────
    private async Task AcceptLoop()
    {
        while (_running)
        {
            TcpClient? c = null;
            try { c = await _listener!.AcceptTcpClientAsync(); }
            catch { if (!_running) break; continue; }
            _ = Task.Run(() => Serve(c));
        }
    }

    private void Serve(TcpClient client)
    {
        using (client)
        {
            try
            {
                client.ReceiveTimeout = 5000; client.SendTimeout = 5000;
                var stream = client.GetStream();
                // read the request head (we only use GET)
                var sb = new StringBuilder();
                var buf = new byte[2048];
                while (sb.Length < 16384)
                {
                    int n = stream.Read(buf, 0, buf.Length);
                    if (n <= 0) break;
                    sb.Append(Encoding.ASCII.GetString(buf, 0, n));
                    if (sb.ToString().Contains("\r\n\r\n")) break;
                }
                string head = sb.ToString();
                int sp1 = head.IndexOf(' '), sp2 = sp1 < 0 ? -1 : head.IndexOf(' ', sp1 + 1);
                if (sp1 < 0 || sp2 < 0) return;
                string target = head.Substring(sp1 + 1, sp2 - sp1 - 1);
                string path = target, query = "";
                int qi = target.IndexOf('?');
                if (qi >= 0) { path = target[..qi]; query = target[(qi + 1)..]; }
                var q = ParseQuery(query);

                switch (path)
                {
                    case "/":
                    case "/index.html":
                        Send(stream, 200, "text/html; charset=utf-8", StudentPage); break;
                    case "/state":
                        Send(stream, 200, "application/json", StateJson(q)); break;
                    case "/vote":
                        Send(stream, 200, "application/json", Vote(q)); break;
                    case "/join":
                        Send(stream, 200, "application/json", Join(q)); break;
                    default:
                        Send(stream, 404, "text/plain", "not found"); break;
                }
            }
            catch { }
        }
    }

    private string StateJson(Dictionary<string, string> q)
    {
        q.TryGetValue("id", out var id);
        lock (_lock)
        {
            int[] counts = new int[OptionCount];
            foreach (var v in _answers.Values) if (v.choice >= 0 && v.choice < counts.Length) counts[v.choice]++;
            Player? me = id != null && _players.TryGetValue(id, out var p) ? p : null;
            var order = _players.Values.Where(x => x.Name.Length > 0).OrderByDescending(x => x.Score).ToList();
            int rank = me == null ? 0 : order.IndexOf(me) + 1;
            int? mine = id != null && _answers.TryGetValue(id, out var a) ? a.choice : null;
            return JsonSerializer.Serialize(new
            {
                qid = Qid, q = Question, opts = Options, n = OptionCount, open = IsOpen, reveal = Reveal,
                correct = Reveal ? Correct : -1, total = _answers.Count, counts = Reveal ? counts : null,
                qi = QIndex, qn = QCount, secs = Secs, left = Secs <= 0 ? -1 : Math.Max(0, Secs - (int)((Environment.TickCount64 - _askedAt) / 1000)),
                joined = me != null, name = me?.Name ?? "",
                score = me?.Score ?? 0, last = me?.Last ?? 0, ok = me?.LastCorrect ?? false, rank, players = order.Count,
                mine, top = order.Take(5).Select(x => new { name = x.Name, score = x.Score }).ToList()
            });
        }
    }

    private string Join(Dictionary<string, string> q)
    {
        if (!q.TryGetValue("id", out var id) || id.Length is < 4 or > 64) return "{\"ok\":false}";
        string name = (q.TryGetValue("name", out var n) ? n : "").Trim();
        if (name.Length == 0) return "{\"ok\":false}";
        if (name.Length > 24) name = name[..24];
        lock (_lock)
        {
            if (!_players.TryGetValue(id, out var p)) _players[id] = p = new Player();
            p.Name = name;
        }
        Changed?.Invoke();
        return "{\"ok\":true}";
    }

    private string Vote(Dictionary<string, string> q)
    {
        bool ok = false;
        lock (_lock)
        {
            if (IsOpen && q.TryGetValue("q", out var qid) && qid == Qid &&
                q.TryGetValue("id", out var id) && id.Length is > 3 and < 64 &&
                q.TryGetValue("c", out var cs) && int.TryParse(cs, out int c) && c >= 0 && c < OptionCount)
            {
                if (!_players.ContainsKey(id)) _players[id] = new Player();
                if (!_answers.ContainsKey(id))   // first answer counts (speed matters)
                {
                    _answers[id] = (c, Environment.TickCount64);
                    ok = true;
                }
            }
        }
        if (ok) Changed?.Invoke();
        return ok ? "{\"ok\":true}" : "{\"ok\":false}";
    }

    private static Dictionary<string, string> ParseQuery(string q)
    {
        var d = new Dictionary<string, string>();
        foreach (var part in q.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            int eq = part.IndexOf('=');
            string k = Uri.UnescapeDataString(eq < 0 ? part : part[..eq]);
            string v = eq < 0 ? "" : Uri.UnescapeDataString(part[(eq + 1)..].Replace('+', ' '));
            d[k] = v;
        }
        return d;
    }

    private static void Send(NetworkStream s, int code, string type, string body)
    {
        byte[] b = Encoding.UTF8.GetBytes(body);
        string head = $"HTTP/1.1 {code} {(code == 200 ? "OK" : "Not Found")}\r\nContent-Type: {type}\r\nContent-Length: {b.Length}\r\n" +
                      "Cache-Control: no-store\r\nConnection: close\r\n\r\n";
        byte[] h = Encoding.ASCII.GetBytes(head);
        s.Write(h, 0, h.Length);
        s.Write(b, 0, b.Length);
        s.Flush();
    }

    /// <summary>The address students should open (prefers the laptop hotspot's address).</summary>
    public string BestUrl()
    {
        var ips = WifiLink.LocalAddresses().Select(a => a.ToString()).ToList();
        string ip = ips.FirstOrDefault(a => a.StartsWith("192.168.137.")) ??   // Windows mobile hotspot
                    ips.FirstOrDefault(a => a.StartsWith("192.168.") || a.StartsWith("10.") || a.StartsWith("172.")) ??
                    ips.FirstOrDefault() ?? "127.0.0.1";
        return $"http://{ip}:{Port}";
    }

    public void Dispose()
    {
        _clock?.Dispose();
        _running = false;
        try { _listener?.Stop(); } catch { }
    }

    // ── Windows Firewall: allow students' phones in (asks once, with a Windows admin prompt) ──
    private static bool _ruleChecked;
    public static void EnsureFirewallRule(Action<string> log)
    {
        if (_ruleChecked) return;
        _ruleChecked = true;
        try
        {
            var psi = new ProcessStartInfo("netsh", "advfirewall firewall show rule name=\"UOR-RC quiz\"")
            { CreateNoWindow = true, UseShellExecute = false, RedirectStandardOutput = true };
            using (var p = Process.Start(psi)!)
            {
                string o = p.StandardOutput.ReadToEnd();
                p.WaitForExit(5000);
                if (o.Contains("UOR-RC quiz")) return;
            }
            log("Allowing students' phones through Windows Firewall (Windows will ask for permission once)…");
            var add = new ProcessStartInfo("netsh",
                "advfirewall firewall add rule name=\"UOR-RC quiz\" dir=in action=allow protocol=TCP localport=8088-8095 profile=any")
            { UseShellExecute = true, Verb = "runas", WindowStyle = ProcessWindowStyle.Hidden };
            using var a = Process.Start(add);
            a?.WaitForExit(15000);
        }
        catch (Exception ex)
        {
            log("Firewall permission not given (" + ex.Message + "). If students can't open the quiz, allow UOR-RC when Windows asks.");
        }
    }

    // ── the page students see ───────────────────────────────────────────
    private const string StudentPage = """
<!doctype html><html><head><meta charset="utf-8"><meta name="viewport" content="width=device-width,initial-scale=1">
<title>Quiz</title><style>
*{box-sizing:border-box}body{margin:0;font-family:system-ui,Segoe UI,Tahoma,sans-serif;background:#0f1117;color:#e8eaf0;min-height:100vh}
.wrap{padding:16px;max-width:680px;margin:0 auto}
h1{font-size:21px;margin:8px 0 4px;line-height:1.35}.sub{color:#8a90a2;font-size:14px}
input{width:100%;padding:16px;font-size:20px;border-radius:14px;border:0;margin:14px 0}
.go{width:100%;padding:16px;font-size:20px;font-weight:800;border:0;border-radius:14px;background:#4f8cff;color:#fff}
.opts{display:grid;gap:12px;grid-template-columns:1fr 1fr;margin-top:14px}
.opt{border:0;border-radius:18px;color:#fff;min-height:104px;padding:12px;box-shadow:0 6px 0 rgba(0,0,0,.35);text-align:left;font-size:17px;font-weight:700}
.opt b{display:block;font-size:30px;font-weight:900}
.opt:active{transform:translateY(3px);box-shadow:0 3px 0 rgba(0,0,0,.35)}
.opt.sel{outline:5px solid #fff}.opt.dim{opacity:.35}
.pill{display:inline-block;background:#1a1d27;border-radius:999px;padding:6px 14px;font-size:14px;color:#c8ccd8;margin-top:8px}
#st{text-align:center;font-size:18px;margin-top:16px;min-height:26px}
.big{font-size:42px;font-weight:900;text-align:center;margin-top:14px}
.tiny{font-size:13px;color:#8a90a2;text-align:center;margin-top:6px}
table{width:100%;margin-top:14px;font-size:16px}td{padding:6px 4px;border-bottom:1px solid #22262f}td:last-child{text-align:right;color:#fbbf24;font-weight:700}
</style></head><body><div class="wrap">
<div id="join"><div class="sub">Quiz · تاقیکردنەوە</div><h1>Your name · ناوت</h1>
 <input id="nm" maxlength="24" autocomplete="off" placeholder="Sara"><button class="go" onclick="join()">Join · بەشداربوون</button></div>
<div id="game" style="display:none">
 <div class="sub"><span id="who"></span> <span class="pill" id="sc">0</span> <span class="pill" id="clock" style="display:none">⏱ 0</span></div>
 <h1 id="q"></h1><div id="opts" class="opts"></div><div id="st"></div>
 <div id="res"></div>
</div></div>
<script>
const C=['#ef4444','#3b82f6','#eab308','#22c55e','#a855f7','#f97316'];
let id=localStorage.getItem('remcoId');if(!id){id=Math.random().toString(36).slice(2)+Date.now().toString(36);localStorage.setItem('remcoId',id);}
let S=null,mine=null,name=localStorage.getItem('remcoName')||'';
async function join(){const v=document.getElementById('nm').value.trim();if(!v)return;
 name=v;localStorage.setItem('remcoName',v);
 await fetch('/join?id='+encodeURIComponent(id)+'&name='+encodeURIComponent(v),{cache:'no-store'});poll();}
function draw(){if(!S)return;
 document.getElementById('join').style.display=S.joined?'none':'block';
 document.getElementById('game').style.display=S.joined?'block':'none';
 if(!S.joined)return;
 document.getElementById('who').textContent=S.name;
 document.getElementById('sc').textContent='⭐ '+S.score;
 const ck=document.getElementById('clock');
 if(S.left>=0&&S.open){ck.style.display='';ck.textContent='⏱ '+S.left;ck.style.background=S.left<=5?'#ef4444':'#1a1d27';}else ck.style.display='none';
 document.getElementById('q').textContent=(S.qn>1?('('+S.qi+'/'+S.qn+') '):'')+(S.q||'');
 const o=document.getElementById('opts');
 if(o.dataset.qid!==S.qid||o.childElementCount!==S.n){o.innerHTML='';o.dataset.qid=S.qid;
  for(let i=0;i<S.n;i++){const b=document.createElement('button');b.className='opt';b.style.background=C[i];b.onclick=()=>vote(i);o.appendChild(b);}}
 [...o.children].forEach((b,i)=>{const t=(S.opts&&S.opts[i])?S.opts[i]:'';
  let extra='';if(S.reveal&&S.counts){const tot=S.counts.reduce((a,b)=>a+b,0)||1;extra=' — '+S.counts[i]+' · '+Math.round(S.counts[i]*100/tot)+'%'+(S.correct===i?' ✓':'');}
  b.innerHTML='<b>'+String.fromCharCode(65+i)+'</b>'+(t||'')+extra;
  b.className='opt'+(mine===i?' sel':'')+(S.reveal&&S.correct>=0&&S.correct!==i?' dim':'');b.disabled=!S.open;});
 const st=document.getElementById('st'),res=document.getElementById('res');res.innerHTML='';
 if(S.reveal){st.textContent='';
  res.innerHTML='<div class="big">'+(S.correct<0?'📊 Results':(S.ok?'✅ +'+S.last:'❌ 0'))+'</div>'+
   '<div class="tiny">'+(S.ok?'Correct · ڕاستە':(mine===null?'No answer · وەڵامت نەدا':'Not this time · هەڵەیە'))+
   ' — ⭐ '+S.score+' · #'+S.rank+' of '+S.players+'</div>'+
   (S.top&&S.top.length?'<table>'+S.top.map((t,i)=>'<tr><td>'+['🥇','🥈','🥉','4.','5.'][i]+' '+t.name+'</td><td>'+t.score+'</td></tr>').join('')+'</table>':'');}
 else if(!S.open)st.textContent='⏸ Waiting for the teacher · چاوەڕێی مامۆستا';
 else st.textContent=mine===null?'Tap your answer · وەڵامەکەت هەڵبژێرە':'✓ Sent · نێردرا';}
async function poll(){try{const r=await fetch('/state?id='+encodeURIComponent(id),{cache:'no-store'});const s=await r.json();
 if(!S||s.qid!==S.qid)mine=(s.mine===null||s.mine===undefined)?null:s.mine; else if(s.mine!==null&&s.mine!==undefined)mine=s.mine;
 S=s;draw();}catch(e){}}
async function vote(i){if(!S||!S.open||mine!==null)return;mine=i;draw();
 try{await fetch('/vote?id='+encodeURIComponent(id)+'&c='+i+'&q='+S.qid,{cache:'no-store'});}catch(e){}}
if(name){document.getElementById('nm').value=name;fetch('/join?id='+encodeURIComponent(id)+'&name='+encodeURIComponent(name),{cache:'no-store'});}
poll();setInterval(poll,1200);
</script></body></html>
""";

}
