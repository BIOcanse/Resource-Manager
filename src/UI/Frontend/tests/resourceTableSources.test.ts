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

// 后端 ProcessMetricFieldRegistry 的 wire 形状：内存四种口径带 reading，其余为 null。
// supportsResourceFacts 是后端说的（工作集、私有工作集、私有提交量 true，峰值提交量 false），前端只读它。
const wireField = (name: string, quantity: string | null, supportsResourceFacts = quantity !== null && quantity !== "peak_private_commit") => ({
  name, valueKind: "unsigned64", unit: "B", sourceId: "windows.process.query",
  resource: "process", vendor: "all", reading: quantity === null ? null : { resource: "memory", quantity }, supportsResourceFacts });
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
assert.throws(() => processFieldCatalogDecoder.decode([wireField("x", "shared_bytes")]), /quantity/,
  "An unknown memory quantity is a wire change, not something to guess");
assert.throws(() => processFieldCatalogDecoder.decode([{ ...wireField("x", null), resource: "gpu" }]), /resource/);
assert.throws(() => processFieldCatalogDecoder.decode([{ ...wireField("x", null), supportsResourceFacts: undefined }]), /supportsResourceFacts/,
  "Whether a field can be a resource fact is a backend fact; a catalog without it is not the current wire");

// 内存列：自动选择在前，然后是资源事实接受的口径（峰值提交量不进资源事实），按目录顺序。别的列没有来源。
const choices = resourceColumnSourceChoices("memory", fields);
assert.deepEqual(choices.map((choice) => choice.source),
  [null, "process_all_working_set_actual", "process_all_private_working_set_actual", "process_all_private_commit_actual"]);
assert.deepEqual(choices.map((choice) => resourceColumnSourceLabel(choice, fields)), [uiText.metricPicker.sourceAutomatic(null),
  uiText.resourceTable.memoryQuantities.working_set_including_shared, uiText.resourceTable.memoryQuantities.private_working_set,
  uiText.resourceTable.memoryQuantities.private_commit]);
assert.deepEqual(resourceColumnSourceChoices("cpu", fields), []);
assert.deepEqual(resourceColumnSourceChoices("memory", []), [], "No catalog yet: nothing to choose");
// 能不能作来源只听目录：后端改标峰值提交量后它就出现，名字没有文案时写字段名。
const peakAllowed = processFieldCatalogDecoder.decode([wireField("process_all_peak_private_commit_actual", "peak_private_commit", true)]);
assert.deepEqual(resourceColumnSourceChoices("memory", peakAllowed).map((choice) => resourceColumnSourceLabel(choice, peakAllowed)),
  [uiText.metricPicker.sourceAutomatic(null), "process_all_peak_private_commit_actual"]);
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
  metrics: [{ metricId: "process_all_private_commit_actual", unit: "B", totalMetricId: "virtualMemory.usage", processFieldName: "process_all_private_commit_actual", totalValue: 6 * GiB, capacityValue: 24 * GiB,
    totalSystemPercent: 25, processSumValue: GiB, sharedValue: null, software: [
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
const residualProcess = table.rows.find((row) => row.kind === "process" && row.softwareId === "resource-residual:virtualMemory.usage")!;
assert.equal(residualProcess.name, uiText.resourceBreakdown.residualCommit, "The residual is named after the commit total it belongs to");
assert.equal(table.rows.find((row) => row.softwareId === "example" && row.kind === "software")!.values.memory.value, GiB);

// 自动选择的列不认领进程字段指标。
const automatic = projectResourceFacts(facts, [{ id: "name", visible: true, width: 200 }, { id: "memory", visible: true, width: 110 }], "software", "memory", "desc");
assert.equal(automatic.rows[0].values.memory, undefined);

console.log("resourceTableSources: process field catalog, memory column sources and private-commit totals passed");
