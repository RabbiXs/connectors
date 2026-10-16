from __future__ import annotations

from dataclasses import dataclass
from typing import Any


@dataclass(frozen=True)
class AgentInfo:
    """Identifies the agent that produced the result. Both fields are optional."""

    name: str | None = None
    framework: str | None = None


@dataclass(frozen=True)
class Verdict:
    """RabbiX's answer for one checkpoint.

    ``status`` is one of APPROVED, MODIFIED, REJECTED, EXPIRED or PENDING, but is kept as a
    plain string: a status this SDK does not know is passed through, and ``checkpoint()``
    treats it as a rejection.
    """

    decision_id: str
    status: str
    route: str | None = None
    result: Any = None
    """The edited result to act on instead of the original. Set when status is MODIFIED."""
    reason: str | None = None
