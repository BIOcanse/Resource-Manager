import assert from "node:assert/strict";
import {
  getPerformanceOverlaySettings, listEnabledPerformanceOverlays, OverlaySettingsSaveError, resetPerformanceOverlayPresentation,
  savePerformanceOverlaySettings
} from "../src/api.ts";
import { AppSettingsRevisionConflictError } from "../src/data/appSettings/appSettingsApi.ts";
import { RequestClient } from "../src/frontendRuntime/request/RequestClient.ts";
import { BackendSessionOwner } from "../src/frontendRuntime/session/BackendSessionOwner.ts";
import type { PerformanceOverlayEditingConstraints, PerformanceOverlayEntry, PerformanceOverlayItem } from "../src/types.ts";

/*
 * 浮窗分页整份读写后端设置（docs/frontend_block_trees_2026-10-26.md「浮窗设置归后端」）：
 * 读 GET /api/performance-overlay/software/{id}，写 PATCH /api/settings/app 只带这个软件的整条，恢复默认只带开关和方式；
 * 启用且注入时先登记两个浮层启动模块。前端不存浮窗设置。
 */
const item = (kind: PerformanceOverlayItem["kind"], id: string): PerformanceOverlayItem =>
  ({ kind, id, source: "auto", intervalMs: null, syncPrecision: "mid" });
const defaults: PerformanceOverlayEntry = {
  enabled: false, mode: "external",
  // Deliberately different from the product build's defaults: a frontend copy must not satisfy this fixture.
  items: [item("software", "memory.usage")],
  sizeMode: "windowRatio", fontSizePx: 31, regionWidthRatio: 0.42, regionHeightRatio: 0.37, anchor: "bottomLeft", marginPx: 23
};
const overlayModules = ["performance-overlay-preload", "performance-overlay-vulkan"];
const editingConstraints: PerformanceOverlayEditingConstraints = {
  maximumItems: 3, fontSizePxMinimum: 9, fontSizePxMaximum: 57, marginPxMinimum: 2, marginPxMaximum: 97,
  regionRatioMinimum: 0.1, regionRatioMaximum: 0.85, regionRatioStep: 0.025
};

type Call = { url: string; method: string; body: unknown };
const calls: Call[] = [];
let backendEntries: Record<string, PerformanceOverlayEntry> = {};
let revision = 1;
let moduleIds: string[] = ["other-module"];
let conflictNextPatch = false;
let nextPatchReply: { status: number; disposition?: string; settings?: Record<string, unknown> } | undefined;
let omitOverlaySection = false;

const settingsResult = (disposition?: string) => ({
  revision: `r${revision}`,
  settings: { version: "1", ...(omitOverlaySection ? {} : { performanceOverlay: { software: backendEntries } }) },
  ...(disposition ? { runtimeApplicationDisposition: disposition } : {})
});
const json = (value: unknown, status = 200) =>
  new Response(JSON.stringify(value), { status, headers: { "Content-Type": "application/json" } });

const originalFetch = globalThis.fetch;
globalThis.fetch = (async (input, init) => {
  const url = String(input);
  const method = init?.method ?? "GET";
  const body = typeof init?.body === "string" ? JSON.parse(init.body) : undefined;
  calls.push({ url, method, body });
  if (url === "/api/performance-overlay/software/game-1" && method === "GET") {
    const stored = backendEntries["game-1"];
    return json({ softwareId: "game-1", stored: stored !== undefined, revision: `r${revision}`, settings: stored ?? defaults, editingConstraints });
  }
  if (url.startsWith("/api/startup-modules/software/") && method === "GET")
    return json({ softwareId: decodeURIComponent(url.split("/").at(-1)!), moduleIds });
  if (url.startsWith("/api/startup-modules/software/") && method === "PUT") {
    moduleIds = (body as { moduleIds: string[] }).moduleIds;
    return json(body);
  }
  if (url === "/api/settings/app" && method === "GET") return json(settingsResult());
  if (url === "/api/settings/app" && method === "PATCH") {
    if (nextPatchReply) {
      const reply = nextPatchReply;
      nextPatchReply = undefined;
      return json({ ...settingsResult(reply.disposition), ...(reply.settings ? { settings: { version: "1", ...reply.settings } } : {}) }, reply.status);
    }
    const request = body as { expectedRevision: string; changes: { performanceOverlay: { software: Record<string, Partial<PerformanceOverlayEntry>> } } };
    if (conflictNextPatch || request.expectedRevision !== `r${revision}`) {
      conflictNextPatch = false;
      return json(settingsResult("revisionConflict"), 409);
    }
    // 后端：整条替换，省略的字段展开成默认值。
    for (const [id, entry] of Object.entries(request.changes.performanceOverlay.software))
      backendEntries = { ...backendEntries, [id]: { ...defaults, ...entry } };
    revision += 1;
    return json(settingsResult("committedAndApplied"));
  }
  return json({ error: "not-found" }, 404);
}) as typeof fetch;

