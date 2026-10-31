// 资源表一列先选项目、再选来源（docs/metric_item_source_choice_2026-10-25.md）：
// 进程字段目录的解码、内存列的来源、按来源请求的资源事实，以及选私有提交量时总计行和系统/驱动保留行。
import assert from "node:assert/strict";
import { processFieldCatalogDecoder } from "../src/data/resourceMonitor/processFieldCatalog.ts";
import { decodeResourceFacts, projectResourceFacts } from "../src/features/resourceTable/resourceFacts.ts";
import {
  resourceColumnSourceChoices,
  resourceColumnSourceLabel,
  resourceTableColumnText,
  resourceTableMetricId
} from "../src/features/resourceTable/resourceTableSources.ts";
import { uiText } from "../src/text.ts";
import { catalogField, gpuEngineField, gpuResidentField, staticProcessFieldCatalog } from "./processFieldCatalogFixture.ts";
import { factsMetricKeys } from "./resourceFactsWireFixture.ts";

// 后端 ProcessMetricFieldRegistry 的 wire 形状：内存四种口径带 reading，其余为 null。
// supportsResourceFacts 是后端说的（工作集、私有工作集、私有提交量 true，峰值提交量 false），前端只读它。
const totalFor = (quantity: string | null) => quantity === "working_set_including_shared" || quantity === "private_working_set" ? "memory.usage"
  : quantity === "private_commit" ? "virtualMemory.usage" : null;
const wireField = (name: string, quantity: string | null, supportsResourceFacts = quantity !== null && quantity !== "peak_private_commit") => ({
  ...catalogField(name, "unsigned64", "B", "windows.process.query", { reading: quantity === null ? null : { resource: "memory", quantity },
    additive: quantity === null ? null : quantity === "private_working_set" || quantity === "private_commit" }),
  supportsResourceFacts, resourceFactTotalMetricId: supportsResourceFacts ? totalFor(quantity) : null });
const fields = processFieldCatalogDecoder.decode([
  wireField("process_all_handle_count_actual", null),
  wireField("process_all_working_set_actual", "working_set_including_shared"),
  wireField("process_all_private_working_set_actual", "private_working_set"),
  wireField("process_all_private_commit_actual", "private_commit"),
  wireField("process_all_peak_private_commit_actual", "peak_private_commit")
]);
assert.deepEqual(fields.map((field) => field.reading?.quantity ?? null),
  [null, "working_set_including_shared", "private_working_set", "private_commit", "peak_private_commit"]);
assert.throws(() => processFieldCatalogDecoder.decode([{ ...wireField("x", null), reading: undefined }]), /reading/,
  "A field without the reading member is not the current wire");
assert.equal(processFieldCatalogDecoder.decode([wireField("x", "shared_bytes")])[0].reading?.quantity, "shared_bytes",
  "Unknown quantities remain catalog data and use the field name as label");
assert.throws(() => processFieldCatalogDecoder.decode([{ ...wireField("x", null), resource: "gpu" }]), /resource/);
assert.throws(() => processFieldCatalogDecoder.decode([{ ...wireField("x", null), supportsResourceFacts: undefined }]), /supportsResourceFacts/,
  "Whether a field can be a resource fact is a backend fact; a catalog without it is not the current wire");

// 内存列：自动选择在前，然后是资源事实接受的口径（峰值提交量不进资源事实），按目录顺序。别的列没有来源。
const choices = resourceColumnSourceChoices("memory", fields);
assert.deepEqual(choices.map((choice) => choice.source),
  [null, "process_all_working_set_actual", "process_all_private_working_set_actual", "process_all_private_commit_actual"],
  "The memory column lists every memory quantity; private commit is compared with system commit (contract 2026-10-25)");
assert.deepEqual(choices.map((choice) => resourceColumnSourceLabel(choice, fields)), [uiText.metricPicker.sourceAutomatic(null),
  uiText.resourceTable.memoryQuantities.working_set_including_shared, uiText.resourceTable.memoryQuantities.private_working_set,
  uiText.resourceTable.memoryQuantities.private_commit]);
