using LedgerReview.Domain;
using LedgerReview.Infrastructure;
using LedgerReview.Pdf;
using SkiaSharp;

namespace LedgerReview.Vision;

public sealed record PersonRef(string Id, string Name, float[] Feature);

public sealed class SignatureEngine
{
    private static readonly string[] Names =
    {
        "黄远波","陈德亮","高峰","黄巍","雷春旺","王甦","王炜","潘岑","吴著铭","周含诚",
        "朱诗虎","黄志远","林天宇","肖星运","纪联鑫","施俊杰","郑俊言","吴在杨","赵乐杨","巫晖"
    };

    private readonly List<PersonRef> _refs = new();
    private readonly PdfService _pdf;
    private readonly Workspace _ws;
    private readonly Db _db;
    private readonly ILogger<SignatureEngine> _log;

    public bool Ready => _refs.Count >= 20;
    public double LooAccuracy { get; private set; }
    public int RefCount => _refs.Count;
    public string Status { get; private set; } = "未加载";

    public SignatureEngine(PdfService pdf, Workspace ws, Db db, ILogger<SignatureEngine> log)
    {
        _pdf = pdf; _ws = ws; _db = db; _log = log;
        Load();
    }

    private void Load()
    {
        try
        {
            var dir = Path.Combine(_ws.Root, "sigbook");
            Directory.CreateDirectory(dir);
            var png = Path.Combine(dir, "page.png");
            var src = Path.Combine(_ws.AssetsRoot, "signature-book.pdf");
            if (!File.Exists(src)) { Status = "缺少签名字迹表"; return; }
            if (!File.Exists(png))
            {
                _pdf.RenderPage(src, 0, 300, png);
            }
            using var page = SKBitmap.Decode(png);
            if (page == null) { Status = "签名字迹表无法解码"; return; }

            const double x0 = 0.1109, c1 = 0.1786, c2 = 0.3020, x1 = 0.9044;
            const double y0 = 0.2030, y1 = 0.8164;
            double rowH = (y1 - y0) / Names.Length;
            double colW = (x1 - c2) / 4.0;

            _db.ClearRefs();
            for (int r = 0; r < Names.Length; r++)
            {
                string pid = $"P{r + 1:00}";
                _db.UpsertPerson(pid, Names[r]);
                for (int c = 0; c < 4; c++)
                {
                    var rect = new Rect(c2 + c * colW, y0 + r * rowH, colW, rowH);
                    using var crop = ImageOps.Crop(page, rect, 0.03);
                    var feat = ImageOps.SignatureFeature(crop);
                    if (feat == null) continue;
                    var cropPath = Path.Combine(dir, $"{pid}_{c}.png");
                    ImageOps.SavePng(crop, cropPath);
                    _db.AddRef(pid, c, cropPath);
                    _refs.Add(new PersonRef(pid, Names[r], feat));
                }
            }
            Calibrate();
            Status = Ready ? $"签名字迹表：{Names.Length} 人 / {_refs.Count} 份（留一法 {LooAccuracy:P0}）" : "签名字迹表样本不足";
            _log.LogInformation(Status);
        }
        catch (Exception ex)
        {
            Status = "签名字迹表加载失败：" + ex.Message;
            _log.LogWarning(ex, Status);
        }
    }

    private void Calibrate()
    {
        int correct = 0, total = 0;
        foreach (var r in _refs)
        {
            var scores = ScoreAll(r.Feature, exclude: r);
            if (scores.Count == 0) continue;
            total++;
            if (scores[0].PersonId == r.Id) correct++;
        }
        LooAccuracy = total == 0 ? 0 : (double)correct / total;
    }

    private List<Candidate> ScoreAll(float[] feat, PersonRef? exclude = null)
    {
        var byPerson = new Dictionary<string, List<double>>();
        var names = new Dictionary<string, string>();
        foreach (var r in _refs)
        {
            if (exclude != null && ReferenceEquals(r, exclude)) continue;
            double s = ImageOps.Cosine(feat, r.Feature);
            if (!double.IsFinite(s)) continue;
            if (!byPerson.TryGetValue(r.Id, out var list)) { list = new List<double>(); byPerson[r.Id] = list; names[r.Id] = r.Name; }
            list.Add(s);
        }
        var result = new List<Candidate>();
        foreach (var (pid, list) in byPerson)
        {
            list.Sort((a, b) => b.CompareTo(a));
            double score = list.Count >= 2 ? (list[0] + list[1]) / 2 : list[0];
            result.Add(new Candidate(pid, names[pid], score));
        }
        result.Sort((a, b) => b.Score.CompareTo(a.Score));
        return result;
    }

    public sealed class MatchOutcome
    {
        public string Verdict { get; set; } = "UNDETERMINED";
        public Candidate? Top { get; set; }
        public double Score { get; set; }
        public List<Candidate> Candidates { get; set; } = new();
        public string? CropPath { get; set; }
    }

