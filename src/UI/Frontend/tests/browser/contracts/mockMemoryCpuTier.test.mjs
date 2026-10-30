import assert from "node:assert/strict";
import { before, after, beforeEach, test } from "node:test";
import { startMockServer } from "./mockServerFixture.mjs";
import { memoryCpuTierReadingDecoder } from "../../../src/data/memoryCpuTier/memoryCpuTierDecoder.ts";
import { ResponseDecodeError } from "../../../src/frontendRuntime/request/ResponseDecoder.ts";

// 模拟后端的 GET /api/settings/memory-cpu-tier 和 PATCH /api/settings/app 的 memoryCpuTier（docs/memory_cpu_tiers_2026-10-30.md §5）：
// 回复经前端真实解码器；保存后下一次读取反映新档位和单独设置，清除就是补丁里少了那个键。
let server;
before(async () => { server = await startMockServer(); });
after(async () => { await server?.close(); });
beforeEach(async () => {
  const response = await fetch(`${server.baseUrl}/__test/scenario/reset`, { method: "POST" });
  assert.equal(response.status, 200);
});
const request = (method, body) => ({ method, headers: { "Content-Type": "application/json" }, body: JSON.stringify(body) });
const readTier = async () => {
  const { status, value } = await server.json("/api/settings/memory-cpu-tier");
  assert.equal(status, 200);
  return memoryCpuTierReadingDecoder.decode(value);
};
const saveTier = async memoryCpuTier => {
  const initial = (await server.json("/api/settings/app")).value;
  return server.json("/api/settings/app", request("PATCH", { expectedRevision: initial.revision, changes: { memoryCpuTier } }));
};
const option = (reading, name) => reading.options.find(item => item.name === name);

test("default read decodes with every unit kind and a pending restart option", async () => {
  const reading = await readTier();
  assert.equal(reading.tier, 3);
  assert.deepEqual(reading.options.map(item => item.unit), ["bytes", "level", null, "seconds", "milliseconds", "count"]);
  assert.equal(option(reading, "gcGen0MaxBudget").range.unset, true);
  assert.equal(option(reading, "ledgerMemoryModeFloor").range.kind, "choice");
  const budget = option(reading, "gcGen0MaxBudget");
  assert.equal(budget.apply, "restart");
  assert.equal(budget.value, 4194304);
  assert.equal(budget.runningValue, 2097152);
  assert.equal(budget.pendingRestart, true);
  const conserve = option(reading, "gcConserveMemory");
  assert.equal(conserve.apply, "restart");
  assert.equal(conserve.pendingRestart, false, "the running value equals the tier 3 value");
  assert.ok(reading.options.every(item => item.source === "tier"));
  const settings = (await server.json("/api/settings/app")).value.settings;
  assert.deepEqual(settings.memoryCpuTier, { tier: 3, overrides: {} });
});

test("a saved tier and override are reflected by the next read, and clearing removes the override", async () => {
  const saved = await saveTier({ tier: 5, overrides: { browserRuntimeSnapshotSeconds: 45 } });
  assert.equal(saved.status, 200);
  assert.deepEqual(saved.value.settings.memoryCpuTier, { tier: 5, overrides: { browserRuntimeSnapshotSeconds: 45 } });
  let reading = await readTier();
  assert.equal(reading.tier, 5);
  assert.equal(option(reading, "browserRuntimeSnapshotSeconds").value, 45);
  assert.equal(option(reading, "browserRuntimeSnapshotSeconds").source, "user");
  assert.equal(option(reading, "gcGen0MaxBudget").value, null);
  assert.equal(option(reading, "gcGen0MaxBudget").category, "aggressive");
  assert.equal(option(reading, "ledgerMemoryModeFloor").value, "unrestricted");
  const cleared = await saveTier({ tier: 5, overrides: {} });
  assert.equal(cleared.status, 200);
  assert.deepEqual(cleared.value.settings.memoryCpuTier, { tier: 5, overrides: {} }, "the whole section is replaced");
  reading = await readTier();
  assert.equal(option(reading, "browserRuntimeSnapshotSeconds").value, 300);
  assert.equal(option(reading, "browserRuntimeSnapshotSeconds").source, "tier");
});

test("invalid tier settings are rejected before commit", async () => {
  for (const [memoryCpuTier, path] of [
    [{ tier: 6, overrides: {} }, "$.memoryCpuTier.tier"],
    [{ tier: 3, overrides: { browserRuntimeSnapshotSeconds: 601 } }, "$.memoryCpuTier.overrides.browserRuntimeSnapshotSeconds"],
    [{ tier: 3, overrides: { browserRuntimeSnapshotSeconds: null } }, "$.memoryCpuTier.overrides.browserRuntimeSnapshotSeconds"],
    [{ tier: 3, overrides: { ledgerMemoryModeFloor: "never" } }, "$.memoryCpuTier.overrides.ledgerMemoryModeFloor"],
    [{ tier: 3, overrides: { futureTradeOff: 1 } }, "$.memoryCpuTier.overrides.futureTradeOff"]
  ]) {
    const { status, value } = await saveTier(memoryCpuTier);
    assert.equal(status, 409);
    assert.equal(value.runtimeFailureCode, "invalid-settings-value-type");
    assert.equal(value.runtimeFailurePath, path);
  }
  assert.equal((await readTier()).tier, 3);
  const unsetBudget = await saveTier({ tier: 3, overrides: { gcGen0MaxBudget: null } });
  assert.equal(unsetBudget.status, 200, "null is a valid override when the range allows unset");
  assert.equal(option(await readTier(), "gcGen0MaxBudget").value, null);
});

test("scenario variants: unknown option, malformed reply and read failure", async () => {
  await server.scenario({ memoryCpuTier: "unknown-option" });
  assert.equal((await readTier()).options.at(-1).name, "futureTradeOff");
  await server.scenario({ memoryCpuTier: "malformed" });
  const malformed = await server.json("/api/settings/memory-cpu-tier");
  assert.equal(malformed.status, 200);
  assert.throws(() => memoryCpuTierReadingDecoder.decode(malformed.value), ResponseDecodeError);
  await server.scenario({ memoryCpuTier: "error" });
  const failed = await fetch(`${server.baseUrl}/api/settings/memory-cpu-tier`);
  assert.equal(failed.status, 503);
  await failed.text();
});
