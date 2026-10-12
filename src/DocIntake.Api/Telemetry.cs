using DocIntake.ServiceDefaults;
using System.Diagnostics;
using DocIntake.Rendering;
using Microsoft.AspNetCore.Http.Features;

namespace DocIntake.Api;

/// <summary>The API's own spans (registered with the tracer in <see cref="IntakeTelemetry"/>) and helpers for the request span.</summary>
internal static class Telemetry
{
    public static ActivitySource Source => IntakeTelemetry.Source;

    /// <summary>Longest text put on a span attribute; Honeycomb truncates values above 64 KB.</summary>
    public const int MaxTextTag = 32 * 1024;

    /// <summary>Tags the request's server span, so one row says what the call was about and how it ended.</summary>
    public static void TagRequest(HttpContext context, string key, object? value) =>
        context.Features.Get<IHttpActivityFeature>()?.Activity.SetTag(key, value);

    /// <summary>Time since the request started: queueing for a slot, the upload and the timeout all count.</summary>
    public static TimeSpan RequestAge(HttpContext context) =>
        context.Features.Get<IHttpActivityFeature>()?.Activity is { } a ? DateTime.UtcNow - a.StartTimeUtc : TimeSpan.Zero;

    public static string Truncate(string text) =>
        text.Length <= MaxTextTag ? text : text[..MaxTextTag] + $"… [{text.Length - MaxTextTag} more characters]";
}
