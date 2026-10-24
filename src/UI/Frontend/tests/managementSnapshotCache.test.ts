import assert from "node:assert/strict";

const storageKey = "resource-manager:management-snapshot:v3";
const capturedAt = "2026-10-24T00:00:00.000Z";

const legacy = snapshot();
legacy.components[0]!.definition!.managementRole = "Dependency" as never;
legacy.components[0]!.state = "InstalledUnverified";
legacy.components[0]!.providers = [{ state: "RuntimeAvailable" }];
legacy.software[0]!.kind = "HighPerformance";
legacy.software[0]!.displayKind = "WindowsApp";
legacy.software[0]!.managementRole = "Support";
legacy.software[0]!.issues = [{ kind: "KnownMalware" }] as never;
legacy.software[1]!.kind = "RuntimeProduct";
legacy.software[1]!.displayKind = "General";
legacy.software[1]!.managementRole = null;
legacy.software[1]!.state = null;

const legacyStorage = memoryStorage(JSON.stringify(legacy));
setLocalStorage(legacyStorage);
const legacyReader = await loadReader("legacy");
const restored = legacyReader.readManagementSnapshotCache();
assert.equal(restored?.components[0]?.definition?.managementRole, "dependency");
assert.equal(restored?.components[0]?.state, "installed_unverified");
assert.equal(restored?.components[0]?.providers?.[0]?.state, "runtime_available");
assert.equal(restored?.software[0]?.kind, "high_performance");
assert.equal(restored?.software[0]?.displayKind, "runtime_package");
assert.equal(restored?.software[0]?.managementRole, "support");
assert.equal(restored?.software[0]?.issues, undefined);
assert.equal(restored?.software[1]?.kind, "runtime_product");
assert.equal(restored?.software[1]?.displayKind, "other");
assert.equal(restored?.software[1]?.managementRole, null);
assert.equal(restored?.software[1]?.state, null);
assert.equal(legacyStorage.writes, 1, "recognized legacy values are rewritten canonically");
const rewritten = JSON.parse(legacyStorage.values.get(storageKey) ?? "null");
assert.equal(rewritten.capturedAt, capturedAt);
assert.equal(rewritten.components[0].definition.managementRole, "dependency");
assert.equal(rewritten.components[0].providers[0].state, "runtime_available");
assert.equal(rewritten.software[0].kind, "high_performance");
assert.equal(rewritten.software[0].displayKind, "runtime_package");
assert.equal("issues" in rewritten.software[0], false);

const canonicalStorage = memoryStorage(JSON.stringify(snapshot()));
setLocalStorage(canonicalStorage);
const canonicalReader = await loadReader("canonical");
canonicalReader.readManagementSnapshotCache();
assert.equal(canonicalStorage.writes, 0, "canonical values are not rewritten");

const unknownCases: Array<[string, (value: ReturnType<typeof snapshot>) => void, string]> = [
  ["component state", value => { value.components[0]!.state = "UnknownState"; }, "component state"],
  ["provider state", value => { value.components[0]!.providers = [{ state: "UnknownProviderState" }]; }, "component provider state"],
  ["component role", value => { value.components[0]!.definition!.managementRole = "UnknownRole" as never; }, "management role"],
  ["software role", value => { value.software[0]!.managementRole = "UnknownRole" as never; }, "management role"],
  ["software kind", value => { value.software[0]!.kind = "UnknownKind"; }, "software kind"],
  ["software display kind", value => { value.software[0]!.displayKind = "UnknownDisplayKind"; }, "software display kind"]
];

for (const [name, mutate, valueSet] of unknownCases) {
  const invalid = snapshot();
  mutate(invalid);
  setLocalStorage(memoryStorage(JSON.stringify(invalid)));
  const reader = await loadReader(`unknown-${name.replaceAll(" ", "-")}`);
  assert.throws(
    () => reader.readManagementSnapshotCache(),
    error => error instanceof Error
      && error.name === "InvalidDataException"
      && new RegExp(`InvalidDataException: unsupported stored ${valueSet}`).test(error.message),
    `${name} must fail loudly instead of disappearing into an empty cache`);
}

const invalidJsonStorage = memoryStorage("not-json");
setLocalStorage(invalidJsonStorage);
const malformedReader = await loadReader("malformed-json");
assert.equal(malformedReader.readManagementSnapshotCache(), null, "malformed cache keeps the existing unavailable behavior");

for (const [index, root] of ["null", "true", "7", "[]"].entries()) {
  setLocalStorage(memoryStorage(root));
  const invalidRootReader = await loadReader(`invalid-root-${index}`);
  assert.equal(invalidRootReader.readManagementSnapshotCache(), null, `invalid root ${root} keeps the existing unavailable behavior`);
}

setLocalStorage({
  values: new Map(),
  writes: 0,
  getItem: () => { throw new Error("storage unavailable"); },
  setItem: () => { throw new Error("storage unavailable"); }
});
const unavailableReader = await loadReader("unavailable-storage");
assert.equal(unavailableReader.readManagementSnapshotCache(), null, "unavailable storage keeps the existing behavior");

console.log("management snapshot cache legacy-value migration tests passed");

function snapshot() {
  return {
    version: 3,
    capturedAt,
    components: [{ definition: {}, state: "installed", providers: [{ state: "missing" }] }],
    software: [
      { id: "software", name: "Software", kind: "game", displayKind: "game", managementRole: "dependency" },
      { id: "nullable", name: "Nullable", kind: "other", displayKind: "other", managementRole: "dependency" }
    ]
  } as unknown as {
    version: number;
    capturedAt: string;
    components: import("../src/types.ts").ManagedComponent[];
    software: import("../src/types.ts").SoftwareRecord[];
  };
}

function memoryStorage(initialValue?: string) {
  const values = new Map<string, string>();
  if (initialValue !== undefined) values.set(storageKey, initialValue);
  return {
    values,
    writes: 0,
    getItem(key: string) { return values.get(key) ?? null; },
    setItem(key: string, value: string) { this.writes++; values.set(key, value); }
  };
}

function setLocalStorage(value: ReturnType<typeof memoryStorage> | {
  values: Map<string, string>;
  writes: number;
  getItem: (key: string) => string | null;
  setItem: (key: string, value: string) => void;
}) {
  Object.defineProperty(globalThis, "localStorage", { configurable: true, value });
}

async function loadReader(name: string) {
  return import(`../src/features/management/managementSnapshotCache.ts?test=${encodeURIComponent(name)}`);
}
