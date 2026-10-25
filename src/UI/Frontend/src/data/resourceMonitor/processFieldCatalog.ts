import type { RequestClient } from "../../frontendRuntime/request/RequestClient.ts";
import {
  defineResponseDecoder,
  requireArray,
  requireBoolean,
  requireNonEmptyString,
  requireNullable,
  requireOneOf,
  requireRecord,
  requireString,
  ResponseDecodeError
} from "../../frontendRuntime/request/ResponseDecoder.ts";
import { uiText } from "../../text.ts";

/*
 * 进程字段目录（GET /api/resource-breakdown/fields/catalog，后端 ProcessMetricFieldRegistry）。
 * 字段的 reading 说它属于哪种资源、是哪种口径（docs/metric_reading_source_selection.md「进程内存读取项与资源事实」）：
 * 资源表靠它把同一资源的不同口径挂到一列下面作来源。口径不同就是不同读取项，不合并。
 * 内存的四种口径是后端合同写明的全部取值；出现别的就是 wire 变了，解码失败而不是猜。
 */
export const processMemoryQuantities = [
  "working_set_including_shared",
  "private_working_set",
  "private_commit",
  "peak_private_commit"
] as const;
export type ProcessMemoryQuantity = typeof processMemoryQuantities[number];

/** 后端合同里资源事实能比较的两个整机指标；出现别的就是 wire 变了。 */
export const resourceFactTotalMetricIds = ["memory.usage", "virtualMemory.usage"] as const;
export type ResourceFactTotalMetricId = typeof resourceFactTotalMetricIds[number];

export type ProcessMetricFieldReading = { readonly resource: "memory"; readonly quantity: ProcessMemoryQuantity };

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
      resourceFactTotalMetricId: requireNullable(record.resourceFactTotalMetricId, `${path}.resourceFactTotalMetricId`,
        (total, totalPath) => requireOneOf(total, totalPath, resourceFactTotalMetricIds)),
      reading: requireNullable(record.reading, `${path}.reading`, (reading, readingPath) => {
        const fact = requireRecord(reading, readingPath);
        return {
          resource: requireOneOf(fact.resource, `${readingPath}.resource`, ["memory"] as const),
          quantity: requireOneOf(fact.quantity, `${readingPath}.quantity`, processMemoryQuantities)
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
