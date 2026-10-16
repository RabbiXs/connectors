using Microsoft.Agents.AI;

namespace RabbiX.Sdk.AgentFramework;

/// <summary>Registers the RabbiX checkpoint on an <see cref="AIAgentBuilder"/>.</summary>
public static class RabbiXAgentBuilderExtensions
{
    /// <summary>
    /// Checkpoints every run of the agent with RabbiX. Call it first: the first <c>Use</c> is the outermost layer, so it sees the final response.
    /// See <see cref="RabbiXCheckpointAgent"/> for the exact behaviour.
    /// </summary>
    public static AIAgentBuilder UseRabbiXCheckpoint(
        this AIAgentBuilder builder, RabbiXClient client, string? decisionClass = null, IReadOnlyDictionary<string, string>? metadata = null)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(client);
        return builder.Use(inner => new RabbiXCheckpointAgent(inner, client, decisionClass, metadata));
    }
}
