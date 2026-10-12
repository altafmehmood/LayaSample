using System.Globalization;
using System.Text;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Options;

namespace LayaSample.Api.Services.Usage;

/// <param name="Before">Rows with an id below this one, to page through newest-first results.</param>
public sealed record UsageQuery(DateTimeOffset? From, DateTimeOffset? To, string? ApiKey, string? Model, string? File, long? Before, int Limit);

public sealed record UsageSummaryRow(
    string Group, long Calls, long InputTokens, long CachedInputTokens, long ReasoningTokens, long OutputTokens,
    decimal CostUsd, long Unpriced);

/// <summary>
/// The usage history, one SQLite file (WAL mode). Costs are stored as whole micro-dollars so sums are exact. The file and
/// its table are created on first use, so a process that never records or reads usage never touches the disk.
/// </summary>
public sealed class UsageStore(IOptions<UsageOptions> options, IHostEnvironment environment, ILogger<UsageStore> logger)
{
    public static readonly string[] GroupBys = ["day", "model", "key"];

    private const string TimeFormat = "yyyy-MM-dd'T'HH:mm:ss.fff'Z'";

    private const string Schema = """
        CREATE TABLE IF NOT EXISTS usage_calls (
            id INTEGER PRIMARY KEY AUTOINCREMENT,
            ts TEXT NOT NULL,
            trace_id TEXT,
            key_fp TEXT NOT NULL,
            file_name TEXT NOT NULL,
            file_bytes INTEGER NOT NULL,
            doc_kind TEXT NOT NULL,
            pages INTEGER NOT NULL,
            images INTEGER NOT NULL,
            agent TEXT NOT NULL,
            model TEXT NOT NULL,
            attempt INTEGER NOT NULL,
            input_tokens INTEGER,
            cached_tokens INTEGER,
            reasoning_tokens INTEGER,
            output_tokens INTEGER,
            cost_micro_usd INTEGER,
            price_key TEXT,
            outcome TEXT NOT NULL,
            duration_ms INTEGER NOT NULL);
        CREATE INDEX IF NOT EXISTS ix_usage_ts ON usage_calls (ts);
        CREATE INDEX IF NOT EXISTS ix_usage_key ON usage_calls (key_fp, ts);
        """;

    private readonly object _gate = new();
    private string? _connectionString;

    public bool Enabled => options.Value.Enabled;

    private string ConnectionString()
    {
        if (_connectionString is not null)
            return _connectionString;
        lock (_gate)
        {
            if (_connectionString is not null)
                return _connectionString;

            var path = Path.GetFullPath(options.Value.DatabasePath!, environment.ContentRootPath);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var connectionString = new SqliteConnectionStringBuilder { DataSource = path, Mode = SqliteOpenMode.ReadWriteCreate, DefaultTimeout = 5 }.ToString();
            using (var connection = new SqliteConnection(connectionString))
            {
                connection.Open();
                using var command = connection.CreateCommand();
                // Readers (the usage endpoints) never block the single writer, and the other way round.
                command.CommandText = "PRAGMA journal_mode=WAL;" + Schema;
                command.ExecuteNonQuery();
            }
            logger.LogInformation("Usage history is kept in {Path}", path);
            return _connectionString = connectionString;
        }
    }

    private async Task<SqliteConnection> OpenAsync(CancellationToken ct)
    {
        var connection = new SqliteConnection(ConnectionString());
        await connection.OpenAsync(ct);
        return connection;
    }

