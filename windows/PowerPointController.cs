using System.Runtime.InteropServices;
using System.Text.Json;

namespace Remco;

/// <summary>
/// Drives the real PowerPoint on this PC through its COM automation interface.
/// PowerPoint itself plays the show, so every animation, transition, trigger,
/// morph and embedded video behaves exactly as on the laptop.
/// Things PowerPoint can't do (spotlight, custom laser, zoom, numbers, text,
/// projector timer) are drawn by <see cref="Overlay"/> on top of the show.
///
/// All methods run on the UI (STA) thread.
/// </summary>
internal sealed class PowerPointController
{
    private readonly Action<object> _send;
    private readonly Action<string> _log;
    private readonly Overlay _ov;
    private readonly AnnotationStore _store = new();
    private readonly ScreenMirror _mirror;
    private PhoneScreenForm? _phone;
    private readonly AudioPlayer _audio = new();
    private readonly BtSpeaker _speaker = new();
    private MediaViewer? _media;
    private bool MediaShowing => _media != null && _media.IsOpen;

    /// <summary>The screen the slide show is on (the projector), or the main screen.</summary>
    private Rectangle ShowArea() =>
        _ov.WindowArea.Width > 10 ? _ov.WindowArea : (Screen.PrimaryScreen?.Bounds ?? new Rectangle(0, 0, 1920, 1080));
    public FileTransfer Files { get; }
    private bool PhoneShowing => _phone != null && _phone.Visible;

    // 🧑‍🏫 whiteboard · 📑 PDF/Word · 🗳️ quiz · 🎲 picker
    private BoardForm? _board;
    private DocViewer? _doc;
    private QuizScreen? _quizScreen;
    private QuizServer? _quiz;
    private PickerScreen? _picker;
    private GalleryScreen? _gallery;
    private bool GalleryShowing => _gallery != null && _gallery.Visible;
    private bool BoardShowing => _board != null && _board.Visible;
    private bool DocShowing => _doc != null && _doc.Visible;
    private bool QuizShowing => _quizScreen != null && _quizScreen.Visible;
    private bool PickerShowing => _picker != null && _picker.Visible;

    /// <summary>The Remco full-screen view on top of the projector (null = PowerPoint / desktop).</summary>
    private Form? TopView =>
        MediaShowing ? _media : PhoneShowing ? _phone : BoardShowing ? _board : DocShowing ? _doc :
        QuizShowing ? _quizScreen : PickerShowing ? _picker : GalleryShowing ? _gallery : null;

    private string ViewName =>
        MediaShowing ? "media" : PhoneShowing ? "phone" : BoardShowing ? "board" : DocShowing ? "doc" :
        QuizShowing ? "quiz" : PickerShowing ? "picker" : GalleryShowing ? "gallery" : "";

    /// <summary>Which page of drawings (ink, numbers, text) belongs to what is on screen.</summary>
    private string ViewKey =>
        MediaShowing ? "media:" + _media!.CurrentName : PhoneShowing ? "phone" : BoardShowing ? _board!.Key :
        DocShowing ? _doc!.Key : QuizShowing ? "quiz" : PickerShowing ? "picker" : GalleryShowing ? _gallery!.Key : "";

    /// <summary>Show one view: every other Remco view steps aside (photos pause, the phone screen hides…).</summary>
    private long _suppressPhoneUntil;

    private void HideViews(string except)
    {
        if (except != "phone") _suppressPhoneUntil = Environment.TickCount64 + 1200;
        if (except != "media") _media?.Hide2();
        if (except != "phone") _phone?.HideScreen();
        if (except != "board" && BoardShowing) _board!.Hide();
        if (except != "doc" && DocShowing) _doc!.Hide();
        if (except != "quiz" && QuizShowing) _quizScreen!.Hide();
        if (except != "picker" && PickerShowing) _picker!.Hide();
        if (except != "gallery" && GalleryShowing) _gallery!.Hide();
        _lastStateKey = "";
        _ov.Invalidate2();
    }

    // tool state requested by the phone
    private string _tool = "mouse";
    private readonly SynchronizationContext? _ui;
    private void OnUi(Action a) { if (_ui != null) _ui.Post(_ => a(), null); else a(); }
    private int _inkBgr = 0x0000FF; // red, BGR

    private double _remX, _remY;
    private Ann? _stroke;
    private bool _erased;
    private readonly System.Windows.Forms.Timer _caretTimer = new() { Interval = 400 };

    // what we last told the phone
    private string _lastStateKey = "";
    private string _presKey = "";
    private string _slideKey = "";
    private int _lastThumbSlide = -1;
    private int _pendingThumb = -1;
    private bool _titlesSent;
    private bool _annsDirty = true;
    private bool _wasShowing;
    private double _sw = 16, _sh = 9;
    private readonly Dictionary<int, string> _thumbCache = new();
    private readonly Dictionary<int, string> _notesCache = new();
    private readonly string _tmpDir = Path.Combine(Path.GetTempPath(), "UOR-RC");
    private readonly System.Windows.Forms.Timer _thumbTimer = new() { Interval = 150 };
    private int _pollCount;

    public string Status { get; private set; } = "PowerPoint: not detected";

    public PowerPointController(Action<object> send, Action<string> log, Overlay overlay)
    {
        _send = send;
        _log = log;
        _ov = overlay;
        _mirror = new ScreenMirror(send, () => _ov.WindowArea);
        Files = new FileTransfer(send, log);
        _speaker.StartWatching();
        _media = new MediaViewer(send, log);   // created now: it also empties the temp media folder from last time
        _media.ItemChanged += () => { _lastStateKey = ""; Poll(); _ov.Invalidate2(); };
        Files.MediaReceived += (path, index) =>
        {
            if (index == 0) _media!.NewBatch();
            _phone?.HideScreen();                     // switching from the phone screen to photos / videos
            _media!.Add(path, ShowArea());
            _ov.Invalidate2();
        };
        Files.DocReceived += path => OpenDocument(path);
        _ui = SynchronizationContext.Current;
        Directory.CreateDirectory(_tmpDir);
        _thumbTimer.Tick += (_, _) => { _thumbTimer.Stop(); SendPendingThumbs(); };
        _caretTimer.Tick += (_, _) =>
        {
            _caretTimer.Stop();
            if (!_wasShowing || Native.IsIconicShow) { if (Native.TextCaretVisible()) _send(new { e = "caret" }); }
        };
        _store.Open("__desktop__");
        RefreshSlideArea();
    }

    private sealed class Ctx
    {
        public dynamic App = null!;
        public dynamic? Win;   // SlideShowWindow (null if not presenting)
        public dynamic? View;  // SlideShowView
        public dynamic? Pres;  // Presentation
    }

