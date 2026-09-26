import type { AppOptimizationMode } from "../../types.ts";

export type OptimizationDomain = "memory" | "cpu" | "gpu";

const modeMasks: Record<AppOptimizationMode, number> = {
  normal: 0,
  limited: 1,
  cpu: 2,
  gpu: 4,
  "memory+cpu": 3,
  "memory+gpu": 5,
  "cpu+gpu": 6,
  smart: 7
};

const maskModes: AppOptimizationMode[] = [
  "normal", "limited", "cpu", "memory+cpu",
  "gpu", "memory+gpu", "cpu+gpu", "smart"
];

const domainMasks: Record<OptimizationDomain, number> = {
  memory: 1,
  cpu: 2,
  gpu: 4
};

export function normalizeOptimizationModeValue(mode?: string | null): AppOptimizationMode {
  return mode !== undefined && mode !== null && Object.hasOwn(modeMasks, mode)
    ? mode as AppOptimizationMode
    : "normal";
}

export function optimizationModeHasDomain(
  mode: AppOptimizationMode,
  domain: OptimizationDomain
): boolean {
  return (modeMasks[mode] & domainMasks[domain]) !== 0;
}

export function toggleOptimizationDomain(
  mode: AppOptimizationMode,
  domain: OptimizationDomain
): AppOptimizationMode {
  return maskModes[modeMasks[mode] ^ domainMasks[domain]];
}
