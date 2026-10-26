import { readFileSync } from "node:fs";
import { deviceTopologyStateDecoder } from "../src/features/deviceTopology/deviceTopologyStateDecoder.ts";
import assert from "node:assert/strict";
import {
  decodeResourceMonitorWireSnapshot,
  decodeResourceProcessDetailsWireSnapshot
} from "../src/api/resourceBreakdownWire.ts";
import { languageOptions } from "../src/i18n/settingsLanguages.ts";
import { applyLanguage, currentLanguage } from "../src/i18n/appTextStore.ts";
import { localizedMetricLabel, resourceProcessDisplayName } from "../src/presentation/metricLabels.ts";
import { metricSnapshotDecoder } from
  "../src/data/monitor/monitorSourceDecoders.ts";

type DecodeRequest = {
  kind: "metric" | "monitor" | "facts" | "details" | "device-topology";
  payload: unknown;
  metricIds?: string[];
};

const request = JSON.parse(readFileSync(0, "utf8")) as DecodeRequest;
const rawPayload = request.payload as Record<string, unknown>;
const rawTable = request.kind === "monitor"
  ? (rawPayload.table ?? {}) as Record<string, unknown>
  : rawPayload;
const rawBreakdown = request.kind === "monitor"
  ? rawPayload.breakdown as Record<string, unknown>
  : {};
const metric = request.kind === "metric"
  ? metricSnapshotDecoder.decode(request.payload)
  : null;
const monitor = request.kind === "monitor" || request.kind === "facts"
  ? decodeResourceMonitorWireSnapshot(request.payload)
  : null;
const table = metric === null
  ? monitor?.table ?? null
  : null;
const breakdownProcess = monitor?.breakdown.bars
  ?.flatMap((bar) => bar.software)
  .flatMap((software) => software.processes)[0];
const tableProcess = table?.rows.find((row) => row.kind === "process");
const details = request.kind === "details"
  ? decodeResourceProcessDetailsWireSnapshot(request.payload) : null;
const decodedMetrics = details?.metrics ?? monitor?.facts?.metrics ?? monitor?.breakdown.bars ?? [];
const decodedProcesses = decodedMetrics.flatMap(metric => metric.software
  .flatMap(software => software.processes.map(process => ({ metricId: metric.metricId, ...process }))));
const localizedNames: Record<string, string[]> = {};
const localizedProcessNames: Record<string, string[]> = {};
for (const language of languageOptions.filter(option => option.id !== "system")) {
  await applyLanguage(language.id);
  assert.equal(currentLanguage(), language.id);
  localizedNames[language.id] = (request.metricIds ?? []).map(id => {
    const label = localizedMetricLabel(id);
    assert.ok(label.trim().length > 0 && label !== id, `${language.id}: missing name for ${id}`);
    return label;
  });
  localizedProcessNames[language.id] = decodedProcesses.map(process => {
    const label = resourceProcessDisplayName(process, process.metricId);
    assert.ok(label.trim().length > 0 && label !== process.attributionKind,
      `${language.id}: missing process name for ${process.attributionKind}/${process.metricId}`);
    if (process.attributionKind === "system-residual") assert.equal(process.name, "");
    return label;
  });
}

process.stdout.write(JSON.stringify({
  machineIdentity: request.kind === "device-topology"
    ? deviceTopologyStateDecoder.decode(request.payload).snapshot?.machineIdentity ?? null : null,
  topologySnapshot: request.kind === "device-topology"
    ? deviceTopologyStateDecoder.decode(request.payload).snapshot ?? null : null,
  decodedMetricIds: decodedMetrics.map(metric => metric.metricId),
  decodedValues: decodedMetrics.map(metric => ({ metricId: metric.metricId,
    total: "totalValue" in metric ? metric.totalValue : null,
    capacity: "capacityValue" in metric ? metric.capacityValue : null,
    percent: "totalSystemPercent" in metric ? metric.totalSystemPercent : null,
    reason: "unavailableReason" in metric ? metric.unavailableReason : null,
    segments: metric.software.length })),
  decodedEmptyBars: (monitor?.breakdown.bars ?? []).filter(bar => bar.totalValue === null)
    .map(bar => ({ metricId: bar.metricId, scaleMode: bar.scaleMode, reason: bar.unavailableReason,
      total: bar.totalValue, capacity: bar.capacityValue, percent: bar.totalSystemPercent, segments: bar.software.length })),
  localizedNames,
  decodedProcesses,
  localizedProcessNames,
  processCount: decodedMetrics.flatMap(metric => metric.software).flatMap(software => software.processes).length,
  factsProcessStartKey: monitor?.facts?.metrics[0]?.software[0]?.processes[0]?.processStartKey ?? null,
  factsProcessSharedValue: monitor?.facts?.metrics[0]?.software[0]?.processes[0]?.sharedValue ?? null,
  monitorVersion: monitor === null ? null : 8,
  tableProcessStartKey: tableProcess?.processStartKey ?? null,
  breakdownProcessStartKey: breakdownProcess?.processStartKey ?? null,
  hasTableSamplingMetadata: table === null
    ? false
    : Object.hasOwn(rawTable, "status")
      || Object.hasOwn(rawTable, "inputDatasets"),
  hasBreakdownSamplingMetadata: monitor === null
    ? false
    : Object.hasOwn(rawBreakdown, "sampling")
      || Object.hasOwn(rawBreakdown, "datasets"),
  hasProviderStates: table === null
    ? false
    : Object.hasOwn(rawTable, "providerStates"),
  metricVersion: metric?.version ?? null,
  metricCapturedAt: metric?.capturedAt ?? null,
  hasMetricDisplayText: metric === null ? false : ["label", "group", "displayValue", "detail"]
    .some(key => Object.hasOwn((rawPayload.items as Record<string, object>)["cpu.usage"] ?? {}, key)),
  metricNumericValue: metric?.items["cpu.usage"]?.numericValue ?? null,
  hasMetricSamplingMetadata: metric === null
    ? false
    : Object.hasOwn(rawPayload, "status")
      || Object.hasOwn(rawPayload, "datasets")
      || Object.hasOwn(rawPayload, "hasValue")
}));
