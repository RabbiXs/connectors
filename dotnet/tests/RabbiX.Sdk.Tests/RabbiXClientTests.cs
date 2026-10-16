using System.Net;
using System.Text.Json;

namespace RabbiX.Sdk.Tests;

public class RabbiXClientTests
{
    private static JsonElement Json(string json) => JsonDocument.Parse(json).RootElement.Clone();

    [Test]
    public async Task Approved_returns_original_result()
    {
        var handler = new FakeHandler().Respond(HttpStatusCode.OK, Fake.Verdict("APPROVED"));
        using var client = Fake.Client(handler);

        var result = await client.CheckpointAsync("Refund order 42", "Refunded 10 EUR");

        await Assert.That(result).IsEqualTo("Refunded 10 EUR");
        await Assert.That(handler.Requests).Count().IsEqualTo(1);
    }

    [Test]
    public async Task Modified_returns_edited_result()
    {
        var handler = new FakeHandler().Respond(HttpStatusCode.OK, Fake.Verdict("MODIFIED", ""","result":{"amount":5}"""));
        using var client = Fake.Client(handler);

        var result = await client.CheckpointAsync(new CheckpointRequest { Goal = "g", Result = Json("""{"amount":10}""") });

        await Assert.That(result.GetProperty("amount").GetInt32()).IsEqualTo(5);
    }

    [Test]
    [Arguments("REJECTED")]
    [Arguments("EXPIRED")]
    [Arguments("SOMETHING_NEW")]
    [Arguments("approved")]
    public async Task Non_approval_throws(string status)
    {
        var handler = new FakeHandler().Respond(HttpStatusCode.OK, Fake.Verdict(status, ""","reason":"policy" """));
        using var client = Fake.Client(handler);

        var e = await Assert.ThrowsAsync<CheckpointRejectedException>(() => client.CheckpointAsync("g", "r"));

        await Assert.That(e!.Verdict.DecisionId).IsEqualTo("d-1");
        await Assert.That(e.Verdict.Reason).IsEqualTo("policy");
    }

    [Test]
    public async Task Modified_without_result_throws()
    {
        var handler = new FakeHandler().Respond(HttpStatusCode.OK, Fake.Verdict("MODIFIED"));
        using var client = Fake.Client(handler);

        await Assert.ThrowsAsync<CheckpointRejectedException>(() => client.CheckpointAsync("g", "r"));
    }

    [Test]
    public async Task Missing_status_throws_api_exception()
    {
        var handler = new FakeHandler().Respond(HttpStatusCode.OK, """{"decisionId":"d-1"}""");
        using var client = Fake.Client(handler);

        await Assert.ThrowsAsync<RabbiXApiException>(() => client.CheckpointAsync("g", "r"));
    }

    [Test]
    public async Task CheckpointVerdict_returns_rejection_without_throwing()
    {
        var handler = new FakeHandler().Respond(HttpStatusCode.OK, Fake.Verdict("REJECTED", ""","route":"HUMAN_ONLY","reason":"no" """));
        using var client = Fake.Client(handler);

        var verdict = await client.CheckpointVerdictAsync(new CheckpointRequest { Goal = "g", Result = Json("\"r\"") });

        await Assert.That(verdict.Status).IsEqualTo(VerdictStatus.Rejected);
        await Assert.That(verdict.Route).IsEqualTo("HUMAN_ONLY");
        await Assert.That(verdict.Reason).IsEqualTo("no");
    }

