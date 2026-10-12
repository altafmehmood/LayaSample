namespace DocIntake.Contracts;

/// <summary>Strips what a client-supplied file name should not carry into a record, a log line or a CSV cell.</summary>
public static class UsageFileName
{
    public const int MaxLength = 255;

    public static string Clean(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
            return "unnamed";
        var leaf = name[(name.LastIndexOfAny(['/', '\\']) + 1)..];
        var cleaned = new string(leaf.Where(c => !char.IsControl(c)).ToArray()).Trim();
        if (cleaned.Length > MaxLength)
            cleaned = cleaned[..MaxLength];
        return cleaned.Length == 0 ? "unnamed" : cleaned;
    }
}
