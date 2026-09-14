export default async function r2RemediationJourney(page, baseUrl = "http://127.0.0.1:4177") {
  const failures = [];
  page.on("pageerror", (error) => failures.push(String(error)));

  const assert = (condition, message) => {
    if (!condition) {
      throw new Error(message);
    }
  };
  const setScenario = async (patch) => {
    const response = await page.request.post(`${baseUrl}/__test/scenario`, { data: patch });
    assert(response.ok(), `Unable to set R2 scenario: ${response.status()}`);
  };
  const requestCount = async (path) => {
    const response = await page.request.get(`${baseUrl}/__test/requests`);
    assert(response.ok(), `Unable to read R2 request telemetry: ${response.status()}`);
    return (await response.json())[path]?.completed ?? 0;
  };

  await page.setViewportSize({ width: 800, height: 520 });
  await setScenario({ capabilities: "ready", settings: "ready", resourceTableVariant: "many" });
  await page.goto(`${baseUrl}/`, { waitUntil: "domcontentloaded" });

  const selectedSegment = page.locator(".resource-bar-track [role='option'][aria-selected='true']").first();
  await selectedSegment.waitFor({ state: "visible", timeout: 7000 });
  const interactionBudget = await page.evaluate(() => ({
    tracks: document.querySelectorAll(".resource-bar-track, .resource-process-track:not(.empty)").length,
    layers: document.querySelectorAll(".resource-segment-interaction-layer").length,
    options: document.querySelectorAll(".resource-bar-track [role='option'], .resource-process-track [role='option']").length,
    tooltips: document.querySelectorAll(".resource-bar-track .resource-tooltip, .resource-process-track .resource-tooltip").length
  }));
  assert(interactionBudget.layers <= interactionBudget.tracks,
    `Resource interaction layers exceeded the per-track budget: ${JSON.stringify(interactionBudget)}`);
  assert(interactionBudget.options <= interactionBudget.tracks,
    `Resource options exceeded the per-track budget: ${JSON.stringify(interactionBudget)}`);
  assert(interactionBudget.tooltips <= interactionBudget.tracks,
    `Resource tooltips exceeded the per-track budget: ${JSON.stringify(interactionBudget)}`);

  const activeRow = page.locator(".resource-table-row[role='row'][tabindex='0']").first();
  await activeRow.waitFor({ state: "visible", timeout: 7000 });
  await activeRow.focus();
  for (let index = 0; index < 35; index += 1) {
    await page.keyboard.press("ArrowDown");
  }
  await page.waitForFunction(() => document.activeElement?.getAttribute("data-resource-row-id")
    === "software:frontend-harness.software.35", null, { timeout: 5000 });
  const focusedRowId = await page.evaluate(() => document.activeElement?.getAttribute("data-resource-row-id"));
  assert(focusedRowId === "software:frontend-harness.software.35",
    `Virtual row focus did not cross the mounted window: ${focusedRowId}`);

  const resourceTablePanel = page.locator(".resource-table-panel");
  const performanceMode = resourceTablePanel.getByRole("radio", { name: "性能", exact: true });
  const metricSnapshotRequestBaseline = await requestCount("/api/metrics/snapshot");
  await performanceMode.focus();
  await page.keyboard.press("Enter");
  const selectedMode = await resourceTablePanel.locator("[role='radio'][aria-checked='true']")
    .textContent().catch(() => null);
  if (selectedMode?.trim() !== "性能") {
    const diagnostic = await page.evaluate(() => ({
      radios: Array.from(document.querySelectorAll(".resource-table-panel [role='radio']")).map((element) => ({
        text: element.textContent?.trim(),
        checked: element.getAttribute("aria-checked"),
        disabled: element.hasAttribute("disabled")
      })),
      boundaries: Array.from(document.querySelectorAll(".page-boundary-fallback"))
        .map((element) => element.textContent?.trim()),
      bodyPage: document.body.dataset.page
    }));
    throw new Error(`Performance mode did not activate: ${JSON.stringify(diagnostic)}; pageErrors=${JSON.stringify(failures)}`);
  }
  const performanceSummary = resourceTablePanel.locator("#resourcePerformanceSummary");
  await performanceSummary.waitFor({ state: "visible", timeout: 4000 });
  await page.waitForFunction(async () => {
    const response = await fetch("/__test/requests");
    const telemetry = await response.json();
    const publications = telemetry["/api/metrics/subscribe"]?.publicationsByQuery ?? {};
    return Object.entries(publications).some(([query, count]) =>
      Number(count) > 0
      && query.includes("ids=cpu.usage")
      && query.includes("ids=memory.percent"));
  }, null, { timeout: 4000 });
  await page.waitForFunction(() => {
    const summary = document.querySelector("#resourcePerformanceSummary");
    const text = summary?.textContent ?? "";
    return text.includes("处理器")
      && text.includes("12%")
      && text.includes("内存占用")
      && text.includes("25%");
  }, null, { timeout: 2000 });
  const summaryText = await performanceSummary.innerText();
  assert(summaryText.includes("处理器") && summaryText.includes("12%")
    && summaryText.includes("内存占用") && summaryText.includes("25%"),
  `Performance DOM summary does not match the metric snapshot: ${summaryText}`);
  assert(await requestCount("/api/metrics/snapshot") === metricSnapshotRequestBaseline,
    "Performance mode used a metric snapshot request instead of the subscription push.");

  await setScenario({ resourceTableVariant: "unavailable-actions" });
  await page.reload({ waitUntil: "domcontentloaded" });
  const unavailableRow = page.locator(".resource-table-row[data-resource-row-id='software:unavailable-actions']");
  await unavailableRow.waitFor({ state: "visible", timeout: 7000 });
  await unavailableRow.focus();
  await page.keyboard.press("Shift+F10");
  const unavailableToast = page.locator(".toast").filter({ hasText: "当前没有可执行的操作" });
  await unavailableToast.waitFor({ state: "visible", timeout: 3000 });
  assert(await page.getByRole("menu").count() === 0, "An all-disabled context menu was mounted.");
  assert(await unavailableRow.evaluate((element) => document.activeElement === element),
    "All-disabled context-menu handling did not preserve opener focus.");

  assert(failures.length === 0, `R2 browser page errors: ${JSON.stringify(failures)}`);
  return {
    initialResourceSelection: true,
    boundedResourceInteractionDom: interactionBudget,
    virtualRowFocus: focusedRowId,
    performanceDomSummary: true,
    disabledMenuRejected: true
  };
}
