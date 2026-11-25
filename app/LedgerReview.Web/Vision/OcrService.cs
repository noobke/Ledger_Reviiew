using LedgerReview.Domain;
using LedgerReview.Infrastructure;
using RapidOcrNet;
using SkiaSharp;
using Windows.Graphics.Imaging;
using Windows.Media.Ocr;
using Windows.Storage.Streams;
using DomainOcrLine = LedgerReview.Domain.OcrLine;
using DomainOcrWord = LedgerReview.Domain.OcrWord;

namespace LedgerReview.Vision;

public sealed class OcrService
{
    private readonly RapidOcr? _rapid;
    private readonly OcrEngine? _win;
    public bool Available => _rapid != null || _win != null;
    public string Status { get; } = "未初始化";

    public OcrService(Workspace ws, ILogger<OcrService> log)
    {
        try
        {
            var det = Path.Combine(AppContext.BaseDirectory, "models", "v5", "ch_PP-OCRv5_mobile_det.onnx");
            var cls = Path.Combine(AppContext.BaseDirectory, "models", "v5", "ch_PP-LCNet_x0_25_textline_ori_cls_mobile.onnx");
            var rec = Path.Combine(ws.AssetsRoot, "models", "ch_PP-OCRv5_rec_mobile.onnx");
            var dict = Path.Combine(ws.AssetsRoot, "models", "ppocrv5_dict.txt");
            if (File.Exists(det) && File.Exists(cls) && File.Exists(rec) && File.Exists(dict))
            {
                _rapid = new RapidOcr();
                _rapid.InitModels(det, cls, rec, dict);
                Status = "PP-OCRv5 中文（RapidOcrNet / ONNX）";
                log.LogInformation(Status);
            }
        }
        catch (Exception ex)
        {
            Status = "PP-OCRv5 初始化失败，回退 Windows OCR：" + ex.Message;
            log.LogWarning(ex, Status);
        }

        if (_rapid == null)
        {
            try
            {
                var langs = OcrEngine.AvailableRecognizerLanguages;
                var zh = langs.FirstOrDefault(l => l.LanguageTag.StartsWith("zh", StringComparison.OrdinalIgnoreCase));
                _win = zh != null ? OcrEngine.TryCreateFromLanguage(zh) : OcrEngine.TryCreateFromUserProfileLanguages();
                Status = _win == null ? "OCR 不可用" : $"Windows OCR：{zh?.DisplayName ?? "用户默认语言"}";
                log.LogInformation(Status);
            }
            catch (Exception ex)
            {
                Status = "Windows OCR 初始化失败：" + ex.Message;
                log.LogWarning(ex, Status);
            }
        }
    }

    public async Task<OcrPage> RecognizeAsync(string pngPath, CancellationToken ct = default)
    {
        if (_rapid != null)
        {
            try
            {
                using var bmp = SKBitmap.Decode(pngPath);
                if (bmp != null)
                {
                    var result = await Task.Run(() => _rapid.Detect(bmp, RapidOcrOptions.Default), ct);
                    return Map(result, bmp.Width, bmp.Height);
                }
            }
            catch
            {
                // fall through to Windows OCR
            }
        }
        return await WindowsRecognizeAsync(pngPath, ct);
    }

    private static OcrPage Map(RapidOcrNet.OcrResult result, int width, int height)
    {
        var lines = new List<DomainOcrLine>();
        foreach (var b in result.TextBlocks)
        {
            if (string.IsNullOrWhiteSpace(b.Text) || b.BoxPoints is not { Length: 4 }) continue;
            int minX = b.BoxPoints.Min(p => p.X), maxX = b.BoxPoints.Max(p => p.X);
            int minY = b.BoxPoints.Min(p => p.Y), maxY = b.BoxPoints.Max(p => p.Y);
            var w = new DomainOcrWord(b.Text,
                (double)minX / width, (double)minY / height,
                (double)(maxX - minX) / width, (double)(maxY - minY) / height);
            lines.Add(new DomainOcrLine(b.Text, new List<DomainOcrWord> { w }));
        }
        return new OcrPage(lines, width, height);
    }

    private async Task<OcrPage> WindowsRecognizeAsync(string pngPath, CancellationToken ct)
    {
        if (_win == null || !File.Exists(pngPath)) return OcrPage.Empty;
        try
        {
            var bytes = await File.ReadAllBytesAsync(pngPath, ct);
            using var ras = new InMemoryRandomAccessStream();
            using (var writer = new DataWriter(ras.GetOutputStreamAt(0)))
            {
                writer.WriteBytes(bytes);
                await writer.StoreAsync();
                writer.DetachStream();
            }
            ras.Seek(0);
            var decoder = await BitmapDecoder.CreateAsync(ras);
            using var bitmap = await decoder.GetSoftwareBitmapAsync();
            var result = await _win.RecognizeAsync(bitmap);
            var lines = new List<DomainOcrLine>();
            foreach (var line in result.Lines)
            {
                var words = new List<DomainOcrWord>();
                foreach (var w in line.Words)
                {
                    var r = w.BoundingRect;
                    words.Add(new DomainOcrWord(w.Text,
                        r.X / decoder.PixelWidth, r.Y / decoder.PixelHeight,
                        r.Width / decoder.PixelWidth, r.Height / decoder.PixelHeight));
                }
                lines.Add(new DomainOcrLine(line.Text, words));
            }
            return new OcrPage(lines, (int)decoder.PixelWidth, (int)decoder.PixelHeight);
        }
        catch
        {
            return OcrPage.Empty;
        }
    }
}
