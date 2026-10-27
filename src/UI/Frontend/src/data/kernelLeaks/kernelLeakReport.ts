import {
  defineResponseDecoder,
  requireArray,
  requireBackendMessage,
  requireBoolean,
  requireFiniteNumber,
  requireNonEmptyString,
  requireNonNegativeFiniteNumber,
  requireNonNegativeSafeInteger,
  requireNullable,
  requireOneOf,
  requireRecord,
  requireString,
  ResponseDecodeError
} from "../../frontendRuntime/request/ResponseDecoder.ts";
import type { BackendMessage } from "../../types.ts";

/*
 * 内核泄漏报告，合同 v2（后端 KernelLeakReport，订阅 /api/kernel-leaks/subscribe，设计见 docs/kernel_leak_report_feature_2026-10-27.md）。
 * 观察在后端整个运行期间进行（可关闭）；订阅只决定界面收不收报告。边界上一次解码成按状态区分的类型，界面拿到后直接显示：
 * - Disabled：配置关闭了观察；
 * - Observing：本轮观察时长或样本数还不够判定，进度在 observation 里；
 * - Empty：本轮观察已足够，没有疑似项；
 * - Suspected：至少一项疑似持续增长的资源；
 * - Unavailable：本轮观察失败，带原因；下一个采样间隔自动开始新一轮（restartPolicy）。
 * 每份报告都带同一形状的观察进度；上一轮失败（只保留一条）在新一轮里继续带着。
 * 持有者的映像信息和服务信息各有状态，只有 Current 时带值。每项带单位和两个保留的处理操作（是否可用、原因和风险都是后端消息码）。
 * 未知枚举、缺字段、状态与内容不一致都是解码错误，按订阅通道的现有方式进入错误状态。
 */

/** 统一订阅通道上的逻辑选择器（需要 processes.read）。后端在报告变化时推送，没有采样间隔参数。 */
export function buildKernelLeakReportSubscriptionUrl(): string {
  return "/api/kernel-leaks/subscribe";
}

export const kernelLeakReportStatuses = ["Disabled", "Observing", "Empty", "Suspected", "Unavailable"] as const;
export const kernelLeakFailures = ["SourceUnavailable", "CapacityExceeded", "InvalidObservation"] as const;
/** 后端对外发布的资源种类。内部样本种类 FileCachePoolBytes 只用于判据排除，出现在报告里就是解码错误。 */
export const kernelLeakResourceKinds = ["ProcessHandles"] as const;
export const kernelLeakUnits = ["count"] as const;
export const kernelLeakMetadataStatuses = ["Current", "Unavailable", "ProcessEnded"] as const;
export const kernelLeakRestartPolicies = ["NextInterval"] as const;
export const kernelLeakActionKinds = ["RestartHolder", "ForceReleaseHandles"] as const;
export const kernelLeakRiskLevels = ["Warning", "High"] as const;

export type KernelLeakFailure = typeof kernelLeakFailures[number];
export type KernelLeakResourceKind = typeof kernelLeakResourceKinds[number];
export type KernelLeakUnit = typeof kernelLeakUnits[number];
export type KernelLeakRestartPolicy = typeof kernelLeakRestartPolicies[number];
export type KernelLeakActionKind = typeof kernelLeakActionKinds[number];
export type KernelLeakRiskLevel = typeof kernelLeakRiskLevels[number];

/** 映像信息：Current 时名称和路径必有；文件没有产品名时 productName 为 null（读取失败时整个映像是 Unavailable）。 */
export type KernelLeakImage =
  | { readonly status: "Current"; readonly name: string; readonly path: string; readonly productName: string | null }
  | { readonly status: "Unavailable" | "ProcessEnded" };

/** 服务信息：Current 且名单为空表示没有对应的服务。 */
export type KernelLeakServices =
  | { readonly status: "Current"; readonly names: readonly string[] }
  | { readonly status: "Unavailable" | "ProcessEnded" };

export interface KernelLeakHolder {
  readonly processId: number;
  /** 进程启动键（十进制 FILETIME 文本），和 PID 一起确定是哪一个进程，也是处理操作的目标身份。 */
  readonly startKey: string;
  readonly image: KernelLeakImage;
  readonly services: KernelLeakServices;
}

/**
 * 保留的处理操作（第 3 期才会有执行入口）：合同 v2 里每项都不可用，必带原因；原因和风险都是后端消息，措辞由前端按语言渲染。
 * 前端没有可以接上的命令，收到可用的操作就是合同不一致（解码错误），而不是画一个点了没有反应的按钮。
 */
export interface KernelLeakAction {
  readonly action: KernelLeakActionKind;
  readonly enabled: false;
  readonly disabledReason: BackendMessage;
  readonly risk: BackendMessage;
  readonly riskLevel: KernelLeakRiskLevel;
}

