import {
  run,
  RunContext,
  type Agent,
  type AgentInputItem,
  type NonStreamRunOptions,
  type RunResult,
} from "@openai/agents";
import { RabbiXError, type RabbiX } from "@rabbix/sdk";

export interface RunWithCheckpointOptions<TContext, TAgent extends Agent<any, any>> {
  decisionClass?: string;
  metadata?: Record<string, string>;
  /** Passed through to `run()`. */
  runOptions?: NonStreamRunOptions<TContext, TAgent>;
}

type FinalOutput<TAgent extends Agent<any, any>> = NonNullable<
  RunResult<unknown, TAgent>["finalOutput"]
>;

/**
 * Runs the agent with `run(agent, input)`, submits its instructions + input as
 * the goal and `finalOutput` as the result, and returns the output to act on.
 * Throws `CheckpointRejected` (or another RabbiX error) unless approved.
 */
export async function runWithCheckpoint<TAgent extends Agent<any, any>, TContext = undefined>(
  client: RabbiX,
  agent: TAgent,
  input: string | AgentInputItem[],
  options: RunWithCheckpointOptions<TContext, TAgent> = {},
): Promise<FinalOutput<TAgent>> {
  const result = await run(agent, input, options.runOptions);
  const output = result.finalOutput as FinalOutput<TAgent> | undefined;
  if (output === undefined || output === null) {
    throw new RabbiXError(`Agent "${agent.name}" finished without a final output`);
  }

  // Resolves function-style instructions the same way the SDK does.
  const instructions = await agent.getSystemPrompt(new RunContext(options.runOptions?.context));
  const task = typeof input === "string" ? input : JSON.stringify(input);
  const goal = instructions ? `${instructions}\n\n${task}` : task;

  return client.checkpoint({
    goal,
    result: output,
    decisionClass: options.decisionClass,
    agent: { name: agent.name, framework: "openai-agents" },
    metadata: options.metadata,
  });
}
