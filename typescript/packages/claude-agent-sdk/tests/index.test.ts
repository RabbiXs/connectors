import { query, type Options, type SDKMessage } from "@anthropic-ai/claude-agent-sdk";
import { CheckpointRejected, RabbiXError, type RabbiX } from "@rabbix/sdk";
import { beforeEach, expect, test, vi } from "vite-plus/test";
import { queryWithCheckpoint } from "../src/index.ts";

vi.mock("@anthropic-ai/claude-agent-sdk", () => ({
  query: vi.fn(),
  SYSTEM_PROMPT_DYNAMIC_BOUNDARY: "__SYSTEM_PROMPT_DYNAMIC_BOUNDARY__",
}));

const queryMock = vi.mocked(query);
const checkpoint = vi.fn();
const client = { checkpoint } as unknown as RabbiX;

function respondWith(...messages: Partial<SDKMessage>[]) {
  queryMock.mockImplementation(async function* () {
    yield* messages;
  } as never);
}

const success = { type: "result", subtype: "success", is_error: false, result: "Refund 10 EUR" };
const sentGoal = () => checkpoint.mock.calls[0]![0].goal as string;

beforeEach(() => {
  vi.clearAllMocks();
  checkpoint.mockImplementation(async ({ result }) => result);
  respondWith(success as never);
});

test("checkpoints system prompt + prompt and the final result", async () => {
  respondWith({ type: "assistant" } as never, success as never);
  const options = { systemPrompt: "Decide refunds." };

  await expect(
    queryWithCheckpoint({
      prompt: "Order 42 arrived broken",
      options,
      client,
      agentName: "refunds",
      decisionClass: "refund",
      metadata: { tenant: "t1" },
    }),
  ).resolves.toBe("Refund 10 EUR");

  expect(queryMock).toHaveBeenCalledWith({ prompt: "Order 42 arrived broken", options });
  expect(checkpoint).toHaveBeenCalledWith({
    goal: "Decide refunds.\n\nOrder 42 arrived broken",
    result: "Refund 10 EUR",
    decisionClass: "refund",
    agent: { name: "refunds", framework: "claude-agent-sdk" },
    metadata: { tenant: "t1" },
  });
});

const support = {
  agent: "support",
  agents: { support: { description: "Support agent", prompt: "Handle refunds." } },
};

test.each<[string, Options | undefined, string]>([
  ["system prompt", { systemPrompt: "Be careful." }, "Be careful.\n\nRefund order 7"],
  ["no system prompt", undefined, "Refund order 7"],
  [
    "system prompt + selected subagent",
    { systemPrompt: "Be careful.", ...support },
    "Be careful.\n\nHandle refunds.\n\nRefund order 7",
  ],
  [
    "array system prompt without the cache boundary",
    { systemPrompt: ["Be careful.", "__SYSTEM_PROMPT_DYNAMIC_BOUNDARY__", "Session A."] },
    "Be careful.\n\nSession A.\n\nRefund order 7",
  ],
  [
    "custom prompt",
    { systemPrompt: { type: "custom", prompt: "Be careful." } },
    "Be careful.\n\nRefund order 7",
  ],
  [
    "preset uses only its append",
    { systemPrompt: { type: "preset", preset: "claude_code", append: "Be careful." }, ...support },
    "Be careful.\n\nHandle refunds.\n\nRefund order 7",
  ],
  [
    "preset without append",
    { systemPrompt: { type: "preset", preset: "claude_code" } },
    "Refund order 7",
  ],
])("goal: %s", async (_, options, goal) => {
  await queryWithCheckpoint({ prompt: "Refund order 7", options, client });
  expect(sentGoal()).toBe(goal);
});

test("empty system prompt and empty prompt throw before anything runs or is sent", async () => {
  await expect(
    queryWithCheckpoint({ prompt: "", options: { systemPrompt: "" }, client }),
  ).rejects.toBeInstanceOf(RabbiXError);
  expect(queryMock).not.toHaveBeenCalled();
  expect(checkpoint).not.toHaveBeenCalled();
});

test("uses structured output and defaults agentName to options.agent", async () => {
  respondWith({ ...success, structured_output: { refund: 10 } } as never);
  await queryWithCheckpoint({ prompt: "Order 42", options: support, client });
  expect(checkpoint.mock.calls[0]![0]).toMatchObject({
    result: { refund: 10 },
    agent: { name: "support", framework: "claude-agent-sdk" },
  });
});

test("returns the modified result", async () => {
  checkpoint.mockResolvedValue("Refund 5 EUR");
  await expect(queryWithCheckpoint({ prompt: "x", client })).resolves.toBe("Refund 5 EUR");
});

test("propagates a rejection", async () => {
  const rejected = new CheckpointRejected({ decisionId: "d1", status: "REJECTED" });
  checkpoint.mockRejectedValue(rejected);
  await expect(queryWithCheckpoint({ prompt: "x", client })).rejects.toBe(rejected);
});

test.each([
  ["no result message", [{ type: "assistant" }]],
  ["an error result", [{ type: "result", subtype: "error_max_turns", is_error: true }]],
])("throws without checkpointing on %s", async (_, messages) => {
  respondWith(...(messages as never[]));
  await expect(queryWithCheckpoint({ prompt: "x", client })).rejects.toBeInstanceOf(RabbiXError);
  expect(checkpoint).not.toHaveBeenCalled();
});
