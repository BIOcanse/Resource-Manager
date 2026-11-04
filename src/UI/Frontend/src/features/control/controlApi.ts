import {
  defineResponseDecoder,
  requireArray,
  requireBoolean,
  requireFiniteNumber,
  requireNonEmptyString,
  requireOneOf,
  requireRecord,
  requireString,
  ResponseDecodeError
} from "../../frontendRuntime/request/ResponseDecoder.ts";
import type { RequestClient } from "../../frontendRuntime/request/RequestClient.ts";
import { uiText } from "../../text.ts";
import { controlAccessLevels, controlUnavailableKinds } from "./controlTypes.ts";
import { readGpuVfBinding, readGpuVfPoints } from "./gpuVfCurveApi.ts";
import type {
  ControlAccessLevel,
  ControlApplyOutcome,
  ControlObjectDesiredState,
  ControlPreset,
  ControlPresetCatalog,
  ControlCapability,
  ControlCurvePoint,
  ControlNumberRange,
  ControlObject,
  ControlInstanceCatalog,
  ControlObjectCatalog,
  ControlSetting,
  ControlStateView
} from "./controlTypes.ts";

const valueKinds = ["toggle", "number", "curve", "gpu-vf-curve-reference"] as const;
/** Accepted execution snapshots are backend facts, never a browser command. */
export function controlSettingsInput(settings: readonly ControlSetting[]): ControlSetting[] {
  return settings.map(({ gpuVfCurveExecution: _accepted, ...input }) => input);
}
// A page action can serialize several slow EC transactions for multiple fans.
export const controlHardwareRequestTimeoutMs = 120_000;

function optionalString(value: unknown, path: string): string | null {
  return value === null || value === undefined ? null : requireString(value, path);
}

function optionalOrdinal(value: unknown, path: string): number | null {
  if (value === null || value === undefined) return null;
  const number = requireFiniteNumber(value, path);
  if (!Number.isInteger(number) || number < 0) throw new ResponseDecodeError(path, "a nonnegative integer");
  return number;
}

function readRange(value: unknown, path: string): ControlNumberRange | null {
  if (value === null || value === undefined) {
    return null;
  }
  const record = requireRecord(value, path);
  const range: ControlNumberRange = {
    minimum: requireFiniteNumber(record.minimum, `${path}.minimum`),
    maximum: requireFiniteNumber(record.maximum, `${path}.maximum`),
    step: requireFiniteNumber(record.step, `${path}.step`),
    unit: requireString(record.unit, `${path}.unit`),
    defaultValue: record.defaultValue === null || record.defaultValue === undefined
      ? null
      : requireFiniteNumber(record.defaultValue, `${path}.defaultValue`)
  };
  if (range.maximum < range.minimum || range.step <= 0) {
    throw new ResponseDecodeError(path, "an ordered range with a positive step");
  }
  return range;
}

function readCapability(value: unknown, path: string): ControlCapability {
  const record = requireRecord(value, path);
  const supported = requireBoolean(record.supported, `${path}.supported`);
  const unavailableReason = readControlReason(record.unavailableReason, `${path}.unavailableReason`);
  // Unavailable facts must include a backend reason; consumers do not infer one.
  if (!supported && !unavailableReason) {
    throw new ResponseDecodeError(path, "an unavailable capability to carry its reason");
  }
  return {
    id: requireNonEmptyString(record.id, `${path}.id`),
    valueKind: requireOneOf(record.valueKind, `${path}.valueKind`, valueKinds),
    supported,
    unavailableReason,
    unavailableKind: record.unavailableKind === null
      || record.unavailableKind === undefined
      ? null
      : requireOneOf(
        record.unavailableKind,
        `${path}.unavailableKind`,
        controlUnavailableKinds),
    requiredComponentId: optionalString(record.requiredComponentId, `${path}.requiredComponentId`),
    range: readRange(record.range, `${path}.range`),
    channel: optionalString(record.channel, `${path}.channel`),
    requiredAccessLevel: requireOneOf(
      record.requiredAccessLevel,
      `${path}.requiredAccessLevel`,
      controlAccessLevels),
    curvePointLimit: readCurvePointLimit(record.curvePointLimit, `${path}.curvePointLimit`)
  };
}