    private Ctx? Open()
    {
        object? o = Native.GetRunningComObject("PowerPoint.Application");
        if (o == null) return null;
        var c = new Ctx { App = o };
        try
        {
            if ((int)c.App.SlideShowWindows.Count > 0)
            {
                c.Win = c.App.SlideShowWindows.Item(1);
                c.View = c.Win.View;
                c.Pres = c.Win.Presentation;
            }
        }
        catch { c.Win = null; c.View = null; }
        if (c.Pres == null)
        {
            try { if ((int)c.App.Presentations.Count > 0) c.Pres = c.App.ActivePresentation; } catch { }
        }
        return c;
    }

    private static void Release(Ctx? c)
    {
        if (c == null) return;
        try { Marshal.ReleaseComObject((object)c.App); } catch { }
    }

    public void OnPhoneConnected()
    {
        _lastStateKey = "";
        _lastThumbSlide = -1;
        _titlesSent = false;
        _annsDirty = true;
        Poll();
    }

    public void OnPhoneDisconnected()
    {
        _mirror.Stop();
        _phone?.HideScreen();
        _audio.Stop();
        _speaker.Close();
        _media?.Close2();
        HideViews("");
        Files.CancelAll();
        _ov.ClearAll();
        _ov.TimerVisible = false;
        _ov.Invalidate2();
    }