export interface KernelLeakItem {
  readonly resourceKind: KernelLeakResourceKind;
  readonly unit: KernelLeakUnit;
  readonly currentValue: number;
  readonly growthPerSecond: number;
  readonly observationSeconds: number;
  readonly sampleCount: number;
  readonly growth: number;
  readonly holder: KernelLeakHolder;
  readonly actions: readonly KernelLeakAction[];
}

export interface KernelLeakRunFailure {
  /** 失败那一轮的开始时间（该轮第一帧的采集时间）；只有真正开始过的一轮才会记成失败，所以一定有。 */
  readonly startedAt: string;
  readonly failedAt: string;
  readonly failure: KernelLeakFailure;
}

export interface KernelLeakObservation {
  /** 本轮第一帧的时间；关闭、首帧前等待或预热、首帧前就不可用时为 null，此时观察时长和样本数都是 0。 */
  readonly startedAt: string | null;
  readonly elapsedSeconds: number;
  readonly sampleCount: number;
  readonly requiredSeconds: number;
  readonly requiredSampleCount: number;
  readonly previousRunFailure: KernelLeakRunFailure | null;
  readonly restartPolicy: KernelLeakRestartPolicy;
}

interface KernelLeakReportCommon {
  readonly observation: KernelLeakObservation;
  /** 本帧里读不到资源值的持有者数；它们没有参与判定，已观察到的持有者照常判定。 */
  readonly unavailableHolderCount: number;
}

export type KernelLeakReport =
  | KernelLeakReportCommon & { readonly status: "Disabled" | "Observing"; readonly capturedAt: string | null }
  | KernelLeakReportCommon & { readonly status: "Empty"; readonly capturedAt: string }
  | KernelLeakReportCommon & { readonly status: "Suspected"; readonly capturedAt: string; readonly items: readonly KernelLeakItem[] }
  | KernelLeakReportCommon & { readonly status: "Unavailable"; readonly capturedAt: string | null; readonly failure: KernelLeakFailure };

export const kernelLeakReportDecoder = defineResponseDecoder<KernelLeakReport>(
  "kernel-leak-report.v2",
  (value) => {
    const record = requireRecord(value);
    const status = requireOneOf(record.status, "$.status", kernelLeakReportStatuses);
    const failure = requireOneOf(record.failure, "$.failure", ["None", ...kernelLeakFailures] as const);
    const capturedAt = requireNullable(record.capturedAt, "$.capturedAt", requireTimestamp);
    const observation = decodeObservation(record.observation, "$.observation");
    const unavailableHolderCount = requireNonNegativeSafeInteger(record.unavailableHolderCount, "$.unavailableHolderCount");
    const items = requireArray(record.items, "$.items").map((item, index) => decodeItem(item, `$.items[${index}]`));
    const common = { observation, unavailableHolderCount };

    if (status === "Unavailable") {
      if (failure === "None") throw new ResponseDecodeError("$.failure", "a failure reason when status is 'Unavailable'");
      if (items.length > 0) throw new ResponseDecodeError("$.items", "no items when status is 'Unavailable'");
      return { ...common, status, capturedAt, failure };
    }
    if (failure !== "None") throw new ResponseDecodeError("$.failure", `'None' when status is '${status}'`);
    if (status === "Suspected") {
      if (items.length === 0) throw new ResponseDecodeError("$.items", "at least one item when status is 'Suspected'");
    } else if (items.length > 0) {
      throw new ResponseDecodeError("$.items", `no items when status is '${status}'`);
    }
    if (status === "Disabled" || status === "Observing") return { ...common, status, capturedAt };
    if (capturedAt === null) throw new ResponseDecodeError("$.capturedAt", `a capture time when status is '${status}'`);
    return status === "Empty" ? { ...common, status, capturedAt } : { ...common, status, capturedAt, items };
  });

function decodeObservation(value: unknown, path: string): KernelLeakObservation {
  const record = requireRecord(value, path);
  return {
    startedAt: requireNullable(record.startedAt, `${path}.startedAt`, requireTimestamp),
    elapsedSeconds: requireNonNegativeFiniteNumber(record.elapsedSeconds, `${path}.elapsedSeconds`),
    sampleCount: requireNonNegativeSafeInteger(record.sampleCount, `${path}.sampleCount`),
    requiredSeconds: requireNonNegativeSafeInteger(record.requiredSeconds, `${path}.requiredSeconds`),
    requiredSampleCount: requireNonNegativeSafeInteger(record.requiredSampleCount, `${path}.requiredSampleCount`),
    previousRunFailure: requireNullable(record.previousRunFailure, `${path}.previousRunFailure`, (item, itemPath) => {
      const failure = requireRecord(item, itemPath);
      return {
        startedAt: requireTimestamp(failure.startedAt, `${itemPath}.startedAt`),
        failedAt: requireTimestamp(failure.failedAt, `${itemPath}.failedAt`),
        failure: requireOneOf(failure.failure, `${itemPath}.failure`, kernelLeakFailures)
      };
    }),
    restartPolicy: requireOneOf(record.restartPolicy, `${path}.restartPolicy`, kernelLeakRestartPolicies)
  };
}

