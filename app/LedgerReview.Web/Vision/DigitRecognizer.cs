using LedgerReview.Infrastructure;
using SkiaSharp;

namespace LedgerReview.Vision;

/// <summary>
/// 手写数字识别：字符切分 + 归一化 + 近邻模板匹配（纯 C#，离线）。
/// 模板来自三处：程序内合成字体模板、Assets/digits/{0-9}/、工作区 digits/{0-9}/（人工确认过的切片）。
/// </summary>
public sealed class DigitRecognizer
{
    private const int Size = 16;
    private readonly List<(int Label, float[] Feature)> _templates = new();
    private readonly ILogger<DigitRecognizer> _log;

    public DigitRecognizer(Workspace ws, ILogger<DigitRecognizer> log)
    {
        _log = log;
        LoadFolder(Path.Combine(ws.AssetsRoot, "digits"));
        LoadFolder(Path.Combine(ws.Root, "digits"));
        if (_templates.Count == 0) BuildSynthetic();
        _log.LogInformation("数字模板：{n} 个", _templates.Count);
    }

    public int TemplateCount => _templates.Count;
    public bool Ready => _templates.Count >= 10;

    private void LoadFolder(string dir)
    {
        if (!Directory.Exists(dir)) return;
        foreach (var sub in Directory.GetDirectories(dir))
        {
            if (!int.TryParse(Path.GetFileName(sub), out var label) || label is < 0 or > 9) continue;
            foreach (var f in Directory.GetFiles(sub, "*.png"))
            {
                try
                {
                    using var bmp = SKBitmap.Decode(f);
                    var feat = Feature(bmp);
                    if (feat != null) _templates.Add((label, feat));
                }
                catch { }
            }
        }
    }

    private void BuildSynthetic()
    {
        string[] families =
        {
            "Arial", "Times New Roman", "KaiTi", "SimSun", "Microsoft YaHei",
            "Consolas", "Segoe Script", "Comic Sans MS", "Ink Free", "Calibri"
        };
        float[] skews = { 0f, -0.12f, 0.12f };
        foreach (var fam in families)
        {
            var tf = SKTypeface.FromFamilyName(fam);
            if (tf == null) continue;
            foreach (var skew in skews)
                for (int d = 0; d <= 9; d++)
                {
                    using var bmp = Render(d.ToString(), tf, skew);
                    var feat = Feature(bmp);
                    if (feat != null) _templates.Add((d, feat));
                }
            tf.Dispose();
        }
    }

    private static SKBitmap Render(string text, SKTypeface tf, float skew)
    {
        var bmp = new SKBitmap(new SKImageInfo(64, 64, SKColorType.Bgra8888, SKAlphaType.Opaque));
        using var canvas = new SKCanvas(bmp);
        canvas.Clear(SKColors.White);
        using var paint = new SKPaint
        {
            Color = SKColors.Black,
            IsAntialias = true,
            TextSize = 50,
            Typeface = tf,
            TextAlign = SKTextAlign.Center,
            SubpixelText = true
        };
        canvas.Translate(32, 32);
        canvas.Skew(skew, 0);
        var fm = paint.FontMetrics;
        float y = -(fm.Ascent + fm.Descent) / 2;
        canvas.DrawText(text, 0, y, paint);
        return bmp;
    }

    public sealed record DigitResult(int Value, double Confidence, string Detail);

    public DigitResult? Recognize(SKBitmap crop) => RecognizeBest(crop, 0.52, 0.03);

    /// <summary>尽力识别，返回首个候选与置信度；调用方自行决定是否采信。</summary>
    public DigitResult? RecognizeBest(SKBitmap crop, double minScore = 0, double minMargin = 0)
    {
        if (!Ready || crop.Width < 6 || crop.Height < 6) return null;
        var glyphs = Segment(crop);
        if (glyphs.Count is < 1 or > 3) return null;

        int value = 0;
        double conf = 1;
        var detail = new List<string>();
        bool ok = true;
        foreach (var g in glyphs)
        {
            var feat = Feature(g);
            g.Dispose();
            if (feat == null) return null;
            var (label, score, margin) = Classify(feat);
            if (label < 0) return null;
            if (score < minScore || margin < minMargin) ok = false;
            value = value * 10 + label;
            conf = Math.Min(conf, score);
            detail.Add($"{label}:{score:0.00}");
        }
        if (value is < 1 or > 300) return null;
        var res = new DigitResult(value, conf, string.Join(" ", detail));
        return ok ? res : res with { Confidence = Math.Min(conf, 0.49) };
    }

    /// <summary>人工确认后的切片入库：按位拆字，保存到工作区 digits/{数字}/，立即参与后续识别。</summary>
    public int LearnFrom(SKBitmap crop, int value, Workspace ws)
    {
        var digits = value.ToString().ToCharArray().Select(c => c - '0').ToArray();
        var glyphs = Segment(crop);
        if (glyphs.Count != digits.Length) { foreach (var g in glyphs) g.Dispose(); return 0; }
        int saved = 0;
        for (int i = 0; i < glyphs.Count; i++)
        {
            try
            {
                var feat = Feature(glyphs[i]);
                if (feat == null) continue;
                var dir = Path.Combine(ws.Root, "digits", digits[i].ToString());
                Directory.CreateDirectory(dir);
                var path = Path.Combine(dir, $"h_{DateTime.Now:yyyyMMddHHmmssfff}_{i}.png");
                ImageOps.SavePng(glyphs[i], path);
                _templates.Add((digits[i], feat));
                saved++;
            }
            finally { glyphs[i].Dispose(); }
        }
        return saved;
    }

