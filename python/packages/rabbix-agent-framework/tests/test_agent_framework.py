from __future__ import annotations

from collections.abc import Sequence
from typing import Any, cast

import pytest
from agent_framework import Agent, AgentResponse, ChatResponse, Message
from rabbix import AgentInfo, AsyncRabbiX, CheckpointRejected, RabbiXError, Verdict
from rabbix_agent_framework import RabbiXCheckpointMiddleware, run_with_checkpoint


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


class FakeChatClient:
    """Minimal SupportsChatGetResponse that always answers with the same text."""

    def __init__(self) -> None:
        self.additional_properties: dict[str, Any] = {}

    def get_response(self, messages: Sequence[Message], **kwargs: Any) -> Any:
        async def respond() -> ChatResponse:
            return ChatResponse(messages=[Message("assistant", ["Refund 42 EUR"])])

        return respond()


def make_agent(instructions: str | None = "Handle refunds.", **kwargs: Any) -> Agent:
    return Agent(client=FakeChatClient(), instructions=instructions, name="support", **kwargs)


async def test_middleware_passes_approved_response_through() -> None:
    rabbix = FakeRabbiX()
    agent = make_agent(middleware=[RabbiXCheckpointMiddleware(cast(AsyncRabbiX, rabbix))])

    response = await agent.run("Customer wants a refund for order 7")

    assert response.text == "Refund 42 EUR"
    assert rabbix.calls == [
        {
            "goal": "Handle refunds.\n\nCustomer wants a refund for order 7",
            "result": "Refund 42 EUR",
            "decision_class": None,
            "agent": AgentInfo(name="support", framework="agent-framework"),
            "metadata": None,
        }
    ]


async def test_middleware_replaces_response_on_modified() -> None:
    rabbix = FakeRabbiX("MODIFIED", edited="Refund 40 EUR")
    agent = make_agent(middleware=[RabbiXCheckpointMiddleware(cast(AsyncRabbiX, rabbix))])

    response = await agent.run("refund order 7")

    assert response.text == "Refund 40 EUR"


async def test_middleware_rejection_propagates_out_of_the_run() -> None:
    rabbix = FakeRabbiX("REJECTED")
    agent = make_agent(middleware=[RabbiXCheckpointMiddleware(cast(AsyncRabbiX, rabbix))])

    with pytest.raises(CheckpointRejected):
        await agent.run("refund order 7")


async def test_middleware_refuses_streaming() -> None:
    rabbix = FakeRabbiX()
    agent = make_agent(middleware=[RabbiXCheckpointMiddleware(cast(AsyncRabbiX, rabbix))])

    with pytest.raises(RabbiXError, match="streaming"):
        await agent.run("refund order 7", stream=True).get_final_response()

    assert rabbix.calls == []


async def test_run_helper_checkpoints_and_returns_response() -> None:
    rabbix = FakeRabbiX()

    response = await run_with_checkpoint(
        make_agent(),
        "refund order 7",
        client=cast(AsyncRabbiX, rabbix),
        decision_class="refund",
        metadata={"ticket": "T-1"},
    )

    assert isinstance(response, AgentResponse)
    assert response.text == "Refund 42 EUR"
    assert rabbix.calls[0]["goal"] == "Handle refunds.\n\nrefund order 7"
    assert rabbix.calls[0]["decision_class"] == "refund"
    assert rabbix.calls[0]["metadata"] == {"ticket": "T-1"}


async def test_run_helper_serialises_structured_edits() -> None:
    rabbix = FakeRabbiX("MODIFIED", edited={"amount": 40})

    response = await run_with_checkpoint(make_agent(), "refund", client=cast(AsyncRabbiX, rabbix))

    assert response.text == '{"amount": 40}'


@pytest.mark.parametrize("status", ["REJECTED", "EXPIRED", "UNKNOWN"])
async def test_run_helper_raises_on_non_approval(status: str) -> None:
    with pytest.raises(CheckpointRejected):
        await run_with_checkpoint(
            make_agent(), "refund", client=cast(AsyncRabbiX, FakeRabbiX(status))
        )


@pytest.mark.parametrize(
    ("instructions", "messages", "goal"),
    [
        ("Be careful.", "Refund order 7", "Be careful.\n\nRefund order 7"),
        (None, "Refund order 7", "Refund order 7"),
        ("Be careful.", ["Hi", "Refund order 7"], "Be careful.\n\nHi\n\nRefund order 7"),
        ("Be careful.", ["Hi", "", "Refund order 7"], "Be careful.\n\nHi\n\nRefund order 7"),
        ("  Be careful.  ", [" Hi "], "  Be careful.  \n\n Hi "),  # verbatim, no trimming
    ],
    ids=["instructions+string", "string-only", "messages", "skips-empty", "verbatim"],
)
async def test_goal_vectors(instructions: str | None, messages: Any, goal: str) -> None:
    rabbix = FakeRabbiX()

    await run_with_checkpoint(make_agent(instructions), messages, client=cast(AsyncRabbiX, rabbix))
    middleware_rabbix = FakeRabbiX()
    agent = make_agent(
        instructions, middleware=[RabbiXCheckpointMiddleware(cast(AsyncRabbiX, middleware_rabbix))]
    )
    await agent.run(messages)

    assert rabbix.calls[0]["goal"] == goal
    assert middleware_rabbix.calls[0]["goal"] == goal


async def test_empty_goal_raises_without_checkpointing() -> None:
    rabbix = FakeRabbiX()
    agent = make_agent("", middleware=[RabbiXCheckpointMiddleware(cast(AsyncRabbiX, rabbix))])

    with pytest.raises(RabbiXError, match="goal is empty"):
        await run_with_checkpoint(make_agent(""), "", client=cast(AsyncRabbiX, rabbix))
    with pytest.raises(RabbiXError, match="goal is empty"):
        await agent.run("")

    assert rabbix.calls == []
