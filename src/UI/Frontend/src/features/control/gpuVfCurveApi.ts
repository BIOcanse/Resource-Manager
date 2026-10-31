import { defineResponseDecoder, requireArray, requireBoolean, requireFiniteNumber, requireNonEmptyString,
  requireOneOf, requireRecord, requireString, ResponseDecodeError } from "../../frontendRuntime/request/ResponseDecoder.ts";
import type { RequestClient } from "../../frontendRuntime/request/RequestClient.ts";
import { uiText } from "../../text.ts";
import { readControlReason, controlHardwareRequestTimeoutMs } from "./controlApi.ts";
import type { GpuVfCurveBinding, GpuVfCurvePoint, GpuVfCurveHardwarePoint, GpuVfCurveEditorRules,
  GpuVfCurveDescriptor, GpuVfCurvePreset, GpuVfCurveLibrary, GpuVfCurveDraft, GpuVfCurveCommand } from "./gpuVfCurveTypes.ts";
const nullableNumber = (value: unknown, path: string) => value == null ? null : requireFiniteNumber(value, path);
const integer = (value: unknown, path: string, minimum: number) => {
  const result = requireFiniteNumber(value, path);
  if (!Number.isInteger(result) || result < minimum) throw new ResponseDecodeError(path, `an integer >= ${minimum}`);
  return result;
};
export function readGpuVfBinding(value: unknown, path: string): GpuVfCurveBinding {
  const row = requireRecord(value, path);
  return { vendor: requireNonEmptyString(row.vendor, `${path}.vendor`), deviceIdentity: requireNonEmptyString(row.deviceIdentity, `${path}.deviceIdentity`),
    model: requireString(row.model, `${path}.model`), pointSignature: requireNonEmptyString(row.pointSignature, `${path}.pointSignature`),
    scope: requireOneOf(row.scope, `${path}.scope`, ["device", "model"] as const) };
}
export function readGpuVfPoints(value: unknown, path: string): GpuVfCurvePoint[] {
  const points = requireArray(value, path).map((item, index) => {
    const at = `${path}[${index}]`, row = requireRecord(item, at);
    return { pointId: integer(row.pointId, `${at}.pointId`, 0), voltageMicrovolts: requireFiniteNumber(row.voltageMicrovolts, `${at}.voltageMicrovolts`),
      frequencyOffsetKhz: requireFiniteNumber(row.frequencyOffsetKhz, `${at}.frequencyOffsetKhz`) };
  });
  if (new Set(points.map(point => point.pointId)).size !== points.length) throw new ResponseDecodeError(path, "unique native point IDs");
  return points;
}
export function readGpuVfRules(value: unknown, path: string): GpuVfCurveEditorRules {
  const row = requireRecord(value, path);
  const number = (name: string) => requireFiniteNumber(row[name], `${path}.${name}`);
  const positive = (name: string) => { const result = number(name); if (result <= 0) throw new ResponseDecodeError(`${path}.${name}`, "positive"); return result; };
  const nonnegative = (name: string) => { const result = number(name); if (result < 0) throw new ResponseDecodeError(`${path}.${name}`, "nonnegative"); return result; };
  const precision = (name: string) => { const result = integer(row[name], `${path}.${name}`, 0); if (result > 100) throw new ResponseDecodeError(`${path}.${name}`, "a supported toFixed precision"); return result; };
  return { fineStepKhz: positive("fineStepKhz"), coarseStepKhz: positive("coarseStepKhz"), dragQuantizationKhz: positive("dragQuantizationKhz"),
    frequencyDecimals: precision("frequencyDecimals"), voltageDecimals: precision("voltageDecimals"),
    axisPaddingRatio: nonnegative("axisPaddingRatio"), hitRadiusPixels: positive("hitRadiusPixels"),
    maximumPresetCount: integer(row.maximumPresetCount, `${path}.maximumPresetCount`, 1), maximumNameLength: integer(row.maximumNameLength, `${path}.maximumNameLength`, 1),
    maximumDraftCount: integer(row.maximumDraftCount, `${path}.maximumDraftCount`, 1), undoCapacity: integer(row.undoCapacity, `${path}.undoCapacity`, 1),
    defaultBindingScope: requireOneOf(row.defaultBindingScope, `${path}.defaultBindingScope`, ["device", "model"] as const),
    safetyMinimumOffsetKhz: nullableNumber(row.safetyMinimumOffsetKhz, `${path}.safetyMinimumOffsetKhz`),
    safetyMaximumOffsetKhz: nullableNumber(row.safetyMaximumOffsetKhz, `${path}.safetyMaximumOffsetKhz`), readbackToleranceKhz: nonnegative("readbackToleranceKhz") };
}
function readDescriptor(value: unknown, path: string): GpuVfCurveDescriptor {
  const row = requireRecord(value, path);
  const hardware = requireArray(row.points, `${path}.points`).map((item, index): GpuVfCurveHardwarePoint => {
    const at = `${path}.points[${index}]`, point = requireRecord(item, at);
    return { pointId: integer(point.pointId, `${at}.pointId`, 0), voltageMicrovolts: requireFiniteNumber(point.voltageMicrovolts, `${at}.voltageMicrovolts`),
      defaultFrequencyKhz: nullableNumber(point.defaultFrequencyKhz, `${at}.defaultFrequencyKhz`),
      currentFrequencyKhz: nullableNumber(point.currentFrequencyKhz, `${at}.currentFrequencyKhz`),
      defaultOffsetKhz: nullableNumber(point.defaultOffsetKhz, `${at}.defaultOffsetKhz`), currentOffsetKhz: requireFiniteNumber(point.currentOffsetKhz, `${at}.currentOffsetKhz`),
      minimumOffsetKhz: nullableNumber(point.minimumOffsetKhz, `${at}.minimumOffsetKhz`), maximumOffsetKhz: nullableNumber(point.maximumOffsetKhz, `${at}.maximumOffsetKhz`) };
  });
  const available = requireBoolean(row.available, `${path}.available`), unavailableReason = readControlReason(row.unavailableReason, `${path}.unavailableReason`);
  if (!available && !unavailableReason) throw new ResponseDecodeError(path, "an unavailable descriptor with its reason");
  if (new Set(hardware.map(point => point.pointId)).size !== hardware.length) throw new ResponseDecodeError(`${path}.points`, "unique native point IDs");
  if (available && (row.binding == null || !hardware.length)) throw new ResponseDecodeError(path, "available curve facts with a binding and points");
  return { scalarOnly: requireBoolean(row.scalarOnly, `${path}.scalarOnly`), objectId: requireNonEmptyString(row.objectId, `${path}.objectId`), available, writable: requireBoolean(row.writable, `${path}.writable`), unavailableReason,
    binding: row.binding == null ? null : readGpuVfBinding(row.binding, `${path}.binding`), points: hardware,
    rules: readGpuVfRules(row.rules, `${path}.rules`), readAt: requireNonEmptyString(row.readAt, `${path}.readAt`) };
}
function readPreset(value: unknown, path: string): GpuVfCurvePreset {
  const row = requireRecord(value, path);
  return { id: requireNonEmptyString(row.id, `${path}.id`), name: requireNonEmptyString(row.name, `${path}.name`),
    binding: readGpuVfBinding(row.binding, `${path}.binding`), points: readGpuVfPoints(row.points, `${path}.points`),
    createdAt: requireNonEmptyString(row.createdAt, `${path}.createdAt`), updatedAt: requireNonEmptyString(row.updatedAt, `${path}.updatedAt`) };
}
export const gpuVfDescriptorDecoder = defineResponseDecoder<GpuVfCurveDescriptor>("control.gpu-vf.descriptor.v1", value => readDescriptor(value, "$"));
export const gpuVfPresetDecoder = defineResponseDecoder<GpuVfCurvePreset>("control.gpu-vf.preset.v1", value => readPreset(value, "$"));
export const gpuVfLibraryDecoder = defineResponseDecoder<GpuVfCurveLibrary>("control.gpu-vf.library.v1", value => {
  const row = requireRecord(value, "$");
  return { rules: readGpuVfRules(row.rules, "$.rules"), presets: requireArray(row.presets, "$.presets").map((item, index) => {
    const at = `$.presets[${index}]`, entry = requireRecord(item, at);
    const applicable = requireBoolean(entry.applicable, `${at}.applicable`), unavailableReason = readControlReason(entry.unavailableReason, `${at}.unavailableReason`);
    if (!applicable && !unavailableReason) throw new ResponseDecodeError(at, "an inapplicable curve with its reason");
    return { preset: readPreset(entry.preset, `${at}.preset`), applicable, unavailableReason };
  }) };
});
export const gpuVfDraftDecoder = defineResponseDecoder<GpuVfCurveDraft>("control.gpu-vf.draft.v1", value => {
  const row = requireRecord(value, "$");
  const points = readGpuVfPoints(row.points, "$.points"), descriptor = readDescriptor(row.descriptor, "$.descriptor");
  if (points.length !== descriptor.points.length || points.some((point, index) => point.pointId !== descriptor.points[index].pointId
    || point.voltageMicrovolts !== descriptor.points[index].voltageMicrovolts)) throw new ResponseDecodeError("$.points", "the complete fixed descriptor sequence");
  return { id: requireNonEmptyString(row.id, "$.id"), objectId: requireNonEmptyString(row.objectId, "$.objectId"), binding: readGpuVfBinding(row.binding, "$.binding"),
    points, descriptor, canUndo: requireBoolean(row.canUndo, "$.canUndo"), canRedo: requireBoolean(row.canRedo, "$.canRedo") };
});
export const gpuVfReferencesDecoder = defineResponseDecoder("control.gpu-vf.references.v1", value => {
  const row = requireRecord(value, "$"); return { references: requireArray(row.references, "$.references").map((item, index) => {
    const at = `$.references[${index}]`, entry = requireRecord(item, at);
    return { kind: requireNonEmptyString(entry.kind, `${at}.kind`), id: requireNonEmptyString(entry.id, `${at}.id`), objectId: requireNonEmptyString(entry.objectId, `${at}.objectId`),
      name: entry.name == null ? null : requireString(entry.name, `${at}.name`) };
  }) };
});
const acknowledgement = (key: "closed" | "deleted") => defineResponseDecoder(`control.gpu-vf.${key}.v1`, value => {
  if (requireRecord(value, "$")[key] !== true) throw new ResponseDecodeError(`$.${key}`, "true"); return true;
});
type Client = Pick<RequestClient, "request">;
const json = (method: string, body?: unknown) => ({ method, headers: { "content-type": "application/json" }, ...(body === undefined ? {} : { body: JSON.stringify(body) }) });
export const getGpuVfDescriptor = (client: Client, objectId: string, signal?: AbortSignal) => client.request({ key: `control.gpu-vf.descriptor.${objectId}`,
  url: `/api/control/objects/${encodeURIComponent(objectId)}/gpu-vf-curve`, decoder: gpuVfDescriptorDecoder, fallbackError: uiText.control.loadFailed,
  timeoutMs: controlHardwareRequestTimeoutMs, signal, request: { method: "GET" } });
