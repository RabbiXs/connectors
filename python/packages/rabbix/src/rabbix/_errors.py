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
    ``decision_id`` is set when the error happened while polling a known decision; resume
    with ``wait(decision_id)``. It is None for errors from the initial submission.
    """

    def __init__(
        self, status: int | None, code: str | None, message: str, decision_id: str | None = None
    ) -> None:
        super().__init__(message)
        self.status = status
        self.code = code
        self.message = message
        self.decision_id = decision_id

    def __str__(self) -> str:
        where = f"HTTP {self.status}" if self.status is not None else "network error"
        text = f"{where} ({self.code}): {self.message}" if self.code else f"{where}: {self.message}"
        return f"checkpoint {self.decision_id}: {text}" if self.decision_id else text
