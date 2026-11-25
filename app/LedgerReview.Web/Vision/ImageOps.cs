using LedgerReview.Domain;
using SkiaSharp;

namespace LedgerReview.Vision;

public static class ImageOps
{
    public static SKBitmap EnsureBgra(SKBitmap src)
    {
        if (src.ColorType == SKColorType.Bgra8888) return src;
        var dst = new SKBitmap(new SKImageInfo(src.Width, src.Height, SKColorType.Bgra8888, SKAlphaType.Opaque));
        using var canvas = new SKCanvas(dst);
        canvas.DrawBitmap(src, 0, 0);
        return dst;
    }

    public static unsafe byte[] ToGray(SKBitmap bmp)
    {
        int w = bmp.Width, h = bmp.Height;
        var gray = new byte[w * h];
        byte* p = (byte*)bmp.GetPixels().ToPointer();
        for (int i = 0; i < w * h; i++)
            gray[i] = (byte)((p[i * 4 + 2] * 77 + p[i * 4 + 1] * 150 + p[i * 4 + 0] * 29) >> 8);
        return gray;
    }

    public static int Otsu(byte[] gray, int x0, int y0, int rw, int rh, int stride)
    {
        var hist = new int[256];
        for (int y = y0; y < y0 + rh; y++)
            for (int x = x0; x < x0 + rw; x++) hist[gray[y * stride + x]]++;
        int total = rw * rh;
        double sum = 0;
        for (int i = 0; i < 256; i++) sum += i * hist[i];
        double sumB = 0, wB = 0, maxVar = -1;
        int threshold = 128;
        for (int t = 0; t < 256; t++)
        {
            wB += hist[t];
            if (wB == 0) continue;
            double wF = total - wB;
            if (wF == 0) break;
            sumB += t * hist[t];
            double mB = sumB / wB, mF = (sum - sumB) / wF;
            double var = wB * wF * (mB - mF) * (mB - mF);
            if (var > maxVar) { maxVar = var; threshold = t; }
        }
        return threshold;
    }

    public static (int X, int Y, int W, int H) ToPixels(Rect r, int width, int height)
    {
        int x = (int)Math.Round(r.X * width);
        int y = (int)Math.Round(r.Y * height);
        int w = (int)Math.Round(r.W * width);
        int h = (int)Math.Round(r.H * height);
        x = Math.Clamp(x, 0, Math.Max(0, width - 1));
        y = Math.Clamp(y, 0, Math.Max(0, height - 1));
        w = Math.Clamp(w, 1, width - x);
        h = Math.Clamp(h, 1, height - y);
        return (x, y, w, h);
    }

    /// <summary>Ink ratio inside a normalized rectangle, ignoring a margin to avoid table borders.</summary>
    public static double InkRatio(SKBitmap bmp, Rect normRect, double marginRatio = 0.14)
    {
        var gray = ToGray(bmp);
        var (x, y, w, h) = ToPixels(normRect, bmp.Width, bmp.Height);
        int mx = (int)(w * marginRatio), my = (int)(h * marginRatio);
        int x0 = x + mx, y0 = y + my, rw = Math.Max(3, w - 2 * mx), rh = Math.Max(3, h - 2 * my);
        if (x0 + rw > bmp.Width) rw = bmp.Width - x0;
        if (y0 + rh > bmp.Height) rh = bmp.Height - y0;

        int min = 255, max = 0;
        for (int yy = y0; yy < y0 + rh; yy++)
            for (int xx = x0; xx < x0 + rw; xx++)
            { int v = gray[yy * bmp.Width + xx]; if (v < min) min = v; if (v > max) max = v; }
        if (max - min < 38) return 0;

        int t = Otsu(gray, x0, y0, rw, rh, bmp.Width);
        t = Math.Min(t, max - 18);
        int ink = 0;
        for (int yy = y0; yy < y0 + rh; yy++)
            for (int xx = x0; xx < x0 + rw; xx++)
                if (gray[yy * bmp.Width + xx] < t) ink++;
        return (double)ink / (rw * rh);
    }

    public static double QualityScore(SKBitmap bmp)
    {
        var gray = ToGray(bmp);
        double mean = 0;
        for (int i = 0; i < gray.Length; i++) mean += gray[i];
        mean /= gray.Length;
        double var = 0;
        for (int i = 0; i < gray.Length; i++) { double d = gray[i] - mean; var += d * d; }
        double std = Math.Sqrt(var / gray.Length);
        return Math.Clamp(std / 62.0, 0, 1);
    }

    public static SKBitmap Crop(SKBitmap src, Rect normRect, double inset = 0)
    {
        var (x, y, w, h) = ToPixels(normRect, src.Width, src.Height);
        int ix = (int)(w * inset), iy = (int)(h * inset);
        x += ix; y += iy; w -= 2 * ix; h -= 2 * iy;
        w = Math.Max(1, w); h = Math.Max(1, h);
        var dst = new SKBitmap(new SKImageInfo(w, h, SKColorType.Bgra8888, SKAlphaType.Opaque));
        using var canvas = new SKCanvas(dst);
        canvas.DrawBitmap(src, new SKRect(x, y, x + w, y + h), new SKRect(0, 0, w, h));
        return dst;
    }

    public static void SavePng(SKBitmap bmp, string path)
    {
        using var img = SKImage.FromBitmap(bmp);
        using var data = img.Encode(SKEncodedImageFormat.Png, 90);
        using var fs = File.Create(path);
        data.SaveTo(fs);
    }

    /// <summary>Normalized ink bounding box inside a crop; returns null when too little ink.</summary>
    public static SKRectI? InkBounds(SKBitmap bmp, out byte[] gray)
    {
        gray = ToGray(bmp);
        return InkBounds(gray, bmp.Width, bmp.Height);
    }

