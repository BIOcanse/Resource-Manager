import assert from "node:assert/strict";
import {
  createProfile, deleteProfile, deleteRecording, exportFileName, exportFormats, exportRecording, getItemCatalog,
  getRecording, getRecordingTargets, itemCatalogDecoder, listProfiles, listRecordings, patchProfile,
  profileListDecoder, recordingErrorKey, recordingErrorText, recordingFailure, recordingHeaderDecoder,
  recordingReportDecoder, recordingTargetsDecoder, startRecording, stopRecording
} from "../src/features/targetedReport/targetedRecordingApi.ts";
import { RequestProblem } from "../src/frontendRuntime/request/RequestProblem.ts";
import type { RequestClient } from "../src/frontendRuntime/request/RequestClient.ts";
import { uiText } from "../src/text.ts";

// 旧报告头：没有配置、录制项、进程和存储字段。
const header = {
  id: "recording-1", softwareId: "game-1", softwareName: "Game",
  startedAt: "2026-10-02T00:00:00Z", endedAt: null,
  maximumDurationSeconds: 600, status: "recording", stopReason: null, incomplete: false
};
// 新报告头：后端按配置录的报告。
const newHeader = {
  ...header,
  status: "completed", endedAt: "2026-10-02T00:01:00Z", stopReason: "user-stop",
  profileId: "default", profileName: "默认",
  items: [
    { kind: "frames", id: "frames.present", unit: "ms", selectedSource: "auto", actualSources: ["etw"],
      selectedIntervalMs: null, intervalOrigin: "per-present", actualIntervalMs: null, syncPrecision: "mid",
      pointCount: 3, gaps: [] },
    { kind: "metric", id: "cpu.temperature", unit: "°C", selectedSource: "auto", actualSources: ["cpu.temperature@cpu0"],
      selectedIntervalMs: 500, intervalOrigin: "item", actualIntervalMs: 500, syncPrecision: "high",
      pointCount: 2, gaps: [{ fromMs: 0, toMs: 1000, reason: "value-unavailable" }] }
  ],
  processes: [{ processId: 12, processStartKey: 1234, name: "game.exe", firstSeenAt: "2026-10-02T00:00:00Z", exitedAt: null }],
  storedBytes: 2048,
  maximumReportKib: 4096
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
const profile = {
  id: "default", name: "默认", defaultIntervalMs: 1000,
  items: [{ kind: "software", id: "cpu.usage", source: "auto", intervalMs: null, syncPrecision: "mid" }]
};

// ---------- 解码 ----------
assert.equal(recordingReportDecoder.decode(report).resourceSamples[0].software["gpu.0.vram"], null);
assert.throws(() => recordingReportDecoder.decode({ ...report, summary: { ...report.summary, averageFps: "100" } }));

// 旧报告头：新字段保持缺省，不补默认值。
const oldDecoded = recordingHeaderDecoder.decode(header);
assert.equal("items" in oldDecoded, false);
assert.equal("profileId" in oldDecoded, false);
assert.equal(oldDecoded.storedBytes, undefined);

// 新报告头：逐字段严格解码。
const newDecoded = recordingHeaderDecoder.decode(newHeader);
assert.equal(newDecoded.profileName, "默认");
assert.equal(newDecoded.items?.length, 2);
assert.equal(newDecoded.items?.[1].gaps[0].reason, "value-unavailable");
assert.equal(newDecoded.items?.[0].selectedIntervalMs, null);
assert.equal(newDecoded.processes?.[0].exitedAt, null);
assert.equal(newDecoded.storedBytes, 2048);
assert.equal(newDecoded.maximumReportKib, 4096);
assert.equal(recordingHeaderDecoder.decode({ ...newHeader, profileId: null, profileName: null }).profileName, null);
assert.throws(() => recordingHeaderDecoder.decode({ ...newHeader, items: [{ ...newHeader.items[0], kind: "disk" }] }));
assert.throws(() => recordingHeaderDecoder.decode({ ...newHeader, items: [{ ...newHeader.items[0], syncPrecision: "max" }] }));
assert.throws(() => recordingHeaderDecoder.decode({ ...newHeader, items: [{ ...newHeader.items[0], pointCount: -1 }] }));
assert.throws(() => recordingHeaderDecoder.decode({ ...newHeader, storedBytes: "2048" }));
assert.throws(() => recordingHeaderDecoder.decode({ ...newHeader, processes: {} }));
assert.equal(recordingReportDecoder.decode({ ...report, recording: newHeader }).recording.items?.length, 2);

assert.deepEqual(recordingTargetsDecoder.decode([{ softwareId: "game-1", name: "Game", processCount: 3 }]),
  [{ softwareId: "game-1", name: "Game", processCount: 3 }]);
assert.throws(() => recordingTargetsDecoder.decode([{ softwareId: "", name: "Game", processCount: 3 }]));
assert.throws(() => recordingTargetsDecoder.decode([{ softwareId: "game-1", name: "Game", processCount: 1.5 }]));

assert.equal(itemCatalogDecoder.decode([
  { kind: "metric", id: "cpu.temperature", unit: "°C", sources: ["cpu.temperature@cpu0"], naturalIntervalMs: 2000 }
])[0].naturalIntervalMs, 2000);
assert.throws(() => itemCatalogDecoder.decode([{ kind: "metric", id: "x", unit: "", sources: "auto", naturalIntervalMs: null }]));

assert.equal(profileListDecoder.decode([profile])[0].items[0].intervalMs, null);
assert.throws(() => profileListDecoder.decode([{ ...profile, items: [{ ...profile.items[0], syncPrecision: "none" }] }]));
assert.throws(() => profileListDecoder.decode([{ ...profile, defaultIntervalMs: null }]));

// ---------- 错误体 ----------
const problem = (status: number, payload: unknown) => new RequestProblem("BACKEND_TEXT", {
  kind: "http", status, payload, retryable: false,
  attempt: { id: "request-1", sequence: 1, key: "targeted.start", decoderId: "targeted.header.v2", method: "POST",
    backendEpoch: null, startedAt: 0, finishedAt: 1, durationMs: 1 }
});
const errors = uiText.targetedReport.errors;
assert.deepEqual(recordingFailure(problem(409, { error: "target-not-running" })),
  { status: 409, code: "target-not-running", message: "BACKEND_TEXT" });
assert.equal(recordingErrorText(recordingFailure(problem(409, { error: "target-not-running" })), "start"), errors.targetNotRunning);
assert.equal(recordingErrorText(recordingFailure(problem(400, { error: "report-size-limit-exceeded" })), "start"),
  errors.reportSizeLimitExceeded);
assert.equal(recordingErrorText(recordingFailure(problem(409, { error: "targeted-recording-last-profile" })), "profile"),
  errors.lastProfile);
assert.equal(recordingErrorText(recordingFailure(problem(400, { error: "targeted-recording-interval-out-of-range" })), "profile"),
  errors.intervalOutOfRange);
assert.equal(recordingErrorText(recordingFailure(problem(400, { error: "something-new" })), "profile"),
  errors.unknown("something-new"));
// 没有代码的旧式空响应：按动作和状态说明。
assert.equal(recordingErrorText(recordingFailure(problem(409, null)), "start"), uiText.targetedReport.oneAtATime);
assert.equal(recordingErrorText(recordingFailure(problem(404, null)), "start"), errors.softwareNotFound);
assert.equal(recordingErrorText(recordingFailure(problem(400, null)), "start"), errors.invalidRequest);
assert.equal(recordingErrorText(recordingFailure(problem(500, null)), "other"), uiText.targetedReport.failed);
assert.equal(recordingErrorText(recordingFailure(new Error("network down")), "other"), "network down");
assert.equal(recordingErrorKey("settings-revision-conflict"), "settingsConflict");
assert.equal(recordingErrorKey("unknown-code"), null);

// ---------- 路由和请求形状 ----------
const calls: Array<{ key: string; url: string; method: string; body?: string; binary?: boolean }> = [];
const client = {
  request: async (descriptor: {
    key: string; url: string; decoder: { decode: (value: unknown) => unknown };
    request?: { method?: string; body?: string; binary?: boolean }
  }) => {
    calls.push({ key: descriptor.key, url: descriptor.url,
      method: descriptor.request?.method ?? "GET", body: descriptor.request?.body,
      binary: descriptor.request?.binary });
    const value = descriptor.key === "targeted.targets" ? [{ softwareId: "game-1", name: "Game", processCount: 2 }]
      : descriptor.key === "targeted.items.catalog" ? []
      : descriptor.key === "targeted.profiles" ? [profile]
      : descriptor.key === "targeted.profiles.create" || descriptor.key === "targeted.profiles.patch" ? profile
      : descriptor.key === "targeted.list" ? [header]
      : descriptor.key === "targeted.get" ? report
      : descriptor.key === "targeted.delete" || descriptor.key === "targeted.profiles.delete" ? null
      : descriptor.key.startsWith("targeted.export") ? new ArrayBuffer(4)
      : header;
    return descriptor.decoder.decode(value);
  }
} as unknown as Pick<RequestClient, "request">;

assert.equal((await listRecordings(client))[0].id, header.id);
assert.equal((await startRecording(client,
  { softwareId: "game-1", profileId: "default", maximumDurationSeconds: 600 })).maximumDurationSeconds, 600);
assert.equal((await stopRecording(client, header.id)).id, header.id);
assert.equal((await getRecording(client, header.id)).summary?.averageFps, 100);
await deleteRecording(client, header.id);
assert.equal((await exportRecording(client, header.id, "csv")).byteLength, 4);
assert.equal((await getRecordingTargets(client))[0].processCount, 2);
assert.deepEqual(await getItemCatalog(client), []);
assert.equal((await listProfiles(client))[0].id, "default");
await createProfile(client, { name: "Copy", copyFromId: "default" });
await createProfile(client, { name: "Empty", defaultIntervalMs: 1000, items: [] });
await patchProfile(client, "my profile", { name: "Renamed" });
await deleteProfile(client, "my profile");
for (const format of exportFormats) await exportRecording(client, header.id, format);

assert.deepEqual(calls.slice(0, 13).map((call) => [call.method, call.url]), [
  ["GET", "/api/targeted-recordings"],
  ["POST", "/api/targeted-recordings"],
  ["POST", "/api/targeted-recordings/recording-1/stop"],
  ["GET", "/api/targeted-recordings/recording-1"],
  ["DELETE", "/api/targeted-recordings/recording-1"],
  ["GET", "/api/targeted-recordings/recording-1/export?format=csv"],
  ["GET", "/api/targeted-recordings/targets"],
  ["GET", "/api/targeted-recordings/items/catalog"],
  ["GET", "/api/targeted-recordings/profiles"],
  ["POST", "/api/targeted-recordings/profiles"],
  ["POST", "/api/targeted-recordings/profiles"],
  ["PATCH", "/api/targeted-recordings/profiles/my%20profile"],
  ["DELETE", "/api/targeted-recordings/profiles/my%20profile"]
]);
assert.deepEqual(JSON.parse(calls[1].body ?? ""), { softwareId: "game-1", profileId: "default", maximumDurationSeconds: 600 });
assert.deepEqual(JSON.parse(calls[9].body ?? ""), { name: "Copy", copyFromId: "default" });
assert.deepEqual(JSON.parse(calls[10].body ?? ""), { name: "Empty", defaultIntervalMs: 1000, items: [] });
assert.deepEqual(JSON.parse(calls[11].body ?? ""), { name: "Renamed" });
assert.equal(calls[5].binary, true);
assert.deepEqual(calls.slice(13).map((call) => call.url.split("format=")[1]),
  ["presentmon", "long-csv", "frames-csv", "json", "csv"]);
assert.deepEqual(exportFormats.map((format) => exportFileName("r1", format)), [
  "targeted-recording-r1-presentmon-1.6.csv", "targeted-recording-r1-long.csv", "targeted-recording-r1-frames.csv",
  "targeted-recording-r1.json", "targeted-recording-r1.csv"
]);
console.log("Targeted recording API routes, request shapes, header and profile decoding, and error texts passed.");
