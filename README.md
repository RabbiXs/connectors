# RabbiX connectors

Open-source SDKs that connect autonomous AI agents to the RabbiX checkpoint.

An agent runs with a goal, produces a final result, and **before anything acts on that result** it submits both to RabbiX. RabbiX returns a verdict: let it through, stop it, or hand it to a human. These SDKs are that one call.

This repository is public on purpose: you can read exactly what leaves your system and how.

> **Status: pre-release.** The API is not live. The SDKs default to `https://api.rabbix.invalid`, a reserved domain that never resolves, so nothing is sent anywhere until you set `RABBIX_BASE_URL`. Packages are not published yet.

## What we send

One request per agent run, `POST /v1/checkpoints` ([openapi.yaml](openapi.yaml)):

| Field | Source |
|---|---|
| `goal` | You pass it (or the framework wrapper reads the agent's instructions + input) |
| `result` | You pass it (or the wrapper reads the agent's final output). Sent in full, never truncated |
| `decisionClass` | Optional, you pass it |
| `agent.name`, `agent.framework` | Optional; wrappers fill these from the agent object |
| `metadata` | Optional string pairs, only what you pass |

Headers: `Authorization: Bearer <api key>`, `Idempotency-Key` (random UUID per call), `User-Agent: rabbix-<lang>/<version>`, and `Content-Encoding: gzip` for bodies over 8 KB.

**Nothing else.** No telemetry, no hostnames, no environment variables, no tool calls or intermediate steps, no background requests. While a human reviews, the SDK polls `GET /v1/checkpoints/{decisionId}`; that is the only other request it makes.

## Behaviour

- **Fail-closed.** `checkpoint()` returns the result to act on only for `APPROVED` (original result) or `MODIFIED` (the human-edited result). `REJECTED`, `EXPIRED`, timeouts and unrecoverable errors raise. Framework wrappers always raise.
- **Waits for the verdict.** On `202` the SDK polls, honouring `Retry-After` (otherwise backoff up to 30 s), for up to 15 minutes by default (configurable, including no limit). On timeout it raises `CheckpointTimeout` carrying the `decisionId`; resume with `wait(decisionId)`.
- **Retries** on network errors, `429` and `5xx` with the same `Idempotency-Key` (3 retries, exponential backoff).
- **Size limit.** Bodies over 10 MB uncompressed raise `PayloadTooLarge` before any network call.
- `checkpoint_verdict()` / `checkpointVerdict()` / `CheckpointVerdictAsync()` return the raw verdict without raising, for hosts that branch themselves.

## Configuration

| Env var | Default |
|---|---|
| `RABBIX_API_KEY` | none (required) |
| `RABBIX_BASE_URL` | `https://api.rabbix.invalid` |

Both can also be passed to the client constructor.

## SDKs

| Language | Core | Framework wrappers |
|---|---|---|
| [Python](python/) ≥3.11 | `rabbix` | `rabbix-openai-agents`, `rabbix-agent-framework`, `rabbix-claude-agent-sdk` |
| [TypeScript](typescript/) Node ≥22 | `@rabbix/sdk` | `@rabbix/openai-agents`, `@rabbix/claude-agent-sdk` |
| [.NET](dotnet/) net10.0 | `RabbiX.Sdk` | `RabbiX.Sdk.AgentFramework` |

Wrappers are separate packages so the core has no framework dependencies.

## License

[Apache-2.0](LICENSE)
