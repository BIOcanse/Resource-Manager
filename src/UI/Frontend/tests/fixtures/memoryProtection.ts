// Shapes follow the production serializer samples in the backend handoff (HostMemoryProtectionContracts.cs).
export const memorySettings = { enabled: false, thresholdPercent: 95, checkIntervalSeconds: 1,
  actionMode: "dumpThenTerminate", reserves: [{ location: "F:\\reserve", sizeBytes: 67108864 }],
  minimumProcessGrowthBytes: 1073741824, protectedProcesses: ["explorer.exe", "C:\\Tools\\keep.exe"] };
export const memoryCommit = { currentBytes: 200, limitBytes: 1000, percent: 20 };
export const memoryProcess = { processId: 20, processStartKey: "18446744073709551615", name: "example",
  privateCommitBytes: 100, softwareId: "example-software", executablePath: "C:\\fixture\\example.exe", parentProcessId: 17 };
export const memoryPoint = { at: "2026-10-27T05:00:00+00:00", commit: memoryCommit, processes: [memoryProcess], processCount: 1 };
export const memoryReserveEntry = { location: "F:\\reserve", configuredSizeBytes: 67108864,
  built: true, reason: null, remainingBytes: 33554432, clusterBytes: 4096 };
export const memoryReserve = { entries: [memoryReserveEntry], configuredSizeBytes: 67108864,
  built: true, reason: null, remainingBytes: 33554432 };
export const memoryProcessBasis = { rule: "process-growth-at-least-minimum", processGrowthBytes: 700,
  directChildrenMemoryGrowthBytes: null, minimumProcessGrowthBytes: 512 };
export const memoryTreeBasis = { rule: "largest-direct-children-memory-growth", processGrowthBytes: null,
  directChildrenMemoryGrowthBytes: 400, minimumProcessGrowthBytes: 1000 };
export const memoryAction = { at: memoryPoint.at, processId: 20, processStartKey: memoryProcess.processStartKey, name: "example",
  baselineBytes: 100, currentBytes: 800, growthBytes: 700, dumpPaths: ["F:\\reserve\\example.dmp", "G:\\reserve\\example.part0002.dmp"],
  bytesWritten: 900, outcome: "completed", reason: null, detail: null, selectionBasis: memoryProcessBasis,
  actionId: "5fb774b0d85b49259ad57b3280d2fa51", targetKind: "process", treeRoot: null };
export const memoryTreeRoot = { processId: 82148, processStartKey: "134375874274860391", name: "powershell" };
// Native tree response fragments from the handoff: a terminated child and the protected root of the same action.
export const memoryTreeChild = { at: "2026-10-27T15:10:28.0733919+00:00", processId: 10048, processStartKey: "134375874278245164",
  name: "ping", baselineBytes: 0, currentBytes: 10, growthBytes: 10, dumpPaths: [], bytesWritten: 0, outcome: "terminated-without-dump",
  reason: "process-tree", detail: null, selectionBasis: memoryTreeBasis, actionId: "8703199381c14eaea137231376bb4def",
  targetKind: "processTree", treeRoot: memoryTreeRoot };
export const memoryTreeRootRecord = { ...memoryTreeChild, at: "2026-10-27T15:10:28.0734311+00:00", processId: 82148,
  processStartKey: "134375874274860391", name: "powershell", baselineBytes: 100, currentBytes: 100, growthBytes: 0,
  outcome: "excluded", reason: "protected-process" };
// HostMemoryProtectionLiveStatus: the status frame; GET and the rebuild response add the point summary and records.
export const memoryLiveStatus = { enabled: false, thresholdPercent: 95, checkIntervalSeconds: 1,
  reserve: memoryReserve, triggerBytes: 950, remainingBytes: 750, currentCommit: memoryCommit,
  inactiveReason: "disabled", excludedCandidates: [], actionMode: "dumpThenTerminate", active: false, atOrAboveThreshold: false,
  currentActionId: null, minimumProcessGrowthBytes: 1073741824, protectedProcesses: ["explorer.exe"], decisionReason: null };
export const memoryStatus = { ...memoryLiveStatus, normalRunningPoint: { at: memoryPoint.at, processCount: 1 },
  actionRecordLimit: 64, actions: [memoryAction] };
export const memorySettingsResult = { settings: { version: "1.0.37", memoryProtection: memorySettings }, revision: "revision-a",
  serviceRecovery: null, updatedAt: memoryPoint.at, storagePath: "settings.json", runtimeApplicationDisposition: "committedAndApplied", runtimeFailureCode: null };
export const softwarePolicy = { softwareId: "example-software", softwareName: "Example", allowedProviders: ["windows-graphics-preference"],
  startupTargetGpu: "system_default_gpu", runtimeHotSwitchEnabled: null, preferredRuntimeSwitchMethod: "future_frame_takeover",
  baseScoreOverride: null, processOverrideAllowed: true, updatedAt: memoryPoint.at, cpuSchedulingExcluded: false,
  memorySchedulingExcluded: false, gpuSchedulingExcluded: false, cpuMaximumOccupancyMode: "all_cores",
  cpuManualExclusivePositionIds: [], cpuManualLockedPositionIds: [], hostMemoryProtectionNoDump: true,
  hostMemoryProtectionNeverTerminate: true };