try {
  const client = new RequestClient(new BackendSessionOwner({ transport: null, allowStandalone: true }));

  // 没有条目：后端的默认条目（关），启动模块没有浮层模块。
  const first = await getPerformanceOverlaySettings(client, "game-1");
  assert.deepEqual(first, { softwareId: "game-1", revision: "r1", stored: false, injectionModules: "none", ...defaults, editingConstraints });
  assert.equal(Object.isFrozen(first.editingConstraints), true);

  // 启用注入：先把两个浮层模块登记上（其他模块保留），再只写这个软件的整条。
  calls.length = 0;
  const enabled = await savePerformanceOverlaySettings(client, { ...first, enabled: true, mode: "injected", fontSizePx: 20 }, first);
  assert.deepEqual(calls.map((call) => `${call.method} ${call.url}`), [
    "GET /api/startup-modules/software/game-1", "PUT /api/startup-modules/software/game-1", "PATCH /api/settings/app"]);
  assert.deepEqual(moduleIds, ["other-module", ...overlayModules]);
  assert.deepEqual(calls[2]!.body, { expectedRevision: "r1", changes: { performanceOverlay: { software: {
    "game-1": { ...defaults, enabled: true, mode: "injected", fontSizePx: 20 } } } } });
  assert.deepEqual(enabled, { softwareId: "game-1", revision: "r2", stored: true, injectionModules: "complete",
    ...defaults, enabled: true, mode: "injected", fontSizePx: 20, editingConstraints });

  // 只改外观：启动模块已经一致，不再读写，只发 PATCH。
  calls.length = 0;
  const moved = await savePerformanceOverlaySettings(client, { ...enabled, anchor: "bottomRight" }, enabled);
  assert.deepEqual(calls.map((call) => `${call.method} ${call.url}`), ["PATCH /api/settings/app"]);
  assert.equal(moved.anchor, "bottomRight");
  assert.equal(moved.revision, "r3");

  // 恢复默认外观：只带开关和方式，其余由后端展开成默认值。
  calls.length = 0;
  const reset = await resetPerformanceOverlayPresentation(client, { ...moved, fontSizePx: 30 });
  assert.deepEqual(calls[0]!.body, { expectedRevision: "r3", changes: { performanceOverlay: { software: {
    "game-1": { enabled: true, mode: "injected" } } } } });
  assert.deepEqual(reset, { ...defaults, softwareId: "game-1", revision: "r4", stored: true, injectionModules: "complete",
    enabled: true, mode: "injected", editingConstraints });

  // 关闭时启动模块已经撤下，但设置在这期间被改过（修订号冲突）：报“模块已更新、浮窗设置未保存”，原因是冲突。
  conflictNextPatch = true;
  await assert.rejects(savePerformanceOverlaySettings(client, { ...reset, enabled: false }, reset), (error: unknown) =>
    error instanceof OverlaySettingsSaveError && error.cause instanceof AppSettingsRevisionConflictError);
  assert.deepEqual(moduleIds, ["other-module"]);
  // 外观的冲突没有动启动模块，原样抛出冲突。
  conflictNextPatch = true;
  await assert.rejects(savePerformanceOverlaySettings(client, { ...reset, marginPx: 0 }, { ...reset, injectionModules: "complete" }),
    AppSettingsRevisionConflictError);

  // 不合规的项不发请求。
  calls.length = 0;
  await assert.rejects(savePerformanceOverlaySettings(client, { ...reset, items: [item("metric", "x"), item("metric", "X")] }, reset),
    /overlay-items-invalid: duplicate/);
  assert.equal(calls.length, 0);
  await assert.rejects(savePerformanceOverlaySettings(client, { ...reset,
    items: Array.from({ length: editingConstraints.maximumItems + 1 }, (_, index) => item("metric", `m.${index}`)) }, reset),
    /overlay-items-invalid: too-many/);
  assert.equal(calls.length, 0, "the backend's nondefault limit applies before module or settings writes");

  // 设置里的总览：只列启用的软件，带各自的启动模块登记。
  backendEntries = { ...backendEntries, "Other-Game": { ...defaults, enabled: false } };
  const overview = await listEnabledPerformanceOverlays(client, new AbortController().signal);
  assert.deepEqual(overview.map((entry) => [entry.softwareId, entry.enabled, entry.injectionModules]), [["game-1", true, "none"]]);

  // A 200 status does not turn a conflicting or uncommitted write into a successful save. Never retry the stale draft.
  for (const disposition of ["revisionConflict", "rejectedBeforeCommit", "notRequested", undefined]) {
    calls.length = 0;
    nextPatchReply = { status: 200, disposition };
    await assert.rejects(resetPerformanceOverlayPresentation(client, reset), disposition === "revisionConflict"
      ? (error: unknown) => error instanceof AppSettingsRevisionConflictError
      : (error: unknown) => error instanceof Error);
    assert.deepEqual(calls.map(call => call.method), ["PATCH"]);
  }
  // HTTP rejection with a pre-commit validation reason remains a transport problem, never a revision conflict.
  nextPatchReply = { status: 409, disposition: "rejectedBeforeCommit" };
  await assert.rejects(resetPerformanceOverlayPresentation(client, reset),
    (error: unknown) => error instanceof Error && !(error instanceof AppSettingsRevisionConflictError));
  // All durable partial-application dispositions retain saved values, including pending runtime publication.
  for (const disposition of ["committedWithCapabilityConstraints", "committedWithDeliveryFailures", "savedNotApplied"]) {
    nextPatchReply = { status: 200, disposition };
    assert.equal((await resetPerformanceOverlayPresentation(client, reset)).revision, `r${revision}`);
  }
  // A generic commit from an older backend that ignored the section cannot be shown as a saved overlay.
  nextPatchReply = { status: 200, disposition: "committedAndApplied", settings: {} };
  await assert.rejects(resetPerformanceOverlayPresentation(client, reset), /performance-overlay-entry-missing/);
  omitOverlaySection = true;
  calls.length = 0;
  await assert.rejects(listEnabledPerformanceOverlays(client, new AbortController().signal));
  assert.deepEqual(calls.map(call => `${call.method} ${call.url}`), ["GET /api/settings/app"]);
} finally {
  globalThis.fetch = originalFetch;
}

console.log("Overlay settings API: backend read, PATCH bodies, module sync, reset and overview passed.");
