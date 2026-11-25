using Docnet.Core;
using Docnet.Core.Models;
using LedgerReview.Vision;
using SkiaSharp;
using UglyToad.PdfPig;
using UglyToad.PdfPig.Writer;

namespace LedgerReview.Pdf;

public sealed class PdfService
{
    public int PageCount(string path)
    {
        using var doc = PdfDocument.Open(path);
        return doc.NumberOfPages;
    }

    /// <summary>Render one page to PNG. Returns pixel width/height.</summary>
    public (int Width, int Height) RenderPage(string pdfPath, int pageIndex, int dpi, string outPath)
    {
        double scale = dpi / 72.0;
        using var docReader = DocLib.Instance.GetDocReader(pdfPath, new PageDimensions(scale));
        using var pageReader = docReader.GetPageReader(pageIndex);
        int w = pageReader.GetPageWidth();
        int h = pageReader.GetPageHeight();
        byte[] bgr = pageReader.GetImage();

        using var bmp = new SKBitmap(new SKImageInfo(w, h, SKColorType.Bgra8888, SKAlphaType.Opaque));
        int bpp = bgr.Length / Math.Max(1, w * h);
        if (bpp != 3 && bpp != 4) bpp = 3;
        int stride = bgr.Length / Math.Max(1, h);
        if (stride < w * bpp) stride = w * bpp;
        unsafe
        {
            byte* dst = (byte*)bmp.GetPixels().ToPointer();
            for (int y = 0; y < h; y++)
            {
                int row = y * stride;
                for (int x = 0; x < w; x++)
                {
                    int s = row + x * bpp;
                    int d = (y * w + x) * 4;
                    dst[d + 0] = bgr[s + 0];
                    dst[d + 1] = bgr[s + 1];
                    dst[d + 2] = bgr[s + 2];
                    dst[d + 3] = 255;
                }
            }
        }
        using (var img = SKImage.FromBitmap(bmp))
        using (var data = img.Encode(SKEncodedImageFormat.Png, 92))
        using (var fs = File.Create(outPath))
            data.SaveTo(fs);
        return (w, h);
    }

    public void SaveThumb(string pngPath, string thumbPath, int maxWidth = 260)
    {
        using var original = SKBitmap.Decode(pngPath);
        if (original == null) return;
        int w = original.Width, h = original.Height;
        double s = Math.Min(1.0, (double)maxWidth / w);
        int tw = Math.Max(1, (int)(w * s)), th = Math.Max(1, (int)(h * s));
        using var resized = ImageOps.Resize(original, tw, th);
        using var img = SKImage.FromBitmap(resized);
        using var data = img.Encode(SKEncodedImageFormat.Jpeg, 78);
        using var fs = File.Create(thumbPath);
        data.SaveTo(fs);
    }

    /// <summary>Copy a single page into a new PDF without recompressing the page content.</summary>
    public void ExtractPage(string pdfPath, int pageIndex, string outPath)
    {
        using var src = PdfDocument.Open(pdfPath);
        var builder = new PdfDocumentBuilder();
        builder.AddPage(src, pageIndex + 1);
        var bytes = builder.Build();
        File.WriteAllBytes(outPath, bytes);
    }
}

