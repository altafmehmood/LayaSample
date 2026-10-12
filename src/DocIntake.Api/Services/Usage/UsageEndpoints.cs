using System.Globalization;
using System.Text;
using DocIntake.Api.Services.Documents;

namespace DocIntake.Api.Services.Usage;

public sealed record UsagePage(int Count, long? NextBefore, IReadOnlyList<UsageRecord> Rows);

public sealed record UsageSummary(string GroupBy, UsageSummaryRow Total, IReadOnlyList<UsageSummaryRow> Groups);

/// <summary>RFC 4180 CSV for the usage endpoints.</summary>
public static class UsageCsv
{
    private const string FormulaStarts = "=+-@\t\r";

    /// <summary>
    /// A text cell. One starting with a character a spreadsheet reads as a formula gets a leading apostrophe, because file
    /// names come from the caller and would otherwise run when the export is opened in Excel.
    /// </summary>
    public static string Text(string? value)
    {
        if (string.IsNullOrEmpty(value))
            return "";
        if (FormulaStarts.Contains(value[0]))
            value = "'" + value;
        return value.IndexOfAny([',', '"', '\r', '\n']) >= 0 ? "\"" + value.Replace("\"", "\"\"") + "\"" : value;
    }

    public static string Number(long? value) => value?.ToString(CultureInfo.InvariantCulture) ?? "";

    public static string Money(decimal? value) => value?.ToString("0.000000", CultureInfo.InvariantCulture) ?? "";

    public static string Time(DateTimeOffset value) => value.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture);

    public static byte[] Encode(IEnumerable<string> header, IEnumerable<IEnumerable<string>> rows)
    {
        var text = new StringBuilder();
        text.Append(string.Join(',', header)).Append("\r\n");
        foreach (var row in rows)
            text.Append(string.Join(',', row)).Append("\r\n");
        // A byte order mark, so Excel reads the file names as UTF-8.
        return [.. Encoding.UTF8.GetPreamble(), .. Encoding.UTF8.GetBytes(text.ToString())];
    }

    public static readonly string[] CallHeader =
    [
        "id", "timestamp", "trace_id", "api_key_fingerprint", "file_name", "file_bytes", "document_kind", "pages", "images",
        "agent", "model", "attempt", "input_tokens", "cached_input_tokens", "reasoning_tokens", "output_tokens", "cost_usd",
        "price_key", "outcome", "duration_ms"
    ];

    public static IEnumerable<string> Call(UsageRecord r) =>
    [
        Number(r.Id), Time(r.Timestamp), Text(r.TraceId), Text(r.ApiKeyFingerprint), Text(r.FileName), Number(r.FileBytes),
        Text(r.DocumentKind), Number(r.Pages), Number(r.Images), Text(r.Agent), Text(r.Model), Number(r.Attempt),
        Number(r.InputTokens), Number(r.CachedInputTokens), Number(r.ReasoningTokens), Number(r.OutputTokens), Money(r.CostUsd),
        Text(r.PriceKey), Text(r.Outcome), Number(r.DurationMs)
    ];

    public static readonly string[] SummaryHeader =
        ["group", "calls", "input_tokens", "cached_input_tokens", "reasoning_tokens", "output_tokens", "cost_usd", "unpriced_calls"];

    public static IEnumerable<string> Summary(UsageSummaryRow r) =>
        [Text(r.Group), Number(r.Calls), Number(r.InputTokens), Number(r.CachedInputTokens), Number(r.ReasoningTokens),
         Number(r.OutputTokens), Money(r.CostUsd), Number(r.Unpriced)];
}

public static class UsageEndpoints
{
    public const int DefaultLimit = 1_000;
    public const int MaxLimit = 10_000;

