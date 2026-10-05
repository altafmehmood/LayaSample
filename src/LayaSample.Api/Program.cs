using ElBruno.LocalLLMs.Decisions;
using LayaSample.Api.Models;
using LayaSample.Api.Services;
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

app.Run();

public partial class Program;