    // ────────────────────────────────────────────────────────────────────
    //  Commands from the phone
    // ────────────────────────────────────────────────────────────────────
    public void Handle(string line)
    {
        JsonElement m;
        try { m = JsonDocument.Parse(line).RootElement; } catch { return; }
        string c = Str(m, "c");

        // Fast path: things that don't need PowerPoint.
        switch (c)
        {
            case "rel":
                _remX += Num(m, "dx"); _remY += Num(m, "dy");
                int ix = (int)_remX, iy = (int)_remY;
                _remX -= ix; _remY -= iy;
                Native.MoveRelative(ix, iy);
                return;
            case "btn":
                Native.Button(Str(m, "b"), Str(m, "a"));
                if (Str(m, "a") is "click" or "dblclick" or "up") { _caretTimer.Stop(); _caretTimer.Start(); }
                return;
            case "scroll": Native.Wheel((int)Num(m, "d")); return;
            case "hello": OnPhoneConnected(); return;
            case "ping": _send(new { e = "pong" }); return;
            case "abs": MoveToSlidePoint(Num(m, "x"), Num(m, "y")); return;
            case "sabs":   // absolute point on the mirrored screen
            {
                var r = _mirror.Rect;
                if (r.Width > 0)
                    Native.MoveAbsolute((int)Math.Round(r.Left + Math.Clamp(Num(m, "x"), 0, 1) * (r.Width - 1)),
                                        (int)Math.Round(r.Top + Math.Clamp(Num(m, "y"), 0, 1) * (r.Height - 1)));
                return;
            }
            case "mirror":
                if (Bool(m, "on")) _mirror.Start((int)Num(m, "w")); else _mirror.Stop();
                return;
            case "frame_ack": _mirror.Ack(); return;
            case "desktop":
                HideViews("");
                Native.ShowDesktop();
                return;

            // ── the phone's own screen, full-screen on the projector ──
            case "phone_frame":
                try
                {
                    byte[] jpg = Convert.FromBase64String(Str(m, "img"));
                    using var ms = new MemoryStream(jpg);
                    using var img = Image.FromStream(ms);
                    var bmp = new Bitmap(img);
                    if (Environment.TickCount64 < _suppressPhoneUntil) { _send(new { e = "phone_ack" }); return; }
                    _phone ??= new PhoneScreenForm();
                    if (!PhoneShowing) HideViews("phone");  // switching to the phone screen / document camera
                    _phone.ShowFrame(bmp, _ov.WindowArea.Width > 10 ? _ov.WindowArea : (Screen.PrimaryScreen?.Bounds ?? new Rectangle(0, 0, 1920, 1080)));
                    _ov.Invalidate2();
                }
                catch { }
                _send(new { e = "phone_ack" });   // ready for the next frame
                return;
            case "phone_view":
                _phone ??= new PhoneScreenForm();
                _phone.Rotation = ((int)Num(m, "rot") % 360 + 360) % 360;
                _phone.Fill = Bool(m, "fill");
                _phone.Invalidate();
                return;
            case "audio":
                try { _audio.Add(Convert.FromBase64String(Str(m, "d")), (int)Num(m, "r"), (int)Num(m, "ch")); } catch { }
                return;
            case "audio_stop": _audio.Stop(); return;

            // 🔈 sound only on the PC: the PC becomes the phone's Bluetooth speaker
            case "pc_speaker":
                if (Bool(m, "on"))
                {
                    string phone = Str(m, "name");
                    _log("Making this PC the phone's Bluetooth speaker…");
                    _ = Task.Run(async () =>
                    {
                        var (ok, msg) = await _speaker.OpenAsync(phone);
                        OnUi(() =>
                        {
                            _log(ok ? "🔈 The phone's sound now plays on this PC (" + msg + ")." : "Bluetooth speaker failed: " + msg);
                            _send(new { e = "pc_speaker", on = ok, error = ok ? "" : msg });
                        });
                    });
                }
                else
                {
                    _speaker.Close();
                    _send(new { e = "pc_speaker", on = false, error = "" });
                }
                return;
            case "fs": Files.Handle(m); return;
            case "media":
                if (Str(m, "a") == "show")
                {
                    HideViews("media");
                    _media!.ShowAgain(ShowArea());
                    _ov.Invalidate2();
                    return;
                }
                _media?.Command(Str(m, "a"), Num(m, "v"));
                if (Str(m, "a") == "close") { _lastStateKey = ""; Poll(); }
                return;
            // ── 🔎 magnifier lens ──
            case "lens":
                _ov.LensX = Num(m, "x"); _ov.LensY = Num(m, "y");
                _ov.LensActive = Bool(m, "active");
                if (Has(m, "radius")) _ov.LensRadius = Math.Clamp((int)Num(m, "radius"), 60, 400);
                if (Has(m, "zoom")) _ov.LensZoom = Math.Clamp(Num(m, "zoom"), 1.5, 5);
                if (Has(m, "bright")) _ov.LensBright = (int)Num(m, "bright");
                if (Has(m, "dim")) _ov.LensDim = Bool(m, "dim");
                _ov.Invalidate2();
                return;
            case "lens_style":
                if (Has(m, "radius")) _ov.LensRadius = Math.Clamp((int)Num(m, "radius"), 60, 400);
                if (Has(m, "zoom")) _ov.LensZoom = Math.Clamp(Num(m, "zoom"), 1.5, 5);
                if (Has(m, "bright")) _ov.LensBright = (int)Num(m, "bright");
                if (Has(m, "dim")) _ov.LensDim = Bool(m, "dim");
                _ov.Invalidate2();
                return;

            // ── 📸 group photos from the phone camera ──
            case "shot":
            {
                try
                {
                    byte[] jpg = Convert.FromBase64String(Str(m, "img"));
                    string dir = Path.Combine(FileTransfer.Folder, "Camera");
                    Directory.CreateDirectory(dir);
                    string file = Path.Combine(dir, $"{DateTime.Now:yyyy-MM-dd HH-mm-ss} {Clean(Str(m, "label"))}.jpg".Replace("  ", " "));
                    File.WriteAllBytes(file, jpg);
                    using var ms = new MemoryStream(jpg);
                    using var img = Image.FromStream(ms);
                    _gallery ??= new GalleryScreen();
                    _gallery.Add(new Bitmap(img), Str(m, "label"), file);
                    _log("📸 Photo saved: " + file);
                    if (Bool(m, "show")) { HideViews("gallery"); _gallery.Single = Bool(m, "single"); _gallery.Open(ShowArea()); }
                }
                catch (Exception ex) { _log("Photo failed: " + ex.Message); }
                _lastStateKey = "";
                Poll();
                return;
            }
            case "gallery":
                if (_gallery == null) return;
                switch (Str(m, "a"))
                {
                    case "show": HideViews("gallery"); _gallery.Open(ShowArea()); break;
                    case "grid": _gallery.Single = false; _gallery.Invalidate(); break;
                    case "single": _gallery.Single = true; _gallery.Invalidate(); break;
                    case "next": _gallery.Next(); break;
                    case "prev": _gallery.Prev(); break;
                    case "clear": _gallery.Clear(); break;
                    case "close": HideViews(""); _lastThumbSlide = -1; break;
                }
                _lastStateKey = "";
                Poll();
                return;

            // ── 🧑‍🏫 whiteboard ──
            case "board": Board(Str(m, "a"), Str(m, "v")); return;

            // ── 📑 PDF / Word pages ──
            case "doc":
                if (_doc == null) return;
                switch (Str(m, "a"))
                {
                    case "next": _ = _doc.Next(); break;
                    case "prev": _ = _doc.Prev(); break;
                    case "goto": _ = _doc.GoTo((int)Num(m, "n")); break;
                    case "close": _doc.CloseDoc(); HideViews(""); _lastThumbSlide = -1; Poll(); break;
                }
                return;

            // ── 🗳️ live quiz ──
            case "quiz": Quiz(m); return;

            // ── 🎲 random picker ──
            case "picker":
                if (Str(m, "a") == "close") { if (PickerShowing) _picker!.Hide(); _lastStateKey = ""; Poll(); return; }
                {
                    var names = m.TryGetProperty("names", out var nameArr) && nameArr.ValueKind == JsonValueKind.Array
                        ? nameArr.EnumerateArray().Select(v => v.GetString() ?? "").Where(v => v.Length > 0).ToList() : new List<string>();
                    if (names.Count == 0) return;
                    HideViews("picker");
                    if (_picker == null)
                    {
                        _picker = new PickerScreen();
                        _picker.Done += name => _send(new { e = "picked", name });
                    }
                    _picker.Pick(names, (int)Num(m, "winner"), Str(m, "title"),
                        m.TryGetProperty("remaining", out var rv) ? rv.GetInt32() : -1, ShowArea(), Str(m, "style"));
                    _lastStateKey = "";
                }
                return;

            case "view_close":   // ✕ on the phone: back to what was underneath
                HideViews("");
                _lastThumbSlide = -1;
                Poll();
                return;

            case "phone_stop":
                _phone?.HideScreen();
                _audio.Stop();
                _lastStateKey = "";
                Poll();
                return;

            // ── keyboard from the phone ──
            case "type": Native.TypeText(Str(m, "text")); return;
            case "key":
                Native.KeyCombo(Str(m, "k"), Bool(m, "ctrl"), Bool(m, "shift"), Bool(m, "alt"), Bool(m, "win"), Math.Max(1, (int)Num(m, "n")));
                return;

            // ── join the phone's hotspot (name + password sent over Bluetooth) ──
            case "join_wifi":
            {
                string ssid = Str(m, "ssid"), pass = Str(m, "pass");
                bool wpa3 = Str(m, "sec") == "wpa3";
                _log($"Joining the phone's hotspot \"{ssid}\"…");
                Task.Run(() =>
                {
                    string r = WifiJoin.Join(ssid, pass, wpa3);
                    _log("Wi-Fi: " + r);
                });
                return;
            }

            // ── pen / highlighter drawn by Remco (any thickness) ──
            case "ink_start":
            {
                var (sx, sy) = _ov.ScreenToSlide(Num(m, "x"), Num(m, "y"));
                _stroke = new Ann
                {
                    Kind = "ink", X = sx, Y = sy, Color = Str(m, "color").Length > 0 ? Str(m, "color") : "#ff3b30",
                    Size = Math.Clamp((int)Num(m, "size"), 1, 80), Hl = Bool(m, "hl"), Pts = new List<double> { sx, sy }
                };
                _store.For(_slideKey).Add(_stroke);
                _ov.Anns = _store.For(_slideKey);
                _ov.Invalidate2();
                return;
            }
            case "ink_pts":
                if (_stroke != null && m.TryGetProperty("p", out var arr) && arr.ValueKind == JsonValueKind.Array)
                {
                    var list = arr.EnumerateArray().Select(v => v.GetDouble()).ToList();
                    for (int i = 0; i + 1 < list.Count; i += 2)
                    {
                        var (sx, sy) = _ov.ScreenToSlide(list[i], list[i + 1]);
                        _stroke.Pts!.Add(Math.Round(sx, 5)); _stroke.Pts.Add(Math.Round(sy, 5));
                    }
                    _ov.Invalidate2();
                }
                return;
            case "ink_end":
                _stroke = null;
                AnnsChanged();
                return;
            case "ink_erase":
            {
                double x = Num(m, "x"), y = Num(m, "y");
                float r = _ov.CssPx(18);
                int removed = _store.For(_slideKey).RemoveAll(a => a.Kind == "ink" && _ov.InkDistancePx(a, x, y) < r);
                if (removed > 0) { _ov.Invalidate2(); _erased = true; }
                return;
            }
            case "ink_erase_end":
                if (_erased) { _erased = false; AnnsChanged(); }
                return;

            case "laser":
                _ov.LaserX = Num(m, "x"); _ov.LaserY = Num(m, "y");
                _ov.LaserActive = Bool(m, "active");
                if (Has(m, "size")) _ov.LaserSize = (int)Num(m, "size");
                if (Has(m, "color")) _ov.LaserColor = Overlay.Html(Str(m, "color"));
                if (Has(m, "label")) _ov.LaserLabel = Str(m, "label");
                _ov.Invalidate2();
                return;
            case "laser_style":
                if (Has(m, "size")) _ov.LaserSize = (int)Num(m, "size");
                if (Has(m, "color")) _ov.LaserColor = Overlay.Html(Str(m, "color"));
                if (Has(m, "label")) _ov.LaserLabel = Str(m, "label");
                _ov.Invalidate2();
                return;
            case "spot":
                _ov.SpotX = Num(m, "x"); _ov.SpotY = Num(m, "y");
                _ov.SpotActive = Bool(m, "active");
                ApplySpotStyle(m);
                _ov.Invalidate2();
                return;
            case "spot_style":
                ApplySpotStyle(m);
                _ov.Invalidate2();
                return;
            case "zoom":
                _ov.ZoomScale = Math.Clamp(Num(m, "s"), 1, 4);
                _ov.ZoomX = Num(m, "x"); _ov.ZoomY = Num(m, "y");
                _ov.Invalidate2();
                return;
            case "ann_preview":
            {
                var (sx, sy) = _ov.ScreenToSlide(Num(m, "x"), Num(m, "y"));
                _ov.PreviewKind = Str(m, "kind") == "text" ? "text" : "number";
                _ov.PreviewX = sx; _ov.PreviewY = sy;
                var list = _store.For(_slideKey);
                int count = list.Count(a => a.Kind == "number");
                _ov.PreviewColor = _ov.PreviewKind == "number" ? AnnotationStore.NumberColors[count % 10] : Str(m, "color");
                _ov.PreviewText = (count + 1).ToString();
                _ov.Invalidate2();
                return;
            }
            case "ann_preview_end": _ov.PreviewKind = null; _ov.Invalidate2(); return;
            case "ann_tap": AnnTap(m); return;
            case "text_save": TextSave(m); return;
            case "ann_delete":
                _store.For(_slideKey).RemoveAll(a => a.Id == Str(m, "id"));
                AnnsChanged();
                return;
            case "timer":
                _ov.TimerMode = Str(m, "mode") == "up" ? "up" : "down";
                _ov.TimerSeconds = (int)Num(m, "sec");
                _ov.TimerVisible = Bool(m, "visible");
                _ov.Invalidate2();
                return;
            case "timer_alert":
                if (_ov.TimerVisible)
                {
                    string label = Str(m, "label");
                    _ov.TimerAlert(label, label == "0" ? 1600 : 900);
                }
                return;
        }

        Ctx? ctx = null;
        try
        {
            ctx = Open();
            if (MediaShowing && c is "next" or "prev")
            {
                if (c == "next") _media!.Next(); else _media!.Prev();
                return;
            }
            if (BoardShowing && c is "next" or "prev")
            {
                if (c == "next") _board!.NextPage(); else _board!.PrevPage();
                return;
            }
            if (GalleryShowing && _gallery!.Single && c is "next" or "prev")
            {
                if (c == "next") _gallery.Next(); else _gallery.Prev();
                return;
            }
            if (DocShowing && c is "next" or "prev")
            {
                _ = c == "next" ? _doc!.Next() : _doc!.Prev();
                return;
            }
            switch (c)
            {
                case "next": Next(ctx); break;
                case "prev": Prev(ctx); break;
                case "goto": GoTo(ctx, (int)Num(m, "n")); break;
                case "start": Start(ctx, Str(m, "from") == "current"); break;
                case "end": if (ctx?.View != null) ctx.View.Exit(); else Native.Key(Native.VK_ESCAPE); break;
                case "screen": ToggleScreen(ctx, Str(m, "m")); break;
                case "tool": _tool = Str(m, "t"); ApplyTool(ctx); _ov.PreviewKind = null; _ov.LaserActive = false; _ov.SpotActive = false; _ov.Invalidate2(); break;
                case "color": _inkBgr = ParseBgr(Str(m, "rgb")); break;
                case "erase": if (ctx?.View != null) ctx.View.EraseDrawing(); else Native.Key(Native.VK_E); break;
                case "back_show": BackToShow(ctx); break;
                case "clear":
                    if (ctx?.View != null) ctx.View.EraseDrawing();
                    _store.For(_slideKey).Clear();
                    AnnsChanged();
                    break;
            }
        }
        catch (COMException ex) { _log("PowerPoint did not accept '" + c + "': " + ex.Message); }
        catch (Exception ex) { _log("Command '" + c + "' failed: " + ex.Message); }
        finally { Release(ctx); }
        Poll();
    }

