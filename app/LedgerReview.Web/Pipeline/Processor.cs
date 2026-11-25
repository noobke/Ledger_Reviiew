using System.Text.RegularExpressions;
using System.Threading.Channels;
using LedgerReview.Audit;
using LedgerReview.Domain;
using LedgerReview.Infrastructure;
using LedgerReview.Pdf;
using LedgerReview.Vision;
using SkiaSharp;

namespace LedgerReview.Pipeline;

public sealed class Processor : BackgroundService
{
    private readonly Channel<string> _queue = Channel.CreateUnbounded<string>();
    private readonly Db _db;
    private readonly Workspace _ws;
    private readonly PdfService _pdf;
    private readonly OcrService _ocr;
    private readonly Classifier _classifier;
    private readonly CheckReader _checks;
    private readonly SignatureEngine _sig;
    private readonly AuditEngine _audit;
    private readonly ILogger<Processor> _log;

    public Processor(Db db, Workspace ws, PdfService pdf, OcrService ocr, Classifier classifier,
        CheckReader checks, SignatureEngine sig, AuditEngine audit, ILogger<Processor> log)
    {
        _db = db; _ws = ws; _pdf = pdf; _ocr = ocr; _classifier = classifier;
        _checks = checks; _sig = sig; _audit = audit; _log = log;
    }

