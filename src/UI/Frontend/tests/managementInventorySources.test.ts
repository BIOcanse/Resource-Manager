import assert from "node:assert/strict";
import { managementComponentsDecoder, managementSoftwareDecoder, managementBrowserRuntimesDecoder } from "../src/data/managementInventory.ts";
import { createFrontendSources } from "../src/frontendRuntime/source/FrontendSources.ts";
import { SourceRegistry } from "../src/frontendRuntime/source/SourceRegistry.ts";
import { RequestClient } from "../src/frontendRuntime/request/RequestClient.ts";
import { BackendSessionOwner } from "../src/frontendRuntime/session/BackendSessionOwner.ts";
import { BackendSubscriptionChannel } from "../src/frontendRuntime/push/BackendSubscriptionChannel.ts";

const component = { definition: { id: "fixture", vendor: "Fixture", sourcePageUrl: "https://example.invalid", externalTermsUrl: null,
  managementRole: "dependency", isBundled: false, requiresElevation: false, requiresExternalTermsAcknowledgement: false, capabilities: [] },
  state: "missing", installerSourceKind: "manual", message: { domain: 1, code: 15, args: [] }, installRoot: "", installerDirectory: "", installerPath: null,
  canDownload: false, canInstall: false, canVerify: false, installerAvailable: false, installed: false, providerActive: false, providers: [] };
const software = { id: "manual:fixture", name: "Fixture", kind: "other", state: "manual", sourceFacts: [{ kind: "manual" }],
  registrationFacts: { kind: "manual", processCount: 0, serviceCount: 0 }, requiresRootPathConfirmation: false, identityConfirmed: true, rootPaths: [],
  operations: { canUninstall: false, uninstallKind: "None", uninstallConstraint: "manual_registration" } };
const runtime = { id: "runtime", name: "Runtime", kind: "web_view2_runtime", version: "1", executablePath: "runtime.exe", runtimeDirectory: "runtime",
  source: "managed", nativeWebView2Compatible: true, selected: true };
const emptyRuntime = { sharedRuntime: null, browserFallback: null, candidates: [], installComponentId: "shared-webview2-runtime", capturedAt: "2026-10-26T00:00:00Z" };
const runtimeSnapshot = { ...emptyRuntime, sharedRuntime: runtime, candidates: [runtime] };
assert.equal(managementComponentsDecoder.decode([component])[0].definition.id, "fixture");
assert.equal(managementSoftwareDecoder.decode([software])[0].id, "manual:fixture");
assert.deepEqual(managementBrowserRuntimesDecoder.decode(runtimeSnapshot), runtimeSnapshot);
for (const decoder of [managementComponentsDecoder, managementSoftwareDecoder]) {
  assert.deepEqual(decoder.decode([]), []);
  for (const invalid of [null, {}, [{}]]) assert.throws(() => decoder.decode(invalid));
}
for (const invalid of [null, {}, { ...emptyRuntime, capturedAt: "invalid" },
  { ...runtimeSnapshot, candidates: [{ ...runtime, selected: "yes" }] },
  { ...runtimeSnapshot, sharedRuntime: { ...runtime, kind: "unknown" } }]) {
  assert.throws(() => managementBrowserRuntimesDecoder.decode(invalid));
}
const session = new BackendSessionOwner({ transport: null, allowStandalone: true });
const channel = new BackendSubscriptionChannel({ backendSession: session });
const registry = new SourceRegistry(session);
let result: unknown;
let fail = false;
let hold: Promise<void> | null = null;
let release = () => {};
const calls: string[] = [];
let lastSignal: AbortSignal | undefined;
const client = new RequestClient(session, { transport: async (url, options) => {
  calls.push(url); lastSignal = options.signal;
  assert.equal(options.method, "GET");
  if (hold) await hold;
  if (fail) throw new Error("fixture read failure");
  return result;
} });
const tick = async () => { for (let i = 0; i < 12; i++) await Promise.resolve(); };
try {
  const sources = createFrontendSources(client, registry, channel);
  for (const [source, value, empty, path] of [
    [sources.managementComponents, [component], [], "/api/components"],
    [sources.managementSoftware, [software], [], "/api/software"],
    [sources.managementBrowserRuntimes, runtimeSnapshot, emptyRuntime, "/api/browser-runtimes"]
  ] as const) {
    const start = calls.length;
    const a = source.acquire({ active: false }); const b = source.acquire({ active: false });
    result = value;
    assert.equal(a.snapshot.status, "idle"); assert.equal(calls.length, start);
    a.setDemand({ active: true }); b.setDemand({ active: true });
    await Promise.all([a.refresh(), b.refresh()]);
    assert.equal(calls.length, start + 1, "demand and consumers share one read");
    assert.equal(calls.at(-1), path); assert.equal(a.snapshot.status, "ready");
    a.release();
    result = empty;
    assert.deepEqual((await b.refresh()).data, empty, "accepted empty is distinct from pending");
    result = value; await b.refresh(); fail = true;
    const failed = await b.refresh(); assert.equal(failed.status, "error"); assert.equal(failed.data, null);
    fail = false;
    await b.refresh();
    hold = new Promise<void>(resolve => { release = resolve; });
    const normal = b.refresh(); await tick();
    const beforeInvalidation = calls.length;
    const invalidations = [b.invalidate(), b.invalidate()];
    release(); hold = null;
    await Promise.all([normal, ...invalidations]);
    assert.equal(calls.length, beforeInvalidation + 1, "in-flight invalidations drain exactly one subsequent read");
    hold = new Promise<void>(resolve => { release = resolve; });
    const oldRead = b.refresh(); await tick();
    const beforeCommand = calls.length;
    const commandRefresh = b.refresh({ afterCurrent: true });
    release(); hold = null; await Promise.all([oldRead, commandRefresh]);
    assert.equal(calls.length, beforeCommand + 1, "command completion reads after the preceding request");
    if (path !== "/api/components") {
      hold = new Promise<void>(resolve => { release = resolve; });
      const ordinary = b.refresh(); await tick();
      const beforeForce = calls.length;
      const forced = [b.refresh({ force: true }), b.refresh({ force: true })];
      release(); hold = null;
      await Promise.all([ordinary, ...forced]);
      assert.equal(calls.length, beforeForce + 1); assert.equal(calls.at(-1), path + "?refresh=true");
    }
    hold = new Promise<void>(resolve => { release = resolve; });
    const reading = b.refresh(); await tick();
    const invalidated = b.invalidate();
    const beforeHidden = calls.length;
    b.setDemand({ active: false }); assert.equal(lastSignal?.aborted, true);
    release(); hold = null; await Promise.all([reading, invalidated]);
    assert.equal(calls.length, beforeHidden, "hidden source drops queued invalidation");
    assert.equal(b.snapshot.status, "ready", "withdrawal preserves the accepted snapshot");
    b.release();
  }
} finally { channel.dispose(); registry.dispose(); session.dispose(); }
console.log("Management sources: decode, demand, shared flight, empty, failure replacement, invalidation and force passed.");
