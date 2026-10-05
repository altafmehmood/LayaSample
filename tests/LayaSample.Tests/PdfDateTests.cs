using LayaSample.Api.Services.Documents.Pdf;

namespace LayaSample.Tests;

public class PdfDateTests
{
    [Theory]
    [InlineData("D:20240131120000+01'00'", "2024-01-31T12:00:00+01:00")]
    [InlineData("D:20240131120000-05'30'", "2024-01-31T12:00:00-05:30")]
    [InlineData("D:20240131120000Z", "2024-01-31T12:00:00+00:00")]
    [InlineData("D:2024", "2024-01-01T00:00:00+00:00")]
    [InlineData("20240131", "2024-01-31T00:00:00+00:00")]
    // Out-of-range offsets fall back to UTC instead of throwing.
    [InlineData("D:20240131120000+15'00'", "2024-01-31T12:00:00+00:00")]
    [InlineData("D:20240131120000+01'75'", "2024-01-31T12:00:00+00:00")]
    public void Parses_pdf_dates(string value, string expected) =>
        Assert.Equal(DateTimeOffset.Parse(expected), PdfSignatureVerifier.ParsePdfDate(value));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("D:")]
    [InlineData("D:20241341")]
    [InlineData("not a date")]
    public void Unparseable_dates_are_null(string? value) => Assert.Null(PdfSignatureVerifier.ParsePdfDate(value));
}
