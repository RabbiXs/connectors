# RabbiX Python SDK

Python ≥3.11. The core package `rabbix` depends only on `httpx`. Framework wrappers are separate packages.

| Package | For |
|---|---|
| `rabbix` | Any agent: sync `RabbiX` and async `AsyncRabbiX` clients |
| `rabbix-openai-agents` | [OpenAI Agents SDK](https://github.com/openai/openai-agents-python) |
| `rabbix-agent-framework` | [Microsoft Agent Framework](https://github.com/microsoft/agent-framework) |
| `rabbix-claude-agent-sdk` | [Claude Agent SDK](https://github.com/anthropics/claude-agent-sdk-python) |

## Install

Not published yet. Install from source:

```sh
pip install "rabbix @ git+https://github.com/RabbiXs/connectors#subdirectory=python/packages/rabbix"
# then a wrapper, from the same URL with e.g. subdirectory=python/packages/rabbix-openai-agents
# (install rabbix first: the wrappers depend on it and it is not on PyPI yet)
```

Set `RABBIX_API_KEY` (required) and `RABBIX_BASE_URL` (defaults to the placeholder `https://api.rabbix.invalid`, which never resolves), or pass `api_key=` / `base_url=` to the client.

## Quickstart

```python
from rabbix import RabbiX

with RabbiX() as rabbix:
    result = rabbix.checkpoint(
        goal="Refund order 7 if the customer was double-charged",
        result={"action": "refund", "order": 7, "amount": 42.0},
        decision_class="refund",  # optional
    )
# Only reached on APPROVED (your result) or MODIFIED (the edited result).
apply(result)
```

`AsyncRabbiX` has the same methods as coroutines (`async with AsyncRabbiX() as rabbix: await rabbix.checkpoint(...)`).

Client options: `RabbiX(api_key=None, base_url=None, wait_timeout=900.0, request_timeout=30.0, max_retries=3)`. `wait_timeout=None` waits for a human verdict forever.

## Framework wrappers

Each wrapper runs the agent, sends the agent's instructions and input as `goal` (built as described in [How wrappers build `goal`](../README.md#how-wrappers-build-goal)) and its final output as `result`, and returns the output to act on. They always raise on anything but approval, and raise `RabbiXError` before sending anything if the goal is empty.

```python
from rabbix import AsyncRabbiX

async with AsyncRabbiX() as rabbix:
    # OpenAI Agents SDK: wraps Runner.run(agent, input)
    from rabbix_openai_agents import run_with_checkpoint

    output = await run_with_checkpoint(
        agent, "Customer asks for a refund on order 7", client=rabbix
    )

    # Microsoft Agent Framework: wraps agent.run(messages) ...
    from rabbix_agent_framework import RabbiXCheckpointMiddleware, run_with_checkpoint

    response = await run_with_checkpoint(
        agent, "Customer asks for a refund on order 7", client=rabbix
    )
    # ... or checks every non-streaming run of an agent as middleware
    middleware = [RabbiXCheckpointMiddleware(rabbix)]
    agent = Agent(client=chat_client, instructions="...", middleware=middleware)

    # Claude Agent SDK: wraps query(prompt=..., options=...)
    from rabbix_claude_agent_sdk import query_with_checkpoint

    output = await query_with_checkpoint(
        "Refund order 7", client=rabbix, options=options, agent_name="support"
    )
```

All wrappers accept `decision_class=` and `metadata=`.

## Errors

All errors subclass `rabbix.RabbiXError`.

| Error | When |
|---|---|
| `CheckpointRejected` | Verdict is REJECTED, EXPIRED or unknown. `.verdict` has the details |
| `CheckpointTimeout` | No final verdict within `wait_timeout`. Resume with `wait(e.decision_id)` |
| `PayloadTooLarge` | Body over 10 MB uncompressed. Nothing was sent |
| `ApiError` | Non-retryable HTTP error or retries exhausted. `.status` (None for network failures), `.code`, `.message`, `.decision_id` (set when it failed while polling a pending decision: resume with `wait(e.decision_id)`; None if the submission itself failed) |

```python
from rabbix import CheckpointRejected, CheckpointTimeout

try:
    result = rabbix.checkpoint(goal, result)
except CheckpointTimeout as e:
    save_for_later(e.decision_id)  # later: rabbix.wait(e.decision_id)
except CheckpointRejected as e:
    log_rejection(e.verdict.status, e.verdict.reason)
```

To branch on the verdict yourself, `checkpoint_verdict()` returns the `Verdict` (`decision_id`, `status`, `route`, `result`, `reason`) without raising on REJECTED or EXPIRED.

## Development

```sh
cd python
uv sync
uv run ruff check . && uv run ruff format --check . && uv run pyright && uv run pytest
```
