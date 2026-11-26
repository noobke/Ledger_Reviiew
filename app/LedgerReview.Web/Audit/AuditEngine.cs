using System.Text.RegularExpressions;
using LedgerReview.Domain;
using LedgerReview.Infrastructure;

namespace LedgerReview.Audit;

public sealed class AuditEngine
{
    private static readonly Regex EquipRx = new(@"^[A-Za-z]{1,4}\d{2,6}(?:[-–—]\d{2,6}){0,2}(?:\([A-Za-z0-9]{1,6}\))?$", RegexOptions.Compiled);
    private static readonly Regex WbsRx = new(@"^[A-Z]{1,4}\d{2,5}-\d{3,6}-\d{2,4}$", RegexOptions.Compiled);

    public List<FindingDraft> AuditPage(PageFact f, FormTemplate? t, double quality, bool ocrAvailable)
    {
        var list = new List<FindingDraft>();

        if (f.Type == DocType.Unknown)
            list.Add(new FindingDraft { RuleCode = "F2", Severity = Severity.Warning, Message = "未识别单据类型，未执行类型专属检查" });

        if (quality < 0.12)
            list.Add(new FindingDraft { RuleCode = "F1", Severity = Severity.Serious, Message = $"图像质量低（{quality:0.00}），可能无法辨认" });

        if (t == null) return list;

        // A1 检查项完整性
        if (t.ItemCount > 0)
        {
            if (f.Checks.Count == 0)
                list.Add(new FindingDraft { RuleCode = "A1", Severity = Severity.Serious, Message = $"未检测到检查项（应为 {t.ItemCount} 项）" });
            else
            {
                var blanks = f.Checks.Where(c => c.Result == "BLANK").Select(c => c.ItemNo).ToList();
                if (blanks.Count > 0)
                {
                    var first = f.Checks.First(c => c.Result == "BLANK");
                    var show = string.Join("、", blanks.Take(8)) + (blanks.Count > 8 ? " 等" : "");
                    list.Add(new FindingDraft
                    {
                        RuleCode = "A1", Severity = Severity.Serious,
                        Message = $"第 {show} 项未填写（共 {blanks.Count} 项空白）",
                        Rect = first.Rect
                    });
                }
            }
        }

        // A4 应 N/A 项
        if (t.NaItems.Length > 0 && f.Checks.Count > 0)
        {
            var filledNa = f.Checks.Where(c => t.NaItems.Contains(c.ItemNo) && c.Result == "FILLED")
                .Select(c => c.ItemNo).ToList();
            if (filledNa.Count > 0)
                list.Add(new FindingDraft
                {
                    RuleCode = "A4", Severity = Severity.Info,
                    Message = $"第 {string.Join("、", filledNa)} 项模板标注“无此项”，但填了勾选/实测值，请确认"
                });
            // 现场会按电梯配置写 N/A（不只模板标注的那几项），这类“模板外 N/A”不提示，避免整册几十条噪声。
        }

        // A2 表头必填
        if (ocrAvailable)
        {
            if (string.IsNullOrEmpty(f.EquipmentId))
                list.Add(new FindingDraft { RuleCode = "A2", Severity = Severity.Info, Message = "未识别到设备编号（手写字段可能无法识别，请人工核对）" });
            if (string.IsNullOrEmpty(f.DocDate))
                list.Add(new FindingDraft { RuleCode = "A2", Severity = Severity.Warning, Message = "未识别到日期（手写字段可能无法识别，请人工核对）" });
            if (f.SeqNo == null && (t.Family == "CHUITI" || t.Family == "FUTI"))
                list.Add(new FindingDraft { RuleCode = "A2", Severity = Severity.Info, Message = "未识别到第 N 次（手写字段可能无法识别）" });
        }

        // B1 未来日期：手写“10”常被读成“12”，留 45 天余量，超过才判异常
        if (DateTime.TryParse(f.DocDate, out var d) && d.Date > DateTime.Today.AddDays(45))
            list.Add(new FindingDraft { RuleCode = "B1", Severity = Severity.Serious, Message = $"日期晚于今天 45 天以上：{d:yyyy-MM-dd}（手写日期，请核对原图）" });

        // C2 设备号格式
        if (!string.IsNullOrEmpty(f.EquipmentId))
        {
            var eqNorm = f.EquipmentId.Replace(" ", "").Replace('（', '(').Replace('）', ')').ToUpperInvariant();
            if (!EquipRx.IsMatch(eqNorm))
                list.Add(new FindingDraft { RuleCode = "C2", Severity = Severity.Warning, Message = $"设备编号格式异常：{f.EquipmentId}" });
        }

        // A3 / E1 漏签
        foreach (var s in f.Signatures.Where(s => !s.HasInk))
        {
            string label = s.Role switch
            {
                "CUSTOMER" => "客户/使用单位签字",
                "SUPERVISOR" => "安全管理人员签字",
                _ => "保养人员签字"
            };
            list.Add(new FindingDraft { RuleCode = "A3", Severity = Severity.Serious, Message = label + "缺失", Rect = s.Rect });
        }

        // E2 归属与填写姓名不一致（仅在已有高置信判定时提示）
        foreach (var s in f.Signatures.Where(s => s.Verdict == "MATCH" && !string.IsNullOrEmpty(s.PersonName)))
        {
            if (!string.IsNullOrEmpty(f.OcrText) && f.OcrText.Contains("姓名"))
            {
                // 保守：不在 OCR 文本中做姓名强匹配，避免误报
            }
        }

        return list;
    }

