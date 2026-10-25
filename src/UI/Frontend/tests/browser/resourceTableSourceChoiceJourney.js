import { loadAppCopy } from "../../src/i18n/copy/appCopyLoader.ts";
import { formatBytes } from "../../src/presentation/byteUnits.ts";

/**
 * 资源表内存列先选项目、再选来源（docs/metric_item_source_choice_2026-10-25.md，简体中文）：
 * 编辑列时内存列下面摆“来源”，第一项是自动选择（已勾），其后是后端进程字段目录里的内存口径
 * （峰值提交量不进资源事实，不列）。选私有提交量并保存：资源事实只请求这个字段，表头写出口径，
 * 进程行显示私有提交量，总计行是后端给的系统提交量（不是进程之和），差额在“系统/驱动保留”行；
 * 选择存进前端布局缓存，重新载入后仍在。改回自动选择：恢复请求 memory.usage，表头和数值回到原样。
 */
export default async function resourceTableSourceChoiceJourney(page, baseUrl) {
  const app = await loadAppCopy("zh-CN");
  const table = app.resourceTable;
  const GiB = 1024 ** 3;
  const panel = page.locator(".resource-table-panel");
  const header = (label) => panel.locator(".resource-table-head-cell .resource-table-sort-button").filter({ hasText: label });
  const memoryIndex = async () => {
    const labels = (await panel.locator(".resource-table-head-cell .resource-table-sort-button > span").allTextContents()).map((text) => text.trim());
    const index = labels.findIndex((label) => label.startsWith(table.columns.memory.split("（")[0]));
    if (index < 0) throw new Error(`No memory column among ${JSON.stringify(labels)}`);
    return index;
  };
  const memoryCell = async (rowLocator) => {
    const index = await memoryIndex();
    return (await rowLocator.locator(".resource-table-cell").nth(index).textContent())?.trim();
  };
  const visibleRow = (kind) => panel.locator(`.resource-table-row.${kind}-row[aria-hidden="false"]`);
  const softwareRow = () => visibleRow("software").filter({ hasText: "示例软件" }).first();
  const residualRow = () => visibleRow("software").filter({ hasText: table.systemResidualRow("").trim() }).first();
  const waitForText = async (read, expected, what) => {
    const deadline = Date.now() + 15_000;
    let last;
    while (Date.now() < deadline) {
      last = await read().catch(() => undefined);
      if (last === expected) return;
      await page.waitForTimeout(200);
    }
    throw new Error(`${what}: expected ${JSON.stringify(expected)}, got ${JSON.stringify(last)}`);
  };
  const lastFactsQuery = async () => {
    const telemetry = await (await fetch(`${baseUrl}/__test/requests`)).json();
    const queries = telemetry["/api/resource-facts/subscribe"]?.queries ?? [];
    return new URLSearchParams(queries.at(-1) ?? "").getAll("items");
  };
  const editButton = panel.getByRole("button", { name: app.resourceTableView.editLabel, exact: true });
  const saveButton = panel.getByRole("button", { name: app.resourceTableView.saveLabel, exact: true });
  const sources = panel.getByRole("radiogroup", { name: table.columnSourceLabel(table.columns.memory), exact: true });

  await page.goto(baseUrl, { waitUntil: "domcontentloaded" });
  await panel.scrollIntoViewIfNeeded();
  await softwareRow().waitFor({ timeout: 15_000 });
  await header(table.columns.memory).waitFor();

  await editButton.click();
  await sources.waitFor();
  const radios = sources.getByRole("radio");
  const labels = (await radios.allTextContents()).map((label) => label.trim());
  const expected = [table.sourceAutomatic, table.memoryQuantities.working_set_including_shared,
    table.memoryQuantities.private_working_set, table.memoryQuantities.private_commit];
  if (JSON.stringify(labels) !== JSON.stringify(expected)) throw new Error(`Memory sources ${JSON.stringify(labels)}, expected ${JSON.stringify(expected)}`);
  if (await radios.nth(0).getAttribute("aria-checked") !== "true") throw new Error("Automatic is not the checked source by default");

  // 私有提交量。
  await radios.nth(3).click();
  if (await radios.nth(3).getAttribute("aria-checked") !== "true") throw new Error("Choosing private commit did not move the check");
  await saveButton.click();
  await editButton.waitFor();
  const commitLabel = table.memoryColumnBySource(table.memoryQuantities.private_commit);
  await header(commitLabel).waitFor({ timeout: 15_000 });
  if (await header(commitLabel).getAttribute("title") !== table.memorySourceBasis.private_commit) {
    throw new Error("The private-commit header does not state its basis");
  }
  await waitForText(() => memoryCell(softwareRow()), formatBytes(GiB, "memory"), "Process private commit");
  await waitForText(() => memoryCell(visibleRow("summary").first()), formatBytes(6 * GiB, "memory"), "Total row is the system commit charge");
  await waitForText(() => memoryCell(residualRow()), formatBytes(5 * GiB, "memory"), "System/driver residual is the difference");
  const commitItems = await lastFactsQuery();
  if (!commitItems.includes("process_all_private_commit_actual") || commitItems.includes("memory.usage")) {
    throw new Error(`Resource facts did not request only the chosen field: ${JSON.stringify(commitItems)}`);
  }

  // 选择存在前端布局缓存里。
  await page.reload({ waitUntil: "domcontentloaded" });
  await panel.scrollIntoViewIfNeeded();
  await header(commitLabel).waitFor({ timeout: 15_000 });
  await waitForText(() => memoryCell(visibleRow("summary").first()), formatBytes(6 * GiB, "memory"), "Saved choice after reload");

  // 自动选择恢复后端的选择。
  await editButton.click();
  await sources.waitFor();
  await sources.getByRole("radio").nth(0).click();
  await saveButton.click();
  await editButton.waitFor();
  await header(table.columns.memory).waitFor({ timeout: 15_000 });
  await waitForText(() => memoryCell(softwareRow()), formatBytes(536870912, "memory"), "Automatic process value");
  await waitForText(() => memoryCell(visibleRow("summary").first()), formatBytes(536870912, "memory"), "Automatic total");
  if (await residualRow().count() !== 0) throw new Error("The private-commit residual row stayed after returning to automatic");
  const automaticItems = await lastFactsQuery();
  if (!automaticItems.includes("memory.usage") || automaticItems.includes("process_all_private_commit_actual")) {
    throw new Error(`Automatic did not request memory.usage again: ${JSON.stringify(automaticItems)}`);
  }
  return { sources: labels, commitItems, automaticItems };
}

