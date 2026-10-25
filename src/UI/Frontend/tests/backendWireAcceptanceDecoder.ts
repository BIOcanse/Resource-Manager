import { readFileSync } from "node:fs";
import {
  decodeResourceMonitorWireSnapshot
} from "../src/api/resourceBreakdownWire.ts";
import { metricSnapshotDecoder } from
  "../src/data/monitor/monitorSourceDecoders.ts";

type DecodeRequest = {
  kind: "metric" | "monitor" | "facts";
  payload: unknown;
};

const request = JSON.parse(readFileSync(0, "utf8")) as DecodeRequest;
const rawPayload = request.payload as Record<string, unknown>;
const rawTable = request.kind === "monitor"
  ? rawPayload.table as Record<string, unknown>
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

process.stdout.write(JSON.stringify({
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
  metricDisplayValue: metric?.items["cpu.usage"]?.displayValue ?? null,
  metricNumericValue: metric?.items["cpu.usage"]?.numericValue ?? null,
  hasMetricSamplingMetadata: metric === null
    ? false
    : Object.hasOwn(rawPayload, "status")
      || Object.hasOwn(rawPayload, "datasets")
      || Object.hasOwn(rawPayload, "hasValue")
}));
