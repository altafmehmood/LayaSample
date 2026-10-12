using System.Diagnostics;
using DocIntake.ServiceDefaults;

namespace DocIntake.ExtractionService;

/// <summary>The service's own spans (registered with the tracer in <see cref="IntakeTelemetry"/>).</summary>
internal static class Telemetry
{
    public static ActivitySource Source => IntakeTelemetry.Source;

    /// <summary>Longest text put on a span attribute; Honeycomb truncates values above 64 KB.</summary>
    public const int MaxTextTag = 32 * 1024;

    public static string Truncate(string text) =>
        text.Length <= MaxTextTag ? text : text[..MaxTextTag] + $"… [{text.Length - MaxTextTag} more characters]";
}
