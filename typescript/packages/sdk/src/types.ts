export type VerdictStatus = "APPROVED" | "REJECTED" | "MODIFIED" | "EXPIRED" | "PENDING";

export interface Verdict {
  decisionId: string;
  /** Typed as the documented values; anything else from the server is treated as not approved. */
  status: VerdictStatus;
  route?: "AI_AUTONOMOUS" | "HUMAN_IN_LOOP" | "HUMAN_ONLY";
  /** The edited result to act on instead, when status is MODIFIED. */
  result?: unknown;
  reason?: string | null;
}

export interface AgentInfo {
  name?: string;
  framework?: "openai-agents" | "agent-framework" | "claude-agent-sdk";
}

export interface CheckpointInput<T extends string | object = string | object> {
  /** What the agent was asked to do (instructions and task input). */
  goal: string;
  /** The agent's final result. Sent in full. */
  result: T;
  decisionClass?: string;
  agent?: AgentInfo;
  metadata?: Record<string, string>;
}

export interface RabbiXOptions {
  /** Defaults to `RABBIX_API_KEY`. */
  apiKey?: string;
  /** Defaults to `RABBIX_BASE_URL`, then `https://api.rabbix.invalid`. */
  baseUrl?: string;
  /** How long to wait for a pending verdict. `null` waits forever. Default 15 minutes. */
  waitTimeoutMs?: number | null;
  /** Timeout for each HTTP request. Default 30 seconds. */
  requestTimeoutMs?: number;
  /** Retries on network errors, 429 and 5xx. Default 3. */
  maxRetries?: number;
}

export interface CallOptions {
  signal?: AbortSignal;
}
