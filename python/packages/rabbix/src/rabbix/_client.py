from __future__ import annotations

import gzip
import json
import os
import random
import uuid
from asyncio import sleep as async_sleep
from collections.abc import Generator, Mapping
from contextlib import contextmanager
from time import monotonic, sleep
from types import TracebackType
from typing import Any, Self, cast
from urllib.parse import quote

import httpx

from ._errors import ApiError, CheckpointRejected, CheckpointTimeout, PayloadTooLarge, RabbiXError
from ._types import AgentInfo, Verdict

__version__ = "0.1.0"

DEFAULT_BASE_URL = "https://api.rabbix.invalid"
MAX_BODY_BYTES = 10_485_760
GZIP_THRESHOLD = 8192
MAX_POLL_DELAY = 30.0


class _BaseClient:
    def __init__(
        self,
        api_key: str | None,
        base_url: str | None,
        wait_timeout: float | None,
        max_retries: int,
    ) -> None:
        api_key = api_key or os.environ.get("RABBIX_API_KEY")
        if not api_key:
            raise RabbiXError("missing API key: pass api_key or set RABBIX_API_KEY")
        base_url = base_url or os.environ.get("RABBIX_BASE_URL") or DEFAULT_BASE_URL
        self._base_url = base_url.rstrip("/")
        self._api_key = api_key
        self._wait_timeout = wait_timeout
        self._max_retries = max_retries

    def _init_headers(self, headers: httpx.Headers) -> None:
        # Replace httpx's default Accept/Accept-Encoding/Connection/User-Agent headers so the
        # only headers sent are the ones documented in the README.
        headers.clear()
        headers["Authorization"] = f"Bearer {self._api_key}"
        headers["User-Agent"] = f"rabbix-python/{__version__}"

    def _deadline(self) -> float | None:
        return None if self._wait_timeout is None else monotonic() + self._wait_timeout


class RabbiX(_BaseClient):
    """Synchronous RabbiX client. Reuse one instance; close it when done."""

    def __init__(
        self,
        api_key: str | None = None,
        base_url: str | None = None,
        wait_timeout: float | None = 900.0,
        request_timeout: float = 30.0,
        max_retries: int = 3,
    ) -> None:
        super().__init__(api_key, base_url, wait_timeout, max_retries)
        self._http = httpx.Client(base_url=self._base_url, timeout=request_timeout)
        self._init_headers(self._http.headers)

    def checkpoint(
        self,
        goal: str,
        result: Any,
        *,
        decision_class: str | None = None,
        agent: AgentInfo | None = None,
        metadata: Mapping[str, str] | None = None,
    ) -> Any:
        """Submit goal and result; return the result to act on, or raise.

        Returns ``result`` on APPROVED and the edited ``verdict.result`` on MODIFIED. Raises
        ``CheckpointRejected`` for every other status.
        """
        verdict = self.checkpoint_verdict(
            goal, result, decision_class=decision_class, agent=agent, metadata=metadata
        )
        return _result_to_act_on(verdict, result)

    def checkpoint_verdict(
        self,
        goal: str,
        result: Any,
        *,
        decision_class: str | None = None,
        agent: AgentInfo | None = None,
        metadata: Mapping[str, str] | None = None,
    ) -> Verdict:
        """Like ``checkpoint()``, but return the final verdict instead of raising on it."""
        deadline = self._deadline()
        content, headers = _checkpoint_request(goal, result, decision_class, agent, metadata)
        response = self._send("POST", "/v1/checkpoints", content, headers)
        verdict = _parse_verdict(response)
        with _polling(verdict.decision_id):
            return self._poll(verdict, response, deadline)

    def wait(self, decision_id: str) -> Verdict:
        """Resume waiting for the final verdict of a pending decision."""
        deadline = self._deadline()
        with _polling(decision_id):
            response = self._send("GET", _decision_path(decision_id))
            return self._poll(_parse_verdict(response), response, deadline)

    def _poll(self, verdict: Verdict, response: httpx.Response, deadline: float | None) -> Verdict:
        backoff = 1.0
        while verdict.status == "PENDING":
            sleep(_poll_delay(response, backoff, deadline, verdict.decision_id))
            response = self._send("GET", _decision_path(verdict.decision_id))
            verdict = _parse_verdict(response)
            backoff = min(backoff * 2, MAX_POLL_DELAY)
        return verdict

    def _send(
        self,
        method: str,
        path: str,
        content: bytes | None = None,
        headers: dict[str, str] | None = None,
    ) -> httpx.Response:
        attempt = 0
        while True:
            try:
                response = self._http.request(method, path, content=content, headers=headers)
            except httpx.TransportError as exc:
                if attempt >= self._max_retries:
                    raise ApiError(None, None, str(exc) or type(exc).__name__) from exc
                delay = _backoff(attempt)
            else:
                if not _is_retryable(response.status_code):
                    return response
                if attempt >= self._max_retries:
                    raise _api_error(response)
                delay = _retry_delay(response, attempt)
            attempt += 1
            sleep(delay)

    def close(self) -> None:
        self._http.close()

    def __enter__(self) -> Self:
        return self

    def __exit__(
        self,
        exc_type: type[BaseException] | None,
        exc: BaseException | None,
        tb: TracebackType | None,
    ) -> None:
        self.close()


