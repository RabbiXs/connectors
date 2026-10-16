from __future__ import annotations

import gzip
import json
import uuid
from typing import Any

import pytest
from conftest import ClientFactory, FakeClock, MockServer, Reply
from rabbix import (
    AgentInfo,
    ApiError,
    AsyncRabbiX,
    CheckpointRejected,
    CheckpointTimeout,
    PayloadTooLarge,
    RabbiX,
    RabbiXError,
    Verdict,
)

POST_HEADERS = {
    "host",
    "content-length",
    "content-type",
    "authorization",
    "idempotency-key",
    "user-agent",
}
GET_HEADERS = {"host", "authorization", "user-agent"}


def verdict(status: str, **extra: Any) -> dict[str, Any]:
    return {"decisionId": "dec_1", "status": status, **extra}


def test_approved_returns_original_and_sends_exactly_the_documented_request(
    make_client: ClientFactory, server: MockServer
) -> None:
    server.reply(Reply(200, verdict("APPROVED", route="AI_AUTONOMOUS")))
    result = {"refund": 42}

    returned = make_client().checkpoint("Refund order 7", result)

    assert returned is result
    (request,) = server.requests
    assert (request.method, request.path) == ("POST", "/v1/checkpoints")
    assert set(request.headers) == POST_HEADERS
    assert request.headers["authorization"] == "Bearer test-key"
    assert request.headers["content-type"] == "application/json"
    assert request.headers["user-agent"] == "rabbix-python/0.1.0"
    assert uuid.UUID(request.headers["idempotency-key"]).version == 4
    assert json.loads(request.body) == {"goal": "Refund order 7", "result": {"refund": 42}}


def test_optional_fields_are_camel_case(make_client: ClientFactory, server: MockServer) -> None:
    server.reply(Reply(200, verdict("APPROVED")))

    make_client().checkpoint(
        "g",
        ["a", "b"],
        decision_class="refund",
        agent=AgentInfo(name="support", framework="openai-agents"),
        metadata={"ticket": "T-1"},
    )

    assert json.loads(server.requests[0].body) == {
        "goal": "g",
        "result": ["a", "b"],
        "decisionClass": "refund",
        "agent": {"name": "support", "framework": "openai-agents"},
        "metadata": {"ticket": "T-1"},
    }


def test_modified_returns_edited_result(make_client: ClientFactory, server: MockServer) -> None:
    server.reply(Reply(200, verdict("MODIFIED", result="edited", reason="tone")))

    assert make_client().checkpoint("g", "original") == "edited"


@pytest.mark.parametrize("status", ["REJECTED", "EXPIRED", "SOMETHING_NEW"])
def test_non_approval_raises_with_verdict(
    make_client: ClientFactory, server: MockServer, status: str
) -> None:
    server.reply(Reply(200, verdict(status, reason="no")))

    with pytest.raises(CheckpointRejected) as info:
        make_client().checkpoint("g", "r")

    assert info.value.verdict == Verdict(decision_id="dec_1", status=status, reason="no")


def test_modified_without_result_fails_closed(
    make_client: ClientFactory, server: MockServer
) -> None:
    server.reply(Reply(200, verdict("MODIFIED")))

    with pytest.raises(CheckpointRejected):
        make_client().checkpoint("g", "r")


def test_checkpoint_verdict_returns_rejection(
    make_client: ClientFactory, server: MockServer
) -> None:
    server.reply(Reply(200, verdict("REJECTED", route="HUMAN_ONLY", reason="policy")))

    assert make_client().checkpoint_verdict("g", "r") == Verdict(
        decision_id="dec_1", status="REJECTED", route="HUMAN_ONLY", reason="policy"
    )


def test_pending_is_polled_honouring_retry_after(
    make_client: ClientFactory, server: MockServer, clock: FakeClock
) -> None:
    server.reply(
        Reply(202, verdict("PENDING", route="HUMAN_IN_LOOP"), {"Retry-After": "3"}),
        Reply(200, verdict("PENDING")),
        Reply(200, verdict("APPROVED")),
    )

    assert make_client().checkpoint("g", "r") == "r"

    assert clock.sleeps == [3.0, 2.0]  # Retry-After, then backoff (1s doubled to 2s)
    post, *polls = server.requests
    assert post.method == "POST"
    for poll in polls:
        assert (poll.method, poll.path, poll.body) == ("GET", "/v1/checkpoints/dec_1", b"")
        assert set(poll.headers) == GET_HEADERS


