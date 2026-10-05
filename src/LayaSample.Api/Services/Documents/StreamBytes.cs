namespace LayaSample.Api.Services.Documents;

internal static class StreamBytes
{
    /// <summary>The whole stream as a byte array; leaves the stream at position 0.</summary>
    public static byte[] Read(Stream stream)
    {
        stream.Position = 0;
        byte[] bytes;
        if (stream is MemoryStream buffered)
            bytes = buffered.ToArray();
        else
        {
            using var ms = new MemoryStream();
            stream.CopyTo(ms);
            bytes = ms.ToArray();
        }
        stream.Position = 0;
        return bytes;
    }
}