    public static void MapUsageEndpoints(this WebApplication app)
    {
        app.MapGet("/api/usage", async (
            HttpContext http, UsageStore store, DateTimeOffset? from, DateTimeOffset? to, string? file, string? model, string? apiKey,
            long? before, int? limit, string? format, CancellationToken ct) =>
        {
            if (!store.Enabled)
                return DocumentProblems.Problem(StatusCodes.Status503ServiceUnavailable, "usage tracking is not configured");
            if (Validate(from, to, format, http) is { } invalid)
                return invalid;
            var take = limit ?? DefaultLimit;
            if (take < 1 || take > MaxLimit)
                return DocumentProblems.Problem(StatusCodes.Status400BadRequest, $"limit must be between 1 and {MaxLimit}");

            // One more than asked for, to know whether there is a next page.
            var rows = await store.QueryAsync(Scope(http, new UsageQuery(from, to, apiKey, model, file, before, take + 1)), ct);
            long? next = null;
            if (rows.Count > take)
            {
                rows = rows.Take(take).ToList();
                next = rows[^1].Id;
                http.Response.Headers["X-Next-Before"] = next.Value.ToString(CultureInfo.InvariantCulture);
            }

            return WantsCsv(http, format)
                ? Csv(UsageCsv.Encode(UsageCsv.CallHeader, rows.Select(UsageCsv.Call)), "docintake-usage")
                : Results.Ok(new UsagePage(rows.Count, next, rows));
        })
        .RequireApiKey()
        .WithName("GetUsage")
        .WithSummary("Model calls with their tokens and list-price cost")
        .WithDescription("One row per model call, newest first: the calling API key (a fingerprint, never the key), file name, input, cached, "
            + "reasoning and output tokens and the cost at the configured list price. A key sees its own rows; an admin key sees all. "
            + "format=csv (or Accept: text/csv) returns a CSV download. When more rows exist the response carries the id to continue from "
            + "(nextBefore, or the X-Next-Before header for CSV): pass it as `before`.")
        .Produces<UsagePage>()
        .ProducesProblem(StatusCodes.Status400BadRequest)
        .ProducesProblem(StatusCodes.Status401Unauthorized)
        .ProducesProblem(StatusCodes.Status503ServiceUnavailable);

        app.MapGet("/api/usage/summary", async (
            HttpContext http, UsageStore store, DateTimeOffset? from, DateTimeOffset? to, string? file, string? model, string? apiKey,
            string? groupBy, string? format, CancellationToken ct) =>
        {
            if (!store.Enabled)
                return DocumentProblems.Problem(StatusCodes.Status503ServiceUnavailable, "usage tracking is not configured");
            if (Validate(from, to, format, http) is { } invalid)
                return invalid;
            var group = string.IsNullOrEmpty(groupBy) ? "day" : groupBy;
            if (!UsageStore.GroupBys.Contains(group))
                return DocumentProblems.Problem(StatusCodes.Status400BadRequest, $"groupBy must be one of {string.Join(", ", UsageStore.GroupBys)}");

            var groups = await store.SummaryAsync(Scope(http, new UsageQuery(from, to, apiKey, model, file, null, 0)), group, ct);
            var total = new UsageSummaryRow("total", groups.Sum(g => g.Calls), groups.Sum(g => g.InputTokens), groups.Sum(g => g.CachedInputTokens),
                groups.Sum(g => g.ReasoningTokens), groups.Sum(g => g.OutputTokens), groups.Sum(g => g.CostUsd), groups.Sum(g => g.Unpriced));

            return WantsCsv(http, format)
                ? Csv(UsageCsv.Encode(UsageCsv.SummaryHeader, groups.Append(total).Select(UsageCsv.Summary)), "docintake-usage-summary")
                : Results.Ok(new UsageSummary(group, total, groups));
        })
        .RequireApiKey()
        .WithName("GetUsageSummary")
        .WithSummary("Token and cost totals per day, model or API key")
        .WithDescription("Calls, tokens and list-price cost grouped by day (default), model or key, with a grand total. Same filters, scoping "
            + "and formats as /api/usage. unpriced counts calls of a model with no configured price, whose cost is missing from the total.")
        .Produces<UsageSummary>()
        .ProducesProblem(StatusCodes.Status400BadRequest)
        .ProducesProblem(StatusCodes.Status401Unauthorized)
        .ProducesProblem(StatusCodes.Status503ServiceUnavailable);
    }

    /// <summary>A key sees only its own rows; an admin key sees all and may narrow by key.</summary>
    private static UsageQuery Scope(HttpContext http, UsageQuery query)
    {
        var caller = http.GetCaller();
        return query with { ApiKey = caller.IsAdmin ? query.ApiKey : caller.Fingerprint };
    }

    private static IResult? Validate(DateTimeOffset? from, DateTimeOffset? to, string? format, HttpContext http)
    {
        if (from is { } f && to is { } t && t <= f)
            return DocumentProblems.Problem(StatusCodes.Status400BadRequest, "to must be after from");
        if (!string.IsNullOrEmpty(format) && !format.Equals("json", StringComparison.OrdinalIgnoreCase) && !format.Equals("csv", StringComparison.OrdinalIgnoreCase))
            return DocumentProblems.Problem(StatusCodes.Status400BadRequest, "format must be json or csv");
        return null;
    }

    private static bool WantsCsv(HttpContext http, string? format) =>
        string.IsNullOrEmpty(format)
            ? http.Request.Headers.Accept.ToString().Contains("text/csv", StringComparison.OrdinalIgnoreCase)
            : format.Equals("csv", StringComparison.OrdinalIgnoreCase);

    private static IResult Csv(byte[] content, string name) =>
        Results.File(content, "text/csv; charset=utf-8", $"{name}-{DateTime.UtcNow:yyyyMMdd}.csv");
}
