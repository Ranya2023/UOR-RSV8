using System.Drawing.Drawing2D;
using System.Drawing.Text;

namespace Remco;

/// <summary>Borderless, always-on-top window covering the projector (base for Remco's full-screen views).</summary>
internal class FullScreenView : Form
{
    public FullScreenView()
    {
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.Manual;
        TopMost = true;
        BackColor = Color.Black;
        Icon = MainForm.AppIcon;
        DoubleBuffered = true;
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer, true);
    }

    protected override bool ShowWithoutActivation => true;

    public void Open(Rectangle area)
    {
        if (Bounds != area) Bounds = area;
        if (!Visible) Show();
        Invalidate();
    }

    /// <summary>"CSS pixel" size: scales with the screen like the rest of Remco's drawings.</summary>
    protected float Px(float v) => v * ClientSize.Height / 1080f;

    protected static void Centered(Graphics g, string text, Font f, Brush b, RectangleF r)
    {
        using var sf = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center, Trimming = StringTrimming.EllipsisCharacter };
        if (text.Any(ch => ch >= 0x0590 && ch <= 0x08FF)) sf.FormatFlags |= StringFormatFlags.DirectionRightToLeft;
        g.DrawString(text, f, b, r, sf);
    }
}

// ─────────────────────────────────────────────────────────────────────────
//  🧑‍🏫 Whiteboard — pages you write on with the pen / highlighter / numbers / text
// ─────────────────────────────────────────────────────────────────────────
internal sealed class BoardForm : FullScreenView
{
    public int Page { get; private set; } = 1;
    public int Pages { get; private set; } = 1;
    public string Bg { get; private set; } = "white";   // white | grid | black | green
    public string Key => "board:" + Page;
    public event Action? Changed;

    public BoardForm() { Text = "UOR-RC – whiteboard"; }

    public void SetBg(string bg) { Bg = bg is "grid" or "black" or "green" ? bg : "white"; Invalidate(); Changed?.Invoke(); }
    public void NextPage() { if (Page == Pages) Pages++; Page++; Invalidate(); Changed?.Invoke(); }
    public void PrevPage() { if (Page > 1) { Page--; Invalidate(); Changed?.Invoke(); } }
    public void SetPages(int pages) { Pages = Math.Max(1, pages); Page = Math.Min(Page, Pages); }

    public static Color BgColor(string bg) => bg switch
    {
        "black" => Color.FromArgb(0x16, 0x18, 0x1d),
        "green" => Color.FromArgb(0x1f, 0x4d, 0x3a),
        _ => Color.White
    };

    /// <summary>Background (and grid) — also used when saving pages as pictures.</summary>
    public static void PaintBackground(Graphics g, RectangleF r, string bg)
    {
        using (var b = new SolidBrush(BgColor(bg))) g.FillRectangle(b, r);
        if (bg != "grid") return;
        float step = r.Height / 18f;
        using var p = new Pen(Color.FromArgb(40, 60, 90, 160), Math.Max(1, r.Height / 1080f));
        for (float x = r.X + step; x < r.Right; x += step) g.DrawLine(p, x, r.Y, x, r.Bottom);
        for (float y = r.Y + step; y < r.Bottom; y += step) g.DrawLine(p, r.X, y, r.Right, y);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        PaintBackground(g, ClientRectangle, Bg);
        g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
        using var f = new Font("Segoe UI", Px(20), FontStyle.Bold, GraphicsUnit.Pixel);
        using var b = new SolidBrush(Bg is "black" or "green" ? Color.FromArgb(120, 255, 255, 255) : Color.FromArgb(110, 0, 0, 0));
        string t = $"{Page} / {Pages}";
        var sz = g.MeasureString(t, f);
        g.DrawString(t, f, b, ClientSize.Width - sz.Width - Px(24), ClientSize.Height - sz.Height - Px(18));
    }
}