    [Test]
    public async Task Pending_is_polled_until_final_honouring_retry_after()
    {
        var time = new InstantTimeProvider();
        var handler = new FakeHandler()
            .Respond(HttpStatusCode.Accepted, Fake.Verdict("PENDING"), retryAfterSeconds: 5)
            .Respond(HttpStatusCode.OK, Fake.Verdict("PENDING"), retryAfterSeconds: 7)
            .Respond(HttpStatusCode.OK, Fake.Verdict("APPROVED"));
        using var client = Fake.Client(handler, time);

        var result = await client.CheckpointAsync("g", "r");

        await Assert.That(result).IsEqualTo("r");
        await Assert.That(time.Delays).IsEquivalentTo([TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(7)]);
        await Assert.That(handler.Requests.Select(r => $"{r.Method} {r.Uri}"))
            .IsEquivalentTo([
                "POST https://rabbix.test/v1/checkpoints",
                "GET https://rabbix.test/v1/checkpoints/d-1",
                "GET https://rabbix.test/v1/checkpoints/d-1",
            ]);
        var get = handler.Requests[1];
        await Assert.That(get.Headers.Keys).IsEquivalentTo(["Authorization", "User-Agent"]);
        await Assert.That(get.Body).IsNull();
    }

    [Test]
    public async Task Wait_timeout_throws_with_decision_id_after_backing_off()
    {
        var time = new InstantTimeProvider();
        var handler = new FakeHandler().Respond(HttpStatusCode.Accepted, Fake.Verdict("PENDING"));
        handler.Fallback = () => FakeHandler.Response(HttpStatusCode.OK, Fake.Verdict("PENDING"));
        using var client = Fake.Client(handler, time, waitTimeout: TimeSpan.FromSeconds(10));

        var e = await Assert.ThrowsAsync<CheckpointTimeoutException>(() => client.CheckpointAsync("g", "r"));

        await Assert.That(e!.DecisionId).IsEqualTo("d-1");
        // 1 s doubling, the last wait cut to the deadline, then one final poll at the deadline.
        await Assert.That(time.Delays).IsEquivalentTo(new[] { 1, 2, 4, 3 }.Select(s => TimeSpan.FromSeconds(s)));
        await Assert.That(handler.Requests).Count().IsEqualTo(5);
    }

    [Test]
    public async Task Poll_backoff_is_capped_at_30_seconds()
    {
        var time = new InstantTimeProvider();
        var handler = new FakeHandler().Respond(HttpStatusCode.Accepted, Fake.Verdict("PENDING"));
        for (var i = 0; i < 6; i++)
        {
            handler.Respond(HttpStatusCode.OK, Fake.Verdict("PENDING"));
        }

        handler.Respond(HttpStatusCode.OK, Fake.Verdict("APPROVED"));
        using var client = Fake.Client(handler, time);

        await client.CheckpointAsync("g", "r");

        await Assert.That(time.Delays).IsEquivalentTo(new[] { 1, 2, 4, 8, 16, 30, 30 }.Select(s => TimeSpan.FromSeconds(s)));
    }

    [Test]
    public async Task WaitAsync_resumes_polling()
    {
        var time = new InstantTimeProvider();
        var handler = new FakeHandler()
            .Respond(HttpStatusCode.OK, Fake.Verdict("PENDING"))
            .Respond(HttpStatusCode.OK, Fake.Verdict("EXPIRED"));
        using var client = Fake.Client(handler, time);

        var verdict = await client.WaitAsync("d-1");

        await Assert.That(verdict.Status).IsEqualTo(VerdictStatus.Expired);
        await Assert.That(handler.Requests.All(r => r.Method == HttpMethod.Get)).IsTrue();
        await Assert.That(time.Delays).IsEquivalentTo([TimeSpan.FromSeconds(1)]);
        await Assert.ThrowsAsync<CheckpointRejectedException>(() => Task.FromResult(verdict.GetResultToActOn(Json("\"r\""))));
    }

