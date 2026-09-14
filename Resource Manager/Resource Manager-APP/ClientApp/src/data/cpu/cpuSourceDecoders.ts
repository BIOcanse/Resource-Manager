import {
  defineResponseDecoder,
  requireArray,
  requireBoolean,
  requireFiniteNumber,
  requireNonEmptyString,
  requireNonNegativeSafeInteger,
  requireRecord,
  requireSafeInteger,
  requireStringArray,
  ResponseDecodeError
} from "../../frontendRuntime/request/ResponseDecoder.ts";
import type {
  CpuCcdModel,
  CpuCcdResidency,
  CpuCoreCacheLevelModel,
  CpuCoreResidencySnapshot,
  CpuExclusiveBinding,
  CpuExclusiveBindingSnapshot,
  CpuLogicalProcessorModel,
  CpuLogicalProcessorResidency,
  CpuPhysicalCoreModel,
  CpuPhysicalCoreResidency,
  CpuProcessCoreResidency,
  CpuSpecificationModel,
  CpuThreadCoreResidency,
  CpuTopologySnapshot
} from "../../types.ts";

export const cpuTopologyDecoder = defineResponseDecoder<CpuTopologySnapshot | null>(
  "cpu.topology.v1",
  (value) => {
    if (value === null) return null;
    const record = requireRecord(value);
    return {
      capturedAt: requireTimestamp(record.capturedAt, "$.capturedAt"),
      cpuName: requireNonEmptyString(record.cpuName, "$.cpuName"),
      specification: decodeSpecification(record.specification, "$.specification"),
      topologySource: requireNonEmptyString(record.topologySource, "$.topologySource"),
      usageSource: requireNonEmptyString(record.usageSource, "$.usageSource"),
      affinityTargetKind: requireNonEmptyString(
        record.affinityTargetKind,
        "$.affinityTargetKind"),
      visualLayoutKind: requireNonEmptyString(record.visualLayoutKind, "$.visualLayoutKind"),
      visualLayoutSource: requireNonEmptyString(
        record.visualLayoutSource,
        "$.visualLayoutSource"),
      physicalCoreCount: requireNonNegativeSafeInteger(
        record.physicalCoreCount,
        "$.physicalCoreCount"),
      logicalProcessorCount: requireNonNegativeSafeInteger(
        record.logicalProcessorCount,
        "$.logicalProcessorCount"),
      ccdCount: requireNonNegativeSafeInteger(record.ccdCount, "$.ccdCount"),
      simultaneousMultithreading: requireBoolean(
        record.simultaneousMultithreading,
        "$.simultaneousMultithreading"),
      ccds: requireArray(record.ccds, "$.ccds")
        .map((item, index) => decodeCcd(item, `$.ccds[${index}]`)),
      physicalCores: requireArray(record.physicalCores, "$.physicalCores")
        .map((item, index) => decodePhysicalCore(item, `$.physicalCores[${index}]`)),
      logicalProcessors: requireArray(record.logicalProcessors, "$.logicalProcessors")
        .map((item, index) => decodeLogicalProcessor(
          item,
          `$.logicalProcessors[${index}]`)),
      notes: requireStringArray(record.notes, "$.notes")
    };
  });

export const cpuResidencyDecoder = defineResponseDecoder<CpuCoreResidencySnapshot | null>(
  "cpu.residency.v1",
  (value) => {
    if (value === null) return null;
    const record = requireRecord(value);
    return {
      capturedAt: requireTimestamp(record.capturedAt, "$.capturedAt"),
      window: requireNonEmptyString(record.window, "$.window"),
      sessionGeneration: requireNonNegativeSafeInteger(
        record.sessionGeneration,
        "$.sessionGeneration"),
      measuredFrom: requireTimestamp(record.measuredFrom, "$.measuredFrom"),
      measuredThrough: requireTimestamp(record.measuredThrough, "$.measuredThrough"),
      processes: requireArray(record.processes, "$.processes")
        .map((item, index) => decodeProcessResidency(
          item,
          `$.processes[${index}]`))
    };
  });

export const cpuExclusiveBindingsDecoder =
  defineResponseDecoder<CpuExclusiveBindingSnapshot | null>(
    "cpu.exclusive-bindings.v1",
    (value) => {
      if (value === null) return null;
      const record = requireRecord(value);
      return {
        capturedAt: requireTimestamp(record.capturedAt, "$.capturedAt"),
        cpuName: requireNonEmptyString(record.cpuName, "$.cpuName"),
        bindings: requireArray(record.bindings, "$.bindings")
          .map((item, index) => decodeExclusiveBinding(
            item,
            `$.bindings[${index}]`))
      };
    });

