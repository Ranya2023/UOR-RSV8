namespace Remco;

/// <summary>Small "starting…" window shown while UOR-RC loads (so a click never feels like nothing happened).</summary>
internal sealed class SplashForm : Form
{
    public SplashForm()
    {
        FormBorderStyle = FormBorderStyle.None;
        StartPosition = FormStartPosition.CenterScreen;
        ShowInTaskbar = false;
        TopMost = true;
        ClientSize = new Size(420, 170);
        BackColor = Color.FromArgb(0x0b, 0x0e, 0x17);
        Icon = MainForm.AppIcon;

        var logo = new PictureBox { Location = new Point(32, 44), Size = new Size(72, 72), SizeMode = PictureBoxSizeMode.Zoom };
        try { logo.Image = new Icon(MainForm.AppIcon, 128, 128).ToBitmap(); } catch { }
        var title = new Label
        {
            Text = "UOR-RC", Location = new Point(122, 48), AutoSize = true,
            Font = new Font("Segoe UI Semibold", 22f), ForeColor = Color.White
        };
        var sub = new Label
        {
            Text = "Starting…", Location = new Point(126, 92), AutoSize = true,
            Font = new Font("Segoe UI", 10f), ForeColor = Color.FromArgb(0x8d, 0x95, 0xad)
        };
        var bar = new ProgressBar
        {
            Style = ProgressBarStyle.Marquee, MarqueeAnimationSpeed = 25,
            Location = new Point(126, 120), Size = new Size(250, 6)
        };
        Controls.AddRange(new Control[] { logo, title, sub, bar });
        Paint += (_, e) =>
        {
            using var p = new Pen(Color.FromArgb(0x26, 0x2d, 0x45));
            e.Graphics.DrawRectangle(p, 0, 0, ClientSize.Width - 1, ClientSize.Height - 1);
            using var accent = new System.Drawing.Drawing2D.LinearGradientBrush(
                new Rectangle(0, 0, ClientSize.Width, 4), Color.FromArgb(0x4f, 0x7d, 0xff), Color.FromArgb(0x8b, 0x5c, 0xf6), 0f);
            e.Graphics.FillRectangle(accent, 0, 0, ClientSize.Width, 4);
        };
    }

    protected override bool ShowWithoutActivation => true;
}