function readCurvePointLimit(value: unknown, path: string): number | null {
  if (value === null || value === undefined) {
    return null;
  }
  const limit = requireFiniteNumber(value, path);
  if (!Number.isInteger(limit) || limit < 1) {
    throw new ResponseDecodeError(path, "a positive whole number of curve points");
  }
  return limit;
}

function readObject(value: unknown, path: string): ControlObject {
  const record = requireRecord(value, path);
  const platform = requireRecord(record.platform, `${path}.platform`);
  return {
    id: requireNonEmptyString(record.id, `${path}.id`),
    kind: requireNonEmptyString(record.kind, `${path}.kind`),
    reportedName: optionalString(record.reportedName, `${path}.reportedName`),
    ordinal: optionalOrdinal(record.ordinal, `${path}.ordinal`),
    adapterIndex: optionalOrdinal(record.adapterIndex, `${path}.adapterIndex`),
    platform: {
      operatingSystem: requireNonEmptyString(
        platform.operatingSystem,
        `${path}.platform.operatingSystem`),
      vendor: requireNonEmptyString(platform.vendor, `${path}.platform.vendor`)
    },
    fanManagementModes: record.kind === "fan" ? requireArray(record.fanManagementModes, path + ".fanManagementModes").map((value, index) => {
      const at = path + ".fanManagementModes[" + index + "]";
      const mode = requireRecord(value, at);
      const available = requireBoolean(mode.available, at + ".available");
      const unavailableReason = readControlReason(mode.unavailableReason, at + ".unavailableReason");
      const unavailableKind = available ? null : requireOneOf(mode.unavailableKind, at + ".unavailableKind", controlUnavailableKinds);
      if (!available && !unavailableReason) throw new ResponseDecodeError(at, "an unavailable mode with a reason");
      return { id: requireOneOf(mode.id, at + ".id", ["firmware-management", "firmware-write", "software-management"] as const),
        available, unavailableReason, unavailableKind,
        items: requireArray(mode.items, at + ".items").map((value, itemIndex) => {
          const itemPath = at + ".items[" + itemIndex + "]";
          const item = requireRecord(value, itemPath);
          return { capabilityId: requireNonEmptyString(item.capabilityId, itemPath + ".capabilityId"),
            curveExecution: optionalString(item.curveExecution, itemPath + ".curveExecution") };
        }) };
    }) : null,
    softwareTakeoverCoversAllFans: record.kind === "fan" ? requireBoolean(record.softwareTakeoverCoversAllFans, path + ".softwareTakeoverCoversAllFans") : false,
    firmwareCurveKind: optionalString(record.firmwareCurveKind, path + ".firmwareCurveKind"),
    capabilities: requireArray(record.capabilities, `${path}.capabilities`)
      .map((row, index) => readCapability(row, `${path}.capabilities[${index}]`)),
    detail: optionalString(record.detail, `${path}.detail`),
    terms: record.terms === null || record.terms === undefined
      ? []
      : requireArray(record.terms, `${path}.terms`)
        .map((term, index) => requireNonEmptyString(term, `${path}.terms[${index}]`)),
    gpuAttachment: optionalString(record.gpuAttachment, `${path}.gpuAttachment`),
    isControllable: requireBoolean(record.isControllable, `${path}.isControllable`)
  };
}

export const controlObjectsDecoder = defineResponseDecoder<ControlObjectCatalog>(
  "control.objects.v1",
  (value) => {
    const record = requireRecord(value, "$");
    return {
      objects: requireArray(record.objects, "$.objects")
        .map((row, index) => readObject(row, `$.objects[${index}]`)),
      readAt: requireNonEmptyString(record.readAt, "$.readAt")
    };
  });

export function getControlObjects(
  requestClient: Pick<RequestClient, "request">,
  signal?: AbortSignal
): Promise<ControlObjectCatalog> {
  return requestClient.request({
    key: "control.objects",
    url: "/api/control/objects",
    fallbackError: uiText.control.loadFailed,
    decoder: controlObjectsDecoder,
    signal,
    request: { method: "GET" }
  });
}

/** Read the active firmware curve on demand to avoid slow EC transactions during catalog loading. Null means unavailable, not an all-zero curve. */
export function getControlObjectCurve(
  requestClient: Pick<RequestClient, "request">,
  objectId: string,
  signal?: AbortSignal
): Promise<readonly ControlCurvePoint[] | null> {
  return requestClient.request({
    key: `control.curve.${objectId}`,
    timeoutMs: controlHardwareRequestTimeoutMs,
    url: `/api/control/objects/${encodeURIComponent(objectId)}/curve`,
    fallbackError: uiText.control.loadFailed,
    decoder: controlCurveDecoder,
    signal,
    request: { method: "GET" }
  });
}