    private (int Label, double Score, double Margin) Classify(float[] feat)
    {
        var best = new Dictionary<int, double>();
        foreach (var (label, t) in _templates)
        {
            double sim = Cosine(feat, t);
            if (!best.TryGetValue(label, out var cur) || sim > cur) best[label] = sim;
        }
        var ranked = best.OrderByDescending(x => x.Value).ToList();
        if (ranked.Count == 0) return (-1, 0, 0);
        int lbl = ranked[0].Key;
        double s1 = ranked[0].Value;
        double s2 = ranked.Count > 1 ? ranked[1].Value : 0;
        return (lbl, s1, s1 - s2);
    }

    private static double Cosine(float[] a, float[] b)
    {
        double dot = 0, na = 0, nb = 0;
        for (int i = 0; i < a.Length; i++) { dot += a[i] * b[i]; na += a[i] * a[i]; nb += b[i] * b[i]; }
        double d = Math.Sqrt(na) * Math.Sqrt(nb);
        return d < 1e-6 ? 0 : dot / d;
    }

    private static float[]? Feature(SKBitmap src)
    {
        var gray = ImageOps.ToGray(ImageOps.EnsureBgra(src));
        int w = src.Width, h = src.Height;
        if (w < 3 || h < 3) return null;
        int t = ImageOps.Otsu(gray, 0, 0, w, h, w);
        int x0 = w, y0 = h, x1 = -1, y1 = -1;
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
                if (gray[y * w + x] < t)
                {
                    if (x < x0) x0 = x; if (x > x1) x1 = x;
                    if (y < y0) y0 = y; if (y > y1) y1 = y;
                }
        if (x1 < x0 || y1 < y0) return null;

        var feat = new float[Size * Size];
        double sum = 0;
        for (int gy = 0; gy < Size; gy++)
            for (int gx = 0; gx < Size; gx++)
            {
                int sx0 = x0 + (x1 - x0 + 1) * gx / Size, sx1 = x0 + (x1 - x0 + 1) * (gx + 1) / Size;
                int sy0 = y0 + (y1 - y0 + 1) * gy / Size, sy1 = y0 + (y1 - y0 + 1) * (gy + 1) / Size;
                int ink = 0, total = 0;
                for (int y = sy0; y < Math.Max(sy0 + 1, sy1); y++)
                    for (int x = sx0; x < Math.Max(sx0 + 1, sx1); x++)
                    {
                        if (x >= w || y >= h) continue;
                        total++;
                        if (gray[y * w + x] < t) ink++;
                    }
                float v = total == 0 ? 0 : (float)ink / total;
                feat[gy * Size + gx] = v;
                sum += v;
            }
        if (sum < 1e-4) return null;
        for (int i = 0; i < feat.Length; i++) feat[i] = (float)(feat[i] / sum);
        return feat;
    }

    /// <summary>按列投影切字；粘连时在中部墨迹最少处切开。</summary>
    private static List<SKBitmap> Segment(SKBitmap crop)
    {
        var result = new List<SKBitmap>();
        var bgra = ImageOps.EnsureBgra(crop);
        var gray = ImageOps.ToGray(bgra);
        int w = bgra.Width, h = bgra.Height;
        int t = ImageOps.Otsu(gray, 0, 0, w, h, w);

        var cols = new int[w];
        for (int x = 0; x < w; x++)
            for (int y = 0; y < h; y++)
                if (gray[y * w + x] < t) cols[x]++;

        var runs = new List<(int A, int B)>();
        int start = -1;
        for (int x = 0; x < w; x++)
        {
            if (cols[x] > 0) { if (start < 0) start = x; }
            else if (start >= 0) { runs.Add((start, x)); start = -1; }
        }
        if (start >= 0) runs.Add((start, w));
        if (runs.Count == 0) return result;

        int minGap = Math.Max(2, w / 40);
        var merged = new List<(int A, int B)>();
        foreach (var r in runs)
        {
            if (merged.Count > 0 && r.A - merged[^1].B <= minGap) merged[^1] = (merged[^1].A, r.B);
            else merged.Add(r);
        }

        if (merged.Count == 1 && merged[0].B - merged[0].A > h * 0.55)
        {
            int a = merged[0].A, b = merged[0].B;
            int lo = a + (b - a) * 2 / 5, hi = a + (b - a) * 3 / 5;
            int cut = (lo + hi) / 2;
            for (int x = lo; x <= hi; x++) if (cols[x] < cols[cut]) cut = x;
            merged = new List<(int, int)> { (a, cut), (cut, b) };
        }

        foreach (var r in merged.Take(3))
        {
            int x0 = Math.Max(0, r.A - 1), x1 = Math.Min(w, r.B + 1);
            var bmp = new SKBitmap(new SKImageInfo(x1 - x0, h, SKColorType.Bgra8888, SKAlphaType.Opaque));
            using var canvas = new SKCanvas(bmp);
            canvas.Clear(SKColors.White);
            canvas.DrawBitmap(bgra, new SKRectI(x0, 0, x1, h), new SKRect(0, 0, x1 - x0, h));
            result.Add(bmp);
        }
        return result;
    }
}
