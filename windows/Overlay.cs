using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.Runtime.InteropServices;

namespace Remco;

/// <summary>One number badge or text label placed on a slide (slide-relative 0..1 coords).</summary>
internal sealed class Ann
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N")[..10];
    public string Kind { get; set; } = "number";     // number | text
    public double X { get; set; }
    public double Y { get; set; }
    public string Color { get; set; } = "#f87171";   // badge colour / text background ("transparent" allowed)
    public string Text { get; set; } = "";
    public int Size { get; set; } = 28;              // number diameter (phone px, like the web app)
    public string Style { get; set; } = "pin";       // text: pin | plain | box
    public string TextColor { get; set; } = "#111827";
    public int FontSize { get; set; } = 14;
    /// <summary>ink strokes: x0,y0,x1,y1,… in slide coords</summary>
    public List<double>? Pts { get; set; }
    public bool Hl { get; set; }
}

/// <summary>
/// A transparent, click-through, always-on-top window laid over the PowerPoint
/// slide show. It draws what PowerPoint itself cannot: the web app's spotlight
/// (8 styles), coloured/sized laser with a caption, zoom &amp; pan, number badges,
/// text labels and the projector timer. The mouse still goes to PowerPoint.
///
/// Drawing uses a per-pixel-alpha layered window (UpdateLayeredWindow) backed by
/// a DIB section, so nothing flickers and PowerPoint underneath keeps animating.
/// Sizes are in "CSS pixels" × monitor DPI scale, the same as the web projector page.
/// </summary>
internal sealed class Overlay : Form
{
    // ── public state (set by the controller, then call Invalidate2()) ──
    public Rectangle WindowArea { get; private set; }  // show window (screen px)
    public RectangleF Stage { get; private set; }      // letter-boxed slide inside it (screen px)

    public bool LaserActive; public double LaserX = .5, LaserY = .5; public int LaserSize = 16; public Color LaserColor = ColorTranslator.FromHtml("#ef4444"); public string LaserLabel = "";
    public bool LensActive; public double LensX = .5, LensY = .5; public int LensRadius = 140; public double LensZoom = 2;
    public int LensBright = 88;      // % — a softer lens doesn't wash out the slide
    public bool LensDim;             // optional: darken around the lens
    public bool SpotActive; public double SpotX = .5, SpotY = .5; public int SpotRadius = 160; public string SpotStyle = "stage"; public bool SpotConfetti = true; public Color? SpotColor;
    public double ZoomScale = 1, ZoomX, ZoomY;          // target (web: scale 1..4, x/y in %)
    public List<Ann> Anns = new();                      // annotations of the current slide
    public string? PreviewKind; public double PreviewX, PreviewY; public string PreviewColor = "#f87171"; public string PreviewText = "";
    public bool Hidden;                                 // black / white screen → hide everything but the timer
    public bool TimerVisible; public string TimerMode = "down"; public int TimerSeconds = -1;
    private string? _alertLabel; private long _alertStart; private int _alertMs;

    /// <summary>Screen rectangles of drawn annotations, for tap hit-testing.</summary>
    public readonly Dictionary<string, RectangleF> DrawnRects = new();

    // ── internals ──
    private IntPtr _memDc, _dib, _oldBmp, _bits;
    private Bitmap? _surface;
    private Graphics? _g;
    private Size _surfSize;
    private bool _shown;
    private bool _dirty = true;
    private readonly System.Windows.Forms.Timer _frame = new() { Interval = 16 };
    private readonly long _t0 = Environment.TickCount64;
    private double _zs = 1, _zx, _zy;                  // animated zoom (0.15 s ease-out, like the web)
    private Bitmap? _cap, _staticCap; private long _lastCap;
    private bool _captureExcluded;                      // WDA_EXCLUDEFROMCAPTURE active
    private bool _affinityUnsupported;                  // Windows older than 10 (2004)
    private Bitmap? _spriteBmp; private string _spriteKey = ""; private Color _spriteOuter;
    private float _dpi = 1f;

    private static readonly string[] Confetti = { "#f43f5e", "#3b82f6", "#22c55e", "#eab308", "#a855f7", "#ec4899", "#fb923c" };

    public Overlay()
    {
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.Manual;
        Bounds = new Rectangle(0, 0, 10, 10);
        _frame.Tick += (_, _) => Frame();
        _frame.Start();
    }