    public async Task InsertAsync(IReadOnlyList<UsageRecord> records, CancellationToken ct)
    {
        if (records.Count == 0)
            return;
        await using var connection = await OpenAsync(ct);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(ct);
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO usage_calls (ts, trace_id, key_fp, file_name, file_bytes, doc_kind, pages, images, agent, model, attempt,
                input_tokens, cached_tokens, reasoning_tokens, output_tokens, cost_micro_usd, price_key, outcome, duration_ms)
            VALUES ($ts, $trace, $key, $file, $bytes, $kind, $pages, $images, $agent, $model, $attempt,
                $input, $cached, $reasoning, $output, $cost, $price, $outcome, $duration)
            """;
        var names = new[] { "$ts", "$trace", "$key", "$file", "$bytes", "$kind", "$pages", "$images", "$agent", "$model", "$attempt",
            "$input", "$cached", "$reasoning", "$output", "$cost", "$price", "$outcome", "$duration" };
        var parameters = names.Select(n => command.Parameters.Add(new SqliteParameter { ParameterName = n })).ToArray();
        foreach (var r in records)
        {
            Set(parameters, r);
            await command.ExecuteNonQueryAsync(ct);
        }
        await transaction.CommitAsync(ct);
    }

    private static void Set(SqliteParameter[] p, UsageRecord r)
    {
        p[0].Value = r.Timestamp.UtcDateTime.ToString(TimeFormat, CultureInfo.InvariantCulture);
        p[1].Value = (object?)r.TraceId ?? DBNull.Value;
        p[2].Value = r.ApiKeyFingerprint;
        p[3].Value = r.FileName;
        p[4].Value = r.FileBytes;
        p[5].Value = r.DocumentKind;
        p[6].Value = r.Pages;
        p[7].Value = r.Images;
        p[8].Value = r.Agent;
        p[9].Value = r.Model;
        p[10].Value = r.Attempt;
        p[11].Value = (object?)r.InputTokens ?? DBNull.Value;
        p[12].Value = (object?)r.CachedInputTokens ?? DBNull.Value;
        p[13].Value = (object?)r.ReasoningTokens ?? DBNull.Value;
        p[14].Value = (object?)r.OutputTokens ?? DBNull.Value;
        p[15].Value = r.CostUsd is { } cost ? (long)Math.Round(cost * 1_000_000m, MidpointRounding.AwayFromZero) : DBNull.Value;
        p[16].Value = (object?)r.PriceKey ?? DBNull.Value;
        p[17].Value = r.Outcome;
        p[18].Value = r.DurationMs;
    }

    public async Task<IReadOnlyList<UsageRecord>> QueryAsync(UsageQuery query, CancellationToken ct)
    {
        await using var connection = await OpenAsync(ct);
        await using var command = connection.CreateCommand();
        var sql = new StringBuilder("""
            SELECT id, ts, trace_id, key_fp, file_name, file_bytes, doc_kind, pages, images, agent, model, attempt,
                   input_tokens, cached_tokens, reasoning_tokens, output_tokens, cost_micro_usd, price_key, outcome, duration_ms
            FROM usage_calls
            """);
        AddFilters(sql, command, query);
        sql.Append(" ORDER BY id DESC LIMIT $limit");
        command.Parameters.AddWithValue("$limit", query.Limit);
        command.CommandText = sql.ToString();

        var rows = new List<UsageRecord>();
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            rows.Add(new UsageRecord(
                Timestamp: DateTimeOffset.Parse(reader.GetString(1), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal),
                TraceId: reader.IsDBNull(2) ? null : reader.GetString(2),
                ApiKeyFingerprint: reader.GetString(3),
                FileName: reader.GetString(4),
                FileBytes: reader.GetInt64(5),
                DocumentKind: reader.GetString(6),
                Pages: reader.GetInt32(7),
                Images: reader.GetInt32(8),
                Agent: reader.GetString(9),
                Model: reader.GetString(10),
                Attempt: reader.GetInt32(11),
                InputTokens: Nullable(reader, 12),
                CachedInputTokens: Nullable(reader, 13),
                ReasoningTokens: Nullable(reader, 14),
                OutputTokens: Nullable(reader, 15),
                CostUsd: Nullable(reader, 16) is { } micro ? micro / 1_000_000m : null,
                PriceKey: reader.IsDBNull(17) ? null : reader.GetString(17),
                Outcome: reader.GetString(18),
                DurationMs: reader.GetInt64(19),
                Id: reader.GetInt64(0)));
        }
        return rows;
    }

    public async Task<IReadOnlyList<UsageSummaryRow>> SummaryAsync(UsageQuery query, string groupBy, CancellationToken ct)
    {
        var group = groupBy switch
        {
            "day" => "substr(ts, 1, 10)",
            "model" => "model",
            "key" => "key_fp",
            _ => throw new ArgumentException($"groupBy must be one of {string.Join(", ", GroupBys)}", nameof(groupBy))
        };
        await using var connection = await OpenAsync(ct);
        await using var command = connection.CreateCommand();
        var sql = new StringBuilder($"""
            SELECT {group} AS grp, COUNT(*), SUM(COALESCE(input_tokens, 0)), SUM(COALESCE(cached_tokens, 0)),
                   SUM(COALESCE(reasoning_tokens, 0)), SUM(COALESCE(output_tokens, 0)), SUM(COALESCE(cost_micro_usd, 0)),
                   SUM(CASE WHEN cost_micro_usd IS NULL AND input_tokens IS NOT NULL THEN 1 ELSE 0 END)
            FROM usage_calls
            """);
        AddFilters(sql, command, query with { Before = null });
        sql.Append(" GROUP BY grp ORDER BY grp DESC");
        command.CommandText = sql.ToString();

        var rows = new List<UsageSummaryRow>();
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
            rows.Add(new UsageSummaryRow(reader.GetString(0), reader.GetInt64(1), reader.GetInt64(2), reader.GetInt64(3),
                reader.GetInt64(4), reader.GetInt64(5), reader.GetInt64(6) / 1_000_000m, reader.GetInt64(7)));
        return rows;
    }

    private static long? Nullable(SqliteDataReader reader, int ordinal) => reader.IsDBNull(ordinal) ? null : reader.GetInt64(ordinal);

    private static void AddFilters(StringBuilder sql, SqliteCommand command, UsageQuery query)
    {
        var where = new List<string>();
        if (query.From is { } from)
        {
            where.Add("ts >= $from");
            command.Parameters.AddWithValue("$from", from.UtcDateTime.ToString(TimeFormat, CultureInfo.InvariantCulture));
        }
        if (query.To is { } to)
        {
            where.Add("ts < $to");
            command.Parameters.AddWithValue("$to", to.UtcDateTime.ToString(TimeFormat, CultureInfo.InvariantCulture));
        }
        if (!string.IsNullOrEmpty(query.ApiKey))
        {
            where.Add("key_fp = $key");
            command.Parameters.AddWithValue("$key", query.ApiKey);
        }
        if (!string.IsNullOrEmpty(query.Model))
        {
            where.Add("model = $model");
            command.Parameters.AddWithValue("$model", query.Model);
        }
        if (!string.IsNullOrEmpty(query.File))
        {
            // instr, not LIKE, so a % or _ in the search text is just text.
            where.Add("instr(lower(file_name), lower($file)) > 0");
            command.Parameters.AddWithValue("$file", query.File);
        }
        if (query.Before is { } before)
        {
            where.Add("id < $before");
            command.Parameters.AddWithValue("$before", before);
        }
        if (where.Count > 0)
            sql.Append(" WHERE ").Append(string.Join(" AND ", where));
    }
}
