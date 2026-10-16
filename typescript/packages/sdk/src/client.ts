import {
  ApiError,
  CheckpointRejected,
  CheckpointTimeout,
  PayloadTooLarge,
  RabbiXError,
} from "./errors.ts";
import type { CallOptions, CheckpointInput, RabbiXOptions, Verdict } from "./types.ts";

export const VERSION = "0.1.0";
const DEFAULT_BASE_URL = "https://api.rabbix.invalid";
const MAX_BODY_BYTES = 10_485_760;
const GZIP_THRESHOLD_BYTES = 8192;
const MAX_POLL_DELAY_MS = 30_000;

interface ApiResponse {
  status: number;
  json: unknown;
  retryAfterMs: number | undefined;
}

export class RabbiX {
  readonly #apiKey: string;
  readonly #baseUrl: string;
  readonly #waitTimeoutMs: number | null;
  readonly #requestTimeoutMs: number;
  readonly #maxRetries: number;

  constructor(options: RabbiXOptions = {}) {
    const apiKey = options.apiKey ?? process.env.RABBIX_API_KEY;
    if (!apiKey) throw new RabbiXError("Missing API key: pass apiKey or set RABBIX_API_KEY");
    this.#apiKey = apiKey;
    this.#baseUrl = (options.baseUrl ?? process.env.RABBIX_BASE_URL ?? DEFAULT_BASE_URL).replace(
      /\/+$/,
      "",
    );
    this.#waitTimeoutMs = options.waitTimeoutMs === undefined ? 900_000 : options.waitTimeoutMs;
    this.#requestTimeoutMs = options.requestTimeoutMs ?? 30_000;
    this.#maxRetries = options.maxRetries ?? 3;
  }

  /**
   * Submits the run and waits for a final verdict. Returns the result to act on:
   * the original `result` if APPROVED, the edited one if MODIFIED. Throws
   * `CheckpointRejected` for every other status.
   */
  async checkpoint<T extends string | object>(
    input: CheckpointInput<T>,
    options?: CallOptions,
  ): Promise<T> {
    const verdict = await this.checkpointVerdict(input, options);
    if (verdict.status === "APPROVED") return input.result;
    if (verdict.status === "MODIFIED") {
      if (verdict.result === undefined || verdict.result === null) {
        throw new ApiError(200, "invalid_response", "MODIFIED verdict without a result");
      }
      return verdict.result as T;
    }
    throw new CheckpointRejected(verdict);
  }

  /** Like `checkpoint`, but returns the final verdict instead of throwing on non-approval. */
  async checkpointVerdict(input: CheckpointInput, options: CallOptions = {}): Promise<Verdict> {
    const body = new TextEncoder().encode(JSON.stringify(requestBody(input)));
    if (body.byteLength > MAX_BODY_BYTES)
      throw new PayloadTooLarge(body.byteLength, MAX_BODY_BYTES);

    const headers: Record<string, string> = {
      "Content-Type": "application/json",
      // Generated once per call so retries are deduplicated server-side.
      "Idempotency-Key": crypto.randomUUID(),
    };
    let payload: Uint8Array<ArrayBuffer> = body;
    if (body.byteLength > GZIP_THRESHOLD_BYTES) {
      payload = await gzip(body);
      headers["Content-Encoding"] = "gzip";
    }

    const res = await this.#request("/v1/checkpoints", options.signal, {
      method: "POST",
      headers,
      body: payload,
    });
    const verdict = parseVerdict(res.json);
    if (res.status !== 202 && verdict.status !== "PENDING") return verdict;
    return this.#poll(verdict.decisionId, res.retryAfterMs ?? 1000, options.signal);
  }

  /** Waits for the final verdict of a decision that is (or was) pending. */
  wait(decisionId: string, options: CallOptions = {}): Promise<Verdict> {
    return this.#poll(decisionId, 0, options.signal);
  }

  async #poll(decisionId: string, firstDelayMs: number, signal?: AbortSignal): Promise<Verdict> {
    const deadline = this.#waitTimeoutMs === null ? Infinity : Date.now() + this.#waitTimeoutMs;
    let delay = firstDelayMs;
    let backoff = 1000;
    for (;;) {
      const remaining = deadline - Date.now();
      if (remaining <= 0) throw new CheckpointTimeout(decisionId);
      await sleep(Math.min(delay, remaining), signal);

      let res: ApiResponse;
      let verdict: Verdict;
      try {
        res = await this.#request(`/v1/checkpoints/${encodeURIComponent(decisionId)}`, signal);
        verdict = parseVerdict(res.json);
      } catch (error) {
        if (!(error instanceof ApiError)) throw error;
        // Re-thrown with the decisionId so the host can resume instead of resubmitting.
        throw new ApiError(error.status, error.code, error.message, {
          cause: error.cause,
          decisionId,
        });
      }
      if (verdict.status !== "PENDING") return verdict;
      if (res.retryAfterMs !== undefined) {
        delay = res.retryAfterMs;
      } else {
        delay = backoff;
        backoff = Math.min(backoff * 2, MAX_POLL_DELAY_MS);
      }
    }
  }

  /** GET unless `init.method` is POST. Retries network errors, 429 and 5xx. */
  async #request(
    path: string,
    signal: AbortSignal | undefined,
    init: {
      method?: "POST";
      headers?: Record<string, string>;
      body?: Uint8Array<ArrayBuffer>;
    } = {},
  ): Promise<ApiResponse> {
    for (let attempt = 0; ; attempt++) {
      const timeout = AbortSignal.timeout(this.#requestTimeoutMs);
      let res: Response;
      try {
        res = await fetch(this.#baseUrl + path, {
          ...init,
          headers: {
            ...init.headers,
            Authorization: `Bearer ${this.#apiKey}`,
            "User-Agent": `rabbix-typescript/${VERSION}`,
          },
          signal: signal ? AbortSignal.any([signal, timeout]) : timeout,
        });
      } catch (error) {
        if (signal?.aborted) throw signal.reason;
        if (attempt >= this.#maxRetries) {
          const message = `${init.method ?? "GET"} ${path} failed: ${String(error)}`;
          throw new ApiError(0, "network_error", message, { cause: error });
        }
        await sleep(retryBackoff(attempt), signal);
        continue;
      }

      const retryAfterMs = parseRetryAfter(res.headers.get("retry-after"));
      const retryable = res.status === 429 || res.status >= 500;
      if (retryable && attempt < this.#maxRetries) {
        await res.body?.cancel();
        await sleep(retryAfterMs ?? retryBackoff(attempt), signal);
        continue;
      }

      const json: unknown = await res.json().catch(() => undefined);
      if (!res.ok) throw toApiError(res.status, json);
      return { status: res.status, json, retryAfterMs };
    }
  }
}

