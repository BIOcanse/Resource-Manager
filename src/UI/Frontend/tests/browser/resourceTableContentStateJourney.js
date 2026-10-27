import { resolve } from "node:path";
import { waitForStableRendering } from "./journeyWaits.js";
const viewports = [
  { width: 800, height: 500 },
  { width: 1067, height: 667 }
];

export default async function resourceTableContentStateJourney(
  page,
  baseUrl = "http://127.0.0.1:4177"
) {
  const failures = [];
  page.on("pageerror", (error) => failures.push(String(error)));

  for (const scenario of [
    {
      variant: "empty",
      title: "当前没有可显示的资源",
      actions: []
    }
  ]) {
    await setScenario(page, baseUrl, { resourceTableVariant: scenario.variant });
    await page.setViewportSize(viewports[0]);
    await page.goto(`${baseUrl}/`, { waitUntil: "domcontentloaded" });
    await page.locator(".resource-table-panel").scrollIntoViewIfNeeded();
    const contentState = page.locator(".resource-table-panel .content-state");
    await contentState.getByText(scenario.title, { exact: true })
      .waitFor({ state: "visible", timeout: 7000 });
    for (const action of scenario.actions) {
      await contentState.getByRole("button", { name: action, exact: true })
        .waitFor({ state: "visible", timeout: 2000 });
    }
    await assertContentStateLayout(page, scenario.variant);
    await page.setViewportSize(viewports[1]);
    await assertContentStateLayout(page, `${scenario.variant}-wide`);
  }

  await setScenario(page, baseUrl, { resourceTableVariant: "normal" });
  await page.setViewportSize(viewports[0]);
  await page.goto(`${baseUrl}/`, { waitUntil: "domcontentloaded" });
  await page.locator(".resource-table-panel").scrollIntoViewIfNeeded();
  const rowAction = page.getByRole("button", { name: "更多操作：示例软件" }).first();
  await rowAction.waitFor({ state: "visible", timeout: 7000 });
  const heat = await page.locator('.resource-table-row.software-row[aria-hidden="false"] .value-cell')
    .evaluateAll((cells) => cells.map((cell) => ({
      text: cell.textContent.trim(),
      width: cell.style.getPropertyValue("--heat"),
      privateWidth: cell.style.getPropertyValue("--private-heat"),
      background: getComputedStyle(cell).backgroundImage,
      title: cell.title
    })));
  // 默认软件列来自前端布局点（preferences.points.json 的 softwareColumns）：无 GPU 时数值列为 CPU、内存、磁盘、网络。
  // CreateNetworkThroughputBar 与磁盘条一样发布 B/s；共享内存的 75% 私有段仍单独检查。
  const numericTexts = ["12.0%", "512 MiB", "12 B/s", "12 B/s"];
  if (heat.length !== numericTexts.length || heat.some((cell) => cell.width !== "100%")
      || heat.some((cell, index) => !(index === 1 ? /^512 (?:MB|MiB)$/.test(cell.text) : cell.text === numericTexts[index]))) {
    throw new Error(`Single-row numeric columns must each fill their column: ${JSON.stringify(heat)}`);
  }
  const memory = heat.find(cell => cell.title.includes("共享分摊"));
  if (memory?.privateWidth !== "75%" || !memory.background.includes("211, 171, 48") ||
      !/^512 (?:MB|MiB)$/.test(memory.text) || !/共享分摊 128(?:\.0)? (?:MB|MiB)/.test(memory.title))
    throw new Error(`Shared bytes must be a yellow quarter with exact tooltip: ${JSON.stringify(heat)}`);
  const summary = await page.locator('.resource-table-row.summary-row[aria-hidden="false"] .value-cell').allTextContents();
  if (summary.length !== numericTexts.length || !summary[0].includes("12.0%") || !/^512 (?:MB|MiB)$/.test(summary[1])
      || summary[2] !== numericTexts[2] || summary[3] !== numericTexts[3]) {
    throw new Error(`Frontend summary did not retain authoritative totals: ${JSON.stringify(summary)}`);
  }
  // A short result needs no empty row slots: those still construct every cell,
  // subscribe reactive getters and participate in style/layout on page mount.
  const rowPool = await page.locator(".resource-table-frame").evaluate(frame => ({
    slots: frame.querySelectorAll(".resource-table-row").length,
    rows: Number(frame.getAttribute("aria-rowcount")) - 1
  }));
  if (rowPool.slots > rowPool.rows) {
    throw new Error(`Resource table mounted slots without data rows: ${JSON.stringify(rowPool)}`);
  }
  const nameBindings = await page.locator('#pageStage .resource-table-row[aria-hidden="false"] .resource-row-name')
    .evaluateAll(names => names.map(name => ({ text: name.textContent, title: name.getAttribute("title"),
      accessible: name.closest('[role="row"]')?.getAttribute("aria-label") })));
  if (nameBindings.length === 0 || nameBindings.some(name => name.text !== name.title || name.text !== name.accessible)) {
    throw new Error(`Resource row name text, tooltip and accessibility disagree: ${JSON.stringify(nameBindings)}`);
  }
  await page.locator('.resource-table-panel').screenshot({ path: resolve(process.env.RM_BROWSER_ARTIFACT_ROOT ?? 'tests/browser/artifacts', 'resource-table-shared.png') });
  await setScenario(page, baseUrl, { resourceTableRowCount: 2 });
  await page.getByRole("button", { name: "更多操作：示例软件 1", exact: true }).waitFor();
  await page.getByRole("searchbox", { name: "搜索名称、PID、状态" }).fill("示例软件 1");
  await page.locator('.resource-table-row[aria-hidden="false"]').filter({ hasText: "筛选隐藏项" }).waitFor({ timeout: 3000 });
  await page.getByRole("searchbox", { name: "搜索名称、PID、状态" })
    .fill("no-such-resource");
  await assertContentStateLayout(page, "filtered-empty");
  await page.setViewportSize(viewports[1]);
  await assertContentStateLayout(page, "filtered-empty-wide");
  if (await page.locator('.resource-table-row.summary-row[aria-hidden="false"]').count() !== 1) throw new Error("Search hid the authoritative total");

  if (failures.length > 0) {
    throw new Error(`Resource-table content-state page errors: ${JSON.stringify(failures)}`);
  }
  return {
    variants: ["empty", "filtered-accounted", "filtered-empty"],
    viewports,
    retainedTableHidden: true,
    maximumHeatVerified: heat,
    rowPool,
    nameBindings,
    pageErrors: failures
  };
}