/**
 * 九语检查（localizationJourney 调用，调用前已在监视控制台、资源表内存列可见）：编辑列时内存列的“来源”
 * 名称、四个选项和私有提交量的说明按当前语言显示；选私有提交量后表头写出口径；取消编辑后表头回到原样，不保存。
 */
export async function checkResourceTableSourceText(page, app, language) {
  const table = app.resourceTable;
  const panel = page.locator(".resource-table-panel");
  await panel.scrollIntoViewIfNeeded();
  await panel.getByRole("button", { name: app.resourceTableView.editLabel, exact: true }).click();
  const sources = panel.getByRole("radiogroup", { name: table.columnSourceLabel(table.columns.memory), exact: true });
  await sources.waitFor({ timeout: 15_000 });
  const radios = sources.getByRole("radio");
  const labels = (await radios.allTextContents()).map((label) => label.trim());
  const expected = [table.sourceAutomatic, table.memoryQuantities.working_set_including_shared,
    table.memoryQuantities.private_working_set, table.memoryQuantities.private_commit];
  if (JSON.stringify(labels) !== JSON.stringify(expected)) throw new Error(`${language}: memory sources ${JSON.stringify(labels)}, expected ${JSON.stringify(expected)}`);
  if (await radios.nth(1).getAttribute("title") !== table.memorySourceBasis.working_set_including_shared
    || await radios.nth(3).getAttribute("title") !== table.memorySourceBasis.private_commit) {
    throw new Error(`${language}: memory source basis is not localized`);
  }
  await radios.nth(3).click();
  const commitLabel = table.memoryColumnBySource(table.memoryQuantities.private_commit);
  const commitHeader = panel.locator(".resource-table-head-cell .resource-table-sort-button").filter({ hasText: commitLabel });
  await commitHeader.waitFor({ timeout: 15_000 });
  if (await commitHeader.getAttribute("title") !== table.memorySourceBasis.private_commit) throw new Error(`${language}: private-commit header basis missing`);
  await panel.getByRole("button", { name: app.resourceTableView.cancelEditLabel, exact: true }).click();
  await panel.locator(".resource-table-head-cell .resource-table-sort-button").filter({ hasText: table.columns.memory }).waitFor({ timeout: 15_000 });
  return { language, sources: labels, header: commitLabel };
}