function requestBody(input: CheckpointInput): Record<string, unknown> {
  // Built field by field so nothing beyond the documented fields is ever sent.
  const body: Record<string, unknown> = { goal: input.goal, result: input.result };
  if (input.decisionClass !== undefined) body.decisionClass = input.decisionClass;
  if (input.agent !== undefined) {
    const agent: Record<string, string> = {};
    if (input.agent.name !== undefined) agent.name = input.agent.name;
    if (input.agent.framework !== undefined) agent.framework = input.agent.framework;
    body.agent = agent;
  }
  if (input.metadata !== undefined) body.metadata = input.metadata;
  return body;
}

async function gzip(data: Uint8Array<ArrayBuffer>): Promise<Uint8Array<ArrayBuffer>> {
  const stream = new Blob([data]).stream().pipeThrough(new CompressionStream("gzip"));
  return new Uint8Array(await new Response(stream).arrayBuffer());
}

function parseVerdict(json: unknown): Verdict {
  if (
    typeof json === "object" &&
    json !== null &&
    "decisionId" in json &&
    typeof json.decisionId === "string" &&
    "status" in json &&
    typeof json.status === "string"
  ) {
    return json as Verdict;
  }
  throw new ApiError(200, "invalid_response", "Response is not a verdict");
}

function toApiError(status: number, json: unknown): ApiError {
  const err = (typeof json === "object" && json !== null ? json : {}) as Record<string, unknown>;
  const code = typeof err.code === "string" ? err.code : `http_${status}`;
  const message = typeof err.message === "string" ? err.message : `HTTP ${status}`;
  return new ApiError(status, code, message);
}

function parseRetryAfter(value: string | null): number | undefined {
  if (value === null) return undefined;
  const seconds = Number(value);
  return Number.isFinite(seconds) && seconds >= 0 ? seconds * 1000 : undefined;
}

/** 0.5 s, 1 s, 2 s, ... with ±20 % jitter. */
function retryBackoff(attempt: number): number {
  return 500 * 2 ** attempt * (0.8 + Math.random() * 0.4);
}

function sleep(ms: number, signal?: AbortSignal): Promise<void> {
  return new Promise((resolve, reject) => {
    if (signal?.aborted) return reject(signal.reason);
    const onAbort = () => {
      clearTimeout(timer);
      reject(signal?.reason);
    };
    const timer = setTimeout(() => {
      signal?.removeEventListener("abort", onAbort);
      resolve();
    }, ms);
    signal?.addEventListener("abort", onAbort, { once: true });
  });
}
