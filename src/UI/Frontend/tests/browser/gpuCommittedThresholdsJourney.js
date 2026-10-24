import assert from "node:assert/strict";

export default async function gpuCommittedThresholdsJourney(page, baseUrl) {
  const failures = [];
  page.on("pageerror", error => failures.push(String(error)));

  await patchScenario(baseUrl, {
    gpuPressureFixture: true,
    unknownGpuScore: true,
    gpuOverflowThresholdsMissing: true
  });
  await page.goto(`${baseUrl}/`, { waitUntil: "domcontentloaded" });
  await openGpuModel(page);

  const panel = page.locator(".gpu-scheduling-panel");
  const position = panel.locator(".gpu-position");
  await position.waitFor();
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
  assert.match(header ?? "", /GPU 99% \/ 显存 70%/);
  assert.equal(status?.trim(), "可调度");
  assert.deepEqual(pressure.map(value => value.trim()), ["96.0%", "50.0%"]);
  if (failures.length) throw new Error(failures.join("\n"));

  return { missingThresholds: "classification unknown; telemetry retained", committedThresholds: [99, 70], pressure };
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
