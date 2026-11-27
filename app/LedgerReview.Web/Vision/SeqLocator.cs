using LedgerReview.Domain;
using SkiaSharp;

namespace LedgerReview.Vision;

/// <summary>
/// 定位保养单页眉“第 ____ 次”的手写数字区域。
/// 做法：在页眉窗口内自上而下找第一条“长度只横跨数字空格”的印刷下划线，
/// 再取该线上方的一条窄带。这样切片里只有手写数字，不会把表格线和印刷字带进来。
/// </summary>
public static class SeqLocator
{
    public static Rect? Underline(SKBitmap bmp)
    {
        int W = bmp.Width, H = bmp.Height;
        if (W < 300 || H < 300) return null;

        int x0 = (int)(W * 0.04), x1 = (int)(W * 0.38);
        int y0 = (int)(H * 0.07), y1 = (int)(H * 0.16);
        var gray = ImageOps.ToGray(ImageOps.EnsureBgra(bmp));
        int otsu = ImageOps.Otsu(gray, x0, y0, x1 - x0, y1 - y0, W);
        // 印刷下划线是 1–2 px 的细线，抗锯齿后灰度偏浅，纯 Otsu 会把线判成“白”，这里取一个偏松的上限。
        int t = Math.Clamp(Math.Max(otsu, 175), 0, 220);

        int minLen = (int)(W * 0.030), maxLen = (int)(W * 0.18);
        for (int y = y0; y < y1; y++)
        {
            int run = 0, start = 0, best = 0, bestA = 0, bestB = 0;
            for (int x = x0; x < x1; x++)
            {
                if (gray[y * W + x] < t)
                {
                    if (run == 0) start = x;
                    run++;
                    if (run > best) { best = run; bestA = start; bestB = x + 1; }
                }
                else run = 0;
            }
            if (best < minLen || best > maxLen) continue;

            // 同一行可能因抗锯齿/笔迹压线而断开，把邻近小缺口并进线段范围
            int a = bestA, b = bestB, miss = 0;
            // 只允许极小缺口（抗锯齿），否则会把左侧印刷“第”的笔画并进来，污染数字切片
            int tol = 2;
            for (int x = bestA - 1; x >= x0; x--)
            {
                if (gray[y * W + x] < t) { a = x; miss = 0; }
                else if (++miss > tol) break;
            }
            miss = 0;
            for (int x = bestB; x < x1; x++)
            {
                if (gray[y * W + x] < t) { b = x + 1; miss = 0; }
                else if (++miss > tol) break;
            }
            if (b - a < minLen || b - a > maxLen) continue;

            // 线上方必须真有手写墨迹，否则继续往下找（避免把表格线当成下划线）
            int top = Math.Max(0, y - (int)(H * 0.042));
            int ink = 0, total = 0;
            for (int yy = top; yy < y - 1; yy++)
                for (int xx = a; xx < b; xx++)
                {
                    total++;
                    if (gray[yy * W + xx] < t) ink++;
                }
            if (total == 0) continue;
            double ratio = (double)ink / total;
            if (ratio < 0.004 || ratio > 0.45) continue;

            double rx = Math.Max(0, (a - W * 0.001) / W);
            double rw = Math.Min(1 - rx, (b - a + W * 0.002) / W);
            double bottom = Math.Max(0.02, (y - 1) / (double)H);
            double rh = Math.Min(bottom, 0.042);
            if (rw is < 0.02 or > 0.20 || rh < 0.015) return null;
            return new Rect(rx, bottom - rh, rw, rh);
        }
        return null;
    }
}
