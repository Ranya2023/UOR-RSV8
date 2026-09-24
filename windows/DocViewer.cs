using Windows.Data.Pdf;
using Windows.Storage;
using Windows.Storage.Streams;

namespace Remco;

/// <summary>
/// 📑 Presents PDF and Word files full-screen, page by page, like slides
/// (NEXT / ◀, laser, pen, zoom… all work). Word files are turned into PDF
/// with the Word already installed on the PC. Uses Windows' built-in PDF engine.
/// </summary>
internal sealed class DocViewer : FullScreenView
{
    private PdfDocument? _pdf;
    private Bitmap? _page;
    private readonly Dictionary<string, string> _thumbs = new();
    public string DocName { get; private set; } = "";
    public int Page { get; private set; }
    public int Pages { get; private set; }
    public string Key => $"doc:{DocName}:{Page}";
    public event Action? Changed;

    public DocViewer() { Text = "UOR-RC – document"; }

    public async Task<bool> LoadAsync(string pdfPath, string name)
    {
        var file = await StorageFile.GetFileFromPathAsync(Path.GetFullPath(pdfPath));
        _pdf = await PdfDocument.LoadFromFileAsync(file);
        Pages = (int)_pdf.PageCount;
        DocName = name;
        _thumbs.Clear();
        Page = 0;
        return Pages > 0;
    }

    public async Task GoTo(int p)
    {
        if (_pdf == null || p < 1 || p > Pages) return;
        Page = p;
        var bmp = await Render(p, Math.Max(800, ClientSize.Width));
        var old = _page; _page = bmp; old?.Dispose();
        Invalidate();
        Changed?.Invoke();
    }

    public Task Next() => GoTo(Page + 1);
    public Task Prev() => GoTo(Page - 1);

    private async Task<Bitmap?> Render(int p, int width)
    {
        if (_pdf == null) return null;
        using var page = _pdf.GetPage((uint)(p - 1));
        using var stream = new InMemoryRandomAccessStream();
        await page.RenderToStreamAsync(stream, new PdfPageRenderOptions { DestinationWidth = (uint)width });
        stream.Seek(0);
        using var s = stream.AsStream();
        using var img = Image.FromStream(s);
        return new Bitmap(img);
    }

    /// <summary>Small JPEG of a page for the phone (current / next preview, touchpad picture).</summary>
    public async Task<string> Thumb(int p, int width)
    {
        string k = p + "@" + width;
        if (_thumbs.TryGetValue(k, out var t)) return t;
        using var bmp = await Render(p, width);
        if (bmp == null) return "";
        using var ms = new MemoryStream();
        bmp.Save(ms, System.Drawing.Imaging.ImageFormat.Jpeg);
        return _thumbs[k] = Convert.ToBase64String(ms.ToArray());
    }

    public void CloseDoc()
    {
        Hide();
        _page?.Dispose(); _page = null;
        _pdf = null; Pages = 0; Page = 0;
        _thumbs.Clear();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(Color.Black);
        var f = _page;
        if (f == null) return;
        float s = Math.Min((float)ClientSize.Width / f.Width, (float)ClientSize.Height / f.Height);
        float w = f.Width * s, h = f.Height * s;
        g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
        g.DrawImage(f, (ClientSize.Width - w) / 2, (ClientSize.Height - h) / 2, w, h);
    }

    // ── Word → PDF (uses Microsoft Word on this PC) ────────────────────
    public static Task<string?> WordToPdf(string path, Action<string> log) =>
        RunSta(() =>
        {
            var t = Type.GetTypeFromProgID("Word.Application");
            if (t == null) { log("Microsoft Word isn't installed, so UOR-RC can't show this file itself."); return null; }
            dynamic? word = null;
            try
            {
                word = Activator.CreateInstance(t)!;
                word.Visible = false;
                word.DisplayAlerts = 0;
                dynamic doc = word.Documents.Open(Path.GetFullPath(path), false, true);   // ConfirmConversions=false, ReadOnly=true
                string pdf = Path.Combine(Path.GetTempPath(), "UOR-RC-docs", Path.GetFileNameWithoutExtension(path) + ".pdf");
                Directory.CreateDirectory(Path.GetDirectoryName(pdf)!);
                doc.ExportAsFixedFormat(pdf, 17);   // wdExportFormatPDF
                doc.Close(false);
                return pdf;
            }
            catch (Exception ex) { log("Word could not convert the file: " + ex.Message); return null; }
            finally
            {
                try { word?.Quit(false); } catch { }
                if (word != null) try { System.Runtime.InteropServices.Marshal.FinalReleaseComObject((object)word); } catch { }
            }
        });

    private static Task<T> RunSta<T>(Func<T> f)
    {
        var tcs = new TaskCompletionSource<T>();
        var th = new Thread(() => { try { tcs.SetResult(f()); } catch (Exception ex) { tcs.SetException(ex); } }) { IsBackground = true };
        th.SetApartmentState(ApartmentState.STA);
        th.Start();
        return tcs.Task;
    }

    protected override void Dispose(bool disposing) { if (disposing) _page?.Dispose(); base.Dispose(disposing); }
}