function decodeSpecification(value: unknown, path: string): CpuSpecificationModel {
  const record = requireRecord(value, path);
  return {
    name: requireNonEmptyString(record.name, `${path}.name`),
    vendor: requireNonEmptyString(record.vendor, `${path}.vendor`),
    family: requireNonEmptyString(record.family, `${path}.family`),
    physicalCoreCount: requireNonNegativeSafeInteger(
      record.physicalCoreCount,
      `${path}.physicalCoreCount`),
    logicalProcessorCount: requireNonNegativeSafeInteger(
      record.logicalProcessorCount,
      `${path}.logicalProcessorCount`),
    maxClockSpeedMhz: optionalNumber(record.maxClockSpeedMhz, `${path}.maxClockSpeedMhz`),
    currentClockSpeedMhz: optionalNumber(
      record.currentClockSpeedMhz,
      `${path}.currentClockSpeedMhz`),
    l2CacheSizeKb: optionalNumber(record.l2CacheSizeKb, `${path}.l2CacheSizeKb`),
    l3CacheSizeKb: optionalNumber(record.l3CacheSizeKb, `${path}.l3CacheSizeKb`),
    source: requireNonEmptyString(record.source, `${path}.source`)
  };
}

function decodeCcd(value: unknown, path: string): CpuCcdModel {
  const record = requireRecord(value, path);
  return {
    id: requireNonEmptyString(record.id, `${path}.id`),
    index: requireNonNegativeSafeInteger(record.index, `${path}.index`),
    label: requireNonEmptyString(record.label, `${path}.label`),
    usagePercent: optionalNumber(record.usagePercent, `${path}.usagePercent`),
    physicalCoreIndexes: requireNumberArray(
      record.physicalCoreIndexes,
      `${path}.physicalCoreIndexes`),
    logicalProcessorIds: requireNumberArray(
      record.logicalProcessorIds,
      `${path}.logicalProcessorIds`),
    source: requireNonEmptyString(record.source, `${path}.source`)
  };
}

function decodePhysicalCore(value: unknown, path: string): CpuPhysicalCoreModel {
  const record = requireRecord(value, path);
  return {
    id: requireNonEmptyString(record.id, `${path}.id`),
    index: requireNonNegativeSafeInteger(record.index, `${path}.index`),
    label: requireNonEmptyString(record.label, `${path}.label`),
    ccdId: requireNonEmptyString(record.ccdId, `${path}.ccdId`),
    efficiencyClass: requireSafeInteger(record.efficiencyClass, `${path}.efficiencyClass`),
    performanceScore: requireFiniteNumber(record.performanceScore, `${path}.performanceScore`),
    usagePercent: optionalNumber(record.usagePercent, `${path}.usagePercent`),
    logicalProcessorIds: requireNumberArray(
      record.logicalProcessorIds,
      `${path}.logicalProcessorIds`),
    cacheLevels: requireArray(record.cacheLevels, `${path}.cacheLevels`)
      .map((item, index) => decodeCacheLevel(item, `${path}.cacheLevels[${index}]`))
  };
}

function decodeCacheLevel(value: unknown, path: string): CpuCoreCacheLevelModel {
  const record = requireRecord(value, path);
  return {
    level: requireNonNegativeSafeInteger(record.level, `${path}.level`),
    sizeKb: optionalNumber(record.sizeKb, `${path}.sizeKb`),
    logicalProcessorIds: requireNumberArray(
      record.logicalProcessorIds,
      `${path}.logicalProcessorIds`),
    scope: requireNonEmptyString(record.scope, `${path}.scope`)
  };
}

function decodeLogicalProcessor(value: unknown, path: string): CpuLogicalProcessorModel {
  const record = requireRecord(value, path);
  return {
    id: requireNonNegativeSafeInteger(record.id, `${path}.id`),
    processorGroup: requireNonNegativeSafeInteger(
      record.processorGroup,
      `${path}.processorGroup`),
    groupRelativeIndex: requireNonNegativeSafeInteger(
      record.groupRelativeIndex,
      `${path}.groupRelativeIndex`),
    physicalCoreId: requireNonEmptyString(record.physicalCoreId, `${path}.physicalCoreId`),
    ccdId: requireNonEmptyString(record.ccdId, `${path}.ccdId`),
    performanceScore: requireFiniteNumber(record.performanceScore, `${path}.performanceScore`),
    usagePercent: optionalNumber(record.usagePercent, `${path}.usagePercent`),
    affinitySelectable: requireBoolean(record.affinitySelectable, `${path}.affinitySelectable`)
  };
}

