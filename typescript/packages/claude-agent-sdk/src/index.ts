import { query, type Options, type SDKResultMessage } from "@anthropic-ai/claude-agent-sdk";
import { RabbiXError, type RabbiX } from "@rabbix/sdk";

export interface QueryWithCheckpointParams {
  /** The task. Only string prompts are supported, since the prompt is part of the goal. */
  prompt: string;
  /** Passed through to `query()`. */
  options?: Options;
  /** Sent as `agent.name`. Defaults to `options.agent`. */
  name?: string;
  decisionClass?: string;
  metadata?: Record<string, string>;
}

/**
 * Runs `query({ prompt, options })` to completion, submits the system prompt +
 * prompt as the goal and the final result message as the result, and returns
 * the result to act on: `structured_output` when `outputFormat` is set, else
 * the result text. `T` is not validated. Throws unless approved.
 */
export async function queryWithCheckpoint<T extends string | object = string>(
  client: RabbiX,
  params: QueryWithCheckpointParams,
): Promise<T> {
  let final: SDKResultMessage | undefined;
  for await (const message of query({ prompt: params.prompt, options: params.options })) {
    if (message.type === "result") final = message;
  }
  if (!final) throw new RabbiXError("Claude agent finished without a result message");
  if (final.subtype !== "success" || final.is_error) {
    throw new RabbiXError(`Claude agent run failed: ${final.subtype}`);
  }

  const instructions = systemPromptText(params.options);
  return client.checkpoint({
    goal: instructions ? `${instructions}\n\n${params.prompt}` : params.prompt,
    result: (final.structured_output ?? final.result) as T,
    decisionClass: params.decisionClass,
    agent: { name: params.name ?? params.options?.agent, framework: "claude-agent-sdk" },
    metadata: params.metadata,
  });
}

/** The integrator-supplied instructions: custom system prompt, preset append, and the selected agent's prompt. */
function systemPromptText(options: Options | undefined): string {
  const parts: string[] = [];
  const sp = options?.systemPrompt;
  if (typeof sp === "string") parts.push(sp);
  else if (Array.isArray(sp)) parts.push(...sp);
  else if (sp?.type === "custom") parts.push(...[sp.prompt].flat());
  else if (sp?.type === "preset" && sp.append) parts.push(sp.append);
  const agentPrompt = options?.agent ? options.agents?.[options.agent]?.prompt : undefined;
  if (agentPrompt) parts.push(agentPrompt);
  return parts.join("\n\n");
}
