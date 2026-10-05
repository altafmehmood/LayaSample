using ElBruno.LocalLLMs.Decisions;
using LayaSample.Api.Models;
using LayaSample.Api.Services;
using LayaSample.Api.Services.Documents;
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
    CancellationToken ct) =>
{
    if (file is null || file.Length == 0)
        return Results.BadRequest(new { error = "file is required" });
    if (file.Length > options.Value.MaxFileBytes)
        return Results.Json(new { error = $"file must be at most {options.Value.MaxFileBytes} bytes" }, statusCode: 413);

    try
    {
        // Buffered once so classify and prepare can each re-read it.
        await using var stream = new MemoryStream();
        await file.CopyToAsync(stream, ct);

        var classification = classifier.Classify(stream);
        var prepared = preparer.Prepare(stream, classification, strategy ?? classification.Strategy);
        var agent = dispatch == false ? null : await router.RouteAsync(classification, prepared, ct);

        var parts = prepared.Parts
            .Select(p => new PreparedPartDto(p.MediaType, p.Page, p.Text, p.Data is null ? null : Convert.ToBase64String(p.Data)))
            .ToList();
        return Results.Ok(new AnalyzeDocumentResponse(file.FileName, classification, prepared.Strategy, parts, agent));
    }
    catch (DocumentException ex)
    {
        return Results.Json(new { error = ex.Message }, statusCode: ex.StatusCode);
    }
})
.DisableAntiforgery()
.Accepts<IFormFile>("multipart/form-data")
.WithName("AnalyzeDocument")
.WithSummary("Classify an uploaded PDF, Excel or Word document, prepare it (PNG / markdown / as-is) and route it to an agent")
.Produces<AnalyzeDocumentResponse>()
.ProducesProblem(StatusCodes.Status400BadRequest)
.ProducesProblem(StatusCodes.Status413PayloadTooLarge)
.ProducesProblem(StatusCodes.Status415UnsupportedMediaType)
.ProducesProblem(StatusCodes.Status422UnprocessableEntity);

app.Run();

public partial class Program;
