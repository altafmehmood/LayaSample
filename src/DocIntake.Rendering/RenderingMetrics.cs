using System.Diagnostics.Metrics;

namespace DocIntake.Rendering;

/// <summary>Render and OCR metrics, recorded in whichever process runs the work (the renderer, or the API in development).</summary>
public static class RenderingMetrics
{
    public const string MeterName = "DocIntake.Rendering";

    private static readonly Meter Meter = new(MeterName);

    public static readonly Histogram<double> RenderDuration = Meter.CreateHistogram<double>("intake.render.duration", "s",
        "Time to render one job (all its pages), by media type and outcome.");
    public static readonly Histogram<double> PageDuration = Meter.CreateHistogram<double>("intake.render.page.duration", "s",
        "Time to rasterise one page, excluding OCR.");
    public static readonly Histogram<double> OcrDuration = Meter.CreateHistogram<double>("intake.ocr.duration", "s",
        "Time spent in OCR for one page, including waiting for the engine.");
    public static readonly Counter<long> PagesRendered = Meter.CreateCounter<long>("intake.render.pages", "{page}",
        "Pages rendered, by media type and whether OCR ran.");
    public static readonly Counter<long> OcrCharacters = Meter.CreateCounter<long>("intake.ocr.characters", "{character}",
        "Characters recognised by OCR.");
    public static readonly UpDownCounter<long> JobsInFlight = Meter.CreateUpDownCounter<long>("intake.render.jobs.inflight", "{job}",
        "Render jobs running now.");
    public static readonly Counter<long> Rejections = Meter.CreateCounter<long>("intake.render.rejections", "{request}",
        "Render requests turned away because every slot and queue place was taken.");
    public static readonly Histogram<double> ModelLoadDuration = Meter.CreateHistogram<double>("intake.ocr.model.load.duration", "s",
        "Time to load the OCR models at startup, by outcome.");
}