function decodeProcessResidency(value: unknown, path: string): CpuProcessCoreResidency {
  const record = requireRecord(value, path);
  return {
    processInstanceId: requireNonEmptyString(
      record.processInstanceId,
      `${path}.processInstanceId`),
    processId: requireNonNegativeSafeInteger(record.processId, `${path}.processId`),
    processStartKey: optionalString(record.processStartKey, `${path}.processStartKey`),
    processName: requireNonEmptyString(record.processName, `${path}.processName`),
    executionTimeMilliseconds: requireNonNegativeNumber(
      record.executionTimeMilliseconds,
      `${path}.executionTimeMilliseconds`),
    switchCount: requireNonNegativeSafeInteger(record.switchCount, `${path}.switchCount`),
    threadCount: requireNonNegativeSafeInteger(record.threadCount, `${path}.threadCount`),
    primaryCcdId: optionalString(record.primaryCcdId, `${path}.primaryCcdId`),
    primaryPhysicalCoreId: optionalString(
      record.primaryPhysicalCoreId,
      `${path}.primaryPhysicalCoreId`),
    primaryLogicalProcessorId: optionalInteger(
      record.primaryLogicalProcessorId,
      `${path}.primaryLogicalProcessorId`),
    ccds: requireArray(record.ccds, `${path}.ccds`)
      .map((item, index) => decodeCcdResidency(item, `${path}.ccds[${index}]`)),
    physicalCores: requireArray(record.physicalCores, `${path}.physicalCores`)
      .map((item, index) => decodePhysicalCoreResidency(
        item,
        `${path}.physicalCores[${index}]`)),
    logicalProcessors: requireArray(record.logicalProcessors, `${path}.logicalProcessors`)
      .map((item, index) => decodeLogicalResidency(
        item,
        `${path}.logicalProcessors[${index}]`)),
    threads: requireArray(record.threads, `${path}.threads`)
      .map((item, index) => decodeThreadResidency(item, `${path}.threads[${index}]`))
  };
}

function decodeCcdResidency(value: unknown, path: string): CpuCcdResidency {
  const record = requireRecord(value, path);
  return {
    ccdId: requireNonEmptyString(record.ccdId, `${path}.ccdId`),
    executionTimeMilliseconds: requireNonNegativeNumber(
      record.executionTimeMilliseconds,
      `${path}.executionTimeMilliseconds`),
    switchCount: requireNonNegativeSafeInteger(record.switchCount, `${path}.switchCount`),
    sharePercent: requireFiniteNumber(record.sharePercent, `${path}.sharePercent`)
  };
}

function decodePhysicalCoreResidency(
  value: unknown,
  path: string
): CpuPhysicalCoreResidency {
  const record = requireRecord(value, path);
  return {
    physicalCoreId: requireNonEmptyString(record.physicalCoreId, `${path}.physicalCoreId`),
    ccdId: requireNonEmptyString(record.ccdId, `${path}.ccdId`),
    executionTimeMilliseconds: requireNonNegativeNumber(
      record.executionTimeMilliseconds,
      `${path}.executionTimeMilliseconds`),
    switchCount: requireNonNegativeSafeInteger(record.switchCount, `${path}.switchCount`),
    sharePercent: requireFiniteNumber(record.sharePercent, `${path}.sharePercent`)
  };
}

function decodeLogicalResidency(
  value: unknown,
  path: string
): CpuLogicalProcessorResidency {
  const record = requireRecord(value, path);
  return {
    logicalProcessorId: requireNonNegativeSafeInteger(
      record.logicalProcessorId,
      `${path}.logicalProcessorId`),
    physicalCoreId: requireNonEmptyString(record.physicalCoreId, `${path}.physicalCoreId`),
    ccdId: requireNonEmptyString(record.ccdId, `${path}.ccdId`),
    executionTimeMilliseconds: requireNonNegativeNumber(
      record.executionTimeMilliseconds,
      `${path}.executionTimeMilliseconds`),
    switchCount: requireNonNegativeSafeInteger(record.switchCount, `${path}.switchCount`),
    sharePercent: requireFiniteNumber(record.sharePercent, `${path}.sharePercent`)
  };
}