class AsyncRabbiX(_BaseClient):
    """Asynchronous RabbiX client. Same behaviour as ``RabbiX``."""

    def __init__(
        self,
        api_key: str | None = None,
        base_url: str | None = None,
        wait_timeout: float | None = 900.0,
        request_timeout: float = 30.0,
        max_retries: int = 3,
    ) -> None:
        super().__init__(api_key, base_url, wait_timeout, max_retries)
        self._http = httpx.AsyncClient(base_url=self._base_url, timeout=request_timeout)
        self._init_headers(self._http.headers)

    async def checkpoint(
        self,
        goal: str,
        result: Any,
        *,
        decision_class: str | None = None,
        agent: AgentInfo | None = None,
        metadata: Mapping[str, str] | None = None,
    ) -> Any:
        """Submit goal and result; return the result to act on, or raise.

        Returns ``result`` on APPROVED and the edited ``verdict.result`` on MODIFIED. Raises
        ``CheckpointRejected`` for every other status.
        """
        verdict = await self.checkpoint_verdict(
            goal, result, decision_class=decision_class, agent=agent, metadata=metadata
        )
        return _result_to_act_on(verdict, result)

    async def checkpoint_verdict(
        self,
        goal: str,
        result: Any,
        *,
        decision_class: str | None = None,
        agent: AgentInfo | None = None,
        metadata: Mapping[str, str] | None = None,
    ) -> Verdict:
        """Like ``checkpoint()``, but return the final verdict instead of raising on it."""
        deadline = self._deadline()
        content, headers = _checkpoint_request(goal, result, decision_class, agent, metadata)
        response = await self._send("POST", "/v1/checkpoints", content, headers)
        verdict = _parse_verdict(response)
        with _polling(verdict.decision_id):
            return await self._poll(verdict, response, deadline)

    async def wait(self, decision_id: str) -> Verdict:
        """Resume waiting for the final verdict of a pending decision."""
        deadline = self._deadline()
        with _polling(decision_id):
            response = await self._send("GET", _decision_path(decision_id))
            return await self._poll(_parse_verdict(response), response, deadline)

    async def _poll(
        self, verdict: Verdict, response: httpx.Response, deadline: float | None
    ) -> Verdict:
        backoff = 1.0
        while verdict.status == "PENDING":
            await async_sleep(_poll_delay(response, backoff, deadline, verdict.decision_id))
            response = await self._send("GET", _decision_path(verdict.decision_id))
            verdict = _parse_verdict(response)
            backoff = min(backoff * 2, MAX_POLL_DELAY)
        return verdict

    async def _send(
        self,
        method: str,
        path: str,
        content: bytes | None = None,
        headers: dict[str, str] | None = None,
    ) -> httpx.Response:
        attempt = 0
        while True:
            try:
                response = await self._http.request(method, path, content=content, headers=headers)
            except httpx.TransportError as exc:
                if attempt >= self._max_retries:
                    raise ApiError(None, None, str(exc) or type(exc).__name__) from exc
                delay = _backoff(attempt)
            else:
                if not _is_retryable(response.status_code):
                    return response
                if attempt >= self._max_retries:
                    raise _api_error(response)
                delay = _retry_delay(response, attempt)
            attempt += 1
            await async_sleep(delay)

    async def aclose(self) -> None:
        await self._http.aclose()

    async def __aenter__(self) -> Self:
        return self

    async def __aexit__(
        self,
        exc_type: type[BaseException] | None,
        exc: BaseException | None,
        tb: TracebackType | None,
    ) -> None:
        await self.aclose()


