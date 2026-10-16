"""RabbiX checkpoint for the Claude Agent SDK (``claude-agent-sdk``)."""

from __future__ import annotations

from collections.abc import Mapping
from typing import Any, cast

from claude_agent_sdk import ClaudeAgentOptions, ResultMessage, query
from rabbix import AgentInfo, AsyncRabbiX, RabbiXError

__all__ = ["query_with_checkpoint"]

FRAMEWORK = "claude-agent-sdk"


async def query_with_checkpoint(
    prompt: str,
    *,
    client: AsyncRabbiX,
    options: ClaudeAgentOptions | None = None,
    agent_name: str | None = None,
    decision_class: str | None = None,
    metadata: Mapping[str, str] | None = None,
) -> Any:
    """Run ``query(prompt=..., options=...)`` to completion and checkpoint its final result.

    The goal is the configured system prompt (a preset's ``append`` only) and ``prompt``,
    joined with a blank line; the result is the ``ResultMessage``'s ``structured_output`` if
    set, else its ``result`` text. Returns the result to act on (the edited one on MODIFIED).
    Raises ``rabbix.CheckpointRejected`` on anything but approval, and ``rabbix.RabbiXError``
    if the goal is empty (before running) or the run ends without a successful result.
    """
    goal = "\n\n".join(part for part in (_instructions(options), prompt) if part)
    if not goal:
        raise RabbiXError("goal is empty: no system prompt and the prompt is empty")

    final: ResultMessage | None = None
    async for message in query(prompt=prompt, options=options):
        if isinstance(message, ResultMessage):
            final = message
    if final is None or final.is_error:
        subtype = final.subtype if final else "no result message"
        raise RabbiXError(f"Claude agent run did not succeed ({subtype}); nothing to checkpoint")
    output = final.structured_output if final.structured_output is not None else final.result
    if output is None:
        raise RabbiXError("Claude agent run produced no result; nothing to checkpoint")

    return await client.checkpoint(
        goal,
        output,
        decision_class=decision_class,
        agent=AgentInfo(name=agent_name, framework=FRAMEWORK),
        metadata=metadata,
    )


def _instructions(options: ClaudeAgentOptions | None) -> str | None:
    system_prompt = options.system_prompt if options else None
    if system_prompt is None or isinstance(system_prompt, str):
        return system_prompt
    config = cast(Mapping[str, Any], system_prompt)
    # A custom prompt's text, or what a preset appends (the preset itself is not the goal).
    # A "file" prompt is not read.
    key = {"custom": "prompt", "preset": "append"}.get(config.get("type", ""))
    text = config.get(key) if key else None
    return text if isinstance(text, str) else None