export const getGpuVfLibrary = (client: Client, objectId: string, signal?: AbortSignal) => client.request({ key: `control.gpu-vf.library.${objectId}`,
  url: `/api/control/gpu-vf-curves?objectId=${encodeURIComponent(objectId)}`, decoder: gpuVfLibraryDecoder, fallbackError: uiText.control.loadFailed, signal, request: { method: "GET" } });
export const commandGpuVfDraft = (client: Client, command: GpuVfCurveCommand) => client.request({ key: `control.gpu-vf.command.${command.objectId}`,
  url: "/api/control/gpu-vf-curves/editor/command", decoder: gpuVfDraftDecoder, fallbackError: uiText.control.loadFailed, request: json("POST", command) });
export const closeGpuVfDraft = (client: Client, command: GpuVfCurveCommand) => client.request({ key: `control.gpu-vf.close.${command.draftId}`,
  url: "/api/control/gpu-vf-curves/editor/command", decoder: acknowledgement("closed"), fallbackError: uiText.control.loadFailed, request: json("POST", { ...command, operation: "close" }) });
export const saveGpuVfPreset = (client: Client, id: string, body: { objectId: string; name: string; draftId?: string; cloneFromId?: string; bindingScope?: "device" | "model"; expectedBinding: GpuVfCurveBinding }) => client.request({
  key: `control.gpu-vf.save.${id}`, url: `/api/control/gpu-vf-curves/${encodeURIComponent(id)}`, decoder: gpuVfPresetDecoder, fallbackError: uiText.control.loadFailed, request: json("PUT", body) });
export const getGpuVfReferences = (client: Client, id: string) => client.request({ key: `control.gpu-vf.references.${id}`, url: `/api/control/gpu-vf-curves/${encodeURIComponent(id)}/references`,
  decoder: gpuVfReferencesDecoder, fallbackError: uiText.control.loadFailed, request: { method: "GET" } });
export const deleteGpuVfPreset = (client: Client, id: string) => client.request({ key: `control.gpu-vf.delete.${id}`, url: `/api/control/gpu-vf-curves/${encodeURIComponent(id)}`,
  decoder: acknowledgement("deleted"), fallbackError: uiText.control.loadFailed, request: { method: "DELETE" } });
