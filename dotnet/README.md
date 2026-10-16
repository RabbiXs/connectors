# RabbiX .NET SDK

`RabbiX.Sdk` submits an agent's goal and final result to the RabbiX checkpoint and gives you back only what you may act on. `RabbiX.Sdk.AgentFramework` does the same for [Microsoft Agent Framework](https://learn.microsoft.com/agent-framework/) agents.

Requires .NET 10. The core package has no dependencies beyond the .NET base library.

## Install

Not on NuGet yet. Reference the projects from source:

```sh
git clone https://github.com/RabbiXs/connectors
dotnet add reference connectors/dotnet/src/RabbiX.Sdk/RabbiX.Sdk.csproj
# Agent Framework wrapper:
dotnet add reference connectors/dotnet/src/RabbiX.Sdk.AgentFramework/RabbiX.Sdk.AgentFramework.csproj
```

## Quickstart

```csharp
using RabbiX.Sdk;

// Reads RABBIX_API_KEY and RABBIX_BASE_URL unless set here.
using var rabbix = new RabbiXClient();

string toActOn = await rabbix.CheckpointAsync(
    goal: "Refund order 42 if it arrived damaged",
    result: "Refund 10 EUR to customer 7",
    decisionClass: "refund");

// Only reached on APPROVED (original result) or MODIFIED (the edited result).
Execute(toActOn);
```

Structured results go in as a `JsonElement`:

```csharp
var result = JsonSerializer.SerializeToElement(refund);
JsonElement toActOn = await rabbix.CheckpointAsync(new CheckpointRequest
{
    Goal = goal,
    Result = result,
    DecisionClass = "refund",
    Metadata = new Dictionary<string, string> { ["ticket"] = "T-1" },
});
```

With `IHttpClientFactory`, pass the client in: `new RabbiXClient(options, factory.CreateClient("rabbix"))`.

Options (`RabbiXOptions`): `ApiKey`, `BaseUrl`, `WaitTimeout` (15 min, `null` = wait forever), `RequestTimeout` (30 s), `MaxRetries` (3), `TimeProvider`.

## Microsoft Agent Framework

```csharp
using Microsoft.Agents.AI;
using RabbiX.Sdk.AgentFramework;

AIAgent agent = new ChatClientAgent(chatClient, instructions: "You issue refunds under 50 EUR.", name: "refunder")
    .AsBuilder()
    .UseRabbiXCheckpoint(rabbix, decisionClass: "refund") // first Use() = outermost, sees the final response
    .Build();

AgentResponse response = await agent.RunAsync("Refund order 42"); // throws unless approved
```

`UseRabbiXCheckpoint(client, decisionClass, metadata)` (and the `RabbiXCheckpointAgent` constructor) take an optional `decisionClass` and `metadata` (`IReadOnlyDictionary<string, string>`), sent as-is.

The wrapper sends as `goal` the non-empty parts below joined with `"\n\n"`, verbatim (no trimming): the agent's instructions, the run's `ChatOptions.Instructions`, then each input message's text in order. If that is empty it throws `RabbiXException` before the agent runs. It sends the final response text as `result`, and `agent: { name, framework: "agent-framework" }`. On `MODIFIED` the returned response contains the edited text. Streaming runs are buffered and only yielded after approval. Background runs (continuation tokens) are rejected with `NotSupportedException`.

## Errors

All exceptions derive from `RabbiXException`.

| Exception | When |
|---|---|
| `CheckpointRejectedException` | `REJECTED`, `EXPIRED`, or any status this SDK does not know. `.Verdict` has the details. |
| `CheckpointTimeoutException` | Still pending after `WaitTimeout`. Resume with `WaitAsync(e.DecisionId)`. |
| `PayloadTooLargeException` | Body over 10 MB. Nothing was sent. |
| `RabbiXApiException` | Non-retryable API error (`StatusCode`, `Code`), or retries exhausted (`StatusCode` is null for network failures). If it failed while polling, `DecisionId` is set: the checkpoint exists, so resume with `WaitAsync(e.DecisionId)` instead of resubmitting. |

To branch on the verdict yourself, use `CheckpointVerdictAsync`, which returns the `Verdict` instead of throwing for rejections:

```csharp
Verdict verdict = await rabbix.CheckpointVerdictAsync(request);
// or, after a timeout: Verdict verdict = await rabbix.WaitAsync(decisionId);
JsonElement toActOn = verdict.GetResultToActOn(request.Result); // same fail-closed rule as CheckpointAsync
```

## Development

```sh
cd dotnet
dotnet format --verify-no-changes
dotnet build
dotnet test
```
