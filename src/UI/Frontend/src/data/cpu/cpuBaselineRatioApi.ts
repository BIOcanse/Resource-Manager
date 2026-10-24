import type { RequestClient } from "../../frontendRuntime/request/RequestClient.ts";
import { RequestProblem } from "../../frontendRuntime/request/RequestProblem.ts";
import {
  defineResponseDecoder,
  requireFiniteNumber,
  requireNonEmptyString,
  requireNullable,
  requireOneOf,
  requireRecord,
  ResponseDecodeError
} from "../../frontendRuntime/request/ResponseDecoder.ts";
import { uiText } from "../../text.ts";

// CPU 原始分除以这个比例。默认值来自内置 CPU 数据集，用户值存在时优先。
export const cpuBaselineRatioSources = [
  "uncompensated",
  "intel:homogeneous_physical_reference",
  "intel:measured_throughput_composition_estimate",
  "intel:specification_p_share_estimate",
  "amd:single-ccd",
  "amd:symmetric-dual-ccd"
] as const;

export type CpuBaselineRatioSource = typeof cpuBaselineRatioSources[number];

export interface CpuBaselineRatioSettings {
  cpuName: string;
  defaultRatio: number;
  overrideRatio: number | null;
  defaultSource: CpuBaselineRatioSource;
  matchedModel: string | null;
  datasetVersion: string;
  ratio: number;
  multiplier: number;
}

export type CpuBaselineRatioDisposition = "applied" | "appliedWithDeliveryFailures" | "savedNotApplied";

/** 保存或重置的结果：savedNotApplied 时用户值已经写入，但运行计划没有发布，settings 为 null。 */
export interface CpuBaselineRatioUpdateResult {
  settings: CpuBaselineRatioSettings | null;
  savedOverrideRatio: number | null;
  runtimeApplicationDisposition: CpuBaselineRatioDisposition;
}

type CpuBaselineRequestClient = Pick<RequestClient, "request">;

/** 与后端 CpuBaselineRatio.IsValid 相同：大于 0、不大于 1，且倒数有限。 */
export function isValidCpuBaselineRatio(value: number) {
  return Number.isFinite(value) && value > 0 && value <= 1 && Number.isFinite(1 / value);
}

function requireRatio(value: unknown, path: string) {
  const ratio = requireFiniteNumber(value, path);
  if (!isValidCpuBaselineRatio(ratio)) throw new ResponseDecodeError(path, "ratio in (0, 1]");
  return ratio;
}

function decodeSettings(value: unknown, path: string): CpuBaselineRatioSettings {
  const record = requireRecord(value, path);
  const defaultRatio = requireRatio(record.defaultRatio, `${path}.defaultRatio`);
  const overrideRatio = requireNullable(record.overrideRatio, `${path}.overrideRatio`, requireRatio);
  const ratio = requireRatio(record.ratio, `${path}.ratio`);
  if (ratio !== (overrideRatio ?? defaultRatio)) {
    throw new ResponseDecodeError(`${path}.ratio`, "override ratio or default ratio");
  }
  return {
    cpuName: requireNonEmptyString(record.cpuName, `${path}.cpuName`),
    defaultRatio,
    overrideRatio,
    defaultSource: requireOneOf(record.defaultSource, `${path}.defaultSource`, cpuBaselineRatioSources),
    matchedModel: requireNullable(record.matchedModel, `${path}.matchedModel`, requireNonEmptyString),
    datasetVersion: requireNonEmptyString(record.datasetVersion, `${path}.datasetVersion`),
    ratio,
    multiplier: requireFiniteNumber(record.multiplier, `${path}.multiplier`)
  };
}

export const cpuBaselineRatioSettingsDecoder = defineResponseDecoder<CpuBaselineRatioSettings | null>(
  "cpu.baseline-ratio.v1",
  (value) => value === null ? null : decodeSettings(value, "$"));

export const cpuBaselineRatioUpdateResultDecoder = defineResponseDecoder<CpuBaselineRatioUpdateResult>(
  "cpu.baseline-ratio.update.v1",
  (value) => {
    const record = requireRecord(value);
    const runtimeApplicationDisposition = requireOneOf(
      record.runtimeApplicationDisposition,
      "$.runtimeApplicationDisposition",
      ["applied", "appliedWithDeliveryFailures", "savedNotApplied"]);
    const settings = requireNullable(record.settings, "$.settings", decodeSettings);
    if ((settings === null) !== (runtimeApplicationDisposition === "savedNotApplied")) {
      throw new ResponseDecodeError("$.settings", "settings exactly when the runtime plan was published");
    }
    return {
      settings,
      savedOverrideRatio: requireNullable(record.savedOverrideRatio, "$.savedOverrideRatio", requireRatio),
      runtimeApplicationDisposition
    };
  });

/** 当前 CPU 的基准比例；CPU 拓扑尚未读取时为 null。 */
export function getCpuBaselineRatio(requestClient: CpuBaselineRequestClient, signal?: AbortSignal) {
  return requestClient.request({
    key: "cpu.baseline-ratio",
    url: "/api/cpu/baseline-ratio",
    fallbackError: uiText.cpuBaseline.readFailed,
    decoder: cpuBaselineRatioSettingsDecoder,
    signal,
    request: { method: "GET" }
  });
}

export function saveCpuBaselineRatio(requestClient: CpuBaselineRequestClient, ratio: number) {
  return updateCpuBaselineRatio(requestClient, {
    key: "cpu.baseline-ratio.save",
    url: "/api/cpu/baseline-ratio",
    fallbackError: uiText.cpuBaseline.saveFailed,
    decoder: cpuBaselineRatioUpdateResultDecoder,
    request: {
      method: "PUT",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify({ ratio })
    }
  });
}

export function resetCpuBaselineRatio(requestClient: CpuBaselineRequestClient) {
  return updateCpuBaselineRatio(requestClient, {
    key: "cpu.baseline-ratio.reset",
    url: "/api/cpu/baseline-ratio",
    fallbackError: uiText.cpuBaseline.resetFailed,
    decoder: cpuBaselineRatioUpdateResultDecoder,
    request: { method: "DELETE" }
  });
}

// 后端在运行计划没有完整生效时回 409 并带同一结构的结果；这是已知结果，不是请求失败。
async function updateCpuBaselineRatio(
  requestClient: CpuBaselineRequestClient,
  descriptor: Parameters<RequestClient["request"]>[0] & { decoder: typeof cpuBaselineRatioUpdateResultDecoder }
): Promise<CpuBaselineRatioUpdateResult> {
  try {
    return await requestClient.request(descriptor);
  } catch (error) {
    if (error instanceof RequestProblem && error.status === 409) {
      try {
        return cpuBaselineRatioUpdateResultDecoder.decode(error.payload);
      } catch {
        // 409 体不是结果结构时按原请求失败处理。
      }
    }
    throw error;
  }
}
