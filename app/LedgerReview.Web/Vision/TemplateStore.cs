using LedgerReview.Domain;
using LedgerReview.Infrastructure;
using LedgerReview.Pdf;
using SkiaSharp;

namespace LedgerReview.Vision;

public sealed class TemplateStore
{
    private readonly Dictionary<DocType, byte[]> _refs = new();
    private readonly ILogger<TemplateStore> _log;
    private readonly PdfService _pdf;
    private readonly Workspace _ws;

    public TemplateStore(PdfService pdf, Workspace ws, ILogger<TemplateStore> log)
    {
        _pdf = pdf; _ws = ws; _log = log;
        Build();
    }

    private void Build()
    {
        var dir = Path.Combine(_ws.Root, "templates", "refs");
        Directory.CreateDirectory(dir);
        foreach (var t in Catalog.All.Where(x => x.TemplateAsset != null))
        {
            try
            {
                var asset = Path.Combine(_ws.AssetsRoot, "templates", t.TemplateAsset!);
                if (!File.Exists(asset)) continue;
                var png = Path.Combine(dir, t.Code + ".png");
                if (!File.Exists(png))
                {
                    if (asset.EndsWith(".png", StringComparison.OrdinalIgnoreCase)) File.Copy(asset, png, true);
                    else _pdf.RenderPage(asset, 0, 90, png);
                }
                using var bmp = SKBitmap.Decode(png);
                if (bmp == null) continue;
                _refs[t.Type] = ImageOps.ThumbGray(bmp);
            }
            catch (Exception ex) { _log.LogWarning(ex, "模板参考图生成失败：{code}", t.Code); }
        }
        _log.LogInformation("模板参考图：{n} 个", _refs.Count);
    }

    public (DocType Type, double Score) BestTemplate(byte[] thumb)
    {
        DocType best = DocType.Unknown;
        double bestScore = 0;
        foreach (var (type, r) in _refs)
        {
            double s = ImageOps.Ncc(thumb, r);
            if (s > bestScore) { bestScore = s; best = type; }
        }
        return (best, bestScore);
    }

    public int Count => _refs.Count;
}

