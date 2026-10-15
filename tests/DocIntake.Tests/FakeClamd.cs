using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;

namespace DocIntake.Tests;

/// <summary>A stand-in for clamd that speaks its INSTREAM and PING commands and records what it was sent.</summary>
internal sealed class FakeClamd : IDisposable
{
    private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
    private readonly CancellationTokenSource _stop = new();
    private readonly Task _serving;

    public string? Reply { get; init; }
    /// <summary>Answer ERROR and hang up once this many bytes have arrived, as clamd does at StreamMaxLength.</summary>
    public long? SizeLimit { get; init; }
    /// <summary>Accept the document and then never answer.</summary>
    public bool Silent { get; init; }

    public List<string> Commands { get; } = [];
    public List<int> Chunks { get; } = [];
    public long Received { get; private set; }
    public string? ReceivedHash { get; private set; }
    public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;

    public FakeClamd()
    {
        _listener.Start();
        _serving = Task.Run(Serve);
    }

    private async Task Serve()
    {
        try
        {
            while (!_stop.IsCancellationRequested)
            {
                using var client = await _listener.AcceptTcpClientAsync(_stop.Token);
                await using var stream = client.GetStream();
                try { await Handle(stream); } catch (IOException) { }
            }
        }
        catch (OperationCanceledException) { }
    }

    private async Task Handle(NetworkStream stream)
    {
        var command = new List<byte>();
        var one = new byte[1];
        while (await stream.ReadAsync(one, _stop.Token) == 1 && one[0] != 0)
            command.Add(one[0]);
        var name = Encoding.ASCII.GetString(command.ToArray());
        Commands.Add(name);

        if (name == "zPING")
        {
            await Send(stream, "PONG");
            return;
        }

        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var length = new byte[4];
        while (true)
        {
            await stream.ReadExactlyAsync(length, _stop.Token);
            var size = BinaryPrimitives.ReadInt32BigEndian(length);
            if (size == 0) break;
            var data = new byte[size];
            await stream.ReadExactlyAsync(data, _stop.Token);
            Chunks.Add(size);
            Received += size;
            hash.AppendData(data);
            if (SizeLimit is { } limit && Received > limit)
            {
                await Send(stream, "INSTREAM size limit exceeded. ERROR");
                return;
            }
        }
        ReceivedHash = Convert.ToHexString(hash.GetHashAndReset());
        if (!Silent)
            await Send(stream, Reply ?? "stream: OK");
        else
            await Task.Delay(Timeout.Infinite, _stop.Token);
    }

    private async Task Send(NetworkStream stream, string text) =>
        await stream.WriteAsync(Encoding.ASCII.GetBytes(text + "\0"), _stop.Token);

    public void Dispose()
    {
        _stop.Cancel();
        _listener.Stop();
        try { _serving.Wait(TimeSpan.FromSeconds(2)); } catch (AggregateException) { }
        _stop.Dispose();
    }
}
