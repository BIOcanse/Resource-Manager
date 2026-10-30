import type {
  BackendAppSettings,
  AppSettingsResult
} from "../../types.ts";
import {
  defineResponseDecoder,
  requireBoolean,
  requireNonEmptyString,
  requireNonNegativeSafeInteger,
  requireNullable,
  requireOneOf,
  requireRecord,
  requireSafeInteger,
  requireString,
  requireStringArray,
  ResponseDecodeError
} from "../../frontendRuntime/request/ResponseDecoder.ts";

import { decodeServiceRecovery } from "./serviceRecoveryDecoder.ts";
import { decodeReportSettings } from "./reportRuleSettingsDecoder.ts";
import { decodeHostMemoryProtectionSettings } from "../memoryProtection/memoryProtectionDecoders.ts";
import { decodeMemoryCpuTierSettings } from "../memoryCpuTier/memoryCpuTierDecoder.ts";

export type AppSettingsApplicationDisposition = NonNullable<
  AppSettingsResult["runtimeApplicationDisposition"]
>;

export type CommittedAppSettingsResult = Readonly<
  AppSettingsResult
  & Required<Pick<AppSettingsResult, "settings" | "revision">>
>;

const applicationDispositions = [
  "notRequested",
  "rejectedBeforeCommit",
  "committedAndApplied",
  "committedWithCapabilityConstraints",
  "committedWithDeliveryFailures",
  "savedNotApplied",
  "revisionConflict"
] as const satisfies readonly AppSettingsApplicationDisposition[];

const settingsSectionKeys = [
  "control",
  "performance",
  "systemIntegration",
  "debug",
  "publicService",
  "aiModelService"
] as const;

export const appSettingsResultDecoder =
  defineResponseDecoder<CommittedAppSettingsResult>(
    "app.settings.result.v1",
    (value) => {
      const record = requireRecord(value);
      const settingsRecord = requireRecord(record.settings, "$.settings");
      if ("appearance" in settingsRecord || "frontendRefreshIntervals" in record) {
        throw new ResponseDecodeError("$.settings", "Backend settings must not contain frontend preferences");
      }
      const performance = settingsRecord.performance;
      if (performance && typeof performance === "object"
          && ["smartMonitoringMode", "monitorRefreshIntervalMs", "resourceTableRefreshIntervalMs", "localSystemRefreshIntervalMs"]
            .some(key => key in performance)) {
        throw new ResponseDecodeError("$.settings.performance", "Backend settings must not contain frontend refresh preferences");
      }
      requireNonEmptyString(settingsRecord.version, "$.settings.version");
      for (const key of settingsSectionKeys) {
        if (settingsRecord[key] !== undefined) {
          requireRecord(settingsRecord[key], `$.settings.${key}`);
        }
      }

      if (settingsRecord.control !== undefined) {
        const control = requireRecord(settingsRecord.control, "$.settings.control");
        requireBoolean(control.keepAlive, "$.settings.control.keepAlive");
        const interval = requireSafeInteger(control.reassertionIntervalSeconds, "$.settings.control.reassertionIntervalSeconds");
        if (interval < 1 || interval > 86400) {
          throw new ResponseDecodeError("$.settings.control.reassertionIntervalSeconds", "Expected 1–86400 seconds");
        }
      }

      if (settingsRecord.reports !== undefined) {
        decodeReportSettings(settingsRecord.reports, "$.settings.reports");
      }
      if (settingsRecord.memoryCpuTier !== undefined) {
        decodeMemoryCpuTierSettings(settingsRecord.memoryCpuTier, "$.settings.memoryCpuTier");
      }
      if (settingsRecord.memoryProtection !== undefined && settingsRecord.memoryProtection !== null) {
        decodeHostMemoryProtectionSettings(settingsRecord.memoryProtection, "$.settings.memoryProtection");
      }

      const result: AppSettingsResult = {
        updatedAt: optional(record.updatedAt, "$.updatedAt", requireString),
        serviceRecovery: record.serviceRecovery === undefined ? undefined
          : requireNullable(record.serviceRecovery, "$.serviceRecovery", decodeServiceRecovery),
        settings: deepFreezeClone(settingsRecord) as BackendAppSettings,
        revision: requireNonEmptyString(record.revision, "$.revision"),
        storagePath: optional(record.storagePath, "$.storagePath", requireString),
        source: decodeSource(record.source),
        runtimeApplicationDisposition: optional(
          record.runtimeApplicationDisposition,
          "$.runtimeApplicationDisposition",
          (item, path) => requireOneOf(item, path, applicationDispositions)),
        runtimePlanVersion: optionalNullableInteger(
          record.runtimePlanVersion,
          "$.runtimePlanVersion"),
        runtimePublicationSequence: optionalNullableInteger(
          record.runtimePublicationSequence,
          "$.runtimePublicationSequence"),
        runtimeDeliveryFailureCount: optional(
          record.runtimeDeliveryFailureCount,
          "$.runtimeDeliveryFailureCount",
          requireNonNegativeSafeInteger),
        runtimeCapabilityConstrainedPaths: optional(
          record.runtimeCapabilityConstrainedPaths,
          "$.runtimeCapabilityConstrainedPaths",
          (item, path) => Object.freeze(requireStringArray(item, path))),
        runtimeFailureCode: optional(
          record.runtimeFailureCode,
          "$.runtimeFailureCode",
          (item, path) => requireNullable(item, path, requireString)),
        runtimeFailurePath: optional(
          record.runtimeFailurePath,
          "$.runtimeFailurePath",
          (item, path) => requireNullable(item, path, requireString))
      };
      return Object.freeze(result) as CommittedAppSettingsResult;
    });

