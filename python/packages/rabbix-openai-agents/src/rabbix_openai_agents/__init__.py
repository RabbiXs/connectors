"""RabbiX checkpoint for the OpenAI Agents SDK (``openai-agents``)."""

from __future__ import annotations

import json
from collections.abc import Mapping
from typing import Any

from agents import Agent, Runner, TResponseInputItem
from pydantic_core import to_jsonable_python
from rabbix import AgentInfo, AsyncRabbiX

__all__ = ["run"]

FRAMEWORK = "openai-agents"


async def run(
    agent: Agent[Any],
    input: str | list[TResponseInputItem],
    *,
    client: AsyncRabbiX,
    decision_class: str | None = None,
    metadata: Mapping[str, str] | None = None,
    **run_kwargs: Any,
) -> Any:
    """Run ``agent`` with ``Runner.run`` and checkpoint its final output.

    The goal is the agent's instructions plus ``input``; the result is ``final_output``.
    Returns the output to act on: the original ``final_output`` on APPROVED, the edited JSON
    value on MODIFIED. Raises ``rabbix.CheckpointRejected`` on anything else.
    ``run_kwargs`` are passed to ``Runner.run`` unchanged.
    """
    run_result = await Runner.run(agent, input, **run_kwargs)
    instructions = await agent.get_system_prompt(run_result.context_wrapper)
    task = input if isinstance(input, str) else json.dumps(input, ensure_ascii=False, default=str)
    goal = "\n\n".join(part for part in (instructions, task) if part)

    output = run_result.final_output
    payload = to_jsonable_python(output)  # structured outputs (pydantic, dataclass) to JSON
    approved = await client.checkpoint(
        goal,
        payload,
        decision_class=decision_class,
        agent=AgentInfo(name=agent.name, framework=FRAMEWORK),
        metadata=metadata,
    )
    # checkpoint() hands back the very object it was given when APPROVED.
    return output if approved is payload else approved
