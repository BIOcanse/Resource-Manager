import { loadAppCopy } from "../../src/i18n/copy/appCopyLoader.ts";
import { formatBytes } from "../../src/presentation/byteUnits.ts";
import { waitForCondition } from "./journeyWaits.js";

/**
 * 资源条先选项目、再选来源（docs/metric_item_source_choice_2026-10-25.md，简体中文）：
 * 物理内存资源条在“自动选择”时，条头说明的第一行写出后端这次实际读的字段（夹具：私有工作集）。
 * 编辑资源占用条目时，物理内存下面摆“来源”：第一项自动选择（已勾，写出私有工作集），其后是后端进程字段目录里
 * 整机比较指标（resourceFactTotalMetricId）是 memory.usage 的内存口径：工作集（含共享页）、私有工作集；
 * 私有提交量比的是提交量，不在这里列。选工作集并保存：订阅把这个字段作显式口径（measures=），缩放按
 * modes=字段名:缩放 传，不再按 ids 请求 memory.usage；条头写出口径和说明，数值是后端给的物理内存已用量；
 * 缩放可以改成现有占用，订阅随之改；展开这一条看得到这个字段的进程（明细订阅同样带 measures=）。
 * 选择存进前端布局缓存，重新载入后仍在。这个字段的整机总量读不到时后端保留显式不可用条，页面显示空值，
 * CPU 条照常。改回自动选择：恢复按 ids 请求 memory.usage，条头回到原样。
 */
