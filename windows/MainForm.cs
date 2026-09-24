using System.Diagnostics;
using System.Text.Json;
using QRCoder;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace Remco;

/// <summary>Status window + tray icon. Everything runs on this UI (STA) thread.</summary>
internal sealed class MainForm : Form
{
    private static readonly Color Bg = Color.FromArgb(0x0F, 0x11, 0x17);
    private static readonly Color Panel2 = Color.FromArgb(0x1A, 0x1D, 0x27);
    private static readonly Color Text1 = Color.FromArgb(0xE8, 0xEA, 0xF0);
    private static readonly Color Muted = Color.FromArgb(0x8A, 0x90, 0xA2);
    private static readonly Color Accent = Color.FromArgb(0x4F, 0x8C, 0xFF);
    private static readonly Color Good = Color.FromArgb(0x30, 0xD1, 0x58);
    private static readonly Color Warn = Color.FromArgb(0xFF, 0xB0, 0x20);
    private static readonly Color Bad = Color.FromArgb(0xE5, 0x48, 0x4D);

    private readonly Settings _settings;
    private readonly Label _btStatus = new();
    private readonly Label _wifiStatus = new();
    private readonly Label _pptStatus = new();
    private readonly Label _pin = new();
    private readonly Label _hotspotStatus = new();
    private readonly Button _hotspotBtn = new();
    private readonly CheckBox _autoHotspot = new();
    private readonly PictureBox _qr = new();
    private readonly Label _qrHint = new();
    private readonly TextBox _log = new();
    private readonly Button _sendFiles = new();
    private readonly Button _present = new();
    private readonly Label _fileStatus = new();
    private readonly NotifyIcon _tray = new();
    private readonly System.Windows.Forms.Timer _poll = new();
    private readonly System.Windows.Forms.Timer _hotspotPoll = new();
    private readonly BtServer _bt = new();
    private readonly WifiLink _wifi;
    private readonly Overlay _overlay = new();
    private readonly PowerPointController _ppt;
    private bool _btConnected, _wifiConnected;
    private string _qrText = "";
    private bool _hotspotBusy;

    // ── the modern window (HTML inside Edge WebView2); the classic controls stay as a fallback ──
    private WebView2? _web;
    private bool _uiReady;
    private bool _uiPainted;
    private string _qrPng = "";
    private int _filePct = -1;
    private Hotspot.Info? _hs;
    private string _lastUi = "";
    private readonly List<string> _logLines = new();

    private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = false, PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    public static Icon AppIcon { get; } = LoadIcon();

    private static Icon LoadIcon()
    {
        try
        {
            using var s = typeof(MainForm).Assembly.GetManifestResourceStream("remco.ico");
            if (s != null) return new Icon(s);
        }
        catch { }
        return SystemIcons.Application;
    }

