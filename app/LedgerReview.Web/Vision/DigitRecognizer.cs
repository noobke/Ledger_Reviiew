using LedgerReview.Infrastructure;
using SkiaSharp;

namespace LedgerReview.Vision;

/// <summary>
/// 手写数字识别：字符切分 + 归一化 + 近邻模板匹配（纯 C#，离线）。
/// 模板来源：程序内合成字体模板、Assets/digits/{0-9}/、工作区 digits/{0-9}/（通用），
/// 以及按批次保存的 digits/{batchId}/{0-9}/（人工确认过的切片，只在同一批次内使用，
/// 避免把不同写字人的模板混在一起互相干扰）。
/// </summary>
public sealed class DigitRecognizer
{
    private const int Size = 16;
    private readonly List<(int Label, float[] Feature)> _templates = new();
    private readonly Dictionary<string, List<(int Label, float[] Feature)>> _batch = new();
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

    /// <summary>调试用：返回切分出的字符数与各字符尺寸。</summary>
    public static string DescribeGlyphs(SKBitmap crop)
    {
        var list = Segment(crop);
        var txt = string.Join(",", list.Select(g => $"{g.Width}x{g.Height}"));
        foreach (var g in list) g.Dispose();
        return $"{list.Count}:{txt}";
    }

    public DigitResult? Recognize(SKBitmap crop) => RecognizeBest(crop, 0.52, 0.03);

    /// <summary>懒加载某个批次人工确认过的模板。</summary>
    public void EnsureBatch(Workspace ws, string batchId)
    {
        if (string.IsNullOrWhiteSpace(batchId) || _batch.ContainsKey(batchId)) return;
        var list = new List<(int Label, float[] Feature)>();
        var dir = Path.Combine(ws.Root, "digits", batchId);
        if (Directory.Exists(dir))
        {
            foreach (var sub in Directory.GetDirectories(dir))
            {
                if (!int.TryParse(Path.GetFileName(sub), out var label) || label is < 0 or > 9) continue;
                foreach (var f in Directory.GetFiles(sub, "*.png"))
                {
                    try
                    {
                        using var bmp = SKBitmap.Decode(f);
                        var feat = Feature(bmp);
                        if (feat != null) list.Add((label, feat));
                    }
                    catch { }
                }
            }
        }
        _batch[batchId] = list;
    }

    /// <summary>尽力识别，返回首个候选与置信度；调用方自行决定是否采信。</summary>
    public DigitResult? RecognizeBest(SKBitmap crop, double minScore = 0, double minMargin = 0, string? batchId = null)
    {
        if (!Ready || crop.Width < 6 || crop.Height < 6) return null;
        List<(int Label, float[] Feature)> pool = _templates;
        if (batchId != null && _batch.TryGetValue(batchId, out var bt) && bt.Count > 0)
        {
            pool = new List<(int, float[])>(_templates);
            pool.AddRange(bt);
        }
        var glyphs = Segment(crop);
        if (glyphs.Count is < 1 or > 3) { foreach (var g in glyphs) g.Dispose(); return null; }

        int value = 0;
        double conf = 1;
        var detail = new List<string>();
        bool ok = true;
        foreach (var g in glyphs)
        {
            var feat = Feature(g);
            if (feat == null) { foreach (var x in glyphs) x.Dispose(); return null; }
            var (label, score, margin) = Classify(feat, pool);
            if (label < 0) { foreach (var x in glyphs) x.Dispose(); return null; }
            if (score < minScore || margin < minMargin) ok = false;
            value = value * 10 + label;
            conf = Math.Min(conf, score);
            detail.Add($"{label}:{score:0.00}");
        }

        // 有时候一个数字被投影切成两段（如“9”的圈和尾），合并后再匹配一次；
        // 只有合并假设的置信度明显更高时才采信，避免把真正的两位数合成一个。
        if (glyphs.Count == 2)
        {
            try
            {
                using var merged = MergeGlyphs(glyphs[0], glyphs[1]);
                var mf = Feature(merged);
                if (mf != null)
                {
                    var (ml, ms, mm) = Classify(mf, pool);
                    if (ml >= 0 && ms > conf + 0.10 && ms >= 0.55)
                    {
                        value = ml;
                        conf = ms;
                        detail = new List<string> { $"{ml}:{ms:0.00}(合并)" };
                        ok = ms >= minScore && mm >= minMargin;
                    }
                }
            }
            catch { }
        }
        foreach (var g in glyphs) g.Dispose();

        if (value is < 1 or > 300) return null;
        var res = new DigitResult(value, conf, string.Join(" ", detail));
        return ok ? res : res with { Confidence = Math.Min(conf, 0.49) };
    }