function decodeItem(value: unknown, path: string): KernelLeakItem {
  const record = requireRecord(value, path);
  const actions = requireArray(record.actions, `${path}.actions`).map((item, index) => decodeAction(item, `${path}.actions[${index}]`));
  const kinds = new Set<KernelLeakActionKind>();
  for (const [index, action] of actions.entries()) {
    if (kinds.has(action.action)) throw new ResponseDecodeError(`${path}.actions[${index}].action`, "each action at most once per item");
    kinds.add(action.action);
  }
  return {
    resourceKind: requireOneOf(record.resourceKind, `${path}.resourceKind`, kernelLeakResourceKinds),
    unit: requireOneOf(record.unit, `${path}.unit`, kernelLeakUnits),
    currentValue: requireNonNegativeSafeInteger(record.currentValue, `${path}.currentValue`),
    growthPerSecond: requireFiniteNumber(record.growthPerSecond, `${path}.growthPerSecond`),
    observationSeconds: requireNonNegativeFiniteNumber(record.observationSeconds, `${path}.observationSeconds`),
    sampleCount: requireNonNegativeSafeInteger(record.sampleCount, `${path}.sampleCount`),
    growth: requireNonNegativeSafeInteger(record.growth, `${path}.growth`),
    holder: decodeHolder(record.holder, `${path}.holder`),
    actions
  };
}

function decodeAction(value: unknown, path: string): KernelLeakAction {
  const record = requireRecord(value, path);
  if (requireBoolean(record.enabled, `${path}.enabled`)) {
    throw new ResponseDecodeError(`${path}.enabled`, "false: contract v2 reserves the actions and the frontend has no command for them");
  }
  return {
    action: requireOneOf(record.action, `${path}.action`, kernelLeakActionKinds),
    enabled: false,
    disabledReason: requireBackendMessage(record.disabledReason, `${path}.disabledReason`),
    risk: requireBackendMessage(record.risk, `${path}.risk`),
    riskLevel: requireOneOf(record.riskLevel, `${path}.riskLevel`, kernelLeakRiskLevels)
  };
}

function decodeHolder(value: unknown, path: string): KernelLeakHolder {
  const record = requireRecord(value, path);
  const processId = requireNonNegativeSafeInteger(record.processId, `${path}.processId`);
  const startKey = requireString(record.startKey, `${path}.startKey`);
  if (!/^\d+$/.test(startKey)) throw new ResponseDecodeError(`${path}.startKey`, "decimal FILETIME text");
  return { processId, startKey, image: decodeImage(record.image, `${path}.image`), services: decodeServices(record.services, `${path}.services`) };
}

function decodeImage(value: unknown, path: string): KernelLeakImage {
  const record = requireRecord(value, path);
  const status = requireOneOf(record.status, `${path}.status`, kernelLeakMetadataStatuses);
  if (status !== "Current") {
    for (const key of ["name", "path", "productName"]) {
      if (key in record) throw new ResponseDecodeError(`${path}.${key}`, `no value when status is '${status}'`);
    }
    return { status };
  }
  return {
    status,
    name: requireNonEmptyString(record.name, `${path}.name`),
    path: requireNonEmptyString(record.path, `${path}.path`),
    // 文件没有产品名时后端省略这个键。
    productName: "productName" in record ? requireNonEmptyString(record.productName, `${path}.productName`) : null
  };
}

function decodeServices(value: unknown, path: string): KernelLeakServices {
  const record = requireRecord(value, path);
  const status = requireOneOf(record.status, `${path}.status`, kernelLeakMetadataStatuses);
  const names = requireArray(record.names, `${path}.names`).map((item, index) => requireNonEmptyString(item, `${path}.names[${index}]`));
  if (status === "Current") return { status, names };
  if (names.length > 0) throw new ResponseDecodeError(`${path}.names`, `an empty list when status is '${status}'`);
  return { status };
}

function requireTimestamp(value: unknown, path: string): string {
  const timestamp = requireNonEmptyString(value, path);
  if (!/(?:Z|[+-]\d{2}:\d{2})$/.test(timestamp) || !Number.isFinite(Date.parse(timestamp))) {
    throw new ResponseDecodeError(path, "ISO timestamp with an explicit offset");
  }
  return timestamp;
}
