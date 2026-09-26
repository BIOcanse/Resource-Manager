import assert from "node:assert/strict";
import {
  defineTaskKey,
  type ScopedTaskDescriptor,
  type TaskConcurrency,
  type TaskKey
} from "../src/frontendRuntime/task/TaskDescriptor.ts";
import type {
  TaskExecutionContext,
  TaskRun,
  TaskScope
} from "../src/frontendRuntime/task/TaskScope.ts";
import { TaskRegistry } from "../src/frontendRuntime/task/TaskRegistry.ts";

class FakeClock {
  now = 0;
  private nextId = 0;
  private readonly tasks = new Map<number, { due: number; callback: () => void }>();

  get pendingCount(): number {
    return this.tasks.size;
  }

  schedule = (callback: () => void, delayMs: number): number => {
    const id = ++this.nextId;
    this.tasks.set(id, { due: this.now + delayMs, callback });
    return id;
  };

  cancel = (handle: unknown): void => {
    this.tasks.delete(handle as number);
  };

  advance(milliseconds: number): void {
    const target = this.now + milliseconds;
    while (true) {
      let selectedId: number | null = null;
      let selectedDue = Number.POSITIVE_INFINITY;
      for (const [id, task] of this.tasks) {
        if (task.due <= target && task.due < selectedDue) {
          selectedId = id;
          selectedDue = task.due;
        }
      }
      if (selectedId === null) {
        break;
      }
      const task = this.tasks.get(selectedId)!;
      this.tasks.delete(selectedId);
      this.now = task.due;
      task.callback();
    }
    this.now = target;
  }
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

const testTaskKeys = new Map<string, object>();

function descriptor<T = unknown>(
  concurrency: TaskConcurrency,
  keyValue = "transition",
  groupKey?: string
): ScopedTaskDescriptor<T> {
  let key = testTaskKeys.get(keyValue);
  if (!key) {
    key = defineTaskKey<T>(keyValue);
    testTaskKeys.set(keyValue, key);
  }
  return {
    key: key as TaskKey<T>,
    groupKey,
    durability: "transient",
    visibility: "hidden",
    concurrency
  };
}

{
  const registry = new TaskRegistry();
  const scope = registry.openScope("allow-scope");
  const committed: number[] = [];
  const first = scope.run(descriptor("allow"), async (context) => {
    context.reportProgress({ phase: "paint", completed: 1, total: 2 });
    context.commit(() => committed.push(1));
    return 1;
  });
  const second = scope.run(descriptor("allow"), async (context) => {
    context.commit(() => committed.push(2));
    return 2;
  });
  assert.notEqual(first.id, second.id);
  assert.equal(registry.activeTaskCount, 2);
  assert.deepEqual(await first.completion, { status: "succeeded", value: 1 });
  assert.deepEqual(await second.completion, { status: "succeeded", value: 2 });
  assert.deepEqual(committed.sort(), [1, 2]);
  assert.equal(first.snapshot.progress?.phase, "paint");
  scope.close();
  registry.dispose();
}

{
  const registry = new TaskRegistry();
  const scope = registry.openScope("coalesce-cancel");
  const run = scope.run(descriptor("coalesce"), async ({ signal }) =>
    new Promise<never>((_resolve, reject) => {
      signal.addEventListener("abort", () => reject(signal.reason), { once: true });
    }));
  const coalesced = scope.run(descriptor("coalesce"), async () => "unused");
  assert.equal(run, coalesced);
  coalesced.cancel("shared owner cancelled");
  assert.deepEqual(await run.completion, {
    status: "cancelled",
    reason: "shared owner cancelled"
  });
  scope.close();
  registry.dispose();
}

{
  const registry = new TaskRegistry();
  const scope = registry.openScope("replace-scope");
  const oldGate = deferred<void>();
  let oldContext: TaskExecutionContext | null = null;
  let oldCommitAccepted: boolean | null = null;
  const commits: string[] = [];
  const oldRun = scope.run(descriptor("replace"), async (context) => {
    oldContext = context;
    await oldGate.promise;
    oldCommitAccepted = context.commit(() => commits.push("old"));
    return "old";
  });
  await flushPromises();
  const currentRun = scope.run(descriptor("replace"), async (context) => {
    context.commit(() => commits.push("current"));
    return "current";
  });
  const oldOutcome = await oldRun.completion;
  assert.equal(oldOutcome.status, "superseded");
  assert.equal(oldContext?.signal.aborted, true);
  assert.deepEqual(await currentRun.completion, {
    status: "succeeded",
    value: "current"
  });
  oldGate.resolve();
  await flushPromises();
  assert.equal(oldCommitAccepted, false);
  assert.deepEqual(commits, ["current"]);
  scope.close();
  registry.dispose();
}

{
  const registry = new TaskRegistry();
  const scope = registry.openScope("coalesce-scope");
  const gate = deferred<number>();
  let executions = 0;
  const first = scope.run(descriptor("coalesce"), async () => {
    executions += 1;
    return gate.promise;
  });
  const second = scope.run(descriptor("coalesce"), async () => {
    executions += 100;
    return 100;
  });
  assert.equal(first, second);
  await flushPromises();
  assert.equal(executions, 1);
  gate.resolve(7);
  assert.deepEqual(await first.completion, { status: "succeeded", value: 7 });
  scope.close();
  registry.dispose();
}

{
  const registry = new TaskRegistry();
  const scope = registry.openScope("serial-scope");
  const firstGate = deferred<void>();
  let firstStarted = false;
  let secondStarted = false;
  const first = scope.run(descriptor("serial", "write", "shared-writes"), async () => {
    firstStarted = true;
    await firstGate.promise;
    return "first";
  });
  const second = scope.run(descriptor("serial", "write", "shared-writes"), async () => {
    secondStarted = true;
    return "second";
  });
  await flushPromises();
  assert.equal(firstStarted, true);
  assert.equal(secondStarted, false);
  first.cancel();
  assert.equal((await first.completion).status, "cancelled");
  await flushPromises();
  assert.equal(secondStarted, false);
  firstGate.resolve();
  await flushPromises();
  assert.equal(secondStarted, true);
  assert.deepEqual(await second.completion, {
    status: "succeeded",
    value: "second"
  });
  scope.close();
  registry.dispose();
}

{
  const registry = new TaskRegistry();
  const scopeA = registry.openScope("scope-generation");
  const gate = deferred<void>();
  let contextA: TaskExecutionContext | null = null;
  let staleCommit = true;
  const runA = scopeA.run(descriptor("replace"), async (context) => {
    contextA = context;
    await gate.promise;
    staleCommit = context.commit(() => undefined);
  });
  await flushPromises();
  const scopeB = registry.openScope("scope-generation");
  assert.equal(scopeA.closed, true);
  assert.ok(scopeB.generation > scopeA.generation);
  assert.equal((await runA.completion).status, "cancelled");
  assert.equal(contextA?.signal.aborted, true);
  assert.throws(() => scopeA.run(descriptor("replace"), async () => undefined));
  gate.resolve();
  await flushPromises();
  assert.equal(staleCommit, false);
  const runB = scopeB.run(descriptor("replace"), async () => "current");
  assert.deepEqual(await runB.completion, {
    status: "succeeded",
    value: "current"
  });
  scopeB.close();
  registry.dispose();
}

{
  const listenerErrors: Array<{ error: unknown; taskId: string }> = [];
  const observed: string[] = [];
  const registry = new TaskRegistry({
    onListenerError: (error, taskId) => listenerErrors.push({ error, taskId })
  });
  registry.subscribe(() => {
    throw new Error("observer failed");
  });
  registry.subscribe((change) => {
    if (change.kind === "upsert") {
      observed.push(change.snapshot.status);
    }
  });
  const scope = registry.openScope("listener-isolation");
  const run = scope.run(descriptor("replace"), async () => "ok");
  assert.equal((await run.completion).status, "succeeded");
  assert.deepEqual(observed, ["queued", "running", "succeeded"]);
  assert.equal(listenerErrors.length, 3);
  assert.ok(listenerErrors.every((item) => item.taskId === run.id));
  scope.close();
  registry.dispose();
}

{
  const clock = new FakeClock();
  const registry = new TaskRegistry({
    now: () => clock.now,
    schedule: clock.schedule,
    cancelSchedule: clock.cancel,
    terminalRetentionMs: 10,
    maxTerminalEntries: 2
  });
  const scope = registry.openScope("retention");
  for (let index = 0; index < 3; index += 1) {
    const run = scope.run(descriptor("allow", `task-${index}`), async () => index);
    await run.completion;
    clock.now += 1;
  }
  assert.equal(registry.taskCount, 2);
  clock.advance(10);
  assert.equal(registry.taskCount, 0);
  scope.close();
  registry.dispose();
}

{
  const clock = new FakeClock();
  const registry = new TaskRegistry({
    now: () => clock.now,
    schedule: clock.schedule,
    cancelSchedule: clock.cancel,
    terminalRetentionMs: 10,
    maxTerminalEntries: 8
  });
  const scope = registry.openScope("retention-remove-reentry");
  const first = scope.run(
    descriptor<string>("allow", "retention-reentry-first"),
    async () => "first");
  await first.completion;

  clock.now = 5;
  const retained = scope.run(
    descriptor<string>("allow", "retention-reentry-retained"),
    async () => "retained");
  await retained.completion;

  const runningGate = deferred<void>();
  const running = scope.run(
    descriptor<void>("allow", "retention-reentry-running"),
    async () => runningGate.promise);
  await flushPromises();
  registry.subscribe((change) => {
    if (change.kind === "remove" && change.taskId === first.id) {
      running.cancel("cancelled by remove listener");
    }
  });

  clock.advance(5);
  assert.equal((await running.completion).status, "cancelled");
  assert.equal(clock.pendingCount, 1);
  registry.dispose();
  assert.equal(clock.pendingCount, 0);
  runningGate.resolve();
}

{
  const registry = new TaskRegistry();
  const scope = registry.openScope("external");
  assert.throws(
    () => scope.run({
      key: defineTaskKey<void>("external-operation"),
      durability: "external-durable",
      visibility: "user",
      concurrency: "coalesce"
    }, async () => undefined),
    /cannot run a frontend executor/);
  scope.close();
  registry.dispose();
}

{
  const registry = new TaskRegistry();
  const scope = registry.openScope("dispose");
  const run = scope.run(descriptor("replace"), async ({ signal }) =>
    new Promise<never>((_resolve, reject) => {
      signal.addEventListener("abort", () => reject(signal.reason), { once: true });
    }));
  await flushPromises();
  registry.dispose();
  registry.dispose();
  assert.equal((await run.completion).status, "cancelled");
  assert.equal(scope.closed, true);
  assert.throws(() => registry.openScope("after-dispose"), /disposed/);
}

{
  const registry = new TaskRegistry();
  const scope = registry.openScope("serial-progress");
  const otherScope = registry.openScope("serial-progress-other");
  const firstGate = deferred<void>();
  const events: string[] = [];
  const first = scope.run(descriptor("serial", "first", "ordered"), async () => {
    events.push("first:start");
    await firstGate.promise;
    throw new Error("first failed");
  });
  let cancelledExecutorStarted = false;
  const cancelled = scope.run(
    descriptor("serial", "cancelled", "ordered"),
    async () => {
      cancelledExecutorStarted = true;
    });
  const third = scope.run(descriptor("serial", "third", "ordered"), async () => {
    events.push("third:start");
    return "third";
  });
  const independent = otherScope.run(
    descriptor("serial", "independent", "other-group"),
    async () => {
      events.push("independent:start");
      return "independent";
    });
  await flushPromises();
  assert.deepEqual(events, ["first:start", "independent:start"]);
  assert.equal((await independent.completion).status, "succeeded");
  cancelled.cancel();
  assert.equal((await cancelled.completion).status, "cancelled");
  firstGate.resolve();
  assert.equal((await first.completion).status, "failed");
  await flushPromises();
  assert.equal(cancelledExecutorStarted, false);
  assert.deepEqual(events, [
    "first:start",
    "independent:start",
    "third:start"
  ]);
  assert.equal((await third.completion).status, "succeeded");
  scope.close();
  otherScope.close();
  registry.dispose();
}

{
  const registry = new TaskRegistry();
  const scope = registry.openScope("serial-scope-close");
  const unaffectedScope = registry.openScope("serial-scope-close-unaffected");
  const gate = deferred<void>();
  let queuedStarted = false;
  const active = scope.run(descriptor("serial", "active", "close-group"), async () => {
    await gate.promise;
  });
  const queued = scope.run(descriptor("serial", "queued", "close-group"), async () => {
    queuedStarted = true;
  });
  const unaffected = unaffectedScope.run(
    descriptor("allow", "unaffected"),
    async () => "ok");
  await flushPromises();
  scope.close();
  assert.equal((await active.completion).status, "cancelled");
  assert.equal((await queued.completion).status, "cancelled");
  assert.equal(queuedStarted, false);
  assert.equal((await unaffected.completion).status, "succeeded");
  gate.resolve();
  await flushPromises();
  assert.equal(queuedStarted, false);
  unaffectedScope.close();
  registry.dispose();
}

{
  const registry = new TaskRegistry();
  const scope = registry.openScope("descriptor-contract");
  const gate = deferred<void>();
  const active = scope.run(descriptor("coalesce", "stable-key"), async () => {
    await gate.promise;
  });
  assert.throws(
    () => scope.run(descriptor("replace", "stable-key"), async () => undefined),
    /conflicting descriptor/);
  active.cancel();
  await active.completion;
  const next = scope.run(descriptor("coalesce", "stable-key"), async () => "next");
  assert.notEqual(next.id, active.id);
  assert.ok(next.snapshot.generation > active.snapshot.generation);
  assert.equal((await next.completion).status, "succeeded");
  gate.resolve();
  scope.close();
  registry.dispose();
}

{
  const clock = new FakeClock();
  const registry = new TaskRegistry({
    now: () => clock.now,
    schedule: clock.schedule,
    cancelSchedule: clock.cancel,
    terminalRetentionMs: 0,
    maxTerminalEntries: 0
  });
  const scope = registry.openScope("active-retention");
  const gate = deferred<void>();
  const active = scope.run(descriptor("allow", "active"), async () => gate.promise);
  const terminal = scope.run(descriptor("allow", "terminal"), async () => "done");
  await terminal.completion;
  assert.equal(registry.taskCount, 1);
  assert.equal(registry.listSnapshots()[0].id, active.id);
  gate.resolve();
  await active.completion;
  assert.equal(registry.taskCount, 0);
  scope.close();
  registry.dispose();
}

{
  let survivingNotifications = 0;
  const registry = new TaskRegistry({
    onListenerError: () => {
      throw new Error("diagnostic failed");
    }
  });
  registry.subscribe(() => {
    throw new Error("listener failed");
  });
  registry.subscribe(() => {
    survivingNotifications += 1;
  });
  const scope = registry.openScope("diagnostic-isolation");
  const run = scope.run(descriptor("allow"), async () => "ok");
  assert.equal((await run.completion).status, "succeeded");
  assert.equal(survivingNotifications, 3);
  scope.close();
  registry.dispose();
}

{
  const registry = new TaskRegistry();
  const scope = registry.openScope("replace-reentrant");
  const oldGate = deferred<void>();
  const commits: string[] = [];
  let nestedRun: TaskRun<string> | null = null;
  const oldRun = scope.run(descriptor<void>("replace", "replace-reentrant"), async ({ signal }) => {
    signal.addEventListener("abort", () => {
      nestedRun = scope.run(
        descriptor<string>("replace", "replace-reentrant"),
        async (context) => {
          context.commit(() => commits.push("nested"));
          return "nested";
        });
    }, { once: true });
    await oldGate.promise;
  });

  const outerRun = scope.run(
    descriptor<string>("replace", "replace-reentrant"),
    async (context) => {
      context.commit(() => commits.push("outer"));
      return "outer";
    });
  assert.equal((await oldRun.completion).status, "superseded");
  assert.equal((await outerRun.completion).status, "superseded");
  assert.ok(nestedRun);
  assert.deepEqual(await nestedRun.completion, {
    status: "succeeded",
    value: "nested"
  });
  assert.deepEqual(commits, ["nested"]);
  assert.equal(registry.activeTaskCount, 0);
  oldGate.resolve();
  await flushPromises();
  scope.close();
  registry.dispose();
}

{
  const registry = new TaskRegistry();
  const oldScope = registry.openScope("scope-reentrant");
  const oldGate = deferred<void>();
  const nestedGate = deferred<void>();
  let nestedScope: TaskScope | null = null;
  let nestedRun: TaskRun<void> | null = null;
  let nestedSignal: AbortSignal | null = null;
  const oldRun = oldScope.run(
    descriptor<void>("replace", "scope-reentrant-task"),
    async ({ signal }) => {
      signal.addEventListener("abort", () => {
        nestedScope = registry.openScope("scope-reentrant");
        nestedRun = nestedScope.run(
          descriptor<void>("replace", "scope-reentrant-task"),
          async (context) => {
            nestedSignal = context.signal;
            await nestedGate.promise;
          });
      }, { once: true });
      await oldGate.promise;
    });

  const losingOuterScope = registry.openScope("scope-reentrant");
  assert.equal((await oldRun.completion).status, "cancelled");
  assert.equal(losingOuterScope.closed, true);
  assert.ok(nestedScope);
  assert.equal(nestedScope.closed, false);
  assert.ok(nestedRun);
  assert.equal(nestedRun.snapshot.status, "running");
  assert.equal(nestedSignal?.aborted, false);
  assert.throws(
    () => losingOuterScope.run(
      descriptor<void>("replace", "scope-reentrant-task"),
      async () => undefined),
    /closed/);

  nestedScope.close();
  assert.equal((await nestedRun.completion).status, "cancelled");
  assert.equal(nestedSignal?.aborted, true);
  oldGate.resolve();
  nestedGate.resolve();
  await flushPromises();
  registry.dispose();
}

{
  const registry = new TaskRegistry();
  const scope = registry.openScope("serial-reentrant-order");
  const order: string[] = [];
  let nestedRun: TaskRun<string> | null = null;
  registry.subscribe((change) => {
    if (change.kind === "upsert"
      && change.snapshot.status === "queued"
      && change.snapshot.descriptor.key === "serial-outer"
      && !nestedRun) {
      nestedRun = scope.run(
        descriptor<string>("serial", "serial-nested", "serial-reentrant"),
        async () => {
          order.push("nested");
          return "nested";
        });
    }
  });
  const outerRun = scope.run(
    descriptor<string>("serial", "serial-outer", "serial-reentrant"),
    async () => {
      order.push("outer");
      return "outer";
    });
  await flushPromises();
  assert.ok(nestedRun);
  assert.deepEqual(await outerRun.completion, {
    status: "succeeded",
    value: "outer"
  });
  assert.deepEqual(await nestedRun.completion, {
    status: "succeeded",
    value: "nested"
  });
  assert.deepEqual(order, ["outer", "nested"]);
  scope.close();
  registry.dispose();
}

{
  const registry = new TaskRegistry({
    terminalRetentionMs: 0,
    maxTerminalEntries: 0
  });
  const scope = registry.openScope("serial-queue-release");
  const runningGate = deferred<void>();
  scope.run(
    descriptor<void>("serial", "serial-running", "serial-release"),
    async () => runningGate.promise);
  await flushPromises();
  for (let index = 0; index < 32; index += 1) {
    scope.run(
      descriptor<void>("serial", `serial-queued-${index}`, "serial-release"),
      async () => {
        throw new Error("Cancelled queued executor must not start.");
      });
  }
  scope.close();
  const serialGroups = (registry as unknown as {
    serialGroups: Map<string, { queue: unknown[]; running: unknown | null }>;
  }).serialGroups;
  assert.equal(serialGroups.size, 1);
  const retainedGroup = [...serialGroups.values()][0];
  assert.equal(retainedGroup.queue.length, 0);
  assert.ok(retainedGroup.running);
  assert.equal(registry.taskCount, 0);

  runningGate.resolve();
  await flushPromises();
  assert.equal(serialGroups.size, 0);
  registry.dispose();
}

{
  const clock = new FakeClock();
  const registry = new TaskRegistry({
    now: () => clock.now,
    schedule: clock.schedule,
    cancelSchedule: clock.cancel,
    terminalRetentionMs: 10,
    maxTerminalEntries: 8
  });
  const changes: string[] = [];
  registry.subscribe((change) => {
    changes.push(change.kind === "upsert"
      ? `${change.kind}:${change.snapshot.status}`
      : `${change.kind}:${change.taskId}`);
  });
  const scope = registry.openScope("retention-events");
  const run = scope.run(
    descriptor<string>("allow", "retention-event"),
    async () => "done");
  await run.completion;
  const revisionBeforeRemoval = registry.collectionRevision;
  clock.advance(10);
  assert.equal(registry.taskCount, 0);
  assert.ok(changes.includes(`remove:${run.id}`));
  assert.ok(registry.collectionRevision > revisionBeforeRemoval);
  scope.close();
  registry.dispose();
}

{
  const registry = new TaskRegistry({
    terminalRetentionMs: 0,
    maxTerminalEntries: 0
  });
  const scope = registry.openScope("dynamic-slot-retirement");
  for (let index = 0; index < 200; index += 1) {
    const run = scope.run(
      descriptor<number>("allow", `dynamic-${index}`),
      async () => index);
    assert.equal((await run.completion).status, "succeeded");
  }
  await flushPromises();
  const internals = registry as unknown as {
    slotContracts: Map<string, unknown>;
    activeBySlot: Map<string, unknown>;
    unsettledBySlot: Map<string, unknown>;
  };
  assert.equal(internals.slotContracts.size, 0);
  assert.equal(internals.activeBySlot.size, 0);
  assert.equal(internals.unsettledBySlot.size, 0);
  scope.close();
  registry.dispose();
}

{
  const registry = new TaskRegistry();
  const scope = registry.openScope("token-identity");
  const firstKey = defineTaskKey<string>("same-runtime-name");
  const secondKey = defineTaskKey<string>("same-runtime-name");
  const gate = deferred<void>();
  const active = scope.run({
    key: firstKey,
    durability: "transient",
    visibility: "hidden",
    concurrency: "coalesce"
  }, async () => {
    await gate.promise;
    return "first";
  });
  assert.throws(() => scope.run({
    key: secondKey,
    durability: "transient",
    visibility: "hidden",
    concurrency: "coalesce"
  }, async () => "second"), /conflicting descriptor/);
  active.cancel();
  gate.resolve();
  await active.completion;
  await flushPromises();

  const fresh = scope.run({
    key: secondKey,
    durability: "transient",
    visibility: "hidden",
    concurrency: "coalesce"
  }, async () => "fresh");
  assert.deepEqual(await fresh.completion, {
    status: "succeeded",
    value: "fresh"
  });
  scope.close();
  registry.dispose();
}

{
  const registry = new TaskRegistry();
  const scope = registry.openScope("publication-order");
  const observed: Array<{ revision: number; status: string }> = [];
  let closed = false;
  registry.subscribe((change) => {
    if (!closed
      && change.kind === "upsert"
      && change.snapshot.status === "queued") {
      closed = true;
      scope.close("closed from queued publication");
    }
  });
  registry.subscribe((change) => {
    if (change.kind === "upsert") {
      observed.push({
        revision: change.revision,
        status: change.snapshot.status
      });
    }
  });
  const run = scope.run(
    descriptor<void>("allow", "publication-order"),
    async () => undefined);
  assert.equal((await run.completion).status, "cancelled");
  assert.deepEqual(observed.map((item) => item.status), ["queued", "cancelled"]);
  assert.ok(observed[0].revision < observed[1].revision);
  registry.dispose();
}

async function flushPromises(): Promise<void> {
  for (let index = 0; index < 8; index += 1) {
    await Promise.resolve();
  }
}
