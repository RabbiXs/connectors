"""A real local HTTP server and a fake clock, so tests see exactly what goes on the wire."""

from __future__ import annotations

import asyncio
import json
import threading
from collections.abc import Callable, Iterator
from dataclasses import dataclass, field
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
from typing import Any

import pytest
import rabbix
import rabbix._client


@dataclass
class Recorded:
    method: str
    path: str
    headers: dict[str, str]
    body: bytes


@dataclass
class Reply:
    status: int
    body: Any = None
    headers: dict[str, str] = field(default_factory=dict[str, str])
    drop: bool = False  # close the connection without answering (a network error)


class MockServer:
    """Answers requests from a script of replies; the last reply repeats once the script ends."""

    def __init__(self) -> None:
        self.requests: list[Recorded] = []
        self._replies: list[Reply] = []
        self._lock = threading.Lock()
        self._httpd = ThreadingHTTPServer(("127.0.0.1", 0), self._handler())
        self.url = f"http://127.0.0.1:{self._httpd.server_address[1]}"
        threading.Thread(target=self._httpd.serve_forever, args=(0.01,), daemon=True).start()

    def reply(self, *replies: Reply) -> None:
        self._replies.extend(replies)

    def _next(self, request: Recorded) -> Reply:
        with self._lock:
            self.requests.append(request)
            return self._replies.pop(0) if len(self._replies) > 1 else self._replies[0]

    def _handler(self) -> type[BaseHTTPRequestHandler]:
        server = self

        class Handler(BaseHTTPRequestHandler):
            def _handle(self) -> None:
                length = int(self.headers.get("Content-Length") or 0)
                request = Recorded(
                    method=self.command,
                    path=self.path,
                    headers={name.lower(): value for name, value in self.headers.items()},
                    body=self.rfile.read(length),
                )
                reply = server._next(request)
                if reply.drop:
                    self.close_connection = True
                    return
                if reply.body is None:
                    payload = b""
                elif isinstance(reply.body, str):  # raw text, e.g. a malformed response
                    payload = reply.body.encode()
                else:
                    payload = json.dumps(reply.body).encode()
                self.send_response(reply.status)
                for name, value in reply.headers.items():
                    self.send_header(name, value)
                self.send_header("Content-Length", str(len(payload)))
                self.end_headers()
                self.wfile.write(payload)

            do_GET = do_POST = _handle

            def log_message(self, format: str, *args: Any) -> None:
                pass

        return Handler

    def close(self) -> None:
        self._httpd.shutdown()
        self._httpd.server_close()


@pytest.fixture
def server() -> Iterator[MockServer]:
    s = MockServer()
    yield s
    s.close()


class FakeClock:
    def __init__(self) -> None:
        self.now = 0.0
        self.sleeps: list[float] = []

    def monotonic(self) -> float:
        return self.now

    def sleep(self, seconds: float) -> None:
        self.sleeps.append(seconds)
        self.now += seconds

    async def async_sleep(self, seconds: float) -> None:
        self.sleep(seconds)


@pytest.fixture(autouse=True)
def clock(monkeypatch: pytest.MonkeyPatch) -> FakeClock:
    c = FakeClock()
    monkeypatch.setattr(rabbix._client, "monotonic", c.monotonic)
    monkeypatch.setattr(rabbix._client, "sleep", c.sleep)
    monkeypatch.setattr(rabbix._client, "async_sleep", c.async_sleep)
    return c


class AsyncAdapter:
    """Exposes AsyncRabbiX through a blocking interface so each test runs for both clients."""

    def __init__(self, **kwargs: Any) -> None:
        self._kwargs = kwargs

    def __getattr__(self, name: str) -> Callable[..., Any]:
        def call(*args: Any, **kwargs: Any) -> Any:
            async def run() -> Any:
                async with rabbix.AsyncRabbiX(**self._kwargs) as client:
                    return await getattr(client, name)(*args, **kwargs)

            return asyncio.run(run())

        return call


ClientFactory = Callable[..., Any]


@pytest.fixture(params=["sync", "async"])
def make_client(request: pytest.FixtureRequest, server: MockServer) -> ClientFactory:
    def make(**kwargs: Any) -> Any:
        kwargs = {"api_key": "test-key", "base_url": server.url, **kwargs}
        return rabbix.RabbiX(**kwargs) if request.param == "sync" else AsyncAdapter(**kwargs)

    return make
