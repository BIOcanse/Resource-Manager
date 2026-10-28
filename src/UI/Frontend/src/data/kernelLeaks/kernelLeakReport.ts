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
 * 内核泄漏报告，合同 v3（后端 KernelLeakReport，订阅 /api/kernel-leaks/subscribe，设计见 docs/kernel_leak_report_feature_2026-10-27.md，
 * 条目生命周期见 docs/report_lifecycle_2026-10-28.md）。
 * 观察是后端的一个业务块；订阅只决定界面收不收报告。status 只描述本轮观察，边界上一次解码成按状态区分的类型：
 * - NotObserved：观察块没有运行（自由点 enabled 关闭、观察计划未发布或已停止；服务启动后块运行之前也是它）；
 * - Observing：本轮观察时长或样本数还不够判定，进度在 observation 里；
 * - Empty：本轮观察已足够，没有活动条目（可以有历史条目）；
 * - Suspected：本轮判定至少一条活动条目；
 * - Unavailable：本轮观察失败，带原因；下一个采样间隔自动开始新一轮（restartPolicy）。
 * 每份报告都带同一形状的观察进度；上一轮失败（只保留一条）在新一轮里继续带着。
 * items 是后端保存的全部条目，任何 status 下都可以有，按首次判出时间升序：判出疑似时是 active，之后判定不再成立或进程已退出时
 * 留下来改为 history，不会被删除；同一 (PID, 启动键, 资源种类) 只有一条。容量满时新出现的不记录，只给出 unrecordedItemCount。
 * 持有者的映像信息和服务信息各有状态，只有 Current 时带值。每项带单位和两个保留的处理操作（是否可用、原因和风险都是后端消息码）。
 * 未知枚举、缺字段、状态与内容不一致都是解码错误，按订阅通道的现有方式进入错误状态。
 */

/** 统一订阅通道上的逻辑选择器（需要 processes.read）。后端在报告变化时推送，没有采样间隔参数。 */
export function buildKernelLeakReportSubscriptionUrl(): string {
  return "/api/kernel-leaks/subscribe";
}

// 与后端 KernelLeakReportStatus 逐个相等（测试读后端枚举）：多出一个值整份报告解码失败。
export const kernelLeakReportStatuses = ["NotObserved", "Observing", "Empty", "Suspected", "Unavailable"] as const;
export const kernelLeakFailures = ["SourceUnavailable", "CapacityExceeded", "InvalidObservation"] as const;
/** 后端对外发布的资源种类。内部样本种类 FileCachePoolBytes 只用于判据排除，出现在报告里就是解码错误。 */
export const kernelLeakResourceKinds = ["ProcessHandles"] as const;
export const kernelLeakUnits = ["count"] as const;
export const kernelLeakMetadataStatuses = ["Current", "Unavailable", "ProcessEnded"] as const;
export const kernelLeakRestartPolicies = ["NextInterval"] as const;
export const kernelLeakActionKinds = ["RestartHolder", "ForceReleaseHandles"] as const;
export const kernelLeakRiskLevels = ["Warning", "High"] as const;
export const kernelLeakItemStates = ["active", "history"] as const;

export type KernelLeakFailure = typeof kernelLeakFailures[number];
export type KernelLeakResourceKind = typeof kernelLeakResourceKinds[number];
export type KernelLeakUnit = typeof kernelLeakUnits[number];
export type KernelLeakRestartPolicy = typeof kernelLeakRestartPolicies[number];
export type KernelLeakActionKind = typeof kernelLeakActionKinds[number];
export type KernelLeakRiskLevel = typeof kernelLeakRiskLevels[number];
export type KernelLeakItemState = typeof kernelLeakItemStates[number];

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
  readonly state: KernelLeakItemState;
  /** 第一次和最近一次判出疑似的时间；条目转为 history 后不再更新。 */
  readonly firstObservedAt: string;
  readonly lastObservedAt: string;
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
  readonly items: readonly KernelLeakItem[];
  /** 条目容量已满时，判出疑似但没有记录的条目数。 */
  readonly unrecordedItemCount: number;
}

