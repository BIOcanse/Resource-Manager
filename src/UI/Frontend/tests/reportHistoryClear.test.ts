import assert from "node:assert/strict";
import { readdirSync, readFileSync } from "node:fs";
import {
  clearReportHistory,
  reportHistoryClearPaths,
  reportHistoryKinds
} from "../src/data/reportHistory/reportHistoryClear.ts";
import { RequestClient } from "../src/frontendRuntime/request/RequestClient.ts";
import { RequestProblem } from "../src/frontendRuntime/request/RequestProblem.ts";
import { BackendSessionOwner } from "../src/frontendRuntime/session/BackendSessionOwner.ts";

/*
 * 清空历史（docs/report_lifecycle_2026-10-28.md）：每类报告一个 POST 接口，回复 { "removedCount": <整数> }。
 * 路径照合同；回复只认非负整数的 removedCount，其他形状按不合合同的回复拒绝。
 */
// 后端源码合同：每个清空接口在后端注册为 POST，并且在访问策略里是 Session 范围（docs/report_lifecycle_2026-10-28.md“访问范围”）。
// 智能调度历史的清空接口由控制会话的后端分支提供，内存保护记录的由后端分支 codex/notification-chain 提供，各自和它同一批合入时加进来。
const endpointsDirectory = new URL("../../../Core/Endpoints/", import.meta.url);
const endpointSources = readdirSync(endpointsDirectory).filter((name) => name.endsWith(".cs"))
  .map((name) => readFileSync(new URL(name, endpointsDirectory), "utf8")).join("\n");
const policy = readFileSync(new URL("../../../Core/Infrastructure/Security/TrustedAccessHttpPolicy.cs", import.meta.url), "utf8");
const sessionGroup = /Add\(Protected\(AccessKeyScopes\.Session\),([^;]*);/.exec(policy)?.[1] ?? "";
for (const kind of ["optimizationReports", "kernelLeaks", "targetedRecordings"] as const) {
  const path = reportHistoryClearPaths[kind];
  assert(endpointSources.includes(`app.MapPost("${path}"`), `${kind}: the backend maps POST ${path}`);
  assert(sessionGroup.includes(`"POST ${path}"`), `${kind}: POST ${path} is in the Session scope group`);
}

const originalFetch = globalThis.fetch;
const owner = new BackendSessionOwner({
  transport: null,
  allowStandalone: true,
  standaloneEpochFactory: () => "standalone:report-history-clear-test"
});
const client = new RequestClient(owner);

try {
  assert.deepEqual(reportHistoryClearPaths, {
    optimizationReports: "/api/optimization/reports/history/clear",
    kernelLeaks: "/api/kernel-leaks/history/clear",
    targetedRecordings: "/api/targeted-recordings/history/clear",
    smartRecords: "/api/optimization/smart/records/history/clear",
    memoryProtection: "/api/memory-protection/records/history/clear"
  });

  const requests: Array<{ url: string; init?: RequestInit }> = [];
  let reply: unknown = { removedCount: 3 };
  globalThis.fetch = (async (input, init) => {
    requests.push({ url: String(input), init });
    return new Response(JSON.stringify(reply), { status: 200, headers: { "Content-Type": "application/json" } });
  }) as typeof fetch;

  for (const kind of reportHistoryKinds) {
    assert.equal(await clearReportHistory(client, kind), 3, `${kind}: removedCount`);
  }
  assert.deepEqual(requests.map((request) => request.url), reportHistoryKinds.map((kind) => reportHistoryClearPaths[kind]));
  assert(requests.every((request) => request.init?.method === "POST"), "every clear is a POST");

  reply = { removedCount: 0 };
  assert.equal(await clearReportHistory(client, "kernelLeaks"), 0, "nothing to remove is a valid reply");

  for (const invalid of [{}, { removedCount: -1 }, { removedCount: 1.5 }, { removedCount: "2" }, null, []]) {
    reply = invalid;
    await assert.rejects(
      clearReportHistory(client, "targetedRecordings"),
      (error: unknown) => error instanceof RequestProblem
        && error.kind === "invalid-response"
        && error.attempt.decoderId === "report-history.clear.v1",
      `rejects ${JSON.stringify(invalid)}`);
  }
} finally {
  owner.dispose();
  globalThis.fetch = originalFetch;
}
