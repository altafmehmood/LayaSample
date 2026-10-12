using DocIntake.Contracts;
using DocIntake.Api.Services.Documents;
using DocIntake.Rendering;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit.Sdk;

namespace DocIntake.Tests;

public class StreamingTests
{
    internal sealed class NoRasterizer : IPageRasterizer
    {
        public Task<IReadOnlyList<RasterPage>> InspectImageAsync(Stream image, string mediaType, int maxFrames, CancellationToken ct = default) =>
            throw new NotSupportedException("a text PDF never reaches the renderer");

        public Task<IReadOnlyList<RenderedPage>> RenderAsync(RenderJob job, CancellationToken ct = default) =>
            throw new NotSupportedException("a text PDF never reaches the renderer");
    }

    [Fact]
    public async Task A_large_document_is_classified_and_prepared_without_being_copied_into_memory()
    {
        // As large as the upload limit allows. Written to a file first, so building it is outside the measurement.
        const int padding = 19 * 1024 * 1024;
        var path = Path.Combine(Path.GetTempPath(), "docintake-large-" + Guid.NewGuid().ToString("N") + ".pdf");
        await File.WriteAllBytesAsync(path, DocumentFixtures.PaddedTextPdf(padding), TestContext.Current.CancellationToken);
        try
        {
            await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, FileOptions.Asynchronous);
            var options = Options.Create(new DocumentAnalysisOptions { AllowInProcessRendering = true });
            var rasterizer = new NoRasterizer();
            var classifier = new DocumentClassifier(options, rasterizer);
            var preparer = new DocumentPreparer(options, classifier, rasterizer, NullLogger<DocumentPreparer>.Instance);

            // PdfPig allocates about 30 MB once, the first time it opens any PDF (fonts and encodings), so warm it up on a small one.
            using (var warmUp = new MemoryStream(DocumentFixtures.TextPdf()))
                await preparer.PrepareAsync(warmUp, await classifier.ClassifyAsync(warmUp, TestContext.Current.CancellationToken), TestContext.Current.CancellationToken);

            // Both stages finish without yielding for a text PDF, so this thread's allocations are the whole cost.
            var before = GC.GetAllocatedBytesForCurrentThread();
            var classification = await classifier.ClassifyAsync(stream, TestContext.Current.CancellationToken);
            var prepared = await preparer.PrepareAsync(stream, classification, TestContext.Current.CancellationToken);
            var allocated = GC.GetAllocatedBytesForCurrentThread() - before;

            Assert.Equal(DocumentKind.TextPdf, classification.Kind);
            Assert.Contains("agreement", Assert.Single(prepared.Parts).Text);
            Assert.True(stream.Length > padding, "the document is as large as intended");
            // Copying the file even once would allocate more than its size.
            Assert.True(allocated < stream.Length / 4, $"allocated {allocated / 1024 / 1024} MB for a {stream.Length / 1024 / 1024} MB document");
            // Left open and usable for the next stage.
            Assert.True(stream.CanRead);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void A_stream_lent_to_a_library_stays_open_when_the_library_disposes_it()
    {
        using var inner = new MemoryStream([1, 2, 3, 4]);

        using (var lent = new LeaveOpenStream(inner))
        {
            Assert.Equal(4, lent.Length);
            lent.Position = 2;
            Assert.Equal(3, lent.ReadByte());
            Assert.False(lent.CanWrite);
            Assert.Throws<NotSupportedException>(() => lent.Write([1], 0, 1));
        }

        Assert.True(inner.CanRead);
        Assert.Equal(3, inner.Position);
    }
}