// ─────────────────────────────────────────────────────────────────────────
//  🎲 Random student picker — names spin, slow down and land on the winner
// ─────────────────────────────────────────────────────────────────────────
internal sealed class PickerScreen : FullScreenView
{
    private List<string> _names = new();
    private string _title = "";
    public string Style = "names";        // "names" = big names · "wheel" = spinning wheel
    private int _winner;
    private int _start, _steps, _remaining;
    private long _t0;
    private const int SpinMs = 3800;
    private readonly System.Windows.Forms.Timer _tick = new() { Interval = 16 };
    private readonly Random _rnd = new();
    private (float x, float y, float vy, float rot, Color c)[] _confetti = Array.Empty<(float, float, float, float, Color)>();
    public event Action<string>? Done;
    private bool _doneSent;

    public PickerScreen()
    {
        Text = "UOR-RC – random picker";
        _tick.Tick += (_, _) => Invalidate();
    }

    public void Pick(List<string> names, int winner, string title, int remaining, Rectangle area, string style = "names")
    {
        if (names.Count == 0) return;
        Style = style == "wheel" ? "wheel" : "names";
        _winner = Math.Clamp(winner, 0, names.Count - 1);
        _names = names;
        _title = title;
        _remaining = remaining;
        winner = Math.Clamp(winner, 0, names.Count - 1);
        _start = _rnd.Next(names.Count);
        _steps = PickMath.Steps(names.Count, _start, winner);
        _t0 = Environment.TickCount64;
        _doneSent = false;
        _confetti = Array.Empty<(float, float, float, float, Color)>();
        Open(area);
        _tick.Start();
    }

    private static double EaseOut(double t) => 1 - Math.Pow(1 - Math.Clamp(t, 0, 1), 3);

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
        using (var bg = new LinearGradientBrush(ClientRectangle, Color.FromArgb(0x1d, 0x25, 0x40), Color.FromArgb(0x0a, 0x0d, 0x16), 90f))
            g.FillRectangle(bg, ClientRectangle);
        if (_names.Count == 0) return;

        long t = Environment.TickCount64 - _t0;
        double p = EaseOut(t / (double)SpinMs);
        if (Style == "wheel") { PaintWheel(g, p, t >= SpinMs, t); return; }
        int step = (int)Math.Round(p * _steps);
        bool done = t >= SpinMs;
        string name = _names[(_start + step) % _names.Count];

        using (var tf = new Font("Segoe UI", Px(40), FontStyle.Bold, GraphicsUnit.Pixel))
        using (var tb = new SolidBrush(Color.FromArgb(0x9A, 0xA3, 0xBD)))
            Centered(g, "🎲  " + (_title.Length > 0 ? _title : "Random pick"), tf, tb, new RectangleF(0, Px(60), ClientSize.Width, Px(80)));

        // the name: big, fitted to the width
        float size = Px(done ? 190 : 150);
        using var nf0 = new Font("Segoe UI", size, FontStyle.Bold, GraphicsUnit.Pixel);
        var ms = g.MeasureString(name, nf0);
        if (ms.Width > ClientSize.Width * 0.9f) size *= ClientSize.Width * 0.9f / ms.Width;
        using var nf = new Font("Segoe UI", size, FontStyle.Bold, GraphicsUnit.Pixel);
        var box = new RectangleF(0, ClientSize.Height * 0.3f, ClientSize.Width, ClientSize.Height * 0.4f);
        if (done)
        {
            // glowing card behind the winner
            var card = RectangleF.Inflate(new RectangleF(ClientSize.Width * 0.08f, box.Y, ClientSize.Width * 0.84f, box.Height), 0, 0);
            using (var cb = new SolidBrush(Color.FromArgb(40, 251, 191, 36))) g.FillRectangle(cb, card);
            using var cp = new Pen(Color.FromArgb(0xfb, 0xbf, 0x24), Px(5));
            g.DrawRectangle(cp, card.X, card.Y, card.Width, card.Height);
        }
        using (var nb = new SolidBrush(done ? Color.FromArgb(0xfb, 0xbf, 0x24) : Color.White))
            Centered(g, name, nf, nb, box);

