namespace LayaSample.Rendering;

/// <summary>
/// Lends a stream to code that disposes what it is given (HttpClient's StreamContent, some parsers) without closing it.
/// The document is one seekable stream that scanning, classification, preparation and rendering each read in turn.
/// </summary>
public sealed class LeaveOpenStream(Stream inner) : Stream
{
    public override bool CanRead => inner.CanRead;
    public override bool CanSeek => inner.CanSeek;
    public override bool CanWrite => false;
    public override long Length => inner.Length;

    public override long Position
    {
        get => inner.Position;
        set => inner.Position = value;
    }

    public override void Flush() { }

    public override int Read(byte[] buffer, int offset, int count) => inner.Read(buffer, offset, count);

    public override int Read(Span<byte> buffer) => inner.Read(buffer);

    public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) => inner.ReadAsync(buffer, cancellationToken);

    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) => inner.ReadAsync(buffer, offset, count, cancellationToken);

    public override int ReadByte() => inner.ReadByte();

    public override long Seek(long offset, SeekOrigin origin) => inner.Seek(offset, origin);

    public override void SetLength(long value) => throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    // Deliberately nothing: the owner of the inner stream disposes it.
    protected override void Dispose(bool disposing) { }

    public override ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
