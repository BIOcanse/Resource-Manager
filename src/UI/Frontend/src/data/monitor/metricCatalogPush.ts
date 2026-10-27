import {
  defineResponseDecoder,
  requireNonEmptyString,
  requireRecord
} from "../../frontendRuntime/request/ResponseDecoder.ts";
import type { MetricDefinition } from "../../types.ts";
import { decodeMetricFieldCatalog, type MetricFieldCatalogDevice } from "../metricFields/metricFieldSource.ts";
import { decodeMetricCatalog } from "./monitorSourceDecoders.ts";

/*
 * 指标目录订阅（/api/metrics/fields/catalog/subscribe）的一帧：后端一次提交的目录（MetricCatalogWireSnapshot）。
 * 三项来自同一份已提交目录，同帧到达：
 * - catalogRevision：不透明标记，只作相等比较，和指标快照帧上的 catalogRevision 是同一个值；
 * - fields：与 GET /api/metrics/fields/catalog 同形；
 * - metrics：与 GET /api/metrics/catalog 同形。
 * 订阅时后端推一帧当前值，之后只在目录内容真的变化时推（设备、字段、指标定义、能力、候选来源、
 * 组件安装信息都算内容）；修订号覆盖全部公开内容，同一修订号就是同一份内容。GET 两个接口仍返回裸数组，不经过这里。
 */
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
