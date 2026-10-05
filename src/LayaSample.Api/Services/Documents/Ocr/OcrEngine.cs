using Microsoft.Extensions.Options;
using RapidOcrNet;
using SkiaSharp;

namespace LayaSample.Api.Services.Documents.Ocr;

public interface IOcrEngine
{
    /// <summary>Recognises the text in an image, one line per detected text block.</summary>
    string Recognize(SKBitmap image, CancellationToken ct = default);
}

/// <summary>
/// RapidOCR (PaddleOCR PP-OCRv5 latin models via ONNX Runtime). Models load on first use; calls are serialised
/// because a <see cref="RapidOcr"/> instance is not documented as thread-safe.
/// </summary>
public sealed class RapidOcrEngine(IOptions<DocumentAnalysisOptions> options) : IOcrEngine, IDisposable
{
    private readonly Lazy<RapidOcr> _ocr = new(() =>
    {
        // The preset's paths are relative to the working directory; the NuGet copies the models next to the binary.
        var preset = RapidOcrModelSet.PPOCRv5Latin;
        string Local(string path) => Path.Combine(AppContext.BaseDirectory, path);
        var ocr = new RapidOcr();
        ocr.InitModels(preset with
        {
            DetModelPath = Local(preset.DetModelPath),
            ClsModelPath = Local(preset.ClsModelPath),
            RecModelPath = Local(preset.RecModelPath),
            KeysPath = Local(preset.KeysPath)
        });
        return ocr;
    });
    private readonly Lock _gate = new();
    private readonly int _maxSide = options.Value.OcrMaxSide;

    public string Recognize(SKBitmap image, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        using var scaled = Downscale(image);
        lock (_gate)
            return _ocr.Value.Detect(scaled ?? image, RapidOcrOptions.Default).StrRes.Trim();
    }

    /// <returns>Null when the image is already small enough.</returns>
    private SKBitmap? Downscale(SKBitmap image)
    {
        var longest = Math.Max(image.Width, image.Height);
        if (longest <= _maxSide) return null;
        var scale = (double)_maxSide / longest;
        var info = new SKImageInfo(Math.Max(1, (int)(image.Width * scale)), Math.Max(1, (int)(image.Height * scale)));
        return image.Resize(info, new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.Linear));
    }

    public void Dispose()
    {
        if (_ocr.IsValueCreated) _ocr.Value.Dispose();
    }
}
