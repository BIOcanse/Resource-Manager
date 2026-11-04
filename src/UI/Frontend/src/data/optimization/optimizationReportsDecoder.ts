import type {
  OptimizationActivityContext,
  OptimizationRecorderStatus,
  OptimizationReportAction,
  OptimizationReportEvidence,
  OptimizationReportItem,
  OptimizationReportOverview,
  OptimizationReportTarget,
  OptimizationStorageResponseEvidence,
  ProtectedOptimizationTarget,
  TrustedOptimizationTarget
} from "../../types.ts";
import { storageResponseUnavailableEvidenceCodes } from "../../types.ts";
import {
  defineResponseDecoder,
  requireArray,
  requireBoolean,
  requireFiniteNumber,
  requireNonEmptyString,
  requireNonNegativeFiniteNumber,
  requireNonNegativeSafeInteger,
  requireNullable,
  requireOneOf,
  requireRecord,
  requireString,
  requireStringArray,
  ResponseDecodeError
} from "../../frontendRuntime/request/ResponseDecoder.ts";

// Match the backend optimization report states.
export const reportEntryStates = ["active", "history"] as const;

export const optimizationReportsDecoder = defineResponseDecoder<OptimizationReportOverview>(
  "optimization.reports.overview.v3",
  (value) => {
    const record = requireRecord(value);
    const reports = decodeArray(record.reports, "$.reports", decodeReport);
    const trustedTargets = decodeArray(
      record.trustedTargets,
      "$.trustedTargets",
      decodeTrustedTarget);
    const protectedTargets = decodeArray(
      record.protectedTargets,
      "$.protectedTargets",
      decodeProtectedTarget);
    const status = decodeStatus(record.status, "$.status");
    assertUniqueIds(reports, "$.reports");
    assertUniqueIds(trustedTargets, "$.trustedTargets");
    assertUniqueIds(protectedTargets, "$.protectedTargets");
    if (status.activeReportCount !== reports.filter((report) => report.state === "active").length) {
      throw new ResponseDecodeError(
        "$.status.activeReportCount",
        "count equal to the active entries in $.reports");
    }
    if (status.trustedCount !== trustedTargets.length) {
      throw new ResponseDecodeError(
        "$.status.trustedCount",
        "count equal to $.trustedTargets.length");
    }
    if (status.protectedCount !== protectedTargets.length) {
      throw new ResponseDecodeError(
        "$.status.protectedCount",
        "count equal to $.protectedTargets.length");
    }

    return Object.freeze({
      capturedAt: requireTimestamp(record.capturedAt, "$.capturedAt"),
      reports: Object.freeze(reports),
      trustedTargets: Object.freeze(trustedTargets),
      protectedTargets: Object.freeze(protectedTargets),
      status
    });
  });

function decodeStatus(value: unknown, path: string): OptimizationRecorderStatus {
  const record = requireRecord(value, path);
  const configuredRuleCount = requireNonNegativeSafeInteger(
    record.configuredRuleCount,
    `${path}.configuredRuleCount`);
  const availableRuleCount = requireNonNegativeSafeInteger(
    record.availableRuleCount,
    `${path}.availableRuleCount`);
  if (availableRuleCount > configuredRuleCount) {
    throw new ResponseDecodeError(
      `${path}.availableRuleCount`,
      "count not greater than configuredRuleCount");
  }
  const sampleIntervalSeconds = requireNonNegativeSafeInteger(
    record.sampleIntervalSeconds,
    `${path}.sampleIntervalSeconds`);
  if (sampleIntervalSeconds === 0) {
    throw new ResponseDecodeError(
      `${path}.sampleIntervalSeconds`,
      "positive safe integer");
  }

  const unavailableRules = requireStringArray(record.unavailableRules, `${path}.unavailableRules`);
  if (unavailableRules.length !== configuredRuleCount - availableRuleCount) {
    throw new ResponseDecodeError(`${path}.unavailableRules`, "one entry for each unavailable rule");
  }
  return Object.freeze({
    evaluationState: requireOneOf(record.evaluationState, `${path}.evaluationState`, ["notEvaluated", "completed", "failed"] as const),
    unavailableRules: Object.freeze(unavailableRules),
    lastEvaluationAt: requireNullable(
      record.lastEvaluationAt,
      `${path}.lastEvaluationAt`,
      requireTimestamp),
    lastObservedAt: requireNullable(
      record.lastObservedAt,
      `${path}.lastObservedAt`,
      requireTimestamp),
    configuredRuleCount,
    availableRuleCount,
    activeReportCount: requireNonNegativeSafeInteger(
      record.activeReportCount,
      `${path}.activeReportCount`),
    unrecordedReportCount: requireNonNegativeSafeInteger(
      record.unrecordedReportCount,
      `${path}.unrecordedReportCount`),
    trustedCount: requireNonNegativeSafeInteger(
      record.trustedCount,
      `${path}.trustedCount`),
    protectedCount: requireNonNegativeSafeInteger(
      record.protectedCount,
      `${path}.protectedCount`),
    sampleIntervalSeconds
  });
}