assert.deepEqual(resourceColumnSourceChoices("cpu", fields), []);
assert.deepEqual(resourceColumnSourceChoices("memory", []), [], "No catalog yet: nothing to choose");
// 能不能作来源只听目录：仅有能力标记却没有匹配的整机指标，仍不能列为本列候选。
const peakAllowed = processFieldCatalogDecoder.decode([wireField("process_all_peak_private_commit_actual", "peak_private_commit", true)]);
assert.deepEqual(resourceColumnSourceChoices("memory", peakAllowed).map((choice) => resourceColumnSourceLabel(choice, peakAllowed)),
  [], "A capability without a matching total metric cannot be offered");
// 自动选择写出后端这次实际读的字段（资源事实的 processFieldName）。
assert.equal(resourceColumnSourceLabel(choices[0], fields, "process_all_private_working_set_actual"),
  uiText.metricPicker.sourceAutomatic(uiText.resourceTable.memoryQuantities.private_working_set));

// 列请求的指标：自动选择是列原来的指标，选了来源就是那个字段。
assert.equal(resourceTableMetricId({ id: "memory" }), "memory.usage");
assert.equal(resourceTableMetricId({ id: "memory", source: null }), "memory.usage");
assert.equal(resourceTableMetricId({ id: "memory", source: "process_all_private_commit_actual" }), "process_all_private_commit_actual");
assert.equal(resourceTableMetricId({ id: "gpu.1.vram" }), "gpu.1.vram");
assert.equal(resourceTableMetricId({ id: "name" }), null);

// 表头写出口径；自动选择和私有工作集用原来的说明。
assert.deepEqual(resourceTableColumnText({ id: "memory", source: null }, fields),
  { label: uiText.resourceTable.columns.memory, basis: uiText.resourceBreakdown.memoryTableBasis });
assert.deepEqual(resourceTableColumnText({ id: "memory", source: null }, fields, "process_all_private_working_set_actual"), {
  label: uiText.resourceTable.columns.memory,
  basis: `${uiText.metricPicker.sourceAutomatic(uiText.resourceTable.memoryQuantities.private_working_set)}\n${uiText.resourceBreakdown.memoryTableBasis}` });
assert.equal(resourceTableColumnText({ id: "cpu", source: null }, fields, null).basis, undefined);
assert.deepEqual(resourceTableColumnText({ id: "memory", source: "process_all_private_commit_actual" }, fields), {
  label: uiText.resourceTable.memoryColumnBySource(uiText.resourceTable.memoryQuantities.private_commit),
  basis: uiText.resourceTable.memorySourceBasis.private_commit });
assert.equal(resourceTableColumnText({ id: "memory", source: "process_all_private_working_set_actual" }, fields).basis,
  uiText.resourceBreakdown.memoryTableBasis);
assert.equal(resourceTableColumnText({ id: "memory", source: "process_all_private_commit_actual" }, []).label,
  uiText.resourceTable.memoryColumnBySource("process_all_private_commit_actual"),
  "Before the catalog arrives the header names the field instead of borrowing the automatic name");

// 私有提交量的资源事实（后端 v2 形状）：总计是 totalMetricId 指向的系统提交量，差额在系统/驱动残差。
const GiB = 1024 ** 3;
const wire = {
  version: 2, capturedAt: "2026-10-25T00:00:00Z",
  software: [{ softwareId: "example", name: "Example", kind: "other", attributionKind: "other" },
    { softwareId: "resource-residual:virtualMemory.usage", name: "", kind: "windows_system", attributionKind: "system-residual" }],
  processes: [{ processId: 42, processStartKey: "100", name: "example.exe", executablePath: null, userName: "user", architecture: "x64", attributionKind: "other" },
    { processId: -1, processStartKey: null, name: "", executablePath: null, userName: null, architecture: null, attributionKind: "system-residual" }],
  metrics: [{ metricId: "process_all_private_commit_actual", unit: "B", ...factsMetricKeys({ totalMetricId: "virtualMemory.usage", processSumValue: GiB,
      residualValue: 5 * GiB, additiveAcrossProcesses: true, capturedAt: "2026-10-25T00:00:00Z", quantity: "private_commit" }),
    processFieldName: "process_all_private_commit_actual", totalValue: 6 * GiB, capacityValue: 24 * GiB,
    totalSystemPercent: 25, sharedValue: null, software: [
      { softwareId: "example", value: GiB, systemPercent: 100 / 24, processCount: 1, baseScore: 5, sharedValue: null,
        processes: [{ processIndex: 0, value: GiB, systemPercent: 100 / 24, softwarePercent: 100, baseScore: 5, sharedValue: null }] },
      { softwareId: "resource-residual:virtualMemory.usage", value: 5 * GiB, systemPercent: 500 / 24, processCount: 0, baseScore: 0, sharedValue: null,
        processes: [{ processIndex: 1, value: 5 * GiB, systemPercent: 500 / 24, softwarePercent: 100, baseScore: 0, sharedValue: null }] }] }],
  attributions: [{ processIndex: 0, softwareId: "example", baseScore: 5 }, { processIndex: 1, softwareId: "resource-residual:virtualMemory.usage", baseScore: 0 }]
};
const facts = decodeResourceFacts(wire).facts!;
assert.equal(facts.metrics[0].totalMetricId, "virtualMemory.usage");
assert.equal(facts.metrics[0].processSumValue, GiB);
assert.throws(() => decodeResourceFacts({ ...wire, metrics: [{ ...wire.metrics[0], totalMetricId: undefined }] }), /totalMetricId/);
assert.throws(() => decodeResourceFacts({ ...wire, metrics: [{ ...wire.metrics[0], processSumValue: undefined }] }), /processSumValue/);
assert.equal(facts.metrics[0].processFieldName, "process_all_private_commit_actual");
assert.throws(() => decodeResourceFacts({ ...wire, metrics: [{ ...wire.metrics[0], processFieldName: undefined }] }), /processFieldName/);

