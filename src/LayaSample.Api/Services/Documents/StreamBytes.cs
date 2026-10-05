namespace LayaSample.Api.Services.Documents;

internal static class StreamBytes
{
    /// <summary>
    /// The whole stream as a byte array; leaves the stream at position 0. A <see cref="MemoryStream"/> whose buffer
    /// holds exactly its content (as the endpoint creates it) is shared rather than copied, so treat the result as
    /// read-only.
    /// </summary>
    public static byte[] Read(Stream stream)
    {
        stream.Position = 0;
        if (stream is MemoryStream buffered)
        {
            if (buffered.TryGetBuffer(out var segment) && segment.Offset == 0 && segment.Count == segment.Array!.Length)
                return segment.Array;
            return buffered.ToArray();
        }

        using var ms = new MemoryStream();
        stream.CopyTo(ms);
        stream.Position = 0;
        return ms.ToArray();
    }
}
