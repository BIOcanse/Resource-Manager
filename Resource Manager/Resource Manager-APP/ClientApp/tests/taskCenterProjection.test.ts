import assert from "node:assert/strict";
import type { OperationSnapshot } from "../src/types.ts";
import type {
  OperationRegistrySnapshot
} from "../src/frontendRuntime/operations/OperationRegistry.ts";
import { defineTaskKey } from "../src/frontendRuntime/task/TaskDescriptor.ts";
import { TaskRegistry } from "../src/frontendRuntime/task/TaskRegistry.ts";
import {
  TaskCenterProjection
} from "../src/frontendRuntime/taskCenter/TaskCenterProjection.ts";
import {
  selectTaskCenterItems
} from "../src/frontendRuntime/taskCenter/taskCenterSelectors.ts";

class FakeOperationSource {
  private readonly listeners = new Set<(
    snapshot: OperationRegistrySnapshot
  ) => void>();
  snapshot = operationRegistrySnapshot(1, [operation()]);
  readonly canceledIds: string[] = [];

  subscribe(listener: (
    snapshot: OperationRegistrySnapshot
  ) => void): () => void {
    this.listeners.add(listener);
    return () => this.listeners.delete(listener);
  }

  async cancel(id: string): Promise<OperationSnapshot> {
    this.canceledIds.push(id);
    return operation({ id, cancelRequested: true, stateRevision: "2" });
  }

  publish(snapshot: OperationRegistrySnapshot): void {
    this.snapshot = snapshot;
    for (const listener of [...this.listeners]) {
      listener(snapshot);
    }
  }
}

const userTaskKey = defineTaskKey<void>("frontend.user-action");
const hiddenTaskKey = defineTaskKey<void>("ui.layout-transition");

{
  const taskRegistry = new TaskRegistry();
  const operationSource = new FakeOperationSource();
  const projection = new TaskCenterProjection(taskRegistry, operationSource);
  const scope = taskRegistry.openScope("task-center-test");
  const userGate = deferred<void>();
  const hiddenGate = deferred<void>();
  const userRun = scope.run({
    key: userTaskKey,
    title: "前端用户任务",
    durability: "transient",
    visibility: "user",
    concurrency: "allow"
  }, () => userGate.promise);
  const hiddenRun = scope.run({
    key: hiddenTaskKey,
    durability: "transient",
    visibility: "hidden",
    concurrency: "replace"
  }, () => hiddenGate.promise);

  assert.equal(projection.snapshot.activeUserCount, 2);
  assert.equal(projection.snapshot.operationState, "available");
  assert.equal(
    projection.snapshot.items.find((item) => item.id === `frontend:${userRun.id}`)?.title,
    "前端用户任务");
  assert.equal(
    projection.snapshot.items.find((item) => item.id === `frontend:${userRun.id}`)?.durability,
    "transient");
  assert.equal(
    projection.snapshot.items.find((item) => item.source === "operation")?.title,
    "Install component");
  assert.equal(
    projection.snapshot.items.find((item) => item.source === "operation")?.domainKey,
    "component.example");
  assert.deepEqual(
    selectTaskCenterItems(projection.snapshot, { view: "active" })
      .map((item) => item.id)
      .sort(),
    [`frontend:${userRun.id}`, `operation:${operation().id}`].sort());
  assert.equal(
    selectTaskCenterItems(projection.snapshot, {
      view: "active",
      visibility: "diagnostics"
    }).length,
    3);

  assert.equal(await projection.cancel(`frontend:${userRun.id}`), true);
  assert.equal((await userRun.completion).status, "cancelled");
  assert.equal(
    selectTaskCenterItems(projection.snapshot, { view: "history" })
      .some((item) => item.id === `frontend:${userRun.id}`),
    true);

  assert.equal(await projection.cancel(`operation:${operation().id}`), true);
  assert.deepEqual(operationSource.canceledIds, [operation().id]);

  hiddenGate.resolve();
  await hiddenRun.completion;
  scope.close();
  projection.dispose();
  taskRegistry.dispose();
}