const commitColumns = [{ id: "name", visible: true, width: 200 }, { id: "memory", visible: true, width: 110, source: "process_all_private_commit_actual" }];
const table = projectResourceFacts(facts, commitColumns, "software", "memory", "desc");
assert.equal(table.columns[1].source, "process_all_private_commit_actual");
assert.equal(table.columns[1].unit, "B");
assert.equal(table.columns[1].processFieldName, "process_all_private_commit_actual", "The column carries the field its metric actually read");
const summary = table.rows[0];
assert.equal(summary.values.memory.value, 6 * GiB, "The total row is the system commit charge, not the process sum");
assert.equal(summary.values.memory.percent, 25);
const residual = table.rows.find((row) => row.softwareId === "resource-residual:virtualMemory.usage" && row.kind === "software")!;
assert.equal(residual.values.memory.value, 5 * GiB);
const residualAttribution = projectResourceFacts(facts, table.columns, "process", "memory", "desc").rows
  .find((row) => row.kind === "attribution" && row.softwareId === "resource-residual:virtualMemory.usage")!;
assert.equal(residualAttribution.name, uiText.resourceBreakdown.residualCommit, "The residual is an attribution named after its backend metric");
assert.equal(residualAttribution.processId, null);
assert.equal(table.rows.find((row) => row.softwareId === "example" && row.kind === "software")!.values.memory.value, GiB);

// 自动选择的列不认领进程字段指标。
const automatic = projectResourceFacts(facts, [{ id: "name", visible: true, width: 200 }, { id: "memory", visible: true, width: 110 }], "software", "memory", "desc");
assert.equal(automatic.rows[0].values.memory.availability, "Unavailable");

console.log("resourceTableSources: process field catalog, memory column sources and private-commit totals passed");

// Matching is driven only by the catalog's supportsResourceFacts and resourceFactTotalMetricId, over the backend's real field set
// (ProcessMetricFieldRegistry plus the per-adapter ProcessGpuResourceFieldRegistry fields).
const luid = 0x17;
const authority = processFieldCatalogDecoder.decode([...staticProcessFieldCatalog(),
  gpuEngineField(0, luid, 0, 0), gpuEngineField(0, luid, 0, 1, "Compute"), gpuEngineField(0, luid, 1, 0, "FutureClass"),
  gpuResidentField(0, luid, "dedicated_resident_apportioned"), gpuResidentField(0, luid, "dedicated_resident_private"),
  gpuResidentField(0, luid, "dedicated_resident_shared_apportioned"),
  catalogField("process_all_gpu_future_attributed_gpu_0_luid_0000000000000017", "float64", "B", "windows.etw.gpu-allocation",
    { reading: { resource: "gpu", quantity: "future_quantity" }, total: "gpu.0.vram", additive: true })]);
