import { loadAppCopy } from "../../src/i18n/copy/appCopyLoader.ts";
import { formatBytes } from "../../src/presentation/byteUnits.ts";

/**
 * 资源条先选项目、再选来源（docs/metric_item_source_choice_2026-10-25.md，简体中文）：
 * 物理内存资源条在“自动选择”时，条头说明的第一行写出后端这次实际读的字段（夹具：私有工作集）。
 * 编辑资源占用条目时，物理内存下面摆“来源”：第一项自动选择（已勾，写出私有工作集），其后是后端进程字段目录里
 * 标了 supportsResourceFacts 的内存口径（峰值提交量不列）。选私有提交量并保存：订阅只把这个字段作显式口径
 * （measures=），不再按 ids 请求 memory.usage；条头写出口径和说明，数值是后端给的系统提交量；缩放只剩容量；
 * 选择存进前端布局缓存，重新载入后仍在。这个字段的整机总量读不到时后端不发这一条，页面上就只有这一条不见，
 * CPU 条照常。改回自动选择：恢复按 ids 请求 memory.usage，条头回到原样。
 */
export default async function resourceBarSourceChoiceJourney(page, baseUrl) {
  const app = await loadAppCopy("zh-CN");
  const view = app.resourceBreakdownView;
  const table = app.resourceTable;
  const GiB = 1024 ** 3;
  const memoryLabel = app.metricLabel["memory.usage"];
  const bar = (metricId) => page.locator(".resource-breakdown-item").filter({ has: page.locator(`.resource-bar-track[data-metric-id="${metricId}"]`) });
  const header = (metricId) => bar(metricId).locator(".resource-breakdown-header strong");
  const setScenario = async (patch) => {
    const response = await fetch(`${baseUrl}/__test/scenario`, { method: "POST", headers: { "Content-Type": "application/json" }, body: JSON.stringify(patch) });
    if (!response.ok) throw new Error(`Scenario ${JSON.stringify(patch)} was rejected`);
  };
  const lastBarsQuery = async () => {
    const telemetry = await (await fetch(`${baseUrl}/__test/requests`)).json();
    const queries = (telemetry["/api/resource-monitor/subscribe"]?.queries ?? []).filter((query) => new URLSearchParams(query).get("scope") === "bars");
    const params = new URLSearchParams(queries.at(-1) ?? "");
    return { ids: params.getAll("ids"), measures: params.getAll("measures"), modes: params.getAll("modes") };
  };
  const waitFor = async (check, what) => {
    const deadline = Date.now() + 15_000;
    let last;
    while (Date.now() < deadline) {
      last = await check().catch((error) => error.message);
      if (last === true) return;
      await page.waitForTimeout(200);
    }
    throw new Error(`${what}: ${JSON.stringify(last)}`);
  };
  const editButton = page.getByRole("button", { name: view.editLabel, exact: true });
  const saveButton = page.getByRole("button", { name: view.saveLabel, exact: true });
  const sources = page.getByRole("radiogroup", { name: table.columnSourceLabel(memoryLabel), exact: true });
  const automatic = app.metricPicker.sourceAutomatic(table.memoryQuantities.private_working_set);

  await setScenario({ resourceBarMeasureTotalsMissing: [] });
  await page.goto(baseUrl, { waitUntil: "domcontentloaded" });
  await header("memory.usage").waitFor({ timeout: 15_000 });
  await header("memory.usage").scrollIntoViewIfNeeded();
  if (await header("memory.usage").textContent() !== memoryLabel) throw new Error("The automatic memory bar lost its name");
  if (await header("memory.usage").getAttribute("title") !== `${automatic}\n${app.resourceBreakdown.memoryBasis}`) {
    throw new Error(`The automatic memory bar does not name the field it reads: ${await header("memory.usage").getAttribute("title")}`);
  }

  await editButton.click();
  await sources.waitFor();
  const radios = sources.getByRole("radio");
  const labels = (await radios.allTextContents()).map((label) => label.trim());
  const expected = [automatic, table.memoryQuantities.working_set_including_shared,
    table.memoryQuantities.private_working_set, table.memoryQuantities.private_commit];
  if (JSON.stringify(labels) !== JSON.stringify(expected)) throw new Error(`Bar sources ${JSON.stringify(labels)}, expected ${JSON.stringify(expected)}`);
  if (await radios.nth(0).getAttribute("aria-checked") !== "true") throw new Error("Automatic is not the checked bar source by default");
  if (await sources.evaluate((element) => element.classList.contains("segmented")) !== true
    || await radios.nth(0).evaluate((element) => element.classList.contains("segment")) !== true) {
    throw new Error("The bar source choice does not use the shared segmented look");
  }
  const scale = page.getByRole("combobox", { name: view.scaleModeLabel(memoryLabel), exact: true });

  // 私有提交量。
  await radios.nth(3).click();
  if (await radios.nth(3).getAttribute("aria-checked") !== "true") throw new Error("Choosing private commit did not move the check");
  if (await scale.isEnabled()) throw new Error("A bar with a chosen source still offers scale modes the backend ignores");
  await saveButton.click();
  await editButton.waitFor();
  const commitLabel = table.memoryColumnBySource(table.memoryQuantities.private_commit);
  await waitFor(async () => await header("memory.usage").textContent() === commitLabel || await header("memory.usage").textContent(), "Private-commit bar name");
  if (await header("memory.usage").getAttribute("title") !== app.resourceBreakdown.commitBasis) throw new Error("The private-commit bar does not state its basis");
  const commitTotal = formatBytes(6 * GiB, "memory");
  await waitFor(async () => (await bar("memory.usage").locator(".resource-breakdown-header > span").first().textContent())?.includes(commitTotal)
    || await bar("memory.usage").locator(".resource-breakdown-header > span").first().textContent(), "Private-commit total is the system commit charge");
  const commitQuery = await lastBarsQuery();
  if (!commitQuery.measures.includes("process_all_private_commit_actual") || commitQuery.ids.includes("memory.usage")
    || commitQuery.modes.some((mode) => mode.startsWith("memory.usage:")) || !commitQuery.ids.includes("cpu.usage")) {
    throw new Error(`The bar subscription did not request only the chosen field for memory: ${JSON.stringify(commitQuery)}`);
  }

  // 选择存在前端布局缓存里。
  await page.reload({ waitUntil: "domcontentloaded" });
  await waitFor(async () => await header("memory.usage").textContent() === commitLabel || await header("memory.usage").textContent(), "Saved bar source after reload");

  // 这个字段的整机总量读不到：后端不发这一条，只有它不见，CPU 条照常。
  await setScenario({ resourceBarMeasureTotalsMissing: ["process_all_private_commit_actual"] });
  await waitFor(async () => await bar("memory.usage").count() === 0 || "memory bar still shown", "A bar whose total is missing disappears");
  if (await bar("cpu.usage").count() !== 1) throw new Error("A missing memory total removed other bars");
  await setScenario({ resourceBarMeasureTotalsMissing: [] });
  await header("memory.usage").waitFor({ timeout: 15_000 });

  // 自动选择恢复后端的选择。
  await editButton.click();
  await sources.waitFor();
  await sources.getByRole("radio").nth(0).click();
  await saveButton.click();
  await editButton.waitFor();
  await waitFor(async () => await header("memory.usage").textContent() === memoryLabel || await header("memory.usage").textContent(), "Automatic bar name");
  const automaticQuery = await lastBarsQuery();
  if (!automaticQuery.ids.includes("memory.usage") || automaticQuery.measures.length !== 0) {
    throw new Error(`Automatic did not request memory.usage by id again: ${JSON.stringify(automaticQuery)}`);
  }
  return { sources: labels, commitQuery, automaticQuery };
}

