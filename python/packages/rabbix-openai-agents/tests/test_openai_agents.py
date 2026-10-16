from __future__ import annotations

from types import SimpleNamespace
from typing import Any, cast

import pytest
from agents import Agent, Runner
from pydantic import BaseModel
from rabbix import AgentInfo, AsyncRabbiX, CheckpointRejected, RabbiXError, Verdict
from rabbix_openai_agents import run_with_checkpoint


class FakeRabbiX:
    """Stands in for AsyncRabbiX.checkpoint with the same return/raise contract."""

    def __init__(self, status: str = "APPROVED", edited: Any = None) -> None:
        self.status = status
        self.edited = edited
        self.calls: list[dict[str, Any]] = []

    async def checkpoint(self, goal: str, result: Any, **kwargs: Any) -> Any:
        self.calls.append({"goal": goal, "result": result, **kwargs})
        if self.status == "APPROVED":
            return result
        if self.status == "MODIFIED":
            return self.edited
        raise CheckpointRejected(Verdict(decision_id="dec_1", status=self.status))


class Refund(BaseModel):
    order: int
    amount: float


@pytest.fixture
def final_output(monkeypatch: pytest.MonkeyPatch) -> list[Any]:
    """Replace Runner.run; append to the returned list to set the run's final output."""
    outputs: list[Any] = []

    async def fake_run(agent: Any, input: Any, **kwargs: Any) -> Any:
        return SimpleNamespace(final_output=outputs[0], context_wrapper=None)

    monkeypatch.setattr(Runner, "run", fake_run)
    return outputs


agent = Agent(name="support", instructions="Handle refunds.")


async def test_approved_text_output(final_output: list[Any]) -> None:
    final_output.append("Refund 42 EUR")
    rabbix = FakeRabbiX()

    result = await run_with_checkpoint(agent, "refund order 7", client=cast(AsyncRabbiX, rabbix))

    assert result == "Refund 42 EUR"
    assert rabbix.calls == [
        {
            "goal": "Handle refunds.\n\nrefund order 7",
            "result": "Refund 42 EUR",
            "decision_class": None,
            "agent": AgentInfo(name="support", framework="openai-agents"),
            "metadata": None,
        }
    ]


async def test_structured_output_is_sent_as_json_and_returned_as_model(
    final_output: list[Any],
) -> None:
    refund = Refund(order=7, amount=42)
    final_output.append(refund)
    rabbix = FakeRabbiX()

    result = await run_with_checkpoint(agent, "refund order 7", client=cast(AsyncRabbiX, rabbix))

    assert result is refund
    assert rabbix.calls[0]["result"] == {"order": 7, "amount": 42.0}


@pytest.mark.parametrize(
    ("instructions", "input", "goal"),
    [
        ("Be careful.", "Refund order 7", "Be careful.\n\nRefund order 7"),
        (None, "Refund order 7", "Refund order 7"),
        (
            "Be careful.",
            [{"role": "user", "content": "Refund order 7"}],
            'Be careful.\n\n[{"role":"user","content":"Refund order 7"}]',
        ),
        (None, [{"role": "user", "content": "Café"}], '[{"role":"user","content":"Café"}]'),
        ("  Be careful.  ", " x ", "  Be careful.  \n\n x "),  # verbatim, no trimming
    ],
    ids=["instructions+string", "string-only", "items", "items-non-ascii", "verbatim"],
)
async def test_goal_vectors(
    final_output: list[Any], instructions: str | None, input: Any, goal: str
) -> None:
    final_output.append("ok")
    rabbix = FakeRabbiX()

    await run_with_checkpoint(
        Agent(name="support", instructions=instructions), input, client=cast(AsyncRabbiX, rabbix)
    )

    assert rabbix.calls[0]["goal"] == goal


async def test_dynamic_instructions_form_the_goal(final_output: list[Any]) -> None:
    final_output.append("ok")
    rabbix = FakeRabbiX()
    dynamic = Agent(name="support", instructions=lambda ctx, agent: "Be careful.")

    await run_with_checkpoint(
        dynamic, "Refund order 7", client=cast(AsyncRabbiX, rabbix), decision_class="refund"
    )

    assert rabbix.calls[0]["goal"] == "Be careful.\n\nRefund order 7"
    assert rabbix.calls[0]["decision_class"] == "refund"


@pytest.mark.parametrize("input", ["", []], ids=["empty-string", "empty-items"])
async def test_empty_goal_raises_without_checkpointing(final_output: list[Any], input: Any) -> None:
    final_output.append("ok")
    rabbix = FakeRabbiX()

    with pytest.raises(RabbiXError, match="goal is empty"):
        await run_with_checkpoint(
            Agent(name="support", instructions=""), input, client=cast(AsyncRabbiX, rabbix)
        )

    assert rabbix.calls == []


async def test_modified_returns_edited_value(final_output: list[Any]) -> None:
    final_output.append("Refund 42 EUR")

    result = await run_with_checkpoint(
        agent, "x", client=cast(AsyncRabbiX, FakeRabbiX("MODIFIED", "40 EUR"))
    )

    assert result == "40 EUR"


@pytest.mark.parametrize("status", ["REJECTED", "EXPIRED", "UNKNOWN"])
async def test_non_approval_raises(final_output: list[Any], status: str) -> None:
    final_output.append("Refund 42 EUR")

    with pytest.raises(CheckpointRejected):
        await run_with_checkpoint(agent, "x", client=cast(AsyncRabbiX, FakeRabbiX(status)))