function decodeSource(value: unknown): AppSettingsResult["source"] {
  if (value === undefined) {
    return undefined;
  }
  const source = requireRecord(value, "$.source");
  const kind = source.kind === undefined
    ? undefined
    : typeof source.kind === "number"
      ? requireSafeInteger(source.kind, "$.source.kind")
      : requireString(source.kind, "$.source.kind");
  return Object.freeze({
    kind,
    sourceVersion: optional(
      source.sourceVersion,
      "$.source.sourceVersion",
      requireString),
    inputSha256: optional(source.inputSha256, "$.source.inputSha256", requireString),
    effectiveSha256: optional(
      source.effectiveSha256,
      "$.source.effectiveSha256",
      requireString),
    rewritePerformed: optional(
      source.rewritePerformed,
      "$.source.rewritePerformed",
      requireBoolean),
    recoveryDisposition: optional(
      source.recoveryDisposition,
      "$.source.recoveryDisposition",
      requireString),
    recoveryArtifactPath: optional(
      source.recoveryArtifactPath,
      "$.source.recoveryArtifactPath",
      (item, path) => requireNullable(item, path, requireString)),
    lastKnownGoodPath: optional(
      source.lastKnownGoodPath,
      "$.source.lastKnownGoodPath",
      (item, path) => requireNullable(item, path, requireString))
  });
}

function optional<T>(
  value: unknown,
  path: string,
  decode: (value: unknown, path: string) => T
): T | undefined {
  return value === undefined ? undefined : decode(value, path);
}

function optionalNullableInteger(
  value: unknown,
  path: string
): number | null | undefined {
  return optional(value, path, (item, itemPath) =>
    requireNullable(item, itemPath, requireNonNegativeSafeInteger));
}

function deepFreezeClone<T>(value: T): T {
  const clone = structuredClone(value);
  const seen = new WeakSet<object>();
  const pending: unknown[] = [clone];
  while (pending.length > 0) {
    const item = pending.pop();
    if (typeof item !== "object" || item === null || seen.has(item)) {
      continue;
    }
    seen.add(item);
    for (const nested of Object.values(item)) {
      pending.push(nested);
    }
    Object.freeze(item);
  }
  return clone;
}

export function readSettingsFailureDisposition(
  value: unknown
): AppSettingsApplicationDisposition | null {
  try {
    const record = requireRecord(value);
    return requireOneOf(
      record.runtimeApplicationDisposition,
      "$.runtimeApplicationDisposition",
      applicationDispositions);
  } catch (error) {
    if (error instanceof ResponseDecodeError) {
      return null;
    }
    throw error;
  }
}
