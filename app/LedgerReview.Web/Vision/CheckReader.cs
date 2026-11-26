using System.Text.RegularExpressions;
using LedgerReview.Domain;
using SkiaSharp;

namespace LedgerReview.Vision;

public sealed class CheckReader
{
    public List<CheckFact> Read(SKBitmap page, FormTemplate t, OcrPage? ocr = null)
    {
        var result = new List<CheckFact>();
        if (t.ItemCount <= 0) return result;

        if (t.TwoColumns)
        {
            var leftBands = DetectBands(page, 0.05, 0.47, t.TableTop, t.TableBottom, t.RowsLeft);
            var rightBands = DetectBands(page, 0.52, 0.97, t.TableTop, t.TableBottom, t.RowsRight);
            for (int i = 0; i < t.ItemCount; i++)
            {
                bool left = i < t.RowsLeft;
                int row = left ? i : i - t.RowsLeft;
                var bands = left ? leftBands : rightBands;
                double x = left ? t.LeftResultX : t.RightResultX;
                var (y0, y1) = Band(bands, row, left ? t.RowsLeft : t.RowsRight, t.TableTop, t.TableBottom);
                var rect = new Rect(x, y0 + 0.001, t.ResultWidth, Math.Max(0.006, y1 - y0 - 0.002));
                var cf = Add(result, page, ocr, i + 1, rect, t.NaItems.Contains(i + 1));
                // 每列最后一行常因表格底线贴着而把行带压薄，勾选被判空白；向上补 1.2% 再判一次。
                if (cf.Result == "BLANK" && row == (left ? t.RowsLeft : t.RowsRight) - 1)
                {
                    result.RemoveAt(result.Count - 1);
                    Add(result, page, ocr, i + 1, new Rect(rect.X, Math.Max(0, rect.Y - 0.012), rect.W, rect.H + 0.012), t.NaItems.Contains(i + 1));
                }
            }
        }
        else
        {
            var bands = DetectBands(page, 0.08, 0.95, t.TableTop, t.TableBottom, t.RowsLeft);
            for (int i = 0; i < t.ItemCount; i++)
            {
                var (y0, y1) = Band(bands, i, t.RowsLeft, t.TableTop, t.TableBottom);
                var rect = new Rect(t.LeftResultX, y0 + 0.001, t.ResultWidth, Math.Max(0.006, y1 - y0 - 0.002));
                var cf = Add(result, page, ocr, i + 1, rect, t.NaItems.Contains(i + 1));
                if (cf.Result == "BLANK" && i == t.ItemCount - 1)
                {
                    result.RemoveAt(result.Count - 1);
                    Add(result, page, ocr, i + 1, new Rect(rect.X, Math.Max(0, rect.Y - 0.012), rect.W, rect.H + 0.012), t.NaItems.Contains(i + 1));
                }
            }
        }
        return result;
    }

    private static CheckFact Add(List<CheckFact> list, SKBitmap page, OcrPage? ocr, int itemNo, Rect rect, bool naExpected)
    {
        // 行带检测偶发把某行压得很薄，勾选会被判成空白；小于 1.4% 页高时上下各放 0.5% 再判断。
        if (rect.H < 0.014)
            rect = new Rect(rect.X, Math.Max(0, rect.Y - 0.005), rect.W, rect.H + 0.010);
        bool na = IsNa(ocr, rect);
        double ink = ImageOps.InkRatio(page, rect, 0.12);
        // 勾选常压在列线/格线上，首轮没墨迹时向右下放宽一点再判一次，避免把已勾选判成空白。
        if (!na && ink <= 0.007)
        {
            var wider = new Rect(rect.X, rect.Y, Math.Min(1 - rect.X, rect.W + 0.020), Math.Min(1 - rect.Y, rect.H + 0.004));
            double ink2 = ImageOps.InkRatio(page, wider, 0.12);
            if (ink2 > 0.007) { ink = ink2; rect = wider; }
        }
        string result;
        if (na) result = "NA";
        else if (ink <= 0.007) result = "BLANK";
        else result = naExpected ? "NA" : "FILLED";
        var fact = new CheckFact { ItemNo = itemNo, Ink = ink, Result = result, Rect = rect };
        list.Add(fact);
        return fact;
    }

