using System.IO.Compression;
using System.Net.Http.Headers;
using System.Text.Json;

namespace RabbiX.Sdk;

/// <summary>
/// Client for the RabbiX checkpoint. Thread-safe; create one and reuse it.
/// </summary>
/// <remarks>
/// It sends exactly two kinds of request: <c>POST /v1/checkpoints</c> once per checkpoint (plus retries),
/// and <c>GET /v1/checkpoints/{decisionId}</c> while a verdict is pending. Nothing else, and nothing is logged.
/// </remarks>
public sealed class RabbiXClient : IDisposable
{
    internal const string UserAgent = "rabbix-dotnet/0.1.0";
    internal const string DefaultBaseUrl = "https://api.rabbix.invalid";
    internal const int MaxBodyBytes = 10_485_760;
    internal const int GzipThresholdBytes = 8192;
    private static readonly TimeSpan MaxPollDelay = TimeSpan.FromSeconds(30);

    private readonly HttpClient _http;
    private readonly bool _ownsHttp;
    private readonly string _apiKey;
    private readonly Uri _baseUrl;
    private readonly TimeSpan? _waitTimeout;
    private readonly TimeSpan _requestTimeout;
    private readonly int _maxRetries;
    private readonly TimeProvider _time;

    /// <summary>Creates a client.</summary>
    /// <param name="options">Settings; environment variables fill in the API key and base URL when unset.</param>
    /// <param name="httpClient">Optional client, e.g. from <c>IHttpClientFactory</c>. Not disposed by this instance.</param>
    /// <exception cref="RabbiXException">No API key was configured.</exception>
    public RabbiXClient(RabbiXOptions? options = null, HttpClient? httpClient = null)
    {
        options ??= new RabbiXOptions();

        var apiKey = string.IsNullOrWhiteSpace(options.ApiKey) ? Environment.GetEnvironmentVariable("RABBIX_API_KEY") : options.ApiKey;
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            throw new RabbiXException("No RabbiX API key. Set RabbiXOptions.ApiKey or the RABBIX_API_KEY environment variable.");
        }

        var baseUrl = string.IsNullOrWhiteSpace(options.BaseUrl) ? Environment.GetEnvironmentVariable("RABBIX_BASE_URL") : options.BaseUrl;
        if (string.IsNullOrWhiteSpace(baseUrl))
        {
            baseUrl = DefaultBaseUrl;
        }

        ArgumentOutOfRangeException.ThrowIfLessThan(options.MaxRetries, 0, nameof(options.MaxRetries));
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(options.RequestTimeout, TimeSpan.Zero, nameof(options.RequestTimeout));
        if (options.WaitTimeout is { } wait)
        {
            ArgumentOutOfRangeException.ThrowIfLessThan(wait, TimeSpan.Zero, nameof(options.WaitTimeout));
        }