const controlCurveDecoder = defineResponseDecoder<readonly ControlCurvePoint[] | null>(
  "control.curve",
  (value) => {
    // A 204 response preserves the unavailable state.
    if (value === null || value === undefined || value === "") {
      return null;
    }
    return requireArray(value, "curve").map((point, index) => {
      const record = requireRecord(point, `curve[${index}]`);
      return {
        temperatureCelsius: requireFiniteNumber(
          record.temperatureCelsius,
          `curve[${index}].temperatureCelsius`),
        percent: requireFiniteNumber(record.percent, `curve[${index}].percent`)
      };
    });
  });

/** Refresh hardware discovery and return the refreshed catalog without an intermediate stale fetch. */
export function redetectControlPlatform(
  requestClient: Pick<RequestClient, "request">,
  signal?: AbortSignal
): Promise<ControlObjectCatalog> {
  return requestClient.request({
    key: "control.redetect",
    timeoutMs: controlHardwareRequestTimeoutMs,
    url: "/api/control/redetect",
    fallbackError: uiText.control.loadFailed,
    decoder: controlObjectsDecoder,
    signal,
    request: { method: "POST" }
  });
}

const controlNoticeDecoder = defineResponseDecoder<boolean>(
  "control.notice.v1",
  (value) => requireBoolean(requireRecord(value, "$").acknowledged, "$.acknowledged"));

export function getControlNoticeAcknowledged(
  requestClient: Pick<RequestClient, "request">,
  signal?: AbortSignal
): Promise<boolean> {
  return requestClient.request({
    key: "control.notice",
    url: "/api/control/notice",
    fallbackError: uiText.control.loadFailed,
    decoder: controlNoticeDecoder,
    signal,
    request: { method: "GET" }
  });
}

export function acknowledgeControlNotice(
  requestClient: Pick<RequestClient, "request">,
  signal?: AbortSignal
): Promise<boolean> {
  return requestClient.request({
    key: "control.notice.acknowledge",
    url: "/api/control/notice",
    fallbackError: uiText.control.saveFailed,
    decoder: controlNoticeDecoder,
    signal,
    request: { method: "PUT" }
  });
}

export const controlAccessLevelDecoder = defineResponseDecoder<ControlAccessLevel>(
  "control.accessLevel.v1",
  (value) => requireOneOf(
    requireRecord(value, "$").level,
    "$.level",
    controlAccessLevels));

export function getControlAccessLevel(
  requestClient: Pick<RequestClient, "request">,
  signal?: AbortSignal
): Promise<ControlAccessLevel> {
  return requestClient.request({
    key: "control.accessLevel",
    url: "/api/control/access-level",
    fallbackError: uiText.control.loadFailed,
    decoder: controlAccessLevelDecoder,
    signal,
    request: { method: "GET" }
  });
}

export function setControlAccessLevel(
  requestClient: Pick<RequestClient, "request">,
  level: ControlAccessLevel,
  signal?: AbortSignal
): Promise<ControlAccessLevel> {
  return requestClient.request({
    key: "control.accessLevel.set",
    url: "/api/control/access-level",
    fallbackError: uiText.control.loadFailed,
    decoder: controlAccessLevelDecoder,
    signal,
    request: {
      method: "PUT",
      headers: { "content-type": "application/json" },
      body: JSON.stringify({ level })
    }
  });
}

const applyStatuses = ["applied", "unsupported", "failed"] as const;

