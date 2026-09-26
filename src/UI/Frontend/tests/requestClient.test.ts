import assert from "node:assert/strict";
import { ApiRequestError } from "../src/api/httpTransport.ts";
import { RequestClient } from "../src/frontendRuntime/request/RequestClient.ts";
import { RequestProblem } from "../src/frontendRuntime/request/RequestProblem.ts";
import {
  defineResponseDecoder,
  requireBoolean,
  requireRecord
} from "../src/frontendRuntime/request/ResponseDecoder.ts";
import { BackendSessionOwner } from "../src/frontendRuntime/session/BackendSessionOwner.ts";
import type { HostMessageTransport } from "../src/host/hostMessageTransport.ts";

function readyOwner() {
  return new BackendSessionOwner({
    transport: null,
    allowStandalone: true,
    standaloneEpochFactory: () => "standalone:test"
  });
}

const readyDecoder = defineResponseDecoder("test.ready", (value: unknown) => {
  const record = requireRecord(value);
  return { ready: requireBoolean(record.ready, "$.ready") };
});

{
  const owner = readyOwner();
  let clock = 10;
  const observations: Array<{ outcome: string; durationMs: number }> = [];
  const client = new RequestClient(owner, {
    transport: async () => ({ ready: true }),
    monotonicNow: () => ++clock,
    attemptIdFactory: (sequence) => `attempt-${sequence}`,
    onAttemptSettled: ({ outcome, attempt }) => observations.push({
      outcome,
      durationMs: attempt.durationMs
    })
  });
  const outcome = await client.execute({
    key: "runtime.capabilities",
    url: "/api/runtime/capabilities",
    fallbackError: "读取失败",
    decoder: readyDecoder
  });
  assert.deepEqual(outcome.value, { ready: true });
  assert.equal(outcome.attempt.id, "attempt-1");
  assert.equal(outcome.attempt.sequence, 1);
  assert.equal(outcome.attempt.decoderId, "test.ready");
  assert.equal(outcome.attempt.method, "GET");
  assert.equal(outcome.attempt.backendEpoch, "standalone:test");
  assert.ok(outcome.attempt.durationMs >= 0);
  assert.deepEqual(observations, [{
    outcome: "success",
    durationMs: outcome.attempt.durationMs
  }]);
  owner.dispose();
}

{
  const owner = readyOwner();
  const outcomes: string[] = [];
  const client = new RequestClient(owner, {
    transport: async () => ({ ready: "yes" }),
    onAttemptSettled: ({ outcome }) => outcomes.push(outcome)
  });
  await assert.rejects(
    client.request({
      key: "runtime.capabilities",
      url: "/api/runtime/capabilities",
      fallbackError: "读取失败",
      decoder: readyDecoder
    }),
    (error: unknown) => error instanceof RequestProblem
      && error.kind === "invalid-response"
      && error.attempt.backendEpoch === "standalone:test");
  assert.deepEqual(outcomes, ["invalid-response"]);
  owner.dispose();
}

{
  const owner = readyOwner();
  const client = new RequestClient(owner, {
    transport: async () => ({ ready: true }),
    onAttemptSettled: () => { throw new Error("diagnostic observer failed"); }
  });
  assert.deepEqual(await client.request({
    key: "test.observer-isolation",
    url: "/api/test",
    fallbackError: "读取失败",
    decoder: readyDecoder
  }), { ready: true });
  owner.dispose();
}

{
  const owner = readyOwner();
  const client = new RequestClient(owner, {
    transport: async () => {
      throw new ApiRequestError("服务忙。", {
        kind: "http",
        status: 503,
        payload: { error: "busy" },
        retryable: true
      });
    }
  });
  await assert.rejects(
    client.request({
      key: "test.http",
      url: "/api/test",
      fallbackError: "读取失败",
      decoder: readyDecoder
    }),
    (error: unknown) => error instanceof RequestProblem
      && error.kind === "http"
      && error.status === 503
      && error.retryable
      && (error.payload as { error: string }).error === "busy");
  owner.dispose();
}

{
  let hostListener: ((message: unknown) => void) | null = null;
  const transport: HostMessageTransport = {
    canSubscribeMessages: true,
    postMessage: () => undefined,
    subscribeMessages: (listener) => {
      hostListener = listener;
      return () => {
        hostListener = null;
      };
    }
  };
  const owner = new BackendSessionOwner({ transport });
  let requestSignal: AbortSignal | null = null;
  const client = new RequestClient(owner, {
    transport: async (_url, options) => {
      requestSignal = options.signal ?? null;
      return new Promise<unknown>((_resolve, reject) => {
        const rejectAbort = () => reject(new Error("transport aborted"));
        if (options.signal?.aborted) {
          rejectAbort();
        } else {
          options.signal?.addEventListener("abort", rejectAbort, { once: true });
        }
      });
    }
  });
  const request = client.request({
    key: "test.epoch",
    url: "/api/test",
    fallbackError: "读取失败",
    decoder: readyDecoder,
    timeoutMs: 1_000
  });
  hostListener!({
    type: "host.backend-session",
    schemaVersion: 1,
    publicationRevision: "1",
    state: "ready",
    epoch: "1:0000000000000007:0000000000000011"
  });
  await new Promise((resolve) => setTimeout(resolve, 0));
  hostListener!({
    type: "host.backend-session",
    schemaVersion: 1,
    publicationRevision: "2",
    state: "ready",
    epoch: "2:0000000000000008:0000000000000012"
  });
  await assert.rejects(
    request,
    (error: unknown) => error instanceof RequestProblem
      && error.kind === "backend-session-changed");
  assert.equal(requestSignal?.aborted, true);
  owner.dispose();
}

{
  const transport: HostMessageTransport = {
    canSubscribeMessages: true,
    postMessage: () => undefined,
    subscribeMessages: () => () => undefined
  };
  const owner = new BackendSessionOwner({ transport });
  const client = new RequestClient(owner, {
    transport: async () => ({ ready: true })
  });
  await assert.rejects(
    client.request({
      key: "test.waiting-timeout",
      url: "/api/test",
      fallbackError: "读取失败",
      decoder: readyDecoder,
      timeoutMs: 20
    }),
    (error: unknown) => error instanceof RequestProblem
      && error.kind === "timeout"
      && error.attempt.backendEpoch === null);
  owner.dispose();
}
