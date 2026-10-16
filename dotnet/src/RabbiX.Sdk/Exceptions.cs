namespace RabbiX.Sdk;

/// <summary>Base class for every error this SDK throws.</summary>
public class RabbiXException : Exception
{
    /// <summary>Creates the exception.</summary>
    public RabbiXException(string message, Exception? innerException = null) : base(message, innerException) { }
}

/// <summary>The verdict does not allow acting on the result (rejected, expired, or not understood).</summary>
public sealed class CheckpointRejectedException(Verdict verdict)
    : RabbiXException($"Checkpoint {verdict.DecisionId} was not approved (status: {verdict.Status}){(verdict.Reason is null ? "" : $": {verdict.Reason}")}.")
{
    /// <summary>The verdict that stopped the result.</summary>
    public Verdict Verdict { get; } = verdict;
}

/// <summary>No final verdict arrived within <see cref="RabbiXOptions.WaitTimeout"/>. Resume with <see cref="RabbiXClient.WaitAsync"/>.</summary>
public sealed class CheckpointTimeoutException(string decisionId)
    : RabbiXException($"No final verdict for checkpoint {decisionId} before the wait timeout. Resume with WaitAsync(\"{decisionId}\").")
{
    /// <summary>The pending decision.</summary>
    public string DecisionId { get; } = decisionId;
}

/// <summary>The serialized request exceeds the 10 MB limit. Nothing was sent.</summary>
public sealed class PayloadTooLargeException(long size, long limit)
    : RabbiXException($"Checkpoint body is {size} bytes; the limit is {limit} bytes. Nothing was sent.")
{
    /// <summary>Uncompressed body size in bytes.</summary>
    public long Size { get; } = size;

    /// <summary>Maximum uncompressed body size in bytes.</summary>
    public long Limit { get; } = limit;
}

/// <summary>The API returned a non-retryable error, or retries were exhausted.</summary>
public sealed class RabbiXApiException(int? statusCode, string? code, string message, Exception? innerException = null, string? decisionId = null)
    : RabbiXException(decisionId is null ? message : $"{message} (decisionId: {decisionId}; resume with WaitAsync(\"{decisionId}\"))", innerException)
{
    /// <summary>
    /// The decision being polled when the error happened, or null when the initial submission failed.
    /// When set, the checkpoint exists: resume with <see cref="RabbiXClient.WaitAsync"/> instead of resubmitting.
    /// </summary>
    public string? DecisionId { get; } = decisionId;

    /// <summary>HTTP status code, or null when no response was received (network failure or request timeout).</summary>
    public int? StatusCode { get; } = statusCode;

    /// <summary>Error code from the response body, if any.</summary>
    public string? Code { get; } = code;
}
