import { loadAppCopy } from "../../src/i18n/copy/appCopyLoader.ts";

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

  const firstTrack = page.locator(".resource-bar-track").first();
  await firstTrack.waitFor({ state: "visible", timeout: 7000 });
  const selectedSegment = page.locator(".resource-bar-track [role='option'][aria-selected='true']").first();
  assert(await selectedSegment.count() === 0, "A resource segment was selected before user interaction.");
  await firstTrack.click({ position: { x: 12, y: 12 } });
  await selectedSegment.waitFor({ state: "visible", timeout: 3000 });
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

  // 800×520 下资源表在折叠线以下；监视区按区域可见取数，先把它滚进视口。
  await page.locator('[data-frontend-visibility-surface="monitor.table"]').scrollIntoViewIfNeeded();
  const activeRow = page.locator(".resource-table-row[role='row'][tabindex='0']").first();
  await activeRow.waitFor({ state: "visible", timeout: 7000 });
  await activeRow.focus();
  // 资源表由前端按资源事实排序：夹具 80 个软件的值相同，默认按影响排序打平后按名称的界面语言排序
  // （zh-CN，区分重音、不区分大小写，数字段按数值比较），所以第 36 行不是编号 35。
  const startRowId = await page.evaluate(() => document.activeElement?.getAttribute("data-resource-row-id"));
  const nameOrder = new Intl.Collator("zh-CN", { sensitivity: "accent", numeric: true });
  const softwareRowIds = Array.from({ length: 80 }, (_, index) => ({
    id: `software:frontend-harness.software${index === 0 ? "" : `.${index}`}`,
    name: index === 0 ? "示例软件" : `示例软件 ${index}`
  })).sort((left, right) => nameOrder.compare(left.name, right.name)).map(row => row.id);
  assert(startRowId === softwareRowIds[0], `Keyboard focus did not start on the first software row: ${startRowId}`);
  const expectedRowId = softwareRowIds[35];
  for (let index = 0; index < 35; index += 1) {
    await page.keyboard.press("ArrowDown");
  }
  await page.waitForFunction(expected => document.activeElement?.getAttribute("data-resource-row-id")
    === expected, expectedRowId, { timeout: 5000 });
  const focusedRowId = await page.evaluate(() => document.activeElement?.getAttribute("data-resource-row-id"));
  assert(focusedRowId === expectedRowId,
    `Virtual row focus did not cross the mounted window: ${focusedRowId}, expected ${expectedRowId}`);

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
  // 指标名称由前端按指标 ID 本地化，不用后端目录里的标签。
  const metricLabel = (await loadAppCopy("zh-CN")).metricLabel;
  const expectedSummary = [metricLabel["cpu.usage"], "12%", metricLabel["memory.percent"], "25%"];
  await page.waitForFunction((expected) => {
    const text = document.querySelector("#resourcePerformanceSummary")?.textContent ?? "";
    return expected.every((part) => text.includes(part));
  }, expectedSummary, { timeout: 2000 });
  const summaryText = await performanceSummary.innerText();
  assert(expectedSummary.every((part) => summaryText.includes(part)),
    `Performance DOM summary does not match the metric snapshot: ${summaryText}`);
  assert(await requestCount("/api/metrics/snapshot") === metricSnapshotRequestBaseline,
    "Performance mode used a metric snapshot request instead of the subscription push.");

  // 资源表来自资源事实：每个软件行都有软件标识，“转到详细信息”总是可用，
  // 原来“无标识、无进程、全部禁用”的表格行在当前合同下不会出现。这里检查键盘打开的行菜单：
  // Shift+F10 打开菜单并把焦点放进菜单，Escape 关闭后焦点回到打开它的行。
  await setScenario({ resourceTableVariant: "normal" });
  await page.reload({ waitUntil: "domcontentloaded" });
  // 800×520 下资源表在折叠线以下；监视区按区域可见取数，先把它滚进视口。
  await page.locator('[data-frontend-visibility-surface="monitor.table"]').scrollIntoViewIfNeeded();
  const menuRow = page.locator(".resource-table-row[data-resource-row-id='software:frontend-harness.software']");
  await menuRow.waitFor({ state: "visible", timeout: 7000 });
  await menuRow.focus();
  await page.keyboard.press("Shift+F10");
  const rowMenu = page.getByRole("menu");
  await rowMenu.waitFor({ state: "visible", timeout: 3000 });
  const contextMenuText = (await loadAppCopy("zh-CN")).contextMenu;
  assert(await rowMenu.getByRole("menuitem", { name: contextMenuText.goToDetails, exact: true }).isEnabled(),
    "A software row with a stable identity did not offer its details action.");
  await page.waitForFunction(() => document.activeElement?.closest("[role='menu']") !== null, null, { timeout: 2000 });
  await page.keyboard.press("Escape");
  await rowMenu.waitFor({ state: "detached", timeout: 3000 });
  await page.waitForFunction(() => document.activeElement?.getAttribute("data-resource-row-id")
    === "software:frontend-harness.software", null, { timeout: 2000 });

  assert(failures.length === 0, `R2 browser page errors: ${JSON.stringify(failures)}`);
  return {
    initialResourceSelection: false,
    clickResourceSelection: true,
    boundedResourceInteractionDom: interactionBudget,
    virtualRowFocus: focusedRowId,
    performanceDomSummary: true,
    keyboardRowMenuReturnsFocus: true
  };
}
