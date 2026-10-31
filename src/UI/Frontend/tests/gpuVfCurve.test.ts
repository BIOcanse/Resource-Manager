import assert from "node:assert/strict";
import { readFileSync } from "node:fs";
import { gpuVfDescriptorDecoder, gpuVfDraftDecoder, gpuVfLibraryDecoder, commandGpuVfDraft, closeGpuVfDraft,
  getGpuVfDescriptor, getGpuVfLibrary, saveGpuVfPreset } from "../src/features/control/gpuVfCurveApi.ts";
import { controlObjectsDecoder, controlStateDecoder, applyControlDesiredState, saveControlPreset, setControlObjectSettings } from "../src/features/control/controlApi.ts";
import { hasPendingChanges } from "../src/features/control/controlStateMachine.ts";
import { initialSettingFor } from "../src/features/control/controlInitialSetting.ts";
import type { ControlSetting } from "../src/features/control/controlTypes.ts";
const binding = { vendor: "nvidia", deviceIdentity: "gpu:stable", model: "Simulated GPU", pointSignature: "native-sequence", scope: "device" as const };
const rules = { fineStepKhz: 15000, coarseStepKhz: 75000, dragQuantizationKhz: 15000, frequencyDecimals: 1, voltageDecimals: 2,
  axisPaddingRatio: .1, hitRadiusPixels: 7, maximumPresetCount: 16, maximumNameLength: 48, maximumDraftCount: 4, undoCapacity: 20,
  defaultBindingScope: "device", safetyMinimumOffsetKhz: null, safetyMaximumOffsetKhz: null, readbackToleranceKhz: 0 };
const hardware = { pointId: 7, voltageMicrovolts: 850000, defaultFrequencyKhz: 1800000, currentFrequencyKhz: 1830000,
  defaultOffsetKhz: 0, currentOffsetKhz: 30000, minimumOffsetKhz: -150000, maximumOffsetKhz: 300000 };
const descriptor = { objectId: "gpu:stable", scalarOnly: false, available: true, writable: false,
  unavailableReason: { code: "nvidia-vf-restore-protocol-unverified" }, binding, points: [hardware], rules, readAt: "2026-10-31T12:00:00Z" };
const point = { pointId: 7, voltageMicrovolts: 850000, frequencyOffsetKhz: 30000 };
const preset = { id: "shared", name: "Shared curve", binding, points: [point], createdAt: descriptor.readAt, updatedAt: descriptor.readAt };
const draft = { id: "draft", objectId: descriptor.objectId, binding, points: [point], descriptor, canUndo: false, canRedo: true };
assert.equal(gpuVfDescriptorDecoder.decode(descriptor).writable, false, "Read-only production data still permits a backend editing draft");
assert.equal(gpuVfDraftDecoder.decode(draft).points[0].pointId, 7, "Native IDs are preserved; array positions are not IDs");
assert.equal(gpuVfLibraryDecoder.decode({ presets: [{ preset, applicable: true, unavailableReason: null }], rules }).presets[0].preset.id, "shared");
const scalar = gpuVfDescriptorDecoder.decode({ ...descriptor, scalarOnly: true, points: [{ ...hardware, pointId: 0, voltageMicrovolts: 0, defaultFrequencyKhz: null, defaultOffsetKhz: null }] });
assert.equal(scalar.points[0].defaultFrequencyKhz, null, "Missing reference frequency remains missing; scalar is not a measured zero-volt point");
assert.throws(() => gpuVfDraftDecoder.decode({ ...draft, points: [{ ...point, voltageMicrovolts: 900000 }] }));
assert.throws(() => gpuVfDraftDecoder.decode({ ...draft, points: [point, point] }));
assert.throws(() => gpuVfDescriptorDecoder.decode({ ...descriptor, rules: { ...rules, dragQuantizationKhz: 0 } }));
assert.throws(() => gpuVfDescriptorDecoder.decode({ ...descriptor, rules: { ...rules, frequencyDecimals: 101 } }));
assert.throws(() => gpuVfDescriptorDecoder.decode({ ...descriptor, available: false, unavailableReason: null }));
assert.throws(() => gpuVfLibraryDecoder.decode({ presets: [{ preset, applicable: false, unavailableReason: null }], rules }));
assert.throws(() => gpuVfDraftDecoder.decode({ ...draft, binding: { ...binding, scope: "automatic" } }));
const capability = { id: "gpu.core-vf-curve", valueKind: "gpu-vf-curve-reference", supported: false,
  unavailableReason: descriptor.unavailableReason, unavailableKind: "not-implemented", requiredComponentId: null, range: null, channel: "nvapi", requiredAccessLevel: "normal", curvePointLimit: null };
