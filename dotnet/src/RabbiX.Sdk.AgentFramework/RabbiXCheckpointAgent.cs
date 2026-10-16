using System.Runtime.CompilerServices;
using System.Text.Json;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;

namespace RabbiX.Sdk.AgentFramework;

/// <summary>
/// Wraps an <see cref="AIAgent"/> so every run is checkpointed before its response is returned.
/// </summary>
/// <remarks>
/// <para>The goal is the agent's instructions plus the run's input messages; the result is the final response text.</para>
/// <para>Approved: the response is returned unchanged. Modified: the response is replaced by one assistant message
/// with the edited text. Anything else throws (<see cref="CheckpointRejectedException"/>, <see cref="CheckpointTimeoutException"/>, ...).</para>
/// <para>Streaming runs are buffered: nothing is yielded until the verdict allows it.</para>
/// </remarks>
public sealed class RabbiXCheckpointAgent : DelegatingAIAgent
{
    /// <summary>Value sent as <c>agent.framework</c>.</summary>
    public const string Framework = "agent-framework";

    private readonly RabbiXClient _client;
    private readonly string? _decisionClass;

    /// <summary>Wraps <paramref name="innerAgent"/>.</summary>
    public RabbiXCheckpointAgent(AIAgent innerAgent, RabbiXClient client, string? decisionClass = null)
        : base(innerAgent)
    {
        _client = client ?? throw new ArgumentNullException(nameof(client));
        _decisionClass = decisionClass;
    }

    /// <inheritdoc />
    protected override async Task<AgentResponse> RunCoreAsync(
        IEnumerable<ChatMessage> messages,
        AgentSession? session = null,
        AgentRunOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        List<ChatMessage> input = [.. messages];
        var response = await InnerAgent.RunAsync(input, session, options, cancellationToken).ConfigureAwait(false);
        return await CheckpointAsync(input, options, response, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    protected override async IAsyncEnumerable<AgentResponseUpdate> RunCoreStreamingAsync(
        IEnumerable<ChatMessage> messages,
        AgentSession? session = null,
        AgentRunOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        List<ChatMessage> input = [.. messages];
        List<AgentResponseUpdate> updates = [];
        await foreach (var update in InnerAgent.RunStreamingAsync(input, session, options, cancellationToken).ConfigureAwait(false))
        {
            updates.Add(update);
        }

        var response = updates.ToAgentResponse();
        var approved = await CheckpointAsync(input, options, response, cancellationToken).ConfigureAwait(false);
        IEnumerable<AgentResponseUpdate> output = ReferenceEquals(approved, response) ? updates : approved.ToAgentResponseUpdates();
        foreach (var update in output)
        {
            yield return update;
        }
    }

    private async Task<AgentResponse> CheckpointAsync(
        List<ChatMessage> input, AgentRunOptions? options, AgentResponse response, CancellationToken cancellationToken)
    {
        // A background run is not finished, so there is no final result to check.
#pragma warning disable MEAI001 // ContinuationToken is experimental.
        if (response.ContinuationToken is not null)
#pragma warning restore MEAI001
        {
            throw new NotSupportedException("RabbiX checkpoints need the final result; background responses are not supported.");
        }

        var request = new CheckpointRequest
        {
            Goal = Goal(input, options),
            Result = JsonSerializer.SerializeToElement(response.Text, AIJsonUtilities.DefaultOptions),
            DecisionClass = _decisionClass,
            Agent = new AgentInfo { Name = InnerAgent.Name, Framework = Framework },
        };

        var verdict = await _client.CheckpointVerdictAsync(request, cancellationToken).ConfigureAwait(false);
        var acted = verdict.GetResultToActOn(request.Result);
        if (verdict.Status == VerdictStatus.Approved)
        {
            return response;
        }

        var text = acted.ValueKind == JsonValueKind.String ? acted.GetString()! : acted.GetRawText();
        return new AgentResponse(new ChatMessage(ChatRole.Assistant, text))
        {
            AgentId = response.AgentId,
            ResponseId = response.ResponseId,
            CreatedAt = response.CreatedAt,
            Usage = response.Usage,
        };
    }

    private string Goal(List<ChatMessage> input, AgentRunOptions? options)
    {
        string?[] parts =
        [
            GetService<ChatClientAgent>()?.Instructions,
            (options as ChatClientAgentRunOptions)?.ChatOptions?.Instructions,
            .. input.Select(m => m.Text),
        ];
        return string.Join("\n\n", parts.Where(p => !string.IsNullOrWhiteSpace(p)));
    }
}
