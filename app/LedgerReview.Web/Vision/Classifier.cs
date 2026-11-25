using LedgerReview.Domain;
using SkiaSharp;

namespace LedgerReview.Vision;

public sealed record ClassifyResult(DocType Type, double Confidence, string Evidence);

public sealed class Classifier
{
    private readonly TemplateStore _store;
    public Classifier(TemplateStore store) => _store = store;

    public ClassifyResult Classify(SKBitmap page, OcrPage ocr)
    {
        var top = string.Join("", ocr.Lines
            .Where(l => l.Words.Count == 0 || l.Words.Min(w => w.Y) < 0.24)
            .Select(l => l.Text))
            .Replace(" ", "").Replace("\u3000", "");

        if (top.Contains("签名字迹")) return new(DocType.SignatureBook, 0.97, "标题：人员签名字迹表");
        if (top.Contains("施工作业卡控")) return new(DocType.ControlCard, 0.97, "标题：施工作业卡控表");
        if (top.Contains("监督记录表") || top.Contains("F.1") || top.Contains("F．1"))
            return new(DocType.SupervisionF1, 0.96, "标题：电梯维护保养监督记录表");
        if (top.Contains("定期自行检查") || top.Contains("C.2") || top.Contains("C．2"))
            return new(DocType.SelfCheckC2, 0.96, "标题：定期自行检查记录表");
        if (top.Contains("服务记录") || top.Contains("ServiceDocket") || top.Contains("ServiceDocke"))
            return new(DocType.Repair, 0.95, "标题：Service Docket 服务记录");

        bool chuiti = top.Contains("曳引") || top.Contains("强制驱动") || top.Contains("垂梯");
        bool futi = top.Contains("自动扶梯") || top.Contains("人行道") || top.Contains("扶梯");
        string period = "";
        if (top.Contains("半月")) period = "半月";
        else if (top.Contains("季度")) period = "季度";
        else if (top.Contains("半年")) period = "半年";
        else if (top.Contains("年度")) period = "年度";

        if ((chuiti || futi) && period != "")
        {
            var type = (chuiti, period) switch
            {
                (true, "半月") => DocType.ChuitiHalfMonth,
                (true, "季度") => DocType.ChuitiQuarter,
                (true, "半年") => DocType.ChuitiHalfYear,
                (true, "年度") => DocType.ChuitiYear,
                (false, "半月") => DocType.FutiHalfMonth,
                (false, "季度") => DocType.FutiQuarter,
                (false, "半年") => DocType.FutiHalfYear,
                _ => DocType.FutiYear
            };
            return new(type, 0.96, $"标题：{(chuiti ? "垂梯" : "扶梯")}·{period}");
        }

        var thumb = ImageOps.ThumbGray(page);
        var (t, score) = _store.BestTemplate(thumb);
        if (t != DocType.Unknown && score >= 0.34)
        {
            // Family known but period unreadable: constrain to family
            if (chuiti || futi)
            {
                var family = chuiti ? "CHUITI" : "FUTI";
                var candidates = Catalog.All.Where(x => x.Family == family && x.TemplateAsset != null).ToArray();
                // We cannot rescore without refs exposed per type; fall back to best overall if same family.
                var fam = Catalog.Find(t);
                if (fam?.Family == family)
                    return new(t, Math.Clamp(0.45 + score * 0.4, 0, 0.9), $"版式匹配 {score:0.00}（标题周期不清）");
            }
            return new(t, Math.Clamp(0.42 + score * 0.45, 0, 0.92), $"版式匹配 {score:0.00}");
        }

        if (chuiti || futi)
            return new(DocType.Unknown, 0.35, $"仅识别到设备族（{(chuiti ? "垂梯" : "扶梯")}），周期不清");

        return new(DocType.Unknown, 0.15, "未识别标题与版式");
    }
}