{
  const taskRegistry = new TaskRegistry();
  const operationSource = new FakeOperationSource();
  operationSource.snapshot = operationRegistrySnapshot(1, [
    operation({
      id: "00000000000000000000000000000001",
      domainKey: "migration:first",
      title: "迁移 First",
      createdAt: "2026-08-22T12:00:00.000Z",
      updatedAt: "2026-08-22T12:00:05.000Z"
    }),
    operation({
      id: "00000000000000000000000000000002",
      domainKey: "migration:second",
      title: "迁移 Second",
      createdAt: "2026-08-22T12:01:00.000Z",
      updatedAt: "2026-08-22T12:01:01.000Z"
    })
  ]);
  const projection = new TaskCenterProjection(taskRegistry, operationSource);
  const orderedIds = () => projection.snapshot.items
    .filter((item) => item.source === "operation")
    .map((item) => item.ownerId);
  assert.deepEqual(orderedIds(), [
    "00000000000000000000000000000002",
    "00000000000000000000000000000001"
  ]);

  operationSource.publish(operationRegistrySnapshot(2, [
    operation({
      id: "00000000000000000000000000000001",
      domainKey: "migration:first",
      title: "迁移 First",
      createdAt: "2026-08-22T12:00:00.000Z",
      updatedAt: "2026-08-22T12:03:00.000Z",
      stateRevision: "2"
    }),
    operation({
      id: "00000000000000000000000000000002",
      domainKey: "migration:second",
      title: "迁移 Second",
      createdAt: "2026-08-22T12:01:00.000Z",
      updatedAt: "2026-08-22T12:01:02.000Z",
      stateRevision: "2"
    })
  ]));
  assert.deepEqual(orderedIds(), [
    "00000000000000000000000000000002",
    "00000000000000000000000000000001"
  ], "progress updates must not reorder task-center identities");
  projection.dispose();
  taskRegistry.dispose();
}

{
  const taskRegistry = new TaskRegistry();
  const operationSource = new FakeOperationSource();
  const listenerErrors: unknown[] = [];
  const projection = new TaskCenterProjection(taskRegistry, operationSource, {
    onListenerError: (error) => listenerErrors.push(error)
  });
  let secondListenerCalls = 0;
  projection.subscribe(() => {
    throw new Error("consumer failed");
  });
  projection.subscribe(() => {
    secondListenerCalls += 1;
  });
  operationSource.publish(operationRegistrySnapshot(2, [operation({
    stateRevision: "2",
    updatedAt: "2026-08-22T12:00:02.000Z"
  })]));

  assert.equal(listenerErrors.length, 1);
  assert.equal(secondListenerCalls, 1);
  projection.dispose();
  taskRegistry.dispose();
}

{
  const taskRegistry = new TaskRegistry();
  const operationSource = new FakeOperationSource();
  const projection = new TaskCenterProjection(taskRegistry, operationSource);
  operationSource.publish(operationRegistrySnapshot(2, [operation()], {
    status: "disposed",
    actionsEnabled: false,
    error: new Error("offline")
  }));

  const item = projection.snapshot.items.find((candidate) =>
    candidate.source === "operation")!;
  assert.equal(item.startedAt, null);
  assert.equal(item.backendDisconnected, true);
  assert.equal(item.cancelable, false);
  await assert.rejects(
    projection.cancel(item.id),
    /本机服务会话尚未同步/);
  assert.equal(projection.snapshot.operationState, "disconnected");
  projection.dispose();
  taskRegistry.dispose();
}

{
  const taskRegistry = new TaskRegistry();
  const operationSource = new FakeOperationSource();
  const projection = new TaskCenterProjection(taskRegistry, operationSource);
  const scope = taskRegistry.openScope("task-center-reentry");
  const gate = deferred<void>();
  const observed: number[] = [];
  let reentered = false;
  const unsubscribe = projection.subscribe((snapshot) => {
    observed.push(snapshot.revision);
    const frontend = snapshot.items.find((item) => item.source === "frontend");
    if (!reentered && frontend?.active) {
      reentered = true;
      void projection.cancel(frontend.id);
    }
  });
  const run = scope.run({
    key: userTaskKey,
    durability: "frontend-session",
    visibility: "user",
    concurrency: "allow"
  }, () => gate.promise);

  assert.equal((await run.completion).status, "cancelled");
  assert.ok(observed.length >= 2);
  for (let index = 1; index < observed.length; index += 1) {
    assert.ok(observed[index] > observed[index - 1]);
  }
  gate.resolve();
  unsubscribe();
  scope.close();
  projection.dispose();
  taskRegistry.dispose();
}

function operation(
  overrides: Partial<OperationSnapshot> = {}
): OperationSnapshot {
  return Object.freeze({
    id: "00000000000000000000000000000001",
    kind: "component.install",
    domainKey: "component.example",
    title: "Install component",
    state: "running",
    configurationGeneration: "1",
    stateRevision: "1",
    attemptNumber: 1,
    maximumAttempts: 3,
    createdAt: "2026-08-22T12:00:00.000Z",
    updatedAt: "2026-08-22T12:00:01.000Z",
    completedAt: null,
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

function operationRegistrySnapshot(
  revision: number,
  operations: readonly OperationSnapshot[],
  overrides: Partial<OperationRegistrySnapshot> = {}
): OperationRegistrySnapshot {
  return Object.freeze({
    status: "ready",
    sourcePublicationRevision: String(revision),
    capturedAt: "2026-08-22T12:00:02.000Z",
    operations: Object.freeze([...operations]),
    actionsEnabled: true,
    error: null,
    revision,
    ...overrides
  });
}

function deferred<T>() {
  let resolve!: (value: T) => void;
  let reject!: (error: unknown) => void;
  const promise = new Promise<T>((resolvePromise, rejectPromise) => {
    resolve = resolvePromise;
    reject = rejectPromise;
  });
  return { promise, resolve, reject };
}