    public MainForm(Settings settings)
    {
        _settings = settings;
        _wifi = new WifiLink(settings);

        AutoScaleDimensions = new SizeF(96f, 96f);
        AutoScaleMode = AutoScaleMode.Dpi;
        Text = "UOR-RC";
        ClientSize = new Size(560, 700);
        MinimumSize = new Size(560, 560);
        StartPosition = FormStartPosition.CenterScreen;
        Font = new Font("Segoe UI", 10f);
        BackColor = Bg;
        ForeColor = Text1;
        Icon = AppIcon;

        // ── header ─────────────────────────────────────────────────────
        var logo = new PictureBox { Location = new Point(16, 14), Size = new Size(44, 44), SizeMode = PictureBoxSizeMode.Zoom };
        try { logo.Image = new Icon(AppIcon, 64, 64).ToBitmap(); } catch { }
        var title = new Label { Text = "UOR-RC", Location = new Point(68, 10), AutoSize = true, Font = new Font("Segoe UI Semibold", 18f) };
        var sub = new Label { Text = "PowerPoint remote  ·  Wi-Fi + Bluetooth  ·  offline", Location = new Point(71, 42), AutoSize = true, ForeColor = Muted };

        // ── connection status ─────────────────────────────────────────
        Setup(_wifiStatus, 16, 76, "● Wi-Fi: starting…", Warn);
        Setup(_btStatus, 16, 102, "● Bluetooth: starting…", Warn);
        Setup(_pptStatus, 16, 128, "PowerPoint: checking…", Text1);

        var pinTitle = new Label { Text = "Wi-Fi PIN — type it once on the phone:", Location = new Point(16, 164), AutoSize = true, ForeColor = Muted };
        _pin.Text = _settings.Pin;
        _pin.Location = new Point(14, 184); _pin.AutoSize = true;
        _pin.Font = new Font("Consolas", 28f, FontStyle.Bold); _pin.ForeColor = Accent;

        // ── hotspot ────────────────────────────────────────────────────
        var hsTitle = new Label { Text = "Laptop Wi-Fi hotspot", Location = new Point(16, 246), AutoSize = true, Font = new Font("Segoe UI Semibold", 11f) };
        _hotspotStatus.Location = new Point(16, 272); _hotspotStatus.Size = new Size(350, 66); _hotspotStatus.ForeColor = Muted;
        _hotspotStatus.Text = "Checking…";

        StyleButton(_hotspotBtn, "Turn hotspot on", 16, 344, 170, true);
        _hotspotBtn.Click += async (_, _) => await ToggleHotspot();
        var hsSettings = new Button();
        StyleButton(hsSettings, "Hotspot settings…", 194, 344, 160, false);
        hsSettings.Click += (_, _) => OpenUri("ms-settings:network-mobilehotspot");

        _autoHotspot.Text = "Turn the hotspot on when UOR-RC opens";
        _autoHotspot.Location = new Point(16, 384); _autoHotspot.AutoSize = true;
        _autoHotspot.Checked = _settings.AutoHotspot;
        _autoHotspot.CheckedChanged += (_, _) => { _settings.AutoHotspot = _autoHotspot.Checked; _settings.Save(); };

        _qr.Location = new Point(386, 240); _qr.Size = new Size(156, 156);
        _qr.SizeMode = PictureBoxSizeMode.Zoom; _qr.BackColor = Panel2;
        _qrHint.Location = new Point(378, 398); _qrHint.Size = new Size(172, 36);
        _qrHint.ForeColor = Muted; _qrHint.Font = new Font("Segoe UI", 8.5f); _qrHint.TextAlign = ContentAlignment.TopCenter;
        _qrHint.Text = "";

        // ── files ──────────────────────────────────────────────────────
        StyleButton(_present, "📑 Present a file…", 16, 416, 170, true);
        StyleButton(_sendFiles, "📁 Send to phone…", 194, 416, 160, false);
        _fileStatus.Location = new Point(362, 422); _fileStatus.Size = new Size(182, 22); _fileStatus.ForeColor = Muted;
        _fileStatus.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right; _fileStatus.AutoEllipsis = true;
        _fileStatus.Text = "or drag files onto this window";

        // ── log ────────────────────────────────────────────────────────
        _log.Location = new Point(16, 456); _log.Size = new Size(528, 228);
        _log.Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right;
        _log.Multiline = true; _log.ReadOnly = true; _log.ScrollBars = ScrollBars.Vertical; _log.BorderStyle = BorderStyle.None;
        _log.BackColor = Panel2; _log.ForeColor = Color.FromArgb(0xC8, 0xCC, 0xD8); _log.Font = new Font("Consolas", 9f);

        Controls.AddRange(new Control[] { logo, title, sub, _wifiStatus, _btStatus, _pptStatus, pinTitle, _pin,
            hsTitle, _hotspotStatus, _hotspotBtn, hsSettings, _autoHotspot, _qr, _qrHint, _present, _sendFiles, _fileStatus, _log });

        // ── tray ───────────────────────────────────────────────────────
        _tray.Icon = AppIcon;
        _tray.Text = "UOR-RC";
        _tray.Visible = true;
        var menu = new ContextMenuStrip();
        menu.Items.Add("Show", null, (_, _) => RestoreFromTray());
        menu.Items.Add("Exit", null, (_, _) => { _tray.Visible = false; Close(); });
        _tray.ContextMenuStrip = menu;
        _tray.DoubleClick += (_, _) => RestoreFromTray();
        Resize += (_, _) =>
        {
            if (WindowState == FormWindowState.Minimized)
            {
                Hide();
                _tray.ShowBalloonTip(2000, "UOR-RC", "Still running in the tray.", ToolTipIcon.Info);
            }
        };

        // ── links ──────────────────────────────────────────────────────
        _ppt = new PowerPointController(SendToPhone, Log, _overlay);

        _bt.Log += msg => UI(() => Log(msg));
        _bt.LineReceived += line => UI(() => _ppt.Handle(line));
        _bt.ConnectionChanged += (connected, name) => UI(() =>
        {
            bool before = _btConnected || _wifiConnected;
            _btConnected = connected;
            SetLabel(_btStatus, connected ? $"● Bluetooth: phone connected — {name}" : "● Bluetooth: waiting for the phone…", connected ? Good : Warn);
            AfterConnectionChange(before);
        });

        _wifi.Log += msg => UI(() => Log(msg));
        _wifi.LineReceived += line => UI(() => _ppt.Handle(line));
        _wifi.ConnectionChanged += (connected, name) => UI(() =>
        {
            bool before = _btConnected || _wifiConnected;
            _wifiConnected = connected;
            UpdateWifiLabel();
            AfterConnectionChange(before);
        });

        // files: button, drag & drop, progress, "received" balloon
        // 📑 show a PDF / Word / PowerPoint / photo / video from this PC full-screen on the projector
        _present.Click += (_, _) =>
        {
            using var dlg = new OpenFileDialog
            {
                Title = "Present a file",
                Filter = "Presentable files|*.pdf;*.docx;*.doc;*.rtf;*.odt;*.txt;*.pptx;*.ppt;*.ppsx;*.odp;*.jpg;*.jpeg;*.png;*.gif;*.webp;*.mp4;*.mov;*.webm|All files|*.*"
            };
            if (dlg.ShowDialog(this) == DialogResult.OK) _ppt.OpenDocument(dlg.FileName);
        };

        _sendFiles.Click += (_, _) =>
        {
            if (!(_btConnected || _wifiConnected)) { Log("Connect the phone first."); return; }
            using var dlg = new OpenFileDialog { Multiselect = true, Title = "Send to phone" };
            if (dlg.ShowDialog(this) == DialogResult.OK) _ppt.Files.SendFiles(dlg.FileNames);
        };
        AllowDrop = true;
        DragEnter += (_, e) => { if (e.Data?.GetDataPresent(DataFormats.FileDrop) == true) e.Effect = DragDropEffects.Copy; };
        DragDrop += (_, e) =>
        {
            if (e.Data?.GetData(DataFormats.FileDrop) is string[] files)
            {
                if (!(_btConnected || _wifiConnected)) { Log("Connect the phone first."); return; }
                _ppt.Files.SendFiles(files.Where(File.Exists));
            }
        };
        _ppt.Files.Progress += (name, pct, toPhone) => UI(() =>
        {
            _fileStatus.Text = (toPhone ? "→ phone: " : "← phone: ") + name + (pct >= 100 ? "  ✓" : $"  {pct}%");
            _filePct = pct >= 100 ? -1 : pct;
            PushUi();
        });
        _ppt.Files.Received += path => UI(() =>
        {
            _tray.BalloonTipClicked -= OpenReceived;
            _tray.BalloonTipClicked += OpenReceived;
            _tray.ShowBalloonTip(4000, "UOR-RC – file received", Path.GetFileName(path) + "\nClick to open the folder.", ToolTipIcon.Info);
        });

        _poll.Interval = 300;
        _poll.Tick += (_, _) =>
        {
            _ppt.Poll();
            _pptStatus.Text = _ppt.Status;
            PushUi();
        };
        _hotspotPoll.Interval = 3000;
        _hotspotPoll.Tick += (_, _) => { RefreshHotspot(); if (!_wifiConnected) UpdateWifiLabel(); };

        Load += async (_, _) =>
        {
            await InitModernUi();
            Log("UOR-RC started. Settings are kept in: " + Settings.DataDir);
            Log("Connect the phone by Wi-Fi (fastest) or Bluetooth — whichever is available.");
            Log("  • Wi-Fi: phone and PC on the same hotspot/network, then enter the PIN on the phone once.");
            Log("  • Bluetooth: pair the phone with this PC once in Windows Settings.");

            try
            {
                await _bt.StartAsync();
                SetLabel(_btStatus, "● Bluetooth: waiting for the phone…", Warn);
            }
            catch (Exception ex)
            {
                SetLabel(_btStatus, "● Bluetooth: off or not available", Bad);
                Log("Bluetooth not started: " + ex.Message);
            }

            _wifi.BtAddress = _bt.Address;
            try { _wifi.Start(); }
            catch (Exception ex) { Log("Wi-Fi not started: " + ex.Message); }
            UpdateWifiLabel();

            if (_settings.AutoHotspot) await ToggleHotspot(forceOn: true);
            else RefreshHotspot();

            _poll.Start();
            _hotspotPoll.Start();
        };

        FormClosing += (_, _) =>
        {
            _poll.Stop();
            _hotspotPoll.Stop();
            _tray.Visible = false;
            _overlay.Dispose();
            _bt.Dispose();
            _wifi.Dispose();
            if (Hotspot.StartedByRemco)
            {
                try { Task.Run(() => Hotspot.StopAsync()).Wait(3000); } catch { }
            }
        };
    }