    public static SKRectI? InkBounds(byte[] gray, int w, int h)
    {
        int t = Otsu(gray, 0, 0, w, h, w);
        int minX = w, minY = h, maxX = -1, maxY = -1, count = 0;
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
                if (gray[y * w + x] < t)
                {
                    count++;
                    if (x < minX) minX = x;
                    if (y < minY) minY = y;
                    if (x > maxX) maxX = x;
                    if (y > maxY) maxY = y;
                }
        if (count < Math.Max(24, w * h / 400) || maxX < minX || maxY < minY) return null;
        return new SKRectI(minX, minY, maxX + 1, maxY + 1);
    }

    /// <summary>Signature descriptor: ink grid + gradient orientation + projections.</summary>
    public static float[]? SignatureFeature(SKBitmap crop)
    {
        var bounds = InkBounds(crop, out var gray);
        if (bounds == null) return null;
        // Remove residual table borders near the crop edges.
        int cw = crop.Width, ch = crop.Height;
        for (int y = 0; y < ch; y++)
            for (int x = 0; x < cw; x++)
                if (x < 2 || y < 2 || x >= cw - 2 || y >= ch - 2) gray[y * cw + x] = 255;
        bounds = InkBounds(gray, cw, ch);
        if (bounds == null) return null;
        var b = bounds.Value;
        const int NW = 48, NH = 24;
        var bin = new byte[NW * NH];
        for (int y = 0; y < NH; y++)
        {
            int sy = b.Top + (int)((long)y * b.Height / NH);
            for (int x = 0; x < NW; x++)
            {
                int sx = b.Left + (int)((long)x * b.Width / NW);
                bin[y * NW + x] = gray[sy * crop.Width + sx];
            }
        }
        // local threshold for binary
        int t = Otsu(bin, 0, 0, NW, NH, NW);
        var mask = new byte[NW * NH];
        for (int i = 0; i < mask.Length; i++) mask[i] = bin[i] < t ? (byte)1 : (byte)0;

        var feat = new List<float>(340);
        // 8x4 density
        for (int gy = 0; gy < 4; gy++)
            for (int gx = 0; gx < 8; gx++)
            {
                int c = 0;
                for (int y = gy * 6; y < gy * 6 + 6; y++)
                    for (int x = gx * 6; x < gx * 6 + 6; x++) c += mask[y * NW + x];
                feat.Add(c / 36f);
            }
        // gradient orientation histograms per 8x4 cell
        for (int gy = 0; gy < 4; gy++)
            for (int gx = 0; gx < 8; gx++)
            {
                var hist = new float[8];
                for (int y = Math.Max(1, gy * 6); y < Math.Min(NH - 1, gy * 6 + 6); y++)
                    for (int x = Math.Max(1, gx * 6); x < Math.Min(NW - 1, gx * 6 + 6); x++)
                    {
                        int gxv = bin[y * NW + x + 1] - bin[y * NW + x - 1];
                        int gyv = bin[(y + 1) * NW + x] - bin[(y - 1) * NW + x];
                        if (Math.Abs(gxv) + Math.Abs(gyv) < 40) continue;
                        double ang = Math.Atan2(gyv, gxv) + Math.PI;
                        int bi = (int)(ang / (2 * Math.PI) * 8) % 8;
                        hist[bi] += (float)Math.Sqrt(gxv * gxv + gyv * gyv) / 255f;
                    }
                float s = hist.Sum() + 1e-6f;
                for (int i = 0; i < 8; i++) feat.Add(hist[i] / s);
            }
        // projections
        for (int i = 0; i < 16; i++)
        {
            int c = 0;
            for (int y = 0; y < NH; y++)
                for (int x = i * 3; x < (i + 1) * 3; x++) c += mask[y * NW + x];
            feat.Add(c / (float)(NH * 3));
        }
        for (int i = 0; i < 8; i++)
        {
            int c = 0;
            for (int y = i * 3; y < (i + 1) * 3; y++)
                for (int x = 0; x < NW; x++) c += mask[y * NW + x];
            feat.Add(c / (float)(NW * 3));
        }
        feat.Add((float)b.Width / b.Height / 4f);
        feat.Add(mask.Sum(b => (int)b) / (float)mask.Length);

        double norm = Math.Sqrt(feat.Sum(v => v * v));
        if (norm < 1e-6) return null;
        return feat.Select(v => (float)(v / norm)).ToArray();
    }

    public static double Cosine(float[] a, float[] b)
    {
        int n = Math.Min(a.Length, b.Length);
        double s = 0;
        for (int i = 0; i < n; i++) s += a[i] * b[i];
        return s;
    }

    public static double Ncc(byte[] a, byte[] b)
    {
        int n = Math.Min(a.Length, b.Length);
        double ma = 0, mb = 0;
        for (int i = 0; i < n; i++) { ma += a[i]; mb += b[i]; }
        ma /= n; mb /= n;
        double num = 0, da = 0, db = 0;
        for (int i = 0; i < n; i++)
        {
            double x = a[i] - ma, y = b[i] - mb;
            num += x * y; da += x * x; db += y * y;
        }
        return da < 1e-6 || db < 1e-6 ? 0 : num / Math.Sqrt(da * db);
    }

    public static SKBitmap Resize(SKBitmap src, int w, int h)
    {
        w = Math.Max(1, w); h = Math.Max(1, h);
        return src.Resize(new SKImageInfo(w, h, SKColorType.Bgra8888, SKAlphaType.Opaque),
            new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.Linear));
    }

    public static byte[] ThumbGray(SKBitmap bmp, int w = 96, int h = 136)
    {
        using var resized = Resize(bmp, w, h);
        return ToGray(resized);
    }
}