    [Test]
    public async Task Server_error_is_retried_with_same_idempotency_key()
    {
        var time = new InstantTimeProvider();
        var handler = new FakeHandler()
            .Respond(HttpStatusCode.ServiceUnavailable, """{"code":"unavailable","message":"later"}""")
            .Respond(HttpStatusCode.TooManyRequests, """{"code":"rate_limited","message":"slow down"}""", retryAfterSeconds: 2)
            .Fail(new HttpRequestException("connection reset"))
            .Respond(HttpStatusCode.OK, Fake.Verdict("APPROVED"));
        using var client = Fake.Client(handler, time);

        await client.CheckpointAsync("g", "r");

        await Assert.That(handler.Requests).Count().IsEqualTo(4);
        await Assert.That(handler.Requests.Select(r => r.Headers["Idempotency-Key"]).Distinct()).Count().IsEqualTo(1);
        await Assert.That(handler.Requests.Select(r => r.BodyText).Distinct()).Count().IsEqualTo(1);
        await Assert.That(time.Delays[0]).IsBetween(TimeSpan.FromSeconds(0.375), TimeSpan.FromSeconds(0.625));
        await Assert.That(time.Delays[1]).IsEqualTo(TimeSpan.FromSeconds(2));
        await Assert.That(time.Delays[2]).IsBetween(TimeSpan.FromSeconds(1.5), TimeSpan.FromSeconds(2.5));
    }

    [Test]
    public async Task Each_checkpoint_call_gets_a_new_idempotency_key()
    {
        var handler = new FakeHandler()
            .Respond(HttpStatusCode.OK, Fake.Verdict("APPROVED"))
            .Respond(HttpStatusCode.OK, Fake.Verdict("APPROVED"));
        using var client = Fake.Client(handler);

        await client.CheckpointAsync("g", "r");
        await client.CheckpointAsync("g", "r");

        await Assert.That(handler.Requests[0].Headers["Idempotency-Key"]).IsNotEqualTo(handler.Requests[1].Headers["Idempotency-Key"]);
    }

    [Test]
    public async Task Exhausted_retries_throw_api_exception()
    {
        var handler = new FakeHandler { Fallback = () => FakeHandler.Response(HttpStatusCode.BadGateway, "<html>bad gateway</html>") };
        using var client = Fake.Client(handler);

        var e = await Assert.ThrowsAsync<RabbiXApiException>(() => client.CheckpointAsync("g", "r"));

        await Assert.That(e!.StatusCode).IsEqualTo(502);
        await Assert.That(handler.Requests).Count().IsEqualTo(4);
    }

    [Test]
    public async Task Network_failure_after_retries_throws_api_exception_without_status()
    {
        var handler = new FakeHandler();
        handler.Fallback = () => throw new HttpRequestException("no route to host");
        using var client = Fake.Client(handler);

        var e = await Assert.ThrowsAsync<RabbiXApiException>(() => client.CheckpointAsync("g", "r"));

        await Assert.That(e!.StatusCode).IsNull();
        await Assert.That(e.InnerException).IsTypeOf<HttpRequestException>();
        await Assert.That(handler.Requests).Count().IsEqualTo(4);
    }

    [Test]
    public async Task Polling_network_failure_carries_decision_id()
    {
        var handler = new FakeHandler().Respond(HttpStatusCode.Accepted, """{"decisionId":"dec_1","status":"PENDING"}""");
        handler.Fallback = () => throw new HttpRequestException("no route to host");
        using var client = Fake.Client(handler);

        var e = await Assert.ThrowsAsync<RabbiXApiException>(() => client.CheckpointAsync("g", "r"));

        await Assert.That(e!.DecisionId).IsEqualTo("dec_1");
        await Assert.That(e.StatusCode).IsNull();
        await Assert.That(e.InnerException).IsTypeOf<HttpRequestException>();
        await Assert.That(e.Message).Contains("dec_1");
        await Assert.That(handler.Requests).Count().IsEqualTo(5);
    }

    [Test]
    public async Task Polling_server_errors_carry_decision_id()
    {
        var handler = new FakeHandler().Respond(HttpStatusCode.Accepted, """{"decisionId":"dec_1","status":"PENDING"}""");
        handler.Fallback = () => FakeHandler.Response(HttpStatusCode.InternalServerError, "{}");
        using var client = Fake.Client(handler);

        var e = await Assert.ThrowsAsync<RabbiXApiException>(() => client.CheckpointVerdictAsync(new CheckpointRequest { Goal = "g", Result = Json("\"r\"") }));

        await Assert.That(e!.DecisionId).IsEqualTo("dec_1");
        await Assert.That(e.StatusCode).IsEqualTo(500);
        await Assert.That(e.Message).Contains("dec_1");
        await Assert.That(handler.Requests).Count().IsEqualTo(5);
    }