export type KernelLeakReport =
  | KernelLeakReportCommon & { readonly status: "NotObserved" | "Observing"; readonly capturedAt: string | null }
  | KernelLeakReportCommon & { readonly status: "Empty"; readonly capturedAt: string }
  | KernelLeakReportCommon & { readonly status: "Suspected"; readonly capturedAt: string }
  | KernelLeakReportCommon & { readonly status: "Unavailable"; readonly capturedAt: string | null; readonly failure: KernelLeakFailure };

export const kernelLeakReportDecoder = defineResponseDecoder<KernelLeakReport>(
  "kernel-leak-report.v3",
  (value) => {
    const record = requireRecord(value);
    const status = requireOneOf(record.status, "$.status", kernelLeakReportStatuses);
    const failure = requireOneOf(record.failure, "$.failure", ["None", ...kernelLeakFailures] as const);
    const capturedAt = requireNullable(record.capturedAt, "$.capturedAt", requireTimestamp);
    const observation = decodeObservation(record.observation, "$.observation");
    const unavailableHolderCount = requireNonNegativeSafeInteger(record.unavailableHolderCount, "$.unavailableHolderCount");
    const items = decodeItems(record.items, "$.items");
    const unrecordedItemCount = requireNonNegativeSafeInteger(record.unrecordedItemCount, "$.unrecordedItemCount");
    const common = { observation, unavailableHolderCount, items, unrecordedItemCount };

    if (status === "Unavailable") {
      if (failure === "None") throw new ResponseDecodeError("$.failure", "a failure reason when status is 'Unavailable'");
      return { ...common, status, capturedAt, failure };
    }
    if (failure !== "None") throw new ResponseDecodeError("$.failure", `'None' when status is '${status}'`);
    const hasActive = items.some((item) => item.state === "active");
    if (status === "Suspected" && !hasActive) throw new ResponseDecodeError("$.items", "at least one active item when status is 'Suspected'");
    if (status === "Empty" && hasActive) throw new ResponseDecodeError("$.items", "no active item when status is 'Empty'");
    if (status === "NotObserved" || status === "Observing") return { ...common, status, capturedAt };
    if (capturedAt === null) throw new ResponseDecodeError("$.capturedAt", `a capture time when status is '${status}'`);
    return { ...common, status, capturedAt };
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

/** 条目身份：一个持有者进程（PID 加启动键区分 PID 复用）的一种资源。 */
export function kernelLeakItemKey(item: Pick<KernelLeakItem, "resourceKind" | "holder">): string {
  return `${item.resourceKind}:${item.holder.processId}:${item.holder.startKey}`;
}

/** 全部条目：每个 (资源种类, PID, 启动键) 只有一条，按首次判出时间升序。 */
function decodeItems(value: unknown, path: string): readonly KernelLeakItem[] {
  const items = requireArray(value, path).map((item, index) => decodeItem(item, `${path}[${index}]`));
  const keys = new Set<string>();
  for (const [index, item] of items.entries()) {
    const key = kernelLeakItemKey(item);
    if (keys.has(key)) throw new ResponseDecodeError(`${path}[${index}]`, "one item per holder process and resource kind");
    keys.add(key);
    if (index > 0 && Date.parse(item.firstObservedAt) < Date.parse(items[index - 1]!.firstObservedAt)) {
      throw new ResponseDecodeError(`${path}[${index}].firstObservedAt`, "items in ascending firstObservedAt order");
    }
  }
  return items;
}

function decodeItem(value: unknown, path: string): KernelLeakItem {
  const record = requireRecord(value, path);
  const actions = requireArray(record.actions, `${path}.actions`).map((item, index) => decodeAction(item, `${path}.actions[${index}]`));
  const kinds = new Set<KernelLeakActionKind>();
  for (const [index, action] of actions.entries()) {
    if (kinds.has(action.action)) throw new ResponseDecodeError(`${path}.actions[${index}].action`, "each action at most once per item");
    kinds.add(action.action);
  }
  const firstObservedAt = requireTimestamp(record.firstObservedAt, `${path}.firstObservedAt`);
  const lastObservedAt = requireTimestamp(record.lastObservedAt, `${path}.lastObservedAt`);
  if (Date.parse(lastObservedAt) < Date.parse(firstObservedAt)) {
    throw new ResponseDecodeError(`${path}.lastObservedAt`, "a time not earlier than firstObservedAt");
  }
  return {
    state: requireOneOf(record.state, `${path}.state`, kernelLeakItemStates),
    firstObservedAt,
    lastObservedAt,
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
