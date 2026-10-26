import assert from "node:assert/strict";

export default async function gpuCommittedThresholdsJourney(page, baseUrl) {
  const failures = [];
  page.on("pageerror", error => failures.push(String(error)));

  await patchScenario(baseUrl, {
    optimizationRuntime: true,
    runtimeEffectOwners: true,
    gpuPressureFixture: true,
    unknownGpuScore: true,
    gpuOverflowThresholdsMissing: true
  });
  await page.goto(`${baseUrl}/`, { waitUntil: "domcontentloaded" });
  await openGpuModel(page);

  const panel = page.locator(".gpu-scheduling-panel");
  const position = panel.locator(".gpu-position");
  await position.waitFor();
  await position.getByText(/^\d+ 软件 · 未请求进程明细$/).waitFor();
  await page.waitForFunction(() => {
    const positionState = document.querySelector(".gpu-position-main em")?.textContent?.trim();
    const pressureValues = [...document.querySelectorAll(".gpu-pressure-grid .gpu-pressure-meter strong")]
      .map(element => element.textContent?.trim());
    return positionState === "暂无数据" && pressureValues[0] === "96.0%" && pressureValues[1] === "50.0%";
  });
  const missingThresholdHeader = await panel.locator(".gpu-scheduling-header span").first().textContent();
  assert.match(missingThresholdHeader ?? "", /暂无数据/);

  await patchScenario(baseUrl, {
    gpuOverflowThresholdsMissing: false,
    gpuOverflowUsagePercent: 99,
    gpuOverflowDedicatedMemoryPercent: 70
  });
  await page.reload({ waitUntil: "domcontentloaded" });
  await openGpuModel(page);
  await page.waitForFunction(() => {
    const header = document.querySelector(".gpu-scheduling-header span")?.textContent ?? "";
    const positionState = document.querySelector(".gpu-position-main em")?.textContent?.trim();
    return header.includes("GPU 99% / 显存 70%") && positionState === "可调度";
  });

  const header = await page.locator(".gpu-scheduling-header span").first().textContent();
  const status = await page.locator(".gpu-position-main em").first().textContent();
  const pressure = await page.locator(".gpu-pressure-grid .gpu-pressure-meter strong").allTextContents();
  // V5 supplies raw numeric clocks, with no backend displayValue.
  await position.getByText("频率 1500 MHz", { exact: true }).waitFor();
  await position.getByText("显存频率 7000 MHz", { exact: true }).waitFor();
  assert.match(header ?? "", /GPU 99% \/ 显存 70%/);
  assert.equal(status?.trim(), "可调度");
  assert.deepEqual(pressure.map(value => value.trim()), ["96.0%", "50.0%"]);
  let writeState = "error";
  const writes = [];
  await page.route("**/api/gpu/performance-overrides", async route => {
    const method = route.request().method();
    if (method === "GET") return route.continue();
    writes.push(method);
    if (writeState === "error") return route.fulfill({ status: 503, contentType: "text/plain", body: "fixture failure" });
    return route.fulfill({ status: 202, json: {
      policy: { scoresByGpuId: { GPU0: 321 }, updatedAt: new Date().toISOString(), storagePath: "fixture" },
      runtimeApplicationDisposition: "committedWithDeliveryFailures", startupInterceptionDisposition: "notAttempted",
      runtimePlanVersion: 7, runtimePublicationSequence: 8, runtimeDeliveryFailureCount: 1, failureCode: null
    } });
  });
  await panel.getByRole("button", { name: "编辑", exact: true }).click();
  const draft = panel.getByRole("spinbutton").first();
  await draft.fill("321");
  await panel.getByRole("button", { name: "保存", exact: true }).click();
  await panel.getByRole("alert").getByText("保存 GPU 性能分失败。", { exact: true }).waitFor();
  assert.equal(await draft.inputValue(), "321", "Failed save must preserve the draft");
  await panel.getByRole("button", { name: "重置", exact: true }).click();
  await panel.getByRole("alert").getByText("重置 GPU 性能分失败。", { exact: true }).waitFor();
  assert.equal(await draft.inputValue(), "321", "Failed reset must preserve the draft");
  assert.deepEqual(writes, ["PUT", "DELETE"]);
  writeState = "partial";
  await panel.getByRole("button", { name: "保存", exact: true }).click();
  await panel.getByText("已保存策略，但运行调度配置尚未完全应用。", { exact: true }).waitFor();
  assert.equal(await draft.inputValue(), "321", "Partial delivery must keep the draft");
  await panel.getByRole("button", { name: "重新应用", exact: true }).click();
  await panel.getByRole("button", { name: "编辑", exact: true }).waitFor();
  assert.equal(await panel.getByRole("alert").count(), 0);
  if (failures.length) throw new Error(failures.join("\n"));

  return { missingThresholds: "classification unknown; telemetry retained", committedThresholds: [99, 70], pressure,
    clocks: ["1500 MHz", "7000 MHz"] };
}

async function openGpuModel(page) {
  await page.getByRole("navigation").getByRole("button", { name: "详细信息", exact: true }).click();
  await page.getByRole("tab", { name: "GPU 调度", exact: true }).click();
  await page.locator(".gpu-scheduling-panel").waitFor();
}

async function patchScenario(baseUrl, patch) {
  const response = await fetch(`${baseUrl}/__test/scenario`, {
    method: "POST",
    headers: { "Content-Type": "application/json" },
    body: JSON.stringify(patch)
  });
  assert.equal(response.status, 200);
}