    [Test]
    public async Task WaitAsync_not_found_carries_decision_id()
    {
        var handler = new FakeHandler().Respond(HttpStatusCode.NotFound, """{"code":"not_found","message":"unknown decision"}""");
        using var client = Fake.Client(handler);

        var e = await Assert.ThrowsAsync<RabbiXApiException>(() => client.WaitAsync("dec_1"));

        await Assert.That(e!.DecisionId).IsEqualTo("dec_1");
        await Assert.That(e.StatusCode).IsEqualTo(404);
        await Assert.That(e.Code).IsEqualTo("not_found");
        await Assert.That(e.Message).Contains("unknown decision").And.Contains("dec_1");
    }

    [Test]
    public async Task Submission_failure_has_no_decision_id()
    {
        var handler = new FakeHandler { Fallback = () => FakeHandler.Response(HttpStatusCode.InternalServerError, "{}") };
        using var client = Fake.Client(handler);

        var e = await Assert.ThrowsAsync<RabbiXApiException>(() => client.CheckpointAsync("g", "r"));

        await Assert.That(e!.DecisionId).IsNull();
    }

    [Test]
    [Arguments(HttpStatusCode.BadRequest)]
    [Arguments(HttpStatusCode.Unauthorized)]
    [Arguments(HttpStatusCode.RequestEntityTooLarge)]
    public async Task Client_errors_are_not_retried(HttpStatusCode status)
    {
        var handler = new FakeHandler().Respond(status, """{"code":"invalid_request","message":"goal is required"}""");
        using var client = Fake.Client(handler);

        var e = await Assert.ThrowsAsync<RabbiXApiException>(() => client.CheckpointAsync("g", "r"));

        await Assert.That(e!.StatusCode).IsEqualTo((int)status);
        await Assert.That(e.Code).IsEqualTo("invalid_request");
        await Assert.That(e.Message).IsEqualTo("goal is required");
        await Assert.That(handler.Requests).Count().IsEqualTo(1);
    }

    [Test]
    public async Task Sends_exactly_the_documented_request()
    {
        var handler = new FakeHandler().Respond(HttpStatusCode.OK, Fake.Verdict("APPROVED"));
        using var client = Fake.Client(handler);

        await client.CheckpointAsync(new CheckpointRequest
        {
            Goal = "Refund order 42",
            Result = Json("""{"amount":10,"currency":"EUR"}"""),
            DecisionClass = "refund",
            Agent = new AgentInfo { Name = "refunder", Framework = "agent-framework" },
            Metadata = new Dictionary<string, string> { ["ticket"] = "T-1" },
        });

        var request = handler.Requests.Single();
        await Assert.That(request.Method).IsEqualTo(HttpMethod.Post);
        await Assert.That(request.Uri.ToString()).IsEqualTo("https://rabbix.test/v1/checkpoints");
        await Assert.That(request.BodyText).IsEqualTo(
            """{"goal":"Refund order 42","result":{"amount":10,"currency":"EUR"},"decisionClass":"refund","agent":{"name":"refunder","framework":"agent-framework"},"metadata":{"ticket":"T-1"}}""");
        await Assert.That(request.Headers.Keys).IsEquivalentTo(["Authorization", "Idempotency-Key", "User-Agent"]);
        await Assert.That(request.Headers["Authorization"]).IsEqualTo("Bearer test-key");
        await Assert.That(request.Headers["User-Agent"]).IsEqualTo("rabbix-dotnet/0.1.0");
        await Assert.That(Guid.TryParse(request.Headers["Idempotency-Key"], out _)).IsTrue();
        await Assert.That(request.ContentHeaders.Keys.Except(["Content-Length"])).IsEquivalentTo(["Content-Type"]);
        await Assert.That(request.ContentHeaders["Content-Type"]).IsEqualTo("application/json");
    }

