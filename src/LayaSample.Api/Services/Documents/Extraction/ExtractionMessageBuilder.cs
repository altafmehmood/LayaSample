using LayaSample.Api.Models;
using LayaSample.Api.Services.Security;
using Microsoft.Extensions.AI;

namespace LayaSample.Api.Services.Documents.Extraction;

public sealed record ExtractionRequest(IReadOnlyList<ChatMessage> Messages, bool UsesImages, IReadOnlyList<string> Warnings);

/// <summary>
/// Turns a prepared document into chat messages. Pages are labelled "--- Page N ---" so the
/// model can cite them; page images go first on each page, with that page's OCR / text-layer text beside them.
/// The document sits in a delimited block the system prompt declares to be data, not instructions.
/// </summary>
public static class ExtractionMessageBuilder
{
    public static ExtractionRequest Build(string systemPrompt, PreparedDocument document, ExtractionOptions options)
    {
        var warnings = new List<string>();
        var contents = new List<AIContent> { new TextContent("<document>") };
        var charsLeft = options.MaxTextChars;
        var imagesSent = 0;
        var imagesSkipped = new List<int>();

        void AddText(string label, string? text)
        {
            if (string.IsNullOrWhiteSpace(text))
                return;
            // The document sits between <document> tags; text that contains a tag cannot be allowed to close the block early.
            text = InjectionPatterns.Neutralise(text);
            if (text.Length > charsLeft)
            {
                warnings.Add($"{label} was cut to fit the {options.MaxTextChars}-character text limit");
                text = text[..Math.Max(0, charsLeft)];
            }
            charsLeft -= text.Length;
            if (text.Length > 0)
                contents.Add(new TextContent($"[{label}]\n{text}"));
        }

        foreach (var page in document.Parts.Where(p => p.Page is not null).GroupBy(p => p.Page!.Value).OrderBy(g => g.Key))
        {
            contents.Add(new TextContent($"--- Page {page.Key} ---"));
            foreach (var part in page.Where(p => p.Role == PartRole.PageImage && p.Data is { Length: > 0 }))
            {
                if (imagesSent >= options.MaxImagePages)
                {
                    imagesSkipped.Add(page.Key);
                    continue;
                }
                contents.Add(new DataContent(part.Data!, part.MediaType) { AdditionalProperties = new() { ["detail"] = options.ImageDetail } });
                imagesSent++;
            }
            foreach (var part in page.Where(p => p.Role is PartRole.OcrText or PartRole.PageText))
                AddText(part.Role == PartRole.OcrText ? "OCR text of this page" : "Text of this page", part.Text);
            foreach (var part in page.Where(p => p.Role == PartRole.StructuredData))
                AddText("Form field values", part.Text);
        }

        foreach (var part in document.Parts.Where(p => p.Page is null))
            AddText(part.Source is null ? "Document data" : $"Attachment {part.Source}", part.Text);

        if (imagesSkipped.Count > 0)
            warnings.Add($"page(s) {string.Join(", ", imagesSkipped)} were sent as text only; at most {options.MaxImagePages} page images are sent");

        contents.Add(new TextContent("</document>"));

        var messages = new List<ChatMessage>
        {
            new(ChatRole.System, systemPrompt),
            new(ChatRole.User, contents)
        };
        return new ExtractionRequest(messages, imagesSent > 0, warnings);
    }
}
