using System.Drawing.Drawing2D;
using System.Drawing.Imaging;

namespace Remco;

/// <summary>
/// Streams the PC screen to the phone's touchpad ("See the screen" in Mouse mode).
/// Self-pacing: the next frame is only sent after the phone says it showed the
/// previous one, so it runs as fast as the link allows (Wi-Fi ≫ Bluetooth).
/// </summary>
internal sealed class ScreenMirror
{
    private readonly Action<object> _send;
    private readonly Func<Rectangle> _area;
    private readonly AutoResetEvent _ack = new(false);
    private volatile bool _on;
    private volatile int _width = 800;
    private int _gen;
    private static readonly ImageCodecInfo? Jpeg = ImageCodecInfo.GetImageEncoders().FirstOrDefault(c => c.FormatID == ImageFormat.Jpeg.Guid);

    /// <summary>Screen rectangle of the last frame sent (for mapping taps).</summary>
    public Rectangle Rect { get; private set; }

    public ScreenMirror(Action<object> send, Func<Rectangle> area) { _send = send; _area = area; }

    public void Start(int width)
    {
        _width = Math.Clamp(width, 320, 1280);
        if (_on) { _ack.Set(); return; }
        _on = true;
        int gen = Interlocked.Increment(ref _gen);
        new Thread(() => Loop(gen)) { IsBackground = true, Name = "mirror" }.Start();
    }

    public void Stop() { _on = false; Interlocked.Increment(ref _gen); _ack.Set(); }

    public void Ack() => _ack.Set();

    private void Loop(int gen)
    {
        // quicker frames on Wi-Fi (big width requested), gentler on Bluetooth
        while (_on && gen == _gen)
        {
            try { SendFrame(); } catch { }
            _ack.WaitOne(2500);
            if (!_on || gen != _gen) break;
            Thread.Sleep(_width >= 800 ? 60 : 250);
        }
    }

    private void SendFrame()
    {
        var area = _area();
        if (area.Width < 10 || area.Height < 10) area = Screen.PrimaryScreen?.Bounds ?? new Rectangle(0, 0, 1920, 1080);
        Rect = area;
        int w = Math.Min(_width, area.Width);
        int h = (int)Math.Round((double)w * area.Height / area.Width);

        using var full = new Bitmap(area.Width, area.Height, PixelFormat.Format32bppRgb);
        using (var g = Graphics.FromImage(full))
        {
            g.CopyFromScreen(area.Left, area.Top, 0, 0, area.Size, CopyPixelOperation.SourceCopy);
            // draw the mouse pointer so it's visible on the phone
            if (Native.GetCursorPos(out var cp) && area.Contains(cp.X, cp.Y))
            {
                float r = Math.Max(6, area.Width / 180f);
                float x = cp.X - area.Left, y = cp.Y - area.Top;
                using var b = new SolidBrush(Color.FromArgb(200, 255, 59, 48));
                using var p = new Pen(Color.White, Math.Max(2, r / 3));
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.FillEllipse(b, x - r, y - r, r * 2, r * 2);
                g.DrawEllipse(p, x - r, y - r, r * 2, r * 2);
            }
        }
        using var small = new Bitmap(w, h, PixelFormat.Format24bppRgb);
        using (var g = Graphics.FromImage(small))
        {
            g.InterpolationMode = InterpolationMode.HighQualityBilinear;
            g.DrawImage(full, 0, 0, w, h);
        }
        using var ms = new MemoryStream();
        if (Jpeg != null)
        {
            using var ep = new EncoderParameters(1);
            ep.Param[0] = new EncoderParameter(System.Drawing.Imaging.Encoder.Quality, _width >= 800 ? 60L : 45L);
            small.Save(ms, Jpeg, ep);
        }
        else small.Save(ms, ImageFormat.Jpeg);
        _send(new { e = "frame", img = Convert.ToBase64String(ms.ToArray()), l = area.Left, t = area.Top, w = area.Width, h = area.Height });
    }
}
