# RabbiX TypeScript SDK

| Package                                                 | What it is                                                                                                                   |
| ------------------------------------------------------- | ---------------------------------------------------------------------------------------------------------------------------- |
| [`@rabbix/sdk`](packages/sdk)                           | The checkpoint client. Zero runtime dependencies (native `fetch`, `CompressionStream`, `crypto.randomUUID`).                 |
| [`@rabbix/openai-agents`](packages/openai-agents)       | Wrapper for the [OpenAI Agents SDK](https://openai.github.io/openai-agents-js/) (`@openai/agents`).                          |
| [`@rabbix/claude-agent-sdk`](packages/claude-agent-sdk) | Wrapper for the [Claude Agent SDK](https://code.claude.com/docs/en/agent-sdk/typescript) (`@anthropic-ai/claude-agent-sdk`). |

Node 22 or newer, ESM only. See the [repository README](../README.md) for exactly what is sent.

## Install

Not published yet. Build from source with [Vite+](https://viteplus.dev/):

```sh
cd typescript
vp install
vp run -r build
```

## Quickstart

```ts
import { RabbiX } from "@rabbix/sdk";

const rabbix = new RabbiX(); // reads RABBIX_API_KEY and RABBIX_BASE_URL

// Returns the result to act on: the original if APPROVED, the edited one if MODIFIED.
// Throws on anything else.
const refund = await rabbix.checkpoint({
  goal: "Refund customers whose order arrived damaged. Order 42: screen cracked.",
  result: { orderId: 42, refundEur: 120 },
  decisionClass: "refund",
});
await issueRefund(refund);
```

Options: `new RabbiX({ apiKey, baseUrl, waitTimeoutMs = 900_000 (null = forever), requestTimeoutMs = 30_000, maxRetries = 3 })`. Every call also accepts `{ signal }`.

To branch on the verdict yourself, `checkpointVerdict()` returns the `Verdict` (`decisionId`, `status`, `route`, `result`, `reason`) without throwing on `REJECTED` or `EXPIRED`.

## Framework wrappers

Each wrapper mirrors the framework call it wraps, with the client and checkpoint options added. It runs the agent, sends its instructions + input as `goal` and its final output as `result`, and returns the output to act on. It always throws unless the verdict is `APPROVED` or `MODIFIED`.

```ts
import { Agent } from "@openai/agents";
import { runWithCheckpoint } from "@rabbix/openai-agents";

const agent = new Agent({ name: "refunds", instructions: "Decide refunds for damaged orders." });
const output = await runWithCheckpoint(agent, "Order 42: screen cracked.", {
  client: rabbix,
  decisionClass: "refund",
  // metadata, runOptions (passed through to run())
});
```

```ts
import { queryWithCheckpoint } from "@rabbix/claude-agent-sdk";

const output = await queryWithCheckpoint({
  prompt: "Order 42: screen cracked.",
  options: { systemPrompt: "Decide refunds for damaged orders." }, // passed through to query()
  client: rabbix,
  agentName: "refunds", // defaults to options.agent
  decisionClass: "refund",
});
```

`goal` is the non-empty parts below joined with `"\n\n"`, verbatim:

1. Instructions. OpenAI Agents: the agent's instructions (function instructions are resolved with `runOptions.context`). Claude Agent SDK: `systemPrompt` (a string, the text parts of an array, or a custom `prompt`; for a preset only its `append`), then the prompt of the subagent selected by `options.agent`.
2. The input: a string as-is; OpenAI Agents input items as compact `JSON.stringify(items)`.

If the goal is empty, the wrapper throws `RabbiXError` before running the agent or sending anything.

## Errors

All errors extend `RabbiXError`.

| Error                | When                                                                                                                                                                                                                                   |
| -------------------- | -------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------- |
| `CheckpointRejected` | Verdict is `REJECTED`, `EXPIRED` or unknown. `error.verdict` has the details.                                                                                                                                                          |
| `CheckpointTimeout`  | Still pending after `waitTimeoutMs`. Resume with `rabbix.wait(error.decisionId)`.                                                                                                                                                      |
| `PayloadTooLarge`    | Body over 10 MB uncompressed. Nothing was sent.                                                                                                                                                                                        |
| `ApiError`           | Non-retryable 4xx, retries exhausted, or an unusable response. Has `status` (0 = no response), `code`, `message`, and `decisionId` when it failed while polling (resume with `rabbix.wait(error.decisionId)` instead of resubmitting). |

```ts
import { ApiError, CheckpointRejected, CheckpointTimeout } from "@rabbix/sdk";

try {
  await act(await rabbix.checkpoint({ goal, result }));
} catch (error) {
  if (error instanceof CheckpointTimeout) await saveForLater(error.decisionId);
  else if (error instanceof ApiError && error.decisionId) await saveForLater(error.decisionId);
  else if (error instanceof CheckpointRejected) console.warn(error.verdict.reason);
  else throw error;
}
```

## Development

```sh
vp run ready   # build, check (format, lint, types), test
```
