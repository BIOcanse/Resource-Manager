import assert from "node:assert/strict";
import { startMockServer } from "../browser/contracts/mockServerFixture.mjs";
import { decodeResourceBreakdownWireSnapshot } from "../../src/api/resourceBreakdownWire.ts";
import { decodeResourceFacts, projectResourceFacts } from "../../src/features/resourceTable/resourceFacts.ts";
import { readFile, writeFile } from "node:fs/promises";
import { resolve } from "node:path";
import { dashboardPointValues, dashboardMetricAvailable } from "../../src/preferences/dashboardPreferences.ts";
import { normalizeResourceTableColumns } from "../../src/stores/monitorConfig.ts";
import { resourceTableMetricId } from "../../src/features/resourceTable/resourceTableSources.ts";
import { buildResourceMonitorSubscriptionUrl, normalizeResourceMonitorQuery } from "../../src/data/resourceMonitor/resourceMonitorApi.ts";
import { generateScenario } from "./generateScenario.mjs";
import { metricFieldDevicesDecoder } from "../../src/data/metricFields/metricFieldSource.ts";

const server = await startMockServer();
try {
  const traces = [];
  for (const seed of [1, 17, 104729, 8675309]) {
    await server.scenario({ preset: "realistic", seed });
    const { value: scenario } = await server.json("/__test/scenario");
    const { value: catalog } = await server.json("/api/metrics/catalog");
    const columns = normalizeResourceTableColumns(dashboardPointValues(catalog).resourceTableColumns, catalog, "software");
    const query = normalizeResourceMonitorQuery({ scope: "table", bars: [],
      sampleMetricIds: ["process.inventory", "process.attribution", ...columns.filter(column => column.visible
        && dashboardMetricAvailable(column.id, column.binding, catalog)).map(resourceTableMetricId).filter(id => id !== null)],
      processDetailLevel: columns.some(column => column.visible && ["user", "architecture"].includes(column.id)) ? "full" : "basic",
      visibleColumnIds: [], sortColumnId: "", sortDirection: "desc", processDetailSoftwareIds: [], residualDetailMetricIds: [], tableMode: "software" });
    const path = buildResourceMonitorSubscriptionUrl(query, 1000);
    const startedAt = Date.now();
    const frames = await server.frames([{ id: "table", path }], frames => frames.some(frame => frame.subscriptionId === "table"));
    const wire = frames.find(frame => frame.subscriptionId === "table").value;
    const decoded = decodeResourceFacts(wire);
    const projected = projectResourceFacts(decoded.facts, columns, "software", "impact", "desc", "zh-CN");
    const softwareRows = projected.rows.filter(row => row.kind === "software");
    assert.equal(softwareRows.length, scenario.resourceTableRowCount, `seed ${seed}: all software rows reach the production projection`);
    assert.ok(softwareRows.every(row => row.values.cpu?.value > 0 && row.values.memory?.value > 0), "CPU/memory values are ready");
    traces.push({ seed, path, firstFrameAfterMs: Date.now() - startedAt, wire,
      softwareRows: softwareRows.length, projectedRows: projected.rows.length, columns: projected.columns.map(column => column.id) });
  }
  if (process.env.RM_REALISTIC_EVIDENCE_DIR) await writeFile(resolve(process.env.RM_REALISTIC_EVIDENCE_DIR, "resource-table-wire.json"), JSON.stringify(traces, null, 2));
  for (const preset of ["realistic", "realistic-extreme", "processes-1000", "longest-names", "gpus-4"]) {
    await fetch(`${server.baseUrl}/__test/scenario/reset`, { method: "POST" });
    await server.scenario({ preset, seed: 17 });
    const { value: scenario } = await server.json("/__test/scenario");
    assert.ok(scenario.resourceTableRowCount >= 33);
    if (preset === "processes-1000") assert.equal(scenario.resourceTableRowCount, 1000);
    const frames = await server.frames([{ id: "facts", path: "/api/resource-facts/subscribe?items=process.inventory&items=process.attribution&items=cpu.usage&items=memory.usage&items=gpu.0.usage&items=gpu.0.vram&items=gpu.1.usage&items=gpu.1.vram&items=disk.io&items=network.traffic&processDetailLevel=basic&intervalMs=100" }], frames => frames.length > 0);
    const facts = frames[0].value;
    assert.equal(facts.processes.filter(process => process.processId > 0).length, scenario.resourceTableRowCount);
    // Use the production decoder, including finite values and balanced row sums.
    decodeResourceFacts(facts);
    const cpu = facts.metrics.find(metric => metric.metricId === "cpu.usage");
    assert.ok(Math.abs(cpu.software.reduce((sum, row) => sum + row.value, 0) - cpu.processSumValue) < 1e-6);
    assert.ok(new Set(cpu.software.map(row => row.value)).size > 5, "nonuniform workload distribution");
    assert.ok(facts.software.some(software => /[\u3040-\u30ff\uac00-\ud7af\u0400-\u04ff]/u.test(software.name)), "mixed language names reach wire");
    const { value: wire } = await server.json("/api/resource-monitor/snapshot?ids=cpu.usage,memory.usage,gpu.0.usage,gpu.1.usage");
    const bars = decodeResourceBreakdownWireSnapshot(wire.breakdown);
    assert.equal(bars.bars.length, 4);
    assert.ok(bars.bars.every(bar => bar.software.length === scenario.resourceTableRowCount));
    const { value: scores } = await server.json("/api/gpu/performance-scores");
    assert.equal(scores.gpus.length, scenario.generatedData.gpus.length);
    if (preset === "gpus-4") assert.equal(scores.gpus.length, 4);
    const { value: deviceFields } = await server.json("/api/metrics/fields/catalog");
    assert.equal(deviceFields.length, Math.max(scenario.generatedData.devices.length, scenario.generatedData.gpus.length));
    assert.equal(deviceFields.filter(device => device.device.kind === "gpu_adapter").length, scores.gpus.length);
    const names = [...new Set(deviceFields.flatMap(device => device.fields.map(field => field.name)))];
    const { value: fieldReadings } = await server.json(`/api/metrics/fields/snapshot?fields=${encodeURIComponent(names.join(","))}`);
    const decodedFields = metricFieldDevicesDecoder.decode(fieldReadings);
    assert.ok(decodedFields.some(device => device.fields.some(field => field.numericValue !== null || field.integerValue !== null)), "generated field readings pass the production decoder");
    const { value: fields } = await server.json("/api/resource-breakdown/fields/catalog");
    assert.equal(fields.filter(field => field.gpuEngine !== null).length, scenario.generatedData.engines.length);
    const { value: software } = await server.json("/api/software");
    assert.equal(software.length, scenario.resourceTableRowCount);
  }
  const profile = JSON.parse(await readFile(new URL("../fixtures/realDataProfile.json", import.meta.url), "utf8"));
  await server.scenario(generateScenario(profile, 20, { mode: "realistic-extreme", multiplier: 15 }));
  const { value: batch } = await server.json("/__test/scenario");
  assert.equal(batch.resourceTableRowCount, 1200, "batch output can be posted directly and extremes exceed the old row cap");
  const batchFrames = await server.frames([{ id: "batch", path: "/api/resource-facts/subscribe?items=process.inventory&intervalMs=100" }], frames => frames.length > 0);
  assert.equal(batchFrames[0].value.processes.length, 1200);
  await fetch(`${server.baseUrl}/__test/scenario/reset`, { method: "POST" });
  const { value: reset } = await server.json("/__test/scenario");
  assert.equal(reset.generatedData, null, "old journeys remain opt in");
  console.log("realistic scenarios exercise production wire decoders");
} finally { await server.close(); }
