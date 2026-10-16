namespace RabbiX.Sdk;

/// <summary>Settings for <see cref="RabbiXClient"/>. Read once, when the client is constructed.</summary>
public sealed class RabbiXOptions
{
    /// <summary>API key. Defaults to the <c>RABBIX_API_KEY</c> environment variable.</summary>
    public string? ApiKey { get; set; }

    /// <summary>API base URL. Defaults to <c>RABBIX_BASE_URL</c>, then <c>https://api.rabbix.invalid</c>.</summary>
    public string? BaseUrl { get; set; }

    /// <summary>How long to wait for a pending verdict. <c>null</c> waits forever.</summary>
    public TimeSpan? WaitTimeout { get; set; } = TimeSpan.FromMinutes(15);

    /// <summary>Timeout for each individual HTTP request.</summary>
    public TimeSpan RequestTimeout { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>Retries after network errors, 429 and 5xx (so at most <c>MaxRetries + 1</c> attempts per request).</summary>
    public int MaxRetries { get; set; } = 3;

    /// <summary>Clock used for retry and poll delays and the wait deadline.</summary>
    public TimeProvider TimeProvider { get; set; } = TimeProvider.System;
}
