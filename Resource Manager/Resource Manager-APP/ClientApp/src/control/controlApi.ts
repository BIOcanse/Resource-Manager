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
} from "../frontendRuntime/request/ResponseDecoder.ts";
import type { RequestClient } from "../frontendRuntime/request/RequestClient.ts";
import { uiText } from "../text.ts";
import type {
  ControlApplyOutcome,
  ControlCapability,
  ControlNumberRange,
  ControlObject,
  ControlInstanceCatalog,
  ControlObjectCatalog,
  ControlSetting,
  ControlStateView
} from "./controlTypes.ts";

const valueKinds = ["toggle", "number", "curve"] as const;

function optionalString(value: unknown, path: string): string | null {
  return value === null || value === undefined ? null : requireString(value, path);
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
  if (range.maximum <= range.minimum) {
    throw new ResponseDecodeError(path, "a range whose maximum exceeds its minimum");
  }
  return range;
}

function readCapability(value: unknown, path: string): ControlCapability {
  const record = requireRecord(value, path);
  const supported = requireBoolean(record.supported, `${path}.supported`);
  const unavailableReason = optionalString(record.unavailableReason, `${path}.unavailableReason`);
  // 不可用就必须说明原因 —— 生产端保证，消费端不去猜。
  if (!supported && !unavailableReason) {
    throw new ResponseDecodeError(path, "an unavailable capability to carry its reason");
  }
  return {
    id: requireNonEmptyString(record.id, `${path}.id`),
    label: requireNonEmptyString(record.label, `${path}.label`),
    valueKind: requireOneOf(record.valueKind, `${path}.valueKind`, valueKinds),
    supported,
    unavailableReason,
    requiredComponentId: optionalString(record.requiredComponentId, `${path}.requiredComponentId`),
    requiredComponentName: optionalString(
      record.requiredComponentName,
      `${path}.requiredComponentName`),
    range: readRange(record.range, `${path}.range`)
  };
}

function readObject(value: unknown, path: string): ControlObject {
  const record = requireRecord(value, path);
  const platform = requireRecord(record.platform, `${path}.platform`);
  return {
    id: requireNonEmptyString(record.id, `${path}.id`),
    kind: requireNonEmptyString(record.kind, `${path}.kind`),
    displayName: requireString(record.displayName, `${path}.displayName`),
    platform: {
      operatingSystem: requireNonEmptyString(
        platform.operatingSystem,
        `${path}.platform.operatingSystem`),
      vendor: requireNonEmptyString(platform.vendor, `${path}.platform.vendor`)
    },
    capabilities: requireArray(record.capabilities, `${path}.capabilities`)
      .map((row, index) => readCapability(row, `${path}.capabilities[${index}]`)),
    detail: optionalString(record.detail, `${path}.detail`),
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

const applyStatuses = ["applied", "unsupported", "failed"] as const;

function readSetting(value: unknown, path: string): ControlSetting {
  const record = requireRecord(value, path);
  return {
    capabilityId: requireNonEmptyString(record.capabilityId, `${path}.capabilityId`),
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
      })
  };
}

function readOutcome(value: unknown, path: string): ControlApplyOutcome {
  const record = requireRecord(value, path);
  return {
    objectId: requireNonEmptyString(record.objectId, `${path}.objectId`),
    capabilityId: requireNonEmptyString(record.capabilityId, `${path}.capabilityId`),
    status: requireOneOf(record.status, `${path}.status`, applyStatuses),
    message: optionalString(record.message, `${path}.message`)
  };
}

export const controlStateDecoder = defineResponseDecoder<ControlStateView>(
  "control.state.v1",
  (value) => {
    const record = requireRecord(value, "$");
    const desired = requireRecord(record.desired, "$.desired");
    const lastApply = requireRecord(record.lastApply, "$.lastApply");
    return {
      desired: {
        objects: requireArray(desired.objects, "$.desired.objects").map((row, index) => {
          const entry = requireRecord(row, `$.desired.objects[${index}]`);
          return {
            objectId: requireNonEmptyString(
              entry.objectId,
              `$.desired.objects[${index}].objectId`),
            settings: requireArray(entry.settings, `$.desired.objects[${index}].settings`)
              .map((setting, at) =>
                readSetting(setting, `$.desired.objects[${index}].settings[${at}]`))
          };
        })
      },
      lastApply: {
        outcomes: requireArray(lastApply.outcomes, "$.lastApply.outcomes")
          .map((row, index) => readOutcome(row, `$.lastApply.outcomes[${index}]`)),
        appliedAt: requireString(lastApply.appliedAt, "$.lastApply.appliedAt")
      }
    };
  });

/** 期望状态 + 最近一次施加的回执。 */
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
          displayName: requireString(instance.displayName, `${at}.displayName`),
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

/** 重新检测。新设备会被登记并自带默认配置。 */
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

/** 删掉一条早就不用的记录，连同它的设定。设备还在场时后端会拒绝。 */
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
