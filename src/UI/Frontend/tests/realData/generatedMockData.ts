import { readFileSync } from "node:fs";
import type { DeviceField, GeneratedData, GeneratedDevice, GeneratedMetric } from "./generateScenario.ts";

const fieldSamples = (JSON.parse(readFileSync(new URL("../fixtures/metricFieldsWire/subscription-frame.json", import.meta.url), "utf8")) as { value: GeneratedDevice[] }).value;

export interface GeneratedMetricDefinition {
  id: string;
  unit: string;
  selectable: boolean;
  candidateSources: never[];
  hasDedicatedMemoryMetrics: boolean | null;
  deviceIdentity?: string;
}
export interface GeneratedMetricItem extends Omit<GeneratedMetric, "status"> {
  id: string;
  status: 1 | 3;
}
export interface GeneratedGpuScore {
  gpuId: string;
  index: number;
  name: string;
  defaultPerformanceScore: number;
  performanceScore: number;
  rasterPerformanceScore: number;
  hasPerformanceOverride: boolean;
  isIntegrated: boolean;
  source: "generated";
  matchedPreset: null;
  isPresetMatch: boolean;
  hasDedicatedMemoryMetrics: boolean;
}
export interface GeneratedWireField extends DeviceField {
  sourceId: "generated";
  status: number;
  numericValue: number | null;
  integerValue: string | null;
  bytesValue: string | null;
  booleanValue: boolean | null;
  sampleDurationMilliseconds: number | null;
  observedAt: string | null;
}
export interface GeneratedFieldDevice extends GeneratedDevice {
  fields: GeneratedWireField[];
}
// Adapt generated numbers to the existing mock's production wire shapes.
// Every payload still leaves through mockFrontendServer's original routes.
export function generatedMetricDefinitions(data?: GeneratedData | null): GeneratedMetricDefinition[] {
  return Object.entries(data?.metrics ?? {}).map(([id, item]) => ({ id, unit: item.unit ?? "%", selectable: true,
    candidateSources: [], hasDedicatedMemoryMetrics: id.startsWith("gpu.") ? true : null,
    ...(/^gpu\.(\d+)\./.test(id) ? { deviceIdentity: `generated-gpu-${id.split(".")[1]}` } : {}) }));
}

export function generatedMetricItems(data?: Pick<GeneratedData, "metrics"> | null): Record<string, GeneratedMetricItem> {
  return Object.fromEntries(Object.entries(data?.metrics ?? {}).map<[string, GeneratedMetricItem]>(([id, item]) => [id, { id, ...item,
    status: item.numericValue === null ? 3 : 1 }]));
}

export function generatedGpuScores(data?: GeneratedData | null): GeneratedGpuScore[] {
  return (data?.gpus ?? []).map(gpu => ({ gpuId: `gpu:${gpu.index}`, index: gpu.index, name: gpu.name,
    defaultPerformanceScore: 100 + gpu.index * 50, performanceScore: 100 + gpu.index * 50,
    rasterPerformanceScore: 100 + gpu.index * 50, hasPerformanceOverride: false, isIntegrated: gpu.index === 0,
    source: "generated", matchedPreset: null, isPresetMatch: false, hasDedicatedMemoryMetrics: true } satisfies GeneratedGpuScore));
}

export function generatedFieldDevices(data?: GeneratedData | null): GeneratedFieldDevice[] {
  const gpuTemplate = data?.devices?.find(device => device.device?.kind === "gpu_adapter");
  const nonGpuCount = Math.max(0, (data?.devices.length ?? 0) - (data?.gpus.length ?? 0));
  const devices = (data?.devices ?? []).filter(device => device.device?.kind !== "gpu_adapter").slice(0, nonGpuCount);
  for (const gpu of data?.gpus ?? []) {
    const identity = `generated-gpu-${gpu.index}`;
    if (!devices.some(device => device.deviceIdentity === identity)) devices.push({ deviceIdentity: identity,
      device: { kind: "gpu_adapter", address: null, displayName: gpu.name, ordinal: gpu.index }, fields: structuredClone(gpuTemplate?.fields ?? []) });
  }
  return devices.map(device => ({ ...device, fields: device.fields.map(field => {
    const sampleDevice = fieldSamples.find(candidate => candidate.fields.some(sample => sample.name === field.name));
    const sample = sampleDevice?.fields.find(sample => sample.name === field.name);
    // A device can only enter this map through a present data argument.
    const value = data!.metrics[`${field.name}@${device.deviceIdentity}`]
      ?? data!.metrics[`${field.name}@${sampleDevice?.deviceIdentity}`];
    return { ...sample, ...field, name: field.name, sourceId: "generated", status: sample?.status ?? 3,
      numericValue: value?.numericValue ?? sample?.numericValue ?? null,
      integerValue: value?.integerValue ?? sample?.integerValue ?? null,
      bytesValue: sample?.bytesValue ?? null, booleanValue: sample?.booleanValue ?? null,
      sampleDurationMilliseconds: sample?.sampleDurationMilliseconds ?? null,
      observedAt: sample?.observedAt ?? null } satisfies GeneratedWireField;
  }) }));
}
