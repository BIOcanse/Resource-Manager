import assert from "node:assert/strict";
import {
  getCpuBaselineRatio,
  resetCpuBaselineRatio,
  saveCpuBaselineRatio
} from "../src/data/cpu/cpuBaselineRatioApi.ts";
import { RequestClient } from "../src/frontendRuntime/request/RequestClient.ts";
import { RequestProblem } from "../src/frontendRuntime/request/RequestProblem.ts";
import { BackendSessionOwner } from "../src/frontendRuntime/session/BackendSessionOwner.ts";

const originalFetch = globalThis.fetch;
const owner = new BackendSessionOwner({
  transport: null,
  allowStandalone: true,
  standaloneEpochFactory: () => "standalone:cpu-baseline-test"
});
const client = new RequestClient(owner);

// 后端 CpuBaselineRatioSettings 的 JSON：ratio、multiplier、defaultMultiplier 是计算属性。
function settings(overrideRatio: number | null, extra: Record<string, unknown> = {}) {
  const ratio = overrideRatio ?? 0.5557;
  return {
    cpuName: "Intel(R) Core(TM) i9-12900K",
    defaultRatio: 0.5557,
    overrideRatio,
    defaultSource: "intel:measured_throughput_composition_estimate",
    matchedModel: "i9-12900K",
    datasetVersion: "2026-09-02",
    ratio,
    multiplier: 1 / ratio,
    defaultMultiplier: 1 / 0.5557,
    ...extra
  };
}

try {
  const requests: Array<{ url: string; init: RequestInit | undefined }> = [];
  let next: Response = jsonResponse(settings(null));
  globalThis.fetch = (async (input, init) => {
    requests.push({ url: String(input), init });
    return next;
  }) as typeof fetch;

  const current = await getCpuBaselineRatio(client);
  assert.equal(current?.ratio, 0.5557);
  assert.equal(current?.overrideRatio, null);
  assert.equal(current?.defaultSource, "intel:measured_throughput_composition_estimate");

  next = jsonResponse(null);
  assert.equal(await getCpuBaselineRatio(client), null, "No CPU topology yet reads as null");

  next = jsonResponse({ settings: settings(0.75), savedOverrideRatio: 0.75, runtimeApplicationDisposition: "applied" });
  const saved = await saveCpuBaselineRatio(client, 0.75);
  assert.equal(saved.settings?.ratio, 0.75);
  assert.equal(requests.at(-1)?.init?.method, "PUT");
  assert.deepEqual(JSON.parse(String(requests.at(-1)?.init?.body)), { ratio: 0.75 });

  // 409 带同一结构的结果：已保存但运行计划没有发布，是已知结果而不是失败。
  next = jsonResponse({ settings: null, savedOverrideRatio: 0.6, runtimeApplicationDisposition: "savedNotApplied" }, 409);
  const notApplied = await saveCpuBaselineRatio(client, 0.6);
  assert.equal(notApplied.runtimeApplicationDisposition, "savedNotApplied");
  assert.equal(notApplied.settings, null);

  next = jsonResponse({
    settings: settings(null), savedOverrideRatio: null, runtimeApplicationDisposition: "appliedWithDeliveryFailures"
  }, 409);
  const reset = await resetCpuBaselineRatio(client);
  assert.equal(reset.runtimeApplicationDisposition, "appliedWithDeliveryFailures");
  assert.equal(requests.at(-1)?.init?.method, "DELETE");

  // 409 体不是结果结构时仍按请求失败。
  next = jsonResponse({ error: "conflict" }, 409);
  await assert.rejects(saveCpuBaselineRatio(client, 0.6),
    (error: unknown) => error instanceof RequestProblem && error.status === 409);
  next = jsonResponse({ error: "CPU baseline ratio must be greater than zero and at most one." }, 400);
  await assert.rejects(saveCpuBaselineRatio(client, 0.6),
    (error: unknown) => error instanceof RequestProblem && error.status === 400);

  for (const invalid of [
    settings(null, { defaultSource: "intel:unknown_basis" }),
    settings(null, { defaultRatio: 0 }),
    settings(1.5),
    settings(0.75, { ratio: 0.5557 })
  ]) {
    next = jsonResponse(invalid);
    await assert.rejects(getCpuBaselineRatio(client),
      (error: unknown) => error instanceof RequestProblem && error.kind === "invalid-response"
        && error.attempt.decoderId === "cpu.baseline-ratio.v1");
  }
  next = jsonResponse({ settings: null, savedOverrideRatio: 0.75, runtimeApplicationDisposition: "applied" });
  await assert.rejects(saveCpuBaselineRatio(client, 0.75),
    (error: unknown) => error instanceof RequestProblem && error.kind === "invalid-response",
    "An applied result must carry the published settings");
} finally {
  owner.dispose();
  globalThis.fetch = originalFetch;
}

function jsonResponse(value: unknown, status = 200) {
  return new Response(JSON.stringify(value), { status, headers: { "Content-Type": "application/json" } });
}
