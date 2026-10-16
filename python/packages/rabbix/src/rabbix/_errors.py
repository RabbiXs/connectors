from __future__ import annotations

from ._types import Verdict


class RabbiXError(Exception):
    """Base class for every error this SDK raises."""


class CheckpointRejected(RabbiXError):
    """The verdict does not allow acting on the result (REJECTED, EXPIRED or unknown)."""

    def __init__(self, verdict: Verdict) -> None:
        detail = f": {verdict.reason}" if verdict.reason else ""
        super().__init__(f"checkpoint {verdict.decision_id} {verdict.status}{detail}")
        self.verdict = verdict


class CheckpointTimeout(RabbiXError):
    """No final verdict within ``wait_timeout``. Resume with ``wait(decision_id)``."""

    def __init__(self, decision_id: str) -> None:
        super().__init__(f"checkpoint {decision_id} still pending; resume with wait()")
        self.decision_id = decision_id


class PayloadTooLarge(RabbiXError):
    """The uncompressed request body exceeds 10 MB. Nothing was sent."""

    def __init__(self, size: int, limit: int) -> None:
        super().__init__(f"request body is {size} bytes, limit is {limit}")
        self.size = size
        self.limit = limit


class ApiError(RabbiXError):
    """A non-retryable HTTP error, or retries were exhausted.

    ``status`` is None when the request never got an HTTP response (network failure).
    """

    def __init__(self, status: int | None, code: str | None, message: str) -> None:
        where = f"HTTP {status}" if status is not None else "network error"
        super().__init__(f"{where} ({code}): {message}" if code else f"{where}: {message}")
        self.status = status
        self.code = code
        self.message = message