def _checkpoint_request(
    goal: str,
    result: Any,
    decision_class: str | None,
    agent: AgentInfo | None,
    metadata: Mapping[str, str] | None,
) -> tuple[bytes, dict[str, str]]:
    body: dict[str, Any] = {"goal": goal, "result": result}
    if decision_class is not None:
        body["decisionClass"] = decision_class
    if agent is not None:
        fields = {"name": agent.name, "framework": agent.framework}
        body["agent"] = {key: value for key, value in fields.items() if value is not None}
    if metadata is not None:
        body["metadata"] = dict(metadata)

    content = json.dumps(body, ensure_ascii=False, allow_nan=False, separators=(",", ":")).encode()
    if len(content) > MAX_BODY_BYTES:
        raise PayloadTooLarge(len(content), MAX_BODY_BYTES)

    # One key per checkpoint call; _send reuses these headers on every retry.
    headers = {"Content-Type": "application/json", "Idempotency-Key": str(uuid.uuid4())}
    if len(content) > GZIP_THRESHOLD:
        content = gzip.compress(content, mtime=0)
        headers["Content-Encoding"] = "gzip"
    return content, headers


@contextmanager
def _polling(decision_id: str) -> Generator[None]:
    """Tag API errors raised while polling with the decision they concern."""
    try:
        yield
    except ApiError as exc:
        exc.decision_id = decision_id
        raise


def _decision_path(decision_id: str) -> str:
    return f"/v1/checkpoints/{quote(decision_id, safe='')}"


def _result_to_act_on(verdict: Verdict, result: Any) -> Any:
    if verdict.status == "APPROVED":
        return result
    if verdict.status == "MODIFIED" and verdict.result is not None:
        return verdict.result
    # REJECTED, EXPIRED, MODIFIED without a result, or a status we don't know: fail closed.
    raise CheckpointRejected(verdict)


def _parse_verdict(response: httpx.Response) -> Verdict:
    if response.status_code not in (200, 202):
        raise _api_error(response)
    data = _json_object(response)
    if (
        data is None
        or not isinstance(data.get("decisionId"), str)
        or not isinstance(data.get("status"), str)
    ):
        raise ApiError(response.status_code, "invalid_response", "response is not a verdict")
    return Verdict(
        decision_id=data["decisionId"],
        status=data["status"],
        route=data.get("route"),
        result=data.get("result"),
        reason=data.get("reason"),
    )


def _api_error(response: httpx.Response) -> ApiError:
    data = _json_object(response) or {}
    code = data.get("code")
    message = data.get("message")
    return ApiError(
        response.status_code,
        code if isinstance(code, str) else None,
        message if isinstance(message, str) else response.reason_phrase,
    )


def _json_object(response: httpx.Response) -> dict[str, Any] | None:
    try:
        data = response.json()
    except ValueError:
        return None
    return cast(dict[str, Any], data) if isinstance(data, dict) else None


def _is_retryable(status: int) -> bool:
    return status == 429 or status >= 500


def _retry_after(response: httpx.Response) -> float | None:
    value = response.headers.get("Retry-After")
    if value is None:
        return None
    try:
        seconds = float(value)
    except ValueError:
        return None  # HTTP-date form is not used by the API; fall back to backoff.
    return seconds if seconds >= 0 else None


def _backoff(attempt: int) -> float:
    return 0.5 * 2**attempt * random.uniform(0.8, 1.2)


def _retry_delay(response: httpx.Response, attempt: int) -> float:
    retry_after = _retry_after(response)
    return _backoff(attempt) if retry_after is None else retry_after


def _poll_delay(
    response: httpx.Response, backoff: float, deadline: float | None, decision_id: str
) -> float:
    delay = _retry_after(response)
    if delay is None:
        delay = backoff
    if deadline is None:
        return delay
    remaining = deadline - monotonic()
    if remaining <= 0:
        raise CheckpointTimeout(decision_id)
    # Sleep at most until the deadline, then poll one last time.
    return min(delay, remaining)
