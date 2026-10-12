namespace DocIntake.Api.Models;

public enum RiskSeverity
{
    /// <summary>Reported and counted, never a reason to refuse the document (a fillable form legitimately carries JavaScript).</summary>
    Info,

    /// <summary>Refuses the document with 422 while <c>Security:OnFinding</c> is <c>reject</c>.</summary>
    Block
}

/// <summary>
/// Something in an upload that a reader should know about before trusting what the model extracts from it. The detail
/// names the kind of thing and where; it never repeats the document's own text.
/// </summary>
public sealed record RiskFinding(string Kind, RiskSeverity Severity, int? Page, string Detail);

public static class RiskKinds
{
    /// <summary>A PDF action that runs code or a program (JavaScript, Launch), or embedded rich media.</summary>
    public const string ActiveContent = "active-content";

    /// <summary>An embedded file that is a program or a script.</summary>
    public const string ExecutableAttachment = "executable-attachment";

    /// <summary>An Office macro, ActiveX control, or an embedded or remote object.</summary>
    public const string Macro = "macro";

    /// <summary>Text the reader of the page cannot see: tiny, near-white or outside the page.</summary>
    public const string HiddenText = "hidden-text";

    /// <summary>Text that addresses the model rather than the reader ("ignore the previous instructions").</summary>
    public const string PromptInjection = "prompt-injection";
}
