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