    private static SKBitmap MergeGlyphs(SKBitmap a, SKBitmap b)
    {
        int gap = 2, w = a.Width + b.Width + gap, h = Math.Max(a.Height, b.Height);
        var bmp = new SKBitmap(new SKImageInfo(w, h, SKColorType.Bgra8888, SKAlphaType.Opaque));
        using var c = new SKCanvas(bmp);
        c.Clear(SKColors.White);
        c.DrawBitmap(a, 0, h - a.Height);
        c.DrawBitmap(b, a.Width + gap, h - b.Height);
        return bmp;
    }

    /// <summary>人工确认后的切片入库：按位拆字，保存到工作区 digits/{批次}/{数字}/，只在本批次内参与识别。</summary>
    public int LearnFrom(SKBitmap crop, int value, Workspace ws, string? batchId = null)
    {
        var digits = value.ToString().ToCharArray().Select(c => c - '0').ToArray();
        var glyphs = Segment(crop);
        // 切分数和位数不一致时做一次补救：多切了只留最宽的几个（去掉边缘残笔），
        // 少切了把最宽的一块按最薄处竖切。补救不成才放弃入库。
        if (glyphs.Count != digits.Length)
        {
            if (glyphs.Count > digits.Length)
            {
                var keep = glyphs.OrderByDescending(g => g.Width).Take(digits.Length).ToList();
                foreach (var g in glyphs.Except(keep)) g.Dispose();
                glyphs = keep;
            }
            while (glyphs.Count > 0 && glyphs.Count < digits.Length)
            {
                var idx = glyphs.IndexOf(glyphs.OrderByDescending(g => g.Width).First());
                var parts = SplitVertical(glyphs[idx]);
                if (parts.Count < 2) break;
                glyphs[idx].Dispose();
                glyphs.RemoveAt(idx);
                glyphs.AddRange(parts);
            }
        }
        if (glyphs.Count != digits.Length) { foreach (var g in glyphs) g.Dispose(); return 0; }
        string root = string.IsNullOrWhiteSpace(batchId)
            ? Path.Combine(ws.Root, "digits")
            : Path.Combine(ws.Root, "digits", batchId);
        if (!string.IsNullOrWhiteSpace(batchId)) EnsureBatch(ws, batchId);
        var bucket = string.IsNullOrWhiteSpace(batchId) ? _templates : _batch[batchId];
        int saved = 0;
        for (int i = 0; i < glyphs.Count; i++)
        {
            try
            {
                var feat = Feature(glyphs[i]);
                if (feat == null) continue;
                var dir = Path.Combine(root, digits[i].ToString());
                Directory.CreateDirectory(dir);
                var path = Path.Combine(dir, $"h_{DateTime.Now:yyyyMMddHHmmssfff}_{i}.png");
                ImageOps.SavePng(glyphs[i], path);
                bucket.Add((digits[i], feat));
                saved++;
            }
            finally { glyphs[i].Dispose(); }
        }
        return saved;
    }

