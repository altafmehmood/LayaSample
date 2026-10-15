using System.Buffers;
using System.Buffers.Binary;
using System.Net.Sockets;
using System.Text;
using System.Diagnostics;
using DocIntake.Contracts;
using DocIntake.ServiceDefaults;
using Microsoft.Extensions.Options;

namespace DocIntake.ScanService;

/// <summary>
/// ClamAV over clamd's TCP socket, using the <c>INSTREAM</c> command: the document goes over as length-prefixed chunks
/// and clamd answers with one line. See the clamd documentation for the protocol; the z prefix makes both sides use NUL
/// instead of newline as the terminator. The socket is plaintext and unauthenticated, which is why only this service, in
/// the same pod, ever speaks to it.
/// </summary>
public sealed class ClamdClient(IOptions<ScanOptions> options, ILogger<ClamdClient> logger)
{
    /// <summary>Memory used per scan, whatever the document's size.</summary>
    public const int ChunkSize = 64 * 1024;

    private const int MaxReplyBytes = 4096;

    private readonly ScanOptions _options = options.Value;

    /// <summary>
    /// Scans a document, reading it once from where it is in fixed-size chunks (never holding it in memory). Never throws for
    /// a scanner problem; that is <see cref="ScanOutcomes.Unavailable"/>.
    /// </summary>
    public async Task<ScanResponse> ScanAsync(Stream document, CancellationToken ct)
    {
        // The round trip to clamd, so a slow scan can be told from a slow upload to this service.
        using var span = IntakeTelemetry.Source.StartActivity("scan.clamd", ActivityKind.Client);
        span?.SetTag("peer.service", "clamd");
        span?.SetTag("server.address", _options.ClamdHost);
        span?.SetTag("server.port", _options.ClamdPort);
        long sent = 0;
        try
        {
            var response = await ScanCoreAsync(document, n => sent += n, ct);
            span?.SetTag("intake.scan.bytes", sent);
            span?.SetTag("intake.scan.result", response.Outcome);
            if (response.Outcome == ScanOutcomes.Unavailable)
                span?.SetStatus(ActivityStatusCode.Error, response.Error);
            return response;
        }
        catch (Exception ex)
        {
            span.RecordFailure(ex);
            throw;
        }
    }

    private async Task<ScanResponse> ScanCoreAsync(Stream document, Action<int> sentBytes, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(_options.TimeoutSeconds));
        var token = timeout.Token;
        var chunk = ArrayPool<byte>.Shared.Rent(ChunkSize);
        try
        {
            using var client = new TcpClient { NoDelay = true };
            await client.ConnectAsync(_options.ClamdHost, _options.ClamdPort, token);
            await using var network = client.GetStream();

            await network.WriteAsync("zINSTREAM\0"u8.ToArray(), token);
            var length = new byte[4];
            try
            {
                int read;
                while ((read = await document.ReadAsync(chunk.AsMemory(0, ChunkSize), token)) > 0)
                {
                    BinaryPrimitives.WriteInt32BigEndian(length, read);
                    await network.WriteAsync(length, token);
                    await network.WriteAsync(chunk.AsMemory(0, read), token);
                    sentBytes(read);
                }
                // A zero-length chunk ends the stream.
                BinaryPrimitives.WriteInt32BigEndian(length, 0);
                await network.WriteAsync(length, token);
            }
            catch (IOException)
            {
                // clamd answers and hangs up when it refuses a document (INSTREAM size limit exceeded): its reply says why.
            }

            return Parse(await ReadReplyAsync(network, token));
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return Unavailable($"no verdict within {_options.TimeoutSeconds} s");
        }
        catch (Exception ex) when (ex is SocketException or IOException or InvalidDataException)
        {
            return Unavailable(ex.Message);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(chunk);
        }
    }

    /// <summary>Is clamd up and answering?</summary>
    public async Task<bool> PingAsync(CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(Math.Min(_options.TimeoutSeconds, 5)));
        try
        {
            using var client = new TcpClient();
            await client.ConnectAsync(_options.ClamdHost, _options.ClamdPort, timeout.Token);
            await using var network = client.GetStream();
            await network.WriteAsync("zPING\0"u8.ToArray(), timeout.Token);
            return await ReadReplyAsync(network, timeout.Token) == "PONG";
        }
        catch (Exception ex) when (ex is SocketException or IOException or InvalidDataException or OperationCanceledException)
        {
            if (ex is OperationCanceledException && ct.IsCancellationRequested)
                throw;
            return false;
        }
    }

    private ScanResponse Unavailable(string reason)
    {
        logger.ScannerNoVerdict(_options.ClamdHost, _options.ClamdPort, reason);
        return new ScanResponse(ScanOutcomes.Unavailable, Error: reason);
    }

    /// <summary>One reply, up to the NUL that ends it (or the connection closing).</summary>
    private static async Task<string> ReadReplyAsync(NetworkStream network, CancellationToken ct)
    {
        var reply = new List<byte>(64);
        var one = new byte[1];
        while (reply.Count < MaxReplyBytes && await network.ReadAsync(one, ct) == 1 && one[0] != 0)
            reply.Add(one[0]);
        if (reply.Count == 0)
            throw new InvalidDataException("the scanner closed the connection without a reply");
        return Encoding.UTF8.GetString(reply.ToArray()).Trim();
    }

    /// <summary>"stream: OK", "stream: Eicar-Test-Signature FOUND" or "... ERROR".</summary>
    private ScanResponse Parse(string reply)
    {
        if (reply.EndsWith(" OK", StringComparison.Ordinal))
            return new ScanResponse(ScanOutcomes.Clean);
        if (reply.EndsWith(" FOUND", StringComparison.Ordinal))
        {
            var body = reply[..^" FOUND".Length];
            var colon = body.IndexOf(':');
            return new ScanResponse(ScanOutcomes.Infected, Signature: (colon >= 0 ? body[(colon + 1)..] : body).Trim());
        }
        // ERROR, or something unexpected: no verdict is not a clean one.
        return Unavailable($"unexpected reply '{reply}'");
    }
}
