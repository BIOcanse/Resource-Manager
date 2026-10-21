import assert from "node:assert/strict";
import { controlObjectsDecoder, getControlObjectCurve, setControlObjectSettings, controlHardwareRequestTimeoutMs } from "../src/features/control/controlApi.ts";
import { initialSettingFor } from "../src/features/control/controlInitialSetting.ts";
import { canConfigureRuntimePlacement } from "../src/components/gpuPlacementTargetOptions.ts";
import type { ControlCapability } from "../src/features/control/controlTypes.ts";

const capability: ControlCapability = {
  id: "fan.duty", label: "Fan duty", valueKind: "number", supported: false,
  unavailableReason: "Fixed range", unavailableKind: "platform",
  requiredComponentId: null, requiredComponentName: null, channel: "fan-core",
  requiredAccessLevel: "normal", range: { minimum: 0, maximum: 0, step: 1, unit: "%", defaultValue: null }
};
const wire = (item = capability) => ({ readAt: "2026-10-02T00:00:00Z", objects: [{
  id: "fan:0", kind: "fan", displayName: "Fan", platform: { operatingSystem: "windows", vendor: "OEM" },
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
