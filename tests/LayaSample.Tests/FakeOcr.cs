using LayaSample.Api.Services.Documents.Ocr;
using SkiaSharp;

namespace LayaSample.Tests;

/// <summary>Stands in for RapidOCR so tests do not load ONNX models.</summary>
internal sealed class FakeOcr(string text = "recognised text") : IOcrEngine
{
    public List<(int Width, int Height)> Calls { get; } = [];

    public string Recognize(SKBitmap image, CancellationToken ct = default)
    {
        Calls.Add((image.Width, image.Height));
        return text;
    }
}
