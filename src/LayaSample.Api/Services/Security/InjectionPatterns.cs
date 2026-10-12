using System.Text.RegularExpressions;

namespace LayaSample.Api.Services.Security;

/// <summary>
/// Phrases that address the model instead of the reader of the document. Precision matters more than recall here: a hit
/// rejects the upload, and lending documents are full of ordinary uses of "instructions" and "system". Each pattern
/// therefore needs the unmistakable shape of an attack ("ignore the previous instructions", a chat-template token), not a
/// single word. This is one layer; the model call also treats the document as data and answers in a fixed schema.
/// </summary>
public static partial class InjectionPatterns
{
    private const RegexOptions Options = RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled;
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(1);

    private static readonly (string Id, Regex Pattern)[] Patterns =
    [
        ("ignore-instructions",
            new(@"\b(ignore|disregard|forget|override|bypass)\b\W+(?:(?:all|any|every|each|of|the|your|previous|prior|above|earlier|preceding|foregoing|system|original)\W+){1,6}(instructions?|prompts?|rules|guidelines|directions|constraints|context)\b", Options, Timeout)),
        ("model-addressed-role",
            new(@"\byou\s+are\s+(now\s+)?(an?\s+)?(ai|a\.i\.|assistant|chatbot|language\s+model|llm|dan)\b|\bact\s+as\s+(an?\s+)?(unrestricted|jailbroken)\b|\b(developer|jailbreak)\s+mode\b", Options, Timeout)),
        ("system-prompt",
            new(@"\b(system|developer)\s+prompt\b|\bprompt\s+injection\b", Options, Timeout)),
        ("as-an-ai",
            new(@"\bas\s+an\s+ai\s+(language\s+)?model\b", Options, Timeout)),
        ("role-marker",
            // "System: LoanPro" and "Developer: Acme Homes" are ordinary labels; only a role marker followed by an instruction counts.
            new(@"^\s*(system|assistant)\s*:\s*(you|ignore|disregard|do|never|always|respond|answer|output|extract|return|reveal)\b", Options | RegexOptions.Multiline, Timeout)),
        ("chat-template-token",
            new(@"<\|(im_start|im_end|system|user|assistant|endoftext)\|>|\[/?INST\]|<<\s*/?SYS\s*>>|<\s*/?\s*(system|assistant)\s*>", Options, Timeout)),
        ("document-delimiter",
            new(@"<\s*/?\s*document\s*>", Options, Timeout)),
        ("reveal-instructions",
            new(@"\b(reveal|repeat|print|show|output|leak)\b\W+(?:\w+\W+){0,3}(system|hidden|initial|original|secret)\W+(instructions?|prompt|message)\b", Options, Timeout)),
    ];

    /// <summary>
    /// Ids that are reported but never refuse a document: a literal document tag is neutralised before the model sees it, and an
    /// XML attachment can have a document element of its own.
    /// </summary>
    public static bool IsInformational(string id) => id == "document-delimiter";

    /// <summary>Ids of the patterns the text matches, each once. Empty for ordinary text.</summary>
    public static IReadOnlyList<string> Find(string? text)
    {
        if (string.IsNullOrEmpty(text))
            return [];
        var found = new List<string>();
        foreach (var (id, pattern) in Patterns)
        {
            try
            {
                if (pattern.IsMatch(text))
                    found.Add(id);
            }
            catch (RegexMatchTimeoutException)
            {
                // A text built to stall the matcher is not ordinary text either.
                found.Add(id + "-timeout");
            }
        }
        return found;
    }

    /// <summary>
    /// Stops text inside the document block from forging the tags that close it. A tag is made inert by putting a zero-width
    /// joiner after its opening bracket, which a reader of the page does not see but which no longer matches the tag.
    /// </summary>
    public static string Neutralise(string text) => DelimiterTag().Replace(text, m => "<‍" + m.Value[1..]);

    [GeneratedRegex(@"<\s*/?\s*document\s*>", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex DelimiterTag();
}
