import assert from "node:assert/strict";
import type { HostMessageTransport } from "../src/host/hostMessageTransport.ts";
import {
  BackendSessionChangedError,
  BackendSessionOwner,
  BackendSessionUnavailableError,
  decodeHostBackendSessionMessage
} from "../src/frontendRuntime/session/BackendSessionOwner.ts";

function createHostTransport() {
  let listener: ((message: unknown) => void) | null = null;
  const sent: unknown[] = [];
  const transport: HostMessageTransport = {
    canSubscribeMessages: true,
    postMessage: (message) => sent.push(message),
    subscribeMessages: (next) => {
      listener = next;
      return () => {
        listener = null;
      };
    }
  };
  return {
    transport,
    sent,
    publish: (message: unknown) => listener?.(message),
    hasListener: () => listener !== null,
    captureListener: () => listener
  };
}

function createManualScheduler() {
  let current: (() => void) | null = null;
  return {
    schedule: (callback: () => void) => {
      current = callback;
      return callback;
    },
    cancel: (handle: unknown) => {
      if (current === handle) {
        current = null;
      }
    },
    fire: () => {
      const callback = current;
      current = null;
      callback?.();
    },
    hasPending: () => current !== null
  };
}

const readyMessage = {
  type: "host.backend-session",
  schemaVersion: 1,
  publicationRevision: "1",
  state: "ready",
  epoch: "1:0000000000000007:0000000000000011"
} as const;

{
  const owner = new BackendSessionOwner({
    transport: null,
    allowStandalone: true,
    standaloneEpochFactory: () => "standalone:test"
  });
  assert.equal(owner.snapshot.status, "ready");
  assert.equal(owner.capture().epoch, "standalone:test");
  assert.equal(owner.capture().authority, "standalone-page");
  owner.dispose();
}

{
  const owner = new BackendSessionOwner({ transport: null });
  assert.equal(owner.snapshot.status, "unavailable");
  assert.throws(() => owner.capture(), BackendSessionUnavailableError);
  owner.dispose();
}

{
  const host = createHostTransport();
  const owner = new BackendSessionOwner({ transport: host.transport });
  assert.equal(owner.snapshot.status, "waiting");
  assert.deepEqual(host.sent, [{ type: "host.backend-session.request" }]);

  const waiting = owner.waitForReady();
  host.publish({ type: "unrelated" });
  assert.equal(owner.snapshot.status, "waiting");
  host.publish(readyMessage);
  const capture = await waiting;
  assert.equal(capture.epoch, readyMessage.epoch);
  assert.equal(capture.authority, "native-ui");

  host.publish({ ...readyMessage, publicationRevision: "2" });
  assert.equal(owner.snapshot.publicationRevision, "2");
  assert.equal(capture.signal.aborted, false);

  host.publish({
    ...readyMessage,
    publicationRevision: "3",
    epoch: "2:0000000000000008:0000000000000012"
  });
  assert.equal(capture.signal.aborted, true);
  assert.ok(capture.signal.reason instanceof BackendSessionChangedError);
  assert.equal(owner.capture().epoch, "2:0000000000000008:0000000000000012");

  host.publish({
    type: "host.backend-session",
    schemaVersion: 1,
    publicationRevision: "4",
    state: "unavailable",
    reasonCode: "connection-lost"
  });
  assert.equal(owner.snapshot.status, "unavailable");
  await assert.rejects(
    owner.waitForReady(),
    (error: unknown) => error instanceof BackendSessionUnavailableError);
  owner.dispose();
  assert.equal(host.hasListener(), false);
}

{
  const host = createHostTransport();
  const owner = new BackendSessionOwner({ transport: host.transport });
  host.publish(readyMessage);
  const queuedListener = host.captureListener();
  owner.dispose();
  queuedListener?.({
    ...readyMessage,
    publicationRevision: "2",
    epoch: "2:0000000000000008:0000000000000012"
  });
  assert.equal(owner.snapshot.status, "disposed");
}

assert.deepEqual(decodeHostBackendSessionMessage(readyMessage), {
  type: "host.backend-session",
  schemaVersion: 1,
  publicationRevision: "1",
  state: "ready",
  epoch: readyMessage.epoch
});
assert.deepEqual(decodeHostBackendSessionMessage({
  ...readyMessage,
  backend: { ...readyMessage.backend, processId: 0 }
}), decodeHostBackendSessionMessage(readyMessage));
assert.deepEqual(decodeHostBackendSessionMessage({
  ...readyMessage,
  backend: { ...readyMessage.backend, instanceId: 7 }
}), decodeHostBackendSessionMessage(readyMessage));
assert.deepEqual(decodeHostBackendSessionMessage({
  ...readyMessage,
  backend: { ...readyMessage.backend, processStartUtcTicks: 7 }
}), decodeHostBackendSessionMessage(readyMessage));
assert.deepEqual(decodeHostBackendSessionMessage({
  ...readyMessage,
  accessToken: "secret"
}), decodeHostBackendSessionMessage(readyMessage));

{
  const host = createHostTransport();
  const owner = new BackendSessionOwner({ transport: host.transport });
  host.publish(readyMessage);
  const capture = owner.capture();
  host.publish({
    ...readyMessage,
    publicationRevision: "2",
    backend: { ...readyMessage.backend, processId: 43 }
  });
  assert.equal(owner.snapshot.status, "ready");
  assert.equal(capture.signal.aborted, false);
  owner.dispose();
}

{
  const host = createHostTransport();
  const scheduler = createManualScheduler();
  const owner = new BackendSessionOwner({
    transport: host.transport,
    waitingTimeoutMs: 25,
    schedule: scheduler.schedule,
    cancelSchedule: scheduler.cancel
  });
  assert.equal(scheduler.hasPending(), true);
  scheduler.fire();
  assert.equal(owner.snapshot.status, "unavailable");
  assert.match(owner.snapshot.reason ?? "", /超时/);

  const firstRetry = owner.retry();
  const coalescedRetry = owner.retry();
  assert.equal(owner.snapshot.status, "waiting");
  assert.equal(host.sent.length, 2);
  assert.equal(scheduler.hasPending(), true);
  host.publish(readyMessage);
  assert.equal((await firstRetry).epoch, readyMessage.epoch);
  assert.equal((await coalescedRetry).epoch, readyMessage.epoch);
  assert.equal(scheduler.hasPending(), false);
  owner.dispose();
}

{
  const host = createHostTransport();
  const owner = new BackendSessionOwner({ transport: host.transport });
  host.publish({ ...readyMessage, publicationRevision: "9" });
  const capture = owner.capture();
  host.publish({
    ...readyMessage,
    publicationRevision: "8",
    epoch: "old-epoch"
  });
  assert.equal(owner.capture().epoch, readyMessage.epoch);
  assert.equal(capture.signal.aborted, false);
  owner.dispose();
}