function readSetting(value: unknown, path: string): ControlSetting {
  const record = requireRecord(value, path);
  return {
    capabilityId: requireNonEmptyString(record.capabilityId, `${path}.capabilityId`),
    gpuVfCurvePresetId: record.gpuVfCurvePresetId == null ? null : requireNonEmptyString(record.gpuVfCurvePresetId, `${path}.gpuVfCurvePresetId`),
    gpuVfCurveExecution: record.gpuVfCurveExecution == null ? null : (() => {
      const execution = requireRecord(record.gpuVfCurveExecution, `${path}.gpuVfCurveExecution`);
      return { binding: readGpuVfBinding(execution.binding, `${path}.gpuVfCurveExecution.binding`),
        points: readGpuVfPoints(execution.points, `${path}.gpuVfCurveExecution.points`) };
    })(),
    number: record.number === null || record.number === undefined
      ? null
      : requireFiniteNumber(record.number, `${path}.number`),
    toggle: record.toggle === null || record.toggle === undefined
      ? null
      : requireBoolean(record.toggle, `${path}.toggle`),
    curve: record.curve === null || record.curve === undefined
      ? null
      : requireArray(record.curve, `${path}.curve`).map((point, index) => {
        const entry = requireRecord(point, `${path}.curve[${index}]`);
        return {
          temperatureCelsius: requireFiniteNumber(
            entry.temperatureCelsius,
            `${path}.curve[${index}].temperatureCelsius`),
          percent: requireFiniteNumber(entry.percent, `${path}.curve[${index}].percent`)
        };
      }),
    curveExecution: optionalString(record.curveExecution, `${path}.curveExecution`)
  };
}

export function readControlReason(value: unknown, path: string): import("./controlTypes.ts").ControlApplyReason | null {
  if (value == null) return null;
  const record = requireRecord(value, path);
  const args = record.arguments == null ? null : requireRecord(record.arguments, `${path}.arguments`);
  const number = (key: string) => args?.[key] == null ? null : requireFiniteNumber(args[key], `${path}.arguments.${key}`);
  return { code: requireNonEmptyString(record.code, `${path}.code`), arguments: args ? {
    requiredAccessLevel: args.requiredAccessLevel == null ? null
      : requireOneOf(args.requiredAccessLevel, `${path}.arguments.requiredAccessLevel`, controlAccessLevels),
    minimum: number("minimum"), maximum: number("maximum"), requested: number("requested"), observed: number("observed"), unit: optionalString(args.unit, `${path}.arguments.unit`)
  } : null };
}

function readOutcome(value: unknown, path: string): ControlApplyOutcome {
  const record = requireRecord(value, path);
  return {
    objectId: requireNonEmptyString(record.objectId, `${path}.objectId`),
    capabilityId: requireNonEmptyString(record.capabilityId, `${path}.capabilityId`),
    status: requireOneOf(record.status, `${path}.status`, applyStatuses),
    nativeStatus: record.nativeStatus == null ? null : requireFiniteNumber(record.nativeStatus, `${path}.nativeStatus`),
    reason: readControlReason(record.reason, `${path}.reason`)
  };
}

/** Desired state uses the same shape in saved presets. */
function readDesiredObjects(
  value: unknown,
  path: string
): ControlObjectDesiredState[] {
  return requireArray(value, path).map((row, index) => {
    const entry = requireRecord(row, `${path}[${index}]`);
    return {
      objectId: requireNonEmptyString(entry.objectId, `${path}[${index}].objectId`),
      settings: requireArray(entry.settings, `${path}[${index}].settings`)
        .map((setting, at) => readSetting(setting, `${path}[${index}].settings[${at}]`))
    };
  });
}

export const presetCatalogDecoder = defineResponseDecoder<ControlPresetCatalog>(
  "control.presets.v1",
  (value) => {
    const record = requireRecord(value, "$");
    return {
      presets: requireArray(record.presets, "$.presets").map((row, index): ControlPreset => {
        const entry = requireRecord(row, `$.presets[${index}]`);
        return {
          id: requireNonEmptyString(entry.id, `$.presets[${index}].id`),
          objectId: requireNonEmptyString(entry.objectId, `$.presets[${index}].objectId`),
          name: requireNonEmptyString(entry.name, `$.presets[${index}].name`),
          settings: requireArray(entry.settings, `$.presets[${index}].settings`)
            .map((setting, at) =>
              readSetting(setting, `$.presets[${index}].settings[${at}]`)),
          updatedAt: requireString(entry.updatedAt, `$.presets[${index}].updatedAt`)
        };
      })
    };
  });

export const controlStateDecoder = defineResponseDecoder<ControlStateView>(
  "control.state.v1",
  (value) => {
    const record = requireRecord(value, "$");
    const desired = requireRecord(record.desired, "$.desired");
    const lastApply = requireRecord(record.lastApply, "$.lastApply");
    return {
      desired: {
        objects: readDesiredObjects(desired.objects, "$.desired.objects"),
        pendingReleases: readDesiredObjects(desired.pendingReleases ?? [], "$.desired.pendingReleases")
      },
      lastApply: {
        outcomes: requireArray(lastApply.outcomes, "$.lastApply.outcomes")
          .map((row, index) => readOutcome(row, `$.lastApply.outcomes[${index}]`)),
        appliedAt: requireString(lastApply.appliedAt, "$.lastApply.appliedAt")
      }
    };
  });