    /// <summary>把一块粘连的数字图按中部墨迹最少的列竖切成两半。</summary>
    private static List<SKBitmap> SplitVertical(SKBitmap bmp)
    {
        var gray = ImageOps.ToGray(ImageOps.EnsureBgra(bmp));
        int w = bmp.Width, h = bmp.Height;
        if (w < 12) return new List<SKBitmap>();
        int t = ImageOps.Otsu(gray, 0, 0, w, h, w);
        var cols = new int[w];
        for (int x = 0; x < w; x++)
            for (int y = 0; y < h; y++)
                if (gray[y * w + x] < t) cols[x]++;
        int lo = w * 30 / 100, hi = w * 70 / 100, cut = lo, mn = int.MaxValue;
        for (int x = lo; x <= hi; x++) if (cols[x] < mn) { mn = cols[x]; cut = x; }
        if (cut <= 2 || cut >= w - 2) return new List<SKBitmap>();
        var a = new SKBitmap(new SKImageInfo(cut, h, SKColorType.Bgra8888, SKAlphaType.Opaque));
        var b = new SKBitmap(new SKImageInfo(w - cut, h, SKColorType.Bgra8888, SKAlphaType.Opaque));
        using (var ca = new SKCanvas(a)) { ca.Clear(SKColors.White); ca.DrawBitmap(bmp, new SKRectI(0, 0, cut, h), new SKRect(0, 0, cut, h)); }
        using (var cb = new SKCanvas(b)) { cb.Clear(SKColors.White); cb.DrawBitmap(bmp, new SKRectI(cut, 0, w, h), new SKRect(0, 0, w - cut, h)); }
        return new List<SKBitmap> { a, b };
    }

    private static (int Label, double Score, double Margin) Classify(float[] feat, List<(int Label, float[] Feature)> pool)
    {
        var best = new Dictionary<int, double>();
        foreach (var (label, t) in pool)
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

        int minGap = Math.Max(3, w / 30);
        var merged = new List<(int A, int B)>();
        foreach (var r in runs)
        {
            if (merged.Count > 0 && r.A - merged[^1].B <= minGap) merged[^1] = (merged[^1].A, r.B);
            else merged.Add(r);
        }

        // 用墨迹内容高度判断“两个数字粘成一整块”：单字宽高比约 0.5，两位并写会明显更宽。
        int top = -1, bot = -1;
        for (int y = 0; y < h; y++)
        {
            bool any = false;
            for (int x = 0; x < w; x++) if (gray[y * w + x] < t) { any = true; break; }
            if (any) { if (top < 0) top = y; bot = y + 1; }
        }
        int ch = Math.Max(1, bot - top);

        if (merged.Count == 1 && merged[0].B - merged[0].A > ch * 0.40)
        {
            int a = merged[0].A, b = merged[0].B;
            int lo = a + (b - a) * 30 / 100, hi = a + (b - a) * 70 / 100;
            int cut = lo, mn = int.MaxValue, peak = 0;
            for (int x = lo; x <= hi; x++)
            {
                if (cols[x] > peak) peak = cols[x];
                if (cols[x] < mn) { mn = cols[x]; cut = x; }
            }
            // 够宽且中间有明显“低谷”（两个数字粘连）才切；单个宽数字（8/0）没有这种低谷
            bool deepValley = peak > 0 && mn <= peak * 0.25;
            bool veryWide = b - a > ch * 0.50;
            if ((veryWide || deepValley) && cut > a + 2 && cut < b - 2)
                merged = new List<(int, int)> { (a, cut), (cut, b) };
        }

        // 切片左右边缘常残留印刷“第/次”的细笔画：贴边且明显偏窄的段直接剔除
        if (merged.Count > 1)
        {
            int edge = Math.Max(4, (int)(w * 0.10));
            merged = merged.Where(r =>
            {
                int wd = r.B - r.A;
                bool atEdge = r.A <= 1 || r.B >= w - 1;
                return !(atEdge && wd < edge);
            }).ToList();
            if (merged.Count == 0) return result;
        }

        // 其次按中位宽度再筛一遍，去掉更细的噪点
        if (merged.Count > 1)
        {
            var widths = merged.Select(r => r.B - r.A).OrderBy(v => v).ToList();
            int minW = Math.Max(3, widths[widths.Count / 2] / 4);
            merged = merged.Where(r => r.B - r.A >= minW).ToList();
            if (merged.Count == 0) return result;
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