    private void ApplySpotStyle(JsonElement m)
    {
        if (Has(m, "radius")) _ov.SpotRadius = Math.Clamp((int)Num(m, "radius"), 40, 600);
        if (Has(m, "style")) _ov.SpotStyle = Str(m, "style");
        if (Has(m, "confetti")) _ov.SpotConfetti = Bool(m, "confetti");
        if (Has(m, "color"))
        {
            string col = Str(m, "color");
            _ov.SpotColor = col.Length == 0 ? null : Overlay.Html(col);
        }
    }

    // ── numbers & text ─────────────────────────────────────────────────
    private void AnnTap(JsonElement m)
    {
        _ov.PreviewKind = null;
        double x = Num(m, "x"), y = Num(m, "y");
        string kind = Str(m, "kind") == "text" ? "text" : "number";
        var list = _store.For(_slideKey);
        string? hit = _ov.HitTest(x, y, kind);
        var (sx, sy) = _ov.ScreenToSlide(x, y);

        if (kind == "number")
        {
            if (hit != null) list.RemoveAll(a => a.Id == hit);  // tap an existing badge = delete (web behaviour)
            else
            {
                int count = list.Count(a => a.Kind == "number");
                list.Add(new Ann
                {
                    Kind = "number", X = sx, Y = sy,
                    Color = AnnotationStore.NumberColors[count % 10],
                    Text = (count + 1).ToString(),
                    Size = Math.Clamp((int)Num(m, "size"), 16, 64)
                });
            }
            AnnsChanged();
        }
        else
        {
            var existing = hit == null ? null : list.FirstOrDefault(a => a.Id == hit);
            if (existing != null) _send(new { e = "edit_text", id = existing.Id, text = existing.Text });
            else _send(new { e = "new_text", sx, sy });
            _ov.Invalidate2();
        }
    }

    private void TextSave(JsonElement m)
    {
        var list = _store.For(_slideKey);
        string id = Str(m, "id");
        string text = Str(m, "text").Trim();
        var a = id.Length > 0 ? list.FirstOrDefault(v => v.Id == id) : null;
        if (a != null)
        {
            if (text.Length == 0) list.Remove(a); else a.Text = text;
        }
        else if (text.Length > 0)
        {
            list.Add(new Ann
            {
                Kind = "text", X = Num(m, "sx"), Y = Num(m, "sy"), Text = text,
                Style = Str(m, "style") is "plain" or "box" ? Str(m, "style") : "pin",
                Color = Str(m, "color").Length > 0 ? Str(m, "color") : "#eab308",
                TextColor = Str(m, "textColor").Length > 0 ? Str(m, "textColor") : "#111827",
                FontSize = Math.Clamp((int)Num(m, "fontSize"), 10, 32)
            });
        }
        AnnsChanged();
    }

    private void AnnsChanged()
    {
        _store.Save();
        _ov.Anns = _store.For(_slideKey);
        _ov.Invalidate2();
        _annsDirty = true;
        SendAnns();
    }

    private void SendAnns()
    {
        if (!_annsDirty) return;
        _annsDirty = false;
        _send(new { e = "ann", items = _store.For(_slideKey) });
    }

