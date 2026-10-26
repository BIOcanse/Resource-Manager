import { loadAppCopy } from "../../src/i18n/copy/appCopyLoader.ts";
import { pointPreferences } from "../../src/preferences/frontendPreferences.ts";
import { dashboardPointValues } from "../../src/preferences/dashboardPreferences.ts";

/**
 * 资源表的行和表头的呈现（docs/resource_table_rows_2026-10-25.md）。
 * 内存列选私有提交量，差额落在“系统/驱动保留”行；夹具 12 个软件。
 */
export default async function resourceTableRowsJourney(page, baseUrl) {
  const app = await loadAppCopy("zh-CN");
  const errors = [];
  page.on("pageerror", (error) => errors.push(String(error)));
  const scenario = await page.request.post(`${baseUrl}/__test/scenario`, { data: { resourceTableVariant: "many", resourceTableRowCount: 12 } });
  if (!scenario.ok()) throw new Error(`Unable to configure resource-table fixture: ${scenario.status()}`);
  const preferences = pointPreferences();
  const dashboard = dashboardPointValues();
  delete dashboard.usesFactoryDefaults;
  for (const columns of [dashboard.resourceTableColumns, dashboard.resourceTableProcessColumns]) {
    for (const column of columns) if (column.id === "memory") column.source = "process_all_private_commit_actual";
  }
  preferences.dashboard = dashboard;
  preferences.appearance.animations = "normal";
  await page.addInitScript(([key, text]) => {
    if (location.origin !== "null" && localStorage.getItem(key) === null) localStorage.setItem(key, text);
  }, ["resource-manager:frontend-preferences:v1", JSON.stringify(preferences)]);
  await page.setViewportSize({ width: 1400, height: 900 });
  await page.goto(baseUrl, { waitUntil: "domcontentloaded" });
  const panel = page.locator(".resource-table-panel");
  const visibleRows = (kind) => panel.locator(`.resource-table-row${kind ? `.${kind}-row` : ""}[aria-hidden="false"]`);
  await visibleRows("software").first().waitFor({ timeout: 15_000 });
  await panel.evaluate((element) => element.scrollIntoView({ block: "start" }));
  const residualName = app.resourceTable.systemResidualRow("").trim();
  const residual = visibleRows("software").filter({ hasText: residualName }).first();
  await residual.waitFor({ timeout: 15_000 });
  const results = {};

  // 1. 系统/驱动保留行的状态列是“系统/驱动保留”，不是“状态未知”（它装的是归属类型，不是进程状态）。
  const statusIndex = await headerIndex(panel, "status");
  const residualStatus = (await residual.locator(".resource-table-cell").nth(statusIndex).textContent())?.trim();
  if (residualStatus !== app.softwareKind.system_residual) {
    throw new Error(`Residual row status: expected ${app.softwareKind.system_residual}, got ${residualStatus}`);
  }
  results.residualStatus = residualStatus;


  // 3. 行位复用：展开时换成进程行的行位底色直接换，不经过中间色；状态色（悬停）才有过渡。
  const expandFrames = await panel.evaluate(async () => {
    const slots = [...document.querySelectorAll(".resource-table-row")].slice(0, 16);
    const read = () => slots.map((slot) => getComputedStyle(slot).backgroundColor);
    const start = read();
    const expander = [...document.querySelectorAll('.resource-table-row.software-row[aria-hidden="false"] .resource-row-expander')]
      .find((button) => button.getAttribute("aria-expanded") === "false");
    expander.click();
    const frames = [];
    for (let frame = 0; frame < 12; frame += 1) {
      await new Promise((resolveFrame) => requestAnimationFrame(resolveFrame));
      frames.push(read());
    }
    const final = frames.at(-1);
    const intermediate = new Set();
    frames.forEach((colors) => colors.forEach((color, index) => {
      if (color !== start[index] && color !== final[index]) intermediate.add(`${index}:${color}`);
    }));
    return { changed: final.filter((color, index) => color !== start[index]).length, intermediate: [...intermediate],
      transition: getComputedStyle(slots[0]).transitionProperty };
  });
  if (expandFrames.changed === 0 || expandFrames.intermediate.length > 0 || !expandFrames.transition.includes("--row-tint-amount")
    || expandFrames.transition.includes("background")) {
    throw new Error(`Recycled row slots fade their base colour: ${JSON.stringify(expandFrames)}`);
  }
  results.expandFrames = expandFrames;

  // 4. 底色、悬停、当前行三种状态的颜色各不相同；右键菜单打开时它对着的那一行显示成当前行。
  await page.mouse.move(5, 5);
  const ordinary = panel.locator('.resource-table-row[aria-label="示例软件 3"]');
  const background = () => ordinary.evaluate((row) => getComputedStyle(row).backgroundColor);
  await page.waitForTimeout(250);
  const baseColor = await background();
  await ordinary.hover();
  await page.waitForTimeout(250);
  const hoverColor = await background();
  await ordinary.locator(".status-cell").click();
  await page.mouse.move(5, 5);
  await page.waitForTimeout(250);
  const currentColor = await background();
  if (new Set([baseColor, hoverColor, currentColor]).size !== 3) {
    throw new Error(`Row states are not distinct: ${JSON.stringify({ baseColor, hoverColor, currentColor })}`);
  }
  await panel.locator('.resource-table-row[aria-label="示例软件 4"]').click({ button: "right" });
  await page.locator(".standard-context-menu").waitFor();
  const contextTarget = await panel.locator('.resource-table-row[aria-label="示例软件 4"]')
    .evaluate((row) => ({ target: row.classList.contains("context-target"), color: getComputedStyle(row).backgroundColor }));
  await page.keyboard.press("Escape");
  await page.locator(".standard-context-menu").waitFor({ state: "detached" });
  if (!contextTarget.target || contextTarget.color === baseColor) {
    throw new Error(`The context menu's row is not shown as current: ${JSON.stringify(contextTarget)}`);
  }
  results.rowStates = { baseColor, hoverColor, currentColor, contextTarget };

  if (errors.length > 0) throw new Error(`Page errors: ${errors.join("\n")}`);
  return results;
}

async function headerIndex(panel, columnId) {
  const ids = await panel.locator(".resource-table-head-cell").evaluateAll((cells) => cells.map((cell) => cell.dataset.columnId));
  const index = ids.indexOf(columnId);
  if (index < 0) throw new Error(`No ${columnId} column among ${JSON.stringify(ids)}`);
  return index;
}