    private void AfterConnectionChange(bool before)
    {
        bool now = _btConnected || _wifiConnected;
        _tray.Text = now ? "UOR-RC – phone connected" : "UOR-RC – waiting for the phone";
        if (now && !before) _ppt.OnPhoneConnected();
        if (!now && before) _ppt.OnPhoneDisconnected();
    }

    private void UpdateWifiLabel()
    {
        if (_wifiConnected) { SetLabel(_wifiStatus, $"● Wi-Fi: phone connected — {_wifi.PeerName}", Good); return; }
        var ips = WifiLink.LocalAddresses().Select(a => a.ToString()).ToList();
        SetLabel(_wifiStatus, ips.Count == 0
            ? "● Wi-Fi: no network — turn on the hotspot below"
            : "● Wi-Fi: waiting for the phone  (this PC: " + string.Join(", ", ips.Take(3)) + ")", Warn);
    }

    // ── hotspot ────────────────────────────────────────────────────────
    private async Task ToggleHotspot(bool forceOn = false)
    {
        if (_hotspotBusy) return;
        _hotspotBusy = true;
        _hotspotBtn.Enabled = false;
        try
        {
            var st = Hotspot.Status();
            if (st.On && !forceOn)
            {
                Log("Turning the hotspot off…");
                await Hotspot.StopAsync();
            }
            else if (!st.On)
            {
                Log("Turning the hotspot on…");
                var r = await Hotspot.StartAsync();
                if (r.On) Log($"Hotspot is on: \"{r.Ssid}\"  password: {r.Pass}");
                else
                {
                    Log("Could not start the hotspot: " + r.Error);
                    Log("Tip: use the phone's own hotspot instead (connect this laptop to it) — UOR-RC finds the phone the same way.");
                }
            }
        }
        finally
        {
            _hotspotBusy = false;
            _hotspotBtn.Enabled = true;
            RefreshHotspot();
        }
    }

