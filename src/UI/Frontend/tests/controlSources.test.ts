import assert from "node:assert/strict";
import { createControlSources } from "../src/data/control/controlSources.ts";
import { BackendSessionOwner } from "../src/frontendRuntime/session/BackendSessionOwner.ts";
import { RequestClient } from "../src/frontendRuntime/request/RequestClient.ts";
import { SourceRegistry } from "../src/frontendRuntime/source/SourceRegistry.ts";
import type { SourceHandle } from "../src/frontendRuntime/source/SourceDescriptor.ts";

const session = new BackendSessionOwner({ transport: null, allowStandalone: true });
const registry = new SourceRegistry(session);
let fail = false;
let hold: Promise<void> | undefined;
let release = () => {};
let calls = 0;
const responses: Record<string, unknown> = {
  "/api/control/objects": { objects: [], readAt: "2026-10-27T00:00:00Z" },
  "/api/control/state": { desired: { objects: [] }, lastApply: { outcomes: [], appliedAt: "2026-10-27T00:00:00Z" } },
  "/api/control/instances": { instances: [], readAt: "2026-10-27T00:00:00Z" },
  "/api/control/presets": { presets: [] },
  "/api/control/overclock-consent": { accepted: false },
  "/api/control/access-level": { level: "normal" },
  "/api/control/notice": { acknowledged: true }
};
const client = new RequestClient(session, { transport: async (url, options) => {
  calls++;
  assert.equal(options.method, "GET");
  if (hold) await hold;
  if (fail) throw new Error("new accepted failure");
  assert(url in responses, url);
  return responses[url];
} });
try {
  const sources = createControlSources(registry, client);
  for (const source of Object.values(sources) as SourceHandle<unknown>[]) {
    let lease = source.acquire({ active: false });
    const accepted = await lease.refresh();
    assert.equal(accepted.status, "ready", `${source.key}: ${String(accepted.error)}`);
    lease.release();
    const count = calls;
    lease = source.acquire({ active: false });
    assert.strictEqual(lease.snapshot, accepted, `${source.key}: remount reads immediately`);
    assert.equal(calls, count, "read does not sample");
    hold = new Promise<void>(resolve => { release = resolve; });
    lease.setDemand({ active: true });
    const pending = lease.refresh();
    assert.strictEqual(lease.snapshot, accepted, "reacquisition never publishes loading");
    lease.setDemand({ active: false });
    assert.strictEqual(lease.snapshot, accepted, "hiding retains complete snapshot");
    release(); hold = undefined; await pending;
    fail = true;
    assert.equal((await lease.refresh()).data, null, "failure replaces current data");
    assert.equal(lease.snapshot.status, "error");
    fail = false;
    await lease.refresh();
    hold = new Promise<void>(resolve => { release = resolve; });
    const oldRead = lease.refresh();
    const command = lease.acceptAuthoritative(accepted.data);
    release(); hold = undefined; await oldRead;
    assert.strictEqual(lease.snapshot, command, "old read cannot overwrite command response");
    lease.release();
  }
} finally { registry.dispose(); session.dispose(); }
console.log("Control sources: synchronous remount, visibility retention, failure replacement and command fence passed.");
