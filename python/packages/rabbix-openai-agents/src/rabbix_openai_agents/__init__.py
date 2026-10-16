"""RabbiX checkpoint for the OpenAI Agents SDK (``openai-agents``)."""

from __future__ import annotations

import json
from collections.abc import Mapping
from typing import Any

from agents import Agent, Runner, TResponseInputItem
from pydantic_core import to_jsonable_python
from rabbix import AgentInfo, AsyncRabbiX, RabbiXError

__all__ = ["run_with_checkpoint"]

FRAMEWORK = "openai-agents"


async def run_with_checkpoint(
    agent: Agent[Any],
    input: str | list[TResponseInputItem],
    *,
    client: AsyncRabbiX,
    decision_class: str | None = None,
    metadata: Mapping[str, str] | None = None,
    **run_kwargs: Any,
) -> Any:
    """Run ``agent`` with ``Runner.run`` and checkpoint its final output.

    The goal is the agent's instructions and ``input`` (input items as compact JSON), joined
    with a blank line; the result is ``final_output``. Returns the output to act on: the
    original ``final_output`` on APPROVED, the edited JSON value on MODIFIED. Raises
    ``rabbix.CheckpointRejected`` on anything else, and ``rabbix.RabbiXError`` if the goal is
    empty. ``run_kwargs`` are passed to ``Runner.run`` unchanged.
    """
    run_result = await Runner.run(agent, input, **run_kwargs)
    instructions = await agent.get_system_prompt(run_result.context_wrapper)
    goal = _goal(instructions, input)

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


def _goal(instructions: str | None, input: str | list[TResponseInputItem]) -> str:
    if isinstance(input, str):
        task = input
    elif input:
        items = to_jsonable_python(input)
        task = json.dumps(items, separators=(",", ":"), ensure_ascii=False)
    else:
        task = ""  # no input items: nothing to describe
    goal = "\n\n".join(part for part in (instructions, task) if part)
    if not goal:
        raise RabbiXError("goal is empty: the agent has no instructions and the input is empty")
    return goal