def test_poll_backoff_caps_at_30_seconds(
    make_client: ClientFactory, server: MockServer, clock: FakeClock
) -> None:
    server.reply(*[Reply(202 if i == 0 else 200, verdict("PENDING")) for i in range(7)])
    server.reply(Reply(200, verdict("APPROVED")))

    make_client(wait_timeout=None).checkpoint("g", "r")

    assert clock.sleeps == [1, 2, 4, 8, 16, 30, 30]


def test_wait_timeout_raises_with_decision_id(
    make_client: ClientFactory, server: MockServer, clock: FakeClock
) -> None:
    server.reply(Reply(202, verdict("PENDING")), Reply(200, verdict("PENDING")))

    with pytest.raises(CheckpointTimeout) as info:
        make_client(wait_timeout=5).checkpoint("g", "r")

    assert info.value.decision_id == "dec_1"
    assert clock.sleeps == [1, 2, 2]  # the last sleep is cut short to end at the deadline


def test_wait_resumes_a_pending_decision(make_client: ClientFactory, server: MockServer) -> None:
    server.reply(Reply(200, verdict("PENDING")), Reply(200, verdict("APPROVED")))

    assert make_client().wait("dec/1").status == "APPROVED"

    assert [r.method for r in server.requests] == ["GET", "GET"]
    assert server.requests[0].path == "/v1/checkpoints/dec%2F1"


def test_retries_5xx_with_the_same_idempotency_key(
    make_client: ClientFactory, server: MockServer, clock: FakeClock
) -> None:
    server.reply(
        Reply(503, {"code": "unavailable", "message": "busy"}),
        Reply(429, {"code": "rate_limited", "message": "slow down"}, {"Retry-After": "7"}),
        Reply(200, verdict("APPROVED")),
    )

    make_client().checkpoint("g", "r")

    keys = {r.headers["idempotency-key"] for r in server.requests}
    assert len(server.requests) == 3
    assert len(keys) == 1
    assert 0.4 <= clock.sleeps[0] <= 0.6
    assert clock.sleeps[1] == 7.0


def test_new_call_gets_a_new_idempotency_key(
    make_client: ClientFactory, server: MockServer
) -> None:
    server.reply(Reply(200, verdict("APPROVED")))
    client = make_client()

    client.checkpoint("g", "r")
    client.checkpoint("g", "r")

    assert len({r.headers["idempotency-key"] for r in server.requests}) == 2


def test_retries_are_exhausted(
    make_client: ClientFactory, server: MockServer, clock: FakeClock
) -> None:
    server.reply(Reply(500, {"code": "internal", "message": "boom"}))

    with pytest.raises(ApiError) as info:
        make_client().checkpoint("g", "r")

    assert (info.value.status, info.value.code, info.value.message) == (500, "internal", "boom")
    assert len(server.requests) == 4  # 1 + max_retries
    assert len(clock.sleeps) == 3


def test_client_error_is_not_retried(make_client: ClientFactory, server: MockServer) -> None:
    server.reply(Reply(400, {"code": "invalid_request", "message": "goal is empty"}))

    with pytest.raises(ApiError) as info:
        make_client().checkpoint("", "r")

    assert (info.value.status, info.value.code) == (400, "invalid_request")
    assert len(server.requests) == 1


def test_malformed_success_response_fails_closed(
    make_client: ClientFactory, server: MockServer
) -> None:
    server.reply(Reply(200, "<html>proxy</html>"))

    with pytest.raises(ApiError) as info:
        make_client().checkpoint("g", "r")

    assert info.value.code == "invalid_response"


def test_network_error_is_retried_with_the_same_idempotency_key(
    make_client: ClientFactory, server: MockServer
) -> None:
    server.reply(Reply(0, drop=True), Reply(200, verdict("APPROVED")))

    make_client().checkpoint("g", "r")

    assert len(server.requests) == 2
    assert len({r.headers["idempotency-key"] for r in server.requests}) == 1