/**
 * 九语检查（localizationJourney 调用，调用前已在监视控制台）：编辑资源占用条目时物理内存的“来源”名称、
 * 四个选项（自动选择写出实际读的私有工作集）和工作集、私有提交量的说明按当前语言显示；取消编辑，不保存。
 * 来源只给已勾选的资源条摆，而九语旅程前面的字段卡检查把资源条清空了（metricFieldsProbe 写 resourceBars: []）：
 * 这时先勾上物理内存并保存，等后端发来这一条（自动选择才写得出实际读的字段），检查完再取消勾选保存，还原成原样。
 */
export async function checkResourceBarSourceText(page, app, language) {
  const view = app.resourceBreakdownView;
  const table = app.resourceTable;
  const memoryLabel = app.metricLabel["memory.usage"];
  const editButton = page.getByRole("button", { name: view.editLabel, exact: true });
  const saveButton = page.getByRole("button", { name: view.saveLabel, exact: true });
  const memoryOption = page.locator(".resource-bar-option").filter({ hasText: memoryLabel }).getByRole("checkbox");
  const memoryBar = page.locator('.resource-breakdown-item .resource-bar-track[data-metric-id="memory.usage"]');
  await editButton.scrollIntoViewIfNeeded();
  await editButton.click();
  const enabledHere = !await memoryOption.isChecked();
  if (enabledHere) {
    await memoryOption.check();
    await saveButton.click();
    await editButton.waitFor({ timeout: 15_000 });
    await memoryBar.waitFor({ timeout: 15_000 });
    await editButton.click();
  }
  const sources = page.getByRole("radiogroup", { name: table.columnSourceLabel(memoryLabel), exact: true });
  await sources.waitFor({ timeout: 15_000 });
  const radios = sources.getByRole("radio");
  const labels = (await radios.allTextContents()).map((label) => label.trim());
  const expected = [app.metricPicker.sourceAutomatic(table.memoryQuantities.private_working_set), table.memoryQuantities.working_set_including_shared,
    table.memoryQuantities.private_working_set, table.memoryQuantities.private_commit];
  if (JSON.stringify(labels) !== JSON.stringify(expected)) throw new Error(`${language}: bar sources ${JSON.stringify(labels)}, expected ${JSON.stringify(expected)}`);
  if (await radios.nth(1).getAttribute("title") !== table.memorySourceBasis.working_set_including_shared
    || await radios.nth(3).getAttribute("title") !== app.resourceBreakdown.commitBasis) {
    throw new Error(`${language}: bar source basis is not localized`);
  }
  if (enabledHere) {
    await memoryOption.uncheck();
    await saveButton.click();
    await editButton.waitFor({ timeout: 15_000 });
    await memoryBar.waitFor({ state: "detached", timeout: 15_000 });
  } else {
    await page.getByRole("button", { name: view.cancelEditLabel, exact: true }).click();
    await editButton.waitFor({ timeout: 15_000 });
  }
  return { language, sources: labels, enabledHere };
}
