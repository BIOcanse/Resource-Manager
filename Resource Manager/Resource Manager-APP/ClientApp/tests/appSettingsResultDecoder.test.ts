import assert from "node:assert/strict";
import {
  appSettingsResultDecoder
} from "../src/data/appSettings/appSettingsResultDecoder.ts";
import {
  ResponseDecodeError
} from "../src/frontendRuntime/request/ResponseDecoder.ts";

const source = {
  settings: {
    version: "1.0.23",
    appearance: {
      theme: "system",
      futureNestedValue: { enabled: true }
    },
    futureSection: { retained: true }
  },
  revision: "revision-a",
  runtimeApplicationDisposition: "committedAndApplied",
  runtimePlanVersion: 3,
  runtimePublicationSequence: 4,
  runtimeDeliveryFailureCount: 0,
  runtimeCapabilityConstrainedPaths: ["performance.example"],
  runtimeFailureCode: null,
  futureEnvelopeField: "ignored"
};

const decoded = appSettingsResultDecoder.decode(source);
assert.equal(decoded.revision, "revision-a");
assert.equal(decoded.settings.version, "1.0.23");
assert.deepEqual(
  (decoded.settings as unknown as Record<string, unknown>).futureSection,
  { retained: true });
assert.equal(Object.isFrozen(decoded), true);
assert.equal(Object.isFrozen(decoded.settings), true);
assert.equal(Object.isFrozen(decoded.settings.appearance), true);
assert.equal(Object.isFrozen(decoded.runtimeCapabilityConstrainedPaths), true);

for (const [value, expectedPath] of [
  [{ ...source, settings: null }, "$.settings"],
  [{ ...source, settings: { ...source.settings, version: "" } }, "$.settings.version"],
  [{ ...source, revision: "" }, "$.revision"],
  [{ ...source, settings: { ...source.settings, appearance: [] } }, "$.settings.appearance"],
  [{ ...source, runtimeApplicationDisposition: "maybe" }, "$.runtimeApplicationDisposition"],
  [{ ...source, runtimeDeliveryFailureCount: -1 }, "$.runtimeDeliveryFailureCount"]
] as const) {
  assert.throws(
    () => appSettingsResultDecoder.decode(value),
    (error: unknown) => error instanceof ResponseDecodeError
      && error.path === expectedPath);
}
