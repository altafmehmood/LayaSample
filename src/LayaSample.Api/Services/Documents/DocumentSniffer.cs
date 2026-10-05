using System.IO.Compression;

namespace LayaSample.Api.Services.Documents;

/// <summary>Identifies a file's media type from its content; names and client-supplied types are never trusted.</summary>
public static class DocumentSniffer
{
    public const string Pdf = "application/pdf";
    public const string Xlsx = "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet";
    public const string Docx = "application/vnd.openxmlformats-officedocument.wordprocessingml.document";
    public const string Zip = "application/zip";
    /// <summary>OLE compound file: legacy .xls/.doc, or a password-protected .xlsx/.docx.</summary>
    public const string Ole = "application/x-ole-storage";
    public const string Tiff = "image/tiff";
    public const string Png = "image/png";
    public const string Jpeg = "image/jpeg";
    public const string Xml = "application/xml";
    public const string Json = "application/json";
    public const string Text = "text/plain";
    public const string Binary = "application/octet-stream";

    public static bool IsImage(string mediaType) => mediaType is Tiff or Png or Jpeg;
    public static bool IsTextual(string mediaType) => mediaType is Xml or Json or Text;

    public static string Sniff(byte[] bytes) => Sniff(new MemoryStream(bytes, writable: false));

    public static string Sniff(Stream stream)
    {
        var start = stream.Position;
        var buffer = new byte[1024];
        var read = stream.ReadAtLeast(buffer, buffer.Length, throwOnEndOfStream: false);
        stream.Position = start;
        var head = buffer.AsSpan(0, read);

        if (head.IndexOf("%PDF-"u8) >= 0) return Pdf;
        if (head.StartsWith("PK\u0003\u0004"u8)) return SniffZip(stream, start);
        if (head.StartsWith((ReadOnlySpan<byte>)[0xD0, 0xCF, 0x11, 0xE0, 0xA1, 0xB1, 0x1A, 0xE1])) return Ole;
        if (head.StartsWith("II*\0"u8) || head.StartsWith("MM\0*"u8)) return Tiff;
        if (head.StartsWith((ReadOnlySpan<byte>)[0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A])) return Png;
        if (head.StartsWith((ReadOnlySpan<byte>)[0xFF, 0xD8, 0xFF])) return Jpeg;

        if (head.Length == 0 || head.Contains((byte)0)) return Binary;
        var text = head.StartsWith((ReadOnlySpan<byte>)[0xEF, 0xBB, 0xBF]) ? head[3..] : head;
        text = text.TrimStart(" \t\r\n"u8);
        if (text.Length > 0 && text[0] == '<') return Xml;
        if (text.Length > 0 && text[0] is (byte)'{' or (byte)'[') return Json;
        return Text;
    }

    private static string SniffZip(Stream stream, long start)
    {
        try
        {
            using var zip = new ZipArchive(stream, ZipArchiveMode.Read, leaveOpen: true);
            if (zip.GetEntry("xl/workbook.xml") is not null) return Xlsx;
            if (zip.GetEntry("word/document.xml") is not null) return Docx;
            return Zip;
        }
        catch (InvalidDataException)
        {
            return Zip;
        }
        finally
        {
            stream.Position = start;
        }
    }
}