        if (_remaining >= 0)
        {
            using var sf = new Font("Segoe UI", Px(28), FontStyle.Regular, GraphicsUnit.Pixel);
            using var sb = new SolidBrush(Color.FromArgb(0x8A, 0x90, 0xA2));
            Centered(g, $"{_remaining} left", sf, sb, new RectangleF(0, ClientSize.Height - Px(120), ClientSize.Width, Px(60)));
        }

        if (done)
        {
            if (_confetti.Length == 0) MakeConfetti();
            DrawConfetti(g, (t - SpinMs) / 1000f);
            if (!_doneSent) { _doneSent = true; Done?.Invoke(name); }
            if (t - SpinMs > 4500) _tick.Stop();
        }
    }

    /// <summary>🎡 The wheel: it spins and slows down until the pointer is on the chosen name.</summary>
    private void PaintWheel(Graphics g, double p, bool done, long t)
    {
        int n = _names.Count;
        float W = ClientSize.Width, H = ClientSize.Height;
        float r = Math.Min(W, H) * 0.40f;
        var c = new PointF(W / 2, H * 0.53f);
        float seg = 360f / n;
        float end = 270f - (_winner * seg + seg / 2) + 360f * (n <= 6 ? 6 : 4);
        float rot = (float)(end * p);
        var box = new RectangleF(c.X - r, c.Y - r, 2 * r, 2 * r);

        using (var tf = new Font("Segoe UI", Px(34), FontStyle.Bold, GraphicsUnit.Pixel))
        using (var tb = new SolidBrush(Color.FromArgb(0x9A, 0xA3, 0xBD)))
            Centered(g, "🎲  " + (_title.Length > 0 ? _title : "Random pick"), tf, tb, new RectangleF(0, Px(30), W, Px(60)));

        float nameFont = Math.Max(Px(12), Math.Min(Px(34), r * 1.2f / Math.Max(6, n)));
        using var nf = new Font("Segoe UI", nameFont, FontStyle.Bold, GraphicsUnit.Pixel);
        for (int i = 0; i < n; i++)
        {
            using var b = new SolidBrush(WheelColor(i, n));
            g.FillPie(b, box.X, box.Y, box.Width, box.Height, rot + i * seg, seg);
            var st = g.Save();
            g.TranslateTransform(c.X, c.Y);
            g.RotateTransform(rot + i * seg + seg / 2);
            using var sfm = new StringFormat { Alignment = StringAlignment.Far, LineAlignment = StringAlignment.Center, Trimming = StringTrimming.EllipsisCharacter, FormatFlags = StringFormatFlags.NoWrap };
            using var wb = new SolidBrush(Color.White);
            g.DrawString(_names[i], nf, wb, new RectangleF(r * 0.25f, -nameFont * 0.7f, r * 0.70f, nameFont * 1.4f), sfm);
            g.Restore(st);
        }
        using (var rim = new Pen(Color.FromArgb(0xfb, 0xbf, 0x24), Px(6))) g.DrawEllipse(rim, box);
        // hub + pointer
        using (var hub = new SolidBrush(Color.FromArgb(0x0a, 0x0d, 0x16))) g.FillEllipse(hub, c.X - r * 0.16f, c.Y - r * 0.16f, r * 0.32f, r * 0.32f);
        using (var pb = new SolidBrush(Color.FromArgb(0xfb, 0xbf, 0x24)))
            g.FillPolygon(pb, new[] { new PointF(c.X, c.Y - r + Px(26)), new PointF(c.X - Px(26), c.Y - r - Px(26)), new PointF(c.X + Px(26), c.Y - r - Px(26)) });

        if (done)
        {
            using var wf = new Font("Segoe UI", Px(58), FontStyle.Bold, GraphicsUnit.Pixel);
            using var wb2 = new SolidBrush(Color.FromArgb(0xfb, 0xbf, 0x24));
            Centered(g, _names[_winner], wf, wb2, new RectangleF(0, H - Px(120), W, Px(80)));
            if (_confetti.Length == 0) MakeConfetti();
            DrawConfetti(g, (t - SpinMs) / 1000f);
            if (!_doneSent) { _doneSent = true; Done?.Invoke(_names[_winner]); }
            if (t - SpinMs > 4500) _tick.Stop();
        }
    }

    private static Color WheelColor(int i, int n)
    {
        Color[] cols = { Color.FromArgb(0xef,0x44,0x44), Color.FromArgb(0x3b,0x82,0xf6), Color.FromArgb(0xea,0xb3,0x08),
                         Color.FromArgb(0x22,0xc5,0x5e), Color.FromArgb(0xa8,0x55,0xf7), Color.FromArgb(0xf9,0x73,0x16),
                         Color.FromArgb(0x06,0xb6,0xd4), Color.FromArgb(0xec,0x48,0x99) };
        var c = cols[i % cols.Length];
        return (i == n - 1 && n % cols.Length == 1) ? cols[(i + 1) % cols.Length] : c;
    }

    private void MakeConfetti()
    {
        Color[] cols = { Color.FromArgb(0xf4, 0x3f, 0x5e), Color.FromArgb(0x3b, 0x82, 0xf6), Color.FromArgb(0x22, 0xc5, 0x5e), Color.FromArgb(0xea, 0xb3, 0x08), Color.FromArgb(0xa8, 0x55, 0xf7) };
        _confetti = Enumerable.Range(0, 140).Select(_ => ((float)_rnd.NextDouble(), (float)(-_rnd.NextDouble() * 0.6), 0.25f + (float)_rnd.NextDouble() * 0.35f, (float)_rnd.NextDouble() * 360, cols[_rnd.Next(cols.Length)])).ToArray();
    }

    private void DrawConfetti(Graphics g, float secs)
    {
        foreach (var c in _confetti)
        {
            float y = (c.y + c.vy * secs) * ClientSize.Height;
            if (y > ClientSize.Height) continue;
            float x = c.x * ClientSize.Width + (float)Math.Sin(secs * 3 + c.rot) * Px(20);
            var st = g.Save();
            g.TranslateTransform(x, y);
            g.RotateTransform(c.rot + secs * 200);
            using var b = new SolidBrush(c.c);
            g.FillRectangle(b, -Px(6), -Px(9), Px(12), Px(18));
            g.Restore(st);
        }
    }

    protected override void Dispose(bool disposing) { if (disposing) _tick.Dispose(); base.Dispose(disposing); }
}

