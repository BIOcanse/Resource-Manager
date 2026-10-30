import assert from "node:assert/strict";
import { runtimeCapabilitiesDecoder } from "../src/data/runtimeCapabilities/runtimeCapabilitiesDecoder.ts";
import { ResponseDecodeError } from "../src/frontendRuntime/request/ResponseDecoder.ts";

const source = {
  profileId: "production",
  readOnly: false,
  mutablePersistence: true,
  gpuLaunchInterceptionReconciliation: true,
  runtimeEffectOwners: true,
  publicServiceCoordination: false,
  optimizationRuntime: true,
  futureField: "ignored"
};

const decoded = runtimeCapabilitiesDecoder.decode(source);
assert.deepEqual(decoded, {
  profileId: "production",
  readOnly: false,
  mutablePersistence: true,
  gpuLaunchInterceptionReconciliation: true,
  runtimeEffectOwners: true,
  publicServiceCoordination: false,
  optimizationRuntime: true
});
assert.notEqual(decoded, source);
assert.equal(Object.isFrozen(decoded), true);

for (const [value, expectedPath] of [
  [{ ...source, profileId: "" }, "$.profileId"],
  [{ ...source, readOnly: 0 }, "$.readOnly"],
  [{ ...source, mutablePersistence: "true" }, "$.mutablePersistence"],
  [{ ...source, optimizationRuntime: undefined }, "$.optimizationRuntime"]
] as const) {
  assert.throws(
    () => runtimeCapabilitiesDecoder.decode(value),
    (error: unknown) => error instanceof ResponseDecodeError
      && error.path === expectedPath);
}
