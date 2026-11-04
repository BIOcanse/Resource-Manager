/** Control wire types share object identities with monitoring. */
export interface ControlObjectPlatform {
  operatingSystem: string;
  vendor: string;
}

export interface ControlNumberRange {
  minimum: number;
  maximum: number;
  step: number;
  unit: string;
  /** Vendor or system default restored by reset; null when unavailable. */
  defaultValue: number | null;
}

/** Ordered adjustment levels classify recoverable instability versus operations without a recovery guarantee. */
export type ControlAccessLevel = "normal" | "root";

/** Ascending order defines inclusion. */
export const controlAccessLevels: readonly ControlAccessLevel[] = ["normal", "root"];

/** Whether the current level includes the required level. */
export function controlAccessLevelAllows(
  current: ControlAccessLevel,
  required: ControlAccessLevel
): boolean {
  return controlAccessLevels.indexOf(current) >= controlAccessLevels.indexOf(required);
}

/** Backend unavailability categories distinguish platform limits, access levels, missing components, prerequisites, and missing implementations. */
export type ControlUnavailableKind =
  | "platform"
  | "access-level"
  | "component"
  | "firmware-owned"
  | "prerequisite"
  | "not-implemented";

export const controlUnavailableKinds: readonly ControlUnavailableKind[] = [
  "platform", "access-level", "component", "firmware-owned",
  "prerequisite", "not-implemented"
];

export interface ControlCapability {
  id: string;
  valueKind: "toggle" | "number" | "curve" | "gpu-vf-curve-reference";
  /** Unavailable capabilities always include a reason. */
  supported: boolean;
  unavailableReason: ControlApplyReason | null;
  /** Backend category used to choose the availability icon; null when available. */
  unavailableKind: ControlUnavailableKind | null;
  requiredComponentId: string | null;
  range: ControlNumberRange | null;
  /** Writer channel for this capability, or null when no writer owns it. */
  channel: string | null;
  /** Required adjustment level, displayed even when access is sufficient. */
  requiredAccessLevel: ControlAccessLevel;
  /** Firmware curve point limit; null without firmware curve support. */
  curvePointLimit: number | null;
}

export interface ControlFanManagementItem { capabilityId: string; curveExecution: string | null; }
export type ControlFanManagementModeId = "firmware-management" | "firmware-write" | "software-management";
export interface ControlFanManagementMode {
  id: ControlFanManagementModeId; available: boolean; unavailableKind: ControlUnavailableKind | null;
  unavailableReason: ControlApplyReason | null; items: readonly ControlFanManagementItem[];
}
export interface ControlObject {
  fanManagementModes?: readonly ControlFanManagementMode[] | null;
  softwareTakeoverCoversAllFans?: boolean;
  firmwareCurveKind?: string | null;
  id: string;
  kind: string;
  reportedName: string | null;
  ordinal?: number | null;
  adapterIndex?: number | null;
  platform: ControlObjectPlatform;
  capabilities: readonly ControlCapability[];
  detail: string | null;
  /** Object classifications, separate from its reported name. */
  terms: readonly string[];
  /** GPU attachment type; null for other objects. Integrated GPU controls are constrained by the CPU package. */
  gpuAttachment: string | null;
  /** No capability is controllable; the UI dims the whole object. */
  isControllable: boolean;
}

export interface ControlObjectCatalog {
  objects: readonly ControlObject[];
  readAt: string;
}

/** One temperature-to-speed curve point. */
export interface ControlCurvePoint {
  temperatureCelsius: number;
  percent: number;
}

/** Exactly one value matches valueKind. Numeric settings carry units so persisted presets remain self-describing. */
export interface ControlSetting {
  capabilityId: string;
  gpuVfCurvePresetId?: string | null;
  gpuVfCurveExecution?: { binding: import("./gpuVfCurveTypes.ts").GpuVfCurveBinding; points: readonly import("./gpuVfCurveTypes.ts").GpuVfCurvePoint[] } | null;
  number?: number | null;
  toggle?: boolean | null;
  curve?: readonly ControlCurvePoint[] | null;
  /** Persist the chosen curve executor because firmware and software have different lifetimes. */
  curveExecution?: string | null;
  /** Numeric unit from range.unit; omitted for other value kinds. */
  unit?: string | null;
}

export interface ControlObjectDesiredState {
  objectId: string;
  settings: readonly ControlSetting[];
}

/** Backend-persisted desired state maintained across restarts and driver resets. */
export interface ControlDesiredState {
  objects: readonly ControlObjectDesiredState[];
  pendingReleases?: readonly ControlObjectDesiredState[];
}

export interface ControlApplyReason {
  code: string;
  arguments?: { requiredAccessLevel?: ControlAccessLevel | null; minimum?: number | null;
    maximum?: number | null; unit?: string | null; requested?: number | null; observed?: number | null } | null;
}

export interface ControlApplyOutcome {
  objectId: string;
  capabilityId: string;
  status: "applied" | "unsupported" | "failed";
  reason?: ControlApplyReason | null;
  nativeStatus?: number | null;
}

export interface ControlApplyReport {
  outcomes: readonly ControlApplyOutcome[];
  appliedAt: string;
}

/** Desired state and latest execution receipt; consumers do not infer execution. */
export interface ControlStateView {
  desired: ControlDesiredState;
  lastApply: ControlApplyReport;
}

/** Registered instances retain identity and presets while absent. */
export interface ControlInstance {
  id: string;
  kind: string;
  reportedName: string | null;
  ordinal?: number | null;
  adapterIndex?: number | null;
  platform: ControlObjectPlatform;
  gpuAttachment: string | null;
  /** Whether this identity uniquely identifies the device. */
  identityIsUnique: boolean;
  firstSeenAt: string;
  lastSeenAt: string;
  /** Default preset created at registration for reset. */
  defaultSettings: readonly ControlSetting[];
}

export interface ControlInstanceView {
  instance: ControlInstance;
  isPresent: boolean;
}

export interface ControlInstanceCatalog {
  instances: readonly ControlInstanceView[];
  readAt: string;
}

/** Named settings bound to one instance; loading edits the draft and requires explicit apply. */
export interface ControlPreset {
  id: string;
  /** Instance that owns this preset. */
  objectId: string;
  name: string;
  settings: readonly ControlSetting[];
  updatedAt: string;
}

export interface ControlPresetCatalog {
  presets: readonly ControlPreset[];
}

/** Observed capability value with its unit; unavailable readings carry reasons and remain distinct from zero. */
export interface ControlActualValue {
  objectId: string;
  capabilityId: string;
  number?: number | null;
  toggle?: boolean | null;
  curve?: readonly ControlCurvePoint[] | null;
  unit?: string | null;
  unreadableReason?: ControlApplyReason | null;
}

/** Observed hardware state is independent of desired state and may change externally. */
export interface ControlActualState {
  values: readonly ControlActualValue[];
  readAt: string;
}
