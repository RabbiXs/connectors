import { gunzipSync } from "node:zlib";
import { afterEach, describe, expect, test, vi } from "vite-plus/test";
import {
  ApiError,
  CheckpointRejected,
  CheckpointTimeout,
  PayloadTooLarge,
  RabbiX,
  RabbiXError,
} from "../src/index.ts";
import { mockApi, type Reply } from "./server.ts";

const UUID = /^[0-9a-f]{8}-[0-9a-f]{4}-4[0-9a-f]{3}-[89ab][0-9a-f]{3}-[0-9a-f]{12}$/;
// Headers fetch (undici) adds on its own; everything else must come from the SDK.
const TRANSPORT_HEADERS = new Set([
  "host",
  "connection",
  "content-length",
  "accept",
  "accept-language",
  "accept-encoding",
  "sec-fetch-mode",
]);

let api: Awaited<ReturnType<typeof mockApi>> | undefined;
afterEach(async () => {
  await api?.close();
  api = undefined;
  vi.unstubAllEnvs();
});

async function setup(replies: Reply[], options: ConstructorParameters<typeof RabbiX>[0] = {}) {
  api = await mockApi(replies);
  const client = new RabbiX({ apiKey: "test-key", baseUrl: api.baseUrl, ...options });
  return { client, requests: api.requests };
}

const verdict = (status: string, extra: object = {}) => ({
  status: 200,
  body: { decisionId: "d1", status, ...extra },
});

const input = { goal: "Refund order 42", result: { refund: 10 } };

describe("checkpoint", () => {
  test("APPROVED returns the original result", async () => {
    const { client } = await setup([verdict("APPROVED", { result: { refund: 999 } })]);
    await expect(client.checkpoint(input)).resolves.toBe(input.result);
  });

  test("MODIFIED returns the edited result", async () => {
    const { client } = await setup([verdict("MODIFIED", { result: { refund: 5 } })]);
    await expect(client.checkpoint(input)).resolves.toEqual({ refund: 5 });
  });

  test.each(["REJECTED", "EXPIRED", "SOMETHING_NEW"])(
    "%s throws CheckpointRejected",
    async (status) => {
      const { client } = await setup([verdict(status, { reason: "no" })]);
      const error = await client.checkpoint(input).catch((e: unknown) => e);
      expect(error).toBeInstanceOf(CheckpointRejected);
      expect((error as CheckpointRejected).verdict).toMatchObject({ decisionId: "d1", status });
    },
  );

  test("checkpointVerdict returns REJECTED without throwing", async () => {
    const { client } = await setup([verdict("REJECTED", { reason: "policy" })]);
    await expect(client.checkpointVerdict(input)).resolves.toEqual({
      decisionId: "d1",
      status: "REJECTED",
      reason: "policy",
    });
  });

  test("a response that is not a verdict fails closed", async () => {
    const { client } = await setup([{ status: 200, body: { ok: true } }]);
    await expect(client.checkpoint(input)).rejects.toBeInstanceOf(ApiError);
  });

  test("sends exactly the documented body and headers", async () => {
    const { client, requests } = await setup([verdict("APPROVED")]);
    await client.checkpoint({
      goal: "g",
      result: "r",
      decisionClass: "refund",
      agent: { name: "a", framework: "openai-agents" },
      metadata: { tenant: "t1" },
    });

    expect(requests).toHaveLength(1);
    const [req] = requests;
    expect(req!.method).toBe("POST");
    expect(req!.url).toBe("/v1/checkpoints");
    expect(JSON.parse(req!.body.toString("utf8"))).toEqual({
      goal: "g",
      result: "r",
      decisionClass: "refund",
      agent: { name: "a", framework: "openai-agents" },
      metadata: { tenant: "t1" },
    });
    const sdkHeaders = Object.fromEntries(
      Object.entries(req!.headers).filter(([name]) => !TRANSPORT_HEADERS.has(name)),
    );
    expect(sdkHeaders).toEqual({
      authorization: "Bearer test-key",
      "content-type": "application/json",
      "idempotency-key": expect.stringMatching(UUID),
      "user-agent": "rabbix-typescript/0.1.0",
    });
  });

  test("omits absent optional fields", async () => {
    const { client, requests } = await setup([verdict("APPROVED")]);
    await client.checkpoint({ goal: "g", result: ["x"] });
    expect(JSON.parse(requests[0]!.body.toString("utf8"))).toEqual({ goal: "g", result: ["x"] });
  });
});

