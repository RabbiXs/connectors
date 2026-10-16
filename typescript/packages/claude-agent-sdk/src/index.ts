import {
  query,
  SYSTEM_PROMPT_DYNAMIC_BOUNDARY,
  type Options,
  type SDKResultMessage,
} from "@anthropic-ai/claude-agent-sdk";
import { RabbiXError, type RabbiX } from "@rabbix/sdk";

export interface QueryWithCheckpointParams {
  /** The task. Only string prompts are supported, since the prompt is part of the goal. */
  prompt: string;
  /** Passed through to `query()`. */
  options?: Options;
  client: RabbiX;
  /** Sent as `agent.name`. Defaults to `options.agent`. */
  agentName?: string;
  decisionClass?: string;
  metadata?: Record<string, string>;
}

/**
 * Runs `query({ prompt, options })` to completion, submits the system prompt,
 * the selected subagent's prompt and the prompt as the goal and the final
 * result message as the result, and returns the result to act on:
 * `structured_output` when `outputFormat` is set, else the result text. `T` is
 * not validated. Throws unless approved.
 */
export async function queryWithCheckpoint<T extends string | object = string>(
  params: QueryWithCheckpointParams,
): Promise<T> {
  const { prompt, options, client } = params;
  // Checked before running so an agent whose run can never be approved is not started.
  const goal = joinParts([...instructionParts(options), prompt]);
  if (!goal) throw new RabbiXError("Cannot checkpoint: the goal (instructions + prompt) is empty");

  let final: SDKResultMessage | undefined;
  for await (const message of query({ prompt, options })) {
    if (message.type === "result") final = message;
  }
  if (!final) throw new RabbiXError("Claude agent finished without a result message");
  if (final.subtype !== "success" || final.is_error) {
    throw new RabbiXError(`Claude agent run failed: ${final.subtype}`);
  }

  return client.checkpoint({
    goal,
    result: (final.structured_output ?? final.result) as T,
    decisionClass: params.decisionClass,
    agent: { name: params.agentName ?? options?.agent, framework: "claude-agent-sdk" },
    metadata: params.metadata,
  });
}

/** System prompt (string, text parts of an array, custom prompt, or a preset's `append`), then the selected subagent's prompt. */
function instructionParts(options: Options | undefined): string[] {
  const parts: string[] = [];
  const sp = options?.systemPrompt;
  if (typeof sp === "string") parts.push(sp);
  else if (Array.isArray(sp)) parts.push(textOf(sp));
  else if (sp?.type === "custom") parts.push(textOf([sp.prompt].flat()));
  else if (sp?.type === "preset" && sp.append) parts.push(sp.append);
  if (options?.agent) parts.push(options.agents?.[options.agent]?.prompt ?? "");
  return parts;
}

/** The text parts of an array system prompt; the cache-boundary marker is not text. */
function textOf(prompt: string[]): string {
  return joinParts(prompt.filter((p) => p !== SYSTEM_PROMPT_DYNAMIC_BOUNDARY));
}

/** Non-empty parts joined with a blank line, verbatim. */
function joinParts(parts: string[]): string {
  return parts.filter((p) => p !== "").join("\n\n");
}
