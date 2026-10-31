import type { AppCopy } from "../../src/i18n/copy/index.ts";
import type { Locator } from "playwright-core";
import type { Page } from "playwright-core";
import { loadAppCopy } from "../../src/i18n/copy/appCopyLoader.ts";
import { formatBytes } from "../../src/presentation/byteUnits.ts";
import { waitForCondition } from "./journeyWaits.ts";
import { fullContent, helpHintText } from "./tooltipProbe.ts";

/** 表头的口径说明：排序按钮旁边的问号（同一个表头格里紧跟在排序按钮后面）。 */
const columnHeaderBasis = (sortButton: Locator) =>
  helpHintText(sortButton.locator("xpath=following-sibling::button[contains(@class, 'help-hint')][1]"));
/** 列来源各选项的口径：来源选择旁边的问号，每项一段（来源名一行，口径在下面）。 */
const sourceOptionsBasis = async (sources: Locator) =>
  (await helpHintText(sources.locator("xpath=following-sibling::button[contains(@class, 'help-hint')][1]"))).split("\n\n");

/**
 * 资源表内存列先选项目、再选来源（docs/metric_item_source_choice_2026-10-25.md，简体中文）：
 * 编辑列时选中内存芯片（点芯片文字，不点方格子），下面摆内存的“来源”，第一项是自动选择（已勾），写出后端这次实际读的字段（资源事实的 processFieldName，
 * 夹具是私有工作集），表头问号里的口径说明第一行也写出它；其后是后端进程字段目录里标了 supportsResourceFacts 的内存口径
 * （峰值提交量后端标为 false，不列）。选私有提交量并保存：资源事实只请求这个字段，表头写出口径，
 * 进程行显示私有提交量，总计行是后端给的系统提交量（不是进程之和），差额在“系统/驱动保留”行；
 * 选择存进前端布局缓存，重新载入后仍在。改回自动选择：恢复请求 memory.usage，表头和数值回到原样。
 * 再选工作集（含共享页）：目录和本帧都说它不可按进程相加，进程值照发，总计说明写明不可相加，没有残差行。
 */