    // ── navigation ─────────────────────────────────────────────────────
    private static void Next(Ctx? ctx)
    {
        if (ctx?.View != null) ctx.View.Next();          // next animation / transition, exactly like the keyboard
        else Native.Key(Native.VK_NEXT);                 // other apps (PDF, browser…)
    }

    /// <summary>
    /// PowerPoint's View.Previous() gets stuck on slides whose animations start
    /// automatically ("With/After Previous"): it rewinds and replays them instead
    /// of leaving the slide. So: step back one animation while there are clicked
    /// animations to undo, otherwise jump to the previous visible slide and show
    /// it fully built — the same thing pressing ← on the laptop does.
    /// </summary>
    private static void Prev(Ctx? ctx)
    {
        if (ctx?.View == null) { Native.Key(Native.VK_PRIOR); return; }
        dynamic v = ctx.View;
        int state = (int)v.State;
        if (state == 5) { v.Previous(); return; }       // "End of slide show" screen

        int click = -1;
        try { click = (int)v.GetClickIndex(); } catch { }
        if (click > 0) { v.Previous(); return; }        // undo one animation

        int cur;
        try { cur = (int)v.Slide.SlideIndex; } catch { v.Previous(); return; }
        int target = PrevVisible((object?)ctx.Pres, cur);
        if (target < 1) return;                          // already on the first slide
        v.GotoSlide(target, 0 /*msoFalse: keep its state*/);
        try
        {
            int clicks = (int)v.GetClickCount();
            if (clicks > 0 && (int)v.GetClickIndex() < clicks) v.GotoClick(clicks); // fully built
        }
        catch { }
    }

    private static int PrevVisible(object? presObj, int cur)
    {
        if (presObj == null) return cur - 1;
        dynamic pres = presObj;
        for (int i = cur - 1; i >= 1; i--)
        {
            try
            {
                if (Convert.ToInt32((object)pres.Slides.Item(i).SlideShowTransition.Hidden) == 0) return i;
            }
            catch { return i; }
        }
        return 0;
    }

    // ────────────────────────────────────────────────────────────────────
    //  🧑‍🏫 Whiteboard
    // ────────────────────────────────────────────────────────────────────
    private void Board(string a, string v)
    {
        switch (a)
        {
            case "open":
                if (_board == null)
                {
                    _board = new BoardForm();
                    _board.Changed += () => { _lastStateKey = ""; Poll(); };
                }
                // pages written in an earlier lesson are still there
                int pages = 1;
                foreach (var k in _store.Keys)
                    if (k.StartsWith("board:") && int.TryParse(k[6..], out int n) && _store.For(k).Count > 0) pages = Math.Max(pages, n);
                _board.SetPages(pages);
                if (v.Length > 0) _board.SetBg(v);
                HideViews("board");
                _board.Open(ShowArea());
                break;
            case "next": _board?.NextPage(); break;
            case "prev": _board?.PrevPage(); break;
            case "bg": _board?.SetBg(v); break;
            case "save": SaveBoard(); break;
            case "close": HideViews(""); _lastThumbSlide = -1; break;
        }
        _lastStateKey = "";
        Poll();
    }

    /// <summary>Every whiteboard page → a PNG picture in Downloads\Remco\Whiteboard …</summary>
    private void SaveBoard()
    {
        if (_board == null) return;
        try
        {
            var a = _board.Bounds;
            int w = Math.Max(1280, a.Width), h = (int)Math.Round(w * (double)a.Height / Math.Max(1, a.Width));
            string dir = Path.Combine(FileTransfer.Folder, "Whiteboard " + DateTime.Now.ToString("yyyy-MM-dd HH-mm"));
            Directory.CreateDirectory(dir);
            for (int p = 1; p <= _board.Pages; p++)
            {
                using var bmp = new Bitmap(w, h);
                using (var g = Graphics.FromImage(bmp))
                {
                    var r = new RectangleF(0, 0, w, h);
                    BoardForm.PaintBackground(g, r, _board.Bg);
                    _ov.RenderPage(g, r, _store.For("board:" + p));
                }
                bmp.Save(Path.Combine(dir, $"page {p}.png"), System.Drawing.Imaging.ImageFormat.Png);
            }
            _log($"Whiteboard saved ({_board.Pages} page{(_board.Pages == 1 ? "" : "s")}): {dir}");
            _send(new { e = "saved", path = dir, pages = _board.Pages });
        }
        catch (Exception ex) { _log("Could not save the whiteboard: " + ex.Message); }
    }

    // ────────────────────────────────────────────────────────────────────
    //  🗳️ Live quiz / poll
    // ────────────────────────────────────────────────────────────────────
    private void Quiz(JsonElement m)
    {
        switch (Str(m, "a"))
        {
            case "start":          // a new question (also used for "next question")
                if (_quiz == null)
                {
                    _quiz = new QuizServer();
                    _quiz.Changed += () => OnUi(QuizUpdated);
                }
                if (!_quiz.Start()) { _log("The quiz could not start (ports 8088-8095 are busy)."); return; }
                Task.Run(() => QuizServer.EnsureFirewallRule(_log));
                var opts = m.TryGetProperty("opts", out var oa) && oa.ValueKind == JsonValueKind.Array
                    ? oa.EnumerateArray().Select(v => v.GetString() ?? "").ToList()
                    : new List<string>();
                int n = (int)Num(m, "n");
                while (opts.Count < Math.Max(2, n)) opts.Add("");
                _quiz.NewQuestion(Str(m, "q"), opts, m.TryGetProperty("correct", out var cv) ? cv.GetInt32() : -1,
                    (int)Num(m, "qi"), (int)Num(m, "qn"), (int)Num(m, "secs"));
                _quizScreen ??= new QuizScreen();
                _quizScreen.ShowScores = false;
                SetQuizCodes();
                HideViews("quiz");
                _quizScreen.Open(ShowArea());
                _log("Quiz open at " + _quiz.BestUrl());
                break;
            case "reveal": _quiz?.ShowResults(); break;
            case "correct": _quiz?.SetCorrect((int)Num(m, "v")); break;
            case "scores":         // 🏆 leaderboard full-screen
                if (_quizScreen != null) { _quizScreen.ShowScores = !_quizScreen.ShowScores; HideViews("quiz"); _quizScreen.Open(ShowArea()); }
                break;
            case "reset": _quiz?.ResetScores(); break;
            case "show":
                if (_quizScreen != null) { HideViews("quiz"); _quizScreen.Open(ShowArea()); }
                break;
            case "close":
                _quiz?.Close();
                HideViews("");
                _lastThumbSlide = -1;
                break;
        }
        QuizUpdated();
        _lastStateKey = "";
        Poll();
    }

