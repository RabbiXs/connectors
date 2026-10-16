from __future__ import annotations

from collections.abc import AsyncIterator
from typing import Any, cast

import pytest
import rabbix_claude_agent_sdk
from claude_agent_sdk import AssistantMessage, ClaudeAgentOptions, ResultMessage, TextBlock
from rabbix import AgentInfo, AsyncRabbiX, CheckpointRejected, RabbiXError, Verdict
from rabbix_claude_agent_sdk import run


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


def result_message(**kwargs: Any) -> ResultMessage:
    fields: dict[str, Any] = {
        "subtype": "success",
        "duration_ms": 1,
        "duration_api_ms": 1,
        "is_error": False,
        "num_turns": 1,
        "session_id": "s",
        "result": "Refund 42 EUR",
        **kwargs,
    }
    return ResultMessage(**fields)


@pytest.fixture
def messages(monkeypatch: pytest.MonkeyPatch) -> list[Any]:
    """Replace query(); append to the returned list to script the streamed messages."""
    scripted: list[Any] = []

    async def fake_query(*, prompt: str, options: Any = None) -> AsyncIterator[Any]:
        for message in scripted:
            yield message

    monkeypatch.setattr(rabbix_claude_agent_sdk, "query", fake_query)
    return scripted


async def test_approved_result_text(messages: list[Any]) -> None:
    messages.append(AssistantMessage(content=[TextBlock(text="thinking")], model="m"))
    messages.append(result_message())
    rabbix = FakeRabbiX()

    result = await run(
        "refund order 7",
        client=cast(AsyncRabbiX, rabbix),
        options=ClaudeAgentOptions(system_prompt="Handle refunds."),
        agent_name="support",
        metadata={"ticket": "T-1"},
    )

    assert result == "Refund 42 EUR"
    assert rabbix.calls == [
        {
            "goal": "Handle refunds.\n\nrefund order 7",
            "result": "Refund 42 EUR",
            "decision_class": None,
            "agent": AgentInfo(name="support", framework="claude-agent-sdk"),
            "metadata": {"ticket": "T-1"},
        }
    ]


async def test_structured_output_and_preset_append(messages: list[Any]) -> None:
    messages.append(result_message(structured_output={"amount": 42}))
    rabbix = FakeRabbiX()
    options = ClaudeAgentOptions(
        system_prompt={"type": "preset", "preset": "claude_code", "append": "Handle refunds."}
    )

    result = await run("refund", client=cast(AsyncRabbiX, rabbix), options=options)

    assert result == {"amount": 42}
    assert rabbix.calls[0]["goal"] == "Handle refunds.\n\nrefund"


async def test_modified_returns_edited(messages: list[Any]) -> None:
    messages.append(result_message())

    result = await run("refund", client=cast(AsyncRabbiX, FakeRabbiX("MODIFIED", "40 EUR")))

    assert result == "40 EUR"


@pytest.mark.parametrize("status", ["REJECTED", "EXPIRED", "UNKNOWN"])
async def test_non_approval_raises(messages: list[Any], status: str) -> None:
    messages.append(result_message())

    with pytest.raises(CheckpointRejected):
        await run("refund", client=cast(AsyncRabbiX, FakeRabbiX(status)))


@pytest.mark.parametrize(
    "scripted",
    [[], [result_message(is_error=True, subtype="error_max_turns")], [result_message(result=None)]],
)
async def test_failed_run_raises_without_checkpoint(
    messages: list[Any], scripted: list[Any]
) -> None:
    messages.extend(scripted)
    rabbix = FakeRabbiX()

    with pytest.raises(RabbiXError):
        await run("refund", client=cast(AsyncRabbiX, rabbix))

    assert rabbix.calls == []