/** Desired state and its latest execution receipt. */
export function getControlState(
  requestClient: Pick<RequestClient, "request">,
  signal?: AbortSignal
): Promise<ControlStateView> {
  return requestClient.request({
    key: "control.state",
    url: "/api/control/state",
    fallbackError: uiText.control.loadFailed,
    decoder: controlStateDecoder,
    signal,
    request: { method: "GET" }
  });
}

function readInstanceCatalog(value: unknown): ControlInstanceCatalog {
  const record = requireRecord(value, "$");
  return {
    instances: requireArray(record.instances, "$.instances").map((row, index) => {
      const view = requireRecord(row, `$.instances[${index}]`);
      const instance = requireRecord(view.instance, `$.instances[${index}].instance`);
      const platform = requireRecord(
        instance.platform,
        `$.instances[${index}].instance.platform`);
      const at = `$.instances[${index}].instance`;
      return {
        isPresent: requireBoolean(view.isPresent, `$.instances[${index}].isPresent`),
        instance: {
          id: requireNonEmptyString(instance.id, `${at}.id`),
          kind: requireNonEmptyString(instance.kind, `${at}.kind`),
          reportedName: optionalString(instance.reportedName, `${at}.reportedName`),
          ordinal: optionalOrdinal(instance.ordinal, `${at}.ordinal`),
          adapterIndex: optionalOrdinal(instance.adapterIndex, `${at}.adapterIndex`),
          platform: {
            operatingSystem: requireNonEmptyString(
              platform.operatingSystem,
              `${at}.platform.operatingSystem`),
            vendor: requireNonEmptyString(platform.vendor, `${at}.platform.vendor`)
          },
          gpuAttachment: optionalString(instance.gpuAttachment, `${at}.gpuAttachment`),
          identityIsUnique: requireBoolean(
            instance.identityIsUnique,
            `${at}.identityIsUnique`),
          firstSeenAt: requireNonEmptyString(instance.firstSeenAt, `${at}.firstSeenAt`),
          lastSeenAt: requireNonEmptyString(instance.lastSeenAt, `${at}.lastSeenAt`),
          defaultSettings: requireArray(instance.defaultSettings, `${at}.defaultSettings`)
            .map((setting, index2) => readSetting(setting, `${at}.defaultSettings[${index2}]`))
        }
      };
    }),
    readAt: requireNonEmptyString(record.readAt, "$.readAt")
  };
}

export const controlInstancesDecoder = defineResponseDecoder<ControlInstanceCatalog>(
  "control.instances.v1",
  readInstanceCatalog);

export function getControlInstances(
  requestClient: Pick<RequestClient, "request">,
  signal?: AbortSignal
): Promise<ControlInstanceCatalog> {
  return requestClient.request({
    key: "control.instances",
    url: "/api/control/instances",
    fallbackError: uiText.control.loadFailed,
    decoder: controlInstancesDecoder,
    signal,
    request: { method: "GET" }
  });
}

/** Rediscover devices and register new instances with default presets. */
export function refreshControlInstances(
  requestClient: Pick<RequestClient, "request">
): Promise<ControlInstanceCatalog> {
  return requestClient.request({
    key: "control.instances.refresh",
    url: "/api/control/instances/refresh",
    fallbackError: uiText.control.loadFailed,
    decoder: controlInstancesDecoder,
    request: { method: "POST" }
  });
}

/** Delete an absent instance and its settings; the backend rejects present devices. */
export function forgetControlInstance(
  requestClient: Pick<RequestClient, "request">,
  instanceId: string
): Promise<ControlInstanceCatalog> {
  return requestClient.request({
    key: "control.instances.forget",
    url: `/api/control/instances/${encodeURIComponent(instanceId)}`,
    fallbackError: uiText.control.forgetFailed,
    decoder: controlInstancesDecoder,
    request: { method: "DELETE" }
  });
}

