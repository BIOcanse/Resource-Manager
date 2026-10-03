import assert from "node:assert/strict";
import {
  deleteRecording, exportRecording, getRecording, listRecordings,
  recordingReportDecoder, startRecording, stopRecording
} from "../src/features/targetedReport/targetedRecordingApi.ts";
import type { RequestClient } from "../src/frontendRuntime/request/RequestClient.ts";

const header = {
  id: "recording-1", softwareId: "game-1", softwareName: "Game",
  startedAt: "2026-10-02T00:00:00Z", endedAt: null,
  maximumDurationSeconds: 600, status: "recording", stopReason: null, incomplete: false
};
const report = {
  recording: header,
  summary: {
    intervalCount: 1, durationMs: 10, averageFps: 100,
    onePercentLowFps: 100, pointOnePercentLowFps: 100,
    p50FrameTimeMs: 10, p95FrameTimeMs: 10, p99FrameTimeMs: 10, maxFrameTimeMs: 10
  },
  frameIntervals: [{ processId: 12, processStartKey: 1234, source: 0,
    swapChain: 1, endedAt: "2026-10-02T00:00:01Z", durationMs: 10 }],
  fpsDistribution: [{ fromFps: 100, toFps: 110, count: 1 }],
  resourceSamples: [{ capturedAt: "2026-10-02T00:00:01Z",
    software: { "cpu.usage": 40, "gpu.0.vram": null },
    system: { "cpu.temperature": 55 },
    processes: [{ processId: 12, processStartKey: 1234 }] }]
};

assert.equal(recordingReportDecoder.decode(report).resourceSamples[0].software["gpu.0.vram"], null);
assert.throws(() => recordingReportDecoder.decode({ ...report, summary: { ...report.summary, averageFps: "100" } }));

const calls: Array<{ key: string; url: string; method: string; body?: string; binary?: boolean }> = [];
const client = {
  request: async (descriptor: {
    key: string; url: string; decoder: { decode: (value: unknown) => unknown };
    request?: { method?: string; body?: string; binary?: boolean }
  }) => {
    calls.push({ key: descriptor.key, url: descriptor.url,
      method: descriptor.request?.method ?? "GET", body: descriptor.request?.body,
      binary: descriptor.request?.binary });
    const value = descriptor.key === "targeted.list" ? [header]
      : descriptor.key === "targeted.get" ? report
      : descriptor.key === "targeted.delete" ? null
      : descriptor.key.startsWith("targeted.export") ? new ArrayBuffer(4)
      : header;
    return descriptor.decoder.decode(value);
  }
} as unknown as Pick<RequestClient, "request">;

assert.equal((await listRecordings(client))[0].id, header.id);
assert.equal((await startRecording(client, "game-1", 600)).maximumDurationSeconds, 600);
assert.equal((await stopRecording(client, header.id)).id, header.id);
assert.equal((await getRecording(client, header.id)).summary?.averageFps, 100);
await deleteRecording(client, header.id);
assert.equal((await exportRecording(client, header.id, "csv")).byteLength, 4);

assert.deepEqual(calls.map((call) => [call.method, call.url]), [
  ["GET", "/api/targeted-recordings"],
  ["POST", "/api/targeted-recordings"],
  ["POST", "/api/targeted-recordings/recording-1/stop"],
  ["GET", "/api/targeted-recordings/recording-1"],
  ["DELETE", "/api/targeted-recordings/recording-1"],
  ["GET", "/api/targeted-recordings/recording-1/export?format=csv"]
]);
assert.deepEqual(JSON.parse(calls[1].body ?? ""), { softwareId: "game-1", maximumDurationSeconds: 600 });
assert.equal(calls[5].binary, true);
console.log("Targeted recording API routes, request shapes and report decoding passed.");
