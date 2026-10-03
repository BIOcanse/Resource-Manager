import type { RequestClient } from "../../frontendRuntime/request/RequestClient.ts";
import {
  defineResponseDecoder, requireArray, requireBoolean, requireFiniteNumber,
  requireNonEmptyString, requireNonNegativeSafeInteger, requireRecord, requireString,
  ResponseDecodeError
} from "../../frontendRuntime/request/ResponseDecoder.ts";
import { uiText } from "../../text.ts";

export interface RecordingHeader {
  id: string;
  softwareId: string;
  softwareName: string;
  startedAt: string;
  endedAt: string | null;
  maximumDurationSeconds: number;
  status: "recording" | "completed";
  stopReason: string | null;
  incomplete: boolean;
}

export interface FrameInterval {
  processId: number;
  processStartKey: number;
  source: number;
  swapChain: number;
  endedAt: string;
  durationMs: number;
}

export interface RecordingSummary {
  intervalCount: number;
  durationMs: number;
  averageFps: number;
  onePercentLowFps: number;
  pointOnePercentLowFps: number;
  p50FrameTimeMs: number;
  p95FrameTimeMs: number;
  p99FrameTimeMs: number;
  maxFrameTimeMs: number;
}

export interface ResourceSample {
  capturedAt: string;
  software: Record<string, number | null>;
  system: Record<string, number | null>;
  processes: Array<{ processId: number; processStartKey: number }>;
}

export interface RecordingReport {
  recording: RecordingHeader;
  summary: RecordingSummary | null;
  frameIntervals: FrameInterval[];
  fpsDistribution: Array<{ fromFps: number; toFps: number; count: number }>;
  resourceSamples: ResourceSample[];
}

function nullableString(value: unknown, path: string): string | null {
  return value === null ? null : requireString(value, path);
}

function header(value: unknown): RecordingHeader {
  const row = requireRecord(value);
  const status = requireString(row.status, "status");
  if (status !== "recording" && status !== "completed") {
    throw new ResponseDecodeError("status", "recording or completed");
  }
  return {
    id: requireNonEmptyString(row.id, "id"),
    softwareId: requireNonEmptyString(row.softwareId, "softwareId"),
    softwareName: requireString(row.softwareName, "softwareName"),
    startedAt: requireString(row.startedAt, "startedAt"),
    endedAt: nullableString(row.endedAt, "endedAt"),
    maximumDurationSeconds: requireNonNegativeSafeInteger(row.maximumDurationSeconds, "maximumDurationSeconds"),
    status,
    stopReason: nullableString(row.stopReason, "stopReason"),
    incomplete: requireBoolean(row.incomplete, "incomplete")
  };
}

function metricValues(value: unknown, path: string): Record<string, number | null> {
  const row = requireRecord(value, path);
  const values: Record<string, number | null> = {};
  for (const [key, item] of Object.entries(row)) {
    values[key] = item === null ? null : requireFiniteNumber(item, `${path}.${key}`);
  }
  return values;
}

export const recordingHeaderDecoder = defineResponseDecoder<RecordingHeader>("targeted.header.v1", header);
export const recordingListDecoder = defineResponseDecoder<RecordingHeader[]>(
  "targeted.list.v1", (value) => requireArray(value, "$").map(header));
