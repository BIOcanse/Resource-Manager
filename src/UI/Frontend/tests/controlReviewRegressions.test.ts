import assert from "node:assert/strict";
import { controlObjectsDecoder, controlStateDecoder, getControlObjectCurve, setControlObjectSettings, retryControlObject, controlHardwareRequestTimeoutMs } from "../src/features/control/controlApi.ts";
import { curveSettingFromRead, initialSettingFor } from "../src/features/control/controlInitialSetting.ts";
import { canConfigureRuntimePlacement } from "../src/components/gpuPlacementTargetOptions.ts";
import type { ControlCapability } from "../src/features/control/controlTypes.ts";

const capability: ControlCapability = {
  id: "fan.duty", valueKind: "number", supported: false,
  unavailableReason: { code: "control-range-fixed" }, unavailableKind: "platform",
  requiredComponentId: null, channel: "fan-core", curvePointLimit: null,
  requiredAccessLevel: "normal", range: { minimum: 0, maximum: 0, step: 1, unit: "%", defaultValue: null }
};
const wire = (item = capability) => ({ readAt: "2026-10-02T00:00:00Z", objects: [{
  id: "fan:0", kind: "fan", reportedName: null, ordinal: 0, platform: { operatingSystem: "windows", vendor: "OEM" },
  capabilities: [item], isControllable: false
}] });
assert.equal(controlObjectsDecoder.decode(wire()).objects[0].capabilities[0].range?.maximum, 0);
assert.throws(() => controlObjectsDecoder.decode(wire({ ...capability, range: { ...capability.range!, minimum: 2 } })));
assert.throws(() => controlObjectsDecoder.decode(wire({ ...capability, range: { ...capability.range!, step: 0 } })));
const adjustable = { ...capability, supported: true, range: { ...capability.range!, maximum: 100 } };
assert.equal(initialSettingFor(adjustable, 47).number, 47);
assert.equal(initialSettingFor(adjustable).number, null, "an unknown actual duty must not become zero");
assert.equal(initialSettingFor({ ...adjustable, range: { ...adjustable.range, defaultValue: 65 } }).number, 65);
assert.equal(initialSettingFor(adjustable, NaN).number, null);
assert.equal(canConfigureRuntimePlacement("unknown"), true);
assert.equal(canConfigureRuntimePlacement("supported"), true);
assert.equal(canConfigureRuntimePlacement("unsupported"), false);
assert.equal(canConfigureRuntimePlacement(undefined), false);

const requests: { key: string; timeoutMs?: number }[] = [];
const client = { request: async <T>(descriptor: { key: string; timeoutMs?: number }): Promise<T> => {
  requests.push(descriptor); return null as T;
} };
await getControlObjectCurve(client, "fan:0");
await setControlObjectSettings(client, "fan:0", []);
assert.deepEqual(requests.map(r => r.timeoutMs), [controlHardwareRequestTimeoutMs, controlHardwareRequestTimeoutMs]);
assert.ok(controlHardwareRequestTimeoutMs >= 60_000);
console.log("control review regressions: ok");

assert.equal(curveSettingFromRead("fan.curve", null, [40, 60, 80], "firmware"), null,
  "Unavailable firmware must not turn into writable fabricated points");
assert.equal(curveSettingFromRead("fan.curve", [], [40, 60, 80], "firmware"), null,
  "An empty firmware table is unavailable, not a zero-speed table");
assert.deepEqual(curveSettingFromRead("fan.curve", [
  { temperatureCelsius: 40, percent: 31 }, { temperatureCelsius: 80, percent: 73 }
], [40, 60, 80], "firmware"), {
  capabilityId: "fan.curve", curveExecution: "firmware", curve: [
    { temperatureCelsius: 40, percent: 31 }, { temperatureCelsius: 60, percent: 52 },
    { temperatureCelsius: 80, percent: 73 }
  ]
}, "Editable points derive only from a confirmed firmware table");

const retryWire = { desired: { objects: [], pendingReleases: [{ objectId: "fan:0", settings: [{ capabilityId: "fan.duty", number: 40 }] }] },
  lastApply: { outcomes: [{ objectId: "fan:0", capabilityId: "fan.duty", status: "failed",
    reason: { code: "control-out-of-range", arguments: { minimum: 20, maximum: 80, unit: "%" } } }], appliedAt: "" } };
const decodedRetry = controlStateDecoder.decode(retryWire);
assert.equal(decodedRetry.desired.pendingReleases?.[0].settings[0].number, 40);
assert.deepEqual(decodedRetry.lastApply.outcomes[0].reason?.arguments, { requiredAccessLevel: null, minimum: 20, maximum: 80, requested: null, observed: null, unit: "%" });
assert.throws(() => controlObjectsDecoder.decode(wire({ ...capability, unavailableReason: "old prose" } as unknown as ControlCapability)));
assert.throws(() => controlStateDecoder.decode({ ...retryWire, lastApply: { ...retryWire.lastApply,
  outcomes: [{ ...retryWire.lastApply.outcomes[0], reason: { code: "control-out-of-range", arguments: { minimum: "20" } } }] } }));
const retryRequests: unknown[] = [];
await retryControlObject({ request: async <T>(descriptor: unknown): Promise<T> => { retryRequests.push(descriptor); return null as T; } }, "fan:0");
assert.equal((retryRequests[0] as { url: string }).url, "/api/control/state/fan%3A0/retry");
assert.deepEqual((retryRequests[0] as { request: unknown }).request, { method: "POST" }, "Explicit retry contains no draft body");
