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
  const rowAction = page.getByRole("button", { name: "更多操作：示例软件" }).first();
  await rowAction.waitFor({ state: "visible", timeout: 7000 });
  const heat = await page.locator('.resource-table-row[aria-hidden="false"] .value-cell')
    .evaluateAll((cells) => cells.map((cell) => ({
      text: cell.textContent.trim(),
      width: cell.style.getPropertyValue("--heat"),
      privateWidth: cell.style.getPropertyValue("--private-heat"),
      background: getComputedStyle(cell).backgroundImage,
      title: cell.title
    })));
  if (heat.length !== 2 || heat.some((cell) => cell.width !== "100%")) {
    throw new Error(`Single-row numeric columns must each fill their column: ${JSON.stringify(heat)}`);
  }
  const memory = heat.find(cell => cell.title.includes("共享分摊"));
  if (memory?.privateWidth !== "75%" || !memory.background.includes("211, 171, 48") ||
      !/^512 (?:MB|MiB)$/.test(memory.text) || !/共享分摊 128(?:\.0)? (?:MB|MiB)/.test(memory.title))
    throw new Error(`Shared bytes must be a yellow quarter with exact tooltip: ${JSON.stringify(heat)}`);
  await page.locator('.resource-table-panel').screenshot({ path: 'tests/browser/artifacts/resource-table-shared.png' });
  await page.getByRole("searchbox", { name: "搜索名称、PID、状态" })
    .fill("no-such-resource");
  const filteredState = page.locator(".resource-table-panel .content-state");
  await filteredState.getByText("没有匹配的资源", { exact: true })
    .waitFor({ state: "visible", timeout: 3000 });
  await filteredState.getByRole("button", { name: "清除搜索", exact: true })
    .waitFor({ state: "visible", timeout: 2000 });
  await assertContentStateLayout(page, "filtered-empty");

  if (failures.length > 0) {
    throw new Error(`Resource-table content-state page errors: ${JSON.stringify(failures)}`);
  }
  return {
    variants: ["empty", "filtered-empty"],
    viewports,
    retainedTableHidden: true,
    maximumHeatVerified: heat,
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
  await page.waitForTimeout(50);
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
