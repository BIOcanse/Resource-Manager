import type {
  AppSettings,
  FrontendRefreshIntervals,
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

export type AppSettingsApplicationDisposition = NonNullable<
  AppSettingsResult["runtimeApplicationDisposition"]
>;

export type CommittedAppSettingsResult = Readonly<
  AppSettingsResult
  & Required<Pick<AppSettingsResult, "settings" | "revision" | "frontendRefreshIntervals">>
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
  "appearance",
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
      requireNonEmptyString(settingsRecord.version, "$.settings.version");
      for (const key of settingsSectionKeys) {
        if (settingsRecord[key] !== undefined) {
          requireRecord(settingsRecord[key], `$.settings.${key}`);
        }
      }

      if (settingsRecord.control !== undefined) {
        const control = requireRecord(settingsRecord.control, "$.settings.control");
        const interval = requireSafeInteger(control.reassertionIntervalSeconds, "$.settings.control.reassertionIntervalSeconds");
        if (interval < 1 || interval > 86400) {
          throw new ResponseDecodeError("$.settings.control.reassertionIntervalSeconds", "Expected 1–86400 seconds");
        }
      }

      if (settingsRecord.reports !== undefined) {
        decodeReportSettings(settingsRecord.reports, "$.settings.reports");
      }

      const result: AppSettingsResult = {
        serviceRecovery: record.serviceRecovery === undefined ? undefined : decodeServiceRecovery(record.serviceRecovery),
        settings: deepFreezeClone(settingsRecord) as AppSettings,
        frontendRefreshIntervals: decodeFrontendRefreshIntervals(record.frontendRefreshIntervals),
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
  const freeze = (item: unknown): void => {
    if (typeof item !== "object" || item === null || seen.has(item)) {
      return;
    }
    seen.add(item);
    for (const nested of Object.values(item)) {
      freeze(nested);
    }
    Object.freeze(item);
  };
  freeze(clone);
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

function decodeFrontendRefreshIntervals(value: unknown): FrontendRefreshIntervals {
  const path = "$.frontendRefreshIntervals";
  const record = requireRecord(value, path);
  const keys = ["monitor", "resourceTable", "localSystem"] as const;
  const presets = ["responsive", "balanced", "lowPower", "quiet"] as const;
  const decodeInterval = (value: unknown, path: string) => {
    const interval = requireSafeInteger(value, path);
    if (interval < 1) throw new ResponseDecodeError(path, "Expected a positive interval");
    return interval;
  };
  const profile = (name: "foreground" | "backgroundOptimized") => {
    const item = requireRecord(record[name], `${path}.${name}`);
    return Object.freeze(Object.fromEntries(keys.map(key => [key, decodeInterval(item[key], `${path}.${name}.${key}`)])));
  };
  const catalogue = requireRecord(record.presets, `${path}.presets`);
  const defaults = requireRecord(record.defaults, `${path}.defaults`);
  const result = {
    foreground: profile("foreground"),
    backgroundOptimized: profile("backgroundOptimized"),
    presets: Object.fromEntries(keys.map(key => {
      const item = requireRecord(catalogue[key], `${path}.presets.${key}`);
      return [key, Object.freeze(Object.fromEntries(presets.map(preset => [preset,
        decodeInterval(item[preset], `${path}.presets.${key}.${preset}`)])))];
    })),
    defaults: Object.fromEntries(keys.map(key => {
      const itemPath = `${path}.defaults.${key}`;
      const item = requireRecord(defaults[key], itemPath);
      return [key, Object.freeze({
        mode: requireOneOf(item.mode, `${itemPath}.mode`, ["aotu", "preset", "custom"]),
        preset: requireOneOf(item.preset, `${itemPath}.preset`, presets),
        customValue: decodeInterval(item.customValue, `${itemPath}.customValue`)
      })];
    }))
  };
  return deepFreezeClone(result) as FrontendRefreshIntervals;
}