export default async function resourceBarSourceChoiceJourney(page, baseUrl) {
  const app = await loadAppCopy("zh-CN");
  const view = app.resourceBreakdownView;
  const table = app.resourceTable;
  const GiB = 1024 ** 3;
  const field = "process_all_working_set_actual";
  const memoryLabel = app.metricLabel["memory.usage"];
  const bar = (metricId) => page.locator(".resource-breakdown-item").filter({ has: page.locator(`.resource-bar-track[data-metric-id="${metricId}"]`) });
  const header = (metricId) => bar(metricId).locator(".resource-breakdown-header strong");
  const setScenario = async (patch) => {
    const response = await fetch(`${baseUrl}/__test/scenario`, { method: "POST", headers: { "Content-Type": "application/json" }, body: JSON.stringify(patch) });
    if (!response.ok) throw new Error(`Scenario ${JSON.stringify(patch)} was rejected`);
  };
  const lastQuery = async (scope) => {
    const telemetry = await (await fetch(`${baseUrl}/__test/requests`)).json();
    const queries = (telemetry["/api/resource-monitor/subscribe"]?.queries ?? []).filter((query) => new URLSearchParams(query).get("scope") === scope);
    const params = new URLSearchParams(queries.at(-1) ?? "");
    return { ids: params.getAll("ids"), measures: params.getAll("measures"), modes: params.getAll("modes"), processDetails: params.getAll("processDetails") };
  };
  const waitFor = async (check, what) => {
    let last;
    await waitForCondition(page, async () => {
      last = await check().catch((error) => error.message);
      return last === true;
    }, { timeoutMs: 15_000, description: what }).catch(() => {
      throw new Error(`${what}: ${JSON.stringify(last)}`);
    });
  };
  const editButton = page.getByRole("button", { name: view.editLabel, exact: true });
  const saveButton = page.getByRole("button", { name: view.saveLabel, exact: true });
  const sources = page.getByRole("radiogroup", { name: table.columnSourceLabel(memoryLabel), exact: true });
  const scale = page.getByRole("combobox", { name: view.scaleModeLabel(memoryLabel), exact: true });
  // 来源行只给选中的那一条摆：进入编辑后先点物理内存芯片上的名字选中它。
  const selectMemory = () => page.getByRole("group", { name: view.editorLabel, exact: true })
    .getByRole("button", { name: memoryLabel, exact: true }).click();
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
  await selectMemory();
  await sources.waitFor();
  const radios = sources.getByRole("radio");
  const labels = (await radios.allTextContents()).map((label) => label.trim());
  const expected = [automatic, table.memoryQuantities.working_set_including_shared, table.memoryQuantities.private_working_set];
  if (JSON.stringify(labels) !== JSON.stringify(expected)) throw new Error(`Bar sources ${JSON.stringify(labels)}, expected ${JSON.stringify(expected)}`);
  if (await radios.nth(0).getAttribute("aria-checked") !== "true") throw new Error("Automatic is not the checked bar source by default");
  if (await sources.evaluate((element) => element.classList.contains("segmented")) !== true
    || await radios.nth(0).evaluate((element) => element.classList.contains("segment")) !== true) {
    throw new Error("The bar source choice does not use the shared segmented look");
  }

  // 工作集（含共享页）。缩放仍可选。
  await radios.nth(1).click();
  if (await radios.nth(1).getAttribute("aria-checked") !== "true") throw new Error("Choosing the working set did not move the check");
  if (!await scale.isEnabled()) throw new Error("A bar with a chosen source lost its scale choice");
  await saveButton.click();
  await editButton.waitFor();
  const sourcedLabel = table.memoryColumnBySource(table.memoryQuantities.working_set_including_shared);
  await waitFor(async () => await header("memory.usage").textContent() === sourcedLabel || await header("memory.usage").textContent(), "Working-set bar name");
  if (await header("memory.usage").getAttribute("title") !== `${table.nonAdditiveBasis}\n${table.memorySourceBasis.working_set_including_shared}`) {
    throw new Error("The working-set bar does not state its basis");
  }
  const memoryTotal = formatBytes(8 * GiB, "memory");
  await waitFor(async () => (await bar("memory.usage").locator(".resource-breakdown-header > span").first().textContent())?.includes(memoryTotal)
    || await bar("memory.usage").locator(".resource-breakdown-header > span").first().textContent(), "Working-set total is the physical memory in use");
  const sourcedQuery = await lastQuery("bars");
  if (!sourcedQuery.measures.includes(field) || !sourcedQuery.modes.includes(`${field}:capacity`) || sourcedQuery.ids.includes("memory.usage")
    || sourcedQuery.modes.some((mode) => mode.startsWith("memory.usage:")) || !sourcedQuery.ids.includes("cpu.usage")) {
    throw new Error(`The bar subscription did not request only the chosen field for memory: ${JSON.stringify(sourcedQuery)}`);
  }

  // 展开这一条：进程来自这个字段的明细订阅（measures=），不是“暂不可用”。
  const track = bar("memory.usage").locator(".resource-bar-track");
  await track.scrollIntoViewIfNeeded();
  const bounds = await track.boundingBox();
  if (!bounds) throw new Error("The working-set bar has no bounds");
  await track.click({ position: { x: Math.max(1, bounds.width / 100), y: bounds.height / 2 } });
  const processPanel = bar("memory.usage").locator(".resource-process-panel");
  await processPanel.locator('.resource-process-track[role="listbox"]').waitFor({ state: "visible", timeout: 15_000 });
  await waitFor(async () => {
    const details = await lastQuery("details");
    return (details.measures.includes(field) && details.processDetails.some((id) => id.includes("frontend-harness.software"))) || details;
  }, "The process details of a sourced bar subscribe its field");

  // 缩放改成现有占用：订阅按字段名带上 active。
  await editButton.click();
  await scale.click();
  await page.getByRole("option", { name: app.resourceBreakdown.scaleActive, exact: true }).click();
  await saveButton.click();
  await editButton.waitFor();
  await waitFor(async () => (await lastQuery("bars")).modes.includes(`${field}:active`) || (await lastQuery("bars")).modes, "Sourced bar scale mode in the subscription");

  // 选择存在前端布局缓存里。
  await page.reload({ waitUntil: "domcontentloaded" });
  await waitFor(async () => await header("memory.usage").textContent() === sourcedLabel || await header("memory.usage").textContent(), "Saved bar source after reload");

  // bar-continuity：总量读不到仍保留所选条，空值不能显示成零或沿用上帧数值。
  await setScenario({ resourceBarMeasureTotalsMissing: [field] });
  await waitFor(async () => (await bar("memory.usage").locator(".resource-breakdown-header > span").first().textContent())?.startsWith("- ·")
    || "missing total was not shown as unavailable", "An unavailable bar remains with an empty total");
  if (await bar("memory.usage").locator('[role="option"]').count() !== 0) throw new Error("An unavailable bar retained software segments");
  if (await bar("cpu.usage").count() !== 1) throw new Error("A missing memory total removed other bars");
  await setScenario({ resourceBarMeasureTotalsMissing: [] });
  await header("memory.usage").waitFor({ timeout: 15_000 });

  // 自动选择恢复后端的选择。
  await editButton.click();
  await selectMemory();
  await sources.waitFor();
  await sources.getByRole("radio").nth(0).click();
  await saveButton.click();
  await editButton.waitFor();
  await waitFor(async () => await header("memory.usage").textContent() === memoryLabel || await header("memory.usage").textContent(), "Automatic bar name");
  const automaticQuery = await lastQuery("bars");
  if (!automaticQuery.ids.includes("memory.usage") || automaticQuery.measures.length !== 0) {
    throw new Error(`Automatic did not request memory.usage by id again: ${JSON.stringify(automaticQuery)}`);
  }
  // 后端拒绝不支持的口径（峰值提交量）：mock 和后端一样回 400。
  const rejected = await fetch(`${baseUrl}/api/resource-monitor/snapshot?scope=bars&ids=&measures=process_all_peak_private_commit_actual`);
  if (rejected.status !== 400) throw new Error(`An unsupported measure was accepted: ${rejected.status}`);
  return { sources: labels, sourcedQuery, automaticQuery };
}