    private void RefreshHotspot()
    {
        if (_hotspotBusy) return;
        var st = Hotspot.Status();
        _hs = st;
        if (st.On)
        {
            _hotspotStatus.ForeColor = Good;
            _hotspotStatus.Text = $"On  ·  Name: {st.Ssid}\r\nPassword: {st.Pass}\r\n{st.Clients} device(s) connected";
            _hotspotBtn.Text = "Turn hotspot off";
            ShowQr(Hotspot.WifiQr(st.Ssid, st.Pass));
            _qrHint.Text = "Scan with the phone camera\nto join this Wi-Fi";
        }
        else
        {
            _hotspotStatus.ForeColor = st.Error.Length > 0 ? Warn : Muted;
            _hotspotStatus.Text = st.Error.Length > 0 ? "Not available here: " + st.Error : "Off";
            _hotspotBtn.Text = "Turn hotspot on";
            ShowQr("");
            _qrHint.Text = "";
        }
    }

    private void ShowQr(string text)
    {
        if (text == _qrText) return;
        _qrText = text;
        var old = _qr.Image;
        _qr.Image = null;
        old?.Dispose();
        _qrPng = "";
        if (text.Length == 0) { PushUi(); return; }
        try
        {
            using var gen = new QRCodeGenerator();
            using var data = gen.CreateQrCode(text, QRCodeGenerator.ECCLevel.M);
            byte[] png = new PngByteQRCode(data).GetGraphic(8);
            using var ms = new MemoryStream(png);
            _qr.Image = new Bitmap(Image.FromStream(ms));
            _qrPng = "data:image/png;base64," + Convert.ToBase64String(png);
            PushUi();
        }
        catch (Exception ex) { Log("QR code failed: " + ex.Message); }
    }

    // ── helpers ────────────────────────────────────────────────────────
    private static void Setup(Label l, int x, int y, string text, Color c)
    {
        l.Location = new Point(x, y); l.Size = new Size(528, 24); l.Text = text; l.ForeColor = c;
        l.AutoEllipsis = true;
        l.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
    }