const sourcesOf = (id: string) => resourceColumnSourceChoices(id, authority).map(choice => choice.source);
assert.deepEqual(sourcesOf("cpu"), [null, "process_all_cpu_usage_actual"]);
assert.deepEqual(sourcesOf("disk"), [null, "process_all_physical_disk_read_bytes_attributed", "process_all_physical_disk_write_bytes_attributed"]);
assert.deepEqual(sourcesOf("network"), [null, "process_all_network_receive_bytes_attributed", "process_all_network_send_bytes_attributed",
  "process_all_network_receive_rate_attributed", "process_all_network_send_rate_attributed"]);
assert.deepEqual(sourcesOf("memory"), [null, "process_all_working_set_actual", "process_all_private_working_set_actual", "process_all_private_commit_actual"]);
assert.equal(sourcesOf("gpu.0.usage").length, 4);
assert.equal(sourcesOf("gpu.0.vram").length, 5);
assert.deepEqual(sourcesOf("gpu.1.usage"), [], "A GPU without catalog fields offers no source");
assert.deepEqual(sourcesOf("name"), []);
const labelOf = (id: string, source: string) => resourceColumnSourceLabel({ source, field: authority.find(field => field.name === source)! }, authority);
const text = uiText.resourceTable;
assert.equal(labelOf("cpu", "process_all_cpu_usage_actual"), text.processFieldNames.process_all_cpu_usage_actual);
assert.equal(labelOf("network", "process_all_network_send_rate_attributed"), text.processFieldNames.process_all_network_send_rate_attributed);
assert.deepEqual(sourcesOf("gpu.0.usage").slice(1).map(source => labelOf("gpu.0.usage", source!)),
  [text.gpuEngineBusy(0, 0, null), text.gpuEngineBusy(1, 0, text.gpuEngineClasses.Compute!), text.gpuEngineBusy(0, 1, "FutureClass")],
  "Engine sources are named from the catalog's engine scope; an unknown class is shown as sent");
assert.deepEqual(sourcesOf("gpu.0.vram").slice(1).map(source => labelOf("gpu.0.vram", source!)),
  [text.gpuQuantities.dedicated_resident_apportioned, text.gpuQuantities.dedicated_resident_private,
    text.gpuQuantities.dedicated_resident_shared_apportioned, "process_all_gpu_future_attributed_gpu_0_luid_0000000000000017"]);
assert.equal(resourceTableMetricId({ id: "gpu.0.usage", source: sourcesOf("gpu.0.usage")[2] }), sourcesOf("gpu.0.usage")[2]);
assert.equal(resourceTableColumnText({ id: "network", source: "process_all_network_receive_rate_attributed" }, authority).label,
  `${uiText.resourceTable.columns.network} · ${text.processFieldNames.process_all_network_receive_rate_attributed}`);
// Additivity is the catalog's declaration: working set is not additive, private working set is.
const workingSetBasis = resourceTableColumnText({ id: "memory", source: "process_all_working_set_actual" }, authority).basis!;
assert.ok(workingSetBasis.includes(text.nonAdditiveBasis));
assert.ok(workingSetBasis.includes(text.memorySourceBasis.working_set_including_shared), "The additivity note does not replace the quantity description");
assert.ok(!resourceTableColumnText({ id: "memory", source: "process_all_private_working_set_actual" }, authority).basis!.includes(text.nonAdditiveBasis));
assert.equal(authority.find(field => field.name === "process_all_handle_count_actual")!.additiveAcrossProcesses, true,
  "Handle counts use the backend's additive declaration for software columns");
assert.equal(authority.find(field => field.name === "process_all_image_path_actual")!.additiveAcrossProcesses, null,
  "An undeclared field stays undeclared");
assert.deepEqual(authority.find(field => field.reading?.quantity === "engine_busy_percent" && field.engineClass === "Compute")!.gpuEngine,
  { physicalIndex: 0, engineIndex: 1 });
// Every catalog key is written by System.Text.Json; a missing one is a changed wire, not a default.
for (const key of ["additiveAcrossProcesses", "gpuEngine", "engineClass", "resourceFactTotalMetricId", "reading"]) {
  const { [key]: _removed, ...rest } = staticProcessFieldCatalog()[0] as Record<string, unknown>;
  assert.throws(() => processFieldCatalogDecoder.decode([rest]), new RegExp(key));
}
console.log("resourceTableSources: catalog-driven CPU, disk, network, GPU engine and GPU resident sources and catalog additivity passed");
