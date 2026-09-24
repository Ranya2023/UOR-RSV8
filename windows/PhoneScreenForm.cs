namespace Remco;

/// <summary>Full-screen window on the projector showing the phone's screen (📱 Phone screen on PC).</summary>
internal sealed class PhoneScreenForm : Form
{
    private Bitmap? _frame;
    /// <summary>Extra rotation on the PC: 0, 90, 180 or 270 degrees.</summary>
    public int Rotation { get; set; }
    /// <summary>true = fill the whole projector (crops the edges), false = fit with black bars.</summary>
    public bool Fill { get; set; }

    public PhoneScreenForm()
    {
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.Manual;
        TopMost = true;
        BackColor = Color.Black;
        Text = "UOR-RC – phone screen";
        Icon = MainForm.AppIcon;
        DoubleBuffered = true;
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer, true);
        DoubleClick += (_, _) => Hide();   // double-click on the PC closes it too
    }

    protected override bool ShowWithoutActivation => true;

    public void ShowFrame(Bitmap bmp, Rectangle area)
    {
        var old = _frame;
        _frame = bmp;
        old?.Dispose();
        if (Bounds != area) Bounds = area;
        if (!Visible) Show();
        Invalidate();
    }

    public void HideScreen()
    {
        if (Visible) Hide();
        _frame?.Dispose();
        _frame = null;
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(Color.Black);
        var f = _frame;
        if (f == null) return;
        bool sideways = Rotation == 90 || Rotation == 270;
        float fw = sideways ? f.Height : f.Width, fh = sideways ? f.Width : f.Height;   // size after rotating
        float sx = ClientSize.Width / fw, sy = ClientSize.Height / fh;
        float s = Fill ? Math.Max(sx, sy) : Math.Min(sx, sy);
        g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBilinear;
        g.TranslateTransform(ClientSize.Width / 2f, ClientSize.Height / 2f);
        g.RotateTransform(Rotation);
        g.ScaleTransform(s, s);
        g.DrawImage(f, -f.Width / 2f, -f.Height / 2f, f.Width, f.Height);
        g.ResetTransform();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) _frame?.Dispose();
        base.Dispose(disposing);
    }
}
