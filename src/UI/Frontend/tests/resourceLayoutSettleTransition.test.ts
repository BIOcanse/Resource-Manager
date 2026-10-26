import assert from "node:assert/strict";
import { TaskRegistry } from "../src/frontendRuntime/task/TaskRegistry.ts";
import type { TaskDelay } from "../src/frontendRuntime/task/waitForTaskDelay.ts";
import {
  createResourceLayoutSettleController,
  resourceLayoutSettleMarginMs,
  shouldRunResourceLayoutSettleTransition,
  startResourceLayoutSettleTransition
} from "../src/features/resourceBreakdown/resourceLayoutSettleTransition.ts";

// 平滑手感的 --motion-bar，不是标准的 360：延时要跟着传进来的值走。
const barDurationMs = 480;

assert.equal(
  shouldRunResourceLayoutSettleTransition(false, "desktop", "none"),
  true,
  "Disabling motion must replace an active settle task without a layout change.");
assert.equal(
  shouldRunResourceLayoutSettleTransition(false, "none", "desktop"),
  false,
  "Enabling motion must not animate an unchanged historical layout.");
assert.equal(
  shouldRunResourceLayoutSettleTransition(true, "none", "desktop"),
  true,
  "A real layout change still starts the transition selected by the new mode.");

for (const disabledMode of ["none", "ultra"] as const) {
  const registry = new TaskRegistry();
  const scope = registry.openScope(`resource-layout-reactive-mode:${disabledMode}`);
  const gate = deferred();
  const mutations: boolean[] = [];
  const update = createResourceLayoutSettleController(
    scope,
    (value) => mutations.push(value),
    async () => gate.promise);
  const firstLayout = [
    { segment: { softwareId: "a", value: 1 }, index: 0, left: 0, right: 100, width: 100 }
  ];
  const changedLayout = [
    { segment: { softwareId: "a", value: 1 }, index: 0, left: 0, right: 50, width: 50 },
    { segment: { softwareId: "b", value: 1 }, index: 1, left: 50, right: 100, width: 50 }
  ];

  let durationReads = 0;
  const readDuration = () => { durationReads += 1; return barDurationMs; };
  assert.equal(update(firstLayout, "desktop", readDuration), null);
  assert.equal(update(structuredClone(firstLayout), "desktop", readDuration), null,
    "An unchanged callback must not hide fixed-type dividers");
  assert.deepEqual(mutations, []);
  assert.equal(durationReads, 0, "Mount and unchanged layouts must not read computed style");
  const animated = update(changedLayout, "desktop", readDuration);
  assert.ok(animated);
  assert.deepEqual(mutations, [true]);
  assert.equal(durationReads, 1, "An animated layout change reads its current token once");
  const disabled = update(changedLayout, disabledMode, readDuration);
  assert.ok(disabled);
  assert.equal((await animated.completion).status, "superseded");
  assert.deepEqual(await disabled.completion, {
    status: "succeeded",
    value: undefined
  });
  assert.deepEqual(mutations, [true, false]);
  const instant = update(firstLayout, disabledMode, readDuration);
  assert.ok(instant);
  await instant.completion;
  assert.equal(durationReads, 1, "None and ultra never consume an animation duration");

  gate.resolve();
  await flushPromises();
  assert.deepEqual(mutations, [true, false, false]);
  scope.close();
  registry.dispose();
}

function deferred() {
  let resolve!: () => void;
  const promise = new Promise<void>((resolvePromise) => {
    resolve = resolvePromise;
  });
  return { promise, resolve };
}

{
  const registry = new TaskRegistry();
  const scope = registry.openScope("resource-layout-normal");
  const gates = [deferred(), deferred()];
  const signals: AbortSignal[] = [];
  let delayIndex = 0;
  const delay: TaskDelay = (signal, durationMs) => {
    assert.equal(durationMs, barDurationMs + resourceLayoutSettleMarginMs, "The settle delay follows --motion-bar plus the margin");
    signals.push(signal);
    return gates[delayIndex++].promise;
  };
  const mutations: boolean[] = [];
  let moving = false;
  const setMoving = (value: boolean) => {
    moving = value;
    mutations.push(value);
  };

  const first = startResourceLayoutSettleTransition(scope, true, barDurationMs, setMoving, delay);
  assert.equal(moving, true);
  const second = startResourceLayoutSettleTransition(scope, true, barDurationMs, setMoving, delay);
  assert.equal((await first.completion).status, "superseded");
  assert.equal(signals[0].aborted, true);
  assert.equal(moving, true);

  gates[0].resolve();
  await flushPromises();
  assert.equal(moving, true);
  gates[1].resolve();
  assert.equal((await second.completion).status, "succeeded");
  assert.equal(moving, false);
  assert.deepEqual(mutations, [true, true, false]);
  scope.close();
  registry.dispose();
}

{
  const registry = new TaskRegistry();
  const scope = registry.openScope("resource-layout-disabled");
  const gate = deferred();
  let delayCalls = 0;
  const delay: TaskDelay = async () => {
    delayCalls += 1;
    return gate.promise;
  };
  const mutations: boolean[] = [];
  let moving = false;
  const setMoving = (value: boolean) => {
    moving = value;
    mutations.push(value);
  };

  const animated = startResourceLayoutSettleTransition(scope, true, barDurationMs, setMoving, delay);
  assert.equal(moving, true);
  const disabled = startResourceLayoutSettleTransition(scope, false, barDurationMs, setMoving, delay);
  assert.equal((await animated.completion).status, "superseded");
  assert.equal(moving, false);
  assert.equal(delayCalls, 1);
  assert.equal((await disabled.completion).status, "succeeded");

  gate.resolve();
  await flushPromises();
  assert.equal(moving, false);
  assert.deepEqual(mutations, [true, false]);
  scope.close();
  registry.dispose();
}

{
  const registry = new TaskRegistry();
  const scope = registry.openScope("resource-layout-cleanup");
  const gate = deferred();
  const mutations: boolean[] = [];
  const run = startResourceLayoutSettleTransition(
    scope,
    true,
    barDurationMs,
    (value) => mutations.push(value),
    async () => gate.promise);
  assert.deepEqual(mutations, [true]);
  scope.close();
  assert.equal((await run.completion).status, "cancelled");
  gate.resolve();
  await flushPromises();
  assert.deepEqual(mutations, [true]);
  registry.dispose();
}

async function flushPromises(): Promise<void> {
  for (let index = 0; index < 8; index += 1) {
    await Promise.resolve();
  }
}