function decodeThreadResidency(value: unknown, path: string): CpuThreadCoreResidency {
  const record = requireRecord(value, path);
  return {
    threadInstanceId: requireNonEmptyString(
      record.threadInstanceId,
      `${path}.threadInstanceId`),
    threadId: requireNonNegativeSafeInteger(record.threadId, `${path}.threadId`),
    executionTimeMilliseconds: requireNonNegativeNumber(
      record.executionTimeMilliseconds,
      `${path}.executionTimeMilliseconds`),
    switchCount: requireNonNegativeSafeInteger(record.switchCount, `${path}.switchCount`),
    primaryCcdId: optionalString(record.primaryCcdId, `${path}.primaryCcdId`),
    primaryPhysicalCoreId: optionalString(
      record.primaryPhysicalCoreId,
      `${path}.primaryPhysicalCoreId`),
    primaryLogicalProcessorId: optionalInteger(
      record.primaryLogicalProcessorId,
      `${path}.primaryLogicalProcessorId`),
    physicalCoreIds: record.physicalCoreIds === undefined || record.physicalCoreIds === null
      ? record.physicalCoreIds
      : requireStringArray(record.physicalCoreIds, `${path}.physicalCoreIds`)
  };
}

function decodeExclusiveBinding(value: unknown, path: string): CpuExclusiveBinding {
  const record = requireRecord(value, path);
  return {
    softwareId: requireNonEmptyString(record.softwareId, `${path}.softwareId`),
    softwareName: requireNonEmptyString(record.softwareName, `${path}.softwareName`),
    requestedExclusivePositionIds: requireStringArray(
      record.requestedExclusivePositionIds,
      `${path}.requestedExclusivePositionIds`),
    expandedExclusivePhysicalCoreIds: requireStringArray(
      record.expandedExclusivePhysicalCoreIds,
      `${path}.expandedExclusivePhysicalCoreIds`),
    requestedLockedPositionIds: requireStringArray(
      record.requestedLockedPositionIds,
      `${path}.requestedLockedPositionIds`),
    expandedLockedPhysicalCoreIds: requireStringArray(
      record.expandedLockedPhysicalCoreIds,
      `${path}.expandedLockedPhysicalCoreIds`),
    locksAffinity: requireBoolean(record.locksAffinity, `${path}.locksAffinity`),
    absolutePerformanceModeEnabled: requireBoolean(
      record.absolutePerformanceModeEnabled,
      `${path}.absolutePerformanceModeEnabled`),
    maximumOccupancyMode: requireNonEmptyString(
      record.maximumOccupancyMode,
      `${path}.maximumOccupancyMode`),
    updatedAt: requireTimestamp(record.updatedAt, `${path}.updatedAt`)
  };
}

function requireTimestamp(value: unknown, path: string): string {
  const text = requireNonEmptyString(value, path);
  if (!Number.isFinite(Date.parse(text))) {
    throw new ResponseDecodeError(path, "valid timestamp string");
  }
  return text;
}

function optionalTimestamp(value: unknown, path: string): string | null | undefined {
  if (value === undefined || value === null) {
    return value;
  }

  return requireTimestamp(value, path);
}

function requireNonNegativeNumber(value: unknown, path: string): number {
  const number = requireFiniteNumber(value, path);
  if (number < 0) {
    throw new ResponseDecodeError(path, "must be non-negative");
  }

  return number;
}

function requireNumberArray(value: unknown, path: string): number[] {
  return requireArray(value, path)
    .map((item, index) => requireNonNegativeSafeInteger(item, `${path}[${index}]`));
}

function optionalString(value: unknown, path: string): string | null | undefined {
  if (value === undefined || value === null) {
    return value;
  }
  if (typeof value !== "string") {
    throw new ResponseDecodeError(path, "string, null, or omitted");
  }
  return value;
}

function optionalNumber(value: unknown, path: string): number | null | undefined {
  if (value === undefined || value === null) {
    return value;
  }
  return requireFiniteNumber(value, path);
}

function optionalInteger(value: unknown, path: string): number | null | undefined {
  if (value === undefined || value === null) {
    return value;
  }
  return requireNonNegativeSafeInteger(value, path);
}
