using LayaSample.Api.Services.Documents.Ocr;
using SkiaSharp;

namespace LayaSample.Tests;

/// <summary>Stands in for RapidOCR so tests do not load ONNX models.</summary>
/// <param name="fail">Behave like an engine whose models are missing.</param>
internal sealed class FakeOcr(string text = "recognised text", bool fail = false) : IOcrEngine
{
    public List<(int Width, int Height)> Calls { get; } = [];

    public string Recognize(SKBitmap image, CancellationToken ct = default)
    {
        if (fail) throw new OcrUnavailableException("models missing");
        Calls.Add((image.Width, image.Height));
        return text;
    }
}

/// <summary>Holds every OCR call until released (or cancelled), to keep a request in flight.</summary>
internal sealed class BlockingOcr : IOcrEngine
{
    private readonly SemaphoreSlim _release = new(0);

    /// <summary>Released once per call that has started.</summary>
    public SemaphoreSlim Entered { get; } = new(0);

    public string Recognize(SKBitmap image, CancellationToken ct = default)
    {
        Entered.Release();
        _release.Wait(ct);
        return "recognised text";
    }

    public void Release() => _release.Release();
}
