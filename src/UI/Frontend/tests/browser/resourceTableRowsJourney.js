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

  // 2. 系统/驱动保留行和普通软件行分得开：文字退一档，底上有斜纹。
  const residualLook = await residual.evaluate((row) => ({
    residualClass: row.classList.contains("residual-row"),
    image: getComputedStyle(row).backgroundImage,
    nameColor: getComputedStyle(row.querySelector(".resource-row-name")).color,
    muted: (() => { const probe = document.createElement("span"); probe.style.color = "var(--muted)"; row.append(probe); const color = getComputedStyle(probe).color; probe.remove(); return color; })()
  }));
  const ordinaryImage = await panel.locator('.resource-table-row[aria-label="示例软件"]').evaluate((row) => getComputedStyle(row).backgroundImage);
  if (!residualLook.residualClass || !residualLook.image.includes("repeating-linear-gradient") || ordinaryImage !== "none"
    || residualLook.nameColor !== residualLook.muted) {
    throw new Error(`Residual row is not set apart: ${JSON.stringify({ residualLook, ordinaryImage })}`);
  }

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

  // 5. 表头：数值列的列名右边缘和数字右边缘对齐；排序箭头换列、换方向时列名不动；悬停和当前排序列不同色；每个表头都有提示。
  const cpuIndex = await headerIndex(panel, "cpu");
  const cpuHead = panel.locator('.resource-table-head-cell[data-column-id="cpu"]');
  const cpuButton = cpuHead.locator(".resource-table-sort-button");
  const headerState = () => cpuHead.evaluate((cell, index) => {
    const label = cell.querySelector(".resource-table-sort-button > span").getBoundingClientRect();
    const indicator = cell.querySelector(".resource-table-sort-indicator");
    const valueCell = document.querySelectorAll('.resource-table-row.software-row[aria-hidden="false"]')[1]
      .querySelectorAll(".resource-table-cell")[index];
    const range = document.createRange();
    range.selectNodeContents(valueCell);
    return { labelLeft: label.left, labelRight: label.right, valueRight: range.getBoundingClientRect().right,
      sort: cell.getAttribute("aria-sort"), opacity: getComputedStyle(indicator).opacity,
      ascending: indicator.classList.contains("ascending"), indicatorLeftOfLabel: indicator.getBoundingClientRect().right <= label.left + 0.5,
      background: getComputedStyle(cell).backgroundColor };
  }, cpuIndex);
  await page.mouse.move(5, 5);
  const unsorted = await headerState();
  await cpuButton.click();
  await page.mouse.move(5, 5);
  await page.waitForTimeout(400);
  const descending = await headerState();
  await cpuButton.click();
  await page.mouse.move(5, 5);
  await page.waitForTimeout(400);
  const ascending = await headerState();
  const ascendingTransform = await cpuHead.locator(".resource-table-sort-indicator").evaluate((icon) => getComputedStyle(icon).transform);
  if (Math.abs(unsorted.labelRight - unsorted.valueRight) > 1 || unsorted.opacity !== "0"
    || descending.sort !== "descending" || descending.opacity !== "1" || descending.ascending || !descending.indicatorLeftOfLabel
    || ascending.sort !== "ascending" || !ascending.ascending || !/^matrix\(-1, /.test(ascendingTransform)
    || [descending, ascending].some((state) => Math.abs(state.labelRight - unsorted.labelRight) > 0.5 || Math.abs(state.labelLeft - unsorted.labelLeft) > 0.5)) {
    throw new Error(`Header sort presentation: ${JSON.stringify({ unsorted, descending, ascending, ascendingTransform })}`);
  }
  const diskHead = panel.locator('.resource-table-head-cell[data-column-id="disk"]');
  await diskHead.hover();
  await page.waitForTimeout(300);
  const hovered = await diskHead.evaluate((cell) => ({ background: getComputedStyle(cell).backgroundColor,
    preview: getComputedStyle(cell.querySelector(".resource-table-sort-indicator")).opacity }));
  if (hovered.background === ascending.background || Number(hovered.preview) <= 0 || Number(hovered.preview) >= 1) {
    throw new Error(`Hovered header looks like the sorted one or shows no preview: ${JSON.stringify({ hovered, active: ascending.background })}`);
  }
  const titles = await panel.locator(".resource-table-sort-button").evaluateAll((buttons) => buttons.map((button) => ({
    label: button.querySelector("span").textContent.trim(), title: button.title })));
  // 有口径说明的列（内存、磁盘）提示写口径，其余写列名本身。
  const plain = titles.filter(({ label }) => ["名称", "状态", "CPU", "网络"].includes(label));
  if (titles.some(({ title }) => !title) || plain.length !== 4 || plain.some(({ label, title }) => title !== label)) {
    throw new Error(`Every header needs a tooltip, the name itself when there is no basis: ${JSON.stringify(titles)}`);
  }
  results.header = { unsorted, descending, ascending, hovered, titles };

  // 6. 列宽把手：平时不接指针（表头格边缘点下去是排序），编辑时以列边为中线 12 px 宽，好按中。
  const edgeHit = () => panel.locator('.resource-table-head-cell[data-column-id="status"]').evaluate((cell) => {
    const rect = cell.getBoundingClientRect();
    const y = (rect.top + rect.bottom) / 2;
    const hit = (x) => document.elementFromPoint(x, y)?.closest(".resource-table-column-resizer, .resource-table-sort-button")?.className ?? null;
    const resizer = cell.querySelector(".resource-table-column-resizer");
    return { inside: hit(rect.right - 3), outside: hit(rect.right + 4), width: resizer.getBoundingClientRect().width,
      cursor: getComputedStyle(resizer).cursor };
  });
  const browsing = await edgeHit();
  await panel.locator('[data-focus-key="resource-table-edit"]').click();
  await panel.locator(".resource-table-head-cell.editing").first().waitFor();
  const editing = await edgeHit();
  await panel.getByRole("button", { name: app.resourceTableView.cancelEditLabel, exact: true }).click();
  if (browsing.inside !== "resource-table-sort-button" || browsing.cursor === "col-resize"
    || editing.inside !== "resource-table-column-resizer" || editing.outside !== "resource-table-column-resizer"
    || editing.width !== 12 || editing.cursor !== "col-resize") {
    throw new Error(`Column resize handle hit area: ${JSON.stringify({ browsing, editing })}`);
  }
  results.resizeHandle = { browsing, editing };

  // 7. 展开：箭头是图标并转到朝下；刚露出来的进程行播一次入场，播完自己清掉；无动画档直接出现，同样清掉。
  const expandAndWatch = (name) => panel.evaluate(async (name) => {
    const row = document.querySelector(`.resource-table-row[aria-label="${name}"]`);
    const button = row.querySelector(".resource-row-expander");
    button.click();
    await new Promise((resolveFrame) => requestAnimationFrame(() => requestAnimationFrame(resolveFrame)));
    const revealing = [...document.querySelectorAll(".resource-table-row.revealing")];
    const first = {
      count: revealing.length,
      kinds: [...new Set(revealing.map((element) => element.classList.contains("process-row") ? "process" : "other"))],
      animation: revealing[0] ? getComputedStyle(revealing[0].querySelector(".resource-table-cell")).animationName : null,
      duration: revealing[0] ? getComputedStyle(revealing[0].querySelector(".resource-table-cell")).animationDuration : null,
      chevron: getComputedStyle(row.querySelector(".resource-row-expander svg")).transform,
      chevronTransition: getComputedStyle(row.querySelector(".resource-row-expander svg")).transitionProperty,
      glyphText: button.textContent.trim()
    };
    await new Promise((resolveDelay) => setTimeout(resolveDelay, 700));
    return { ...first, after: document.querySelectorAll(".resource-table-row.revealing").length,
      expanded: row.getAttribute("aria-expanded"), chevronAfter: getComputedStyle(row.querySelector(".resource-row-expander svg")).transform };
  }, name);
  const reveal = await expandAndWatch("示例软件 5");
  if (reveal.count < 1 || reveal.kinds.join() !== "process" || reveal.animation !== "motion-row-reveal" || reveal.duration === "0s"
    || reveal.glyphText !== "" || !reveal.chevronTransition.includes("transform") || reveal.after !== 0 || reveal.expanded !== "true"
    || reveal.chevronAfter !== "matrix(0, 1, -1, 0, 0, 0)") {
    throw new Error(`Expanded process rows do not reveal once: ${JSON.stringify(reveal)}`);
  }
  await page.evaluate(() => { document.body.dataset.animations = "none"; });
  const instant = await expandAndWatch("示例软件 6");
  await page.evaluate(() => { document.body.dataset.animations = "normal"; });
  // 时长为 0 的入场两帧内多半已经结束、类已清掉；还在的话时长必须是 0。
  if ((instant.count > 0 && instant.duration !== "0s") || instant.after !== 0 || instant.expanded !== "true"
    || instant.chevronTransition.includes("transform")) {
    throw new Error(`Without animations the rows must appear at once and leave no reveal state: ${JSON.stringify(instant)}`);
  }
  results.reveal = { reveal, instant };

  // 8. 窄列：状态、文字、数值单元格的文字在能出省略号的那一层里；把状态列压到 44 px 时出现省略号而不是硬切。
  const ellipsis = await residual.evaluate((row, statusAt) => {
    const cells = [...row.querySelectorAll(".resource-table-cell:is(.status-cell, .text-cell, .value-cell)")];
    const layers = cells.map((cell) => cell.querySelectorAll(":scope > .resource-table-cell-text").length);
    const status = row.querySelectorAll(".resource-table-cell")[statusAt];
    const columns = row.style.gridTemplateColumns;
    const tracks = columns.split(" ");
    tracks[statusAt] = "44px";
    row.style.gridTemplateColumns = tracks.join(" ");
    const text = status.querySelector(".resource-table-cell-text");
    const result = { layers, overflow: text.scrollWidth > text.clientWidth, textOverflow: getComputedStyle(text).textOverflow,
      cellClips: getComputedStyle(status).textOverflow };
    row.style.gridTemplateColumns = columns;
    return result;
  }, statusIndex);
  if (ellipsis.layers.some((count) => count !== 1) || !ellipsis.overflow || ellipsis.textOverflow !== "ellipsis") {
    throw new Error(`Narrow cells must end in an ellipsis: ${JSON.stringify(ellipsis)}`);
  }
  results.ellipsis = ellipsis;

  if (errors.length > 0) throw new Error(`Page errors: ${errors.join("\n")}`);
  return results;
}

async function headerIndex(panel, columnId) {
  const ids = await panel.locator(".resource-table-head-cell").evaluateAll((cells) => cells.map((cell) => cell.dataset.columnId));
  const index = ids.indexOf(columnId);
  if (index < 0) throw new Error(`No ${columnId} column among ${JSON.stringify(ids)}`);
  return index;
}
