using System.Diagnostics.Metrics;

namespace LayaSample.Rendering;

/// <summary>Render and OCR metrics, recorded in whichever process runs the work (the renderer, or the API in development).</summary>
public static class RenderingMetrics
{
    public const string MeterName = "LayaSample.Rendering";

    private static readonly Meter Meter = new(MeterName);

    public static readonly Histogram<double> RenderDuration = Meter.CreateHistogram<double>("laya.render.duration", "s",
        "Time to render one job (all its pages), by media type and outcome.");
    public static readonly Histogram<double> PageDuration = Meter.CreateHistogram<double>("laya.render.page.duration", "s",
        "Time to rasterise one page, excluding OCR.");
    public static readonly Histogram<double> OcrDuration = Meter.CreateHistogram<double>("laya.ocr.duration", "s",
        "Time spent in OCR for one page, including waiting for the engine.");
    public static readonly Counter<long> PagesRendered = Meter.CreateCounter<long>("laya.render.pages", "{page}",
        "Pages rendered, by media type and whether OCR ran.");
    public static readonly Counter<long> OcrCharacters = Meter.CreateCounter<long>("laya.ocr.characters", "{character}",
        "Characters recognised by OCR.");
    public static readonly UpDownCounter<long> JobsInFlight = Meter.CreateUpDownCounter<long>("laya.render.jobs.inflight", "{job}",
        "Render jobs running now.");
    public static readonly Counter<long> Rejections = Meter.CreateCounter<long>("laya.render.rejections", "{request}",
        "Render requests turned away because every slot and queue place was taken.");
    public static readonly Histogram<double> ModelLoadDuration = Meter.CreateHistogram<double>("laya.ocr.model.load.duration", "s",
        "Time to load the OCR models at startup, by outcome.");
}
