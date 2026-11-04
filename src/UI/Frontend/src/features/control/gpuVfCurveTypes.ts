import type { ControlApplyReason } from "./controlTypes.ts";
export interface GpuVfCurveBinding {
  vendor: string; deviceIdentity: string; model: string; pointSignature: string; scope: "device" | "model";
}
export interface GpuVfCurvePoint { pointId: number; voltageMicrovolts: number; frequencyOffsetKhz: number; }
export interface GpuVfCurveHardwarePoint {
  pointId: number; voltageMicrovolts: number;
  defaultFrequencyKhz: number | null; currentFrequencyKhz: number | null;
  defaultOffsetKhz: number | null; currentOffsetKhz: number;
  minimumOffsetKhz: number | null; maximumOffsetKhz: number | null;
}
export interface GpuVfCurveEditorRules {
  fineStepKhz: number; coarseStepKhz: number; dragQuantizationKhz: number;
  frequencyDecimals: number; voltageDecimals: number; axisPaddingRatio: number; hitRadiusPixels: number;
  maximumPresetCount: number; maximumNameLength: number; maximumDraftCount: number; undoCapacity: number;
  defaultBindingScope: "device" | "model"; safetyMinimumOffsetKhz: number | null;
  safetyMaximumOffsetKhz: number | null; readbackToleranceKhz: number;
}
export interface GpuVfCurveDescriptor {
  scalarOnly: boolean;
  objectId: string; available: boolean; writable: boolean; unavailableReason: ControlApplyReason | null;
  binding: GpuVfCurveBinding | null; points: readonly GpuVfCurveHardwarePoint[];
  rules: GpuVfCurveEditorRules; readAt: string;
}
export interface GpuVfCurvePreset {
  id: string; name: string; binding: GpuVfCurveBinding; points: readonly GpuVfCurvePoint[];
  createdAt: string; updatedAt: string;
}
export interface GpuVfCurveLibrary {
  presets: readonly { preset: GpuVfCurvePreset; applicable: boolean; unavailableReason: ControlApplyReason | null }[];
  rules: GpuVfCurveEditorRules;
}
export interface GpuVfCurveDraft {
  id: string; objectId: string; binding: GpuVfCurveBinding; points: readonly GpuVfCurvePoint[];
  descriptor: GpuVfCurveDescriptor; canUndo: boolean; canRedo: boolean;
}
export interface GpuVfCurveReference { kind: string; id: string; objectId: string; name: string | null; }
export interface GpuVfCurveCommand {
  operation: "begin" | "move-selection" | "align-left" | "align-right" | "step" | "reset-default" | "uniform-offset" | "undo" | "redo" | "close";
  objectId: string; draftId?: string; curveId?: string; expectedBinding: GpuVfCurveBinding;
  pointIds?: readonly number[]; deltaKhz?: number; offsetKhz?: number; direction?: number; coarse?: boolean;
}
