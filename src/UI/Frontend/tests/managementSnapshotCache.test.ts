import assert from "node:assert/strict";
import type { SoftwareRecord } from "../src/types.ts";

const key = "resource-manager:management-snapshot:v4";
const current = { version: 4, capturedAt: "2026-10-24T00:00:00.000Z", components: [], software: [software()] };

const storage = memoryStorage(JSON.stringify(current));
setStorage(storage);
const reader = await loadReader("current");
assert.equal(reader.readManagementSnapshotCache()?.software[0]?.name, "Reported software");
assert.equal(reader.readManagementSnapshotCache()?.software[0]?.issues, undefined);
assert.equal(storage.writes, 0);
reader.writeManagementSnapshotCache([], [software()]);
assert.equal(storage.writes, 0, "Equivalent facts do not rewrite the cache");
reader.writeManagementSnapshotCache([], [{ ...software(), name: "Changed software" }]);
assert.equal(storage.writes, 1);
assert.equal(JSON.parse(storage.values.get(key)!).version, 4);
assert.equal("issues" in JSON.parse(storage.values.get(key)!).software[0], false);

const oldStorage = memoryStorage();
oldStorage.values.set("resource-manager:management-snapshot:v3", JSON.stringify({ ...current, version: 3 }));
setStorage(oldStorage);
assert.equal((await loadReader("old-key")).readManagementSnapshotCache(), null);
setStorage(memoryStorage(JSON.stringify({ ...current, version: 3 })));
assert.equal((await loadReader("old-schema")).readManagementSnapshotCache(), null);

for (const field of ["message", "messageCode", "displayKind", "sources"]) {
  setStorage(memoryStorage(JSON.stringify({ ...current, software: [{ ...software(), [field]: "old text" }] })));
  const invalidReader = await loadReader(`removed-${field}`);
  assert.throws(() => invalidReader.readManagementSnapshotCache(), /removed presentation field/);
}
for (const [index, raw] of ["not-json", "null", "true", "7", "[]"].entries()) {
  setStorage(memoryStorage(raw));
  assert.equal((await loadReader(`malformed-${index}`)).readManagementSnapshotCache(), null);
}
setStorage({ ...memoryStorage(), getItem() { throw new Error("unavailable"); } });
assert.equal((await loadReader("unavailable")).readManagementSnapshotCache(), null);
console.log("Management cache v4 preserves typed facts and rejects old text schemas.");

function software(): SoftwareRecord {
  return {
    id: "software", name: "Reported software", kind: "game", state: "installed", sourceFacts: [],
    rootPaths: [], requiresRootPathConfirmation: false, identityConfirmed: true,
    operations: { canUninstall: false, uninstallKind: "None", uninstallConstraint: "missing_uninstall_entry" },
    issues: [{ kind: "KnownMalware" }] as never
  };
}
function memoryStorage(value?: string) {
  const values = new Map<string, string>();
  if (value !== undefined) values.set(key, value);
  return {
    values, writes: 0,
    getItem(key: string) { return values.get(key) ?? null; },
    setItem(key: string, value: string) { this.writes++; values.set(key, value); }
  };
}
function setStorage(value: ReturnType<typeof memoryStorage>) {
  Object.defineProperty(globalThis, "localStorage", { configurable: true, value });
}
function loadReader(name: string) {
  return import(`../src/features/management/managementSnapshotCache.ts?test=${name}`);
}
