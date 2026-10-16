using System.IO.Compression;
using System.Net;
using System.Text;

namespace RabbiX.Sdk.Tests;

internal sealed record CapturedRequest(
    HttpMethod Method,
    Uri Uri,
    Dictionary<string, string> Headers,
    Dictionary<string, string> ContentHeaders,
    byte[]? Body)
{
    public string BodyText => Body is null ? "" :
        ContentHeaders.GetValueOrDefault("Content-Encoding") == "gzip" ? Encoding.UTF8.GetString(Gunzip(Body)) : Encoding.UTF8.GetString(Body);

    private static byte[] Gunzip(byte[] data)
    {
        using var input = new GZipStream(new MemoryStream(data), CompressionMode.Decompress);
        using var output = new MemoryStream();
        input.CopyTo(output);
        return output.ToArray();
    }
}

/// <summary>Replays queued responses (then <see cref="Fallback"/>) and records every request.</summary>
internal sealed class FakeHandler : HttpMessageHandler
{
    private readonly Queue<Func<HttpResponseMessage>> _responses = new();

    public List<CapturedRequest> Requests { get; } = [];

    public Func<HttpResponseMessage>? Fallback { get; set; }

    public FakeHandler Respond(HttpStatusCode status, string json, int? retryAfterSeconds = null)
    {
        _responses.Enqueue(() => Response(status, json, retryAfterSeconds));
        return this;
    }

    public FakeHandler Fail(Exception exception)
    {
        _responses.Enqueue(() => throw exception);
        return this;
    }

    public static HttpResponseMessage Response(HttpStatusCode status, string json, int? retryAfterSeconds = null)
    {
        var response = new HttpResponseMessage(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
        if (retryAfterSeconds is { } seconds)
        {
            response.Headers.RetryAfter = new(TimeSpan.FromSeconds(seconds));
        }

        return response;
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var body = request.Content is null ? null : await request.Content.ReadAsByteArrayAsync(cancellationToken);
        Requests.Add(new CapturedRequest(
            request.Method,
            request.RequestUri!,
            request.Headers.ToDictionary(h => h.Key, h => string.Join(",", h.Value)),
            request.Content?.Headers.ToDictionary(h => h.Key, h => string.Join(",", h.Value)) ?? [],
            body));

        var next = _responses.Count > 0 ? _responses.Dequeue() : Fallback ?? throw new InvalidOperationException("No response queued.");
        return next();
    }
}

/// <summary>A clock that jumps forward instantly whenever code waits on it, recording each wait.</summary>
internal sealed class InstantTimeProvider : TimeProvider
{
    private DateTimeOffset _now = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    public List<TimeSpan> Delays { get; } = [];

    public override DateTimeOffset GetUtcNow() => _now;

    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        if (dueTime != Timeout.InfiniteTimeSpan)
        {
            Delays.Add(dueTime);
            _now += dueTime;
            ThreadPool.QueueUserWorkItem(_ => callback(state));
        }

        return new NoopTimer();
    }

    private sealed class NoopTimer : ITimer
    {
        public bool Change(TimeSpan dueTime, TimeSpan period) => true;

        public void Dispose() { }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}

internal static class Fake
{
    public const string ApiKey = "test-key";
    public const string BaseUrl = "https://rabbix.test";

    public static RabbiXClient Client(FakeHandler handler, InstantTimeProvider? time = null, TimeSpan? waitTimeout = null) =>
        new(
            new RabbiXOptions
            {
                ApiKey = ApiKey,
                BaseUrl = BaseUrl,
                TimeProvider = time ?? new InstantTimeProvider(),
                WaitTimeout = waitTimeout ?? TimeSpan.FromMinutes(15),
            },
            new HttpClient(handler));

    public static string Verdict(string status, string extra = "") =>
        $$"""{"decisionId":"d-1","status":"{{status}}"{{extra}}}""";
}