// ─────────────────────────────────────────────────────────────────────────
//  🗳️ Live quiz / poll — what the projector shows
// ─────────────────────────────────────────────────────────────────────────
internal sealed class QuizScreen : FullScreenView
{
    private static readonly Color[] OptColors =
    {
        Color.FromArgb(0xef, 0x44, 0x44), Color.FromArgb(0x3b, 0x82, 0xf6), Color.FromArgb(0xea, 0xb3, 0x08),
        Color.FromArgb(0x22, 0xc5, 0x5e), Color.FromArgb(0xa8, 0x55, 0xf7), Color.FromArgb(0xf9, 0x73, 0x16)
    };

    public string Question = "";
    public List<string> Options = new();
    public int[] Counts = new int[4];
    public bool Reveal, IsOpen = true;
    public int Correct = -1;
    public int QIndex, QCount, Players;
    public int Left = -1;          // seconds left (-1 = no time limit)
    public int QuizSecs = 30;      // the question's time limit
    public bool ShowScores;
    public List<(string Name, int Score, int Last)> Top = new();
    public string Url = "";
    public string WifiName = "", WifiPass = "";
    public Image? UrlQr, WifiQr;

    public QuizScreen() { Text = "UOR-RC – quiz"; }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
        g.InterpolationMode = InterpolationMode.NearestNeighbor;
        using (var bg = new LinearGradientBrush(ClientRectangle, Color.FromArgb(0x16, 0x1b, 0x2e), Color.FromArgb(0x0a, 0x0d, 0x16), 90f))
            g.FillRectangle(bg, ClientRectangle);
        if (ShowScores) { PaintScores(g); return; }
        float W = ClientSize.Width, H = ClientSize.Height;
        using var white = new SolidBrush(Color.White);
        using var muted = new SolidBrush(Color.FromArgb(0x9A, 0xA3, 0xBD));