describe("polling", () => {
  test("202 then PENDING then APPROVED, honouring Retry-After", async () => {
    const pending = { decisionId: "d1", status: "PENDING", route: "HUMAN_IN_LOOP" };
    const { client, requests } = await setup([
      { status: 202, body: pending, headers: { "retry-after": "0" } },
      { status: 200, body: pending, headers: { "retry-after": "0" } },
      verdict("APPROVED"),
    ]);
    const started = Date.now();
    await expect(client.checkpoint(input)).resolves.toBe(input.result);
    // Without Retry-After the SDK would wait 1 s before the first poll.
    expect(Date.now() - started).toBeLessThan(900);
    expect(requests.map((r) => `${r.method} ${r.url}`)).toEqual([
      "POST /v1/checkpoints",
      "GET /v1/checkpoints/d1",
      "GET /v1/checkpoints/d1",
    ]);
    const getHeaders = Object.keys(requests[1]!.headers).filter((h) => !TRANSPORT_HEADERS.has(h));
    expect(getHeaders.sort()).toEqual(["authorization", "user-agent"]);
  });

  test("waitTimeout throws CheckpointTimeout with the decisionId", async () => {
    const pending = { decisionId: "d7", status: "PENDING" };
    const { client } = await setup(
      [
        { status: 202, body: pending, headers: { "retry-after": "0" } },
        { status: 200, body: pending },
        { status: 200, body: pending },
      ],
      { waitTimeoutMs: 100 },
    );
    const error = await client.checkpoint(input).catch((e: unknown) => e);
    expect(error).toBeInstanceOf(CheckpointTimeout);
    expect((error as CheckpointTimeout).decisionId).toBe("d7");
  });

  test("wait resumes a pending decision", async () => {
    const { client, requests } = await setup([verdict("REJECTED")]);
    await expect(client.wait("d1")).resolves.toMatchObject({ status: "REJECTED" });
    expect(requests[0]!.url).toBe("/v1/checkpoints/d1");
  });
});

describe("polling errors carry the decisionId", () => {
  const accepted: Reply = {
    status: 202,
    body: { decisionId: "dec_42", status: "PENDING" },
    headers: { "retry-after": "0" },
  };
  const failure = async (promise: Promise<unknown>) => {
    const error = await promise.catch((e: unknown) => e);
    expect(error).toBeInstanceOf(ApiError);
    return error as ApiError;
  };

  test("network failures exhausted while polling", async () => {
    const drop = { status: 0, drop: true };
    const { client } = await setup([accepted, drop, drop], { maxRetries: 1 });
    const error = await failure(client.checkpoint(input));
    expect(error).toMatchObject({ status: 0, code: "network_error", decisionId: "dec_42" });
    expect(error.message).toContain("dec_42");
  });

  test("5xx exhausted while polling", async () => {
    const busy = {
      status: 500,
      body: { code: "internal", message: "boom" },
      headers: { "retry-after": "0" },
    };
    const { client } = await setup([accepted, busy, busy], { maxRetries: 1 });
    const error = await failure(client.checkpointVerdict(input));
    expect(error).toMatchObject({ status: 500, code: "internal", decisionId: "dec_42" });
    expect(error.message).toContain("dec_42");
  });

  test("404 while polling", async () => {
    const { client } = await setup([
      accepted,
      { status: 404, body: { code: "not_found", message: "unknown decision" } },
    ]);
    const error = await failure(client.checkpoint(input));
    expect(error).toMatchObject({ status: 404, code: "not_found", decisionId: "dec_42" });
  });

  test("404 in wait", async () => {
    const { client } = await setup([{ status: 404, body: { code: "not_found", message: "gone" } }]);
    const error = await failure(client.wait("dec_9"));
    expect(error).toMatchObject({ status: 404, decisionId: "dec_9" });
    expect(error.message).toContain("dec_9");
  });

  test("errors from the initial POST have no decisionId", async () => {
    const busy = {
      status: 503,
      body: { code: "unavailable", message: "busy" },
      headers: { "retry-after": "0" },
    };
    const { client } = await setup([busy, busy], { maxRetries: 1 });
    const error = await failure(client.checkpoint(input));
    expect(error).toMatchObject({ status: 503, message: "busy" });
    expect(error.decisionId).toBeUndefined();
  });
});

