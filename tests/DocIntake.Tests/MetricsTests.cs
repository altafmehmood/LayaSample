using System.Diagnostics.Metrics;
using DocIntake.Api.Services.Documents;
using DocIntake.ExtractionService;
using DocIntake.Rendering;
using Microsoft.Extensions.DependencyInjection;

namespace DocIntake.Tests;

/// <summary>The shape of the metrics: histograms in seconds have bucket boundaries that suit seconds, and keys are namespaced.</summary>
public class MetricsTests
{
    private static List<Instrument> Publish()
    {
        var factory = new ServiceCollection().AddMetrics().BuildServiceProvider().GetRequiredService<IMeterFactory>();
        var found = new List<Instrument>();
        using var listener = new MeterListener
        {
            InstrumentPublished = (instrument, _) =>
            {
                if (instrument.Meter.Name.StartsWith("DocIntake", StringComparison.Ordinal)) found.Add(instrument);
            }
        };
        listener.Start();
        _ = new DocumentMetrics(factory);
        _ = new ExtractionMetrics(factory);
        _ = RenderingMetrics.RenderDuration; // the rendering meter is static: touching it creates its instruments
        listener.RecordObservableInstruments();
        return found;
    }

    private static IReadOnlyList<double> Boundaries(Instrument i) => i switch
    {
        Histogram<double> h => h.Advice?.HistogramBucketBoundaries ?? [],
        Histogram<long> h => h.Advice?.HistogramBucketBoundaries?.Select(Convert.ToDouble).ToList() ?? [],
        Histogram<int> h => h.Advice?.HistogramBucketBoundaries?.Select(Convert.ToDouble).ToList() ?? [],
        _ => []
    };

    [Fact]
    public void Every_duration_histogram_has_boundaries_from_milliseconds_to_minutes()
    {
        var histograms = Publish().Where(i => i.Unit == "s").ToList();

        Assert.True(histograms.Count >= 8, $"found only {histograms.Count} duration histograms");
        foreach (var histogram in histograms)
        {
            var bounds = Boundaries(histogram);
            Assert.True(bounds.Count >= 10, $"{histogram.Name} has the SDK's default buckets");
            // The SDK default starts at 0, 5, 10, ... 10000: a 200 ms request and a 4 s one would share a bucket.
            Assert.True(bounds[0] <= 0.01 && bounds[^1] >= 60, $"{histogram.Name}: {string.Join(",", bounds)}");
        }
    }

    [Fact]
    public void Size_and_page_histograms_have_their_own_boundaries()
    {
        var all = Publish();
        Assert.Contains(1_048_576d, Boundaries(all.First(i => i.Name == "intake.document.size")));
        Assert.Contains(10d, Boundaries(all.First(i => i.Name == "intake.document.pages")));
    }

    [Fact]
    public void Metric_tags_use_the_namespaced_keys_not_bare_words()
    {
        var factory = new ServiceCollection().AddMetrics().BuildServiceProvider().GetRequiredService<IMeterFactory>();
        var keys = new HashSet<string>();
        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, l) => { if (instrument.Meter.Name.StartsWith("DocIntake", StringComparison.Ordinal)) l.EnableMeasurementEvents(instrument); };
        listener.SetMeasurementEventCallback<long>((_, _, tags, _) => { foreach (var t in tags) keys.Add(t.Key); });
        listener.SetMeasurementEventCallback<double>((_, _, tags, _) => { foreach (var t in tags) keys.Add(t.Key); });
        listener.SetMeasurementEventCallback<int>((_, _, tags, _) => { foreach (var t in tags) keys.Add(t.Key); });
        listener.Start();

        var documents = new DocumentMetrics(factory);
        documents.RecordDocument("TextPdf", "Text", 200);
        documents.RecordUpload("TextPdf", 1000, 2);
        documents.RecordRisk("macro", "Block");
        documents.RecordScan("clean", TimeSpan.FromMilliseconds(5));
        documents.RecordRateLimited("analyze");

        Assert.Contains("intake.document.kind", keys);
        Assert.Contains("http.response.status_code", keys);
        Assert.Contains("intake.ratelimit.policy", keys);
        Assert.DoesNotContain("kind", keys);
        Assert.DoesNotContain("status", keys);
        Assert.DoesNotContain("policy", keys);
    }
}