        // countdown ring, top-right
        if (Left >= 0 && IsOpen)
        {
            float d = Px(120), m = Px(30);
            var box = new RectangleF(W - d - m, m, d, d);
            using (var back = new Pen(Color.FromArgb(60, 255, 255, 255), Px(10))) g.DrawEllipse(back, box);
            using (var arc = new Pen(Left <= 5 ? Color.FromArgb(0xef, 0x44, 0x44) : Color.FromArgb(0x4f, 0x7d, 0xff), Px(10)))
                g.DrawArc(arc, box, -90, -360f * Left / Math.Max(1, QuizSecs));
            using var cf = new Font("Segoe UI", Px(44), FontStyle.Bold, GraphicsUnit.Pixel);
            Centered(g, Left.ToString(), cf, white, box);
        }
        string title = (QCount > 1 ? $"({QIndex}/{QCount})  " : "") + (Question.Length > 0 ? Question : "🗳️  Quiz");
        using (var qf = new Font("Segoe UI", Px(52), FontStyle.Bold, GraphicsUnit.Pixel))
            Centered(g, title, qf, white, new RectangleF(Px(40), Px(24), W - Px(80), Px(150)));

        // left: how to join
        float lx = Px(60), ly = Px(200), qr = Px(230);
        using var hf = new Font("Segoe UI", Px(26), FontStyle.Bold, GraphicsUnit.Pixel);
        using var sf = new Font("Segoe UI", Px(22), FontStyle.Regular, GraphicsUnit.Pixel);
        int step = 1;
        if (WifiQr != null && WifiName.Length > 0)
        {
            g.DrawString($"{step++}. Join Wi-Fi", hf, white, lx, ly);
            g.FillRectangle(Brushes.White, lx, ly + Px(40), qr, qr);
            g.DrawImage(WifiQr, lx + Px(10), ly + Px(50), qr - Px(20), qr - Px(20));
            g.DrawString($"{WifiName}\npassword: {WifiPass}", sf, muted, lx, ly + Px(46) + qr);
            ly += qr + Px(125);
        }
        if (UrlQr != null)
        {
            g.DrawString($"{step}. Scan & type your name", hf, white, lx, ly);
            g.FillRectangle(Brushes.White, lx, ly + Px(40), qr, qr);
            g.DrawImage(UrlQr, lx + Px(10), ly + Px(50), qr - Px(20), qr - Px(20));
            using var uf = new Font("Consolas", Px(24), FontStyle.Bold, GraphicsUnit.Pixel);
            using var ub = new SolidBrush(Color.FromArgb(0x7f, 0xa8, 0xff));
            g.DrawString(Url, uf, ub, lx, ly + Px(46) + qr);
        }

