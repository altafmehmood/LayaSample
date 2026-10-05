using LayaSample.Rendering;
using SkiaSharp;

namespace LayaSample.Tests;

public class OcrEngineTests
{
    private static readonly Dictionary<OcrModel, string> V6Recognizers = new()
    {
        [OcrModel.PPOCRv6Tiny] = "PP-OCRv6_rec_tiny.onnx",
        [OcrModel.PPOCRv6Small] = "PP-OCRv6_rec_small.onnx",
        [OcrModel.PPOCRv6Medium] = "PP-OCRv6_rec_medium.onnx"
    };

    [Fact]
    public void Missing_models_are_reported_as_unavailable()
    {
        // Developers may have downloaded some v6 sizes; test with one that is not installed.
        var missing = V6Recognizers.Where(m => !File.Exists(Path.Combine(AppContext.BaseDirectory, "models/v6", m.Value))).ToList();
        Assert.SkipWhen(missing.Count == 0, "all PP-OCRv6 models are installed");
        var (model, recognizer) = missing[0];

        using var engine = new RapidOcrEngine(Microsoft.Extensions.Options.Options.Create(new RenderingOptions { OcrModel = model }));
        using var image = new SKBitmap(10, 10);

        var ex = Assert.Throws<OcrUnavailableException>(() => engine.Recognize(image, TestContext.Current.CancellationToken));
        Assert.Contains(recognizer, ex.Message);
    }

    [Fact]
    public void Bundled_model_reads_text()
    {
        // Real OCR with the PP-OCRv5 models the NuGet bundles: catches models that stop being copied next to the binary.
        using var engine = new RapidOcrEngine(Microsoft.Extensions.Options.Options.Create(new RenderingOptions()));
        using var image = new SKBitmap(1200, 300);
        using (var canvas = new SKCanvas(image))
        using (var font = new SKFont(SKTypeface.Default, 64))
        using (var paint = new SKPaint { Color = SKColors.Black, IsAntialias = true })
        {
            canvas.Clear(SKColors.White);
            canvas.DrawText("INVOICE 2026-0417", 60, 180, SKTextAlign.Left, font, paint);
        }

        var text = engine.Recognize(image, TestContext.Current.CancellationToken);

        Assert.Contains("INVOICE", text);
    }
}
