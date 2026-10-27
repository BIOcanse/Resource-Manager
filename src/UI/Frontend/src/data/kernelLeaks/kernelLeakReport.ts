import {
  defineResponseDecoder,
  requireArray,
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

/*
 * 内核泄漏报告（后端 KernelLeakReport，订阅 /api/kernel-leaks/subscribe，设计见 docs/kernel_leak_report_feature_2026-10-27.md）。
 * 边界上一次解码成按状态区分的类型：界面拿到后直接显示，不再猜哪个字段在哪种状态下有值。
 * - Empty：没有疑似项，也包括观察时间还不够判定的情况（后端不区分这两者）；
 * - Suspected：至少一项疑似持续增长的资源；
 * - Unavailable：报告不可用，带后端给的原因。
 * 持有者的映像信息（名称、路径、产品名）和服务信息各有自己的状态；状态不是 Current 时界面只显示该状态，
 * 不使用同一条记录里的其他值。未知的枚举值、缺字段和状态与内容不一致都是解码错误，按订阅通道的现有方式进入错误状态。
 */

/** 统一订阅通道上的逻辑选择器（需要 processes.read）。后端在报告变化时推送，没有采样间隔参数。 */
export function buildKernelLeakReportSubscriptionUrl(): string {
  return "/api/kernel-leaks/subscribe";
}

export const kernelLeakReportStatuses = ["Empty", "Suspected", "Unavailable"] as const;
export const kernelLeakFailures = ["NotObserved", "SourceUnavailable", "CapacityExceeded", "InvalidObservation"] as const;
/** 后端会发布的资源种类。后端枚举里的 FileCachePoolBytes 只用于判据排除，不会出现在报告里。 */
export const kernelLeakResourceKinds = ["ProcessHandles"] as const;
export const kernelLeakMetadataStatuses = ["Current", "Unavailable", "ProcessEnded"] as const;

export type KernelLeakFailure = typeof kernelLeakFailures[number];
export type KernelLeakResourceKind = typeof kernelLeakResourceKinds[number];
export type KernelLeakMetadataStatus = typeof kernelLeakMetadataStatuses[number];

/** 映像信息：Current 时名称和路径必有，产品名可能没有（文件不带产品名）。 */
export type KernelLeakImage =
  | { readonly status: "Current"; readonly name: string; readonly imagePath: string; readonly productName: string | null }
  | { readonly status: "Unavailable" | "ProcessEnded" };

/** 服务信息：Current 且名单为空表示没有对应的服务。 */
export type KernelLeakServices =
  | { readonly status: "Current"; readonly names: readonly string[] }
  | { readonly status: "Unavailable" | "ProcessEnded" };

export interface KernelLeakHolder {
  readonly processId: number;
  /** 进程启动键（十进制 FILETIME 文本），和 PID 一起确定是哪一个进程。 */
  readonly startKey: string;
  readonly image: KernelLeakImage;
  readonly services: KernelLeakServices;
}

export interface KernelLeakItem {
  readonly resourceKind: KernelLeakResourceKind;
  readonly currentValue: number;
  readonly growthPerSecond: number;
  readonly observationSeconds: number;
  readonly sampleCount: number;
  readonly growth: number;
  readonly holder: KernelLeakHolder;
}

interface KernelLeakReportCommon {
  /** 本帧里读不到资源值的持有者数；它们没有参与判定，已观察到的持有者照常判定。 */
  readonly unavailableHolderCount: number;
}

export type KernelLeakReport =
  | KernelLeakReportCommon & { readonly status: "Empty"; readonly capturedAt: string }
  | KernelLeakReportCommon & { readonly status: "Suspected"; readonly capturedAt: string; readonly items: readonly KernelLeakItem[] }
  | KernelLeakReportCommon & { readonly status: "Unavailable"; readonly capturedAt: string | null; readonly failure: KernelLeakFailure };

export const kernelLeakReportDecoder = defineResponseDecoder<KernelLeakReport>(
  "kernel-leak-report.v1",
  (value) => {
    const record = requireRecord(value);
    const status = requireOneOf(record.status, "$.status", kernelLeakReportStatuses);
    const failure = requireOneOf(record.failure, "$.failure", ["None", ...kernelLeakFailures] as const);
    const capturedAt = requireNullable(record.capturedAt, "$.capturedAt", requireTimestamp);
    const unavailableHolderCount = requireNonNegativeSafeInteger(record.unavailableHolderCount, "$.unavailableHolderCount");
    const items = requireArray(record.items, "$.items").map((item, index) => decodeItem(item, `$.items[${index}]`));

    if (status === "Unavailable") {
      if (failure === "None") throw new ResponseDecodeError("$.failure", "a failure reason when status is 'Unavailable'");
      if (items.length > 0) throw new ResponseDecodeError("$.items", "no items when status is 'Unavailable'");
      return { status, capturedAt, failure, unavailableHolderCount };
    }
    if (failure !== "None") throw new ResponseDecodeError("$.failure", `'None' when status is '${status}'`);
    if (capturedAt === null) throw new ResponseDecodeError("$.capturedAt", `a capture time when status is '${status}'`);
    if (status === "Empty") {
      if (items.length > 0) throw new ResponseDecodeError("$.items", "no items when status is 'Empty'");
      return { status, capturedAt, unavailableHolderCount };
    }
    if (items.length === 0) throw new ResponseDecodeError("$.items", "at least one item when status is 'Suspected'");
    return { status, capturedAt, items, unavailableHolderCount };
  });

function decodeItem(value: unknown, path: string): KernelLeakItem {
  const record = requireRecord(value, path);
  return {
    resourceKind: requireOneOf(record.resourceKind, `${path}.resourceKind`, kernelLeakResourceKinds),
    currentValue: requireNonNegativeSafeInteger(record.currentValue, `${path}.currentValue`),
    growthPerSecond: requireFiniteNumber(record.growthPerSecond, `${path}.growthPerSecond`),
    observationSeconds: requireNonNegativeFiniteNumber(record.observationSeconds, `${path}.observationSeconds`),
    sampleCount: requireNonNegativeSafeInteger(record.sampleCount, `${path}.sampleCount`),
    growth: requireNonNegativeSafeInteger(record.growth, `${path}.growth`),
    holder: decodeHolder(record.holder, `${path}.holder`)
  };
}

function decodeHolder(value: unknown, path: string): KernelLeakHolder {
  const record = requireRecord(value, path);
  const processId = requireNonNegativeSafeInteger(record.processId, `${path}.processId`);
  const startKey = requireString(record.startKey, `${path}.startKey`);
  if (!/^\d+$/.test(startKey)) throw new ResponseDecodeError(`${path}.startKey`, "decimal FILETIME text");
  const text = (key: string) => requireNullable(record[key], `${path}.${key}`, requireString);
  const name = text("name");
  const imagePath = text("imagePath");
  const productName = text("productName");
  const serviceNames = requireArray(record.serviceNames, `${path}.serviceNames`)
    .map((item, index) => requireNonEmptyString(item, `${path}.serviceNames[${index}]`));
  const imageStatus = requireOneOf(record.imageStatus, `${path}.imageStatus`, kernelLeakMetadataStatuses);
  const serviceStatus = requireOneOf(record.serviceStatus, `${path}.serviceStatus`, kernelLeakMetadataStatuses);

  let image: KernelLeakImage;
  if (imageStatus === "Current") {
    if (!imagePath) throw new ResponseDecodeError(`${path}.imagePath`, "an image path when imageStatus is 'Current'");
    if (!name) throw new ResponseDecodeError(`${path}.name`, "a name when imageStatus is 'Current'");
    // 文件没有产品名时后端给 null；空字符串同样表示没有，一并收成 null。
    image = { status: imageStatus, name, imagePath, productName: productName || null };
  } else {
    image = { status: imageStatus };
  }
  const services: KernelLeakServices = serviceStatus === "Current"
    ? { status: serviceStatus, names: serviceNames }
    : { status: serviceStatus };
  return { processId, startKey, image, services };
}

function requireTimestamp(value: unknown, path: string): string {
  const timestamp = requireNonEmptyString(value, path);
  if (!/(?:Z|[+-]\d{2}:\d{2})$/.test(timestamp) || !Number.isFinite(Date.parse(timestamp))) {
    throw new ResponseDecodeError(path, "ISO timestamp with an explicit offset");
  }
  return timestamp;
}
