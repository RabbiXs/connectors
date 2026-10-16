import { query, type SDKMessage } from "@anthropic-ai/claude-agent-sdk";
import { CheckpointRejected, RabbiXError, type RabbiX } from "@rabbix/sdk";
import { beforeEach, expect, test, vi } from "vite-plus/test";
import { queryWithCheckpoint } from "../src/index.ts";

vi.mock("@anthropic-ai/claude-agent-sdk", () => ({ query: vi.fn() }));

const queryMock = vi.mocked(query);
const checkpoint = vi.fn();
const client = { checkpoint } as unknown as RabbiX;

function respondWith(...messages: Partial<SDKMessage>[]) {
  queryMock.mockImplementation(async function* () {
    yield* messages;
  } as never);
}

const success = { type: "result", subtype: "success", is_error: false, result: "Refund 10 EUR" };

beforeEach(() => {
  vi.clearAllMocks();
  checkpoint.mockImplementation(async ({ result }) => result);
});

test("checkpoints system prompt + prompt and the final result", async () => {
  respondWith({ type: "assistant" } as never, success as never);
  const options = { systemPrompt: "Decide refunds." };

  await expect(
    queryWithCheckpoint(client, {
      prompt: "Order 42 arrived broken",
      options,
      name: "refunds",
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

test("uses structured output, preset append and the selected agent", async () => {
  respondWith({ ...success, structured_output: { refund: 10 } } as never);
  await queryWithCheckpoint(client, {
    prompt: "Order 42",
    options: {
      systemPrompt: { type: "preset", preset: "claude_code", append: "Be strict." },
      agent: "refunds",
      agents: { refunds: { description: "Refund agent", prompt: "Decide refunds." } },
    },
  });
  expect(checkpoint.mock.calls[0]![0]).toMatchObject({
    goal: "Be strict.\n\nDecide refunds.\n\nOrder 42",
    result: { refund: 10 },
    agent: { name: "refunds", framework: "claude-agent-sdk" },
  });
});

test("returns the modified result", async () => {
  respondWith(success as never);
  checkpoint.mockResolvedValue("Refund 5 EUR");
  await expect(queryWithCheckpoint(client, { prompt: "x" })).resolves.toBe("Refund 5 EUR");
});

test("propagates a rejection", async () => {
  respondWith(success as never);
  const rejected = new CheckpointRejected({ decisionId: "d1", status: "REJECTED" });
  checkpoint.mockRejectedValue(rejected);
  await expect(queryWithCheckpoint(client, { prompt: "x" })).rejects.toBe(rejected);
});

test.each([
  ["no result message", [{ type: "assistant" }]],
  ["an error result", [{ type: "result", subtype: "error_max_turns", is_error: true }]],
])("throws without checkpointing on %s", async (_, messages) => {
  respondWith(...(messages as never[]));
  await expect(queryWithCheckpoint(client, { prompt: "x" })).rejects.toBeInstanceOf(RabbiXError);
  expect(checkpoint).not.toHaveBeenCalled();
});
