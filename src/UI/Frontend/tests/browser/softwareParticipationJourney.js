import assert from "node:assert/strict";
import { waitForCondition } from "./journeyWaits.js";

// 策略页“调度参与”：保护（只防降级，独立端点、立即保存）与三个“不参与”开关（进策略草稿）互不联动；
// 退出某个域只禁用该域的设置区，保存时三个开关原样写回，已删除的旧字段不再出现。
export default async function softwareParticipationJourney(page, baseUrl) {
  const errors = [];
  let placementReads = 0;
  page.on("pageerror", error => errors.push(String(error)));
  page.on("request", request => {
    if (request.method() === "GET"
      && new URL(request.url()).pathname === "/api/gpu-placement/software/frontend-harness.software") placementReads++;
  });
  const scenario = await fetch(`${baseUrl}/__test/scenario`, {
    method: "POST", headers: { "Content-Type": "application/json" },
    body: JSON.stringify({ gpuPlacementEnabled: true, runtimeEffectOwners: true })
  });
  if (!scenario.ok) throw new Error("Unable to enable GPU placement and runtime controls");
  const policyPath = `${baseUrl}/api/gpu-placement/software/frontend-harness.software`;
  const initialPolicy = (await (await fetch(policyPath)).json()).softwarePolicy;
  await fetch(`${policyPath}/policy`, { method: "PUT", headers: { "Content-Type": "application/json" },
    body: JSON.stringify({ ...initialPolicy, hostMemoryProtectionNoDump: true }) });
  const protectionWrites = [];
  // 开关写入后前端重读保护列表；列表按写入的结果给出来源为软件详情的条目（后端格式）。
  let protectedNow = false;
  await page.route("**/api/optimization/protection", route => route.fulfill({ json: protectedNow ? [{
    id: "software:software:frontend-harness.software", targetType: "software", targetKey: "software:frontend-harness.software",
    softwareId: "frontend-harness.software", softwareName: "示例软件", softwareKind: "other", protectedAt: "2026-10-29T08:00:00.000Z",
    protectionSource: "software_detail", createdFromReportId: "software-detail", lastVerifiedAt: "2026-10-29T08:00:00.000Z",
    state: "active", allowsPlacementAvoidance: true, protectionLevel: 1,
    defaultProtectionLevel: null, defaultProtectionSource: null, policyGroupId: null, processName: null, executablePath: null
  }] : [] }));
  await page.route("**/api/optimization/protection/software/*", async route => {
    protectionWrites.push(route.request().postDataJSON());
    protectedNow = route.request().postDataJSON().enabled === true;
    await route.fulfill({ json: { softwareId: "frontend-harness.software" } });
  });
  const policyWrites = [];
  await page.route("**/api/gpu-placement/software/frontend-harness.software**", async route => {
    if (route.request().method() === "PUT") {
      const policy = route.request().postDataJSON();
      policyWrites.push(policy);
      return route.continue();
    }
    return route.continue();
  });

  await page.goto(baseUrl, { waitUntil: "domcontentloaded" });
  await page.getByRole("button", { name: "组件与软件", exact: true }).click();
  await page.getByRole("tablist", { name: "组件和软件分类" }).getByRole("tab", { name: /一般应用/ }).click();
  await page.locator(".software-item").filter({ hasText: "示例软件" }).getByRole("button", { name: "软件设置", exact: true }).click();
  const dialog = page.getByRole("dialog", { name: "示例软件" });
  await dialog.getByRole("tab", { name: "策略", exact: true }).click();
  await dialog.getByRole("heading", { name: "调度参与", exact: true }).waitFor();

  const protection = dialog.getByRole("group", { name: "保护", exact: true }).getByRole("checkbox", { name: "启用保护", exact: true });
  const cpu = dialog.getByRole("checkbox", { name: "不参与 CPU 调度", exact: true });
  const memory = dialog.getByRole("checkbox", { name: "不参与内存调度", exact: true });
  const gpu = dialog.getByRole("checkbox", { name: "不参与 GPU 调度", exact: true });
  const cpuRange = dialog.getByRole("combobox", { name: "CPU 使用范围", exact: true });
  const startupGpu = dialog.getByRole("combobox", { name: "启动时 GPU", exact: true });
  await page.waitForFunction(() => !document.querySelector(".software-protection-section input")?.disabled);
  for (const toggle of [protection, cpu, memory, gpu]) {
    assert.equal(await toggle.isChecked(), false);
  }
  assert.equal(await cpuRange.isEnabled(), true);
  assert.equal(await startupGpu.isEnabled(), true);

  await cpu.check();
  assert.equal(await cpuRange.isEnabled(), false, "CPU settings must be disabled when CPU scheduling is excluded");
  await dialog.getByText("已设置为不参与 CPU 调度；下面的设置会保留但不执行。", { exact: true }).waitFor();
  assert.equal(await startupGpu.isEnabled(), true, "Excluding CPU must not disable GPU settings");
  assert.equal(await memory.isChecked(), false);
  assert.equal(await gpu.isChecked(), false);

  const readsBeforeHidden = placementReads;
  await setVisible(false);
  await waitTelemetry(t => t["/api/cpu/topology/subscribe"]?.logicalInFlight === 0);
  // Observe all 150 ms after hiding: topology retirement alone cannot establish
  // that the independent placement-policy reader stays released throughout it.
  await page.waitForTimeout(150);
  assert.equal(placementReads, readsBeforeHidden, "Hidden detail must release its reads");
  const resumedPolicy = page.waitForResponse(response => response.request().method() === "GET"
    && new URL(response.url()).pathname === "/api/gpu-placement/software/frontend-harness.software");
  await setVisible(true);
  await waitTelemetry(() => placementReads === readsBeforeHidden + 1);
  assert.equal((await resumedPolicy).status(), 200);
  await dialog.getByText("正在加载策略...", { exact: true }).waitFor({ state: "hidden" });
  assert.equal(await cpu.isChecked(), true, "Resuming the current policy must preserve an unsaved draft");
  await waitTelemetry(t => t["/api/cpu/topology/subscribe"]?.logicalInFlight === 1);

  await gpu.check();
  assert.equal(await startupGpu.isEnabled(), false, "GPU settings must be disabled when GPU scheduling is excluded");
  await dialog.getByText("已设置为不参与 GPU 调度；下面的设置会保留但不执行。", { exact: true }).waitFor();

  assert.equal(await protection.isChecked(), false, "Opt-outs must not drive the protection switch");
  assert.equal(protectionWrites.length, 0, "Opt-outs must not write protection");
  await protection.check();
  await page.waitForFunction(() => !document.querySelector(".software-protection-section input")?.disabled);
  assert.deepEqual(protectionWrites, [{ enabled: true }]);
  assert.deepEqual([await cpu.isChecked(), await memory.isChecked(), await gpu.isChecked()], [true, false, true],
    "Protection must not change the opt-out switches");

  const completedSave = page.waitForResponse(response => response.request().method() === "PUT"
    && response.url().endsWith("/policy"));
  await dialog.getByRole("button", { name: "保存", exact: true }).click();
  assert.equal((await completedSave).status(), 200);
  assert.equal(policyWrites.length, 1);
  const saved = policyWrites[0];
  assert.equal(saved.cpuSchedulingExcluded, true);
  assert.equal(saved.memorySchedulingExcluded, false);
  assert.equal(saved.gpuSchedulingExcluded, true);
  assert.equal(saved.hostMemoryProtectionNoDump, true, "Saving unrelated scheduling choices preserves the no-dump flag");
  assert.equal((await (await fetch(policyPath)).json()).softwarePolicy.hostMemoryProtectionNoDump, true);
  for (const removed of ["enabledMode", "schedulingMode", "runtimeSchedulingMode", "explicitSelectionMode",
    "keepCpuProcessesOnMainCcd", "gpuExclusive", "absolutePerformanceModeEnabled", "cpuExclusiveLocksAffinity"]) {
    assert.equal(removed in saved, false, `Saved policy still carries removed field ${removed}`);
  }
  assert.equal(errors.length, 0, `Participation page errors: ${JSON.stringify(errors)}`);
  return {
    savedOptOuts: [saved.cpuSchedulingExcluded, saved.memorySchedulingExcluded, saved.gpuSchedulingExcluded],
    protectionWrites: protectionWrites.length,
    independent: true,
    hiddenReadsReleasedAndCurrentPolicyReread: true
  };

  async function setVisible(value) {
    await page.evaluate(value => {
      Object.defineProperty(document, "visibilityState", { configurable: true, value: value ? "visible" : "hidden" });
      document.dispatchEvent(new Event("visibilitychange"));
    }, value);
  }
  async function waitTelemetry(predicate) {
    await waitForCondition(page, async () => {
      const telemetry = await (await fetch(`${baseUrl}/__test/requests`)).json();
      return predicate(telemetry);
    }, { timeoutMs: 10_000, description: "software detail reads reach expected state" });
  }
}
