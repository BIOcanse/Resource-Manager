// 前端文案认识的选项名，和后端 MemoryCpuTierOptionNames 一致（一个值一个选项）。
// 后端改名时只改这里和各语言文案的键；回复里不在这里的名字按“未知选项”显示原名。
// 这份清单只决定哪些名字有翻译好的名称和说明，不决定显示哪些选项：显示什么完全按读回复。
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