export const recordingReportDecoder = defineResponseDecoder<RecordingReport>("targeted.report.v1", (value) => {
  const row = requireRecord(value);
  const summary = row.summary === null ? null : requireRecord(row.summary, "summary");
  return {
    recording: header(row.recording),
    summary: summary === null ? null : {
      intervalCount: requireNonNegativeSafeInteger(summary.intervalCount, "summary.intervalCount"),
      durationMs: requireFiniteNumber(summary.durationMs, "summary.durationMs"),
      averageFps: requireFiniteNumber(summary.averageFps, "summary.averageFps"),
      onePercentLowFps: requireFiniteNumber(summary.onePercentLowFps, "summary.onePercentLowFps"),
      pointOnePercentLowFps: requireFiniteNumber(summary.pointOnePercentLowFps, "summary.pointOnePercentLowFps"),
      p50FrameTimeMs: requireFiniteNumber(summary.p50FrameTimeMs, "summary.p50FrameTimeMs"),
      p95FrameTimeMs: requireFiniteNumber(summary.p95FrameTimeMs, "summary.p95FrameTimeMs"),
      p99FrameTimeMs: requireFiniteNumber(summary.p99FrameTimeMs, "summary.p99FrameTimeMs"),
      maxFrameTimeMs: requireFiniteNumber(summary.maxFrameTimeMs, "summary.maxFrameTimeMs")
    },
    frameIntervals: requireArray(row.frameIntervals, "frameIntervals").map((item) => {
      const frame = requireRecord(item);
      return {
        processId: requireNonNegativeSafeInteger(frame.processId, "frame.processId"),
        processStartKey: requireFiniteNumber(frame.processStartKey, "frame.processStartKey"),
        source: requireNonNegativeSafeInteger(frame.source, "frame.source"),
        swapChain: requireFiniteNumber(frame.swapChain, "frame.swapChain"),
        endedAt: requireString(frame.endedAt, "frame.endedAt"),
        durationMs: requireFiniteNumber(frame.durationMs, "frame.durationMs")
      };
    }),
    fpsDistribution: requireArray(row.fpsDistribution, "fpsDistribution").map((item) => {
      const bucket = requireRecord(item);
      return {
        fromFps: requireFiniteNumber(bucket.fromFps, "bucket.fromFps"),
        toFps: requireFiniteNumber(bucket.toFps, "bucket.toFps"),
        count: requireNonNegativeSafeInteger(bucket.count, "bucket.count")
      };
    }),
    resourceSamples: requireArray(row.resourceSamples, "resourceSamples").map((item) => {
      const sample = requireRecord(item);
      return {
        capturedAt: requireString(sample.capturedAt, "sample.capturedAt"),
        software: metricValues(sample.software, "sample.software"),
        system: metricValues(sample.system, "sample.system"),
        processes: requireArray(sample.processes, "sample.processes").map((value) => {
          const process = requireRecord(value);
          return {
            processId: requireNonNegativeSafeInteger(process.processId, "process.processId"),
            processStartKey: requireFiniteNumber(process.processStartKey, "process.processStartKey")
          };
        })
      };
    })
  };
});

type Client = Pick<RequestClient, "request">;
const error = () => uiText.targetedReport.failed;

export function listRecordings(client: Client, signal?: AbortSignal) {
  return client.request({ key: "targeted.list", url: "/api/targeted-recordings",
    fallbackError: error(), decoder: recordingListDecoder, signal });
}

export function startRecording(client: Client, softwareId: string, maximumDurationSeconds: number) {
  return client.request({ key: "targeted.start", url: "/api/targeted-recordings",
    fallbackError: error(), decoder: recordingHeaderDecoder,
    request: { method: "POST", headers: { "Content-Type": "application/json" },
      body: JSON.stringify({ softwareId, maximumDurationSeconds }) } });
}

export function stopRecording(client: Client, id: string) {
  return client.request({ key: "targeted.stop", url: `/api/targeted-recordings/${encodeURIComponent(id)}/stop`,
    fallbackError: error(), decoder: recordingHeaderDecoder, request: { method: "POST" } });
}

export function getRecording(client: Client, id: string, signal?: AbortSignal) {
  return client.request({ key: "targeted.get", url: `/api/targeted-recordings/${encodeURIComponent(id)}`,
    fallbackError: error(), decoder: recordingReportDecoder, signal, timeoutMs: 60_000 });
}

export function deleteRecording(client: Client, id: string) {
  return client.request({ key: "targeted.delete", url: `/api/targeted-recordings/${encodeURIComponent(id)}`,
    fallbackError: error(), decoder: defineResponseDecoder<null>("targeted.empty.v1", () => null),
    request: { method: "DELETE", allowEmptyResponse: true } });
}

const bytesDecoder = defineResponseDecoder<ArrayBuffer>("targeted.export.v1", (value) => {
  if (!(value instanceof ArrayBuffer)) {
    throw new ResponseDecodeError("$", "binary export");
  }
  return value;
});

export function exportRecording(client: Client, id: string, format: "json" | "csv") {
  return client.request({ key: `targeted.export.${format}`,
    url: `/api/targeted-recordings/${encodeURIComponent(id)}/export?format=${format}`,
    fallbackError: error(), decoder: bytesDecoder, timeoutMs: 60_000,
    request: { binary: true } });
}
