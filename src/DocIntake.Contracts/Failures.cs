namespace DocIntake.Contracts;

/// <summary>
/// Why a request could not be answered, in terms of what went wrong rather than how it is reported. Only the gateway
/// turns a kind into an HTTP status (<c>ProblemMapping</c>), so the services below it never mention HTTP.
/// </summary>
public enum FailureKind
{
    /// <summary>The file is not a type that is read (legacy Office, plain text, unknown).</summary>
    Unsupported,

    /// <summary>The file claims a supported type but cannot be read: corrupt, password protected, nothing to extract.</summary>
    Unreadable,

    /// <summary>The file is readable but beyond a configured limit: too many pages or frames, or unpacks too large.</summary>
    OverLimit,

    /// <summary>The malware scanner found something.</summary>
    Malicious,

    /// <summary>A security policy refused the document or the model's answer (active content, hidden text, injection).</summary>
    PolicyRejected,

    /// <summary>The model provider will not or cannot process this document (content filter, too large, not supported).</summary>
    Declined,

    /// <summary>The model provider is rate limiting; the caller may retry after a while.</summary>
    Busy,

    /// <summary>The model provider rejected our credentials. A deployment problem, never the caller's.</summary>
    Misconfigured,

    /// <summary>The model provider took too long.</summary>
    Timeout,

    /// <summary>The model provider failed, or its answer was cut off or unreadable.</summary>
    DependencyFailed,

    /// <summary>A service this one depends on cannot be reached or is saturated; trying again later may work.</summary>
    DependencyUnavailable
}

/// <summary>
/// A failure with a <see cref="FailureKind"/>. The message is safe to show the caller: it names what went wrong and never
/// repeats the document's text or the provider's error text, which can echo credentials.
/// </summary>
public sealed class IntakeFailureException(FailureKind kind, string message, int? retryAfterSeconds = null, Exception? inner = null)
    : Exception(message, inner)
{
    public FailureKind Kind { get; } = kind;

    /// <summary>How long the caller should wait before trying again, when the provider said.</summary>
    public int? RetryAfterSeconds { get; } = retryAfterSeconds;

    /// <summary>Tokens the provider billed for the call that failed, when it answered (a cut-off or refused answer still costs).</summary>
    public ExtractionUsage? Usage { get; init; }

    /// <summary>
    /// The model calls made before this failure, failed ones included. The extraction service sends them with the failure
    /// so the gateway can record what was spent even when the request did not succeed.
    /// </summary>
    public IReadOnlyList<ModelCall> Calls { get; set; } = [];
}
