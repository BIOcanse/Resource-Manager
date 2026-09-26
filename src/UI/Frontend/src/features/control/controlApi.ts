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
    unavailableKind: record.unavailableKind === null
      || record.unavailableKind === undefined
      ? null
      : requireOneOf(
        record.unavailableKind,
        `${path}.unavailableKind`,
        controlUnavailableKinds),
    requiredComponentId: optionalString(record.requiredComponentId, `${path}.requiredComponentId`),
    requiredComponentName: optionalString(
      record.requiredComponentName,
      `${path}.requiredComponentName`),
    range: readRange(record.range, `${path}.range`),
    channel: optionalString(record.channel, `${path}.channel`),
    requiredAccessLevel: requireOneOf(
      record.requiredAccessLevel,
      `${path}.requiredAccessLevel`,
      controlAccessLevels)
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

/**
 * 这个对象**现在跑的**曲线，从固件里读出来的。
 *
 * 用户要改曲线，起点必须是机器现在真在跑的那条 —— 给他一条我们编的默认曲线，
 * 他调出来的东西和这台机器的实际行为毫无关系，也没法判断自己到底改小了还是改大了。
 *
 * **不跟对象清单一起取。** 读一次要几十次固件往返，混进清单里打开控制页就得等。
 * 所以只在用户真去看曲线的时候才走这一条。
 *
 * 读不到时后端回 204，这里就是 null —— **和"一条全 0 的曲线"必须分开**，
 * 后者会画成一条贴地的线，看着像风扇被我们关掉了。
 */
export function getControlObjectCurve(
  requestClient: Pick<RequestClient, "request">,
  objectId: string,
  signal?: AbortSignal
): Promise<readonly ControlCurvePoint[] | null> {
  return requestClient.request({
    key: `control.curve.${objectId}`,
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
    // 204 没有内容。读不到就是读不到，不编一条。
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

/**
 * 重新检测这台机器。
 *
 * 探测要碰硬件，所以后端都记了缓存；装上组件、插上显卡之后，
 * 不重新问一遍就看不到变化。这条路让用户自己触发那一次重问。
 *
 * 回的就是**重新探过之后的**可控对象清单 —— 不用再单独取一次，
 * 那样中间还会夹一个旧清单闪一下。
 */
export function redetectControlPlatform(
  requestClient: Pick<RequestClient, "request">,
  signal?: AbortSignal
): Promise<ControlObjectCatalog> {
  return requestClient.request({
    key: "control.redetect",
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

function readOutcome(value: unknown, path: string): ControlApplyOutcome {
  const record = requireRecord(value, path);
  return {
    objectId: requireNonEmptyString(record.objectId, `${path}.objectId`),
    capabilityId: requireNonEmptyString(record.capabilityId, `${path}.capabilityId`),
    status: requireOneOf(record.status, `${path}.status`, applyStatuses),
    message: optionalString(record.message, `${path}.message`)
  };
}

/** 一份期望状态。配置里存的也是这个形状，所以两处共用同一个读法。 */
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
        objects: readDesiredObjects(desired.objects, "$.desired.objects")
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

/** 替换某个对象的设定并立刻施加。后端先存后施加，返回的是存下来的那份。 */
/**
 * 整份应用：把草稿里的全部设定一次交上去。
 *
 * 「应用」**只有这一条路**。点一份配置只是把它的内容载入草稿，
 * 落到硬件仍然要用户点应用 —— 多一条捷径就会有两处定义"应用是什么"。
 */
export function applyControlDesiredState(
  requestClient: Pick<RequestClient, "request">,
  objects: readonly ControlObjectDesiredState[]
): Promise<ControlStateView> {
  return requestClient.request({
    key: "control.state.apply",
    url: "/api/control/state",
    fallbackError: uiText.control.saveFailed,
    decoder: controlStateDecoder,
    request: {
      method: "PUT",
      headers: { "content-type": "application/json" },
      body: JSON.stringify({ objects })
    }
  });
}

/**
 * 超频免责声明的答复。
 *
 * **这不是我们发明的流程，是厂商的硬性要求**：Intel 的 IGCL 在用户接受之前
 * 拒绝一切超频接口，原文写着设置它表示用户接受部件寿命缩短，并要求应用
 * 先把这件事告诉用户。所以界面上先把后果说清楚，用户点了同意才轮到后端去调。
 */
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

/** 存下来的几套方案。读写这些都不动硬件。 */
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

/**
 * 给某个实例存一份配置。同一个实例下同名覆盖。**不动硬件。**
 */
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
      body: JSON.stringify(settings)
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
    url: `/api/control/state/${encodeURIComponent(objectId)}`,
    fallbackError: uiText.control.saveFailed,
    decoder: controlStateDecoder,
    request: {
      method: "PUT",
      headers: { "content-type": "application/json" },
      body: JSON.stringify(settings)
    }
  });
}
