import type { RequestClient } from "../../frontendRuntime/request/RequestClient.ts";
import { RequestProblem } from "../../frontendRuntime/request/RequestProblem.ts";
import {
  defineResponseDecoder,
  requireArray,
  requireBoolean,
  requireFiniteNumber,
  requireNonEmptyString,
  requireNonNegativeSafeInteger,
  requireNullable,
  requireOneOf,
  requireRecord,
  requireString,
  ResponseDecodeError
} from "../../frontendRuntime/request/ResponseDecoder.ts";
import { uiText } from "../../text.ts";
import { systemEventChannels, systemEventRuleCategories, systemEventTargetKinds,
  type SystemEventChannel, type SystemEventRuleCategory, type SystemEventTargetKind } from "./systemEvents.ts";

/* Save the complete backend document with only edited fields changed, preserving unknown fields. Defaults and commit_immediate_percent remain backend-owned; save responses replace the document. */

export interface SystemEventRule {
  readonly channel: SystemEventChannel;
  readonly provider: string;
  readonly ids: readonly number[];
  readonly category: SystemEventRuleCategory;
  readonly targetKind: SystemEventTargetKind;
}

export interface SystemEventSettings {
  readonly enabled: boolean;
  readonly historyAfterMs: number;
  readonly eventTailCapacity: number;
  readonly entryCapacity: number;
  readonly rules: readonly SystemEventRule[];
  readonly liveKernelRoot: string;
  readonly liveKernelDirectories: readonly string[];
  readonly liveKernelEntryCapacity: number;
}

export interface FreezeRecorderConfig {
  readonly enabled: boolean;
  readonly slots: number;
  readonly sampleIntervalMs: number;
  readonly reviewWindowMs: number;
  readonly commitImmediatePercent: number | null;
  readonly physicalAvailablePercent: number;
  readonly nonpagedPoolPercent: number;
  readonly storageUnresponsiveConsecutive: number;
}

/** Retain the complete wire document as the save baseline, including unknown fields. */
export interface FreezeSettings {
  readonly systemEvents: SystemEventSettings;
  readonly freezeRecorder: FreezeRecorderConfig;
  readonly ringPath: string | null;
  readonly wire: FreezeSettingsWire;
}

export type FreezeSettingsWire = Readonly<Record<string, unknown>> & {
  readonly systemEvents: Readonly<Record<string, unknown>>;
  readonly freezeRecorder: Readonly<Record<string, unknown>>;
  readonly ring_path: string | null;
};

export const freezeSettingsDecoder = defineResponseDecoder<FreezeSettings>("freeze-reports.settings.v1", (value) => decodeFreezeSettings(value));

export function decodeFreezeSettings(value: unknown, path = "$"): FreezeSettings {
  const record = requireRecord(value, path);
  const systemEvents = decodeSystemEventSettings(record.systemEvents, `${path}.systemEvents`);
  const freezeRecorder = decodeFreezeRecorderConfig(record.freezeRecorder, `${path}.freezeRecorder`);
  const ringPath = requireNullable(record.ring_path, `${path}.ring_path`, requireNonEmptyString);
  return Object.freeze({ systemEvents, freezeRecorder, ringPath, wire: structuredClone(record) as FreezeSettingsWire });
}

function decodeSystemEventSettings(value: unknown, path: string): SystemEventSettings {
  const record = requireRecord(value, path);
  return Object.freeze({
    enabled: requireBoolean(record.enabled, `${path}.enabled`),
    historyAfterMs: requirePositiveInteger(record.history_after_ms, `${path}.history_after_ms`),
    eventTailCapacity: requirePositiveInteger(record.event_tail_capacity, `${path}.event_tail_capacity`),
    entryCapacity: requirePositiveInteger(record.entry_capacity, `${path}.entry_capacity`),
    rules: Object.freeze(requireArray(record.rules, `${path}.rules`).map((item, index) => decodeRule(item, `${path}.rules[${index}]`))),
    liveKernelRoot: requireString(record.live_kernel_root, `${path}.live_kernel_root`),
    liveKernelDirectories: Object.freeze(requireArray(record.live_kernel_directories, `${path}.live_kernel_directories`)
      .map((item, index) => requireNonEmptyString(item, `${path}.live_kernel_directories[${index}]`))),
    liveKernelEntryCapacity: requirePositiveInteger(record.live_kernel_entry_capacity, `${path}.live_kernel_entry_capacity`)
  });
}

