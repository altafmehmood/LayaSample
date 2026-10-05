using Microsoft.Extensions.Options;
using RapidOcrNet;
using SkiaSharp;

namespace LayaSample.Api.Services.Documents.Ocr;

public interface IOcrEngine
{
    /// <summary>Recognises the text in an image, one line per detected text block.</summary>
    /// <exception cref="OcrUnavailableException">The OCR models are missing or cannot be loaded.</exception>
    string Recognize(SKBitmap image, CancellationToken ct = default);
}

/// <summary>
/// PaddleOCR model families. Each runs three ONNX models: a text detector, a 0°/180° line classifier and a recogniser
/// whose character set comes from a dictionary file.
/// </summary>
public enum OcrModel
{
    /// <summary>PP-OCRv5 mobile, Latin script (~14 MB). Ships in the RapidOcrNet NuGet package.</summary>
    PPOCRv5Latin,
    /// <summary>PP-OCRv6 multilingual (Latin, CJK and more), smallest and fastest (~6 MB). Downloaded separately.</summary>
    PPOCRv6Tiny,
    /// <summary>PP-OCRv6 multilingual, balanced (~31 MB). Downloaded separately.</summary>
    PPOCRv6Small,
    /// <summary>PP-OCRv6 multilingual, most accurate and slowest (~138 MB). Downloaded separately.</summary>
    PPOCRv6Medium
}

/// <summary>The OCR engine is misconfigured (a server problem, not a problem with the document).</summary>
public sealed class OcrUnavailableException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>
/// RapidOCR (PaddleOCR models via ONNX Runtime). Models load on first use; calls are serialised
/// because a <see cref="RapidOcr"/> instance is not documented as thread-safe.
/// </summary>
public sealed class RapidOcrEngine : IOcrEngine, IDisposable
{
    private readonly Lazy<RapidOcr> _ocr;
    private readonly RapidOcrOptions _detectOptions;
    private readonly Lock _gate = new();
    private readonly int _maxSide;

    public RapidOcrEngine(IOptions<DocumentAnalysisOptions> options)
    {
        var o = options.Value;
        _maxSide = o.OcrMaxSide;
        // v6 detectors were exported for short-side adaptive resizing; Default's 1024 cap and border starve them.
        _detectOptions = o.OcrModel == OcrModel.PPOCRv5Latin ? RapidOcrOptions.Default : RapidOcrOptions.PPOCRv6;
        _ocr = new Lazy<RapidOcr>(() => Load(o.OcrModel));
    }

    private static RapidOcr Load(OcrModel model)
    {
        var preset = model switch
        {
            OcrModel.PPOCRv6Tiny => RapidOcrModelSet.PPOCRv6Tiny,
            OcrModel.PPOCRv6Small => RapidOcrModelSet.PPOCRv6Small,
            OcrModel.PPOCRv6Medium => RapidOcrModelSet.PPOCRv6Medium,
            _ => RapidOcrModelSet.PPOCRv5Latin
        };
        // The preset's paths (models/v5/..., models/v6/...) are relative to the working directory; the build copies
        // the models next to the binary.
        string Local(string path) => Path.Combine(AppContext.BaseDirectory, path);
        var models = preset with
        {
            DetModelPath = Local(preset.DetModelPath),
            ClsModelPath = Local(preset.ClsModelPath),
            RecModelPath = Local(preset.RecModelPath),
            KeysPath = Local(preset.KeysPath)
        };

        var missing = new[] { models.DetModelPath, models.ClsModelPath, models.RecModelPath, models.KeysPath }.Where(p => !File.Exists(p)).ToList();
        if (missing.Count > 0)
            throw new OcrUnavailableException($"{model} OCR model files not found: {string.Join(", ", missing)}. Run scripts/download-models.sh.");

        var ocr = new RapidOcr();
        try
        {
            ocr.InitModels(models);
            return ocr;
        }
        catch (Exception ex)
        {
            ocr.Dispose();
            throw new OcrUnavailableException($"{model} OCR models could not be loaded", ex);
        }
    }

    public string Recognize(SKBitmap image, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        using var scaled = Downscale(image);
        lock (_gate)
            // The token is also checked inside each ONNX run, so a cancelled request stops mid-page.
            return _ocr.Value.Detect(scaled ?? image, _detectOptions, ct).StrRes.Trim();
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