        // right: the answers
        int options = Math.Max(2, Math.Max(Options.Count, Counts.Length));
        float rx = Px(400), rw = W - rx - Px(70), top = Px(200);
        int total = Counts.Sum();
        using (var tf = new Font("Segoe UI", Px(32), FontStyle.Bold, GraphicsUnit.Pixel))
            g.DrawString($"👥  {Players} student{(Players == 1 ? "" : "s")}   ·   ✍️ {total} answered" + (IsOpen ? "" : "   ·   closed"),
                tf, white, rx, top - Px(10));
        float barTop = top + Px(66), barGap = Px(12);
        float barH = Math.Min(Px(112), (H - barTop - Px(50) - barGap * (options - 1)) / options);
        using var lf = new Font("Segoe UI", barH * 0.40f, FontStyle.Bold, GraphicsUnit.Pixel);
        using var of = new Font("Segoe UI", barH * 0.30f, FontStyle.Bold, GraphicsUnit.Pixel);
        for (int i = 0; i < options; i++)
        {
            float y = barTop + i * (barH + barGap);
            var col = OptColors[i % OptColors.Length];
            using (var lb = new SolidBrush(col)) g.FillRectangle(lb, rx, y, barH, barH);
            Centered(g, ((char)('A' + i)).ToString(), lf, white, new RectangleF(rx, y, barH, barH));
            float bx = rx + barH + Px(12), bw = rw - barH - Px(12);
            using (var track = new SolidBrush(Color.FromArgb(40, 255, 255, 255))) g.FillRectangle(track, bx, y, bw, barH);
            if (Reveal)
            {
                float frac = total > 0 ? Counts[i] / (float)total : 0;
                bool right = Correct == i, wrong = Correct >= 0 && Correct != i;
                using var fb = new SolidBrush(Color.FromArgb(wrong ? 80 : 220, right ? Color.FromArgb(0x22, 0xc5, 0x5e) : col));
                g.FillRectangle(fb, bx, y, Math.Max(Px(4), bw * frac), barH);
            }
            string text = i < Options.Count && Options[i].Length > 0 ? Options[i] : "";
            string tail = Reveal ? $"   {Counts[i]} · {(total > 0 ? Math.Round(Counts[i] * 100.0 / total) : 0)}%" + (Correct == i ? "  ✓" : "") : "";
            using var sfm = new StringFormat { LineAlignment = StringAlignment.Center, Trimming = StringTrimming.EllipsisCharacter, FormatFlags = StringFormatFlags.NoWrap };
            if (text.Any(ch => ch >= 0x0590 && ch <= 0x08FF)) sfm.FormatFlags |= StringFormatFlags.DirectionRightToLeft;
            g.DrawString(text + tail, of, white, new RectangleF(bx + Px(16), y, bw - Px(24), barH), sfm);
        }
        if (!Reveal)
        {
            using var hint = new Font("Segoe UI", Px(22), FontStyle.Italic, GraphicsUnit.Pixel);
            g.DrawString("Answers are hidden until the teacher shows them", hint, muted, rx, barTop + options * (barH + barGap));
        }
    }

    /// <summary>🏆 Leaderboard / podium.</summary>
    private void PaintScores(Graphics g)
    {
        float W = ClientSize.Width, H = ClientSize.Height;
        using var white = new SolidBrush(Color.White);
        using var gold = new SolidBrush(Color.FromArgb(0xfb, 0xbf, 0x24));
        using (var tf = new Font("Segoe UI", Px(60), FontStyle.Bold, GraphicsUnit.Pixel))
            Centered(g, "🏆  Scores", tf, white, new RectangleF(0, Px(50), W, Px(90)));
        if (Top.Count == 0)
        {
            using var nf = new Font("Segoe UI", Px(30), FontStyle.Regular, GraphicsUnit.Pixel);
            Centered(g, "No answers yet", nf, white, new RectangleF(0, H / 2 - Px(30), W, Px(60)));
            return;
        }
        float y = Px(180), rowH = Math.Min(Px(96), (H - y - Px(60)) / Math.Max(1, Top.Count));
        string[] medal = { "🥇", "🥈", "🥉" };
        using var rf = new Font("Segoe UI", rowH * 0.46f, FontStyle.Bold, GraphicsUnit.Pixel);
        for (int i = 0; i < Top.Count; i++)
        {
            var r = new RectangleF(W * 0.18f, y + i * rowH, W * 0.64f, rowH - Px(10));
            using (var bb = new SolidBrush(Color.FromArgb(i == 0 ? 60 : 30, 255, 255, 255))) g.FillRectangle(bb, r);
            string who = (i < 3 ? medal[i] + "  " : $"{i + 1}.  ") + Top[i].Name;
            using var sfm = new StringFormat { LineAlignment = StringAlignment.Center, Trimming = StringTrimming.EllipsisCharacter };
            g.DrawString(who, rf, white, new RectangleF(r.X + Px(20), r.Y, r.Width * 0.7f, r.Height), sfm);
            var right = new StringFormat { Alignment = StringAlignment.Far, LineAlignment = StringAlignment.Center };
            g.DrawString(Top[i].Score.ToString(), rf, i == 0 ? gold : white, new RectangleF(r.X, r.Y, r.Width - Px(20), r.Height), right);
        }
    }
}

// ─────────────────────────────────────────────────────────────────────────
//  📸 Group gallery — photos taken with the phone camera, shown together
// ─────────────────────────────────────────────────────────────────────────
internal sealed class GalleryScreen : FullScreenView
{
    public sealed class Shot { public Bitmap Img = null!; public string Caption = ""; public string Path = ""; }

