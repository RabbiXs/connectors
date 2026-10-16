using System.Net;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using RabbiX.Sdk.Tests;

namespace RabbiX.Sdk.AgentFramework.Tests;

public class RabbiXCheckpointAgentTests
{
    private const string Instructions = "You issue refunds under 50 EUR.";

    private static AIAgent Agent(FakeHandler handler, params string[] chunks) =>
        new ChatClientAgent(new FakeChatClient(chunks), Instructions, name: "refunder")
            .AsBuilder()
            .UseRabbiXCheckpoint(Fake.Client(handler), decisionClass: "refund")
            .Build();

    [Test]
    public async Task Approved_returns_response_and_sends_goal_result_and_agent()
    {
        var handler = new FakeHandler().Respond(HttpStatusCode.OK, Fake.Verdict("APPROVED"));
        var agent = Agent(handler, "Refunded ", "10 EUR");

        var response = await agent.RunAsync("Refund order 42");

        await Assert.That(response.Text).IsEqualTo("Refunded 10 EUR");
        using var body = JsonDocument.Parse(handler.Requests.Single().BodyText);
        await Assert.That(body.RootElement.GetRawText()).IsEqualTo(
            """{"goal":"You issue refunds under 50 EUR.\n\nRefund order 42","result":"Refunded 10 EUR","decisionClass":"refund","agent":{"name":"refunder","framework":"agent-framework"}}""");
    }

    [Test]
    public async Task Modified_replaces_response_text()
    {
        var handler = new FakeHandler().Respond(HttpStatusCode.OK, Fake.Verdict("MODIFIED", ""","result":"Refunded 5 EUR" """));
        var agent = Agent(handler, "Refunded 10 EUR");

        var response = await agent.RunAsync("Refund order 42");

        await Assert.That(response.Text).IsEqualTo("Refunded 5 EUR");
    }

    [Test]
    [Arguments("REJECTED")]
    [Arguments("EXPIRED")]
    [Arguments("SOMETHING_NEW")]
    public async Task Non_approval_throws(string status)
    {
        var handler = new FakeHandler().Respond(HttpStatusCode.OK, Fake.Verdict(status));
        var agent = Agent(handler, "Refunded 10 EUR");

        await Assert.ThrowsAsync<CheckpointRejectedException>(() => agent.RunAsync("Refund order 42"));
    }

    [Test]
    public async Task Streaming_yields_nothing_before_the_verdict()
    {
        var handler = new FakeHandler().Respond(HttpStatusCode.OK, Fake.Verdict("APPROVED"));
        var agent = Agent(handler, "Refunded ", "10 EUR");

        var text = "";
        await foreach (var update in agent.RunStreamingAsync("Refund order 42"))
        {
            await Assert.That(handler.Requests).Count().IsEqualTo(1);
            text += update.Text;
        }

        await Assert.That(text).IsEqualTo("Refunded 10 EUR");
        await Assert.That(handler.Requests.Single().BodyText).Contains("\"result\":\"Refunded 10 EUR\"");
    }

    [Test]
    public async Task Streaming_modified_yields_edited_text()
    {
        var handler = new FakeHandler().Respond(HttpStatusCode.OK, Fake.Verdict("MODIFIED", ""","result":"Refunded 5 EUR" """));
        var agent = Agent(handler, "Refunded ", "10 EUR");

        var text = "";
        await foreach (var update in agent.RunStreamingAsync("Refund order 42"))
        {
            text += update.Text;
        }

        await Assert.That(text).IsEqualTo("Refunded 5 EUR");
    }

    [Test]
    public async Task Streaming_rejected_throws_without_yielding()
    {
        var handler = new FakeHandler().Respond(HttpStatusCode.OK, Fake.Verdict("REJECTED"));
        var agent = Agent(handler, "Refunded ", "10 EUR");
        var yielded = 0;

        await Assert.ThrowsAsync<CheckpointRejectedException>(async () =>
        {
            await foreach (var _ in agent.RunStreamingAsync("Refund order 42"))
            {
                yielded++;
            }
        });

        await Assert.That(yielded).IsEqualTo(0);
    }

    private sealed class FakeChatClient(string[] chunks) : IChatClient
    {
        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) =>
            Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, string.Concat(chunks))));

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null, [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            foreach (var chunk in chunks)
            {
                await Task.Yield();
                yield return new ChatResponseUpdate(ChatRole.Assistant, chunk) { MessageId = "m-1" };
            }
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose() { }
    }
}