function decodeRule(value: unknown, path: string): SystemEventRule {
  const record = requireRecord(value, path);
  return Object.freeze({
    channel: requireOneOf(record.channel, `${path}.channel`, systemEventChannels),
    provider: requireNonEmptyString(record.provider, `${path}.provider`),
    ids: Object.freeze(requireArray(record.ids, `${path}.ids`).map((item, index) => requireNonNegativeSafeInteger(item, `${path}.ids[${index}]`))),
    category: requireOneOf(record.category, `${path}.category`, systemEventRuleCategories),
    targetKind: requireOneOf(record.target_kind, `${path}.target_kind`, systemEventTargetKinds)
  });
}

export function decodeFreezeRecorderConfig(value: unknown, path: string): FreezeRecorderConfig {
  const record = requireRecord(value, path);
  return Object.freeze({
    enabled: requireBoolean(record.enabled, `${path}.enabled`),
    slots: requirePositiveInteger(record.slots, `${path}.slots`),
    sampleIntervalMs: requirePositiveInteger(record.sample_interval_ms, `${path}.sample_interval_ms`),
    reviewWindowMs: requirePositiveInteger(record.review_window_ms, `${path}.review_window_ms`),
    commitImmediatePercent: requireNullable(record.commit_immediate_percent, `${path}.commit_immediate_percent`, requirePercent),
    physicalAvailablePercent: requirePercent(record.physical_available_percent, `${path}.physical_available_percent`),
    nonpagedPoolPercent: requirePercent(record.nonpaged_pool_percent, `${path}.nonpaged_pool_percent`),
    storageUnresponsiveConsecutive: requirePositiveInteger(record.storage_unresponsive_consecutive, `${path}.storage_unresponsive_consecutive`)
  });
}

function requirePositiveInteger(value: unknown, path: string): number {
  const number = requireNonNegativeSafeInteger(value, path);
  if (number === 0) throw new ResponseDecodeError(path, "a positive integer");
  return number;
}

function requirePercent(value: unknown, path: string): number {
  const number = requireFiniteNumber(value, path);
  if (number <= 0 || number > 100) throw new ResponseDecodeError(path, "a percentage in (0, 100]");
  return number;
}

/** Editable wire fields; rules replace the entire table. */
export interface SystemEventSettingsChange {
  readonly enabled?: boolean;
  readonly history_after_ms?: number;
  readonly event_tail_capacity?: number;
  readonly entry_capacity?: number;
  readonly rules?: readonly SystemEventRule[];
  readonly live_kernel_root?: string;
  readonly live_kernel_directories?: readonly string[];
  readonly live_kernel_entry_capacity?: number;
}

export interface FreezeRecorderSettingsChange {
  readonly enabled?: boolean;
  readonly slots?: number;
  readonly sample_interval_ms?: number;
  readonly review_window_ms?: number;
  readonly physical_available_percent?: number;
  readonly nonpaged_pool_percent?: number;
  readonly storage_unresponsive_consecutive?: number;
}

export interface FreezeSettingsChange {
  readonly systemEvents?: SystemEventSettingsChange;
  readonly freezeRecorder?: FreezeRecorderSettingsChange;
  readonly ringPath?: string | null;
}

/** Merge draft changes; the latest edit wins for each field. */
export function mergeFreezeSettingsChange(current: FreezeSettingsChange, next: FreezeSettingsChange): FreezeSettingsChange {
  return {
    ...(current.systemEvents || next.systemEvents ? { systemEvents: { ...current.systemEvents, ...next.systemEvents } } : {}),
    ...(current.freezeRecorder || next.freezeRecorder ? { freezeRecorder: { ...current.freezeRecorder, ...next.freezeRecorder } } : {}),
    ...("ringPath" in next ? { ringPath: next.ringPath } : "ringPath" in current ? { ringPath: current.ringPath } : {})
  };
}

/** Build the complete save document from the baseline and edits. */
export function freezeSettingsRequestBody(wire: FreezeSettingsWire, change: FreezeSettingsChange): Record<string, unknown> {
  const { rules, ...eventFields } = change.systemEvents ?? {};
  return {
    ...wire,
    systemEvents: {
      ...wire.systemEvents,
      ...eventFields,
      ...(rules ? { rules: rules.map((rule) => ({ channel: rule.channel, provider: rule.provider, ids: [...rule.ids],
        category: rule.category, target_kind: rule.targetKind })) } : {})
    },
    freezeRecorder: { ...wire.freezeRecorder, ...change.freezeRecorder },
    ring_path: "ringPath" in change ? change.ringPath ?? null : wire.ring_path
  };
}

