using LedgerReview.Domain;
using LedgerReview.Infrastructure;
using LedgerReview.Pdf;

namespace LedgerReview.Pipeline;

public sealed record ExportResult(int Exported, int Skipped, int Failed, List<string> Errors, string OutputDir);

public sealed class Exporter
{
    private readonly Db _db;
    private readonly PdfService _pdf;
    private readonly ILogger<Exporter> _log;

    public Exporter(Db db, PdfService pdf, ILogger<Exporter> log) { _db = db; _pdf = pdf; _log = log; }

    public ExportResult Export(string batchId, string outputDir)
    {
        var rows = _db.GetExportRows(batchId);
        int ok = 0, skip = 0, fail = 0;
        var errors = new List<string>();
        Directory.CreateDirectory(outputDir);

        foreach (var r in rows)
        {
            try
            {
                var t = Enum.TryParse<DocType>(r.DocType, out var dt) ? Catalog.Find(dt) : Catalog.Find(r.DocType);
                var typeName = Workspace.SafeName(t?.DisplayName, "未识别");
                var station = Workspace.SafeName(r.Station, "未识别站点");
                var equip = Workspace.SafeName(r.EquipmentId, "未识别设备");
                var family = t?.Family == "CHUITI" ? "垂梯" : t?.Family == "FUTI" ? "扶梯" : "其他";
                var date = string.IsNullOrWhiteSpace(r.DocDate) ? "未识别日期" : r.DocDate;
                var dir = Path.Combine(outputDir, "福州地铁滨海快线", station, family, equip, typeName);
                Directory.CreateDirectory(dir);
                var name = $"{station}_{equip}_{typeName}_{date}_P{r.PageNo:D3}.pdf";
                if (name.Length > 180) name = name[..170] + $"_{r.PageNo:D3}.pdf";
                var path = Path.Combine(dir, name);
                if (File.Exists(path) && new FileInfo(path).Length > 0) { skip++; continue; }
                _pdf.ExtractPage(r.SourcePath, r.PageNo - 1, path);
                ok++;
            }
            catch (Exception ex)
            {
                fail++;
                errors.Add($"第 {r.PageNo} 页：{ex.Message}");
                _log.LogWarning(ex, "导出失败 第 {p} 页", r.PageNo);
            }
        }
        return new ExportResult(ok, skip, fail, errors.Take(20).ToList(), outputDir);
    }
}