    private static bool IsNa(OcrPage? ocr, Rect rect)
    {
        if (ocr == null) return false;
        bool Inside(double cx, double cy)
            => cx >= rect.X - 0.006 && cx <= rect.X + rect.W + 0.006 &&
               cy >= rect.Y - 0.004 && cy <= rect.Y + rect.H + 0.004;

        foreach (var line in ocr.Lines)
        {
            foreach (var w in line.Words)
            {
                if (!Inside(w.X + w.W / 2, w.Y + w.H / 2)) continue;
                var t = Regex.Replace(w.Text, @"\s+", "").ToUpperInvariant();
                if (t.Contains("N/A") || t == "NA" || t.Contains("无此项") || t == "／" || t == "/") return true;
            }
            if (line.Words.Count > 0)
            {
                var cx = line.Words.Average(w => w.X + w.W / 2);
                var cy = line.Words.Average(w => w.Y + w.H / 2);
                if (Inside(cx, cy))
                {
                    var t = Regex.Replace(line.Text, @"\s+", "").ToUpperInvariant();
                    if (t.Contains("N/A") || t.Contains("无此项")) return true;
                }
            }
        }
        return false;
    }

    private static (double y0, double y1) Band(List<(double Y0, double Y1)> bands, int row, int rows, double top, double bottom)
    {
        if (bands.Count == rows && row < bands.Count) return (bands[row].Y0, bands[row].Y1);
        if (bands.Count > row) return (bands[row].Y0, bands[row].Y1);
        double h = Math.Min(0.040, (bottom - top) / rows);
        return (top + row * h, top + (row + 1) * h);
    }

    /// <summary>
    /// Detect table row bands with horizontal morphological opening. Unlike uniform row heights,
    /// this keeps double-height rows (e.g. item 27) as one band and does not shift later items.
    /// </summary>
    private static List<(double Y0, double Y1)> DetectBands(SKBitmap page, double nx0, double nx1, double ny0, double ny1, int rows)
    {
        var gray = ImageOps.ToGray(page);
        int w = page.Width, h = page.Height;
        int x0 = Math.Max(0, (int)(nx0 * w));
        int x1 = Math.Min(w - 1, (int)(nx1 * w));
        int y0 = Math.Max(0, (int)((ny0 - 0.03) * h));
        int y1 = Math.Min(h - 1, (int)((ny1 + 0.04) * h));
        int t = ImageOps.Otsu(gray, x0, y0, x1 - x0, y1 - y0, w);
        int width = Math.Max(1, x1 - x0);
        int kernel = Math.Max(24, width / 8);

        var coverage = new int[y1 - y0 + 1];
        for (int y = y0; y <= y1; y++)
        {
            int run = 0, linePixels = 0;
            for (int x = x0; x < x1; x++)
            {
                bool dark = gray[y * w + x] < t;
                if (dark) run++;
                else
                {
                    if (run >= kernel) linePixels += run;
                    run = 0;
                }
            }
            if (run >= kernel) linePixels += run;
            coverage[y - y0] = linePixels;
        }

        var centers = new List<double>();
        int start = -1;
        for (int i = 0; i < coverage.Length; i++)
        {
            bool line = coverage[i] > width * 0.50;
            if (line) { if (start < 0) start = i; }
            else if (start >= 0)
            {
                int end = i - 1;
                if (end - start <= Math.Max(4, (int)(h * 0.005)))
                    centers.Add((y0 + (start + end) / 2.0) / h);
                start = -1;
            }
        }
        if (start >= 0) centers.Add((y0 + (start + coverage.Length - 1) / 2.0) / h);

        var merged = new List<double>();
        foreach (var c in centers)
        {
            if (merged.Count == 0 || c - merged[^1] > 0.005) merged.Add(c);
            else merged[^1] = (merged[^1] + c) / 2;
        }
        if (merged.Count < 2) return new List<(double, double)>();

        var bands = new List<(double Y0, double Y1)>();
        for (int i = 0; i + 1 < merged.Count; i++)
        {
            double a = merged[i], b = merged[i + 1];
            if (b - a >= 0.004) bands.Add((a, b));
        }
        if (bands.Count == 0) return bands;

        // The first band is the header row when detection includes it.
        if (bands.Count > rows) bands.RemoveAt(0);
        if (bands.Count == 0) return bands;

        double Median()
        {
            var hs = bands.Select(x => x.Y1 - x.Y0).OrderBy(x => x).ToList();
            return hs[hs.Count / 2];
        }

        // Merge accidental split bands (e.g. a line through a tall row) until the count fits.
        int guard = 0;
        while (bands.Count > rows && guard++ < 20)
        {
            double med = Median();
            int idx = -1;
            double min = double.MaxValue;
            for (int i = 0; i < bands.Count; i++)
            {
                double hh = bands[i].Y1 - bands[i].Y0;
                if (hh < min) { min = hh; idx = i; }
            }
            if (idx < 0 || idx + 1 >= bands.Count || min >= med * 0.60) break;
            bands[idx] = (bands[idx].Y0, bands[idx + 1].Y1);
            bands.RemoveAt(idx + 1);
        }

        if (bands.Count > rows) bands = bands.Take(rows).ToList();
        return bands;
    }
}