function decodeReport(value: unknown, path: string): OptimizationReportItem {
  const record = requireRecord(value, path);
  return Object.freeze({
    id: requireNonEmptyString(record.id, `${path}.id`),
    type: requireNonEmptyString(record.type, `${path}.type`),
    state: requireOneOf(record.state, `${path}.state`, reportEntryStates),
    trusted: requireBoolean(record.trusted, `${path}.trusted`),
    severity: requireNonEmptyString(record.severity, `${path}.severity`),
    confidence: requireNonEmptyString(record.confidence, `${path}.confidence`),
    createdAt: requireTimestamp(record.createdAt, `${path}.createdAt`),
    updatedAt: requireTimestamp(record.updatedAt, `${path}.updatedAt`),
    firstObservedAt: requireTimestamp(
      record.firstObservedAt,
      `${path}.firstObservedAt`),
    lastObservedAt: requireTimestamp(
      record.lastObservedAt,
      `${path}.lastObservedAt`),
    context: decodeContext(record.context, `${path}.context`),
    target: decodeTarget(record.target, `${path}.target`),
    evidence: decodeEvidence(record.evidence, `${path}.evidence`),
    suggestedActions: Object.freeze(decodeArray(
      record.suggestedActions,
      `${path}.suggestedActions`,
      decodeAction))
  });
}

function decodeContext(value: unknown, path: string): OptimizationActivityContext {
  const record = requireRecord(value, path);
  return Object.freeze({
    contextKind: requireNonEmptyString(record.contextKind, `${path}.contextKind`),
    foregroundProcessId: requireNullable(
      record.foregroundProcessId,
      `${path}.foregroundProcessId`,
      requireNonNegativeSafeInteger),
    foregroundProcessName: requireNullable(
      record.foregroundProcessName,
      `${path}.foregroundProcessName`,
      requireString),
    foregroundExecutablePath: requireNullable(
      record.foregroundExecutablePath,
      `${path}.foregroundExecutablePath`,
      requireString),
    foregroundSoftwareId: requireNullable(
      record.foregroundSoftwareId,
      `${path}.foregroundSoftwareId`,
      requireString),
    foregroundSoftwareName: requireNullable(
      record.foregroundSoftwareName,
      `${path}.foregroundSoftwareName`,
      requireString),
    confidence: requireNonEmptyString(record.confidence, `${path}.confidence`),
    evidence: Object.freeze(requireStringArray(record.evidence, `${path}.evidence`))
  });
}

function decodeTarget(value: unknown, path: string): OptimizationReportTarget {
  const record = requireRecord(value, path);
  return Object.freeze({
    targetType: requireNonEmptyString(record.targetType, `${path}.targetType`),
    targetKey: requireNonEmptyString(record.targetKey, `${path}.targetKey`),
    name: requireString(record.name, `${path}.name`),
    softwareId: requireNullable(record.softwareId, `${path}.softwareId`, requireString),
    softwareName: requireNullable(record.softwareName, `${path}.softwareName`, requireString),
    softwareKind: requireNullable(record.softwareKind, `${path}.softwareKind`, requireString),
    displayKind: requireNullable(record.displayKind, `${path}.displayKind`, requireString),
    processNames: Object.freeze(requireStringArray(
      record.processNames,
      `${path}.processNames`)),
    processIds: Object.freeze(requireArray(record.processIds, `${path}.processIds`)
      .map((item, index) => requireNonNegativeSafeInteger(
        item,
        `${path}.processIds[${index}]`))),
    driveLetter: requireNullable(record.driveLetter, `${path}.driveLetter`, requireString)
  });
}

