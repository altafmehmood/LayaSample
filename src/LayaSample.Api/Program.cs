using ElBruno.LocalLLMs.Decisions;
using LayaSample.Api.Models;
using LayaSample.Api.Services;
using LayaSample.Api.Services.Documents;
using LayaSample.Api.Services.Documents.Ocr;
using Microsoft.Extensions.Options;
using Scalar.AspNetCore;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddLocalDecisions(o =>
{
    builder.Configuration.GetSection("Laya").Bind(o);
    // Resolve a relative cache path against the project folder, not the working directory.
    if (!string.IsNullOrEmpty(o.CacheDirectory))
        o.CacheDirectory = Path.GetFullPath(o.CacheDirectory, builder.Environment.ContentRootPath);
});
builder.Services.AddOpenApi();
builder.Services.AddSingleton<IDissatisfactionAnalyzer, DissatisfactionAnalyzer>();
builder.Services.Configure<DocumentAnalysisOptions>(builder.Configuration.GetSection(DocumentAnalysisOptions.Section));
builder.Services.ConfigureHttpJsonOptions(o => o.SerializerOptions.Converters.Add(new System.Text.Json.Serialization.JsonStringEnumConverter()));
builder.Services.AddSingleton<IOcrEngine, RapidOcrEngine>();
builder.Services.AddSingleton<IDocumentClassifier, DocumentClassifier>();
builder.Services.AddSingleton<IDocumentPreparer, DocumentPreparer>();
builder.Services.AddSingleton<IDocumentRouter, DocumentRouter>();
foreach (var route in DocumentRoutes.All)
    builder.Services.AddSingleton<IDocumentAgent>(new StubDocumentAgent(route));
builder.Services.AddSingleton<ModelWarmup>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<ModelWarmup>());

var app = builder.Build();

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();           // /openapi/v1.json
    app.MapScalarApiReference(); // /scalar/v1
}

const int MaxTextLength = 4000;

app.MapGet("/health", (ModelWarmup warmup) =>
    warmup.Ready ? Results.Ok(new { status = "ready" }) : Results.Json(new { status = "loading" }, statusCode: 503));

app.MapPost("/api/feedback/analyze", async (AnalyzeRequest req, IDissatisfactionAnalyzer analyzer, ModelWarmup warmup, CancellationToken ct) =>
{
    if (string.IsNullOrWhiteSpace(req.Text))
        return Results.BadRequest(new { error = "text is required" });
    if (req.Text.Length > MaxTextLength)
        return Results.BadRequest(new { error = $"text must be at most {MaxTextLength} characters" });
    if (!warmup.Ready)
        return Results.Json(new { error = "model is still loading" }, statusCode: 503);

    return Results.Ok(await analyzer.AnalyzeAsync(req.Text, ct));
})
.WithName("AnalyzeFeedback")
.WithSummary("Analyze hotel guest feedback for dissatisfaction")
.Produces<AnalyzeResponse>()
.ProducesProblem(StatusCodes.Status400BadRequest)
.ProducesProblem(StatusCodes.Status503ServiceUnavailable);

app.MapPost("/api/documents/analyze", async (
    IFormFile? file,
    PreparationStrategy? strategy,
    bool? dispatch,
    IDocumentClassifier classifier,
    IDocumentPreparer preparer,
    IDocumentRouter router,
    IOptions<DocumentAnalysisOptions> options,
    ILogger<Program> logger,
    CancellationToken ct) =>
{
    if (file is null || file.Length == 0)
        return Results.BadRequest(new { error = "file is required" });
    if (file.Length > options.Value.MaxFileBytes)
        return Results.Json(new { error = $"file must be at most {options.Value.MaxFileBytes} bytes" }, statusCode: 413);

    try
    {
        // Buffered once: PDF, ZIP and TIFF need random access. The buffer is sized exactly so classify and prepare
        // share it rather than each taking a copy.
        await using var stream = new MemoryStream((int)file.Length);
        await file.CopyToAsync(stream, ct);

        var classification = classifier.Classify(stream);
        var prepared = preparer.Prepare(stream, classification, strategy ?? classification.Strategy, ct);
        var agent = dispatch == false ? null : await router.RouteAsync(classification, prepared, ct);

        var parts = prepared.Parts
            .Select(p => new PreparedPartDto(p.Role, p.MediaType, p.Page, p.Source, p.Text, p.Data))
            .ToList();
        return Results.Ok(new AnalyzeDocumentResponse(file.FileName, classification, prepared.Strategy, parts, agent));
    }
    catch (DocumentException ex)
    {
        return Results.Json(new { error = ex.Message }, statusCode: ex.StatusCode);
    }
    catch (OcrUnavailableException ex)
    {
        logger.LogError(ex, "OCR is unavailable");
        return Results.Json(new { error = "OCR is unavailable; retry later or use ?strategy=RenderToPng" }, statusCode: 503);
    }
})
.DisableAntiforgery()
.Accepts<IFormFile>("multipart/form-data")
.WithName("AnalyzeDocument")
.WithSummary("Classify an uploaded PDF, Excel, Word or image document, prepare it (PNG / markdown / hybrid with OCR / structured data / as-is) and route it to an agent")
.Produces<AnalyzeDocumentResponse>()
.ProducesProblem(StatusCodes.Status400BadRequest)
.ProducesProblem(StatusCodes.Status413PayloadTooLarge)
.ProducesProblem(StatusCodes.Status415UnsupportedMediaType)
.ProducesProblem(StatusCodes.Status422UnprocessableEntity)
.ProducesProblem(StatusCodes.Status503ServiceUnavailable);

app.Run();

public partial class Program;
