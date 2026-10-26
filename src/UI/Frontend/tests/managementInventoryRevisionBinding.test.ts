import assert from "node:assert/strict";
import { createRoot, createSignal } from "solid-js";
import { useManagementInventory } from "../src/features/management/useManagementInventory.ts";
import { createFrontendSources } from "../src/frontendRuntime/source/FrontendSources.ts";
import { SourceRegistry } from "../src/frontendRuntime/source/SourceRegistry.ts";
import { RequestClient } from "../src/frontendRuntime/request/RequestClient.ts";
import { BackendSessionOwner } from "../src/frontendRuntime/session/BackendSessionOwner.ts";
import { BackendSubscriptionChannel } from "../src/frontendRuntime/push/BackendSubscriptionChannel.ts";
import type { ManagementInventoryRevisionSnapshot } from "../src/data/managementInventoryRevision.ts";
import type { SourceSnapshot } from "../src/frontendRuntime/source/SourceSnapshot.ts";
import type { OperationInvalidation } from "../src/frontendRuntime/operations/OperationRegistry.ts";
const session = new BackendSessionOwner({ transport: null, allowStandalone: true });
const registry = new SourceRegistry(session);
const channel = new BackendSubscriptionChannel({ backendSession: session });
const calls: string[] = [];
const client = new RequestClient(session, { transport: async url => {
  calls.push(url);
  return url.startsWith("/api/browser-runtimes") ? { sharedRuntime: null, browserFallback: null, candidates: [], installComponentId: "shared-webview2-runtime", capturedAt: "2026-10-26T00:00:00Z" } : [];
} });
let revisionListener: ((snapshot: SourceSnapshot<ManagementInventoryRevisionSnapshot>) => void) | null = null;
let invalidationListener: ((event: OperationInvalidation) => void) | null = null;
let revisions = { softwareRevision: "0", componentRevision: "0", browserRuntimeRevision: "0" };
const publish = () => revisionListener?.({ key: "revision", status: "ready", data: revisions, error: null, backendEpoch: "standalone", revision: 1, acceptedAttempt: 1, domainRevision: null, capturedAt: null });
const state = createRoot(dispose => {
  const [inventoryActive, setInventoryActive] = createSignal(false);
  const [browserActive, setBrowserActive] = createSignal(false);
  const sources = { ...createFrontendSources(client, registry, channel), managementInventoryRevision: {
    key: "revision", readSnapshot: () => ({ key: "revision", status: "ready" as const, data: revisions, error: null, backendEpoch: "standalone", revision: 1, acceptedAttempt: 1, domainRevision: null, capturedAt: null }), subscribeSnapshot(listener: typeof revisionListener) { revisionListener = listener; publish(); return () => { revisionListener = null; }; }
  } };
  const view = useManagementInventory({ sources, inventoryActive, browserRuntimesActive: browserActive, mutablePersistenceEnabled: () => true,
    operations: { subscribeInvalidations(listener) { invalidationListener = listener; return () => { invalidationListener = null; }; } } });
  return { dispose, setInventoryActive, setBrowserActive, view };
});
const settle = () => new Promise(resolve => setTimeout(resolve, 0));
const count = (path: string) => calls.filter(url => url === path).length;
const invalidate = (targets: OperationInvalidation["targets"]) => invalidationListener?.({ operationId: "fixture", kind: "fixture", domainKey: null, targets });
try {
  await settle(); assert.equal(calls.length, 0); assert.equal(state.view.componentsObservation().status, "loading");
  state.setInventoryActive(true); await settle();
  assert.equal(count("/api/components"), 1); assert.equal(count("/api/software"), 1, "baseline does not duplicate demand read");
  assert.equal(state.view.componentsObservation().status, "ready"); assert.deepEqual(state.view.components(), []);
  revisions = { ...revisions, componentRevision: "1", softwareRevision: "1" }; publish(); await settle();
  assert.equal(count("/api/components"), 2); assert.equal(count("/api/software"), 2);
  invalidate(["components", "software", "dependencies"]); await settle();
  assert.equal(count("/api/components"), 3); assert.equal(count("/api/software"), 3); assert.equal(count("/api/browser-runtimes"), 0);
  state.setInventoryActive(false); state.setBrowserActive(true); await settle();
  assert.equal(count("/api/browser-runtimes"), 1); assert.equal(count("/api/components"), 4);
  revisions = { ...revisions, componentRevision: "2", softwareRevision: "2", browserRuntimeRevision: "1" }; publish(); await settle();
  assert.equal(count("/api/browser-runtimes"), 2); assert.equal(count("/api/components"), 5); assert.equal(count("/api/software"), 3);
  invalidate(["dependencies"]); await settle(); assert.equal(count("/api/browser-runtimes"), 3);
  state.setBrowserActive(false); await settle(); const before = calls.length;
  invalidate(["components", "software", "dependencies"]); publish(); await settle(); assert.equal(calls.length, before);
  assert.equal(revisionListener, null);
} finally { state.dispose(); channel.dispose(); registry.dispose(); session.dispose(); }
assert.equal(invalidationListener, null);
console.log("Management revision and operation invalidations: real source reads and demand lifecycle passed.");