function decodeEvidence(value: unknown, path: string): OptimizationReportEvidence {
  const record = requireRecord(value, path);
  return Object.freeze({
    process: record.process == null ? null : decodeProcessEvidence(record.process, `${path}.process`),
    storageResponse: record.storageResponse == null
      ? null
      : decodeStorageResponseEvidence(record.storageResponse, `${path}.storageResponse`),
    resourceKind: requireNonEmptyString(record.resourceKind, `${path}.resourceKind`),
    averageValue: requireFiniteNumber(record.averageValue, `${path}.averageValue`),
    peakValue: requireFiniteNumber(record.peakValue, `${path}.peakValue`),
    currentValue: requireFiniteNumber(record.currentValue, `${path}.currentValue`),
    valueUnit: requireString(record.valueUnit, `${path}.valueUnit`),
    activeSampleCount: requireNonNegativeSafeInteger(
      record.activeSampleCount,
      `${path}.activeSampleCount`),
    sampleCount: requireNonNegativeSafeInteger(record.sampleCount, `${path}.sampleCount`),
    durationSeconds: requireNonNegativeFiniteNumber(
      record.durationSeconds,
      `${path}.durationSeconds`),
  });
}

function decodeProcessEvidence(value: unknown, path: string): NonNullable<OptimizationReportEvidence["process"]> {
  const record = requireRecord(value, path);
  return Object.freeze({
    processId: requireNonNegativeSafeInteger(record.processId, `${path}.processId`),
    processStartKey: requireNonEmptyString(record.processStartKey, `${path}.processStartKey`),
    name: requireNonEmptyString(record.name, `${path}.name`),
    memoryBytes: requireNullable(record.memoryBytes, `${path}.memoryBytes`, requireNonNegativeFiniteNumber),
    idleDurationSeconds: requireNullable(record.idleDurationSeconds, `${path}.idleDurationSeconds`, requireNonNegativeFiniteNumber),
    commandLine: requireNullable(record.commandLine, `${path}.commandLine`, requireString)
  });
}

/** Missing storage evidence and unproven disk-presence flags remain null. Require unique known gap codes. */
function decodeStorageResponseEvidence(value: unknown, path: string): OptimizationStorageResponseEvidence {
  const record = requireRecord(value, path);
  const unavailableEvidence = requireArray(record.unavailableEvidence, `${path}.unavailableEvidence`)
    .map((item, index) => requireOneOf(item, `${path}.unavailableEvidence[${index}]`, storageResponseUnavailableEvidenceCodes));
  if (new Set(unavailableEvidence).size !== unavailableEvidence.length) {
    throw new ResponseDecodeError(`${path}.unavailableEvidence`, "unique codes");
  }
  const number = (field: string) => requireNullable(record[field], `${path}.${field}`, requireNonNegativeFiniteNumber);
  const flag = (field: string) => requireNullable(record[field], `${path}.${field}`, requireBoolean);
  return Object.freeze({
    physicalDiskIdentity: requireNonEmptyString(record.physicalDiskIdentity, `${path}.physicalDiskIdentity`),
    queueLength: number("queueLength"),
    completedReadOperationsPerSecond: number("completedReadOperationsPerSecond"),
    completedWriteOperationsPerSecond: number("completedWriteOperationsPerSecond"),
    latencyMilliseconds: number("latencyMilliseconds"),
    holdsPageFile: flag("holdsPageFile"),
    holdsMemoryProtectionReserve: flag("holdsMemoryProtectionReserve"),
    holdsResourceManagerData: flag("holdsResourceManagerData"),
    unavailableEvidence: Object.freeze(unavailableEvidence)
  });
}

function decodeAction(value: unknown, path: string): OptimizationReportAction {
  const record = requireRecord(value, path);
  return Object.freeze({
    id: requireNonEmptyString(record.id, `${path}.id`),
    kind: requireNonEmptyString(record.kind, `${path}.kind`),
    enabled: requireBoolean(record.enabled, `${path}.enabled`),
    // Absent confirmation and risk fields mean no confirmation requirement and no risk code.
    requiresConfirmation: record.requiresConfirmation === undefined
      ? false : requireBoolean(record.requiresConfirmation, `${path}.requiresConfirmation`),
    riskCodes: Object.freeze(record.riskCodes === undefined
      ? [] : requireStringArray(record.riskCodes, `${path}.riskCodes`))
  });
}

