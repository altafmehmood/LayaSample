namespace DocIntake.Api.Services.Security;

/// <summary>Recognises programs and scripts by their first bytes and their name, so one smuggled into a document is noticed.</summary>
public static class ExecutableSniffer
{
    private static readonly HashSet<string> Extensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".exe", ".dll", ".scr", ".com", ".msi", ".bat", ".cmd", ".ps1", ".vbs", ".vbe", ".js", ".jse", ".wsf", ".hta", ".jar",
        ".lnk", ".sh", ".pif", ".cpl", ".reg", ".apk"
    };

    /// <summary>What the file looks like ("a Windows program"), or null when it looks like data.</summary>
    public static string? Describe(string name, ReadOnlySpan<byte> bytes)
    {
        if (bytes.StartsWith("MZ"u8)) return "a Windows program";
        if (bytes.StartsWith("\u007fELF"u8)) return "a Linux program";
        if (bytes.StartsWith((ReadOnlySpan<byte>)[0xCF, 0xFA, 0xED, 0xFE]) || bytes.StartsWith((ReadOnlySpan<byte>)[0xCE, 0xFA, 0xED, 0xFE])
            || bytes.StartsWith((ReadOnlySpan<byte>)[0xFE, 0xED, 0xFA, 0xCF]) || bytes.StartsWith((ReadOnlySpan<byte>)[0xFE, 0xED, 0xFA, 0xCE]))
            return "a macOS program";
        if (bytes.StartsWith("#!"u8)) return "a script";
        return HasExecutableExtension(name) ? "a program or script (by its name)" : null;
    }

    public static bool HasExecutableExtension(string name) => Extensions.Contains(Path.GetExtension(name));
}