/** Replace an object's settings; persistence precedes execution and the response reflects saved state. */
/** Submit the complete draft through the sole apply path. Loading a preset only edits the draft. */
export function applyControlDesiredState(
  requestClient: Pick<RequestClient, "request">,
  objects: readonly ControlObjectDesiredState[]
): Promise<ControlStateView> {
  return requestClient.request({
    key: "control.state.apply",
    timeoutMs: controlHardwareRequestTimeoutMs,
    url: "/api/control/state",
    fallbackError: uiText.control.saveFailed,
    decoder: controlStateDecoder,
    request: {
      method: "PUT",
      headers: { "content-type": "application/json" },
      body: JSON.stringify({ objects: objects.map(object => ({ ...object, settings: controlSettingsInput(object.settings) })) })
    }
  });
}

/** Record consent before Intel IGCL overclocking calls, as required by the vendor. */
const overclockConsentDecoder = defineResponseDecoder<boolean>(
  "control.overclockConsent.v1",
  (value: unknown) => requireBoolean(
    requireRecord(value, "overclockConsent").accepted,
    "overclockConsent.accepted"));

export function getControlOverclockConsent(
  requestClient: Pick<RequestClient, "request">,
  signal?: AbortSignal
): Promise<boolean> {
  return requestClient.request({
    key: "control.overclockConsent",
    url: "/api/control/overclock-consent",
    fallbackError: uiText.control.loadFailed,
    decoder: overclockConsentDecoder,
    signal,
    request: { method: "GET" }
  });
}

export function setControlOverclockConsent(
  requestClient: Pick<RequestClient, "request">,
  accepted: boolean
): Promise<boolean> {
  return requestClient.request({
    key: "control.overclockConsent.set",
    url: "/api/control/overclock-consent",
    fallbackError: uiText.control.saveFailed,
    decoder: overclockConsentDecoder,
    request: {
      method: "PUT",
      headers: { "content-type": "application/json" },
      body: JSON.stringify({ accepted })
    }
  });
}

/** Saved presets can be read or written without changing hardware. */
export function getControlPresets(
  requestClient: Pick<RequestClient, "request">,
  signal?: AbortSignal
): Promise<ControlPresetCatalog> {
  return requestClient.request({
    key: "control.presets",
    url: "/api/control/presets",
    fallbackError: uiText.control.loadFailed,
    decoder: presetCatalogDecoder,
    signal,
    request: { method: "GET" }
  });
}

/** Save a preset for an instance, replacing the same name without changing hardware. */
export function saveControlPreset(
  requestClient: Pick<RequestClient, "request">,
  objectId: string,
  name: string,
  settings: readonly ControlSetting[]
): Promise<ControlPresetCatalog> {
  return requestClient.request({
    key: "control.presets.save",
    url: `/api/control/presets/${encodeURIComponent(objectId)}/${encodeURIComponent(name)}`,
    fallbackError: uiText.control.saveFailed,
    decoder: presetCatalogDecoder,
    request: {
      method: "PUT",
      headers: { "content-type": "application/json" },
      body: JSON.stringify(controlSettingsInput(settings))
    }
  });
}

export function deleteControlPreset(
  requestClient: Pick<RequestClient, "request">,
  presetId: string
): Promise<ControlPresetCatalog> {
  return requestClient.request({
    key: "control.presets.delete",
    url: `/api/control/presets/${encodeURIComponent(presetId)}`,
    fallbackError: uiText.control.saveFailed,
    decoder: presetCatalogDecoder,
    request: { method: "DELETE" }
  });
}

export function setControlObjectSettings(
  requestClient: Pick<RequestClient, "request">,
  objectId: string,
  settings: readonly ControlSetting[]
): Promise<ControlStateView> {
  return requestClient.request({
    key: "control.state.set",
    timeoutMs: controlHardwareRequestTimeoutMs,
    url: `/api/control/state/${encodeURIComponent(objectId)}`,
    fallbackError: uiText.control.saveFailed,
    decoder: controlStateDecoder,
    request: {
      method: "PUT",
      headers: { "content-type": "application/json" },
      body: JSON.stringify(controlSettingsInput(settings))
    }
  });
}

/** Explicit retry never submits the local draft. */
export function retryControlObject(requestClient: Pick<RequestClient, "request">, objectId: string): Promise<ControlStateView> {
  return requestClient.request({ key: `control.retry.${objectId}`,
    url: `/api/control/state/${encodeURIComponent(objectId)}/retry`,
    timeoutMs: controlHardwareRequestTimeoutMs, fallbackError: uiText.control.saveFailed,
    decoder: controlStateDecoder, request: { method: "POST" } });
}