def test_network_errors_are_exhausted(
    make_client: ClientFactory, server: MockServer, clock: FakeClock
) -> None:
    server.reply(Reply(0, drop=True))

    with pytest.raises(ApiError) as info:
        make_client().checkpoint("g", "r")

    assert info.value.status is None
    assert info.value.decision_id is None  # the POST failed: no decision exists yet
    assert len(server.requests) == 4
    assert len(clock.sleeps) == 3


def test_post_error_has_no_decision_id(make_client: ClientFactory, server: MockServer) -> None:
    server.reply(Reply(500, {"code": "internal", "message": "boom"}))

    with pytest.raises(ApiError) as info:
        make_client().checkpoint("g", "r")

    assert info.value.decision_id is None
    assert "dec_" not in str(info.value)


@pytest.mark.parametrize(
    ("poll_reply", "status"),
    [
        (Reply(0, drop=True), None),
        (Reply(500, {"code": "internal", "message": "boom"}), 500),
        (Reply(404, {"code": "not_found", "message": "gone"}), 404),
    ],
    ids=["network", "5xx", "404"],
)
def test_polling_error_carries_decision_id(
    make_client: ClientFactory, server: MockServer, poll_reply: Reply, status: int | None
) -> None:
    server.reply(Reply(202, verdict("PENDING")), poll_reply)

    with pytest.raises(ApiError) as info:
        make_client().checkpoint("g", "r")

    assert info.value.status == status
    assert info.value.decision_id == "dec_1"
    assert str(info.value).startswith("checkpoint dec_1: ")


@pytest.mark.parametrize(
    "poll_reply",
    [Reply(0, drop=True), Reply(500, {"code": "internal", "message": "boom"}), Reply(404)],
    ids=["network", "5xx", "404"],
)
def test_wait_error_carries_decision_id(
    make_client: ClientFactory, server: MockServer, poll_reply: Reply
) -> None:
    server.reply(poll_reply)

    with pytest.raises(ApiError) as info:
        make_client().wait("dec_7")

    assert info.value.decision_id == "dec_7"
    assert "dec_7" in str(info.value)


def test_large_body_is_gzipped(make_client: ClientFactory, server: MockServer) -> None:
    server.reply(Reply(200, verdict("APPROVED")))
    result = "x" * 10_000

    make_client().checkpoint("g", result)

    (request,) = server.requests
    assert set(request.headers) == POST_HEADERS | {"content-encoding"}
    assert request.headers["content-encoding"] == "gzip"
    assert json.loads(gzip.decompress(request.body)) == {"goal": "g", "result": result}


def test_body_over_10mb_raises_before_any_request(
    make_client: ClientFactory, server: MockServer
) -> None:
    server.reply(Reply(200, verdict("APPROVED")))

    with pytest.raises(PayloadTooLarge):
        make_client().checkpoint("g", "x" * 10_485_760)

    assert server.requests == []


@pytest.mark.parametrize("cls", [RabbiX, AsyncRabbiX])
def test_missing_api_key_raises(monkeypatch: pytest.MonkeyPatch, cls: type[Any]) -> None:
    monkeypatch.delenv("RABBIX_API_KEY", raising=False)

    with pytest.raises(RabbiXError, match="RABBIX_API_KEY"):
        cls()


def test_config_falls_back_to_environment(
    monkeypatch: pytest.MonkeyPatch, server: MockServer
) -> None:
    monkeypatch.setenv("RABBIX_API_KEY", "env-key")
    monkeypatch.setenv("RABBIX_BASE_URL", server.url)
    server.reply(Reply(200, verdict("APPROVED")))

    with RabbiX() as client:
        client.checkpoint("g", "r")

    assert server.requests[0].headers["authorization"] == "Bearer env-key"


def test_default_base_url_is_the_placeholder(monkeypatch: pytest.MonkeyPatch) -> None:
    monkeypatch.delenv("RABBIX_BASE_URL", raising=False)

    with RabbiX(api_key="k") as client:
        assert str(client._http.base_url) == "https://api.rabbix.invalid"  # pyright: ignore[reportPrivateUsage]