const objects = controlObjectsDecoder.decode({ objects: [{ id: descriptor.objectId, kind: "gpu", reportedName: null, platform: { vendor: "nvidia", operatingSystem: "windows" },
  capabilities: [capability], detail: null, gpuAttachment: "discrete", terms: [], isControllable: true }], readAt: descriptor.readAt });
assert.equal(objects.objects[0].capabilities[0].valueKind, "gpu-vf-curve-reference");
assert.deepEqual(initialSettingFor(objects.objects[0].capabilities[0], null), { capabilityId: capability.id, gpuVfCurvePresetId: null });
const accepted = { capabilityId: capability.id, gpuVfCurvePresetId: preset.id, gpuVfCurveExecution: { binding, points: [point] } };
const state = controlStateDecoder.decode({ desired: { objects: [{ objectId: descriptor.objectId, settings: [accepted] }] }, lastApply: { outcomes: [], appliedAt: descriptor.readAt } });
assert.deepEqual(state.desired.objects[0].settings[0].gpuVfCurveExecution?.points, [point], "Accepted execution input is decoded strictly from state");
assert.throws(() => controlStateDecoder.decode({ desired: { objects: [{ objectId: descriptor.objectId, settings: [{ ...accepted, gpuVfCurveExecution: { binding, points: [{ ...point, frequencyOffsetKhz: "30" }] } }] }] }, lastApply: { outcomes: [], appliedAt: descriptor.readAt } }));
assert.equal(hasPendingChanges([{ capabilityId: capability.id, gpuVfCurvePresetId: "a" }], [{ capabilityId: capability.id, gpuVfCurvePresetId: "b" }]), true);
assert.equal(hasPendingChanges([accepted], [{ capabilityId: capability.id, gpuVfCurvePresetId: preset.id }]), false, "Accepted backend execution is not a browser edit");
assert.equal(hasPendingChanges([accepted], state.desired.objects[0].settings), false, "Nullable unused wire fields cannot turn an unchanged curve reference into an edit");
const requests: { url: string; request?: { body?: string; method?: string } }[] = [];
const client = { request: async <T>(request: { url: string; request?: { body?: string; method?: string } }): Promise<T> => { requests.push(request); return null as T; } };
await getGpuVfDescriptor(client, "gpu:stable"); await getGpuVfLibrary(client, "gpu:stable");
await commandGpuVfDraft(client, { operation: "uniform-offset", objectId: descriptor.objectId, draftId: "draft", expectedBinding: binding, pointIds: [7], offsetKhz: 45000 });
await closeGpuVfDraft(client, { operation: "close", objectId: descriptor.objectId, draftId: "draft", expectedBinding: binding });
await saveGpuVfPreset(client, "new", { objectId: descriptor.objectId, name: "Shared", draftId: "draft", expectedBinding: binding });
assert.equal(requests[0].url, "/api/control/objects/gpu%3Astable/gpu-vf-curve");
assert.deepEqual(JSON.parse(requests[2].request!.body!), { operation: "uniform-offset", objectId: descriptor.objectId, draftId: "draft", expectedBinding: binding, pointIds: [7], offsetKhz: 45000 });
assert.equal(requests.some(request => request.url.startsWith("/api/control/state")), false, "Library and editor operations never apply hardware");
requests.length = 0;
await applyControlDesiredState(client, [{ objectId: descriptor.objectId, settings: [accepted] }]);
await saveControlPreset(client, descriptor.objectId, "GPU", [accepted]); await setControlObjectSettings(client, descriptor.objectId, [accepted]);
for (const request of requests) assert.equal(request.request!.body!.includes("gpuVfCurveExecution"), false, "Internal accepted execution is never sent back as browser input");
const fan = readFileSync(new URL("../src/features/control/FanCurveChart.tsx", import.meta.url), "utf8");
const gpu = readFileSync(new URL("../src/features/control/GpuVfCurveEditor.tsx", import.meta.url), "utf8");
assert.ok(fan.includes("<FixedAxisCurveChart") && gpu.includes("<FixedAxisCurveChart"), "Fan and GPU consume the same plot and point interaction");
console.log("GPU V/F curve decoders, state references and command routing passed");
