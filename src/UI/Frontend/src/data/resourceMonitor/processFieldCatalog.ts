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

/** Every key is required; nullable values use explicit null. */
function written<T>(record: Record<string, unknown>, key: string, path: string, decode: (value: unknown, path: string) => T): T | null {
  if (!Object.hasOwn(record, key)) throw new ResponseDecodeError(`${path}.${key}`, "written key");
  return requireNullable(record[key], `${path}.${key}`, decode);
}

/* Reading metadata groups resource measures without merging distinct quantities. Preserve unknown resource/quantity facts and trust backend declarations for eligibility, additivity and GPU scope. */
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
  readonly table?: { readonly scopes: readonly ("process" | "software")[];
    readonly sources: readonly { readonly fieldName: string; readonly sourceId: string }[] };
  readonly name: string;
  readonly unit: string;
  readonly valueKind: string;
  readonly sourceId: string;
  /** Null when no reading measure is declared. */
  readonly reading: ProcessMetricFieldReading | null;
  /** Backend eligibility for resource facts; peak commit is excluded. */
  readonly supportsResourceFacts: boolean;
  /** Comparison total metric, or null when ineligible for resource facts. */
  readonly resourceFactTotalMetricId: ResourceFactTotalMetricId | null;
  /** Backend-declared process additivity, or null when undeclared. */
  readonly additiveAcrossProcesses: boolean | null;
  /** Physical adapter and engine indices for engine-scoped fields; otherwise null. */
  readonly gpuEngine: ResourceGpuEngineScope | null;
  /** Backend engine class, or null when absent or ambiguous. */
  readonly engineClass: string | null;
}

export const processFieldCatalogDecoder = defineResponseDecoder<readonly ProcessMetricField[]>(
  "resource-breakdown.process-field-catalog.v1",
  (value) => requireArray(value, "$").map((item, index) => {
    const path = `$[${index}]`;
    const record = requireRecord(item, path);
    if (requireString(record.resource, `${path}.resource`) !== "process") throw new ResponseDecodeError(`${path}.resource`, "process");
    return {
      table: (() => {
        const table = requireRecord(record.table, `${path}.table`);
        return { scopes: requireArray(table.scopes, `${path}.table.scopes`).map((scope, index) => {
          if (scope !== "process" && scope !== "software") throw new ResponseDecodeError(`${path}.table.scopes[${index}]`, "process or software");
          return scope;
        }), sources: requireArray(table.sources, `${path}.table.sources`).map((source, index) => {
          const sourcePath = `${path}.table.sources[${index}]`;
          const item = requireRecord(source, sourcePath);
          return { fieldName: requireNonEmptyString(item.fieldName, `${sourcePath}.fieldName`),
            sourceId: requireNonEmptyString(item.sourceId, `${sourcePath}.sourceId`) };
        }) };
      })(),
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
