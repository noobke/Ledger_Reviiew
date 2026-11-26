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
    private readonly DigitRecognizer _digits;
    private readonly AuditEngine _audit;
    private readonly ILogger<Processor> _log;

    public Processor(Db db, Workspace ws, PdfService pdf, OcrService ocr, Classifier classifier,
        CheckReader checks, SignatureEngine sig, DigitRecognizer digits, AuditEngine audit, ILogger<Processor> log)
    {
        _db = db; _ws = ws; _pdf = pdf; _ocr = ocr; _classifier = classifier;
        _checks = checks; _sig = sig; _digits = digits; _audit = audit; _log = log;
    }

    public void Enqueue(string batchId) => _queue.Writer.TryWrite(batchId);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // 程序重启后，把上次没处理完的批次重新排队，避免一直挂在“待处理”。
        foreach (var b in _db.GetBatches().Where(x => x.Status is "pending" or "processing" or "rendered"))
        {
            _log.LogInformation("恢复未完成批次 {name}（{status}）", b.Name, b.Status);
            _queue.Writer.TryWrite(b.Id);
        }

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

        var facts = new Dictionary<string, PageContext>();
        var work = _db.GetWorkPages(batchId);
        foreach (var p in work)
        {
            ct.ThrowIfCancellationRequested();
            await ProcessPage(batchId, p, batch?.Station ?? "", batch?.Equipment ?? "", facts, ct);
            done++;
            _db.UpdateBatch(batchId, total, done, "processing");
        }

        // 先按“一册=一台梯”归并站点/设备号，再逐页出问题，避免手写 OCR 噪声把同一册拆成很多组。
        ConsolidateIdentity(batchId, facts, batch);
        ConsolidateSeq(batchId, facts);
        foreach (var (pageId, ctx) in facts)
        {
            var findings = _audit.AuditPage(ctx.Fact, ctx.Tpl, ctx.Quality, _ocr.Available);
            _db.ReplaceFindings(batchId, pageId, pageId, findings);
            if (!string.IsNullOrWhiteSpace(ctx.Fact.EquipmentId) || !string.IsNullOrWhiteSpace(ctx.Fact.Station))
                _db.SetEvent(pageId, $"{ctx.Fact.Station}|{ctx.Fact.EquipmentId}|{ctx.Fact.DocDate}|{ctx.Fact.SeqNo}|{ctx.Tpl?.Family ?? "UNKNOWN"}");
        }

        _audit.CrossAudit(batchId, _db, _log);
        _db.RefreshProblemCount(batchId);
        _db.SetBatchStatus(batchId, "completed");
    }

    private sealed record PageContext(PageFact Fact, FormTemplate? Tpl, double Quality);

    private async Task ProcessPage(string batchId, WorkPageRow p, string batchStation, string batchEquipment,
        Dictionary<string, PageContext> facts, CancellationToken ct)
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
            await TryOcrFields(fact, bmp, ocr, cropDir, pageKey, ct);
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

            facts[p.Id] = new PageContext(fact, tpl, quality);
            _db.SetPageStatus(p.Id, "done");
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "页处理失败 {batch}/{page}", batchId, p.PageNo);
            _db.SetPageStatus(p.Id, "failed", ex.Message);
        }
    }

    /// <summary>
    /// 一册 PDF 通常是一台电梯的台账：同一册内站点、设备号以多数页为准，
    /// 缺页（如 F.1 监督表没有项目名称）从同册其他页补齐，避免同一台梯被拆成多组。
    /// </summary>
    private void ConsolidateIdentity(string batchId, Dictionary<string, PageContext> facts, BatchRow? batch)
    {
        var docs = _db.GetDocs(batchId);
        var groups = docs.GroupBy(d => d.SourceFileId).ToList();
        var perFile = groups.ToDictionary(g => g.Key,
            g => (Station: PickStation(g.Select(d => d.Station)), Equip: PickEquipment(g.Select(d => d.EquipmentId))));

        // 整批只识别出唯一一个滨海快线站名时，用它补全没读到站点的册（多站批次不会走到这里）。
        var knownStations = perFile.Values.Select(v => v.Station).Where(StationCatalog.IsKnown).Distinct().ToList();
        var sharedStation = knownStations.Count == 1 ? knownStations[0] : null;

        foreach (var g in groups)
        {
            var (fileStation, equip) = perFile[g.Key];
            foreach (var d in g)
            {
                var st = StationCatalog.Match(d.Station) ?? fileStation ?? sharedStation ?? "";
                var eq = string.IsNullOrWhiteSpace(equip) ? (d.EquipmentId ?? "") : equip;
                if (st != d.Station || eq != d.EquipmentId)
                    _db.SetDocumentIdentity(d.Id, st, eq);
                if (facts.TryGetValue(d.PageId, out var ctx))
                {
                    if (!string.IsNullOrWhiteSpace(st)) ctx.Fact.Station = st;
                    if (!string.IsNullOrWhiteSpace(eq)) ctx.Fact.EquipmentId = eq;
                }
            }
        }

        if (batch == null) return;
        var batchStation = StationCatalog.Match(batch.Station) ?? PickStation(docs.Select(d => d.Station)) ?? batch.Station;
        var batchEquip = string.IsNullOrWhiteSpace(batch.Equipment) ? PickEquipment(docs.Select(d => d.EquipmentId)) ?? "" : batch.Equipment;
        var name = "";
        if (string.IsNullOrWhiteSpace(batch.Name) || batch.Name.StartsWith("未命名批次"))
        {
            var parts = new List<string>();
            if (!string.IsNullOrWhiteSpace(batchStation)) parts.Add(batchStation);
            if (!string.IsNullOrWhiteSpace(batchEquip)) parts.Add(batchEquip);
            name = parts.Count > 0 ? string.Join(" ", parts) : "滨海快线台账";
        }
        _db.UpdateBatchInfo(batchId, name, batchStation ?? "", batchEquip);
    }

    /// <summary>
    /// 同一台梯的保养单按日期排序后，“第 N 次”应逐次 +1。用多数页拟合出偏移量，
    /// 补齐读不出的次数、纠正明显读错的次数；拟合不一致时整体放弃，绝不硬猜。
    /// </summary>
    private void ConsolidateSeq(string batchId, Dictionary<string, PageContext> facts)
    {
        var docs = _db.GetDocs(batchId)
            .Where(d => d.DocType.StartsWith("Chuiti") || d.DocType.StartsWith("Futi")).ToList();
        foreach (var g in docs.GroupBy(d => string.IsNullOrWhiteSpace(d.EquipmentId)
                     ? "file:" + d.SourceFileId : StationCatalog.EquipmentKey(d.EquipmentId)))
        {
            var list = g.Select(d =>
                {
                    facts.TryGetValue(d.PageId, out var ctx);
                    DateTime.TryParse(ctx?.Fact.DocDate, out var dt);
                    return (Doc: d, Ctx: ctx, Date: dt);
                })
                .Where(x => x.Ctx != null && x.Date != default)
                .OrderBy(x => x.Date).ToList();
            if (list.Count < 4) continue;
            var known = list.Select((x, i) => (Index: i, Seq: x.Ctx!.Fact.SeqNo))
                .Where(k => k.Seq.HasValue && k.Seq > 0).ToList();
            if (known.Count < 3) continue;
            var offsets = known.Select(k => k.Seq!.Value - k.Index).OrderBy(v => v).ToList();
            int median = offsets[offsets.Count / 2];
            int agree = known.Count(k => Math.Abs(k.Seq!.Value - k.Index - median) <= 1);
            if (agree < known.Count * 0.8) continue;
            for (int i = 0; i < list.Count; i++)
            {
                int pred = i + median;
                if (pred < 1 || pred > 300) continue;
                var cur = list[i].Ctx!.Fact.SeqNo;
                if (cur.HasValue && cur > 0 && Math.Abs(cur.Value - pred) <= 1) continue;
                _db.SetDocumentSeq(list[i].Doc.Id, pred);
                list[i].Ctx!.Fact.SeqNo = pred;
            }
        }
    }

    private static string? PickStation(IEnumerable<string> values)
    {
        var list = values.Where(v => !string.IsNullOrWhiteSpace(v)).ToList();
        var known = list.Select(StationCatalog.Match).Where(v => v != null)
            .GroupBy(v => v!).OrderByDescending(g => g.Count()).FirstOrDefault();
        if (known != null) return known.Key;
        var raw = list.Where(v => v.Length is >= 2 and <= 12)
            .GroupBy(v => v).OrderByDescending(g => g.Count()).FirstOrDefault();
        return raw?.Key;
    }

    private static string? PickEquipment(IEnumerable<string> values)
    {
        var list = values.Where(v => !string.IsNullOrWhiteSpace(v))
            .Select(StationCatalog.NormalizeEquipment).ToList();
        if (list.Count == 0) return null;
        var wbsRx = new Regex(@"^[A-Z]{1,4}\d{2,5}-\d{3,6}-\d{2,4}$");
        var wbs = list.Where(v => wbsRx.IsMatch(v))
            .GroupBy(v => v).OrderByDescending(g => g.Count()).FirstOrDefault();
        if (wbs != null) return wbs.Key;
        var group = list.GroupBy(StationCatalog.EquipmentKey).OrderByDescending(g => g.Count()).First();
        return group.GroupBy(v => v).OrderByDescending(g => g.Count()).First().Key;
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

    private async Task TryOcrFields(PageFact fact, SKBitmap bmp, OcrPage ocr, string dir, string prefix, CancellationToken ct)
    {
        if (!_ocr.Available) return;

        if (fact.SeqNo == null)
        {
            // “第 ___ 次”位置随版式上下浮动，先试标题行左段，再试整行；数字空格常被 OCR 读丢，最后一档只认数字。
            var seqRects = new[]
            {
                new Rect(0.10, 0.095, 0.13, 0.105),  // 只截“第 __ 次”
                new Rect(0.12, 0.095, 0.09, 0.105),  // 只截手写数字
                new Rect(0.02, 0.10, 0.30, 0.09),
                new Rect(0.02, 0.13, 0.24, 0.07),
                new Rect(0.02, 0.04, 0.58, 0.12)
            };
            foreach (var rect in seqRects)
            {
                // 同一区域读四档（6×、4×、4×二值化、2×），至少两档给出同一个数才采用。
                var readings = new List<int>();
                foreach (var (scale, enhance) in new[] { (6, false), (4, false), (4, true), (2, false) })
                {
                    var text = await OcrRegion(bmp, rect, dir, prefix + "_seq", ct, scale, enhance);
                    var v = ParseSeq(text) ?? ParseBareNumber(text);
                    if (v != null) readings.Add(v.Value);
                }
                var pick = readings.GroupBy(v => v).Where(g => g.Count() >= 2)
                    .OrderByDescending(g => g.Count()).FirstOrDefault();
                if (pick != null) { fact.SeqNo = pick.Key; break; }
            }
        }

        // 数字专用识别：用“第/次”词框夹出的数字区，独立于通用 OCR，读不出时兜底
        if (fact.Type.ToString().StartsWith("Chuiti") || fact.Type.ToString().StartsWith("Futi"))
        {
            var gap = SeqGapRect(ocr) ?? new Rect(0.10, 0.095, 0.13, 0.105);
            using (var crop = ImageOps.Crop(bmp, gap, 0))
            {
                try { ImageOps.SavePng(crop, Path.Combine(dir, prefix + "_seqgap.png")); } catch { }
                var dr = _digits.RecognizeBest(crop);
                _log.LogInformation("数字区 {p}: ocr={q} guess={v} conf={c:0.00} {d}",
                    prefix, fact.SeqNo, dr?.Value, dr?.Confidence ?? 0, dr?.Detail);
                if (fact.SeqNo == null && dr != null && dr.Confidence >= 0.60) fact.SeqNo = dr.Value;
            }
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
            var f1Texts = new List<string>();
            foreach (var rect in new[]
            {
                new Rect(0.58, 0.84, 0.42, 0.12),   // 保养单右下“姓名/日期”栏
                new Rect(0.62, 0.66, 0.37, 0.26),   // F.1 两处签字的日期
                new Rect(0.02, 0.66, 0.96, 0.30)
            })
            {
                foreach (var (scale, enhance) in new[] { (3, false), (3, true), (2, false) })
                {
                    var txt = Regex.Replace(await OcrRegion(bmp, rect, dir, prefix + "_date", ct, scale, enhance), @"\s+", "");
                    if (rect.Y < 0.70) f1Texts.Add(txt);
                    var d = ParseDate(txt);
                    if (d != null) { fact.DocDate = d; break; }
                }
                if (!string.IsNullOrWhiteSpace(fact.DocDate)) break;
            }

            // F.1 两行日期常一行年份错、一行月份错（如 7076年9月1日 / 2026年0月1日），
            // 只有各项能互相印证（日相同）时才拼出日期，避免把两个不同日期混在一起。
            if (string.IsNullOrWhiteSpace(fact.DocDate) && fact.Type == DocType.SupervisionF1)
                fact.DocDate = CombineDateFragments(f1Texts);
        }
    }

    private static string? CombineDateFragments(IEnumerable<string> texts)
    {
        var frags = new List<(int Y, int M, int D)>();
        foreach (var t in texts)
            foreach (Match m in Regex.Matches(t, @"(\d{3,4})[.\-/年](\d{1,2})[.\-/月](\d{1,2})"))
                frags.Add((int.Parse(m.Groups[1].Value), int.Parse(m.Groups[2].Value), int.Parse(m.Groups[3].Value)));
        static bool ValidY(int y) => y >= 2015 && y <= DateTime.Today.Year + 1;
        static bool ValidM(int m) => m >= 1 && m <= 12;
        static bool ValidD(int d) => d >= 1 && d <= 31;

        foreach (var a in frags)
            foreach (var b in frags)
            {
                if (a.Equals(b)) continue;
                if (!ValidY(b.Y) || !ValidM(a.M)) continue;
                int day = ValidD(a.D) ? a.D : b.D;
                if (!ValidD(day)) continue;
                if (ValidD(a.D) && ValidD(b.D) && a.D != b.D) continue;
                try { return new DateTime(b.Y, a.M, day).ToString("yyyy-MM-dd"); } catch { }
            }
        return null;
    }

    private static int? ParseSeq(string txt)
    {
        var compact = Regex.Replace(txt, @"\s+", "");
        var m = Regex.Match(compact, @"第[_\-\s]*([0-9]{1,3}|[一二三四五六七八九十百]{1,4})[_\-\s]*次");
        if (!m.Success) return null;
        var s = m.Groups[1].Value;
        var v = int.TryParse(s, out var n) ? n : ChineseNumber(s);
        return v >= 1 && v <= 200 ? v : null;
    }

    /// <summary>只认数字的兜底：裁得很紧的“第__次”区域可能只剩 1–2 位数字；含汉字则不可信。</summary>
    private static int? ParseBareNumber(string txt)
    {
        if (Regex.IsMatch(txt, @"[\u4e00-\u9fff]")) return null;
        var digits = Regex.Replace(txt, @"[^0-9]", "");
        if (digits.Length is 1 or 2 && int.TryParse(digits, out var n) && n >= 1 && n <= 200) return n;
        return null;
    }

    private async Task<string> OcrRegion(SKBitmap bmp, Rect rect, string dir, string prefix, CancellationToken ct,
        int scale = 2, bool enhance = false)
    {
        SKBitmap? bin = null;
        try
        {
            using var crop = ImageOps.Crop(bmp, rect, 0);
            using var up = ImageOps.Resize(crop, crop.Width * scale, crop.Height * scale);
            SKBitmap target = up;
            if (enhance) { bin = ImageOps.Binarize(up); target = bin; }
            var path = Path.Combine(dir, prefix + (enhance ? "_bin" : "") + "_field.png");
            ImageOps.SavePng(target, path);
            var page = await _ocr.RecognizeAsync(path, ct);
            return page.FullText;
        }
        catch { return ""; }
        finally { bin?.Dispose(); }
    }

    private static string? ParseDate(string compact)
    {
        string? Build(int y, int mo, int d)
        {
            if (y < 2015 || y > DateTime.Today.Year + 1 || mo < 1 || mo > 12 || d < 1 || d > 31) return null;
            try { return new DateTime(y, mo, d).ToString("yyyy-MM-dd"); } catch { return null; }
        }

        // 标准写法：2026.9.1 / 2026-09-01 / 2026年9月1日
        var m = Regex.Matches(compact, @"(20\d{2})[.\-/年](\d{1,2})[.\-/月](\d{1,2})");
        for (int i = m.Count - 1; i >= 0; i--)
        {
            var r = Build(int.Parse(m[i].Groups[1].Value), int.Parse(m[i].Groups[2].Value), int.Parse(m[i].Groups[3].Value));
            if (r != null) return r;
        }

        // 手写年份漏写 0：2.6.5.9 → 2026-05-09
        var m4 = Regex.Matches(compact, @"(?<!\d)(\d)[.\-/年](\d)[.\-/月](\d{1,2})[.\-/日](\d{1,2})(?!\d)");
        for (int i = m4.Count - 1; i >= 0; i--)
        {
            var y = 2000 + int.Parse(m4[i].Groups[1].Value) * 10 + int.Parse(m4[i].Groups[2].Value);
            if (y < DateTime.Today.Year - 5) continue;
            var r = Build(y, int.Parse(m4[i].Groups[3].Value), int.Parse(m4[i].Groups[4].Value));
            if (r != null) return r;
        }

        // 年份漏写 0 且多一个点：2.26.6.9 → 2026-06-09
        var m5 = Regex.Matches(compact, @"(?<!\d)2[.\-/年](\d{2})[.\-/月](\d{1,2})[.\-/日](\d{1,2})(?!\d)");
        for (int i = m5.Count - 1; i >= 0; i--)
        {
            var y = 2000 + int.Parse(m5[i].Groups[1].Value);
            if (y < DateTime.Today.Year - 5) continue;
            var r = Build(y, int.Parse(m5[i].Groups[2].Value), int.Parse(m5[i].Groups[3].Value));
            if (r != null) return r;
        }

        // 三位年份：226年9月1日（首个 2 后漏写 0）→ 2026-09-01；206年 → 2026-xx-xx
        var m3 = Regex.Matches(compact, @"(?<!\d)(2\d{2})[年.\-/](\d{1,2})[月.\-/](\d{1,2})(?!\d)");
        for (int i = m3.Count - 1; i >= 0; i--)
        {
            var y3 = int.Parse(m3[i].Groups[1].Value);
            int y = y3 is >= 200 and <= 209 ? 2020 + (y3 - 200) : 2000 + y3 % 100;
            if (y < DateTime.Today.Year - 5) continue;
            var r = Build(y, int.Parse(m3[i].Groups[2].Value), int.Parse(m3[i].Groups[3].Value));
            if (r != null) return r;
        }

        // 两位年份：26.5.9 / 26年5月9日；只接受近年份，避免把 16.1.1 读成 2016
        var m2 = Regex.Matches(compact, @"(?<!\d)(\d{2})[.\-/年](\d{1,2})[.\-/月](\d{1,2})(?!\d)");
        for (int i = m2.Count - 1; i >= 0; i--)
        {
            int yy = int.Parse(m2[i].Groups[1].Value);
            if (yy < DateTime.Today.Year % 100 - 5 || yy > DateTime.Today.Year % 100 + 1) continue;
            var r = Build(2000 + yy, int.Parse(m2[i].Groups[2].Value), int.Parse(m2[i].Groups[3].Value));
            if (r != null) return r;
        }

        // 无分隔：20260901
        var m8 = Regex.Match(compact, @"(20\d{2})(\d{2})(\d{2})");
        if (m8.Success)
        {
            var r = Build(int.Parse(m8.Groups[1].Value), int.Parse(m8.Groups[2].Value), int.Parse(m8.Groups[3].Value));
            if (r != null) return r;
        }

        // 手写日期读不出时，用印刷单号里的 yymmdd 兜底：单号 N2606092 → 2026-06-09
        var slip = Regex.Match(compact, @"单号[^0-9]{0,6}[A-Z]{0,4}(\d{2})(\d{1,2})(\d{1,2})");
        if (slip.Success)
        {
            var r = Build(2000 + int.Parse(slip.Groups[1].Value),
                int.Parse(slip.Groups[2].Value), int.Parse(slip.Groups[3].Value));
            if (r != null) return r;
        }
        return null;
    }

    private static void ExtractFields(PageFact fact, OcrPage ocr, string batchStation)
    {
        var text = ocr.FullText;
        if (string.IsNullOrWhiteSpace(text)) return;
        var compact = Regex.Replace(text, @"\s+", "");

        var seqLine = ocr.Lines.FirstOrDefault(l => l.Words.Count > 0 && l.Words.Min(w => w.Y) < 0.20 &&
            Regex.IsMatch(Regex.Replace(l.Text, @"\s+", ""), @"第[_\-\s]*([0-9]{1,3}|[一二三四五六七八九十百]{1,4})[_\-\s]*次"));
        if (seqLine != null) fact.SeqNo = ParseSeq(seqLine.Text);
        if (fact.SeqNo == null) fact.SeqNo = ParseSeq(compact);

        fact.DocDate = ParseDate(compact);

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

        // 站点：优先用批次里填的；没填就从“项目名称/使用地点”附近按滨海快线站名表归一，
        // 不再直接把 OCR 原文（如“法线”“海”）当站点。
        fact.Station = StationCatalog.Match(batchStation) ?? batchStation;
        if (string.IsNullOrWhiteSpace(fact.Station))
            fact.Station = ExtractStation(ocr, text) ?? "";
    }

    private static string? ExtractStation(OcrPage ocr, string fullText)
    {
        var labelRx = new Regex(@"项目名称|使用地点|工程名称|安装地点");
        var header = ocr.Lines.Where(l => l.Words.Count > 0 && l.Words.Min(w => w.Y) < 0.35).ToList();
        for (int i = 0; i < header.Count; i++)
        {
            if (!labelRx.IsMatch(header[i].Text)) continue;
            var m = StationCatalog.MatchLine(header[i].Text);
            if (m != null) return m;
            // 标签在上一行、值在下一行
            if (i + 1 < header.Count)
            {
                m = StationCatalog.MatchLine(header[i + 1].Text);
                if (m != null) return m;
            }
        }
        return StationCatalog.Match(fullText);
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
        if (tpl.Type == DocType.SelfCheckC2)
        {
            // C.2 的安全管理员签名写在标签同一行，签名位置随版式上下浮动
            // OCR 常把“全”读成“金”，用更短的“管理人员”定位，y 限定在底部签名区
            var cl = FindLine(ocr, "管理人员", 0.74, 0.98);
            if (cl != null) cust = PickInkWindow(bmp, cl.Value.Top, 0.25, 0.30, 0.050, new[] { -0.045, -0.030, -0.015, 0.0, 0.012 });
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

    /// <summary>用同一行“第”“次”两个词框夹出中间的空白区，作为手写数字的精确裁切。</summary>
    private static Rect? SeqGapRect(OcrPage ocr)
    {
        foreach (var line in ocr.Lines)
        {
            if (line.Words.Count < 2) continue;
            var ws = line.Words.OrderBy(w => w.X).ToList();
            int iFirst = ws.FindIndex(w => w.Text.Contains('第'));
            int iLast = ws.FindLastIndex(w => w.Text.Contains('次'));
            if (iFirst < 0 || iLast <= iFirst) continue;
            double x0 = ws[iFirst].X + ws[iFirst].W + 0.004;
            double x1 = ws[iLast].X - 0.004;
            if (x1 - x0 < 0.012) continue;
            double top = Math.Min(ws[iFirst].Y, ws[iLast].Y) - 0.006;
            double bottom = Math.Max(ws[iFirst].Y + ws[iFirst].H, ws[iLast].Y + ws[iLast].H) + 0.006;
            return new Rect(x0, Math.Max(0, top), x1 - x0, Math.Min(1 - Math.Max(0, top), bottom - top));
        }
        return null;
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




