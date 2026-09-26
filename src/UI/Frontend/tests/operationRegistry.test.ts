import assert from "node:assert/strict";
import type { OperationSnapshot } from "../src/types.ts";
import type {
  HostManagerOperationsState
} from "../src/data/operations/operationsStateDecoder.ts";
import type {
  CurrentValueSource
} from "../src/frontendRuntime/push/PushValueSourceFamily.ts";
import type {
  RequestClient,
  RequestOutcome
} from "../src/frontendRuntime/request/RequestClient.ts";
import {
  OperationRegistry
} from "../src/frontendRuntime/operations/OperationRegistry.ts";
import {
  selectOperations
} from "../src/frontendRuntime/operations/operationSelectors.ts";

class FakeCurrentValueSource
implements CurrentValueSource<HostManagerOperationsState> {
  readonly key = "host-manager.operations.state";
  private readonly listeners = new Set<(value: HostManagerOperationsState) => void>();
  subscribeCount = 0;
  unsubscribeCount = 0;

  subscribe(listener: (value: HostManagerOperationsState) => void): () => void {
    this.subscribeCount += 1;
    this.listeners.add(listener);
    let disposed = false;
    return () => {
      if (disposed) {
        return;
      }
      disposed = true;
      this.unsubscribeCount += 1;
      this.listeners.delete(listener);
    };
  }

  publish(value: HostManagerOperationsState): void {
    for (const listener of [...this.listeners]) {
      listener(value);
    }
  }
}

class FakeRequestClient {
  private readonly outcomes: Array<Promise<RequestOutcome<OperationSnapshot>>> = [];
  executeCount = 0;

  enqueue(operation: OperationSnapshot, backendEpoch = "irrelevant"): void {
    this.outcomes.push(Promise.resolve(requestOutcome(operation, backendEpoch)));
  }

  async execute<T>(): Promise<RequestOutcome<T>> {
    this.executeCount += 1;
    const outcome = this.outcomes.shift();
    if (!outcome) {
      throw new Error("No fake request outcome was queued.");
    }
    return await outcome as RequestOutcome<T>;
  }
}

{
  const fixture = createFixture();
  assert.equal(fixture.registry.snapshot.status, "loading");
  assert.equal(fixture.source.subscribeCount, 1);

  const running = operation();
  fixture.source.publish(state("9", [running]));
  assert.equal(fixture.registry.snapshot.status, "ready");
  assert.equal(fixture.registry.snapshot.actionsEnabled, true);
  assert.equal(fixture.registry.get(running.id), running);
  assert.deepEqual(selectOperations(fixture.registry.snapshot, { active: true }), [running]);

  const replacement = operation({
    title: "Backend current value",
    stateRevision: "2",
    updatedAt: "2026-08-22T12:00:02.000Z"
  });
  fixture.source.publish(state("1", [replacement]));
  assert.equal(
    fixture.registry.get(running.id)?.title,
    "Backend current value",
    "the frontend must display the latest callback instead of judging its revision");

  fixture.source.publish(state("2", []));
  assert.deepEqual(fixture.registry.list(), []);
  fixture.registry.dispose();
}

{
  const fixture = createFixture();
  const invalidations: string[] = [];
  fixture.registry.subscribeInvalidations((value) => {
    invalidations.push(`${value.operationId}:${value.targets.join(",")}`);
  });
  fixture.source.publish(state("1", [operation()]));
  const succeeded = operation({
    state: "succeeded",
    stateRevision: "2",
    progress: { ...operation().progress!, sequence: "2", percent: 100 },
    updatedAt: "2026-08-22T12:00:02.000Z",
    completedAt: "2026-08-22T12:00:02.000Z"
  });
  fixture.source.publish(state("2", [succeeded]));
  fixture.source.publish(state("2", [succeeded]));
  assert.deepEqual(invalidations, [
    `${succeeded.id}:components`
  ]);
  fixture.registry.dispose();
}