    private void SetQuizCodes()
    {
        if (_quiz == null || _quizScreen == null) return;
        string url = _quiz.BestUrl();
        _quizScreen.Url = url;
        _quizScreen.UrlQr?.Dispose();
        _quizScreen.UrlQr = Qr(url);
        var hs = Hotspot.Status();
        _quizScreen.WifiQr?.Dispose();
        _quizScreen.WifiQr = null;
        _quizScreen.WifiName = "";
        if (hs.On)
        {
            _quizScreen.WifiName = hs.Ssid; _quizScreen.WifiPass = hs.Pass;
            _quizScreen.WifiQr = Qr(Hotspot.WifiQr(hs.Ssid, hs.Pass));
        }
    }

    private static Image? Qr(string text)
    {
        try
        {
            using var gen = new QRCoder.QRCodeGenerator();
            using var data = gen.CreateQrCode(text, QRCoder.QRCodeGenerator.ECCLevel.M);
            byte[] png = new QRCoder.PngByteQRCode(data).GetGraphic(10);
            using var ms = new MemoryStream(png);
            return new Bitmap(Image.FromStream(ms));
        }
        catch { return null; }
    }

    private void QuizUpdated()
    {
        if (_quiz == null) return;
        var counts = _quiz.Counts();
        var top = _quiz.Top(8);
        if (_quizScreen != null)
        {
            _quizScreen.Question = _quiz.Question;
            _quizScreen.Options = _quiz.Options;
            _quizScreen.Counts = counts;
            _quizScreen.Reveal = _quiz.Reveal;
            _quizScreen.Correct = _quiz.Correct;
            _quizScreen.IsOpen = _quiz.IsOpen;
            _quizScreen.QIndex = _quiz.QIndex; _quizScreen.QCount = _quiz.QCount;
            _quizScreen.Players = _quiz.PlayerCount;
            _quizScreen.Left = _quiz.Left;
            _quizScreen.QuizSecs = Math.Max(1, _quiz.Secs);
            _quizScreen.Top = top;
            _quizScreen.Invalidate();
        }
        _send(new
        {
            e = "quiz", open = _quiz.IsOpen, reveal = _quiz.Reveal, correct = _quiz.Correct,
            n = _quiz.OptionCount, counts, total = _quiz.Total, players = _quiz.PlayerCount, left = _quiz.Left,
            showing = QuizShowing, url = _quiz.BestUrl(), qi = _quiz.QIndex, qn = _quiz.QCount,
            top = top.Select(t => new { name = t.Name, score = t.Score }).ToList()
        });
    }