describe("retries", () => {
  test("503 then 200 retries with the same Idempotency-Key", async () => {
    const { client, requests } = await setup([
      {
        status: 503,
        body: { code: "unavailable", message: "busy" },
        headers: { "retry-after": "0" },
      },
      verdict("APPROVED"),
    ]);
    await expect(client.checkpoint(input)).resolves.toBe(input.result);
    expect(requests).toHaveLength(2);
    expect(requests[0]!.headers["idempotency-key"]).toBe(requests[1]!.headers["idempotency-key"]);
    expect(requests[0]!.body).toEqual(requests[1]!.body);
  });

  test("each checkpoint call gets a new Idempotency-Key", async () => {
    const { client, requests } = await setup([verdict("APPROVED"), verdict("APPROVED")]);
    await client.checkpoint(input);
    await client.checkpoint(input);
    expect(requests[0]!.headers["idempotency-key"]).not.toBe(
      requests[1]!.headers["idempotency-key"],
    );
  });

  test("400 throws ApiError without retrying", async () => {
    const { client, requests } = await setup([
      { status: 400, body: { code: "invalid_goal", message: "goal is empty" } },
    ]);
    const error = await client.checkpoint(input).catch((e: unknown) => e);
    expect(error).toBeInstanceOf(ApiError);
    expect(error).toMatchObject({ status: 400, code: "invalid_goal", message: "goal is empty" });
    expect(requests).toHaveLength(1);
  });

  test("exhausted retries throw ApiError", async () => {
    const busy = {
      status: 429,
      body: { code: "rate_limited", message: "slow down" },
      headers: { "retry-after": "0" },
    };
    const { client, requests } = await setup([busy, busy, busy], { maxRetries: 2 });
    await expect(client.checkpoint(input)).rejects.toMatchObject({
      status: 429,
      code: "rate_limited",
    });
    expect(requests).toHaveLength(3);
  });

  test("network errors are retried, then throw ApiError", async () => {
    // Port 9 (discard) on loopback is closed, so every attempt fails to connect.
    const client = new RabbiX({ apiKey: "k", baseUrl: "http://127.0.0.1:9", maxRetries: 1 });
    await expect(client.checkpoint(input)).rejects.toMatchObject({
      status: 0,
      code: "network_error",
    });
  });
});

describe("body size", () => {
  test("bodies over 8 KB are gzipped and decode to the original", async () => {
    const { client, requests } = await setup([verdict("APPROVED")]);
    const big = { goal: "g", result: "x".repeat(20_000) };
    await client.checkpoint(big);
    const req = requests[0]!;
    expect(req.headers["content-encoding"]).toBe("gzip");
    expect(req.body.byteLength).toBeLessThan(20_000);
    expect(JSON.parse(gunzipSync(req.body).toString("utf8"))).toEqual(big);
  });

  test("small bodies are not compressed", async () => {
    const { client, requests } = await setup([verdict("APPROVED")]);
    await client.checkpoint(input);
    expect(requests[0]!.headers["content-encoding"]).toBeUndefined();
  });

  test("bodies over 10 MB throw PayloadTooLarge without any request", async () => {
    const { client, requests } = await setup([verdict("APPROVED")]);
    const error = await client
      .checkpoint({ goal: "g", result: "x".repeat(10_485_760) })
      .catch((e: unknown) => e);
    expect(error).toBeInstanceOf(PayloadTooLarge);
    expect(requests).toHaveLength(0);
  });
});

describe("configuration", () => {
  test("missing API key throws at construction", () => {
    vi.stubEnv("RABBIX_API_KEY", "");
    expect(() => new RabbiX()).toThrow(RabbiXError);
  });

  test("reads RABBIX_API_KEY and RABBIX_BASE_URL", async () => {
    api = await mockApi([verdict("APPROVED")]);
    vi.stubEnv("RABBIX_API_KEY", "env-key");
    vi.stubEnv("RABBIX_BASE_URL", api.baseUrl);
    await new RabbiX().checkpoint(input);
    expect(api.requests[0]!.headers.authorization).toBe("Bearer env-key");
  });
});