    protected override bool ShowWithoutActivation => true;

    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            cp.ExStyle |= 0x80000      // WS_EX_LAYERED
                        | 0x20         // WS_EX_TRANSPARENT (click-through)
                        | 0x80         // WS_EX_TOOLWINDOW (no taskbar / alt-tab)
                        | 0x08000000   // WS_EX_NOACTIVATE
                        | 0x8;         // WS_EX_TOPMOST
            return cp;
        }
    }

    // ────────────────────────────────────────────────────────────────────
    public void SetArea(Rectangle window, RectangleF stage)
    {
        if (window == WindowArea && stage == Stage) return;
        WindowArea = window; Stage = stage;
        _dirty = true;
    }

    public void Invalidate2() => _dirty = true;

    private IntPtr _owner;
    /// <summary>
    /// Keep the drawings above a full-screen Remco window (photo/video player, phone screen).
    /// An owned window is ALWAYS above its owner, whatever that window does with its z-order.
    /// </summary>
    public void StayAbove(IntPtr owner)
    {
        if (owner == _owner) return;
        _owner = owner;
        _ = Handle;
        Native.SetWindowLongPtr(Handle, -8 /*GWL_HWNDPARENT*/, owner);
        if (_shown) Native.SetWindowPos(Handle, new IntPtr(-1), 0, 0, 0, 0, 0x0001 | 0x0002 | 0x0010);
        _dirty = true;
    }

    /// <summary>Slide changed → a static zoom snapshot (older Windows only) must be retaken.</summary>
    public void SlideChanged() { _staticCap?.Dispose(); _staticCap = null; _dirty = true; }

    public void TimerAlert(string label, int ms)
    {
        _alertLabel = label; _alertStart = Environment.TickCount64; _alertMs = ms; _dirty = true;
    }

    public void ClearAll()
    {
        LaserActive = false; LaserLabel = ""; SpotActive = false; PreviewKind = null;
        _dirty = true;
    }

    private long Now => Environment.TickCount64 - _t0;

    private bool ZoomOn => ZoomScale > 1.001 || _zs > 1.001 || Math.Abs(_zx) > .01 || Math.Abs(_zy) > .01 || Math.Abs(ZoomX) > .01 || Math.Abs(ZoomY) > .01;

    private bool Animating =>
        (SpotActive && !Hidden && (SpotStyle is "neon" or "colorful" or "celebration")) ||
        PreviewKind != null ||
        (TimerVisible && TimerMode == "down" && TimerSeconds >= 0 && TimerSeconds <= 60) ||
        _alertLabel != null ||
        Math.Abs(_zs - ZoomScale) > .001 || Math.Abs(_zx - ZoomX) > .01 || Math.Abs(_zy - ZoomY) > .01;

    private bool AnythingToDraw =>
        _alertLabel != null ||
        (TimerVisible && TimerSeconds >= 0) ||
        (!Hidden && (LaserActive || LaserLabel.Length > 0 || SpotActive || LensActive || Anns.Count > 0 || PreviewKind != null || ZoomOn));

    // ────────────────────────────────────────────────────────────────────
    //  Frame loop
    // ────────────────────────────────────────────────────────────────────
    private void Frame()
    {
        if (_alertLabel != null && Environment.TickCount64 - _alertStart > _alertMs) { _alertLabel = null; _dirty = true; }

        bool live = (ZoomOn || LensActive) && !Hidden && _captureExcluded && Environment.TickCount64 - _lastCap > 50; // live zoom / lens ~20 fps
        if (!_dirty && !Animating && !live) return;
        _dirty = false;

        if (!AnythingToDraw || WindowArea.Width < 10)
        {
            if (_shown) { Native.ShowWindow(Handle, 0); _shown = false; }
            SetCaptureExcluded(false);
            DrawnRects.Clear();
            return;
        }
        Render();
    }

    private void EnsureSurface()
    {
        var size = WindowArea.Size;
        if (_surface != null && size == _surfSize) return;
        FreeSurface();
        _surfSize = size;
        var bmi = new Native.BITMAPINFO
        {
            biSize = 40, // sizeof(BITMAPINFOHEADER)
            biWidth = size.Width, biHeight = -size.Height, biPlanes = 1, biBitCount = 32, biCompression = 0
        };
        _memDc = Native.CreateCompatibleDC(IntPtr.Zero);
        _dib = Native.CreateDIBSection(IntPtr.Zero, ref bmi, 0, out _bits, IntPtr.Zero, 0);
        _oldBmp = Native.SelectObject(_memDc, _dib);
        _surface = new Bitmap(size.Width, size.Height, size.Width * 4, PixelFormat.Format32bppPArgb, _bits);
        _g = Graphics.FromImage(_surface);
    }

    private void FreeSurface()
    {
        _g?.Dispose(); _g = null;
        _surface?.Dispose(); _surface = null;
        if (_memDc != IntPtr.Zero) { Native.SelectObject(_memDc, _oldBmp); Native.DeleteDC(_memDc); _memDc = IntPtr.Zero; }
        if (_dib != IntPtr.Zero) { Native.DeleteObject(_dib); _dib = IntPtr.Zero; }
    }

    private void Render()
    {
        _ = Handle;
        EnsureSurface();
        var g = _g!;
        try { _dpi = Math.Max(1f, Native.GetDpiForWindow(Handle) / 96f); } catch { _dpi = 1f; }

        g.ResetTransform(); g.ResetClip();
        g.CompositingMode = CompositingMode.SourceCopy;
        g.Clear(Color.Transparent);
        g.CompositingMode = CompositingMode.SourceOver;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;

        // stage relative to our window
        var st = new RectangleF(Stage.X - WindowArea.X, Stage.Y - WindowArea.Y, Stage.Width, Stage.Height);

        // animate zoom towards target (≈ CSS transition 0.15s ease-out)
        double k = 0.35;
        _zs += (ZoomScale - _zs) * k; _zx += (ZoomX - _zx) * k; _zy += (ZoomY - _zy) * k;
        if (Math.Abs(_zs - ZoomScale) < .002) _zs = ZoomScale;
        if (Math.Abs(_zx - ZoomX) < .02) _zx = ZoomX;
        if (Math.Abs(_zy - ZoomY) < .02) _zy = ZoomY;

        DrawnRects.Clear();
        if (!Hidden)
        {
            if (ZoomOn) DrawZoom(g, st);
            else if (!LensActive) { SetCaptureExcluded(false); _staticCap?.Dispose(); _staticCap = null; }
            DrawAnnotations(g, st);
            DrawPreview(g, st);
            if (SpotActive) DrawSpotlight(g, st);
            if (LensActive) DrawLens(g, st);
            if (LaserActive) DrawLaser(g, st);
            if (LaserLabel.Length > 0) DrawLaserLabel(g, st);
        }
        else SetCaptureExcluded(false);
        DrawTimer(g);
        DrawAlert(g);

        g.Flush(FlushIntention.Sync);
        Push();
    }

    private void Push()
    {
        var pos = new Native.POINT { X = WindowArea.X, Y = WindowArea.Y };
        var size = new Native.SIZE { cx = _surfSize.Width, cy = _surfSize.Height };
        var src = new Native.POINT { X = 0, Y = 0 };
        var blend = new Native.BLENDFUNCTION { BlendOp = 0, BlendFlags = 0, SourceConstantAlpha = 255, AlphaFormat = 1 };
        Native.UpdateLayeredWindow(Handle, IntPtr.Zero, ref pos, ref size, _memDc, ref src, 0, ref blend, 2);
        if (!_shown) { Native.ShowWindow(Handle, 4 /*SW_SHOWNOACTIVATE*/); _shown = true; }
        Native.SetWindowPos(Handle, new IntPtr(-1) /*HWND_TOPMOST*/, 0, 0, 0, 0, 0x0001 | 0x0002 | 0x0010 /*NOSIZE|NOMOVE|NOACTIVATE*/);
    }

    private void SetCaptureExcluded(bool on)
    {
        if (on == _captureExcluded) return;
        if (on && _affinityUnsupported) return;
        _ = Handle;
        bool ok = Native.SetWindowDisplayAffinity(Handle, on ? 0x11u /*WDA_EXCLUDEFROMCAPTURE*/ : 0u);
        if (on && !ok) _affinityUnsupported = true;
        _captureExcluded = on && ok;
    }

    // ── coordinate helpers ─────────────────────────────────────────────
    private float Css(double px) => (float)(px * _dpi);

    /// <summary>Slide point (0..1) → window px, applying the zoom transform (web: scale(s) translate(x%,y%), origin center).</summary>
    private PointF SlideToWin(RectangleF st, double x, double y)
    {
        double cx = st.X + st.Width / 2, cy = st.Y + st.Height / 2;
        double px = st.X + x * st.Width, py = st.Y + y * st.Height;
        double tx = _zx / 100 * st.Width, ty = _zy / 100 * st.Height;
        return new PointF((float)(cx + _zs * (px - cx + tx)), (float)(cy + _zs * (py - cy + ty)));
    }

    /// <summary>Screen-fixed stage point (0..1, what the phone sends) → slide point (0..1) under the current zoom.</summary>
    public (double x, double y) ScreenToSlide(double sx, double sy)
    {
        var st = Stage;
        double cx = st.Width / 2, cy = st.Height / 2;
        double px = sx * st.Width, py = sy * st.Height;
        double tx = ZoomX / 100 * st.Width, ty = ZoomY / 100 * st.Height;
        double ox = cx + (px - cx) / ZoomScale - tx, oy = cy + (py - cy) / ZoomScale - ty;
        return (ox / st.Width, oy / st.Height);
    }

    public string? HitTest(double sx, double sy, string kind, float slopCss = 10)
    {
        var p = new PointF((float)(Stage.X - WindowArea.X + sx * Stage.Width), (float)(Stage.Y - WindowArea.Y + sy * Stage.Height));
        float slop = Css(slopCss);
        foreach (var a in Anns.AsEnumerable().Reverse())
        {
            if (a.Kind != kind || !DrawnRects.TryGetValue(a.Id, out var r)) continue;
            var rr = RectangleF.Inflate(r, slop, slop);
            if (rr.Contains(p)) return a.Id;
        }
        return null;
    }

    // ────────────────────────────────────────────────────────────────────
    //  Zoom: live magnified copy of the slide show underneath
    // ────────────────────────────────────────────────────────────────────
    private void DrawZoom(Graphics g, RectangleF st)
    {
        // Our window must not appear in its own capture.
        if (!_captureExcluded) SetCaptureExcluded(true);

        double s = Math.Max(1, _zs);
        double cx = st.Width / 2, cy = st.Height / 2;
        double tx = _zx / 100 * st.Width, ty = _zy / 100 * st.Height;
        // visible source region in stage-local px (inverse of the transform)
        double sx0 = cx - cx / s - tx, sy0 = cy - cy / s - ty;
        double sw = st.Width / s, sh = st.Height / s;
        // clamp to stage
        double cx0 = Math.Max(0, sx0), cy0 = Math.Max(0, sy0);
        double cx1 = Math.Min(st.Width, sx0 + sw), cy1 = Math.Min(st.Height, sy0 + sh);

        g.FillRectangle(Brushes.Black, st);
        if (cx1 - cx0 < 2 || cy1 - cy0 < 2) return;

        Bitmap src; RectangleF srcRect;
        if (_captureExcluded)
        {
            // Live: capture only the visible part, ~20× a second (videos & animations stay live).
            int capW = (int)Math.Ceiling(cx1 - cx0), capH = (int)Math.Ceiling(cy1 - cy0);
            if (_cap == null || _cap.Width != capW || _cap.Height != capH || Environment.TickCount64 - _lastCap > 45)
            {
                if (_cap == null || _cap.Width != capW || _cap.Height != capH)
                {
                    _cap?.Dispose();
                    _cap = new Bitmap(capW, capH, PixelFormat.Format32bppArgb);
                }
                try
                {
                    using var cg = Graphics.FromImage(_cap);
                    cg.CopyFromScreen((int)(Stage.X + cx0), (int)(Stage.Y + cy0), 0, 0, new Size(capW, capH), CopyPixelOperation.SourceCopy);
                }
                catch { }
                _lastCap = Environment.TickCount64;
            }
            src = _cap; srcRect = new RectangleF(0, 0, capW, capH);
        }
        else
        {
            // Older Windows (no capture exclusion): one snapshot of the whole slide, taken with this window hidden.
            if (_staticCap == null)
            {
                if (_shown) { Native.ShowWindow(Handle, 0); _shown = false; Thread.Sleep(80); }
                _staticCap = new Bitmap(Math.Max(1, (int)Stage.Width), Math.Max(1, (int)Stage.Height), PixelFormat.Format32bppArgb);
                try
                {
                    using var cg = Graphics.FromImage(_staticCap);
                    cg.CopyFromScreen((int)Stage.X, (int)Stage.Y, 0, 0, _staticCap.Size, CopyPixelOperation.SourceCopy);
                }
                catch { }
            }
            src = _staticCap;
            srcRect = new RectangleF((float)cx0, (float)cy0, (float)(cx1 - cx0), (float)(cy1 - cy0));
        }

        // destination of the clamped source region
        float dx = (float)(st.X + (cx0 - sx0) * s), dy = (float)(st.Y + (cy0 - sy0) * s);
        float dw = (float)((cx1 - cx0) * s), dh = (float)((cy1 - cy0) * s);
        var state = g.Save();
        g.SetClip(st);
        g.InterpolationMode = InterpolationMode.Bilinear;
        g.PixelOffsetMode = PixelOffsetMode.Half;
        g.DrawImage(src, new RectangleF(dx, dy, dw, dh), srcRect, GraphicsUnit.Pixel);
        g.Restore(state);
    }

    // ────────────────────────────────────────────────────────────────────
    //  Numbers & text labels (inside the zoomed stage, like the web app)
    // ────────────────────────────────────────────────────────────────────
    private static readonly StringFormat Center = new() { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };

    // ────────────────────────────────────────────────────────────────────
    //  🔎 Magnifier lens: a round live zoom that follows the finger
    // ────────────────────────────────────────────────────────────────────
    private Bitmap? _lensCap;

    private void DrawLens(Graphics g, RectangleF st)
    {
        if (!_captureExcluded) SetCaptureExcluded(true);
        if (!_captureExcluded) return;          // very old Windows: the lens would see itself — skip it
        float r = Css(LensRadius);
        float z = (float)Math.Clamp(LensZoom, 1.2, 6);
        var c = new PointF((float)(st.X + LensX * st.Width), (float)(st.Y + LensY * st.Height));
        int src = Math.Max(4, (int)Math.Ceiling(2 * r / z));
        if (_lensCap == null || _lensCap.Width != src) { _lensCap?.Dispose(); _lensCap = new Bitmap(src, src, PixelFormat.Format32bppArgb); }
        try
        {
            using var cg = Graphics.FromImage(_lensCap);
            cg.CopyFromScreen((int)(WindowArea.X + c.X - src / 2f), (int)(WindowArea.Y + c.Y - src / 2f), 0, 0, new Size(src, src), CopyPixelOperation.SourceCopy);
        }
        catch { return; }
        _lastCap = Environment.TickCount64;
        var circle = new RectangleF(c.X - r, c.Y - r, 2 * r, 2 * r);
        // optional: gently darken everything around the lens
        if (LensDim)
        {
            var state0 = g.Save();
            using (var clip0 = new GraphicsPath()) { clip0.AddEllipse(circle); g.SetClip(clip0, CombineMode.Exclude); }
            using (var dim = new SolidBrush(Color.FromArgb(120, 0, 0, 0))) g.FillRectangle(dim, 0, 0, _surfSize.Width, _surfSize.Height);
            g.Restore(state0);
        }
        // soft shadow
        for (int i = 5; i >= 1; i--)
        {
            using var sb = new SolidBrush(Color.FromArgb(14, 0, 0, 0));
            g.FillEllipse(sb, RectangleF.Inflate(circle, Css(i * 3), Css(i * 3)));
        }
        var state = g.Save();
        using (var clip = new GraphicsPath()) { clip.AddEllipse(circle); g.SetClip(clip); }
        g.InterpolationMode = InterpolationMode.HighQualityBicubic;
        g.PixelOffsetMode = PixelOffsetMode.Half;
        float alpha = Math.Clamp(LensBright, 30, 100) / 100f;
        if (alpha >= 0.99f) g.DrawImage(_lensCap, circle, new RectangleF(0, 0, src, src), GraphicsUnit.Pixel);
        else
        {
            var ia = new System.Drawing.Imaging.ImageAttributes();
            ia.SetColorMatrix(new System.Drawing.Imaging.ColorMatrix { Matrix33 = alpha });
            g.DrawImage(_lensCap, new[] { new PointF(circle.Left, circle.Top), new PointF(circle.Right, circle.Top), new PointF(circle.Left, circle.Bottom) },
                new RectangleF(0, 0, src, src), GraphicsUnit.Pixel, ia);
            ia.Dispose();
        }
        g.Restore(state);
        using var ring = new Pen(Color.White, Css(4));
        g.DrawEllipse(ring, circle);
        using var ring2 = new Pen(Color.FromArgb(120, 0, 0, 0), Css(1));
        g.DrawEllipse(ring2, RectangleF.Inflate(circle, Css(2.5f), Css(2.5f)));
    }

    /// <summary>
    /// Draws a page's ink, numbers and text into any Graphics (used to save whiteboard pages as pictures).
    /// </summary>
    public void RenderPage(Graphics g, RectangleF stage, List<Ann> anns)
    {
        double zs = _zs, zx = _zx, zy = _zy;
        var oldRects = new Dictionary<string, RectangleF>(DrawnRects);
        _zs = 1; _zx = 0; _zy = 0;
        try
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
            var keep = Anns;
            Anns = anns;
            DrawAnnotations(g, stage);
            Anns = keep;
        }
        finally
        {
            _zs = zs; _zx = zx; _zy = zy;
            DrawnRects.Clear();
            foreach (var kv in oldRects) DrawnRects[kv.Key] = kv.Value;
        }
    }

    private void DrawAnnotations(Graphics g, RectangleF st)
    {
        var state = g.Save();
        g.SetClip(st);
        // ink first (under numbers and labels), highlighter strokes under pen strokes
        foreach (var a in Anns) if (a.Kind == "ink" && a.Hl) DrawInk(g, a, st);
        foreach (var a in Anns) if (a.Kind == "ink" && !a.Hl) DrawInk(g, a, st);
        foreach (var a in Anns)
        {
            if (a.Kind == "ink") continue;
            var p = SlideToWin(st, a.X, a.Y);
            if (a.Kind == "number") DrawNumber(g, a, p, (float)_zs);
            else DrawText(g, a, p, st, (float)_zs);
        }
        g.Restore(state);
    }

    private void DrawNumber(Graphics g, Ann a, PointF p, float zoom, bool preview = false, float alpha = 1f)
    {
        float d = Css(preview ? 36 : a.Size * (36.0 / 28)) * zoom;
        var r = new RectangleF(p.X - d / 2, p.Y - d / 2, d, d);
        if (!preview) Shadow(g, r, d / 2);
        using (var b = new SolidBrush(WithAlpha(Html(a.Color), alpha))) g.FillEllipse(b, r);
        using (var pen = new Pen(Color.FromArgb((int)(204 * alpha), 255, 255, 255), Css(2) * zoom))
        {
            if (preview) pen.DashStyle = DashStyle.Dash;
            g.DrawEllipse(pen, r);
        }
        float fs = preview ? Css(16) * zoom : Math.Max(Css(12), d * 0.44f);
        using var f = new Font("Segoe UI Black", fs, FontStyle.Bold, GraphicsUnit.Pixel);
        using var tb = new SolidBrush(Color.FromArgb((int)(255 * alpha), 255, 255, 255));
        g.DrawString(a.Text, f, tb, r, Center);
        if (!preview) DrawnRects[a.Id] = r;
    }

    private void DrawText(Graphics g, Ann a, PointF p, RectangleF st, float zoom)
    {
        float fs = Css(a.FontSize * (36.0 / 28)) * zoom;
        using var f = new Font("Segoe UI", fs, FontStyle.Bold, GraphicsUnit.Pixel);
        bool rtl = IsRtl(a.Text);
        using var sf = new StringFormat(rtl ? StringFormatFlags.DirectionRightToLeft : 0);
        float padX = Css(10) * zoom, padY = Css(6) * zoom, gap = Css(4) * zoom;
        bool pin = a.Style is "pin" or "" ;
        float pinW = pin ? fs * 0.8f + gap : 0;
        float maxW = st.Width * 0.55f * zoom - 2 * padX - pinW;
        var ts = g.MeasureString(a.Text.Length == 0 ? " " : a.Text, f, (int)Math.Max(20, maxW), sf);
        float w = ts.Width + 2 * padX + pinW, h = Math.Max(ts.Height, fs * 1.2f) + 2 * padY;
        // web: transform translate(-8%, -110%)
        var box = new RectangleF(p.X - 0.08f * w, p.Y - 1.10f * h, w, h);
        float rad = Css(8) * zoom;

        bool noBg = a.Style == "plain" && a.Color == "transparent";
        if (!noBg) Shadow(g, box, rad);
        using (var path = Rounded(box, rad))
        {
            if (a.Style == "box")
            {
                using var bb = new SolidBrush(Color.FromArgb(209, 17, 24, 39));
                g.FillPath(bb, path);
                using var bp = new Pen(Html(a.Color), Css(2) * zoom);
                g.DrawPath(bp, path);
            }
            else if (!noBg)
            {
                using var bb = new SolidBrush(Html(a.Color));
                g.FillPath(bb, path);
            }
        }
        float tx = box.X + padX;
        if (pin)
        {
            DrawPin(g, new RectangleF(tx, box.Y + padY + fs * 0.1f, fs * 0.8f, fs));
            tx += pinW;
        }
        using var tbr = new SolidBrush(Html(a.TextColor));
        g.DrawString(a.Text, f, tbr, new RectangleF(tx, box.Y + padY, ts.Width + 2, ts.Height + 2), sf);
        DrawnRects[a.Id] = box;
    }

    private void DrawInk(Graphics g, Ann a, RectangleF st)
    {
        var pts = a.Pts;
        if (pts == null || pts.Count < 2) return;
        // pen width scales with the slide (size is in 1080p "css px") and with zoom
        float w = Math.Max(1f, (float)(a.Size * st.Height / 1080.0 * _zs));
        var col = Html(a.Color);
        if (a.Hl) col = Color.FromArgb(110, col);
        using var pen = new Pen(col, w) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round };
        if (pts.Count < 4)
        {
            var p = SlideToWin(st, pts[0], pts[1]);
            using var b = new SolidBrush(col);
            g.FillEllipse(b, p.X - w / 2, p.Y - w / 2, w, w);
            return;
        }
        var arr = new PointF[pts.Count / 2];
        for (int i = 0; i < arr.Length; i++) arr[i] = SlideToWin(st, pts[i * 2], pts[i * 2 + 1]);
        if (arr.Length >= 3) g.DrawCurve(pen, arr, 0.4f); else g.DrawLines(pen, arr);
    }

    /// <summary>Screen distance (px) from a stage point (0..1, what the phone sends) to a stroke.</summary>
    public double InkDistancePx(Ann a, double sx, double sy)
    {
        var pts = a.Pts;
        if (pts == null || pts.Count < 2) return double.MaxValue;
        var st = new RectangleF(0, 0, Stage.Width, Stage.Height);
        double px = sx * st.Width, py = sy * st.Height, best = double.MaxValue;
        for (int i = 0; i + 1 < pts.Count; i += 2)
        {
            var p = SlideToWin(st, pts[i], pts[i + 1]);
            double d = Math.Sqrt((p.X - px) * (p.X - px) + (p.Y - py) * (p.Y - py));
            if (d < best) best = d;
        }
        return best - Math.Max(1f, a.Size * st.Height / 1080.0 * _zs) / 2;
    }

    public float CssPx(double v) => Css(v);

    private static void DrawPin(Graphics g, RectangleF r)
    {
        // 📍 drawn as vector (GDI+ can't render colour emoji)
        float d = r.Width * 0.85f;
        var head = new RectangleF(r.X + (r.Width - d) / 2, r.Y, d, d);
        using var red = new SolidBrush(Color.FromArgb(0xE5, 0x39, 0x35));
        using var needle = new Pen(Color.FromArgb(0x9E, 0x9E, 0x9E), Math.Max(1.5f, r.Width * 0.12f));
        g.DrawLine(needle, head.X + d / 2, head.Bottom - 1, head.X + d / 2, r.Bottom);
        g.FillEllipse(red, head);
        using var hi = new SolidBrush(Color.FromArgb(150, 255, 255, 255));
        g.FillEllipse(hi, head.X + d * 0.22f, head.Y + d * 0.18f, d * 0.28f, d * 0.28f);
    }

    private void DrawPreview(Graphics g, RectangleF st)
    {
        if (PreviewKind == null) return;
        // animate-pulse: opacity 1 → .5 → 1 over 2 s, on top of opacity .7
        double t = (Now % 2000) / 2000.0;
        float pulse = (float)(0.7 * (0.75 + 0.25 * Math.Cos(t * 2 * Math.PI)));
        var p = SlideToWin(st, PreviewX, PreviewY);
        if (PreviewKind == "number")
        {
            DrawNumber(g, new Ann { Color = PreviewColor, Text = PreviewText }, p, (float)_zs, preview: true, alpha: pulse);
        }
        else
        {
            float fs = Css(14) * (float)_zs;
            float w = fs * 2.2f, h = fs * 2f;
            var box = new RectangleF(p.X - 0.08f * w, p.Y - 1.1f * h, w, h);
            using var path = Rounded(box, Css(8));
            using var b = new SolidBrush(WithAlpha(Html(PreviewColor == "transparent" ? "#eab308" : PreviewColor), pulse));
            g.FillPath(b, path);
            using var pen = new Pen(Color.FromArgb((int)(230 * pulse), 255, 255, 255), Css(2)) { DashStyle = DashStyle.Dash };
            g.DrawPath(pen, path);
            DrawPin(g, new RectangleF(box.X + (w - fs * 0.8f) / 2, box.Y + (h - fs) / 2, fs * 0.8f, fs));
        }
    }

    // ────────────────────────────────────────────────────────────────────
    //  Spotlight — the web app's 8 styles
    // ────────────────────────────────────────────────────────────────────
    private (float off, Color c)[] Stops(float r) => SpotStyle switch
    {
        "theater" => new[] { (0f, Color.FromArgb(0, 0, 0, 0)), (10f, Color.FromArgb(56, 251, 191, 36)), (45f, Color.FromArgb(128, 120, 53, 15)), (90f, Color.FromArgb(240, 0, 0, 0)) },
        "minimal" => new[] { (0f, Color.FromArgb(0, 0, 0, 0)), (120f, Color.FromArgb(89, 0, 0, 0)) },
        "stage" => new[] { (0f, Color.FromArgb(0, 0, 0, 0)), (15f, Color.FromArgb(247, 0, 0, 0)) },
        "glass" => new[] { (0f, Color.FromArgb(0, 0, 0, 0)), (4f, Color.FromArgb(102, 147, 197, 253)), (12f, Color.FromArgb(46, 255, 255, 255)), (24f, Color.FromArgb(0, 0, 0, 0)), (80f, Color.FromArgb(191, 0, 0, 0)) },
        "neon" or "colorful" => new[] { (0f, Color.FromArgb(0, 0, 0, 0)), (70f, Color.FromArgb(191, 0, 0, 0)) },
        "celebration" => new[] { (0f, Color.FromArgb(0, 0, 0, 0)), (70f, Color.FromArgb(184, 0, 0, 0)) },
        _ => new[] { (0f, Color.FromArgb(0, 0, 0, 0)), (70f, Color.FromArgb(199, 0, 0, 0)) }, // classic
    };

    private void BuildSprite(float r)
    {
        var stops = Stops(r);
        string key = $"{SpotStyle}|{r:F1}|{_dpi}";
        if (key == _spriteKey && _spriteBmp != null) return;
        _spriteKey = key;
        float maxOff = Css(stops[^1].off);
        int R = (int)Math.Ceiling(r + maxOff) + 2;
        int D = R * 2;
        _spriteBmp?.Dispose();
        _spriteBmp = new Bitmap(D, D, PixelFormat.Format32bppPArgb);

        // 1-D lookup by distance, premultiplied (CSS interpolates premultiplied)
        var lut = new uint[R + 2];
        for (int d = 0; d < lut.Length; d++)
        {
            float x = d - r;
            var c = Interp(stops, x);
            lut[d] = c;
        }
        _spriteOuter = Unpremul(lut[^1]);

        var data = _spriteBmp.LockBits(new Rectangle(0, 0, D, D), ImageLockMode.WriteOnly, PixelFormat.Format32bppPArgb);
        var row = new int[D];
        for (int y = 0; y < D; y++)
        {
            float dy = y + .5f - R;
            for (int x = 0; x < D; x++)
            {
                float dx = x + .5f - R;
                float dist = MathF.Sqrt(dx * dx + dy * dy);
                int i = (int)dist;
                row[x] = unchecked((int)(i >= lut.Length - 1 ? lut[^1] : Lerp(lut[i], lut[i + 1], dist - i)));
            }
            Marshal.Copy(row, 0, data.Scan0 + y * data.Stride, D);
        }
        _spriteBmp.UnlockBits(data);

        uint Interp((float off, Color c)[] s, float x)
        {
            if (x <= Css(s[0].off)) return Pm(s[0].c);
            for (int j = 1; j < s.Length; j++)
            {
                float a = Css(s[j - 1].off), b = Css(s[j].off);
                if (x <= b) return Lerp(Pm(s[j - 1].c), Pm(s[j].c), (x - a) / Math.Max(0.001f, b - a));
            }
            return Pm(s[^1].c);
        }
    }

    private void DrawSpotlight(Graphics g, RectangleF st)
    {
        float r = Css(SpotRadius);
        BuildSprite(r);
        var c = new PointF((float)(st.X + SpotX * st.Width), (float)(st.Y + SpotY * st.Height));
        var sb = _spriteBmp!;
        // Whole pixels for both the hole and the darkness around it, so there is no 1-px seam.
        int ix = (int)Math.Round(c.X - sb.Width / 2f), iy = (int)Math.Round(c.Y - sb.Height / 2f);
        var sr = new Rectangle(ix, iy, sb.Width, sb.Height);

        var state = g.Save();
        var oldSmooth = g.SmoothingMode;
        g.SmoothingMode = SmoothingMode.None;
        g.PixelOffsetMode = PixelOffsetMode.None;
        g.SetClip(sr, CombineMode.Exclude);
        using (var outer = new SolidBrush(_spriteOuter)) g.FillRectangle(outer, 0, 0, _surfSize.Width, _surfSize.Height);
        g.ResetClip();
        g.InterpolationMode = InterpolationMode.NearestNeighbor;
        g.DrawImage(sb, sr, 0, 0, sb.Width, sb.Height, GraphicsUnit.Pixel);
        g.Restore(state);
        g.SmoothingMode = oldSmooth;

        double t = Now / 1000.0;
        if (SpotStyle == "neon")
        {
            float op = (float)(0.75 - 0.25 * Math.Cos(t / 1.6 * 2 * Math.PI)); // 0.5 … 1
            GlowRing(g, c, r, Html("#22d3ee"), Css(3), Css(20), op);
        }
        else if (SpotStyle == "colorful")
        {
            float op = (float)(0.775 - 0.225 * Math.Cos(t / 1.6 * 2 * Math.PI)); // .55 … 1
            Color col = SpotColor ?? HueRotate(Html("#ef4444"), (t % 3.0) / 3.0 * 360);
            GlowRing(g, c, r, col, Css(4), Css(22), op);
        }
        else if (SpotStyle == "celebration")
        {
            double ph = (t % 1.0) * 2 * Math.PI;
            float op = (float)(0.775 - 0.225 * Math.Cos(ph));
            float sc = (float)(1.025 - 0.025 * Math.Cos(ph));
            GlowRing(g, c, r * sc, Html("#fbbf24"), Css(4) * sc, Css(24), op, Color.FromArgb(140, 236, 72, 153));
            if (SpotConfetti)
            {
                for (int i = 0; i < 12; i++)
                {
                    double dur = 0.9 + (i % 4) * 0.15, delay = (i % 6) * 0.12;
                    double tt = t - delay; if (tt < 0) continue;
                    double f = (tt % dur) / dur;
                    float fx = c.X + (float)(((i + 0.5) / 12 - 0.5) * r * 2);
                    float fy = c.Y - r + (float)(-Css(10) + f * (2 * r + Css(30) + Css(10)));
                    var col = WithAlpha(Html(Confetti[i % Confetti.Length]), (float)(1 - f));
                    var st2 = g.Save();
                    g.TranslateTransform(fx + Css(3), fy + Css(4.5f));
                    g.RotateTransform((float)(f * 360));
                    using var b = new SolidBrush(col);
                    g.FillRectangle(b, -Css(3), -Css(4.5f), Css(6), Css(9));
                    g.Restore(st2);
                }
            }
        }
    }

    private static void GlowRing(Graphics g, PointF c, float r, Color col, float width, float glow, float opacity, Color? second = null)
    {
        // box-shadow glow approximated with widening, fading strokes
        int steps = 10;
        for (int i = steps; i >= 1; i--)
        {
            float f = i / (float)steps;
            float w = width + glow * f * 2;
            int a = (int)(opacity * 70 * (1 - f) * (1 - f) + 4 * opacity);
            var gc = second.HasValue && i > steps / 2 ? second.Value : col;
            using var p = new Pen(Color.FromArgb(Math.Clamp(a, 0, 255), gc), w);
            g.DrawEllipse(p, c.X - r, c.Y - r, r * 2, r * 2);
        }
        using var pen = new Pen(Color.FromArgb((int)(255 * opacity), col), width);
        g.DrawEllipse(pen, c.X - r, c.Y - r, r * 2, r * 2);
    }

    // ────────────────────────────────────────────────────────────────────
    //  Laser (screen-fixed regardless of zoom, like the web app)
    // ────────────────────────────────────────────────────────────────────
    private void DrawLaser(Graphics g, RectangleF st)
    {
        float s = Css(LaserSize);
        var c = new PointF((float)(st.X + LaserX * st.Width), (float)(st.Y + LaserY * st.Height));
        // glow = box-shadow 0 0 {size}px {color}
        float gr = s / 2 + s;
        using (var path = new GraphicsPath())
        {
            path.AddEllipse(c.X - gr, c.Y - gr, gr * 2, gr * 2);
            using var pgb = new PathGradientBrush(path)
            {
                CenterColor = Color.FromArgb(150, LaserColor),
                SurroundColors = new[] { Color.FromArgb(0, LaserColor) },
                CenterPoint = c
            };
            g.FillEllipse(pgb, c.X - gr, c.Y - gr, gr * 2, gr * 2);
        }
        using var b = new SolidBrush(LaserColor);
        g.FillEllipse(b, c.X - s / 2, c.Y - s / 2, s, s);
    }

    private void DrawLaserLabel(Graphics g, RectangleF st)
    {
        var c = new PointF((float)(st.X + LaserX * st.Width), (float)(st.Y + LaserY * st.Height));
        using var f = new Font("Segoe UI", Css(16), FontStyle.Bold, GraphicsUnit.Pixel);
        bool rtl = IsRtl(LaserLabel);
        using var sf = new StringFormat(rtl ? StringFormatFlags.DirectionRightToLeft : 0);
        float padX = Css(12), padY = Css(6);
        var ts = g.MeasureString(LaserLabel, f, (int)(st.Width * 0.5f - 2 * padX), sf);
        float w = ts.Width + 2 * padX, h = ts.Height + 2 * padY;
        var box = new RectangleF(c.X + 0.1f * w, c.Y - h / 2, w, h); // translate(10%, -50%)
        Shadow(g, box, h / 2);
        using (var path = Rounded(box, Math.Min(h / 2, Css(999))))
        using (var bb = new SolidBrush(LaserColor)) g.FillPath(bb, path);
        using var tb = new SolidBrush(Html("#111827"));
        g.DrawString(LaserLabel, f, tb, new RectangleF(box.X + padX, box.Y + padY, ts.Width + 2, ts.Height + 2), sf);
    }

    // ────────────────────────────────────────────────────────────────────
    //  Projector timer (bottom-right) + big threshold flash
    // ────────────────────────────────────────────────────────────────────
    private void DrawTimer(Graphics g)
    {
        if (!TimerVisible || TimerSeconds < 0) return;
        bool down = TimerMode == "down";
        int secs = TimerSeconds;
        string txt = $"{secs / 60:00}:{secs % 60:00}";
        Color bg, fg, border; float op = 1f;
        double t = Now / 1000.0;
        if (down && secs == 0) { bg = Color.FromArgb(230, 220, 38, 38); fg = Color.White; border = Html("#f87171"); op = (float)(0.75 + 0.25 * Math.Cos(t * Math.PI)); }
        else if (down && secs <= 60) { bg = Color.FromArgb(230, 245, 158, 11); fg = Color.Black; border = Html("#fcd34d"); op = (float)(0.75 + 0.25 * Math.Cos(t * Math.PI)); }
        else { bg = Color.FromArgb(179, 0, 0, 0); fg = Color.White; border = Color.FromArgb(51, 255, 255, 255); }

        using var f = new Font("Consolas", Css(30), FontStyle.Bold, GraphicsUnit.Pixel);
        using var fi = new Font("Segoe UI Symbol", Css(26), FontStyle.Regular, GraphicsUnit.Pixel);
        string icon = down ? "⌛" : "⏱";
        var ts = g.MeasureString(txt, f); var isz = g.MeasureString(icon, fi);
        float padX = Css(20), padY = Css(10);
        float w = isz.Width + Css(4) + ts.Width + 2 * padX, h = Math.Max(ts.Height, isz.Height) + 2 * padY;
        var box = new RectangleF(_surfSize.Width - Css(20) - w, _surfSize.Height - Css(20) - h, w, h);
        Shadow(g, box, Css(16));
        using (var path = Rounded(box, Css(16)))
        {
            using var bb = new SolidBrush(WithAlpha(bg, op)); g.FillPath(bb, path);
            using var bp = new Pen(WithAlpha(border, op), Math.Max(1, Css(1))); g.DrawPath(bp, path);
        }
        using var tb = new SolidBrush(WithAlpha(fg, op));
        g.DrawString(icon, fi, tb, box.X + padX, box.Y + (h - isz.Height) / 2);
        g.DrawString(txt, f, tb, box.X + padX + isz.Width + Css(4), box.Y + (h - ts.Height) / 2);
    }

    private void DrawAlert(Graphics g)
    {
        if (_alertLabel == null) return;
        using (var dim = new SolidBrush(Color.FromArgb(128, 0, 0, 0))) g.FillRectangle(dim, 0, 0, _surfSize.Width, _surfSize.Height);
        double e = (Environment.TickCount64 - _alertStart) / 350.0;
        float sc = e >= 1 ? 1f : e < .6 ? (float)(0.5 + (1.15 - 0.5) * (e / .6)) : (float)(1.15 - 0.15 * ((e - .6) / .4));
        float op = e >= .6 ? 1f : (float)(e / .6);
        bool zero = _alertLabel == "0";
        string txt = zero ? "⏰" : _alertLabel;
        float size = _surfSize.Width * 0.22f * sc;
        using var f = new Font(zero ? "Segoe UI Symbol" : "Consolas", size, FontStyle.Bold, GraphicsUnit.Pixel);
        using var b = new SolidBrush(WithAlpha(Html(zero ? "#ef4444" : "#fbbf24"), op));
        g.DrawString(txt, f, b, new RectangleF(0, 0, _surfSize.Width, _surfSize.Height), Center);
    }

    // ────────────────────────────────────────────────────────────────────
    //  helpers
    // ────────────────────────────────────────────────────────────────────
    private void Shadow(Graphics g, RectangleF r, float radius)
    {
        // soft "shadow-lg"
        for (int i = 4; i >= 1; i--)
        {
            var sr = RectangleF.Inflate(r, Css(i * 2), Css(i * 2));
            sr.Offset(0, Css(4));
            using var p = Rounded(sr, radius + Css(i * 2));
            using var b = new SolidBrush(Color.FromArgb(10, 0, 0, 0));
            g.FillPath(b, p);
        }
    }

    private static GraphicsPath Rounded(RectangleF r, float rad)
    {
        var p = new GraphicsPath();
        rad = Math.Max(0.5f, Math.Min(rad, Math.Min(r.Width, r.Height) / 2));
        float d = rad * 2;
        p.AddArc(r.X, r.Y, d, d, 180, 90);
        p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        p.CloseFigure();
        return p;
    }

    public static Color Html(string s)
    {
        if (string.IsNullOrEmpty(s) || s == "transparent") return Color.Transparent;
        try { return ColorTranslator.FromHtml(s); } catch { return Color.Red; }
    }

    private static Color WithAlpha(Color c, float a) => Color.FromArgb(Math.Clamp((int)(c.A * a), 0, 255), c.R, c.G, c.B);

    private static bool IsRtl(string s)
    {
        foreach (char ch in s)
        {
            if (ch >= 0x0590 && ch <= 0x08FF) return true;
            if (char.IsLetter(ch)) return false;
        }
        return false;
    }

    private static uint Pm(Color c)
    {
        uint a = c.A;
        uint r = c.R * a / 255, g = c.G * a / 255, b = c.B * a / 255;
        return (a << 24) | (r << 16) | (g << 8) | b;
    }

    private static Color Unpremul(uint p)
    {
        int a = (int)(p >> 24);
        if (a == 0) return Color.Transparent;
        int r = (int)((p >> 16) & 255) * 255 / a, g = (int)((p >> 8) & 255) * 255 / a, b = (int)(p & 255) * 255 / a;
        return Color.FromArgb(a, Math.Min(255, r), Math.Min(255, g), Math.Min(255, b));
    }

    private static uint Lerp(uint a, uint b, float t)
    {
        t = Math.Clamp(t, 0, 1);
        uint L(int sh) => (uint)Math.Round(((a >> sh) & 255) + (((int)((b >> sh) & 255)) - (int)((a >> sh) & 255)) * t) & 255;
        return (L(24) << 24) | (L(16) << 16) | (L(8) << 8) | L(0);
    }

    private static Color HueRotate(Color c, double deg)
    {
        // same matrix CSS hue-rotate() uses
        double rad = deg * Math.PI / 180, cos = Math.Cos(rad), sin = Math.Sin(rad);
        double r = c.R, g = c.G, b = c.B;
        double nr = r * (.213 + cos * .787 - sin * .213) + g * (.715 - cos * .715 - sin * .715) + b * (.072 - cos * .072 + sin * .928);
        double ng = r * (.213 - cos * .213 + sin * .143) + g * (.715 + cos * .285 + sin * .140) + b * (.072 - cos * .072 - sin * .283);
        double nb = r * (.213 - cos * .213 - sin * .787) + g * (.715 - cos * .715 + sin * .715) + b * (.072 + cos * .928 + sin * .072);
        return Color.FromArgb(c.A, (int)Math.Clamp(nr, 0, 255), (int)Math.Clamp(ng, 0, 255), (int)Math.Clamp(nb, 0, 255));
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) { _frame.Dispose(); _cap?.Dispose(); _staticCap?.Dispose(); _spriteBmp?.Dispose(); _lensCap?.Dispose(); }
        FreeSurface();
        base.Dispose(disposing);
    }
}