    // ────────────────────────────────────────────────────────────────────
    //  📑 PDF / Word / PowerPoint files (from the phone or from this PC)
    // ────────────────────────────────────────────────────────────────────
    public async void OpenDocument(string path)
    {
        string ext = Path.GetExtension(path).ToLowerInvariant();
        string name = Path.GetFileName(path);
        try
        {
            if (ext is ".pptx" or ".ppt" or ".ppsx" or ".pps" or ".pptm" or ".odp") { OpenInPowerPoint(path); return; }
            if (ext is ".jpg" or ".jpeg" or ".png" or ".gif" or ".webp" or ".bmp" or ".mp4" or ".mov" or ".m4v" or ".webm" or ".mkv")
            {
                Directory.CreateDirectory(MediaViewer.Folder);
                string copy = Path.Combine(MediaViewer.Folder, name);
                if (!string.Equals(Path.GetFullPath(copy), Path.GetFullPath(path), StringComparison.OrdinalIgnoreCase)) File.Copy(path, copy, true);
                HideViews("media");
                _media!.NewBatch();
                _media.Add(copy, ShowArea());
                return;
            }
            string? pdf = ext == ".pdf" ? path
                : ext is ".docx" or ".doc" or ".rtf" or ".odt" or ".txt" or ".docm" ? await DocViewer.WordToPdf(path, _log)
                : null;
            if (pdf == null)
            {
                _log($"Opening {name} with its normal Windows program (NEXT / ◀ send Page Down / Page Up).");
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(path) { UseShellExecute = true });
                return;
            }
            if (_doc == null)
            {
                _doc = new DocViewer();
                _doc.Changed += () => { _lastStateKey = ""; Poll(); _ = SendDocThumbs(); };
            }
            if (!await _doc.LoadAsync(pdf, name)) { _log(name + " has no pages."); return; }
            HideViews("doc");
            _doc.Open(ShowArea());
            await _doc.GoTo(1);
            _log($"Presenting {name} ({_doc.Pages} pages).");
        }
        catch (Exception ex) { _log($"Could not open {name}: {ex.Message}"); }
    }

    private async Task SendDocThumbs()
    {
        if (_doc == null || !DocShowing) return;
        int p = _doc.Page;
        try
        {
            _send(new { e = "thumb", which = "cur", slide = p, img = await _doc.Thumb(p, 960) });
            if (p + 1 <= _doc.Pages) _send(new { e = "thumb", which = "next", slide = p + 1, img = await _doc.Thumb(p + 1, 400) });
        }
        catch { }
    }

    private void OpenInPowerPoint(string path)
    {
        object? o = Native.GetRunningComObject("PowerPoint.Application");
        if (o == null)
        {
            var t = Type.GetTypeFromProgID("PowerPoint.Application");
            if (t == null)
            {
                _log("PowerPoint isn't installed — opening the file with Windows instead.");
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(path) { UseShellExecute = true });
                return;
            }
            o = Activator.CreateInstance(t);
        }
        try
        {
            dynamic app = o!;
            try { app.Visible = -1; } catch { }
            dynamic pres = app.Presentations.Open(Path.GetFullPath(path), -1, 0, -1);   // ReadOnly, not Untitled, WithWindow
            HideViews("");
            pres.SlideShowSettings.Run();
            _log("Presenting " + Path.GetFileName(path));
        }
        finally { try { Marshal.ReleaseComObject(o!); } catch { } }
        _lastStateKey = ""; _lastThumbSlide = -1;
        Poll();
    }

    /// <summary>Back to the full-screen slide show (restores it after "Show desktop", or starts it from the current slide).</summary>
    private void BackToShow(Ctx? ctx)
    {
        HideViews("");
        _suppressPhoneUntil = Environment.TickCount64 + 2000;   // ignore frames still on their way
        _ov.Hidden = false;
        _ov.Invalidate2();
        bool ok = false;
        try
        {
            if (ctx?.Win != null)
            {
                var h = new IntPtr(Convert.ToInt64((object)ctx.Win.HWND));
                Native.ForceForeground(h);
                try { ctx.Win.Activate(); } catch { }
                ok = true;
            }
            else if (ctx?.Pres != null)
            {
                Start(ctx, fromCurrent: true);      // the show had ended → start it again here
                ok = true;
            }
        }
        catch (Exception ex) { _log("Back to PowerPoint: " + ex.Message); }
        if (!ok) _log("Back to PowerPoint: no presentation is open in PowerPoint.");
        _lastStateKey = "";
        _lastThumbSlide = -1;
    }

    private void GoTo(Ctx? ctx, int n)
    {
        if (ctx?.Pres == null || n < 1) return;
        int total = (int)ctx.Pres.Slides.Count;
        n = Math.Min(n, total);
        if (ctx.View != null) ctx.View.GotoSlide(n);
        else StartAt(ctx, n);
    }

    private void Start(Ctx? ctx, bool fromCurrent)
    {
        if (ctx?.Pres == null) { Native.Key(Native.VK_F5); return; }
        if (ctx.View != null) { if (!fromCurrent) ctx.View.First(); return; }
        int start = 1;
        if (fromCurrent)
        {
            try { start = (int)ctx.App.ActiveWindow.View.Slide.SlideIndex; } catch { start = 1; }
        }
        StartAt(ctx, start);
    }

    private void StartAt(Ctx ctx, int start)
    {
        dynamic s = ctx.Pres!.SlideShowSettings;
        int total = (int)ctx.Pres.Slides.Count;
        if (start <= 1) s.RangeType = 1;
        else { s.RangeType = 2; s.StartingSlide = start; s.EndingSlide = total; }
        dynamic win = s.Run();
        try { s.RangeType = 1; } catch { }
        try { win.Activate(); } catch { }
        _lastStateKey = "";
        var ctx2 = Open();
        try { ApplyTool(ctx2); } finally { Release(ctx2); }
    }

    private static void ToggleScreen(Ctx? ctx, string mode)
    {
        if (ctx?.View == null) { Native.Key(mode == "white" ? Native.VK_W : Native.VK_B); return; }
        int target = mode == "white" ? 4 : 3;
        int state = (int)ctx.View.State;
        ctx.View.State = state == target ? 1 : target;
    }

    private void ApplyTool(Ctx? ctx)
    {
        if (ctx?.View == null) return;
        dynamic v = ctx.View;
        try { v.LaserPointerEnabled = false; } catch { }
        switch (_tool)
        {
            case "pen": case "highlighter": case "eraser":   // drawn by Remco's overlay (any thickness)
            case "laser": case "spotlight": case "zoom": case "number": case "text": case "lens":
                v.PointerType = 3; break;                   // hide PowerPoint's cursor; the overlay draws instead
            default:
                v.PointerType = 4; break;                   // mouse: auto arrow (hides when idle)
        }
    }

    private static void FocusShow(Ctx ctx)
    {
        try { Native.SetForegroundWindow(new IntPtr(Convert.ToInt64((object)ctx.Win!.HWND))); } catch { }
    }

    // ── where the slide is on screen ───────────────────────────────────
    private RectangleF _slideArea = new(0, 0, 1920, 1080);

    private void RefreshSlideArea(Ctx? ctx = null)
    {
        Rectangle window = Screen.PrimaryScreen?.Bounds ?? new Rectangle(0, 0, 1920, 1080);
        RectangleF stage = window;
        bool own = ctx == null;
        try
        {
            if (TopView != null)
            {
                window = TopView.Bounds;
                _slideArea = window;
                _ov.SetArea(window, window);
                return;
            }
            if (own) ctx = Open();
            if (ctx?.Win != null)
            {
                var r = Native.ClientScreenRect(new IntPtr(Convert.ToInt64((object)ctx.Win.HWND)));
                if (r != null && r.Value.Width > 50) window = r.Value;
                double aspect = _sh > 0 ? _sw / _sh : 16.0 / 9.0;
                double w = window.Width, h = window.Height;
                double fw = w, fh = w / aspect;
                if (fh > h) { fh = h; fw = h * aspect; }
                stage = new RectangleF((float)(window.Left + (w - fw) / 2), (float)(window.Top + (h - fh) / 2), (float)fw, (float)fh);
            }
            else
            {
                // Not presenting (PDF, browser…): use the monitor under the mouse.
                if (Native.GetCursorPos(out var cp))
                {
                    window = Screen.FromPoint(new Point(cp.X, cp.Y)).Bounds;
                    stage = window;
                }
            }
        }
        catch { }
        finally { if (own) Release(ctx); }
        _slideArea = stage;
        _ov.SetArea(window, stage);
    }

    private void MoveToSlidePoint(double x, double y)
    {
        var a = _slideArea;
        int px = (int)Math.Round(a.Left + Math.Clamp(x, 0, 1) * (a.Width - 1));
        int py = (int)Math.Round(a.Top + Math.Clamp(y, 0, 1) * (a.Height - 1));
        Native.MoveAbsolute(px, py);
    }

    // ────────────────────────────────────────────────────────────────────
    //  State → phone (timer every ~300 ms and after commands)
    // ────────────────────────────────────────────────────────────────────
    public void Poll()
    {
        Ctx? ctx = null;
        try
        {
            ctx = Open();
            PollInner(ctx);
            RefreshSlideArea(ctx);
        }
        catch (Exception ex)
        {
            if (_pollCount % 25 == 0) _log("Waiting for PowerPoint: " + ex.Message);
        }
        finally { Release(ctx); }
        if (++_pollCount % 30 == 0) { GC.Collect(); GC.WaitForPendingFinalizers(); }
    }

    private void PollInner(Ctx? ctx)
    {
        bool ppt = ctx != null;
        bool showing = ctx?.View != null;
        string name = "", fullName = "";
        int total = 0, slide = 0, click = -1, clicks = -1;
        string screen = "normal";
        string slideKey = "screen";

        if (ctx?.Pres != null)
        {
            dynamic p = ctx.Pres;
            name = (string)p.Name;
            try { fullName = (string)p.FullName; } catch { fullName = name; }
            total = (int)p.Slides.Count;
            try { _sw = Convert.ToDouble((object)p.PageSetup.SlideWidth); _sh = Convert.ToDouble((object)p.PageSetup.SlideHeight); } catch { }
        }

        if (showing && !_wasShowing) { try { ApplyTool(ctx); } catch { } }
        _wasShowing = showing;

        if (showing)
        {
            dynamic v = ctx!.View!;
            int st = (int)v.State;
            screen = st == 3 ? "black" : st == 4 ? "white" : "normal";
            try
            {
                dynamic sl = v.Slide;
                slide = (int)sl.SlideIndex;
                slideKey = "id" + Convert.ToInt32((object)sl.SlideID);
            }
            catch { slide = total + 1; slideKey = "end"; }
            if (st == 5) { slide = total + 1; slideKey = "end"; }
            try { click = (int)v.GetClickIndex(); clicks = (int)v.GetClickCount(); } catch { }
        }
        else if (ctx?.Pres != null)
        {
            try { slide = (int)ctx.App.ActiveWindow.View.Slide.SlideIndex; } catch { slide = 1; }
        }

        Status = !ppt ? "PowerPoint: not running"
               : ctx!.Pres == null ? "PowerPoint: no presentation open"
               : showing ? $"Presenting “{name}” — slide {Math.Min(slide, total)} of {total}"
               : $"Open: “{name}” (not presenting)";

        // presentation changed → reset caches, open its saved numbers/text
        string presKey = fullName + "|" + total;
        if (presKey != _presKey)
        {
            _presKey = presKey;
            _thumbCache.Clear();
            _notesCache.Clear();
            _titlesSent = false;
            _lastThumbSlide = -1;
            _store.Open(fullName.Length > 0 ? fullName : "__desktop__");
            _slideKey = "";
        }

        if (!showing) slideKey = "screen";
        // drawings on photos / videos are kept per file; on the phone screen in one layer
        int mediaIndex = -1;
        if (TopView != null) slideKey = ViewKey;
        if (MediaShowing) mediaIndex = _media!.CurrentIndex;
        if (slideKey != _slideKey)
        {
            _slideKey = slideKey;
            _ov.Anns = _store.For(slideKey);
            _ov.SlideChanged();
            _annsDirty = true;
        }
        bool minimized = false;
        if (showing)
        {
            try { minimized = Native.IsIconic(new IntPtr(Convert.ToInt64((object)ctx!.Win!.HWND))); } catch { }
        }
        // Laser, spotlight, zoom, pen, numbers & text also work over photos/videos and the phone screen.
        bool overView = TopView != null;
        _ov.Hidden = !overView && (screen != "normal" || minimized);
        _ov.StayAbove(TopView?.Handle ?? IntPtr.Zero);
        Native.IsIconicShow = minimized;

        double stateSw = _sw, stateSh = _sh;
        if (TopView != null) { var a = TopView.Bounds; stateSw = a.Width; stateSh = a.Height; }
        string view = ViewName;
        int vPage = BoardShowing ? _board!.Page : DocShowing ? _doc!.Page : GalleryShowing ? _gallery!.Index + 1 : 0;
        int vPages = BoardShowing ? _board!.Pages : DocShowing ? _doc!.Pages : GalleryShowing ? _gallery!.Count : 0;
        string vBg = BoardShowing ? _board!.Bg : "";
        string key = $"{ppt}|{showing}|{presKey}|{slide}|{click}|{clicks}|{screen}|{mediaIndex}|{PhoneShowing}|{view}|{vPage}|{vPages}|{vBg}";
        if (key != _lastStateKey)
        {
            _lastStateKey = key;
            string notes = (ctx?.Pres != null && slide >= 1 && slide <= total) ? Notes((object)ctx.Pres, slide) : "";
            _send(new
            {
                e = "state", ppt, show = showing, name, slide, total, click, clicks, screen, notes, sw = stateSw, sh = stateSh,
                mi = mediaIndex, phone = PhoneShowing, view, page = vPage, pages = vPages, bg = vBg,
                doc = DocShowing ? _doc!.DocName : "", single = GalleryShowing && _gallery!.Single
            });
            _ov.Invalidate2();
        }
        SendAnns();

        if (ctx?.Pres != null && !_titlesSent)
        {
            _titlesSent = true;
            _send(new { e = "slides", titles = Titles((object)ctx.Pres, total) });
        }

        // Thumbnails are exported a moment later so fast NEXT presses are never delayed.
        if (ctx?.Pres != null && slide != _lastThumbSlide)
        {
            _lastThumbSlide = slide;
            _pendingThumb = slide;
            _thumbTimer.Stop(); _thumbTimer.Start();
        }
    }

    private void SendPendingThumbs()
    {
        int slide = _pendingThumb;
        if (slide < 1) return;
        _pendingThumb = -1;
        Ctx? ctx = null;
        try
        {
            ctx = Open();
            if (ctx?.Pres == null) return;
            int total = (int)ctx.Pres.Slides.Count;
            if (slide <= total) _send(new { e = "thumb", which = "cur", slide, img = Thumb((object)ctx.Pres, slide, 960) });
            if (slide + 1 <= total) _send(new { e = "thumb", which = "next", slide = slide + 1, img = Thumb((object)ctx.Pres, slide + 1, 400) });
        }
        catch { }
        finally { Release(ctx); }
    }

    // ────────────────────────────────────────────────────────────────────
    //  Slide content helpers
    // ────────────────────────────────────────────────────────────────────
    private string Thumb(object presObj, int idx, int w)
    {
        dynamic pres = presObj;
        if (_thumbCache.TryGetValue(idx * 10000 + w, out var cached)) return cached;
        try
        {
            int h = (int)Math.Round(w * _sh / _sw);
            string path = Path.Combine(_tmpDir, $"slide_{idx}_{w}.jpg");
            pres.Slides.Item(idx).Export(path, "JPG", w, h);
            string b64 = Convert.ToBase64String(File.ReadAllBytes(path));
            try { File.Delete(path); } catch { }
            _thumbCache[idx * 10000 + w] = b64;
            return b64;
        }
        catch (Exception ex)
        {
            _log($"Could not export slide {idx}: {ex.Message}");
            return "";
        }
    }

    private string Notes(object presObj, int idx)
    {
        dynamic pres = presObj;
        if (_notesCache.TryGetValue(idx, out var cached)) return cached;
        string text = "";
        try
        {
            dynamic shapes = pres.Slides.Item(idx).NotesPage.Shapes;
            int n = (int)shapes.Count;
            for (int i = 1; i <= n; i++)
            {
                dynamic sh = shapes.Item(i);
                try
                {
                    if ((int)sh.Type == 14 && (int)sh.PlaceholderFormat.Type == 2)
                    {
                        text = ((string)sh.TextFrame.TextRange.Text).Replace('\r', '\n').Replace('\v', '\n');
                        break;
                    }
                }
                catch { }
            }
        }
        catch { }
        _notesCache[idx] = text;
        return text;
    }

    private static List<string> Titles(object presObj, int total)
    {
        dynamic pres = presObj;
        var list = new List<string>(total);
        for (int i = 1; i <= total; i++)
        {
            string t = "";
            try
            {
                dynamic shapes = pres.Slides.Item(i).Shapes;
                if (Convert.ToInt32((object)shapes.HasTitle) != 0)
                    t = ((string)shapes.Title.TextFrame.TextRange.Text).Replace('\r', ' ').Replace('\v', ' ').Trim();
            }
            catch { }
            if (t.Length > 80) t = t[..80] + "…";
            list.Add(t);
        }
        return list;
    }

    // ────────────────────────────────────────────────────────────────────
    private static string Clean(string s)
    {
        foreach (char ch in Path.GetInvalidFileNameChars()) s = s.Replace(ch, '_');
        return s.Trim();
    }

    private static bool Has(JsonElement m, string k) => m.TryGetProperty(k, out _);

    private static string Str(JsonElement m, string k) =>
        m.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";

    private static double Num(JsonElement m, string k) =>
        m.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetDouble() : 0;

    private static bool Bool(JsonElement m, string k) =>
        m.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.True;

    private static int ParseBgr(string hex)
    {
        hex = hex.TrimStart('#');
        if (hex.Length != 6) return 0x0000FF;
        int rgb = Convert.ToInt32(hex, 16);
        int r = (rgb >> 16) & 0xFF, g = (rgb >> 8) & 0xFF, b = rgb & 0xFF;
        return r | (g << 8) | (b << 16);
    }
}