    private static void SetLabel(Label l, string text, Color c) { l.Text = text; l.ForeColor = c; }

    private static void StyleButton(Button b, string text, int x, int y, int w, bool primary)
    {
        b.Text = text; b.Location = new Point(x, y); b.Size = new Size(w, 32);
        b.FlatStyle = FlatStyle.Flat; b.FlatAppearance.BorderSize = 0;
        b.BackColor = primary ? Accent : Color.FromArgb(0x24, 0x28, 0x36);
        b.ForeColor = Color.White; b.Cursor = Cursors.Hand;
    }

    private void OpenUri(string uri)
    {
        try { Process.Start(new ProcessStartInfo(uri) { UseShellExecute = true }); }
        catch (Exception ex) { Log("Could not open settings: " + ex.Message); }
    }

    private void OpenReceived(object? sender, EventArgs e) => OpenUri(FileTransfer.Folder);

    private void RestoreFromTray()
    {
        Show();
        WindowState = FormWindowState.Normal;
        Activate();
    }

    private void SendToPhone(object payload)
    {
        string json = JsonSerializer.Serialize(payload, payload.GetType(), JsonOpts);
        if (_wifiConnected) _wifi.Send(json);
        if (_btConnected) _bt.Send(json);
    }

    private void Log(string msg)
    {
        string line = $"[{DateTime.Now:HH:mm:ss}] {msg}\r\n";
        if (_log.TextLength > 60000) _log.Clear();
        _log.AppendText(line);
        _logLines.Add(line.TrimEnd());
        if (_logLines.Count > 300) _logLines.RemoveRange(0, 100);
        if (_uiReady) Post(new { type = "log", line = line.TrimEnd() });
    }

    // ────────────────────────────────────────────────────────────────────
    //  Modern window
    // ────────────────────────────────────────────────────────────────────
    private static string Resource(string name)
    {
        using var s = typeof(MainForm).Assembly.GetManifestResourceStream(name);
        if (s == null) return "";
        using var r = new StreamReader(s);
        return r.ReadToEnd();
    }

    private static string ResourceDataUri(string name, string mime)
    {
        using var s = typeof(MainForm).Assembly.GetManifestResourceStream(name);
        if (s == null) return "";
        using var ms = new MemoryStream();
        s.CopyTo(ms);
        return $"data:{mime};base64," + Convert.ToBase64String(ms.ToArray());
    }

    private async Task InitModernUi()
    {
        string html = Resource("ui.html");
        if (html.Length == 0) { Log("Window file missing — using the classic window."); return; }
        try
        {
            // the page and the logo live in a real folder: the most reliable way to load them
            string dir = Path.Combine(Settings.DataDir, "ui");
            Directory.CreateDirectory(dir);
            File.WriteAllText(Path.Combine(dir, "ui.html"), html);
            using (var ls = typeof(MainForm).Assembly.GetManifestResourceStream("logo.png"))
            using (var fs = File.Create(Path.Combine(dir, "logo.png")))
                ls?.CopyTo(fs);

            var env = await CoreWebView2Environment.CreateAsync(null, Path.Combine(Settings.DataDir, "webview-ui"), null);
            var web = new WebView2 { Dock = DockStyle.Fill, DefaultBackgroundColor = Bg };
            Controls.Add(web);
            web.BringToFront();
            await web.EnsureCoreWebView2Async(env);
            var c = web.CoreWebView2;
            c.Settings.AreDevToolsEnabled = false;
            c.Settings.AreDefaultContextMenusEnabled = false;
            c.Settings.IsStatusBarEnabled = false;
            c.Settings.IsZoomControlEnabled = false;
            c.WebMessageReceived += OnUiMessage;
            c.SetVirtualHostNameToFolderMapping("uor.rc", dir, CoreWebView2HostResourceAccessKind.Allow);
            c.NavigationCompleted += (_, _) =>
            {
                // don't wait for the page to say hello — send everything right away
                _uiReady = true;
                Post(new { type = "state", logo = "https://uor.rc/logo.png" });
                foreach (var l in _logLines) Post(new { type = "log", line = l });
                PushUi(force: true);
            };
            c.Navigate("https://uor.rc/ui.html");
            _web = web;
            MinimumSize = new Size(780, 600);
            ClientSize = new Size(1000, 720);
            CenterToScreen();

            // if the page never shows anything, go back to the classic window instead of a blank one
            var guard = new System.Windows.Forms.Timer { Interval = 6000 };
            guard.Tick += (_, _) =>
            {
                guard.Stop(); guard.Dispose();
                if (_uiPainted) return;
                Log("The new window did not load — showing the classic window.");
                try { _web!.Visible = false; } catch { }
                _web = null; _uiReady = false;
                ClientSize = new Size(560, 700);
            };
            guard.Start();
        }
        catch (Exception ex)
        {
            Log("Modern window not available (" + ex.Message + ") — using the classic window.");
            _web = null; _uiReady = false;
        }
    }

