import { Agent, run } from "@openai/agents";
import { CheckpointRejected, RabbiXError, type RabbiX } from "@rabbix/sdk";
import { beforeEach, expect, test, vi } from "vite-plus/test";
import { runWithCheckpoint } from "../src/index.ts";

vi.mock("@openai/agents", async (importOriginal) => ({
  ...(await importOriginal<typeof import("@openai/agents")>()),
  run: vi.fn(),
}));

const runMock = vi.mocked(run);
const checkpoint = vi.fn();
const client = { checkpoint } as unknown as RabbiX;
const agent = new Agent({ name: "refunds", instructions: "Decide refunds." });

beforeEach(() => {
  vi.clearAllMocks();
  runMock.mockResolvedValue({ finalOutput: "Refund 10 EUR" } as never);
  checkpoint.mockImplementation(async ({ result }) => result);
});

const sentGoal = () => checkpoint.mock.calls[0]![0].goal as string;

test("checkpoints instructions + input and the final output", async () => {
  await expect(
    runWithCheckpoint(agent, "Order 42 arrived broken", {
      client,
      decisionClass: "refund",
      metadata: { tenant: "t1" },
    }),
  ).resolves.toBe("Refund 10 EUR");

  expect(runMock).toHaveBeenCalledWith(agent, "Order 42 arrived broken", undefined);
  expect(checkpoint).toHaveBeenCalledWith({
    goal: "Decide refunds.\n\nOrder 42 arrived broken",
    result: "Refund 10 EUR",
    decisionClass: "refund",
    agent: { name: "refunds", framework: "openai-agents" },
    metadata: { tenant: "t1" },
  });
});

test.each([
  ["instructions + string input", "Be careful.", "Refund order 7", "Be careful.\n\nRefund order 7"],
  ["no instructions", undefined, "Refund order 7", "Refund order 7"],
  ["empty instructions", "", "Refund order 7", "Refund order 7"],
  [
    "input items as compact JSON",
    "Be careful.",
    [{ role: "user", content: "Refund order 7" }],
    'Be careful.\n\n[{"role":"user","content":"Refund order 7"}]',
  ],
  ["no trimming", "  Be careful.\n", " Refund order 7 ", "  Be careful.\n\n\n Refund order 7 "],
])("goal: %s", async (_, instructions, input, goal) => {
  const a = new Agent({ name: "a", instructions });
  await runWithCheckpoint(a, input as never, { client });
  expect(sentGoal()).toBe(goal);
});

test("empty instructions and empty input throw before anything runs or is sent", async () => {
  const a = new Agent({ name: "a", instructions: "" });
  await expect(runWithCheckpoint(a, "", { client })).rejects.toBeInstanceOf(RabbiXError);
  expect(runMock).not.toHaveBeenCalled();
  expect(checkpoint).not.toHaveBeenCalled();
});

test("resolves function instructions with the run context", async () => {
  const dynamic = new Agent<{ shop: string }>({
    name: "dyn",
    instructions: (ctx) => `Refunds for ${ctx.context.shop}.`,
  });
  await runWithCheckpoint(dynamic, "Order 42", { client, runOptions: { context: { shop: "A" } } });
  expect(sentGoal()).toBe("Refunds for A.\n\nOrder 42");
});

test("returns the modified output", async () => {
  checkpoint.mockResolvedValue("Refund 5 EUR");
  await expect(runWithCheckpoint(agent, "x", { client })).resolves.toBe("Refund 5 EUR");
});

test("propagates a rejection", async () => {
  const rejected = new CheckpointRejected({ decisionId: "d1", status: "REJECTED" });
  checkpoint.mockRejectedValue(rejected);
  await expect(runWithCheckpoint(agent, "x", { client })).rejects.toBe(rejected);
});

test("throws without checkpointing when there is no final output", async () => {
  runMock.mockResolvedValue({ finalOutput: undefined } as never);
  await expect(runWithCheckpoint(agent, "x", { client })).rejects.toBeInstanceOf(RabbiXError);
  expect(checkpoint).not.toHaveBeenCalled();
});
