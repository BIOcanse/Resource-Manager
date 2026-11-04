import {
  defineResponseDecoder,
  requireNonEmptyString,
  requireRecord
} from "../../frontendRuntime/request/ResponseDecoder.ts";
import type { MetricDefinition } from "../../types.ts";
import { decodeMetricFieldCatalog, type MetricFieldCatalogDevice } from "../metricFields/metricFieldSource.ts";
import { decodeMetricCatalog } from "./monitorSourceDecoders.ts";

/* One committed catalog arrives atomically as revision, fields and metrics. Compare revisions for equality only; equal revisions cover identical public content. Subscriptions replay once, then push content changes. */
export interface MetricCatalogPush {
  readonly catalogRevision: string;
  readonly fields: readonly MetricFieldCatalogDevice[];
  readonly metrics: MetricDefinition[];
}

export const metricCatalogPushDecoder = defineResponseDecoder<MetricCatalogPush>(
  "metric-catalog.push",
  (value) => {
    const record = requireRecord(value, "$");
    return {
      catalogRevision: requireNonEmptyString(record.catalogRevision, "$.catalogRevision"),
      fields: decodeMetricFieldCatalog(record.fields, "$.fields"),
      metrics: decodeMetricCatalog(record.metrics, "$.metrics")
    };
  });
