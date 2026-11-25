using System.Diagnostics;
using LedgerReview.Api;
using LedgerReview.Audit;
using LedgerReview.Infrastructure;
using LedgerReview.Pdf;
using LedgerReview.Pipeline;
using LedgerReview.Vision;
using Microsoft.AspNetCore.Http.Features;

var contentRoot = Directory.Exists(Path.Combine(AppContext.BaseDirectory, "wwwroot"))
    ? AppContext.BaseDirectory
    : Directory.GetCurrentDirectory();
var builder = WebApplication.CreateBuilder(new WebApplicationOptions
{
    Args = args,
    ContentRootPath = contentRoot
});

var port = Environment.GetEnvironmentVariable("LEDGER_PORT");
if (string.IsNullOrWhiteSpace(port)) port = "5180";
builder.WebHost.UseUrls($"http://127.0.0.1:{port}");
builder.WebHost.ConfigureKestrel(o =>
{
    o.Limits.MaxRequestBodySize = 2_000_000_000;
    o.Limits.KeepAliveTimeout = TimeSpan.FromMinutes(10);
});
builder.Services.Configure<FormOptions>(o =>
{
    o.MultipartBodyLengthLimit = 2_000_000_000;
    o.ValueLengthLimit = int.MaxValue;
    o.MultipartHeadersLengthLimit = int.MaxValue;
});

builder.Logging.ClearProviders();
builder.Logging.AddSimpleConsole(o => { o.SingleLine = true; o.TimestampFormat = "HH:mm:ss "; });

builder.Services.AddSingleton<Workspace>();
builder.Services.AddSingleton<Db>();
builder.Services.AddSingleton<PdfService>();
builder.Services.AddSingleton<OcrService>();
builder.Services.AddSingleton<TemplateStore>();
builder.Services.AddSingleton<Classifier>();
builder.Services.AddSingleton<CheckReader>();
builder.Services.AddSingleton<SignatureEngine>();
builder.Services.AddSingleton<AuditEngine>();
builder.Services.AddSingleton<Exporter>();
builder.Services.AddSingleton<Processor>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<Processor>());

var app = builder.Build();
app.UseDefaultFiles();
app.UseStaticFiles();
Endpoints.Map(app);
app.MapFallbackToFile("index.html");

if (args.Contains("--open"))
{
    app.Lifetime.ApplicationStarted.Register(() =>
    {
        try { Process.Start(new ProcessStartInfo($"http://127.0.0.1:{port}") { UseShellExecute = true }); }
        catch { }
    });
}

app.Run();