    public List<MatchOutcome> MatchSlot(SKBitmap page, Rect slot, string cropDir, string prefix, int expected = 1)
    {
        var outcomes = new List<MatchOutcome>();
        if (_refs.Count == 0) return outcomes;
        using var slotBmp = ImageOps.Crop(page, slot, 0.02);
        var segments = Segment(slotBmp);
        if (expected == 2 && segments.Count == 1)
        {
            var pair = SplitTwo(segments[0]);
            segments[0].Dispose();
            segments.Clear();
            segments.AddRange(pair);
        }
        if (segments.Count == 0) return outcomes;

        Directory.CreateDirectory(cropDir);
        int idx = 0;
        foreach (var seg in segments)
        {
            idx++;
            var feat = ImageOps.SignatureFeature(seg);
            if (feat == null) { seg.Dispose(); continue; }
            var cropPath = Path.Combine(cropDir, $"{prefix}_s{idx}.png");
            ImageOps.SavePng(seg, cropPath);
            var cands = ScoreAll(feat);
            seg.Dispose();
            var top = cands.FirstOrDefault();
            var outcome = new MatchOutcome { Top = top, Candidates = cands.Take(3).ToList(), Score = top?.Score ?? 0, CropPath = cropPath };
            if (top == null) outcome.Verdict = "UNDETERMINED";
            else
            {
                double second = cands.Count > 1 ? cands[1].Score : 0;
                if (top.Score >= 0.86 && top.Score - second >= 0.03) outcome.Verdict = "MATCH";
                else if (top.Score < 0.35) outcome.Verdict = "NOT_IN_ROSTER";
                else outcome.Verdict = "UNDETERMINED";
            }
            outcomes.Add(outcome);
        }
        return outcomes;
    }

    private static List<SKBitmap> SplitTwo(SKBitmap src)
    {
        var result = new List<SKBitmap>();
        int w = src.Width, h = src.Height;
        if (w < 12) { result.Add(src.Copy()); return result; }
        var gray = ImageOps.ToGray(src);
        int t = ImageOps.Otsu(gray, 0, 0, w, h, w);
        var counts = new int[w];
        for (int x = 0; x < w; x++)
        {
            int c = 0;
            for (int y = 0; y < h; y++) if (gray[y * w + x] < t) c++;
            counts[x] = c;
        }
        int lo = (int)(w * 0.34), hi = (int)(w * 0.66);
        int cut = (lo + hi) / 2, best = int.MaxValue;
        for (int x = lo; x <= hi; x++) if (counts[x] < best) { best = counts[x]; cut = x; }

        SKBitmap Slice(int x0, int x1)
        {
            x0 = Math.Max(0, x0); x1 = Math.Min(w, x1);
            var bmp = new SKBitmap(new SKImageInfo(Math.Max(1, x1 - x0), h, SKColorType.Bgra8888, SKAlphaType.Opaque));
            using var canvas = new SKCanvas(bmp);
            canvas.DrawBitmap(src, new SKRectI(x0, 0, x1, h), new SKRect(0, 0, Math.Max(1, x1 - x0), h));
            return bmp;
        }
        result.Add(Slice(0, cut));
        result.Add(Slice(cut, w));
        return result;
    }

    private static List<SKBitmap> Segment(SKBitmap crop)
    {
        var result = new List<SKBitmap>();
        var gray = ImageOps.ToGray(crop);
        int w = crop.Width, h = crop.Height;
        int t = ImageOps.Otsu(gray, 0, 0, w, h, w);
        var counts = new int[w];
        for (int x = 0; x < w; x++)
        {
            int c = 0;
            for (int y = (int)(h * 0.06); y < h * 0.94; y++)
                if (gray[y * w + x] < t) c++;
            counts[x] = c;
        }
        int minCol = Math.Max(2, (int)(h * 0.012));
        var runs = new List<(int a, int b)>();
        int start = -1;
        for (int x = 0; x < w; x++)
        {
            if (counts[x] >= minCol) { if (start < 0) start = x; }
            else if (start >= 0) { runs.Add((start, x)); start = -1; }
        }
        if (start >= 0) runs.Add((start, w));
        if (runs.Count == 0) return result;

        // merge runs separated by small gaps
        int minGap = Math.Max(3, (int)(w * 0.025));
        var merged = new List<(int a, int b)>();
        foreach (var r in runs)
        {
            if (merged.Count == 0) { merged.Add(r); continue; }
            var last = merged[^1];
            if (r.a - last.b <= minGap) merged[^1] = (last.a, r.b);
            else merged.Add(r);
        }
        var filtered = merged.Where(r => r.b - r.a >= w * 0.06).ToList();
        if (filtered.Count == 0) filtered = merged;

        foreach (var r in filtered.Take(3))
        {
            int pad = Math.Max(2, (int)(w * 0.01));
            int x0 = Math.Max(0, r.a - pad);
            int x1 = Math.Min(w, r.b + pad);
            var rect = new SKRectI(x0, 0, x1, h);
            var seg = new SKBitmap(new SKImageInfo(x1 - x0, h, SKColorType.Bgra8888, SKAlphaType.Opaque));
            using (var canvas = new SKCanvas(seg)) canvas.DrawBitmap(crop, rect, new SKRect(0, 0, x1 - x0, h));
            result.Add(seg);
        }
        if (result.Count == 0 && crop.Width > 4) result.Add(crop.Copy());
        return result;
    }
}