async function setScenario(page, baseUrl, patch) {
  const result = await page.request.post(`${baseUrl}/__test/scenario`, { data: patch });
  if (!result.ok()) {
    throw new Error(`Unable to set content-state scenario: ${result.status()}`);
  }
}

async function assertContentStateLayout(page, label) {
  await page.locator(".resource-table-panel .content-state").waitFor({ state: "visible", timeout: 3000 });
  await waitForStableRendering(page, [".resource-table-panel", ".resource-table-panel .content-state"], { timeoutMs: 3000 });
  const state = await page.evaluate(() => {
    const panel = document.querySelector(".resource-table-panel");
    const content = panel?.querySelector(".content-state");
    const panelRect = panel?.getBoundingClientRect();
    const contentRect = content?.getBoundingClientRect();
    return {
      retainedSurfaceCount: panel?.querySelectorAll(".resource-table-retained-surface").length ?? -1,
      retainedSurfaceHidden: panel?.querySelector(".resource-table-retained-surface")?.hidden ?? false,
      visibleTableFrameCount: Array.from(panel?.querySelectorAll(".resource-table-frame") ?? [])
        .filter((element) => element.getClientRects().length > 0).length,
      visibleViewportCount: Array.from(panel?.querySelectorAll(".resource-table-viewport") ?? [])
        .filter((element) => element.getClientRects().length > 0).length,
      visibleSpacerCount: Array.from(panel?.querySelectorAll(".resource-table-spacer") ?? [])
        .filter((element) => element.getClientRects().length > 0).length,
      contentVisible: contentRect
        ? contentRect.width > 0 && contentRect.height > 0
        : false,
      contained: panelRect && contentRect
        ? contentRect.left >= panelRect.left - 0.5
          && contentRect.right <= panelRect.right + 0.5
          && contentRect.top >= panelRect.top - 0.5
          && contentRect.bottom <= panelRect.bottom + 0.5
        : false,
      documentOverflow: document.documentElement.scrollWidth > document.documentElement.clientWidth
    };
  });
  if (state.retainedSurfaceCount !== 1
    || !state.retainedSurfaceHidden
    || state.visibleTableFrameCount !== 0
    || state.visibleViewportCount !== 0
    || state.visibleSpacerCount !== 0) {
    throw new Error(`${label} retained a virtual-scroll surface: ${JSON.stringify(state)}`);
  }
  if (!state.contentVisible || !state.contained || state.documentOverflow) {
    throw new Error(`${label} content-state layout is unusable: ${JSON.stringify(state)}`);
  }
}
