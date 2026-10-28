import assert from "node:assert/strict";
import type { OperationSnapshot } from "../src/types.ts";
import type {
  OperationRegistrySnapshot
} from "../src/frontendRuntime/operations/OperationRegistry.ts";
import { defineTaskKey } from "../src/frontendRuntime/task/TaskDescriptor.ts";
import { TaskRegistry } from "../src/frontendRuntime/task/TaskRegistry.ts";
import {
  operationTitle,
  TaskCenterProjection
} from "../src/frontendRuntime/taskCenter/TaskCenterProjection.ts";
import {
  selectTaskCenterItems
} from "../src/frontendRuntime/taskCenter/taskCenterSelectors.ts";
import { uiText } from "../src/i18n/appTextStore.ts";

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

{
  const tasks = new TaskRegistry();
  const source = new FakeOperationSource();
  const resultMessage = { domain: 1, code: 22, args: [] };
  const errorMessage = { domain: 6, code: 1, args: [] };
  source.snapshot = operationRegistrySnapshot(1, [operation({
    subject: { targetKind: "Component", targetName: null, targetId: "runtime", scope: null, mode: null, targetPath: null }, domainKey: "component:runtime", resultCode: "InstallerLaunched",
    resultMessage, errorMessage,
    progress: { sequence: "1", percent: 50, bytesDone: "5", bytesTotal: "10", speedBytesPerSecond: null, stage: "Download" }
  })]);
  const projection = new TaskCenterProjection(tasks, source);
  const item = projection.snapshot.items[0];
  assert.equal(item.resultCode, "InstallerLaunched");
  assert.deepEqual(item.resultMessage, resultMessage);
  assert.deepEqual(item.errorMessage, errorMessage);
  assert.equal("error" in item, false);
  assert.equal(item.domainKey, "component:runtime");
  assert.equal(item.progressStage, "Download");
  projection.dispose();
  tasks.dispose();
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
  const projectedOperation = projection.snapshot.items.find((item) =>
    item.source === "operation")!;
  // 操作的标题不在投影里定成文字（语言可在运行中切换），显示时由 kind 和 subject 现取。
  assert.equal("title" in projectedOperation, false);
  assert.equal(projectedOperation.source === "operation"
    && operationTitle({ kind: projectedOperation.kind, subject: projectedOperation.subject }),
    `${uiText.taskCenter.operation.componentInstall}: ${uiText.status.component.nameFallback}`);
  assert.equal("title" in operation(), false);
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

{
  const taskRegistry = new TaskRegistry();
  const operationSource = new FakeOperationSource();
  const projection = new TaskCenterProjection(taskRegistry, operationSource);
  const sourceProgress = operation().progress!;
  operationSource.publish(operationRegistrySnapshot(2, [operation({
    state: "failed",
    progress: Object.freeze({ ...sourceProgress })
  })]));
  const failedOperation = projection.snapshot.items.find((item) => item.source === "operation")!;
  assert.equal(failedOperation.progressStage, "Download");
  assert.equal("result" in failedOperation, false);
  assert.equal("error" in failedOperation, false);
  assert.equal("resultSummary" in failedOperation, false);
  assert.equal("errorSummary" in failedOperation, false);
  assert.equal("message" in failedOperation.progress!, false);
  assert.equal("stage" in failedOperation.progress!, false);
  projection.dispose();
  taskRegistry.dispose();
}

for (const [status, expected] of [
  ["error", "error"],
  ["unavailable", "disconnected"]
] as const) {
  const taskRegistry = new TaskRegistry();
  const operationSource = new FakeOperationSource();
  const projection = new TaskCenterProjection(taskRegistry, operationSource);
  operationSource.publish(operationRegistrySnapshot(2, [operation()], {
    status,
    actionsEnabled: false,
    error: new Error("operation source unavailable")
  }));
  assert.equal(projection.snapshot.operationState, expected);
  assert.equal(
    projection.snapshot.items.find((item) => item.source === "operation")?.backendDisconnected,
    true);
  projection.dispose();
  taskRegistry.dispose();
}

// 每次发布只投影变了的那一项：别的前端任务和没变的操作沿用上一次的同一个对象；
// 登记表的会话状态变了，操作重新投影。不能取消的原因是代码，不是某种语言的文字。
{
  const taskRegistry = new TaskRegistry();
  const operationSource = new FakeOperationSource();
  const projection = new TaskCenterProjection(taskRegistry, operationSource);
  const scope = taskRegistry.openScope("task-center-reuse");
  const firstGate = deferred<void>();
  const secondGate = deferred<void>();
  const first = scope.run({ key: userTaskKey, title: "First", durability: "transient", visibility: "user", concurrency: "allow" },
    () => firstGate.promise);
  const byId = (id: string) => projection.snapshot.items.find((item) => item.id === id)!;
  const firstItem = byId(`frontend:${first.id}`);
  const operationItem = byId(`operation:${operation().id}`);
  const second = scope.run({ key: userTaskKey, title: "Second", durability: "transient", visibility: "user", concurrency: "allow" },
    () => secondGate.promise);
  assert.equal(byId(`frontend:${first.id}`), firstItem, "an unchanged frontend task keeps its projected item");
  assert.equal(byId(`operation:${operation().id}`), operationItem, "an unchanged operation keeps its projected item");

  firstGate.resolve();
  await first.completion;
  const finished = byId(`frontend:${first.id}`);
  assert.notEqual(finished, firstItem);
  assert.equal(finished.actionBlockedReason, "taskFinished");
  await assert.rejects(projection.cancel(finished.id), new RegExp(uiText.taskCenter.blocked.taskFinished));

  operationSource.publish(operationRegistrySnapshot(2, operationSource.snapshot.operations, { actionsEnabled: false }));
  const blocked = byId(`operation:${operation().id}`);
  assert.notEqual(blocked, operationItem, "a change of the registry's session state projects the operation again");
  assert.equal(blocked.cancelable, false);
  assert.equal(blocked.actionBlockedReason, "operationCannotCancel");

  secondGate.resolve();
  await second.completion;
  scope.close();
  projection.dispose();
  taskRegistry.dispose();
}

// The title names the target by its typed kind: components and dependencies from the localized catalog,
// software and paths from the name recorded at acceptance, all volumes from the localized scope label.
{
  const subject = { targetName: null, targetId: null, scope: null, mode: null, targetPath: null };
  const titles = uiText.taskCenter.operation;
  assert.equal(
    operationTitle({ kind: "component.install", subject: { ...subject, targetKind: "Component", targetId: "hardware-bridge" } }),
    `${titles.componentInstall}: ${uiText.status.component.names.hardwareBridge}`);
  assert.equal(
    operationTitle({ kind: "dependency.download", subject: { ...subject, targetKind: "Dependency", targetId: "shared-webview2-runtime" } }),
    `${titles.dependencyDownload}: ${uiText.status.component.names.sharedWebView2Runtime}`);
  assert.equal(
    operationTitle({ kind: "software.uninstall", subject: { ...subject, targetKind: "Software", targetId: "software-7", targetName: "Example App" } }),
    `${titles.softwareUninstall}: Example App`);
  assert.equal(
    operationTitle({ kind: "software.uninstall", subject: { ...subject, targetKind: "Software", targetId: "software-7" } }),
    titles.softwareUninstall);
  assert.equal(
    operationTitle({ kind: "disk-usage.scan", subject: { ...subject, targetKind: "Path", targetPath: "D:\\Data", targetName: "D:\\Data" } }),
    `${titles.diskUsageScan}: D:\\Data`);
  assert.equal(
    operationTitle({ kind: "disk-usage.scan", subject: { ...subject, targetKind: "AllVolumes", scope: "global" } }),
    `${titles.diskUsageScan}: ${uiText.diskUsage.scopeAllVolumes}`);
}

function operation(
  overrides: Partial<OperationSnapshot> = {}
): OperationSnapshot {
  return Object.freeze({
    id: "00000000000000000000000000000001",
    kind: "component.install",
    domainKey: "component.example",
    subject: { targetKind: "Component", targetName: null, targetId: "internal-component-id", scope: null, mode: null, targetPath: null },
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
      stage: "Download",
    }),
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
