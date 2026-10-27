export const memorySettings = { enabled: false, thresholdPercent: 95, checkIntervalSeconds: 1,
  actionMode: "dumpThenTerminate", reserves: [{ location: "F:\\reserve", sizeBytes: 67108864 }] };
export const memoryCommit = { currentBytes: 200, limitBytes: 1000, percent: 20 };
export const memoryProcess = { processId: 20, processStartKey: "18446744073709551615", name: "example",
  privateCommitBytes: 100, softwareId: "example-software" };
export const memoryPoint = { at: "2026-10-27T05:00:00+00:00", commit: memoryCommit, processes: [memoryProcess], processCount: 1 };
export const memoryReserveEntry = { location: "F:\\reserve", configuredSizeBytes: 67108864,
  built: true, reason: null, remainingBytes: 33554432, clusterBytes: 4096 };
export const memoryReserve = { entries: [memoryReserveEntry], configuredSizeBytes: 67108864,
  built: true, reason: null, remainingBytes: 33554432 };
export const memoryAction = { at: memoryPoint.at, processId: 20, processStartKey: memoryProcess.processStartKey, name: "example",
  baselineBytes: 100, currentBytes: 800, growthBytes: 700, dumpPaths: ["F:\\reserve\\example.dmp", "G:\\reserve\\example.part0002.dmp"],
  bytesWritten: 900, outcome: "completed", reason: null };
export const memoryStatus = { enabled: false, thresholdPercent: 95, checkIntervalSeconds: 1, actionMode: "dumpThenTerminate",
  reserve: memoryReserve, triggerBytes: 950, remainingBytes: 750, currentCommit: memoryCommit,
  normalRunningPoint: { at: memoryPoint.at, processCount: 1 }, inactiveReason: "disabled", excludedCandidates: [],
  actionRecordLimit: 64, actions: [memoryAction] };
export const memorySettingsResult = { settings: { version: "1.0.36", memoryProtection: memorySettings }, revision: "revision-a",
  serviceRecovery: null, updatedAt: memoryPoint.at, storagePath: "settings.json", runtimeApplicationDisposition: "committedAndApplied", runtimeFailureCode: null };
export const softwarePolicy = { softwareId: "example-software", softwareName: "Example", allowedProviders: ["windows-graphics-preference"],
  startupTargetGpu: "system_default_gpu", runtimeHotSwitchEnabled: null, preferredRuntimeSwitchMethod: "future_frame_takeover",
  baseScoreOverride: null, processOverrideAllowed: true, updatedAt: memoryPoint.at, cpuSchedulingExcluded: false,
  memorySchedulingExcluded: false, gpuSchedulingExcluded: false, cpuMaximumOccupancyMode: "all_cores",
  cpuManualExclusivePositionIds: [], cpuManualLockedPositionIds: [], hostMemoryProtectionNoDump: true };
