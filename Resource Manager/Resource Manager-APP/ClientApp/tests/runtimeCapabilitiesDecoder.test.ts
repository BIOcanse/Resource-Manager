import assert from "node:assert/strict";
import { runtimeCapabilitiesDecoder } from "../src/data/runtimeCapabilities/runtimeCapabilitiesDecoder.ts";
import { ResponseDecodeError } from "../src/frontendRuntime/request/ResponseDecoder.ts";

const source = {
  profileId: "production",
  readOnly: false,
  mutablePersistence: true,
  legacyPersistenceImport: false,
  gpuLaunchInterceptionReconciliation: true,
  runtimeEffectOwners: true,
  publicServiceCoordination: false,
  optimizationRuntime: true,
  sharedResourceOwnership: true,
  futureField: "ignored"
};

const decoded = runtimeCapabilitiesDecoder.decode(source);
assert.deepEqual(decoded, {
  profileId: "production",
  readOnly: false,
  mutablePersistence: true,
  legacyPersistenceImport: false,
  gpuLaunchInterceptionReconciliation: true,
  runtimeEffectOwners: true,
  publicServiceCoordination: false,
  optimizationRuntime: true,
  sharedResourceOwnership: true
});
assert.notEqual(decoded, source);
assert.equal(Object.isFrozen(decoded), true);

for (const [value, expectedPath] of [
  [{ ...source, profileId: "" }, "$.profileId"],
  [{ ...source, readOnly: 0 }, "$.readOnly"],
  [{ ...source, mutablePersistence: "true" }, "$.mutablePersistence"],
  [{ ...source, sharedResourceOwnership: undefined }, "$.sharedResourceOwnership"]
] as const) {
  assert.throws(
    () => runtimeCapabilitiesDecoder.decode(value),
    (error: unknown) => error instanceof ResponseDecodeError
      && error.path === expectedPath);
}
