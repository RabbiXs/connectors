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
});

test("checkpoints instructions + input and the final output", async () => {
  checkpoint.mockImplementation(async ({ result }) => result);

  await expect(
    runWithCheckpoint(client, agent, "Order 42 arrived broken", {
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

test("resolves function instructions with the run context", async () => {
  checkpoint.mockImplementation(async ({ result }) => result);
  const dynamic = new Agent<{ shop: string }>({
    name: "dyn",
    instructions: (ctx) => `Refunds for ${ctx.context.shop}.`,
  });
  await runWithCheckpoint(client, dynamic, "Order 42", { runOptions: { context: { shop: "A" } } });
  expect(checkpoint.mock.calls[0]![0].goal).toBe("Refunds for A.\n\nOrder 42");
});

test("returns the modified output", async () => {
  checkpoint.mockResolvedValue("Refund 5 EUR");
  await expect(runWithCheckpoint(client, agent, "x")).resolves.toBe("Refund 5 EUR");
});

test("propagates a rejection", async () => {
  const rejected = new CheckpointRejected({ decisionId: "d1", status: "REJECTED" });
  checkpoint.mockRejectedValue(rejected);
  await expect(runWithCheckpoint(client, agent, "x")).rejects.toBe(rejected);
});

test("throws without checkpointing when there is no final output", async () => {
  runMock.mockResolvedValue({ finalOutput: undefined } as never);
  await expect(runWithCheckpoint(client, agent, "x")).rejects.toBeInstanceOf(RabbiXError);
  expect(checkpoint).not.toHaveBeenCalled();
});
