import assert from "node:assert/strict";
import { createSettingsReadSources, productVersionCatalogDecoder, refreshAccessKeysAfterCommand } from "../src/data/settingsReadSources.ts";
import { reportRules } from "./fixtures/reportRuleSettings.ts";
import { SourceRegistry } from "../src/frontendRuntime/source/SourceRegistry.ts";
import { acceptSourceCommand } from "../src/frontendRuntime/source/acceptSourceCommand.ts";
import type { SourceHandle } from "../src/frontendRuntime/source/SourceDescriptor.ts";
import { RequestClient } from "../src/frontendRuntime/request/RequestClient.ts";
import { BackendSessionOwner } from "../src/frontendRuntime/session/BackendSessionOwner.ts";
import { ApiRequestError } from "../src/api/httpTransport.ts";
import { TrustedAccessRequestError } from "../src/features/trustedAccess/trustedAccessApi.ts";
import type { AccessKeySummary } from "../src/features/trustedAccess/trustedAccessTypes.ts";

const session = new BackendSessionOwner({ transport: null, allowStandalone: true });
const registry = new SourceRegistry(session);
let result: unknown;
let failure: unknown = null;
let hold: Promise<void> | null = null;
let finish = () => {};
const calls: string[] = [];
const client = new RequestClient(session, { transport: async (url, options) => {
  assert.equal(options.method, "GET"); calls.push(url);
  const value = result;
  if (hold) await hold;
  if (failure) throw failure;
  return value;
} });
const sources = createSettingsReadSources(registry, client);
const key = (id: string): AccessKeySummary => ({ id, name: id, purpose: "control", scopes: ["read"],
  createdAt: "2026-10-27T00:00:00Z", isDefault: false });
const keys = { keys: [key("one")], defaultEnabled: false, defaultKeyId: null, availableScopes: ["read"], administratorHolderScopes: ["machine.write", "injection.configure"] };
const versions = { installedVersion: "1", status: "loaded", complete: true, checkedAt: "2026-10-27T00:00:00Z",
  options: [{ choice: "latest", version: "2", assetName: "fixture", downloadUrl: "https://example.invalid/fixture",
    checksumUrl: "https://example.invalid/checksum", selectable: true, eligibility: "eligible" }] };
const tick = async () => { for (let i = 0; i < 12; i++) await Promise.resolve(); };

try {
  for (const [handle, value, empty, path] of [
    [sources.reportRules, { rules: reportRules }, { rules: [] }, "/api/settings/report-rules"],
    [sources.accessKeys, keys, { ...keys, keys: [] }, "/api/access-keys"],
    [sources.productVersions, versions, { ...versions, options: [] }, "/api/updates/product/versions"]
  ] as const) {
    const source = handle as SourceHandle<unknown>;
    result = value;
    const start = calls.length;
    const a = source.acquire({ active: false }); const b = source.acquire({ active: false });
    assert.equal(a.snapshot.status, "idle"); assert.equal(calls.length, start);
    await Promise.all([a.refresh(), b.refresh()]);
    assert.equal(calls.length, start + 1, "Consumers share one source request");
    assert.equal(calls.at(-1), path);
    const accepted = a.snapshot;
    a.release(); b.release();
    const returning = source.acquire({ active: false });
    assert.strictEqual(returning.snapshot, accepted, "Return first read is the accepted owner value");
    assert.equal(calls.length, start + 1, "Reading a retained value does not issue a GET");
    hold = new Promise(resolve => { finish = resolve; });
    returning.setDemand({ active: true, refreshIntervalMs: null });
    const refresh = returning.refresh(); await tick();
    assert.strictEqual(returning.snapshot, accepted, "Refresh start does not clear an accepted value");
    returning.setDemand({ active: false, refreshIntervalMs: null });
    assert.strictEqual(returning.snapshot, accepted, "Hidden demand does not clear an accepted value");
    finish(); hold = null; await refresh;
    assert.strictEqual(returning.snapshot, accepted, "A canceled hidden read cannot overwrite the retained owner value");
    failure = new Error("fixture failure"); await returning.refresh();
    assert.equal(returning.snapshot.status, "error");
    assert.equal(returning.snapshot.data, null, "Failure replaces old good data");
    failure = null; result = empty; await returning.refresh();
    assert.equal(returning.snapshot.status, "ready");
    assert.deepEqual(returning.snapshot.data, empty, "A successful zero-row response is accepted as empty");
    returning.release();
  }

  result = keys;
  const consumer = sources.accessKeys.acquire({ active: false }); await consumer.refresh();
  let complete!: (value: AccessKeySummary) => void;
  const command = acceptSourceCommand(sources.accessKeys,
    () => new Promise<AccessKeySummary>(resolve => { complete = resolve; }),
    (created, current) => current.data ? { ...current.data, keys: [...current.data.keys, created] } : undefined);
  consumer.acceptAuthoritative({ ...keys, keys: [key("one"), key("concurrent")] });
  consumer.release(); complete(key("created")); await command;
  const returned = sources.accessKeys.acquire({ active: false });
  assert.deepEqual(returned.snapshot.data?.keys.map(item => item.id), ["one", "concurrent", "created"],
    "Command acceptance uses owner value at completion and survives consumer disposal");

  failure = new ApiRequestError("scope failure", { kind: "http", status: 403, payload: { error: "access-scope-required" }, retryable: false });
  await returned.refresh();
  assert.equal(returned.snapshot.data, null);
  assert(returned.snapshot.error instanceof TrustedAccessRequestError);
  assert.equal(returned.snapshot.error.reason, "scope-required", "RequestClient reads retain the established failure classification");
  failure = null; returned.release();

  let finishDefault!: () => void;
  const defaultCommand = refreshAccessKeysAfterCommand(sources.accessKeys,
    () => new Promise<void>(resolve => { finishDefault = resolve; }));
  result = { ...keys, keys: [key("after-default")] };
  finishDefault(); await defaultCommand;
  const afterDefault = sources.accessKeys.acquire({ active: false });
  assert.equal(afterDefault.snapshot.data?.keys[0].id, "after-default",
    "Default-key command refresh completes without a mounted consumer");
  afterDefault.release();

  assert.throws(() => productVersionCatalogDecoder.decode({ ...versions, checkedAt: "2026-10-27T12:00:00-05:00" }));
  result = { ...versions, checkedAt: "invalid" };
  const invalidCatalog = sources.productVersions.acquire({ active: false }); await invalidCatalog.refresh();
  assert.equal(invalidCatalog.snapshot.status, "error"); assert.equal(invalidCatalog.snapshot.data, null);
  invalidCatalog.release();
} finally {
  finish(); registry.dispose(); session.dispose();
}
console.log("Settings source owners: warm returns, shared reads, pending retention, empty/failure replacement, command lifetime and original validation passed.");