    private void Post(object payload)
    {
        try { _web?.CoreWebView2?.PostWebMessageAsJson(JsonSerializer.Serialize(payload, payload.GetType(), JsonOpts)); } catch { }
    }

    private static string StateOf(Color c) => c == Good ? "ok" : c == Bad ? "bad" : "wait";

    private static string Detail(string label, string prefix)
    {
        string t = label.TrimStart('●', ' ');
        return t.StartsWith(prefix) ? t[prefix.Length..].Trim() : t;
    }

    /// <summary>Sends what the window shows (only when something changed).</summary>
    private void PushUi(bool force = false)
    {
        if (!_uiReady || _web == null) return;
        string ppt = _ppt.Status;
        var payload = new
        {
            type = "state",
            wifi = Detail(_wifiStatus.Text, "Wi-Fi:"), wifiState = StateOf(_wifiStatus.ForeColor),
            bt = Detail(_btStatus.Text, "Bluetooth:"), btState = StateOf(_btStatus.ForeColor),
            ppt = ppt.StartsWith("PowerPoint:") ? ppt["PowerPoint:".Length..].Trim() : ppt,
            pptState = ppt.StartsWith("Presenting") ? "show" : ppt.StartsWith("Open") ? "open" : "none",
            pin = _settings.Pin,
            hsOn = _hs?.On ?? false, ssid = _hs?.Ssid ?? "", pass = _hs?.Pass ?? "", clients = _hs?.Clients ?? 0,
            hsErr = _hs != null && !_hs.On ? _hs.Error : "",
            qr = _qrPng, auto = _settings.AutoHotspot,
            fileTxt = _fileStatus.Text, filePct = _filePct,
            version = "v" + (typeof(MainForm).Assembly.GetName().Version?.ToString(3) ?? "1.0")
        };
        string json = JsonSerializer.Serialize(payload, JsonOpts);
        if (!force && json == _lastUi) return;
        _lastUi = json;
        try { _web.CoreWebView2?.PostWebMessageAsJson(json); } catch { }
    }

    private async void OnUiMessage(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        string act = "", v = "";
        try
        {
            using var doc = JsonDocument.Parse(e.WebMessageAsJson);
            var m = doc.RootElement;
            act = m.TryGetProperty("act", out var a) ? a.GetString() ?? "" : "";
            if (m.TryGetProperty("v", out var vv)) v = vv.ValueKind == JsonValueKind.True ? "true" : vv.ValueKind == JsonValueKind.False ? "false" : vv.ToString();
        }
        catch { return; }
        switch (act)
        {
            case "ready":              // the page asks again until it has data
                _uiReady = true;
                Post(new { type = "state", logo = "https://uor.rc/logo.png" });
                PushUi(force: true);
                break;
            case "painted":            // the page confirmed it is showing the data
                _uiPainted = true;
                break;
            case "hotspot": await ToggleHotspot(); PushUi(true); break;
            case "hotspotSettings": OpenUri("ms-settings:network-mobilehotspot"); break;
            case "auto": _autoHotspot.Checked = v == "true"; PushUi(true); break;
            case "present": _present.PerformClick(); break;
            case "sendFiles": _sendFiles.PerformClick(); break;
            case "openFolder": OpenUri(FileTransfer.Folder); break;
            case "drop":
            {
                var paths = new List<string>();
                try
                {
                    foreach (var o in e.AdditionalObjects)
                        if (o is CoreWebView2File f && File.Exists(f.Path)) paths.Add(f.Path);
                }
                catch { }
                if (paths.Count == 0) break;
                if (!(_btConnected || _wifiConnected)) { Log("Connect the phone first."); break; }
                _ppt.Files.SendFiles(paths);
                break;
            }
        }
    }

    private void UI(Action a)
    {
        if (IsDisposed) return;
        try { if (InvokeRequired) BeginInvoke(a); else a(); }
        catch (ObjectDisposedException) { }
        catch (InvalidOperationException) { }
    }
}
