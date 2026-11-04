// Known backend option names select localized labels and descriptions.
// Unknown names remain displayable verbatim.
// The backend response determines which options appear.
export const memoryCpuTierOptionNames = [
  "gcGen0MaxBudget",
  "gcConserveMemory",
  "ledgerMemoryModeFloor",
  "bufferReturn",
  "nativeWorkareaGrowth",
  "singleRequestStopBufferMs",
  "serviceIdentityCacheMs",
  "browserRuntimeSnapshotSeconds",
  "platformSensorSnapshotSeconds",
  "storageSnapshotSeconds",
  "rendererAbsenceEntries",
  "fileQuerySessionsMax",
  "fileQuerySessionsPrebuilt"
] as const;

export type MemoryCpuTierOptionName = typeof memoryCpuTierOptionNames[number];

const knownNames = new Set<string>(memoryCpuTierOptionNames);

export function isKnownMemoryCpuTierOptionName(name: string): name is MemoryCpuTierOptionName {
  return knownNames.has(name);
}
