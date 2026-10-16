using System.Runtime.CompilerServices;
using System.Text.Json;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;

namespace RabbiX.Sdk.AgentFramework;

/// <summary>
/// Wraps an <see cref="AIAgent"/> so every run is checkpointed before its response is returned.
/// </summary>
/// <remarks>
/// <para>The goal is the non-empty parts, joined with <c>"\n\n"</c> and not trimmed: the agent's instructions, the run's
/// <c>ChatOptions.Instructions</c>, then each input message's text. An empty goal throws <see cref="RabbiXException"/>
/// before the agent runs. The result is the final response text.</para>
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
    private readonly IReadOnlyDictionary<string, string>? _metadata;

    /// <summary>Wraps <paramref name="innerAgent"/>.</summary>
    /// <param name="innerAgent">The agent to checkpoint.</param>
    /// <param name="client">The RabbiX client.</param>
    /// <param name="decisionClass">Optional, sent as <c>decisionClass</c>.</param>
    /// <param name="metadata">Optional string pairs, sent as <c>metadata</c>.</param>
    public RabbiXCheckpointAgent(
        AIAgent innerAgent, RabbiXClient client, string? decisionClass = null, IReadOnlyDictionary<string, string>? metadata = null)
        : base(innerAgent)
    {
        _client = client ?? throw new ArgumentNullException(nameof(client));
        _decisionClass = decisionClass;
        _metadata = metadata;
    }

    /// <inheritdoc />
    protected override async Task<AgentResponse> RunCoreAsync(
        IEnumerable<ChatMessage> messages,
        AgentSession? session = null,
        AgentRunOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        List<ChatMessage> input = [.. messages];
        var goal = Goal(input, options);
        var response = await InnerAgent.RunAsync(input, session, options, cancellationToken).ConfigureAwait(false);
        return await CheckpointAsync(goal, response, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    protected override async IAsyncEnumerable<AgentResponseUpdate> RunCoreStreamingAsync(
        IEnumerable<ChatMessage> messages,
        AgentSession? session = null,
        AgentRunOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        List<ChatMessage> input = [.. messages];
        var goal = Goal(input, options);
        List<AgentResponseUpdate> updates = [];
        await foreach (var update in InnerAgent.RunStreamingAsync(input, session, options, cancellationToken).ConfigureAwait(false))
        {
            updates.Add(update);
        }

        var response = updates.ToAgentResponse();
        var approved = await CheckpointAsync(goal, response, cancellationToken).ConfigureAwait(false);
        IEnumerable<AgentResponseUpdate> output = ReferenceEquals(approved, response) ? updates : approved.ToAgentResponseUpdates();
        foreach (var update in output)
        {
            yield return update;
        }
    }

    private async Task<AgentResponse> CheckpointAsync(string goal, AgentResponse response, CancellationToken cancellationToken)
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
            Goal = goal,
            Result = JsonSerializer.SerializeToElement(response.Text, AIJsonUtilities.DefaultOptions),
            DecisionClass = _decisionClass,
            Metadata = _metadata,
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

    // Cross-language contract: non-empty parts joined with "\n\n", verbatim (no trimming).
    private string Goal(List<ChatMessage> input, AgentRunOptions? options)
    {
        string?[] parts =
        [
            GetService<ChatClientAgent>()?.Instructions,
            (options as ChatClientAgentRunOptions)?.ChatOptions?.Instructions,
            .. input.Select(m => m.Text),
        ];
        var goal = string.Join("\n\n", parts.Where(p => !string.IsNullOrEmpty(p)));
        return goal.Length > 0
            ? goal
            : throw new RabbiXException("Cannot checkpoint: the agent has no instructions and the input has no text, so the goal is empty.");
    }
}
