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
 * `decisionId` is set when the error happened while polling a pending decision:
 * resume with `client.wait(decisionId)` instead of resubmitting.
 */
export class ApiError extends RabbiXError {
  override name = "ApiError";
  readonly status: number;
  readonly code: string;
  readonly decisionId: string | undefined;

  constructor(
    status: number,
    code: string,
    message: string,
    options?: ErrorOptions & { decisionId?: string },
  ) {
    super(
      options?.decisionId === undefined
        ? message
        : `${message} (decision ${options.decisionId}; resume with wait(decisionId))`,
      options,
    );
    this.status = status;
    this.code = code;
    this.decisionId = options?.decisionId;
  }
}
