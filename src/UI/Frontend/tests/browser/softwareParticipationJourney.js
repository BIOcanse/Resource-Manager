import assert from "node:assert/strict";

// 策略页“调度参与”：保护（只防降级，独立端点、立即保存）与三个“不参与”开关（进策略草稿）互不联动；
// 退出某个域只禁用该域的设置区，保存时三个开关原样写回，已删除的旧字段不再出现。
export default async function softwareParticipationJourney(page, baseUrl) {
  const errors = [];
  page.on("pageerror", error => errors.push(String(error)));
  const scenario = await fetch(`${baseUrl}/__test/scenario`, {
    method: "POST", headers: { "Content-Type": "application/json" },
    body: JSON.stringify({ gpuPlacementEnabled: true, runtimeEffectOwners: true })
  });
  if (!scenario.ok) throw new Error("Unable to enable GPU placement and runtime controls");
  const protectionWrites = [];
  await page.route("**/api/optimization/protection", route => route.fulfill({ json: [] }));
  await page.route("**/api/optimization/protection/software/*", async route => {
    protectionWrites.push(route.request().postDataJSON());
    await route.fulfill({ json: { softwareId: "frontend-harness.software" } });
  });
  const policyWrites = [];
  await page.route("**/api/gpu-placement/software/frontend-harness.software**", async route => {
    if (route.request().method() === "PUT") {
      const policy = route.request().postDataJSON();
      policyWrites.push(policy);
      return route.fulfill({ json: policy });
    }
    return route.continue();
  });

  await page.goto(baseUrl, { waitUntil: "domcontentloaded" });
  await page.getByRole("button", { name: "组件与软件", exact: true }).click();
  await page.getByRole("navigation", { name: "组件和软件分类" }).getByRole("button", { name: /一般应用/ }).click();
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
  for (const removed of ["enabledMode", "schedulingMode", "runtimeSchedulingMode", "explicitSelectionMode",
    "keepCpuProcessesOnMainCcd", "gpuExclusive", "absolutePerformanceModeEnabled", "cpuExclusiveLocksAffinity"]) {
    assert.equal(removed in saved, false, `Saved policy still carries removed field ${removed}`);
  }
  assert.equal(errors.length, 0, `Participation page errors: ${JSON.stringify(errors)}`);
  return {
    savedOptOuts: [saved.cpuSchedulingExcluded, saved.memorySchedulingExcluded, saved.gpuSchedulingExcluded],
    protectionWrites: protectionWrites.length,
    independent: true
  };
}
