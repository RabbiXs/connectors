"""RabbiX checkpoint for Microsoft Agent Framework (``agent-framework``)."""

from __future__ import annotations

import json
from collections.abc import Awaitable, Callable, Mapping, Sequence
from typing import Any

from agent_framework import (
    AgentContext,
    AgentMiddleware,
    AgentResponse,
    Message,
    SupportsAgentRun,
    normalize_messages,
)
from rabbix import AgentInfo, AsyncRabbiX, RabbiXError

__all__ = ["RabbiXMiddleware", "run"]

FRAMEWORK = "agent-framework"


class RabbiXMiddleware(AgentMiddleware):
    """Agent middleware that checkpoints every non-streaming run's response.

    ``Agent(..., middleware=[RabbiXMiddleware(client)])``. On MODIFIED the run returns a
    response holding the edited text; on anything but approval the run raises
    ``rabbix.CheckpointRejected``. Streaming runs raise ``rabbix.RabbiXError`` before the
    agent runs, because a streamed result reaches the caller before it can be checked.
    """

    def __init__(
        self,
        client: AsyncRabbiX,
        *,
        decision_class: str | None = None,
        metadata: Mapping[str, str] | None = None,
    ) -> None:
        self._client = client
        self._decision_class = decision_class
        self._metadata = metadata

    async def process(
        self, context: AgentContext, call_next: Callable[[], Awaitable[None]]
    ) -> None:
        if context.stream:
            raise RabbiXError("RabbiX checkpoint does not support streaming agent runs")
        await call_next()
        if not isinstance(context.result, AgentResponse):
            raise RabbiXError("agent run produced no response; nothing to checkpoint")
        context.result = await _checkpoint(
            self._client,
            context.agent,
            context.messages,
            context.result,
            self._decision_class,
            self._metadata,
        )


async def run(
    agent: SupportsAgentRun,
    messages: str | Message | Sequence[str | Message],
    *,
    client: AsyncRabbiX,
    decision_class: str | None = None,
    metadata: Mapping[str, str] | None = None,
    **run_kwargs: Any,
) -> AgentResponse:
    """Run ``agent.run(messages, **run_kwargs)`` and checkpoint the response.

    Returns the response to act on (a response with the edited text on MODIFIED). Raises
    ``rabbix.CheckpointRejected`` on anything but approval.
    """
    response: AgentResponse = await agent.run(messages, **run_kwargs)
    return await _checkpoint(
        client, agent, normalize_messages(messages), response, decision_class, metadata
    )


async def _checkpoint(
    client: AsyncRabbiX,
    agent: SupportsAgentRun,
    messages: Sequence[Message],
    response: AgentResponse,
    decision_class: str | None,
    metadata: Mapping[str, str] | None,
) -> AgentResponse:
    # Instructions live in default_options on the built-in Agent; custom agents may have none.
    options: Mapping[str, Any] = getattr(agent, "default_options", None) or {}
    instructions = options.get("instructions")
    parts = [instructions] if isinstance(instructions, str) else []
    goal = "\n\n".join(part for part in [*parts, *(m.text for m in messages)] if part)

    text = response.text
    approved = await client.checkpoint(
        goal,
        text,
        decision_class=decision_class,
        agent=AgentInfo(name=agent.name, framework=FRAMEWORK),
        metadata=metadata,
    )
    if approved is text:  # APPROVED: checkpoint() returns the object it was given
        return response
    edited = approved if isinstance(approved, str) else json.dumps(approved, ensure_ascii=False)
    return AgentResponse(
        messages=[Message("assistant", [edited])],
        response_id=response.response_id,
        agent_id=response.agent_id,
    )