    public void Enqueue(string batchId) => _queue.Writer.TryWrite(batchId);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await foreach (var batchId in _queue.Reader.ReadAllAsync(stoppingToken))
        {
            try { await ProcessBatch(batchId, stoppingToken); }
            catch (Exception ex)
            {
                _log.LogError(ex, "批次处理失败 {id}", batchId);
                _db.SetBatchStatus(batchId, "failed", ex.Message);
            }
        }
    }

    private async Task ProcessBatch(string batchId, CancellationToken ct)
    {
        _db.SetBatchStatus(batchId, "processing");
        var batch = _db.GetBatch(batchId);
        var all = _db.GetPages(batchId, null, false);
        int total = all.Count;
        int done = all.Count(p => p.Status == "done");
        _db.UpdateBatch(batchId, total, done, "processing");

        var work = _db.GetWorkPages(batchId);
        foreach (var p in work)
        {
            ct.ThrowIfCancellationRequested();
            await ProcessPage(batchId, p, batch?.Station ?? "", batch?.Equipment ?? "", ct);
            done++;
            _db.UpdateBatch(batchId, total, done, "processing");
        }

        _audit.CrossAudit(batchId, _db, _log);
        _db.RefreshProblemCount(batchId);
        _db.SetBatchStatus(batchId, "completed");
    }

    private async Task ProcessPage(string batchId, WorkPageRow p, string batchStation, string batchEquipment, CancellationToken ct)
    {
        try
        {
            var pageDir = _ws.PagesDir(batchId);
            var thumbDir = _ws.ThumbsDir(batchId);
            var cropDir = _ws.CropsDir(batchId);
            Directory.CreateDirectory(pageDir); Directory.CreateDirectory(thumbDir); Directory.CreateDirectory(cropDir);

            // 同一册拆成多个 PDF 时页码会重复，文件名必须带源文件标识，否则互相覆盖。
            var tag = p.SourceFileId.Length >= 8 ? p.SourceFileId[..8] : p.SourceFileId;
            var png = Path.Combine(pageDir, $"p{p.PageNo:D3}_{tag}.png");
            var thumb = Path.Combine(thumbDir, $"p{p.PageNo:D3}_{tag}.jpg");
            if (!File.Exists(png) || new FileInfo(png).Length < 1000)
            {
                _pdf.RenderPage(p.SourcePath, p.PageNo - 1, 150, png);
                _pdf.SaveThumb(png, thumb);
            }

            using var bmp = SKBitmap.Decode(png);
            if (bmp == null) { _db.SetPageStatus(p.Id, "failed", "页面图像无法解码"); return; }

            double quality = ImageOps.QualityScore(bmp);
            _db.UpdatePageImage(p.Id, png, thumb, bmp.Width, bmp.Height, quality);

            var ocr = _ocr.Available ? await _ocr.RecognizeAsync(png, ct) : OcrPage.Empty;
            var cls = _classifier.Classify(bmp, ocr);
            var tpl = Catalog.Find(cls.Type);

            var fact = new PageFact { Type = cls.Type, Confidence = cls.Confidence, OcrText = ocr.FullText };
            ExtractFields(fact, ocr, batchStation);
            var pageKey = $"{batchId}_p{p.PageNo:D3}_{tag}";
            await TryOcrFields(fact, bmp, cropDir, pageKey, ct);
            // 设备编号优先用文件名/批次里人工填写的值；手写编号 OCR 常读错，不能让它覆盖人工输入。
            var eqHint = !string.IsNullOrWhiteSpace(p.EquipmentHint) ? p.EquipmentHint : batchEquipment;
            if (!string.IsNullOrWhiteSpace(eqHint)) fact.EquipmentId = eqHint;
            if (tpl != null && tpl.ItemCount > 0) fact.Checks.AddRange(_checks.Read(bmp, tpl, ocr));
            ReadSignatures(bmp, tpl, fact, cropDir, pageKey, ocr);

            // Deterministic document id: one document per page.
            var docId = p.Id;
            _db.SaveDocument(docId, p.Id, fact);
            _db.ClearDocumentChildren(docId);
            SaveFieldsAndChecks(docId, fact);
            SaveSignatures(docId, fact);

            var findings = _audit.AuditPage(fact, tpl, quality, _ocr.Available);
            _db.ReplaceFindings(batchId, p.Id, docId, findings);

            if (!string.IsNullOrWhiteSpace(fact.EquipmentId) || !string.IsNullOrWhiteSpace(fact.Station))
            {
                var fam = tpl?.Family ?? "UNKNOWN";
                _db.SetEvent(docId, $"{fact.Station}|{fact.EquipmentId}|{fact.DocDate}|{fact.SeqNo}|{fam}");
            }

            _db.SetPageStatus(p.Id, "done");
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "页处理失败 {batch}/{page}", batchId, p.PageNo);
            _db.SetPageStatus(p.Id, "failed", ex.Message);
        }
    }

    private void SaveFieldsAndChecks(string docId, PageFact fact)
    {
        var zero = new Rect(0, 0, 0, 0);
        if (!string.IsNullOrEmpty(fact.Station)) _db.AddField(docId, "station", fact.Station, fact.Station, 0.75, zero);
        if (!string.IsNullOrEmpty(fact.EquipmentId)) _db.AddField(docId, "equipment", fact.EquipmentId, fact.EquipmentId, 0.75, zero);
        if (!string.IsNullOrEmpty(fact.DocDate)) _db.AddField(docId, "date", fact.DocDate, fact.DocDate, 0.75, zero);
        if (fact.SeqNo != null) _db.AddField(docId, "seq", fact.SeqNo.Value.ToString(), fact.SeqNo.Value.ToString(), 0.75, zero);
        if (fact.RunCount != null) _db.AddField(docId, "runCount", fact.RunCount.Value.ToString(), fact.RunCount.Value.ToString(), 0.7, zero);
        foreach (var c in fact.Checks) _db.AddCheck(docId, c);
    }

    private void SaveSignatures(string docId, PageFact fact)
    {
        foreach (var sf in fact.Signatures)
        {
            var slotId = _db.AddSignatureSlot(docId, sf);
            int i = 0;
            foreach (var o in sf.Outcomes)
            {
                i++;
                var cand = string.IsNullOrEmpty(o.PersonId) ? null : new Candidate(o.PersonId!, o.PersonName ?? "", o.Score);
                _db.AddMatch(slotId, i, sf, cand, o.Verdict, o.CandidatesJson, o.CropPath);
            }
        }
    }

    private async Task TryOcrFields(PageFact fact, SKBitmap bmp, string dir, string prefix, CancellationToken ct)
    {
        if (!_ocr.Available) return;

        if (fact.SeqNo == null)
        {
            var a = ParseSeq(await OcrRegion(bmp, new Rect(0.02, 0.04, 0.58, 0.10), dir, prefix + "_seq2", ct, 2));
            var b = ParseSeq(await OcrRegion(bmp, new Rect(0.02, 0.04, 0.58, 0.10), dir, prefix + "_seq3", ct, 3));
            if (a != null && a == b) fact.SeqNo = a;
        }

        if (string.IsNullOrWhiteSpace(fact.EquipmentId))
        {
            var eqPattern = @"([A-Za-z]{1,4}\d{2,6}(?:[-–—]\d{2,6}){0,2}(?:[（(][A-Za-z0-9]{1,6}[)）])?)";
            string? Eq(string txt)
            {
                var m = Regex.Match(Regex.Replace(txt, @"\s+", ""), eqPattern);
                if (!m.Success) return null;
                var id = m.Groups[1].Value.Replace('（', '(').Replace('）', ')').ToUpperInvariant();
                return IsEquipmentCandidate(id) ? id : null;
            }
            var a = Eq(await OcrRegion(bmp, new Rect(0.03, 0.12, 0.68, 0.12), dir, prefix + "_eq2", ct, 2));
            var b = Eq(await OcrRegion(bmp, new Rect(0.03, 0.12, 0.68, 0.12), dir, prefix + "_eq3", ct, 3));
            if (a != null && a == b) fact.EquipmentId = a;
        }

        if (string.IsNullOrWhiteSpace(fact.DocDate))
        {
            foreach (var rect in new[]
            {
                new Rect(0.62, 0.66, 0.37, 0.26),
                new Rect(0.02, 0.66, 0.96, 0.30)
            })
            {
                var d = ParseDate(Regex.Replace(await OcrRegion(bmp, rect, dir, prefix + "_date2", ct, 2), @"\s+", ""));
                if (d == null) d = ParseDate(Regex.Replace(await OcrRegion(bmp, rect, dir, prefix + "_date3", ct, 3), @"\s+", ""));
                if (d != null) { fact.DocDate = d; break; }
            }
        }
    }

    private static int? ParseSeq(string txt)
    {
        var compact = Regex.Replace(txt, @"\s+", "");
        var m = Regex.Match(compact, @"第([0-9]{1,3}|[一二三四五六七八九十百]{1,4})次");
        if (!m.Success) return null;
        var s = m.Groups[1].Value;
        return int.TryParse(s, out var n) ? n : ChineseNumber(s);
    }

    private async Task<string> OcrRegion(SKBitmap bmp, Rect rect, string dir, string prefix, CancellationToken ct, int scale = 2)
    {
        try
        {
            using var crop = ImageOps.Crop(bmp, rect, 0);
            using var up = ImageOps.Resize(crop, crop.Width * scale, crop.Height * scale);
            var path = Path.Combine(dir, prefix + "_field.png");
            ImageOps.SavePng(up, path);
            var page = await _ocr.RecognizeAsync(path, ct);
            return page.FullText;
        }
        catch { return ""; }
    }

    private static string? ParseDate(string compact)
    {
        var m = Regex.Matches(compact, @"(20\d{2})[.\-/年](\d{1,2})[.\-/月](\d{1,2})");
        for (int i = m.Count - 1; i >= 0; i--)
        {
            if (int.TryParse(m[i].Groups[1].Value, out var y) &&
                int.TryParse(m[i].Groups[2].Value, out var mo) &&
                int.TryParse(m[i].Groups[3].Value, out var d) &&
                y >= 2015 && y <= DateTime.Today.Year + 1 && mo >= 1 && mo <= 12 && d >= 1 && d <= 31)
            {
                try { return new DateTime(y, mo, d).ToString("yyyy-MM-dd"); } catch { }
            }
        }
        var m8 = Regex.Match(compact, @"(20\d{2})(\d{2})(\d{2})");
        if (m8.Success &&
            int.TryParse(m8.Groups[1].Value, out var y8) && y8 >= 2015 && y8 <= DateTime.Today.Year + 1 &&
            int.TryParse(m8.Groups[2].Value, out var mo8) &&
            int.TryParse(m8.Groups[3].Value, out var d8) &&
            mo8 >= 1 && mo8 <= 12 && d8 >= 1 && d8 <= 31)
        {
            try { return new DateTime(y8, mo8, d8).ToString("yyyy-MM-dd"); } catch { }
        }
        return null;
    }

    private static void ExtractFields(PageFact fact, OcrPage ocr, string batchStation)
    {
        var text = ocr.FullText;
        if (string.IsNullOrWhiteSpace(text)) return;
        var compact = Regex.Replace(text, @"\s+", "");

        var seqLine = ocr.Lines.FirstOrDefault(l => l.Words.Count > 0 && l.Words.Min(w => w.Y) < 0.17 && Regex.IsMatch(Regex.Replace(l.Text, @"\s+", ""), @"第([0-9]{1,3}|[一二三四五六七八九十百]{1,4})次"));
        var seq = seqLine != null
            ? Regex.Match(Regex.Replace(seqLine.Text, @"\s+", ""), @"第([0-9]{1,3}|[一二三四五六七八九十百]{1,4})次")
            : Regex.Match(compact, @"第\s*([0-9]{1,3}|[一二三四五六七八九十百]{1,4})\s*次");
        if (seq.Success)
        {
            var s = seq.Groups[1].Value;
            fact.SeqNo = int.TryParse(s, out var n) ? n : ChineseNumber(s);
        }

        var dates = Regex.Matches(compact, @"(20\d{2})[.\-/年](\d{1,2})[.\-/月](\d{1,2})");
        for (int i = dates.Count - 1; i >= 0; i--)
        {
            if (int.TryParse(dates[i].Groups[1].Value, out var y) &&
                int.TryParse(dates[i].Groups[2].Value, out var mo) &&
                int.TryParse(dates[i].Groups[3].Value, out var d) &&
                y >= 2015 && y <= DateTime.Today.Year + 1 && mo >= 1 && mo <= 12 && d >= 1 && d <= 31)
            {
                try { fact.DocDate = new DateTime(y, mo, d).ToString("yyyy-MM-dd"); break; } catch { }
            }
        }
        if (fact.DocDate == null)
        {
            var compact8 = Regex.Match(compact, @"(20\d{2})(\d{2})(\d{2})");
            if (compact8.Success &&
                int.TryParse(compact8.Groups[1].Value, out var y8) && y8 >= 2015 && y8 <= DateTime.Today.Year + 1 &&
                int.TryParse(compact8.Groups[2].Value, out var m8) &&
                int.TryParse(compact8.Groups[3].Value, out var d8) &&
                m8 >= 1 && m8 <= 12 && d8 >= 1 && d8 <= 31)
            {
                try { fact.DocDate = new DateTime(y8, m8, d8).ToString("yyyy-MM-dd"); } catch { }
            }
        }

        // Equipment number: only from the header block, never from body text.
        // Prefer the WBS number (three parts, e.g. LN60-071722-028) over local numbers.
        var wbsRx = new Regex(@"([A-Za-z]{1,4}\d{2,5}[-–—]\d{3,6}[-–—]\d{2,4})");
        var localRx = new Regex(@"([A-Za-z]{1,4}\d{2,6}(?:[-–—]\d{2,6}){0,2}(?:[（(][A-Za-z0-9]{1,6}[)）])?)");
        foreach (var line in ocr.Lines)
        {
            if (line.Words.Count == 0 || line.Words.Min(w => w.Y) > 0.30) continue;
            var lt = Regex.Replace(line.Text, @"\s+", "");
            var w = wbsRx.Match(lt);
            if (w.Success)
            {
                fact.EquipmentId = w.Groups[1].Value.Replace('（', '(').Replace('）', ')').ToUpperInvariant();
                break;
            }
        }
        if (string.IsNullOrWhiteSpace(fact.EquipmentId))
        {
            foreach (var line in ocr.Lines)
            {
                if (line.Words.Count == 0 || line.Words.Min(w => w.Y) > 0.30) continue;
                var lt = Regex.Replace(line.Text, @"\s+", "");
                var eq = localRx.Match(lt);
                if (!eq.Success) continue;
                var id = eq.Groups[1].Value.Replace('（', '(').Replace('）', ')').ToUpperInvariant();
                if (IsEquipmentCandidate(id)) { fact.EquipmentId = id; break; }
            }
        }

        var run = Regex.Match(compact, @"运行次数\D{0,10}(\d{3,7})");
        if (run.Success && int.TryParse(run.Groups[1].Value, out var rc)) fact.RunCount = rc;

        // 批次里填了站点就以批次为准（手写/印刷站点名 OCR 很容易读花）；
        // 只有批次未填时才从“项目名称/使用地点”同取值，且不跨行。
        fact.Station = batchStation;
        if (string.IsNullOrWhiteSpace(fact.Station))
        {
            var st = Regex.Match(text, @"(项目名称|使用地点)[ \t]*[：:]?[ \t]*([^\n]{2,24})");
            if (st.Success)
            {
                var v = st.Groups[2].Value.Trim(' ', '：', ':', '-', '—');
                if (!Regex.IsMatch(v, @"WBS|编号|电梯|扶梯|名称|地点|项目")) fact.Station = v;
            }
        }
    }

    private static bool IsEquipmentCandidate(string id)
    {
        var up = id.ToUpperInvariant();
        if (up.StartsWith("DB") || up.StartsWith("GB") || up.StartsWith("ISO") || up.StartsWith("TKEC")) return false;
        var m = Regex.Match(up, @"^([A-Z]{1,4})(\d{2,6})");
        if (!m.Success) return false;
        var prefix = m.Groups[1].Value;
        var digits = m.Groups[2].Value;
        // 本地编号至少 4 位数字，避免把 HK09/HK42 这类 OCR 残片当设备号。
        if (digits.Length < 4) return false;
        return true;
    }

    private static int ChineseNumber(string s)
    {
        int idx = s.IndexOf('十');
        if (idx >= 0)
        {
            int tens = idx == 0 ? 1 : ChineseDigit(s[..idx]);
            var rest = s[(idx + 1)..];
            int ones = rest.Length == 0 ? 0 : ChineseDigit(rest);
            return tens * 10 + ones;
        }
        return ChineseDigit(s);
    }

    private static int ChineseDigit(string s)
    {
        const string digits = "零一二三四五六七八九";
        int v = 0;
        foreach (var c in s)
        {
            int i = digits.IndexOf(c);
            if (i >= 0) v = v * 10 + i;
        }
        return v;
    }

    private void ReadSignatures(SKBitmap bmp, FormTemplate? tpl, PageFact fact, string cropDir, string prefix, OcrPage ocr)
    {
        if (tpl == null) return;
        var (tech, sup, cust) = LocateSlots(ocr, tpl);
        if (tpl.Type == DocType.SupervisionF1)
        {
            // 签名有的写在标签上方、有的压着标签写，纵向多试几个窗口取墨迹最多的那个。
            var sl = FindLine(ocr, "电梯安全管理人员", 0.62, 0.80);
            if (sl != null) sup = PickInkWindow(bmp, sl.Value.Top, 0.25, 0.23, 0.050, new[] { -0.045, -0.030, -0.015, 0.0, 0.012 });
            var ml = FindLine(ocr, "维护保养单位人员", 0.80, 0.95);
            if (ml != null) cust = PickInkWindow(bmp, ml.Value.Top, 0.25, 0.25, 0.050, new[] { -0.058, -0.040, -0.025, -0.010, 0.005 });
        }
        var slots = new List<(string Code, string Role, Rect? Rect)>
        {
            ("TECH_SIGN", "MAINTAINER", tech),
            ("SUPERVISOR_SIGN", "SUPERVISOR", sup),
            ("CUSTOMER_SIGN", "CUSTOMER", cust)
        };
        foreach (var (code, role, rect) in slots)
        {
            if (rect == null) continue;
            double ink = ImageOps.InkRatio(bmp, rect, 0.10);
            bool f1Maintainer = tpl.Type == DocType.SupervisionF1 && code == "CUSTOMER_SIGN";
            var sf = new SignatureFact
            {
                Code = code,
                Role = f1Maintainer ? "MAINTAINER" : role,
                Rect = rect,
                HasInk = ink > 0.006
            };
            if (sf.HasInk && _sig.Ready && sf.Role == "MAINTAINER")
            {
                int expected = (code == "TECH_SIGN" || f1Maintainer) ? 2 : 1;
                var outcomes = _sig.MatchSlot(bmp, rect, cropDir, $"{prefix}_{code}", expected);
                foreach (var o in outcomes)
                {
                    var json = System.Text.Json.JsonSerializer.Serialize(o.Candidates);
                    sf.Outcomes.Add(new SignatureOutcome(o.Verdict, o.Top?.PersonId, o.Top?.PersonName, o.Score, json, o.CropPath));
                }
                var first = sf.Outcomes.FirstOrDefault();
                if (first != null) { sf.Verdict = first.Verdict; sf.PersonName = first.PersonName; sf.Score = first.Score; }
            }
            fact.Signatures.Add(sf);
        }
    }

    private static (Rect? Tech, Rect? Supervisor, Rect? Customer) LocateSlots(OcrPage ocr, FormTemplate tpl)
    {
        var tech = AnchorBelow(ocr, new[] { "姓名" }, tpl.NameSlot, 0.715, 0.250, 0.060, 0.010, 0.60, 0.78);
        var sup = AnchorBelow(ocr, new[] { "电梯安全管理人员" }, tpl.SupervisorSlot, 0.295, 0.310, 0.055, -0.008, 0.72, 0.86);
        var cust = AnchorBelow(ocr, new[] { "使用单位安全管理人员", "使用单位安全管理人员（签字）" }, tpl.CustomerSlot, 0.715, 0.250, 0.048, 0.010, 0.78, 0.93);
        if (tpl.Type == DocType.SupervisionF1)
        {
            // F.1 两个签名横排在“（签字）：”右侧、与标签同一行；用标签行上沿定位，取右侧固定宽度。
            var supLine = FindLine(ocr, "电梯安全管理人员", 0.62, 0.80);
            var mLine = FindLine(ocr, "维护保养单位人员", 0.80, 0.95);
            sup = supLine != null
                ? new Rect(0.25, Math.Clamp(supLine.Value.Top - 0.032, 0, 0.93), 0.23, 0.055)
                : tpl.SupervisorSlot;
            // 维保签名有的写在标签上方、有的压着标签写，纵向范围放宽。
            cust = mLine != null
                ? new Rect(0.25, Math.Clamp(mLine.Value.Top - 0.058, 0, 0.93), 0.25, 0.085)
                : tpl.CustomerSlot;
        }
        if (tpl.Type == DocType.SelfCheckC2)
            cust = AnchorBelow(ocr, new[] { "电梯安全管理人员" }, tpl.CustomerSlot, 0.320, 0.300, 0.055, -0.008, 0.82, 0.96);
        return (tech, sup, cust);
    }

    private static Rect PickInkWindow(SKBitmap bmp, double labelTop, double x, double w, double h, double[] offsets)
    {
        Rect best = new(x, Math.Clamp(labelTop + offsets[0], 0, 0.93), w, h);
        double bestInk = ImageOps.InkRatio(bmp, best, 0.10);
        foreach (var dy in offsets)
        {
            var r = new Rect(x, Math.Clamp(labelTop + dy, 0, 0.93), w, h);
            double ink = ImageOps.InkRatio(bmp, r, 0.10);
            if (ink > bestInk) { bestInk = ink; best = r; }
        }
        return best;
    }

    private static (double Top, double Center)? FindLine(OcrPage ocr, string key, double minY, double maxY)
    {
        var k = Regex.Replace(key, @"\s+", "");
        (double Top, double Center)? best = null;
        double bestDist = double.MaxValue;
        foreach (var line in ocr.Lines)
        {
            if (line.Words.Count == 0) continue;
            var cy = line.Words.Average(v => v.Y + v.H / 2);
            if (cy < minY || cy > maxY) continue;
            if (!Regex.Replace(line.Text, @"\s+", "").Contains(k)) continue;
            double dist = Math.Abs(cy - (minY + maxY) / 2);
            if (dist < bestDist) { bestDist = dist; best = (line.Words.Min(v => v.Y), cy); }
        }
        return best;
    }

    private static Rect? AnchorBelow(OcrPage ocr, string[] keys, Rect? fallback, double x, double w, double h, double dy, double minY, double maxY)
    {
        OcrLine? best = null;
        double bestDist = double.MaxValue;
        foreach (var line in ocr.Lines)
        {
            if (line.Words.Count == 0) continue;
            var cy = line.Words.Average(v => v.Y + v.H / 2);
            if (cy < minY || cy > maxY) continue;
            var t = Regex.Replace(line.Text, @"\s+", "");
            if (!keys.Any(k => t.Contains(Regex.Replace(k, @"\s+", "")))) continue;
            double dist = Math.Abs(cy - (minY + maxY) / 2);
            if (dist < bestDist) { bestDist = dist; best = line; }
        }
        if (best == null) return fallback;
        double y = best.Words.Max(v => v.Y + v.H);
        return new Rect(x, Math.Clamp(y + dy, 0, 0.97 - h), w, h);
    }
}