function decodeTrustedTarget(value: unknown, path: string): TrustedOptimizationTarget {
  const record = requireRecord(value, path);
  return Object.freeze({
    id: requireNonEmptyString(record.id, `${path}.id`),
    targetType: requireNonEmptyString(record.targetType, `${path}.targetType`),
    targetKey: requireNonEmptyString(record.targetKey, `${path}.targetKey`),
    name: requireString(record.name, `${path}.name`),
    reportType: requireNonEmptyString(record.reportType, `${path}.reportType`),
    resourceKind: requireNonEmptyString(record.resourceKind, `${path}.resourceKind`),
    trustedAt: requireTimestamp(record.trustedAt, `${path}.trustedAt`),
    createdFromReportId: requireNonEmptyString(
      record.createdFromReportId,
      `${path}.createdFromReportId`),
    lastVerifiedAt: requireNullable(
      record.lastVerifiedAt,
      `${path}.lastVerifiedAt`,
      requireTimestamp),
    state: requireNonEmptyString(record.state, `${path}.state`)
  });
}

function decodeProtectedTarget(
  value: unknown,
  path: string
): ProtectedOptimizationTarget {
  const record = requireRecord(value, path);
  // A user override requires both the replaced default level and its source.
  const defaultProtectionLevel = optionalNullable(
    record.defaultProtectionLevel,
    `${path}.defaultProtectionLevel`,
    requireNonNegativeSafeInteger);
  const defaultProtectionSource = optionalNullable(
    record.defaultProtectionSource,
    `${path}.defaultProtectionSource`,
    requireNonEmptyString);
  if ((defaultProtectionLevel === null) !== (defaultProtectionSource === null)) {
    throw new ResponseDecodeError(
      defaultProtectionLevel === null ? `${path}.defaultProtectionLevel` : `${path}.defaultProtectionSource`,
      "defaultProtectionLevel and defaultProtectionSource both present or both null");
  }
  return Object.freeze({
    id: requireNonEmptyString(record.id, `${path}.id`),
    targetType: requireNonEmptyString(record.targetType, `${path}.targetType`),
    targetKey: requireNonEmptyString(record.targetKey, `${path}.targetKey`),
    softwareId: requireNullable(record.softwareId, `${path}.softwareId`, requireString),
    softwareName: requireNullable(record.softwareName, `${path}.softwareName`, requireString),
    softwareKind: requireNullable(record.softwareKind, `${path}.softwareKind`, requireString),
    protectedAt: requireTimestamp(record.protectedAt, `${path}.protectedAt`),
    protectionSource: requireString(record.protectionSource, `${path}.protectionSource`),
    createdFromReportId: requireNonEmptyString(
      record.createdFromReportId,
      `${path}.createdFromReportId`),
    lastVerifiedAt: requireNullable(
      record.lastVerifiedAt,
      `${path}.lastVerifiedAt`,
      requireTimestamp),
    state: requireNonEmptyString(record.state, `${path}.state`),
    allowsPlacementAvoidance: requireBoolean(
      record.allowsPlacementAvoidance,
      `${path}.allowsPlacementAvoidance`),
    protectionLevel: requireNonNegativeSafeInteger(
      record.protectionLevel,
      `${path}.protectionLevel`),
    defaultProtectionLevel,
    defaultProtectionSource,
    policyGroupId: optionalNullable(record.policyGroupId, `${path}.policyGroupId`, requireString),
    processName: optionalNullable(record.processName, `${path}.processName`, requireString),
    executablePath: optionalNullable(record.executablePath, `${path}.executablePath`, requireString)
  });
}

/** Decode effective protection entries using the same shape as report protectedTargets. */
export const protectedTargetListDecoder = defineResponseDecoder<readonly ProtectedOptimizationTarget[]>(
  "optimization.protection.list.v1",
  (value) => {
    const targets = decodeArray(value, "$", decodeProtectedTarget);
    assertUniqueIds(targets, "$");
    return Object.freeze(targets);
  });

/** Decode omitted and explicit null fields as null. */
function optionalNullable<T>(
  value: unknown,
  path: string,
  decode: (value: unknown, path: string) => T
): T | null {
  return value === undefined ? null : requireNullable(value, path, decode);
}

function decodeArray<T>(
  value: unknown,
  path: string,
  decode: (value: unknown, path: string) => T
): T[] {
  return requireArray(value, path)
    .map((item, index) => decode(item, `${path}[${index}]`));
}

function assertUniqueIds(
  values: readonly { id: string }[],
  path: string
): void {
  const ids = new Set<string>();
  values.forEach((value, index) => {
    if (ids.has(value.id)) {
      throw new ResponseDecodeError(`${path}[${index}].id`, "unique id");
    }
    ids.add(value.id);
  });
}

function requireTimestamp(value: unknown, path: string): string {
  const timestamp = requireNonEmptyString(value, path);
  if (!Number.isFinite(Date.parse(timestamp))) {
    throw new ResponseDecodeError(path, "valid timestamp string");
  }
  return timestamp;
}