/**
 * 九语检查（localizationJourney 调用，调用前已在监视控制台）：编辑资源占用条目时物理内存的“来源”名称、
 * 三个选项（自动选择写出实际读的私有工作集，然后是整机比较指标为物理内存的工作集、私有工作集）和说明按当前语言显示；取消编辑，不保存。
 * 来源只给选中、且已勾选的资源条摆（先点芯片上的名字选中物理内存），而九语旅程前面的字段卡检查把资源条清空了（metricFieldsProbe 写 resourceBars: []）：
 * 这时先勾上物理内存并保存，等后端发来这一条（自动选择才写得出实际读的字段），检查完再取消勾选保存，还原成原样。
 */
export async function checkResourceBarSourceText(page, app, language) {
  const view = app.resourceBreakdownView;
  const table = app.resourceTable;
  const memoryLabel = app.metricLabel["memory.usage"];
  const editButton = page.getByRole("button", { name: view.editLabel, exact: true });
  const saveButton = page.getByRole("button", { name: view.saveLabel, exact: true });
  const editor = page.getByRole("group", { name: view.editorLabel, exact: true });
  const memoryOption = editor.getByRole("checkbox", { name: view.showBar(memoryLabel), exact: true });
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
  // 来源行只给选中的那一条摆：点物理内存芯片上的名字选中它。
  await editor.getByRole("button", { name: memoryLabel, exact: true }).click();
  const sources = page.getByRole("radiogroup", { name: table.columnSourceLabel(memoryLabel), exact: true });
  await sources.waitFor({ timeout: 15_000 });
  const radios = sources.getByRole("radio");
  const labels = (await radios.allTextContents()).map((label) => label.trim());
  const expected = [app.metricPicker.sourceAutomatic(table.memoryQuantities.private_working_set), table.memoryQuantities.working_set_including_shared,
    table.memoryQuantities.private_working_set];
  if (JSON.stringify(labels) !== JSON.stringify(expected)) throw new Error(`${language}: bar sources ${JSON.stringify(labels)}, expected ${JSON.stringify(expected)}`);
  if (await radios.nth(1).getAttribute("title") !== `${table.nonAdditiveBasis}\n${table.memorySourceBasis.working_set_including_shared}`
    || await radios.nth(2).getAttribute("title") !== app.resourceBreakdown.memoryBasis) {
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
