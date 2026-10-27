import assert from "node:assert/strict";
import { createLoginStartupSource } from "../src/data/loginStartup/loginStartupSource.ts";
import { SourceRegistry } from "../src/frontendRuntime/source/SourceRegistry.ts";
import { BackendSessionOwner } from "../src/frontendRuntime/session/BackendSessionOwner.ts";

const session = new BackendSessionOwner({ transport: null, allowStandalone: true });
const registry = new SourceRegistry(session);
let value = true, fail = false, calls = 0;
let hold: Promise<void> | undefined;
let release = () => {};
const source = createLoginStartupSource(registry, async () => {
  calls++;
  if (hold) await hold;
  if (fail) throw new Error("native read failure");
  return value;
});
try {
  let lease = source.acquire({ active: false });
  assert.equal(calls, 0);
  await lease.refresh();
  const accepted = lease.snapshot;
  lease.release();
  lease = source.acquire({ active: false });
  assert.strictEqual(lease.snapshot, accepted, "Section re-entry reads the native current value synchronously");
  hold = new Promise<void>(resolve => { release = resolve; });
  lease.setDemand({ active: true });
  const reading = lease.refresh();
  assert.strictEqual(lease.snapshot, accepted, "Refreshing does not clear the confirmed switch");
  lease.setDemand({ active: false });
  assert.strictEqual(lease.snapshot, accepted, "Hidden retains the complete native value");
  release(); hold = undefined; await reading;
  value = false;
  assert.equal((await lease.refresh()).data, false, "Native false replaces true");
  fail = true;
  assert.equal((await lease.refresh()).status, "error");
  assert.equal(lease.snapshot.data, null, "Native failure replaces its current value");
  lease.release();
} finally { release(); registry.dispose(); session.dispose(); }
console.log("Login startup source: synchronous return, hidden retention, explicit false and failure passed.");
