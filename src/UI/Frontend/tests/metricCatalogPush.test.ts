import assert from "node:assert/strict";
import { readFileSync } from "node:fs";
import { metricFieldCatalogDecoder } from "../src/data/metricFields/metricFieldSource.ts";
import { metricCatalogPushDecoder } from "../src/data/monitor/metricCatalogPush.ts";
import { metricCatalogDecoder } from "../src/data/monitor/monitorSourceDecoders.ts";

/*
 * 目录订阅的一帧（后端 MetricCatalogWireSnapshot）：{catalogRevision, fields, metrics}。
 * fields 与 GET /api/metrics/fields/catalog 同形（这里用 fixtures/metricFieldsWire/catalog.json，后端真实响应），
 * metrics 与 GET /api/metrics/catalog 同形（每条都带 hasDedicatedMemoryMetrics，非 GPU 为 null）。
 * 信封严格解码：必须是对象，三个键都在且类型对；旧的裸数组推送直接拒绝。GET 两个解码器不变。
 */

const fields = JSON.parse(readFileSync(new URL("./fixtures/metricFieldsWire/catalog.json", import.meta.url), "utf8"));
const metrics = [
  { id: "cpu.usage", unit: "%", requiredComponentId: null, selectable: true, disabledReason: null,
    scopeKind: null, scopeKey: null, candidateSources: [], hasDedicatedMemoryMetrics: null },
  { id: "gpu.0.vram", unit: "B", requiredComponentId: null, selectable: true, disabledReason: null,
    scopeKind: "gpu", scopeKey: "opaque-gpu", candidateSources: [], hasDedicatedMemoryMetrics: false },
  { id: "system.motherboardTemperature", unit: "°C", requiredComponentId: "hardware-sensors-pawnio-provider",
    selectable: false, disabledReason: { domain: 3, code: 2, args: [] }, scopeKind: null, scopeKey: null,
    candidateSources: [], hasDedicatedMemoryMetrics: null }
];
const envelope = { catalogRevision: "opaque-revision-1", fields, metrics };

// 三项同帧解出；fields、metrics 与对应 GET 解码器的结果完全相同。
const decoded = metricCatalogPushDecoder.decode(structuredClone(envelope));
assert.equal(decoded.catalogRevision, "opaque-revision-1");
assert.deepEqual(decoded.fields, metricFieldCatalogDecoder.decode(structuredClone(fields)));
assert.deepEqual(decoded.metrics, metricCatalogDecoder.decode(structuredClone(metrics)));
assert.equal(decoded.fields.length, fields.length);
assert.deepEqual(decoded.metrics.map((metric) => metric.id), metrics.map((metric) => metric.id));
// 安装信息原样带过来；它也是目录内容，变化时后端换修订号。
assert.equal(decoded.metrics[2]!.requiredComponentId, "hardware-sensors-pawnio-provider");
assert.equal(decoded.metrics[2]!.selectable, false);

// 空目录也是一帧完整的目录（设备全部消失）。
assert.deepEqual(metricCatalogPushDecoder.decode({ catalogRevision: "r", fields: [], metrics: [] }),
  { catalogRevision: "r", fields: [], metrics: [] });

// GET 仍是裸数组，GET 解码器照旧接受。
assert.equal(metricFieldCatalogDecoder.decode(structuredClone(fields)).length, fields.length);

const rejects = (value: unknown, path: RegExp, why: string) =>
  assert.throws(() => metricCatalogPushDecoder.decode(value), path, why);
rejects(structuredClone(fields), /'\$'/, "the old bare-array push is not an envelope");
rejects(null, /'\$'/, "null frame");
rejects({ fields, metrics }, /\$\.catalogRevision/, "missing revision");
rejects({ ...envelope, catalogRevision: "" }, /\$\.catalogRevision/, "empty revision");
rejects({ ...envelope, catalogRevision: 7 }, /\$\.catalogRevision/, "numeric revision");
rejects({ catalogRevision: "r", metrics }, /\$\.fields/, "missing fields");
rejects({ catalogRevision: "r", fields }, /\$\.metrics/, "missing metrics");
rejects({ ...envelope, fields: {} }, /\$\.fields/, "fields not an array");
rejects({ ...envelope, metrics: null }, /\$\.metrics/, "metrics null");
rejects({ ...envelope, metrics: [{ ...metrics[0], id: "" }] }, /\$\.metrics\[0\]\.id/, "metric without id");
rejects({ ...envelope, metrics: [{ ...metrics[0], candidateSources: undefined }] }, /\$\.metrics\[0\]\.candidateSources/,
  "metric without candidate sources");
rejects({ ...envelope, fields: [{ ...fields[0], device: undefined }] }, /\$\.fields\[0\]\.device/, "device without descriptor");

console.log("metric catalog push tests passed");
