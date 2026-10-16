using System.Text.Json;
using System.Text.Json.Serialization;

namespace RabbiX.Sdk;

/// <summary>The body of <c>POST /v1/checkpoints</c>. Null properties are not sent.</summary>
public sealed record CheckpointRequest
{
    /// <summary>What the agent was asked to do (instructions and task input).</summary>
    public required string Goal { get; init; }

    /// <summary>The agent's final result: a JSON string, object or array. Sent in full.</summary>
    /// <remarks>Use <see cref="JsonSerializer.SerializeToElement{TValue}(TValue, JsonSerializerOptions?)"/> to turn your own type into a <see cref="JsonElement"/>.</remarks>
    public required JsonElement Result { get; init; }

    /// <summary>Optional category with shared rules and thresholds, e.g. <c>refund</c>.</summary>
    public string? DecisionClass { get; init; }

    /// <summary>Optional description of the agent that produced the result.</summary>
    public AgentInfo? Agent { get; init; }

    /// <summary>Optional free-form string pairs. The SDK adds nothing here.</summary>
    public IReadOnlyDictionary<string, string>? Metadata { get; init; }
}

/// <summary>Identifies the agent that produced a result.</summary>
public sealed record AgentInfo
{
    /// <summary>The agent's name.</summary>
    public string? Name { get; init; }

    /// <summary>The framework the agent runs on, e.g. <c>agent-framework</c>.</summary>
    public string? Framework { get; init; }
}

/// <summary>RabbiX's answer to a checkpoint.</summary>
public sealed record Verdict
{
    /// <summary>Identifies the decision; pass it to <see cref="RabbiXClient.WaitAsync"/> to resume waiting.</summary>
    public required string DecisionId { get; init; }

    /// <summary>The decision. Only <see cref="VerdictStatus.Approved"/> and <see cref="VerdictStatus.Modified"/> allow acting.</summary>
    public required VerdictStatus Status { get; init; }

    /// <summary>How the decision was made: <c>AI_AUTONOMOUS</c>, <c>HUMAN_IN_LOOP</c> or <c>HUMAN_ONLY</c>.</summary>
    public string? Route { get; init; }

    /// <summary>The edited result to act on instead, when <see cref="Status"/> is <see cref="VerdictStatus.Modified"/>.</summary>
    public JsonElement? Result { get; init; }

    /// <summary>Human- or policy-supplied explanation.</summary>
    public string? Reason { get; init; }

    /// <summary>
    /// Returns the result the host may act on: <paramref name="original"/> when approved, the edited
    /// <see cref="Result"/> when modified. Throws <see cref="CheckpointRejectedException"/> for anything else.
    /// </summary>
    public JsonElement GetResultToActOn(JsonElement original) => Status switch
    {
        VerdictStatus.Approved => original,
        VerdictStatus.Modified when Result is { } edited => edited,
        _ => throw new CheckpointRejectedException(this),
    };
}

/// <summary>Verdict status. Values this SDK does not know map to <see cref="Unknown"/>, which is never actionable.</summary>
[JsonConverter(typeof(VerdictStatusConverter))]
public enum VerdictStatus
{
    /// <summary>A status this SDK version does not recognise. Treated like a rejection.</summary>
    Unknown = 0,

    /// <summary>Not final yet.</summary>
    Pending,

    /// <summary>Act on the original result.</summary>
    Approved,

    /// <summary>Act on <see cref="Verdict.Result"/> instead of the original.</summary>
    Modified,

    /// <summary>Do not act.</summary>
    Rejected,

    /// <summary>Nobody decided in time. Do not act.</summary>
    Expired,
}

internal sealed class VerdictStatusConverter : JsonConverter<VerdictStatus>
{
    public override VerdictStatus Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType != JsonTokenType.String)
        {
            reader.Skip();
            return VerdictStatus.Unknown;
        }

        return reader.GetString() switch
        {
            "PENDING" => VerdictStatus.Pending,
            "APPROVED" => VerdictStatus.Approved,
            "MODIFIED" => VerdictStatus.Modified,
            "REJECTED" => VerdictStatus.Rejected,
            "EXPIRED" => VerdictStatus.Expired,
            _ => VerdictStatus.Unknown,
        };
    }

    public override void Write(Utf8JsonWriter writer, VerdictStatus value, JsonSerializerOptions options) =>
        writer.WriteStringValue(value.ToString().ToUpperInvariant());
}

internal sealed record ApiError(string? Code, string? Message);

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    RespectNullableAnnotations = true)]
[JsonSerializable(typeof(CheckpointRequest))]
[JsonSerializable(typeof(Verdict))]
[JsonSerializable(typeof(ApiError))]
[JsonSerializable(typeof(string))]
internal sealed partial class RabbiXJson : JsonSerializerContext;
