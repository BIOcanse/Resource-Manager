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

  if (errors.length > 0) throw new Error(`Page errors: ${errors.join("\n")}`);
  return results;
}

async function headerIndex(panel, columnId) {
  const ids = await panel.locator(".resource-table-head-cell").evaluateAll((cells) => cells.map((cell) => cell.dataset.columnId));
  const index = ids.indexOf(columnId);
  if (index < 0) throw new Error(`No ${columnId} column among ${JSON.stringify(ids)}`);
  return index;
}