/** Display backend settings with unsaved edits applied. */
export function freezeSettingsWithChange(settings: FreezeSettings, change: FreezeSettingsChange): FreezeSettings {
  return decodeFreezeSettings(freezeSettingsRequestBody(settings.wire, change));
}

type Client = Pick<RequestClient, "request">;
const settingsPath = "/api/freeze-recorder/settings";

export function readFreezeSettings(client: Client, signal?: AbortSignal): Promise<FreezeSettings> {
  return client.request({ key: "freeze-reports.settings.read", url: settingsPath, decoder: freezeSettingsDecoder,
    fallbackError: uiText.freezeReports.settings.readFailed, signal });
}

export type FreezeSettingsSaveResult =
  | { readonly status: "saved"; readonly settings: FreezeSettings }
  | { readonly status: "invalid" | "conflict" | "resource-bound" };

export async function saveFreezeSettings(client: Client, wire: FreezeSettingsWire, change: FreezeSettingsChange): Promise<FreezeSettingsSaveResult> {
  try {
    const settings = await client.request({ key: "freeze-reports.settings.save", url: settingsPath, decoder: freezeSettingsDecoder,
      fallbackError: uiText.freezeReports.settings.saveFailed,
      request: { method: "PATCH", headers: { "Content-Type": "application/json" }, body: JSON.stringify(freezeSettingsRequestBody(wire, change)) } });
    return { status: "saved", settings };
  } catch (error) {
    if (error instanceof RequestProblem) {
      if (error.status === 400) return { status: "invalid" };
      if (error.status === 409) return { status: "conflict" };
      if (error.status === 503) return { status: "resource-bound" };
    }
    throw error;
  }
}

/** Display incomplete edits without decoding; freezeSettingsWithChange validates save eligibility. */
export function freezeSettingsDraftView(settings: FreezeSettings, change: FreezeSettingsChange): FreezeSettings {
  const events = change.systemEvents ?? {};
  const recorder = change.freezeRecorder ?? {};
  const pick = <T,>(next: T | undefined, current: T) => next === undefined ? current : next;
  return {
    ...settings,
    systemEvents: {
      enabled: pick(events.enabled, settings.systemEvents.enabled),
      historyAfterMs: pick(events.history_after_ms, settings.systemEvents.historyAfterMs),
      eventTailCapacity: pick(events.event_tail_capacity, settings.systemEvents.eventTailCapacity),
      entryCapacity: pick(events.entry_capacity, settings.systemEvents.entryCapacity),
      rules: pick(events.rules, settings.systemEvents.rules),
      liveKernelRoot: pick(events.live_kernel_root, settings.systemEvents.liveKernelRoot),
      liveKernelDirectories: pick(events.live_kernel_directories, settings.systemEvents.liveKernelDirectories),
      liveKernelEntryCapacity: pick(events.live_kernel_entry_capacity, settings.systemEvents.liveKernelEntryCapacity)
    },
    freezeRecorder: {
      ...settings.freezeRecorder,
      enabled: pick(recorder.enabled, settings.freezeRecorder.enabled),
      slots: pick(recorder.slots, settings.freezeRecorder.slots),
      sampleIntervalMs: pick(recorder.sample_interval_ms, settings.freezeRecorder.sampleIntervalMs),
      reviewWindowMs: pick(recorder.review_window_ms, settings.freezeRecorder.reviewWindowMs),
      physicalAvailablePercent: pick(recorder.physical_available_percent, settings.freezeRecorder.physicalAvailablePercent),
      nonpagedPoolPercent: pick(recorder.nonpaged_pool_percent, settings.freezeRecorder.nonpagedPoolPercent),
      storageUnresponsiveConsecutive: pick(recorder.storage_unresponsive_consecutive, settings.freezeRecorder.storageUnresponsiveConsecutive)
    },
    ringPath: "ringPath" in change ? change.ringPath ?? null : settings.ringPath
  };
}

/** Parse nonnegative integer tokens; return null if any token is invalid. */
export function parseEventIds(text: string): number[] | null {
  const parts = text.split(/[\s,，、;；]+/).filter(Boolean);
  const ids: number[] = [];
  for (const part of parts) {
    if (!/^\d+$/.test(part)) return null;
    const id = Number(part);
    if (!Number.isSafeInteger(id) || id > 65_535) return null;
    if (!ids.includes(id)) ids.push(id);
  }
  return ids;
}