    public int CrossAudit(string batchId, Db db, ILogger log)
    {
        db.ClearCrossFindings(batchId);
        var docs = db.GetDocs(batchId);
        int added = 0;

        void Add(DocRow d, FindingDraft f)
        {
            db.AddFinding(batchId, d.PageId, d.Id, f);
            added++;
        }

        // C1 册内设备号不一致（只比较 WBS 三段式编号；本地短编号手写 OCR 易变体，不做册内比对）
        foreach (var g in docs.GroupBy(d => d.SourceFileId))
        {
            var ids = g.Select(d => d.EquipmentId)
                       .Where(x => !string.IsNullOrWhiteSpace(x) && WbsRx.IsMatch(x))
                       .Distinct().ToList();
            if (ids.Count > 1)
                Add(g.First(), new FindingDraft
                {
                    RuleCode = "C1", Severity = Severity.Serious,
                    Message = $"同一册出现多个设备编号：{string.Join(" / ", ids)}"
                });
        }

        // B2 第 N 次与日期矛盾
        foreach (var g in docs.Where(d => !string.IsNullOrWhiteSpace(d.EquipmentId) && d.SeqNo > 0)
                              .GroupBy(d => d.EquipmentId))
        {
            var seq = g.Where(d => DateTime.TryParse(d.DocDate, out _))
                       .OrderBy(d => d.SeqNo).ToList();
            for (int i = 1; i < seq.Count; i++)
            {
                if (!DateTime.TryParse(seq[i - 1].DocDate, out var prev) || !DateTime.TryParse(seq[i].DocDate, out var cur)) continue;
                if (cur >= prev) continue;
                // 手写“第 N 次”经常把 13 读成 3、21 读成 2；只有日期差 ≥30 天且次数差 ≥5 才提示，
                // 否则全是 OCR 噪声，宁可交给人工看原图。
                if ((prev - cur).TotalDays < 30 || Math.Abs(seq[i].SeqNo - seq[i - 1].SeqNo) < 5) continue;
                Add(seq[i], new FindingDraft
                {
                    RuleCode = "B2", Severity = Severity.Warning,
                    Message = $"第 {seq[i].SeqNo} 次日期 {cur:yyyy-MM-dd} 早于第 {seq[i - 1].SeqNo} 次 {prev:yyyy-MM-dd}（次数为手写识别，请核对）"
                });
            }
        }

        // B7 保养单与监督记录表日期不一致（同设备，日期相差 1 天内配对）
        var maint = docs.Where(d => d.DocType.StartsWith("Chuiti") || d.DocType.StartsWith("Futi")).ToList();
        var f1 = docs.Where(d => d.DocType == nameof(DocType.SupervisionF1)).ToList();
        foreach (var m in maint)
        {
            if (!DateTime.TryParse(m.DocDate, out var md)) continue;
            var near = f1.Where(x => x.EquipmentId == m.EquipmentId && DateTime.TryParse(x.DocDate, out var fd) && Math.Abs((fd - md).TotalDays) <= 1).ToList();
            foreach (var x in near)
            {
                if (DateTime.TryParse(x.DocDate, out var fd) && fd.Date != md.Date)
                    Add(m, new FindingDraft
                    {
                        RuleCode = "B7", Severity = Severity.Warning,
                        Message = $"保养单日期 {md:yyyy-MM-dd} 与监督记录表日期 {fd:yyyy-MM-dd} 不一致"
                    });
            }
        }

        // E3 同一事件保养人员签名不一致
        var matches = db.GetMatchedPersons(batchId).GroupBy(x => x.DocumentId)
            .ToDictionary(g => g.Key, g => g.Select(x => x.PersonId).Distinct().OrderBy(x => x).ToList());
        foreach (var g in maint.Where(m => !string.IsNullOrWhiteSpace(m.EquipmentId) && !string.IsNullOrWhiteSpace(m.DocDate))
                     .GroupBy(m => $"{m.EquipmentId}|{m.DocDate}"))
        {
            var m = g.First();
            var f1Same = f1.FirstOrDefault(x => x.EquipmentId == m.EquipmentId && x.DocDate == m.DocDate);
            if (f1Same == null) continue;
            if (matches.TryGetValue(m.Id, out var a) && matches.TryGetValue(f1Same.Id, out var b) && a.Count > 0 && b.Count > 0 && !a.Intersect(b).Any())
                Add(m, new FindingDraft
                {
                    RuleCode = "E3", Severity = Severity.Warning,
                    Message = "同一事件中，保养单与监督记录表的保养人员签名不一致"
                });
        }

        // B4 同站同次日期异类
        foreach (var g in docs.Where(d => !string.IsNullOrWhiteSpace(d.Station) && d.SeqNo > 0 && DateTime.TryParse(d.DocDate, out _))
                     .GroupBy(d => $"{d.Station}|{d.DocType}|{d.SeqNo}"))
        {
            if (g.Count() < 3) continue;
            var byDate = g.GroupBy(d => d.DocDate).OrderByDescending(x => x.Count()).ToList();
            var top = byDate[0];
            if (top.Count() >= g.Count() * 0.6)
            {
                foreach (var d in g.Where(x => x.DocDate != top.Key))
                    Add(d, new FindingDraft
                    {
                        RuleCode = "B4", Severity = Severity.Warning,
                        Message = $"同站同一次保养日期异类：{d.DocDate}，同组多数为 {top.Key}（{top.Count()}/{g.Count()}）"
                    });
            }
        }

        log.LogInformation("跨单审查完成：新增 {n} 条", added);
        return added;
    }
}

