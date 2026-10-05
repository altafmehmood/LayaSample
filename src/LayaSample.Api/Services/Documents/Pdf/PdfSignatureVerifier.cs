using System.Globalization;
using System.Security.Cryptography;
using System.Security.Cryptography.Pkcs;
using System.Security.Cryptography.X509Certificates;
using LayaSample.Api.Models;
using UglyToad.PdfPig;
using UglyToad.PdfPig.AcroForms.Fields;
using UglyToad.PdfPig.Tokens;

namespace LayaSample.Api.Services.Documents.Pdf;

/// <summary>
/// Checks the cryptographic integrity of PDF signatures (adbe.pkcs7.detached / ETSI.CAdES.detached).
/// Certificate trust (chain building, revocation) is deliberately out of scope.
/// </summary>
public static class PdfSignatureVerifier
{
    /// <returns>Null when the signature field has not been signed.</returns>
    public static SignatureInfo? Verify(PdfDocument pdf, AcroSignatureField field, ReadOnlyMemory<byte> file)
    {
        var sig = PdfTokens.Get<DictionaryToken>(pdf, field.Dictionary, "V");
        if (sig is null) return null;

        var fieldName = field.Information.PartialName;
        var nameFromDict = PdfTokens.Text(PdfTokens.Resolve(pdf, sig.Data.GetValueOrDefault("Name")));
        var timeFromDict = ParsePdfDate(PdfTokens.Text(PdfTokens.Resolve(pdf, sig.Data.GetValueOrDefault("M"))));

        SignatureInfo Fail(string error, bool covers = false) => new(fieldName, nameFromDict, timeFromDict, covers, false, error);

        var subFilter = PdfTokens.Text(PdfTokens.Resolve(pdf, sig.Data.GetValueOrDefault("SubFilter")));
        if (subFilter is not ("adbe.pkcs7.detached" or "ETSI.CAdES.detached"))
            return new(fieldName, nameFromDict, timeFromDict, false, null, $"unsupported signature format '{subFilter}'");

        var range = PdfTokens.Get<ArrayToken>(pdf, sig, "ByteRange")?.Data.OfType<NumericToken>().Select(n => n.Long).ToArray();
        if (range is not { Length: 4 } || range.Any(v => v < 0) || range[0] + range[1] > file.Length || range[2] + range[3] > file.Length)
            return Fail("invalid ByteRange");
        var covers = range[0] == 0 && range[2] + range[3] == file.Length;

        var contents = PdfTokens.Resolve(pdf, sig.Data.GetValueOrDefault("Contents")) switch
        {
            HexToken hex => hex.Bytes.ToArray(),
            StringToken str => str.GetBytes(),
            _ => null
        };
        if (contents is null) return Fail("missing signature contents", covers);

        var signed = new byte[range[1] + range[3]];
        file.Span.Slice((int)range[0], (int)range[1]).CopyTo(signed);
        file.Span.Slice((int)range[2], (int)range[3]).CopyTo(signed.AsSpan((int)range[1]));

        try
        {
            var cms = new SignedCms(new ContentInfo(signed), detached: true);
            // /Contents is zero-padded to a fixed size; decode only the DER-encoded part.
            cms.Decode(contents.AsSpan(0, Math.Min(DerLength(contents), contents.Length)));
            cms.CheckSignature(verifySignatureOnly: true);

            var signer = cms.SignerInfos[0];
            var signedTime = signer.SignedAttributes.Cast<CryptographicAttributeObject>()
                .SelectMany(a => a.Values.Cast<AsnEncodedData>()).OfType<Pkcs9SigningTime>()
                .Select(t => (DateTimeOffset?)new DateTimeOffset(t.SigningTime)).FirstOrDefault();

            return new(fieldName,
                signer.Certificate?.GetNameInfo(X509NameType.SimpleName, false) ?? nameFromDict,
                signedTime ?? timeFromDict, covers, true, covers ? null : "signature does not cover the whole file (later incremental update)");
        }
        catch (CryptographicException ex)
        {
            return Fail(ex.Message, covers);
        }
    }

    private static int DerLength(ReadOnlySpan<byte> der)
    {
        if (der.Length < 2 || der[0] != 0x30) return der.Length;
        if (der[1] < 0x80) return 2 + der[1];
        var count = der[1] & 0x7F;
        if (count > 4 || der.Length < 2 + count) return der.Length;
        var length = 0;
        for (var i = 0; i < count; i++) length = (length << 8) | der[2 + i];
        return 2 + count + length;
    }

    /// <summary>Parses a PDF date string such as <c>D:20240131120000+01'00'</c>.</summary>
    internal static DateTimeOffset? ParsePdfDate(string? value)
    {
        if (string.IsNullOrEmpty(value)) return null;
        var s = value.StartsWith("D:") ? value[2..] : value;
        if (s.Length < 4) return null;
        var digits = new string(s.TakeWhile(char.IsDigit).ToArray()).PadRight(14, '0')[..14];
        // Months/days default to 01 when omitted.
        if (digits[4..6] == "00") digits = digits[..4] + "01" + digits[6..];
        if (digits[6..8] == "00") digits = digits[..6] + "01" + digits[8..];
        if (!DateTime.TryParseExact(digits, "yyyyMMddHHmmss", CultureInfo.InvariantCulture, DateTimeStyles.None, out var local))
            return null;

        var rest = s[s.TakeWhile(char.IsDigit).Count()..];
        var offset = TimeSpan.Zero;
        if (rest.Length >= 3 && rest[0] is '+' or '-' && int.TryParse(rest.AsSpan(1, 2), out var hh))
        {
            var mm = rest.Length >= 6 && int.TryParse(rest.AsSpan(4, 2), out var m) ? m : 0;
            offset = new TimeSpan(hh, mm, 0) * (rest[0] == '-' ? -1 : 1);
        }
        return new DateTimeOffset(local, offset);
    }
}
