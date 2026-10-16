import { createServer, type IncomingHttpHeaders } from "node:http";
import type { AddressInfo } from "node:net";

export interface RecordedRequest {
  method: string;
  url: string;
  headers: IncomingHttpHeaders;
  body: Buffer;
}

export interface Reply {
  status: number;
  body?: unknown;
  headers?: Record<string, string>;
  /** Drop the connection instead of answering (a network error for the client). */
  drop?: boolean;
}

/** Local HTTP server that answers with `replies` in order and records every request. */
export async function mockApi(replies: Reply[]) {
  const requests: RecordedRequest[] = [];
  const server = createServer(async (req, res) => {
    const chunks: Buffer[] = [];
    for await (const chunk of req) chunks.push(chunk as Buffer);
    requests.push({
      method: req.method ?? "",
      url: req.url ?? "",
      headers: req.headers,
      body: Buffer.concat(chunks),
    });
    const reply = replies.shift() ?? { status: 500, body: { code: "test", message: "no reply" } };
    if (reply.drop) {
      req.socket.destroy();
      return;
    }
    res.writeHead(reply.status, { "content-type": "application/json", ...reply.headers });
    res.end(reply.body === undefined ? "" : JSON.stringify(reply.body));
  });
  await new Promise<void>((resolve) => server.listen(0, "127.0.0.1", resolve));
  const { port } = server.address() as AddressInfo;
  return {
    baseUrl: `http://127.0.0.1:${port}`,
    requests,
    close: () => new Promise<void>((resolve) => server.close(() => resolve())),
  };
}