    [Test]
    public async Task Omits_unset_optional_fields()
    {
        var handler = new FakeHandler().Respond(HttpStatusCode.OK, Fake.Verdict("APPROVED"));
        using var client = Fake.Client(handler);

        await client.CheckpointAsync("g", "r");

        await Assert.That(handler.Requests.Single().BodyText).IsEqualTo("""{"goal":"g","result":"r"}""");
    }

    [Test]
    public async Task Bodies_over_8_KB_are_gzipped_in_full()
    {
        var handler = new FakeHandler().Respond(HttpStatusCode.OK, Fake.Verdict("APPROVED"));
        using var client = Fake.Client(handler);
        var big = new string('x', 20_000);

        await client.CheckpointAsync("g", big);

        var request = handler.Requests.Single();
        await Assert.That(request.ContentHeaders["Content-Encoding"]).IsEqualTo("gzip");
        await Assert.That(request.Body!.Length).IsLessThan(20_000);
        await Assert.That(request.BodyText).IsEqualTo($$"""{"goal":"g","result":"{{big}}"}""");
    }

    [Test]
    public async Task Bodies_over_10_MB_throw_before_any_request()
    {
        var handler = new FakeHandler();
        using var client = Fake.Client(handler);

        var e = await Assert.ThrowsAsync<PayloadTooLargeException>(() => client.CheckpointAsync("g", new string('x', 10_485_760)));

        await Assert.That(e!.Limit).IsEqualTo(10_485_760);
        await Assert.That(handler.Requests).IsEmpty();
    }

    [Test]
    public async Task Cancellation_is_not_retried_or_wrapped()
    {
        var handler = new FakeHandler { Fallback = () => FakeHandler.Response(HttpStatusCode.OK, Fake.Verdict("PENDING")) };
        using var client = Fake.Client(handler);
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAsync<TaskCanceledException>(() => client.CheckpointAsync("g", "r", cancellationToken: cts.Token));
    }

    [Test]
    public async Task Base_url_path_prefix_is_kept()
    {
        var handler = new FakeHandler().Respond(HttpStatusCode.OK, Fake.Verdict("APPROVED"));
        using var client = new RabbiXClient(new RabbiXOptions { ApiKey = "k", BaseUrl = "https://proxy.test/rabbix/" }, new HttpClient(handler));

        await client.CheckpointAsync("g", "r");

        await Assert.That(handler.Requests.Single().Uri.ToString()).IsEqualTo("https://proxy.test/rabbix/v1/checkpoints");
    }
}

[NotInParallel("environment")]
public class RabbiXClientEnvironmentTests
{
    [After(Test)]
    public void ClearEnvironment()
    {
        Environment.SetEnvironmentVariable("RABBIX_API_KEY", null);
        Environment.SetEnvironmentVariable("RABBIX_BASE_URL", null);
    }

    [Test]
    public async Task Missing_api_key_throws_at_construction()
    {
        Environment.SetEnvironmentVariable("RABBIX_API_KEY", null);

        await Assert.That(() => new RabbiXClient()).Throws<RabbiXException>();
    }

    [Test]
    public async Task Api_key_and_base_url_fall_back_to_environment()
    {
        Environment.SetEnvironmentVariable("RABBIX_API_KEY", "env-key");
        Environment.SetEnvironmentVariable("RABBIX_BASE_URL", "https://env.test");
        var handler = new FakeHandler().Respond(HttpStatusCode.OK, Fake.Verdict("APPROVED"));
        using var client = new RabbiXClient(httpClient: new HttpClient(handler));

        await client.CheckpointAsync("g", "r");

        var request = handler.Requests.Single();
        await Assert.That(request.Uri.ToString()).IsEqualTo("https://env.test/v1/checkpoints");
        await Assert.That(request.Headers["Authorization"]).IsEqualTo("Bearer env-key");
    }
}