    private readonly List<Shot> _shots = new();
    public bool Single;             // false = grid of all photos, true = one photo
    public int Index;
    public int Count => _shots.Count;
    public string Key => Single && Index < _shots.Count ? "shot:" + Index : "shots";

    public GalleryScreen() { Text = "UOR-RC – group photos"; }

    public void Add(Bitmap b, string caption, string path)
    {
        _shots.Add(new Shot { Img = b, Caption = caption, Path = path });
        Index = _shots.Count - 1;
        Invalidate();
    }

    public void Clear()
    {
        foreach (var s in _shots) s.Img.Dispose();
        _shots.Clear(); Index = 0; Single = false;
        Invalidate();
    }

    public void Next() { if (_shots.Count > 0) { Index = (Index + 1) % _shots.Count; Invalidate(); } }
    public void Prev() { if (_shots.Count > 0) { Index = (Index - 1 + _shots.Count) % _shots.Count; Invalidate(); } }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
        using (var bg = new LinearGradientBrush(ClientRectangle, Color.FromArgb(0x11, 0x14, 0x20), Color.FromArgb(0x07, 0x09, 0x10), 90f))
            g.FillRectangle(bg, ClientRectangle);
        if (_shots.Count == 0)
        {
            using var f = new Font("Segoe UI", Px(34), FontStyle.Regular, GraphicsUnit.Pixel);
            using var b = new SolidBrush(Color.FromArgb(0x8d, 0x95, 0xad));
            Centered(g, "📸  Take photos with the phone camera", f, b, ClientRectangle);
            return;
        }
        if (Single) { PaintOne(g, _shots[Math.Clamp(Index, 0, _shots.Count - 1)], ClientRectangle, Px(30)); return; }

        int n = _shots.Count;
        int cols = n <= 1 ? 1 : n <= 4 ? 2 : n <= 9 ? 3 : 4;
        int rows = (int)Math.Ceiling(n / (double)cols);
        float gap = Px(18), m = Px(24);
        float cw = (ClientSize.Width - 2 * m - gap * (cols - 1)) / cols;
        float ch = (ClientSize.Height - 2 * m - gap * (rows - 1)) / rows;
        for (int i = 0; i < n; i++)
        {
            var cell = new RectangleF(m + (i % cols) * (cw + gap), m + (i / cols) * (ch + gap), cw, ch);
            PaintOne(g, _shots[i], Rectangle.Round(cell), Px(20), i == Index);
        }
    }

    private void PaintOne(Graphics g, Shot s, Rectangle area, float caption, bool mark = false)
    {
        float capH = s.Caption.Length > 0 ? caption * 2.1f : 0;
        var pic = new RectangleF(area.X, area.Y, area.Width, area.Height - capH);
        float scale = Math.Min(pic.Width / s.Img.Width, pic.Height / s.Img.Height);
        float w = s.Img.Width * scale, h = s.Img.Height * scale;
        var r = new RectangleF(pic.X + (pic.Width - w) / 2, pic.Y + (pic.Height - h) / 2, w, h);
        using (var sh = new SolidBrush(Color.FromArgb(70, 0, 0, 0))) g.FillRectangle(sh, RectangleF.Inflate(r, Px(5), Px(5)));
        g.InterpolationMode = InterpolationMode.HighQualityBicubic;
        g.DrawImage(s.Img, r);
        if (mark) using (var p = new Pen(Color.FromArgb(0x4f, 0x7d, 0xff), Px(4))) g.DrawRectangle(p, r.X, r.Y, r.Width, r.Height);
        if (s.Caption.Length == 0) return;
        using var f = new Font("Segoe UI", caption, FontStyle.Bold, GraphicsUnit.Pixel);
        using var b = new SolidBrush(Color.White);
        Centered(g, s.Caption, f, b, new RectangleF(area.X, area.Bottom - capH, area.Width, capH));
    }

    protected override void Dispose(bool disposing) { if (disposing) foreach (var s in _shots) s.Img.Dispose(); base.Dispose(disposing); }
}