{
  const fixture = createFixture();
  fixture.source.publish(state("1", []));
  const submitted = operation({
    id: "00000000000000000000000000000002",
    kind: "software.uninstall",
    domainKey: "software.example"
  });
  fixture.client.enqueue(submitted, "unrelated-response-epoch");

  assert.equal((await fixture.registry.submit(command())).id, submitted.id);
  assert.equal(fixture.registry.get(submitted.id), null);
  assert.equal(fixture.client.executeCount, 1);

  const terminal = fixture.registry.waitForTerminal(submitted.id);
  fixture.source.publish(state("2", [submitted]));
  const completed = {
    ...submitted,
    state: "succeeded" as const,
    stateRevision: "2",
    updatedAt: "2026-08-22T12:00:03.000Z",
    completedAt: "2026-08-22T12:00:03.000Z"
  };
  fixture.source.publish(state("3", [completed]));
  assert.equal(await terminal, completed);
  fixture.registry.dispose();
}

{
  const fixture = createFixture();
  fixture.source.publish(state("1", [operation()]));
  const terminal = fixture.registry.waitForTerminal(operation().id);
  fixture.source.publish(state("2", []));
  await assert.rejects(terminal, /已经从当前值中移除/);
  fixture.registry.dispose();
}

{
  const fixture = createFixture();
  const listenerErrors: string[] = [];
  fixture.registry.dispose();

  const second = createFixture({
    onListenerError: (error, source) =>
      listenerErrors.push(`${source}:${String(error)}`)
  });
  let secondListenerCalls = 0;
  second.registry.subscribe(() => {
    throw new Error("consumer failed");
  });
  second.registry.subscribe(() => {
    secondListenerCalls += 1;
  });
  second.source.publish(state("1", []));
  assert.equal(listenerErrors.length, 1);
  assert.equal(secondListenerCalls, 1);

  let disposedPublication = false;
  second.registry.subscribe((snapshot) => {
    disposedPublication ||= snapshot.status === "disposed";
  });
  second.registry.dispose();
  assert.equal(disposedPublication, true);
  assert.equal(second.source.unsubscribeCount, 1);
  assert.doesNotThrow(() => second.registry.dispose());
}

function createFixture(
  options: ConstructorParameters<typeof OperationRegistry>[2] = {}
) {
  const source = new FakeCurrentValueSource();
  const client = new FakeRequestClient();
  const registry = new OperationRegistry(
    client as unknown as RequestClient,
    source,
    options);
  return { source, client, registry };
}

function operation(
  overrides: Partial<OperationSnapshot> = {}
): OperationSnapshot {
  const stateValue = overrides.state ?? "running";
  const updatedAt = overrides.updatedAt ?? "2026-08-22T12:00:01.000Z";
  const terminal = stateValue === "succeeded"
    || stateValue === "failed"
    || stateValue === "canceled"
    || stateValue === "stateUncertain";
  return Object.freeze({
    id: "00000000000000000000000000000001",
    kind: "component.install",
    domainKey: "component.example",
    title: "Install component",
    state: stateValue,
    configurationGeneration: "1",
    stateRevision: "1",
    attemptNumber: 1,
    maximumAttempts: 3,
    createdAt: "2026-08-22T12:00:00.000Z",
    updatedAt,
    completedAt: terminal ? updatedAt : null,
    cancelRequested: false,
    progress: Object.freeze({
      sequence: "1",
      percent: 25,
      bytesDone: "1",
      bytesTotal: "4",
      speedBytesPerSecond: "1",
      stage: "running",
      message: null
    }),
    result: null,
    error: null,
    ...overrides
  });
}

function state(
  publicationRevision: string,
  operations: readonly OperationSnapshot[]
): HostManagerOperationsState {
  return Object.freeze({
    schema: "host-manager.operations.state.v1",
    capturedAt: "2026-08-22T12:00:10.000Z",
    publicationRevision,
    configurationGeneration: "1",
    ready: true,
    persistenceFaulted: false,
    faultStage: null,
    faultMessage: null,
    operations: Object.freeze([...operations])
  });
}

function command() {
  return {
    key: "component.install:component.example",
    url: "/api/components/component.example/install",
    body: {},
    fallbackError: "Install failed"
  };
}

function requestOutcome(
  value: OperationSnapshot,
  backendEpoch: string
): RequestOutcome<OperationSnapshot> {
  return {
    value,
    attempt: {
      id: "request-1",
      sequence: 1,
      key: "test",
      decoderId: "host-manager.operation.v1",
      method: "POST",
      backendEpoch,
      startedAt: 0,
      finishedAt: 1,
      durationMs: 1
    }
  };
}
