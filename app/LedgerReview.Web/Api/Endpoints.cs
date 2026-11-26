using System.Security.Cryptography;
using LedgerReview.Domain;
using LedgerReview.Infrastructure;
using LedgerReview.Pdf;
using LedgerReview.Pipeline;
using LedgerReview.Vision;
using Microsoft.AspNetCore.Http.Features;
using SkiaSharp;

namespace LedgerReview.Api;

public static class Endpoints
{
    public static void Map(WebApplication app)
    {
        app.MapGet("/api/status", (Workspace ws, OcrService ocr, SignatureEngine sig, TemplateStore tpl) => Results.Ok(new
        {
            app = "台账审查工具",
            version = "1.1.0",
            workspace = ws.Root,
            ocr = ocr.Status,
            ocrAvailable = ocr.Available,
            signature = sig.Status,
            signatureReady = sig.Ready,
            signatureAccuracy = sig.LooAccuracy,
            signatureRefs = sig.RefCount,
            templates = tpl.Count
        }));

        app.MapGet("/api/batches", (Db db) => Results.Ok(db.GetBatches()));

        app.MapPost("/api/batches", async (HttpRequest req, Db db, Workspace ws, PdfService pdf, Processor proc, ILogger<Program> log) =>
        {
            var form = await req.ReadFormAsync();
            var station = form["station"].ToString().Trim();
            var equipment = form["equipment"].ToString().Trim();
            var name = form["name"].ToString().Trim();
            if (string.IsNullOrWhiteSpace(name)) name = (string.IsNullOrWhiteSpace(station) ? "未命名批次" : station) + " " + DateTime.Now.ToString("yyyy-MM-dd HH:mm");
            if (form.Files.Count == 0) return Results.BadRequest(new { error = "没有收到文件" });

            var batchId = Guid.NewGuid().ToString("N");
            db.CreateBatch(batchId, name, station, equipment);
            ws.EnsureBatchDirs(batchId);

            int totalPages = 0, fileCount = 0;
            foreach (var f in form.Files)
            {
                var fileId = Guid.NewGuid().ToString("N");
                var fileName = Path.GetFileName(f.FileName);
                var safe = Workspace.SafeName(fileName, "file.pdf");
                var path = Path.Combine(ws.SourceDir(batchId), $"{fileId}_{safe}");
                await using (var fs = File.Create(path)) await f.CopyToAsync(fs);

                var hintMatch = System.Text.RegularExpressions.Regex.Match(fileName, @"([A-Za-z]{1,4}\s*\d{2,5}\s*[-–—]\s*\d{3,6}\s*[-–—]\s*\d{2,4})");
                var hint = hintMatch.Success ? System.Text.RegularExpressions.Regex.Replace(hintMatch.Groups[1].Value, @"\s+", "") : "";

                string sha;
                using (var s = File.OpenRead(path))
                    sha = Convert.ToHexString(SHA256.HashData(s));

                int pages = 0;
                string status = "ok"; string? error = null;
                try { pages = pdf.PageCount(path); }
                catch (Exception ex) { status = "failed"; error = ex.Message; log.LogWarning(ex, "PDF 打开失败 {f}", fileName); }

                db.AddSourceFile(fileId, batchId, fileName, path, hint, sha, pages, status);
                if (status == "ok")
                {
                    for (int i = 1; i <= pages; i++) db.AddPage(Guid.NewGuid().ToString("N"), batchId, fileId, i);
                    totalPages += pages;
                    fileCount++;
                }
            }

            db.UpdateBatch(batchId, totalPages, 0, totalPages > 0 ? "pending" : "failed", totalPages == 0 ? "没有可处理的页面" : null);
            if (totalPages > 0) proc.Enqueue(batchId);
            return Results.Ok(new { batchId, files = fileCount, pages = totalPages });
        }).DisableAntiforgery();

        app.MapGet("/api/batches/{id}", (string id, Db db) =>
        {
            var b = db.GetBatch(id);
            if (b == null) return Results.NotFound();
            var pages = db.GetPages(id, null, false);
            var files = db.GetFiles(id);
            var findings = db.GetFindings(id, null, "open", 1000);
            var donePages = pages.Where(p => p.Status == "done").ToList();
            var byType = donePages.GroupBy(p => string.IsNullOrEmpty(p.DocType) ? "Unknown" : p.DocType)
                .Select(g => new { type = g.Key, name = TypeName(g.Key), count = g.Count(),
                    blanks = g.Sum(p => p.SeriousFindings) }).OrderByDescending(x => x.count);
            return Results.Ok(new { batch = b, files, pages, findings, stats = new
            {
                total = pages.Count,
                done = donePages.Count,
                pending = pages.Count(p => p.Status == "pending"),
                failed = pages.Count(p => p.Status == "failed"),
                // 只有已处理完的页才计入“未识别”，排队中的页不能算成识别失败。
                unknown = donePages.Count(p => string.IsNullOrEmpty(p.DocType) || p.DocType == "Unknown"),
                serious = findings.Count(f => f.Severity == "Serious"),
                warning = findings.Count(f => f.Severity == "Warning"),
                open = findings.Count(f => f.Status == "open")
            }, byType });
        });

        app.MapGet("/api/batches/{id}/pages", (string id, string? type, bool? problem, Db db) =>
            Results.Ok(db.GetPages(id, type, problem == true)));

        app.MapGet("/api/batches/{id}/findings", (string id, string? severity, string? status, Db db) =>
            Results.Ok(db.GetFindings(id, severity, status)));

        app.MapGet("/api/pages/{id}", (string id, Db db) =>
        {
            var d = db.GetPageDetail(id);
            return d == null ? Results.NotFound() : Results.Ok(d);
        });

        app.MapGet("/api/pages/{id}/image", (string id, Db db) =>
        {
            var p = db.GetPageImage(id, false);
            return FileOr404(p, "image/png");
        });

        app.MapGet("/api/pages/{id}/thumb", (string id, Db db) =>
        {
            var p = db.GetPageImage(id, true);
            return FileOr404(p, "image/jpeg");
        });

        app.MapGet("/api/matches/{id}/crop", (long id, Db db) =>
        {
            var p = db.GetMatchCrop(id);
            return FileOr404(p, "image/png");
        });

        app.MapGet("/api/persons", (Db db) => Results.Ok(db.GetPersons()));

        app.MapPost("/api/findings/{id}/status", (long id, FindingStatus body, Db db) =>
        {
            db.UpdateFindingStatus(id, body.Status);
            return Results.Ok(new { ok = true });
        });

        // 人工修正“第 N 次”：写回结果，并把该数字切片入库，供后续识别越用越准。
        app.MapPost("/api/pages/{id}/seq", (string id, SeqFix body, Db db, Workspace ws, DigitRecognizer digits) =>
        {
            var d = db.GetPageDetail(id);
            if (d == null) return Results.NotFound();
            if (body.Value is < 1 or > 300) return Results.BadRequest(new { error = "次数应在 1–300 之间" });
            if (string.IsNullOrWhiteSpace(d.DocId)) return Results.BadRequest(new { error = "该页尚未完成识别" });
            db.SetDocumentSeq(d.DocId, body.Value);
            int learned = 0;
            try
            {
                var cropDir = ws.CropsDir(d.BatchId);
                var file = Directory.Exists(cropDir)
                    ? Directory.GetFiles(cropDir, $"*_p{d.PageNo:D3}_*_seqgap.png").FirstOrDefault()
                    : null;
                if (file != null)
                {
                    using var crop = SKBitmap.Decode(file);
                    if (crop != null) learned = digits.LearnFrom(crop, body.Value, ws);
                }
            }
            catch { }
            return Results.Ok(new { ok = true, learned });
        }).DisableAntiforgery();

        app.MapPost("/api/matches/{id}/confirm", (long id, MatchConfirm body, Db db) =>
        {
            var person = string.IsNullOrEmpty(body.PersonId) ? null : db.GetPerson(body.PersonId!);
            var verdict = body.Verdict;
            if (person != null) verdict = "MATCH";
            db.ConfirmMatch(id, verdict, person?.PersonId, person?.PersonName);
            return Results.Ok(new { ok = true });
        });

        app.MapPost("/api/batches/{id}/export", (string id, ExportRequest body, Db db, Workspace ws, Exporter exporter) =>
        {
            var b = db.GetBatch(id);
            if (b == null) return Results.NotFound();
            var outDir = string.IsNullOrWhiteSpace(body.OutputDir)
                ? Path.Combine(ws.Root, "export", $"{Workspace.SafeName(b.Name)}_{DateTime.Now:yyyyMMdd_HHmm}")
                : body.OutputDir!;
            var r = exporter.Export(id, outDir);
            return Results.Ok(r);
        });

        app.MapGet("/api/batches/{id}/export/default", (string id, Db db, Workspace ws) =>
        {
            var b = db.GetBatch(id);
            var dir = Path.Combine(ws.Root, "export", $"{Workspace.SafeName(b?.Name ?? id)}_{DateTime.Now:yyyyMMdd_HHmm}");
            return Results.Ok(new { dir });
        });

        app.MapPost("/api/batches/{id}/reprocess", (string id, bool? all, Db db, Processor proc) =>
        {
            var b = db.GetBatch(id);
            if (b == null) return Results.NotFound();
            if (all == true)
            {
                db.ResetPages(id);
                db.UpdateBatch(id, b.TotalPages, 0, "pending");
            }
            db.SetBatchStatus(id, "pending");
            proc.Enqueue(id);
            return Results.Ok(new { ok = true });
        });

        app.MapGet("/api/dict/types", () => Results.Ok(Catalog.All.Select(t => new
        {
            code = t.Code, type = t.Type.ToString(), name = t.DisplayName, family = t.Family,
            period = t.Period, items = t.ItemCount
        })));
    }

    private static string TypeName(string code)
        => Enum.TryParse<DocType>(code, out var t) ? (Catalog.Find(t)?.DisplayName ?? "未识别") : "未识别";

    private static IResult FileOr404(string? path, string contentType)
        => string.IsNullOrEmpty(path) || !File.Exists(path) ? Results.NotFound() : Results.File(path, contentType);
}

public sealed record FindingStatus(string Status);
public sealed record MatchConfirm(string? PersonId, string Verdict);
public sealed record ExportRequest(string? OutputDir);
public sealed record SeqFix(int Value);
