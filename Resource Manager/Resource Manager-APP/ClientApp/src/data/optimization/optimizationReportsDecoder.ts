import type {
  OptimizationActivityContext,
  OptimizationRecorderStatus,
  OptimizationReportAction,
  OptimizationReportEvidence,
  OptimizationReportItem,
  OptimizationReportOverview,
  OptimizationReportTarget,
  ProtectedOptimizationTarget,
  TrustedOptimizationTarget
} from "../../types.ts";
import {
  defineResponseDecoder,
  requireArray,
  requireBoolean,
  requireFiniteNumber,
  requireNonEmptyString,
  requireNonNegativeSafeInteger,
  requireNullable,
  requireRecord,
  requireString,
  requireStringArray,
  ResponseDecodeError
} from "../../frontendRuntime/request/ResponseDecoder.ts";

export const optimizationReportsDecoder = defineResponseDecoder<OptimizationReportOverview>(
  "optimization.reports.overview.v2",
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
    if (status.activeReportCount !== reports.length) {
      throw new ResponseDecodeError(
        "$.status.activeReportCount",
        "count equal to $.reports.length");
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

  return Object.freeze({
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
    state: requireNonEmptyString(record.state, `${path}.state`),
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
    title: requireNonEmptyString(record.title, `${path}.title`),
    message: requireString(record.message, `${path}.message`),
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
    displayName: requireNonEmptyString(record.displayName, `${path}.displayName`),
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
    resourceKind: requireNonEmptyString(record.resourceKind, `${path}.resourceKind`),
    averageValue: requireFiniteNumber(record.averageValue, `${path}.averageValue`),
    peakValue: requireFiniteNumber(record.peakValue, `${path}.peakValue`),
    currentValue: requireFiniteNumber(record.currentValue, `${path}.currentValue`),
    averageDisplay: requireString(record.averageDisplay, `${path}.averageDisplay`),
    peakDisplay: requireString(record.peakDisplay, `${path}.peakDisplay`),
    currentDisplay: requireString(record.currentDisplay, `${path}.currentDisplay`),
    activeSampleCount: requireNonNegativeSafeInteger(
      record.activeSampleCount,
      `${path}.activeSampleCount`),
    sampleCount: requireNonNegativeSafeInteger(record.sampleCount, `${path}.sampleCount`),
    durationSeconds: requireNonNegativeFiniteNumber(
      record.durationSeconds,
      `${path}.durationSeconds`),
    details: Object.freeze(requireStringArray(record.details, `${path}.details`))
  });
}

function decodeAction(value: unknown, path: string): OptimizationReportAction {
  const record = requireRecord(value, path);
  return Object.freeze({
    id: requireNonEmptyString(record.id, `${path}.id`),
    label: requireNonEmptyString(record.label, `${path}.label`),
    kind: requireNonEmptyString(record.kind, `${path}.kind`),
    enabled: requireBoolean(record.enabled, `${path}.enabled`),
    disabledReason: requireNullable(
      record.disabledReason,
      `${path}.disabledReason`,
      requireString)
  });
}

function decodeTrustedTarget(value: unknown, path: string): TrustedOptimizationTarget {
  const record = requireRecord(value, path);
  return Object.freeze({
    id: requireNonEmptyString(record.id, `${path}.id`),
    targetType: requireNonEmptyString(record.targetType, `${path}.targetType`),
    targetKey: requireNonEmptyString(record.targetKey, `${path}.targetKey`),
    displayName: requireNonEmptyString(record.displayName, `${path}.displayName`),
    reportType: requireNonEmptyString(record.reportType, `${path}.reportType`),
    resourceKind: requireNonEmptyString(record.resourceKind, `${path}.resourceKind`),
    trustedAt: requireTimestamp(record.trustedAt, `${path}.trustedAt`),
    trustedReason: requireString(record.trustedReason, `${path}.trustedReason`),
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
  return Object.freeze({
    id: requireNonEmptyString(record.id, `${path}.id`),
    targetType: requireNonEmptyString(record.targetType, `${path}.targetType`),
    targetKey: requireNonEmptyString(record.targetKey, `${path}.targetKey`),
    displayName: requireNonEmptyString(record.displayName, `${path}.displayName`),
    softwareId: requireNullable(record.softwareId, `${path}.softwareId`, requireString),
    softwareName: requireNullable(record.softwareName, `${path}.softwareName`, requireString),
    softwareKind: requireNullable(record.softwareKind, `${path}.softwareKind`, requireString),
    protectedAt: requireTimestamp(record.protectedAt, `${path}.protectedAt`),
    protectedReason: requireString(record.protectedReason, `${path}.protectedReason`),
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
      `${path}.protectionLevel`)
  });
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

function requireNonNegativeFiniteNumber(value: unknown, path: string): number {
  const number = requireFiniteNumber(value, path);
  if (number < 0) {
    throw new ResponseDecodeError(path, "non-negative finite number");
  }
  return number;
}