        _apiKey = apiKey;
        // Trailing slash so relative paths append to any path prefix in the base URL.
        _baseUrl = new Uri(baseUrl.TrimEnd('/') + "/", UriKind.Absolute);
        _waitTimeout = options.WaitTimeout;
        _requestTimeout = options.RequestTimeout;
        _maxRetries = options.MaxRetries;
        _time = options.TimeProvider ?? TimeProvider.System;
        _ownsHttp = httpClient is null;
        _http = httpClient ?? new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
    }

    /// <summary>
    /// Submits the checkpoint, waits for the final verdict, and returns the result to act on:
    /// the original on <c>APPROVED</c>, the edited result on <c>MODIFIED</c>.
    /// </summary>
    /// <exception cref="CheckpointRejectedException">Rejected, expired, or any status this SDK does not recognise.</exception>
    /// <exception cref="CheckpointTimeoutException">The wait timeout elapsed while the verdict was pending.</exception>
    /// <exception cref="PayloadTooLargeException">The body exceeds 10 MB. Nothing was sent.</exception>
    /// <exception cref="RabbiXApiException">Non-retryable API error, or retries exhausted.</exception>
    public async Task<JsonElement> CheckpointAsync(CheckpointRequest request, CancellationToken cancellationToken = default)
    {
        var verdict = await CheckpointVerdictAsync(request, cancellationToken).ConfigureAwait(false);
        return verdict.GetResultToActOn(request.Result);
    }

    /// <summary>
    /// Submits a text result and returns the text to act on. A modified result that is not a JSON string
    /// is returned as raw JSON. Throws exactly like <see cref="CheckpointAsync(CheckpointRequest, CancellationToken)"/>.
    /// </summary>
    public async Task<string> CheckpointAsync(string goal, string result, string? decisionClass = null, CancellationToken cancellationToken = default)
    {
        var request = new CheckpointRequest
        {
            Goal = goal,
            Result = JsonSerializer.SerializeToElement(result, RabbiXJson.Default.String),
            DecisionClass = decisionClass,
        };
        var acted = await CheckpointAsync(request, cancellationToken).ConfigureAwait(false);
        return acted.ValueKind == JsonValueKind.String ? acted.GetString()! : acted.GetRawText();
    }

    /// <summary>
    /// Submits the checkpoint and returns the final verdict without throwing for <c>REJECTED</c>/<c>EXPIRED</c>.
    /// Use <see cref="Verdict.GetResultToActOn"/> to apply the fail-closed rule yourself.
    /// </summary>
    /// <exception cref="CheckpointTimeoutException">The wait timeout elapsed while the verdict was pending.</exception>
    /// <exception cref="PayloadTooLargeException">The body exceeds 10 MB. Nothing was sent.</exception>
    /// <exception cref="RabbiXApiException">Non-retryable API error, or retries exhausted.</exception>
    public async Task<Verdict> CheckpointVerdictAsync(CheckpointRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrEmpty(request.Goal))
        {
            throw new ArgumentException("Goal must not be empty.", nameof(request));
        }

        if (request.Result.ValueKind == JsonValueKind.Undefined)
        {
            throw new ArgumentException("Result must be set.", nameof(request));
        }

        var json = JsonSerializer.SerializeToUtf8Bytes(request, RabbiXJson.Default.CheckpointRequest);
        if (json.Length > MaxBodyBytes)
        {
            throw new PayloadTooLargeException(json.Length, MaxBodyBytes);
        }

        var gzip = json.Length > GzipThresholdBytes;
        var body = gzip ? Gzip(json) : json;
        var idempotencyKey = Guid.NewGuid().ToString();
        var url = new Uri(_baseUrl, "v1/checkpoints");

        var (verdict, retryAfter) = await SendAsync(() =>
        {
            var message = new HttpRequestMessage(HttpMethod.Post, url) { Content = new ByteArrayContent(body) };
            message.Headers.Add("Idempotency-Key", idempotencyKey);
            message.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
            if (gzip)
            {
                message.Content.Headers.ContentEncoding.Add("gzip");
            }

            return message;
        }, cancellationToken).ConfigureAwait(false);

        return verdict.Status == VerdictStatus.Pending
            ? await PollAsync(verdict.DecisionId, retryAfter, cancellationToken).ConfigureAwait(false)
            : verdict;
    }

    /// <summary>
    /// Resumes waiting for a decision (e.g. after <see cref="CheckpointTimeoutException"/>) and returns the final
    /// verdict without throwing for <c>REJECTED</c>/<c>EXPIRED</c>. Apply <see cref="Verdict.GetResultToActOn"/> before acting.
    /// </summary>
    public async Task<Verdict> WaitAsync(string decisionId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(decisionId);
        var (verdict, retryAfter) = await GetAsync(decisionId, cancellationToken).ConfigureAwait(false);
        return verdict.Status == VerdictStatus.Pending
            ? await PollAsync(decisionId, retryAfter, cancellationToken).ConfigureAwait(false)
            : verdict;
    }

    /// <summary>Disposes the internal <see cref="HttpClient"/>, if this instance created it.</summary>
    public void Dispose()
    {
        if (_ownsHttp)
        {
            _http.Dispose();
        }
    }

    private async Task<Verdict> PollAsync(string decisionId, TimeSpan? retryAfter, CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(decisionId))
        {
            throw new RabbiXApiException(null, null, "Pending verdict has no decisionId; cannot poll.");
        }

        DateTimeOffset? deadline = _time.GetUtcNow() + _waitTimeout;
        var backoff = TimeSpan.FromSeconds(1);
        while (true)
        {
            var delay = retryAfter ?? backoff;
            backoff = Min(backoff * 2, MaxPollDelay);

            if (deadline is { } end)
            {
                var remaining = end - _time.GetUtcNow();
                if (remaining <= TimeSpan.Zero)
                {
                    throw new CheckpointTimeoutException(decisionId);
                }

                // Poll one last time exactly at the deadline rather than sleeping past it.
                delay = Min(delay, remaining);
            }

            await Task.Delay(delay, _time, cancellationToken).ConfigureAwait(false);

            Verdict verdict;
            (verdict, retryAfter) = await GetAsync(decisionId, cancellationToken).ConfigureAwait(false);
            if (verdict.Status != VerdictStatus.Pending)
            {
                return verdict;
            }
        }
    }

    // Every API error while polling carries the decision id, so the host can resume instead of resubmitting.
    private async Task<(Verdict, TimeSpan?)> GetAsync(string decisionId, CancellationToken cancellationToken)
    {
        var url = new Uri(_baseUrl, "v1/checkpoints/" + Uri.EscapeDataString(decisionId));
        try
        {
            return await SendAsync(() => new HttpRequestMessage(HttpMethod.Get, url), cancellationToken).ConfigureAwait(false);
        }
        catch (RabbiXApiException e) when (e.DecisionId is null)
        {
            throw new RabbiXApiException(e.StatusCode, e.Code, e.Message, e.InnerException, decisionId);
        }
    }

    // Sends with retries on network errors, timeouts, 429 and 5xx. Returns the verdict of a 200/202 response.
    private async Task<(Verdict, TimeSpan?)> SendAsync(Func<HttpRequestMessage> createRequest, CancellationToken cancellationToken)
    {
        for (var attempt = 0; ; attempt++)
        {
            RabbiXApiException failure;
            TimeSpan? retryAfter = null;

            using (var request = createRequest())
            using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
            {
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _apiKey);
                request.Headers.TryAddWithoutValidation("User-Agent", UserAgent);
                timeout.CancelAfter(_requestTimeout);
                try
                {
                    using var response = await _http.SendAsync(request, timeout.Token).ConfigureAwait(false);
                    var body = await response.Content.ReadAsByteArrayAsync(timeout.Token).ConfigureAwait(false);
                    var status = (int)response.StatusCode;
                    retryAfter = RetryAfter(response);

                    if (status is 200 or 202)
                    {
                        return (ParseVerdict(status, body), retryAfter);
                    }

                    failure = ParseError(status, body);
                    if (status != 429 && status < 500)
                    {
                        throw failure;
                    }
                }
                catch (HttpRequestException e)
                {
                    failure = new RabbiXApiException(null, null, $"Network error: {e.Message}", e);
                }
                catch (OperationCanceledException e) when (!cancellationToken.IsCancellationRequested)
                {
                    failure = new RabbiXApiException(null, null, $"Request timed out after {_requestTimeout}.", e);
                }
            }

            if (attempt >= _maxRetries)
            {
                throw failure;
            }

            await Task.Delay(retryAfter ?? RetryBackoff(attempt), _time, cancellationToken).ConfigureAwait(false);
        }
    }

    private static Verdict ParseVerdict(int status, byte[] body)
    {
        try
        {
            return JsonSerializer.Deserialize(body, RabbiXJson.Default.Verdict) ?? throw new JsonException("Empty verdict.");
        }
        catch (JsonException e)
        {
            throw new RabbiXApiException(status, null, "Malformed verdict in API response.", e);
        }
    }

    private static RabbiXApiException ParseError(int status, byte[] body)
    {
        ApiError? error = null;
        try
        {
            error = JsonSerializer.Deserialize(body, RabbiXJson.Default.ApiError);
        }
        catch (JsonException)
        {
            // Not a JSON error body; fall back to the status code.
        }

        return new RabbiXApiException(status, error?.Code, error?.Message ?? $"RabbiX API returned HTTP {status}.");
    }

    private TimeSpan? RetryAfter(HttpResponseMessage response)
    {
        var header = response.Headers.RetryAfter;
        var delay = header?.Delta ?? (header?.Date - _time.GetUtcNow());
        return delay is { } d ? Max(d, TimeSpan.Zero) : null;
    }

    // 0.5 s, 1 s, 2 s, ... with +/-25 % jitter.
    private static TimeSpan RetryBackoff(int attempt) =>
        TimeSpan.FromSeconds(0.5 * Math.Pow(2, attempt) * (0.75 + (0.5 * Random.Shared.NextDouble())));

    private static byte[] Gzip(byte[] data)
    {
        using var output = new MemoryStream();
        using (var gzip = new GZipStream(output, CompressionLevel.Fastest))
        {
            gzip.Write(data);
        }

        return output.ToArray();
    }

    private static TimeSpan Min(TimeSpan a, TimeSpan b) => a < b ? a : b;

    private static TimeSpan Max(TimeSpan a, TimeSpan b) => a > b ? a : b;
}
