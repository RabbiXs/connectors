import type { Verdict } from "./types.ts";

export class RabbiXError extends Error {
  override name = "RabbiXError";
}

/** The verdict does not allow acting on the result (REJECTED, EXPIRED or an unknown status). */
export class CheckpointRejected extends RabbiXError {
  override name = "CheckpointRejected";
  readonly verdict: Verdict;

  constructor(verdict: Verdict) {
    super(
      `Checkpoint ${verdict.decisionId} returned ${verdict.status}` +
        (verdict.reason ? `: ${verdict.reason}` : ""),
    );
    this.verdict = verdict;
  }
}

/** No final verdict before `waitTimeoutMs`. Resume with `client.wait(decisionId)`. */
export class CheckpointTimeout extends RabbiXError {
  override name = "CheckpointTimeout";
  readonly decisionId: string;

  constructor(decisionId: string) {
    super(`Checkpoint ${decisionId} is still pending; resume with wait(decisionId)`);
    this.decisionId = decisionId;
  }
}

/** The uncompressed request body exceeds 10 MB. Nothing was sent. */
export class PayloadTooLarge extends RabbiXError {
  override name = "PayloadTooLarge";
  readonly size: number;

  constructor(size: number, limit: number) {
    super(`Checkpoint body is ${size} bytes; the limit is ${limit} bytes`);
    this.size = size;
  }
}

/**
 * The API answered with a non-retryable error, retries were exhausted, or the
 * response was unusable. `status` is 0 when no HTTP response was received.
 */
export class ApiError extends RabbiXError {
  override name = "ApiError";
  readonly status: number;
  readonly code: string;

  constructor(status: number, code: string, message: string, options?: ErrorOptions) {
    super(message, options);
    this.status = status;
    this.code = code;
  }
}
