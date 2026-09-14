import assert from "node:assert/strict";
import {
  ApiRequestError,
  requestJson,
  requestNoContent
} from "../src/api/httpTransport.ts";

const originalFetch = globalThis.fetch;

try {
  {
    let transportSignal: AbortSignal | null = null;
    globalThis.fetch = ((_input: RequestInfo | URL, init?: RequestInit) => {
      transportSignal = init?.signal ?? null;
      return new Promise<Response>(() => undefined);
    }) as typeof fetch;

    await assert.rejects(
      requestJson("/api/hung", { fallbackError: "读取失败", timeoutMs: 20 }),
      (error: unknown) => error instanceof ApiRequestError
        && error.kind === "timeout"
        && error.retryable);
    assert.equal(transportSignal?.aborted, true);
  }

  {
    globalThis.fetch = ((_input: RequestInfo | URL, _init?: RequestInit) =>
      new Promise<Response>(() => undefined)) as typeof fetch;
    const controller = new AbortController();
    const request = requestJson("/api/cancel", {
      fallbackError: "读取失败",
      signal: controller.signal,
      timeoutMs: 500
    });
    controller.abort();
    await assert.rejects(
      request,
      (error: unknown) => error instanceof ApiRequestError
        && error.kind === "aborted"
        && !error.retryable);
  }

  {
    globalThis.fetch = (async () => new Response(
      new ReadableStream<Uint8Array>({ start: () => undefined }),
      { status: 200, headers: { "Content-Type": "application/json" } })) as typeof fetch;
    await assert.rejects(
      requestJson("/api/hung-body", { fallbackError: "读取失败", timeoutMs: 20 }),
      (error: unknown) => error instanceof ApiRequestError && error.kind === "timeout");
  }

  {
    globalThis.fetch = (async () => new Response("<html>broken</html>", {
      status: 200,
      headers: { "Content-Type": "text/html" }
    })) as typeof fetch;
    await assert.rejects(
      requestJson("/api/invalid", { fallbackError: "读取失败", timeoutMs: 100 }),
      (error: unknown) => error instanceof ApiRequestError
        && error.kind === "invalid-response"
        && error.status === 200);
  }

  {
    globalThis.fetch = (async () => new Response("temporarily unavailable", {
      status: 503,
      headers: { "Content-Type": "text/plain" }
    })) as typeof fetch;
    await assert.rejects(
      requestJson("/api/unavailable", { fallbackError: "保存设置失败", timeoutMs: 100 }),
      (error: unknown) => error instanceof ApiRequestError
        && error.kind === "http"
        && error.status === 503
        && error.retryable
        && error.message.includes("保存设置失败"));
  }

  {
    globalThis.fetch = (async () => new Response(JSON.stringify({ ready: true }), {
      status: 200,
      headers: { "Content-Type": "application/json" }
    })) as typeof fetch;
    assert.deepEqual(
      await requestJson<{ ready: boolean }>("/api/ready", { fallbackError: "读取失败" }),
      { ready: true });
  }

  {
    globalThis.fetch = (async () => new Response(null, { status: 204 })) as typeof fetch;
    await requestNoContent("/api/remove", { method: "DELETE", fallbackError: "删除失败" });
  }
} finally {
  globalThis.fetch = originalFetch;
}
