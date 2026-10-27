import assert from "node:assert/strict";
import { createFrontendSources } from "../src/frontendRuntime/source/FrontendSources.ts";
import { SourceRegistry } from "../src/frontendRuntime/source/SourceRegistry.ts";
import { RequestClient } from "../src/frontendRuntime/request/RequestClient.ts";
import { BackendSessionOwner } from "../src/frontendRuntime/session/BackendSessionOwner.ts";
import { BackendSubscriptionChannel } from "../src/frontendRuntime/push/BackendSubscriptionChannel.ts";
import { saveCpuBaselineRatio, type CpuBaselineRatioSettings } from "../src/data/cpu/cpuBaselineRatioApi.ts";
import { acceptSourceCommand } from "../src/frontendRuntime/source/acceptSourceCommand.ts";

const session = new BackendSessionOwner({ transport: null, allowStandalone: true });
const registry = new SourceRegistry(session);
const channel = new BackendSubscriptionChannel({ backendSession: session });
let response: unknown = settings(null);
let fail = false;
let calls = 0;
let hold: Promise<void> | null = null;
let release = () => {};
const client = new RequestClient(session, { transport: async (url, options) => {
  assert.equal(url, "/api/cpu/baseline-ratio");
  if (options.method === "PUT") {
    return { settings: settings(0.75), savedOverrideRatio: 0.75, runtimeApplicationDisposition: "applied" };
  }
  assert.equal(options.method, "GET");
  calls += 1;
  const value = response;
  if (hold) await hold;
  if (fail) throw new Error("read failed");
  return value;
} });
const tick = async () => { for (let i = 0; i < 12; i++) await Promise.resolve(); };

try {
  const source = createFrontendSources(client, registry, channel).cpuBaselineRatio;
  const a = source.acquire({ active: false });
  const b = source.acquire({ active: false });
  assert.equal(a.snapshot.status, "idle");
  assert.equal(calls, 0, "Reading the pending value does not sample");
  await Promise.all([a.refresh(), b.refresh()]);
  assert.equal(calls, 1, "Consumers share one read");
  assert.equal(a.snapshot.data?.ratio, 0.5);
  assert.strictEqual(a.snapshot, b.snapshot);
  const accepted = b.snapshot;
  a.release(); b.release();
  const returning = source.acquire({ active: false });
  assert.strictEqual(returning.snapshot, accepted, "Return immediately reads the accepted owner snapshot");
  assert.equal(calls, 1, "Returning inactive reads have no request side effect");

  response = null;
  await returning.refresh();
  assert.equal(returning.snapshot.status, "ready");
  assert.equal(returning.snapshot.data, null, "Accepted empty replaces previous settings");
  response = settings(null); await returning.refresh();
  fail = true; await returning.refresh();
  assert.equal(returning.snapshot.status, "error");
  assert.equal(returning.snapshot.data, null, "Read failure never retains a good value");
  fail = false; await returning.refresh();

  hold = new Promise<void>(resolve => { release = resolve; });
  const olderRead = returning.refresh(); await tick();
  const result = await acceptSourceCommand(source, () => saveCpuBaselineRatio(client, 0.75),
    (value) => value.settings ?? undefined);
  assert(result.settings);
  const command = returning.snapshot;
  assert.equal(command.data?.ratio, 0.75);
  release(); hold = null; await olderRead;
  assert.strictEqual(returning.snapshot, command, "Pre-command GET cannot overwrite the authoritative command result");
  returning.release();
  const afterCommand = source.acquire({ active: false });
  assert.strictEqual(afterCommand.snapshot, command, "Command result is owned by the shared source across remounts");
  let completeCommand!: (value: Awaited<ReturnType<typeof saveCpuBaselineRatio>>) => void;
  const delayedCommand = acceptSourceCommand(source,
    () => new Promise<Awaited<ReturnType<typeof saveCpuBaselineRatio>>>(resolve => { completeCommand = resolve; }),
    (value) => value.settings ?? undefined);
  afterCommand.release();
  completeCommand({ settings: settings(0.8), savedOverrideRatio: 0.8, runtimeApplicationDisposition: "applied" });
  await delayedCommand;
  const afterUnmountedCommand = source.acquire({ active: false });
  assert.equal(afterUnmountedCommand.snapshot.data?.ratio, 0.8,
    "A command finishing after its consumer unmounts still updates the source owner");
  const beforeNotApplied = afterUnmountedCommand.snapshot;
  await acceptSourceCommand(source, async () => ({ settings: null, savedOverrideRatio: 0.6,
    runtimeApplicationDisposition: "savedNotApplied" }), (value) => value.settings ?? undefined);
  assert.strictEqual(afterUnmountedCommand.snapshot, beforeNotApplied,
    "A saved but unpublished override does not replace the current runtime settings");
  await assert.rejects(acceptSourceCommand(source, async () => { throw new Error("command failed"); }, () => undefined), /command failed/);
  assert.strictEqual(afterUnmountedCommand.snapshot, beforeNotApplied);
  await acceptSourceCommand(source, async () => null, (value) => value);
  assert.equal(afterUnmountedCommand.snapshot.status, "ready");
  assert.equal(afterUnmountedCommand.snapshot.data, null, "A mapped null is an accepted empty value, distinct from skipping acceptance");
  afterUnmountedCommand.release();
} finally {
  release(); channel.dispose(); registry.dispose(); session.dispose();
}
console.log("CPU baseline owner: shared reads, return snapshot, empty/failure replacement and command fence passed.");

function settings(overrideRatio: number | null): CpuBaselineRatioSettings {
  const ratio = overrideRatio ?? 0.5;
  return { cpuName: "Fixture CPU", defaultRatio: 0.5, overrideRatio,
    defaultSource: "uncompensated", matchedModel: null, datasetVersion: "fixture", ratio, multiplier: 1 / ratio };
}
