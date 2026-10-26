import type { RequestClient } from "../../frontendRuntime/request/RequestClient.ts";
import {
  defineResponseDecoder,
  requireArray,
  requireBoolean,
  requireNonEmptyString,
  requireNullable,
  requireRecord,
  requireSafeInteger,
  requireString,
  ResponseDecodeError
} from "../../frontendRuntime/request/ResponseDecoder.ts";
import type { ResourceGpuEngineScope } from "../../types.ts";
import { uiText } from "../../text.ts";

/** 目录用 System.Text.Json 写全部键（空值写 null）；缺键就是 wire 变了。 */
function written<T>(record: Record<string, unknown>, key: string, path: string, decode: (value: unknown, path: string) => T): T | null {
  if (!Object.hasOwn(record, key)) throw new ResponseDecodeError(`${path}.${key}`, "written key");
  return requireNullable(record[key], `${path}.${key}`, decode);
}

/*
 * 进程字段目录（GET /api/resource-breakdown/fields/catalog，后端 ProcessMetricFieldRegistry）。
 * 字段的 reading 说它属于哪种资源、是哪种口径（docs/metric_reading_source_selection.md「进程内存读取项与资源事实」）：
 * 资源表靠它把同一资源的不同口径挂到一列下面作来源。口径不同就是不同读取项，不合并。
 * resource/quantity 与整机指标都是开放的目录事实；未知口径保留原值，不猜显示名。
 * 来源候选只看 supportsResourceFacts 与 resourceFactTotalMetricId；可加性、GPU 引擎范围同样只读目录。
 */
export const processMemoryQuantities = [
  "working_set_including_shared",
  "private_working_set",
  "private_commit",
  "peak_private_commit"
] as const;
export type ProcessMemoryQuantity = typeof processMemoryQuantities[number];

export type ResourceFactTotalMetricId = string;

export type ProcessMetricFieldReading = { readonly resource: string; readonly quantity: string };

export interface ProcessMetricField {
  readonly name: string;
  readonly unit: string;
  readonly valueKind: string;
  readonly sourceId: string;
  /** 没有读取项口径的字段为 null。 */
  readonly reading: ProcessMetricFieldReading | null;
  /** 后端说这个字段能不能作资源事实（资源表的列、资源条的来源）：工作集、私有工作集、私有提交量是，峰值提交量不是。 */
  readonly supportsResourceFacts: boolean;
  /** 这个字段作资源事实时比较的整机指标：工作集类是 memory.usage，私有提交量是 virtualMemory.usage；不能作资源事实的为 null。 */
  readonly resourceFactTotalMetricId: ResourceFactTotalMetricId | null;
  /** 后端说这个字段的进程值能否相加（ProcessMetricFieldDefinition.AdditiveAcrossProcesses）；后端没有声明的字段为 null。 */
  readonly additiveAcrossProcesses: boolean | null;
  /** 按 GPU 引擎拆开的字段所属的引擎（物理适配器序号和引擎序号）；不是引擎字段为 null。 */
  readonly gpuEngine: ResourceGpuEngineScope | null;
  /** 引擎类别（后端枚举名，如 Compute）；没有或不唯一时为 null。 */
  readonly engineClass: string | null;
}

export const processFieldCatalogDecoder = defineResponseDecoder<readonly ProcessMetricField[]>(
  "resource-breakdown.process-field-catalog.v1",
  (value) => requireArray(value, "$").map((item, index) => {
    const path = `$[${index}]`;
    const record = requireRecord(item, path);
    if (requireString(record.resource, `${path}.resource`) !== "process") throw new ResponseDecodeError(`${path}.resource`, "process");
    return {
      name: requireNonEmptyString(record.name, `${path}.name`),
      unit: requireString(record.unit, `${path}.unit`),
      valueKind: requireNonEmptyString(record.valueKind, `${path}.valueKind`),
      sourceId: requireNonEmptyString(record.sourceId, `${path}.sourceId`),
      supportsResourceFacts: requireBoolean(record.supportsResourceFacts, `${path}.supportsResourceFacts`),
      additiveAcrossProcesses: written(record, "additiveAcrossProcesses", path, requireBoolean),
      gpuEngine: written(record, "gpuEngine", path, (scope, scopePath) => {
        const engine = requireRecord(scope, scopePath);
        return { physicalIndex: requireSafeInteger(engine.physicalIndex, `${scopePath}.physicalIndex`),
          engineIndex: requireSafeInteger(engine.engineIndex, `${scopePath}.engineIndex`) };
      }),
      engineClass: written(record, "engineClass", path, requireNonEmptyString),
      resourceFactTotalMetricId: requireNullable(record.resourceFactTotalMetricId, `${path}.resourceFactTotalMetricId`,
        requireNonEmptyString),
      reading: requireNullable(record.reading, `${path}.reading`, (reading, readingPath) => {
        const fact = requireRecord(reading, readingPath);
        return {
          resource: requireNonEmptyString(fact.resource, `${readingPath}.resource`),
          quantity: requireNonEmptyString(fact.quantity, `${readingPath}.quantity`)
        };
      })
    };
  }));

export function getProcessFieldCatalog(
  requestClient: Pick<RequestClient, "request">,
  signal?: AbortSignal
): Promise<readonly ProcessMetricField[]> {
  return requestClient.request({
    key: "resource-breakdown.process-field-catalog",
    url: "/api/resource-breakdown/fields/catalog",
    fallbackError: uiText.misc.resourceBreakdownReadFailed,
    decoder: processFieldCatalogDecoder,
    signal,
    request: { method: "GET" }
  });
}