export default async function resourceTableSourceChoiceJourney(page: Page, baseUrl: string) {
  const app = await loadAppCopy("zh-CN");
  const table = app.resourceTable;
  const GiB = 1024 ** 3;
  const panel = page.locator(".resource-table-panel");
  const header = (label: string) => panel.locator(".resource-table-head-cell .resource-table-sort-button").filter({ hasText: label });
  const memoryIndex = async () => {
    const labels = (await panel.locator(".resource-table-head-cell .resource-table-sort-button > span").allTextContents()).map((text) => text.trim());
    const index = labels.findIndex((label) => label.startsWith(table.columns.memory.split("（")[0]));
    if (index < 0) throw new Error(`No memory column among ${JSON.stringify(labels)}`);
    return index;
  };
  const memoryCell = async (rowLocator: Locator) => {
    const index = await memoryIndex();
    return ((await rowLocator.locator(".resource-table-cell").nth(index).textContent())!)?.trim();
  };
  const visibleRow = (kind: string) => panel.locator(`.resource-table-row.${kind}-row[aria-hidden="false"]`);
  const softwareRow = () => visibleRow("software").filter({ hasText: "示例软件" }).first();
  const residualRow = () => visibleRow("software").filter({ hasText: table.systemResidualRow("").trim() }).first();
  const waitForText = async (read: () => Promise<string | undefined>, expected: string, what: string) => {
    let last: string | undefined;
    await waitForCondition(page, async () => {
      last = await read().catch(() => undefined);
      return last === expected;
    }, { timeoutMs: 15_000, description: `${what}: expected ${JSON.stringify(expected)}` }).catch(() => {
      throw new Error(`${what}: expected ${JSON.stringify(expected)}, got ${JSON.stringify(last)}`);
    });
  };
  const lastFactsQuery = async () => {
    const telemetry: Record<string, { queries: string[] }> = await (await fetch(`${baseUrl}/__test/requests`)).json();
    const queries = telemetry["/api/resource-facts/subscribe"]?.queries ?? [];
    return new URLSearchParams(queries.at(-1) ?? "").getAll("items");
  };
  const editButton = panel.getByRole("button", { name: app.resourceTableView.editLabel, exact: true });
  const saveButton = panel.getByRole("button", { name: app.resourceTableView.saveLabel, exact: true });
  const sources = panel.getByRole("radiogroup", { name: table.columnSourceLabel(table.columns.memory), exact: true });
  const editor = panel.getByRole("group", { name: app.resourceTableView.columnEditorLabel, exact: true });
  const selectMemory = () => editor.getByRole("button", { name: table.columns.memory, exact: true }).click();

  await page.goto(baseUrl, { waitUntil: "domcontentloaded" });
  await panel.scrollIntoViewIfNeeded();
  await softwareRow().waitFor({ timeout: 15_000 });
  await header(table.columns.memory).waitFor();

  await editButton.click();
  await selectMemory();
  await sources.waitFor();
  const radios = sources.getByRole("radio");
  const labels = (await radios.allTextContents()).map((label) => label.trim());
  const automatic = app.metricPicker.sourceAutomatic(table.memoryQuantities.private_working_set);
  const expected = [automatic, table.memoryQuantities.working_set_including_shared,
    table.memoryQuantities.private_working_set, table.memoryQuantities.private_commit];
  if (JSON.stringify(labels) !== JSON.stringify(expected)) throw new Error(`Memory sources ${JSON.stringify(labels)}, expected ${JSON.stringify(expected)}`);
  if (await radios.nth(0).getAttribute("aria-checked") !== "true") throw new Error("Automatic is not the checked source by default");
  if (await columnHeaderBasis(header(table.columns.memory)) !== `${automatic}\n${app.resourceBreakdown.memoryTableBasis}`) {
    throw new Error("The automatic memory header does not name the field the backend actually reads");
  }

  // 私有提交量。
  await radios.nth(3).click();
  if (await radios.nth(3).getAttribute("aria-checked") !== "true") throw new Error("Choosing private commit did not move the check");
  await saveButton.click();
  await editButton.waitFor();
  const commitLabel = table.memoryColumnBySource(table.memoryQuantities.private_commit);
  await header(commitLabel).waitFor({ timeout: 15_000 });
  if (await columnHeaderBasis(header(commitLabel)) !== table.memorySourceBasis.private_commit) {
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
  await selectMemory();
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
  // 工作集（含共享页）不可按进程相加：进程值照发，总计说明写明不可相加，后端不发残差，也就没有残差行。
  await editButton.click();
  await selectMemory();
  await sources.getByRole("radio").nth(1).click();
  await saveButton.click();
  await editButton.waitFor();
  await waitForText(() => memoryCell(softwareRow()), formatBytes(0.625 * GiB, "memory"), "Working-set values are shown as sent");
  const sharedTotal = visibleRow("summary").first().locator(".resource-table-cell").nth(await memoryIndex());
  if (!(await fullContent(sharedTotal))?.includes(table.nonAdditiveBasis)) throw new Error("Non-additive source has no visible warning");
  if (await residualRow().count() !== 0) throw new Error("A non-additive source shows a residual row");
  await editButton.click();
  await selectMemory();
  await sources.getByRole("radio").nth(0).click();
  await saveButton.click();
  await editButton.waitFor();
  return { sources: labels, commitItems, automaticItems };
}

/**
 * 九语检查（localizationJourney 调用，调用前已在监视控制台、资源表内存列可见）：编辑列时内存方格子的读屏名、选中内存后的“来源”
 * 名称、四个选项（自动选择写出实际读的私有工作集）和私有提交量的说明按当前语言显示；选私有提交量后表头写出口径；取消编辑后表头回到原样，不保存。
 */
export async function checkResourceTableSourceText(page: Page, app: AppCopy, language: string) {
  const table = app.resourceTable;
  const panel = page.locator(".resource-table-panel");
  await panel.scrollIntoViewIfNeeded();
  await panel.getByRole("button", { name: app.resourceTableView.editLabel, exact: true }).click();
  // 选中内存芯片才摆它的来源；方格子的读屏名按当前语言写出“显示内存列”，并保持勾选。
  const editor = panel.getByRole("group", { name: app.resourceTableView.columnEditorLabel, exact: true });
  if (!await editor.getByRole("checkbox", { name: app.resourceTableView.showColumn(table.columns.memory), exact: true }).isChecked()) {
    throw new Error(`${language}: the memory column box is not named in this language or not checked`);
  }
  await editor.getByRole("button", { name: table.columns.memory, exact: true }).click();
  const sources = panel.getByRole("radiogroup", { name: table.columnSourceLabel(table.columns.memory), exact: true });
  const radios = sources.getByRole("radio");
  const expected = [app.metricPicker.sourceAutomatic(table.memoryQuantities.private_working_set), table.memoryQuantities.working_set_including_shared,
    table.memoryQuantities.private_working_set, table.memoryQuantities.private_commit];
  // The editor can render before resource-facts supplies the actual automatic source.
  await waitForCondition(page, async () => (await radios.allTextContents())[0]?.trim() === expected[0],
    { timeoutMs: 15_000, description: `${language}: resolved automatic memory source` });
  const labels = (await radios.allTextContents()).map((label) => label.trim());
  if (JSON.stringify(labels) !== JSON.stringify(expected)) throw new Error(`${language}: memory sources ${JSON.stringify(labels)}, expected ${JSON.stringify(expected)}`);
  // 各来源的口径在来源选择旁边的问号里，选项本身不带原生 title。
  const sourceBasis = await sourceOptionsBasis(sources);
  if (!sourceBasis.includes(`${expected[1]}\n${table.nonAdditiveBasis}\n${table.memorySourceBasis.working_set_including_shared}`)
    || !sourceBasis.includes(`${expected[3]}\n${table.memorySourceBasis.private_commit}`)
    || await radios.nth(1).getAttribute("title") !== null) {
    throw new Error(`${language}: memory source basis is not localized: ${JSON.stringify(sourceBasis)}`);
  }
  await radios.nth(3).click();
  const commitLabel = table.memoryColumnBySource(table.memoryQuantities.private_commit);
  const commitHeader = panel.locator(".resource-table-head-cell .resource-table-sort-button").filter({ hasText: commitLabel });
  await commitHeader.waitFor({ timeout: 15_000 });
  if (await columnHeaderBasis(commitHeader) !== table.memorySourceBasis.private_commit) throw new Error(`${language}: private-commit header basis missing`);
  await panel.getByRole("button", { name: app.resourceTableView.cancelEditLabel, exact: true }).click();
  await panel.locator(".resource-table-head-cell .resource-table-sort-button").filter({ hasText: table.columns.memory }).waitFor({ timeout: 15_000 });
  return { language, sources: labels, header: commitLabel };
}
