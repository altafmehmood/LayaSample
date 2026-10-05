using System.IO.Compression;
using UglyToad.PdfPig;
using UglyToad.PdfPig.Tokens;

namespace LayaSample.Api.Services.Documents.Pdf;

/// <summary>Helpers for reading low-level PDF objects that PdfPig does not expose through its high-level API.</summary>
internal static class PdfTokens
{
    public static IToken? Resolve(PdfDocument pdf, IToken? token)
    {
        for (var depth = 0; token is IndirectReferenceToken reference && depth < 16; depth++)
            token = pdf.Structure.GetObject(reference.Data)?.Data;
        return token;
    }

    public static T? Get<T>(PdfDocument pdf, DictionaryToken? dictionary, string key) where T : class, IToken =>
        dictionary is not null && dictionary.Data.TryGetValue(key, out var token) ? Resolve(pdf, token) as T : null;

    public static DictionaryToken Catalog(PdfDocument pdf) => pdf.Structure.Catalog.CatalogDictionary;

    public static DictionaryToken? AcroForm(PdfDocument pdf) => Get<DictionaryToken>(pdf, Catalog(pdf), "AcroForm");

    /// <summary>Decodes a stream's data. Only FlateDecode (or no filter) is supported, which covers XFA and attachments in practice.</summary>
    public static byte[]? Decode(StreamToken stream)
    {
        var raw = stream.Data.ToArray();
        var filter = stream.StreamDictionary.Data.GetValueOrDefault("Filter");
        var names = filter switch
        {
            null => [],
            NameToken name => [name.Data],
            ArrayToken array => array.Data.OfType<NameToken>().Select(n => n.Data).ToArray(),
            _ => new[] { "?" }
        };

        if (names.Length == 0) return raw;
        if (names is not ["FlateDecode" or "Fl"]) return null;

        using var input = new ZLibStream(new MemoryStream(raw), CompressionMode.Decompress);
        using var output = new MemoryStream();
        input.CopyTo(output);
        return output.ToArray();
    }

    public static string? Text(IToken? token) => token switch
    {
        StringToken s => s.Data,
        HexToken h => h.Data,
        NameToken n => n.Data,
        _ => null
    };
}
