import { loadAppCopy } from "../../src/i18n/copy/appCopyLoader.ts";
import { pointPreferences } from "../../src/preferences/frontendPreferences.ts";
import { dashboardPointValues } from "../../src/preferences/dashboardPreferences.ts";
import { writeFrontendPreferences } from "./frontendPreferencesFixture.js";

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
  preferences.appearance.animationStyle = "desktop"; preferences.appearance.autoDisableAnimations = false;
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
  await page.waitForTimeout(250);
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
  const softwareOrder = () => visibleRows("software").evaluateAll((rows) => rows.map((row) => row.getAttribute("aria-label")));
  const combinedOrder = await softwareOrder();
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

  // 5b. 排序轮流（用户 10-27）：点一次从高到低、两次从低到高、第三次回到默认的综合排序——没有一列有箭头，aria-sort 全是 none，行序回到综合排序。
  //     键盘按表头按钮（Enter、空格）走同一个轮流。
  const sortState = () => panel.evaluate((element) => ({
    sorts: [...element.querySelectorAll(".resource-table-head-cell")].map((cell) => cell.getAttribute("aria-sort")),
    active: element.querySelectorAll(".resource-table-head-cell.active").length,
    shownArrows: [...element.querySelectorAll(".resource-table-sort-indicator")].filter((icon) => getComputedStyle(icon).opacity !== "0").length }));
  const waitForCombined = async (step) => {
    await page.mouse.move(5, 5);
    try {
      await page.waitForFunction(() => [...document.querySelectorAll(".resource-table-panel .resource-table-head-cell")]
        .every((cell) => cell.getAttribute("aria-sort") === "none"), undefined, { timeout: 3000 });
      await page.waitForTimeout(400);
    } catch { /* 下面的断言写出实际状态 */ }
    const state = await sortState();
    const order = await softwareOrder();
    if (state.sorts.some((sort) => sort !== "none") || state.active !== 0 || state.shownArrows !== 0
      || JSON.stringify(order) !== JSON.stringify(combinedOrder)) {
      throw new Error(`${step} did not return to the combined order: ${JSON.stringify({ state, order, combinedOrder })}`);
    }
    return state;
  };
  await cpuButton.click();
  const clearedByClick = await waitForCombined("The third click");
  const keyboardSorts = [];
  await cpuButton.focus();
  for (const key of ["Enter", "Space"]) {
    await page.keyboard.press(key);
    await page.waitForFunction((expected) => document.querySelector('.resource-table-panel .resource-table-head-cell[data-column-id="cpu"]')
      ?.getAttribute("aria-sort") === expected, key === "Enter" ? "descending" : "ascending", { timeout: 3000 });
    keyboardSorts.push(await cpuHead.getAttribute("aria-sort"));
  }
  await page.keyboard.press("Enter");
  const clearedByKeyboard = await waitForCombined("The third key press");
  results.sortCycle = { clearedByClick, keyboardSorts, clearedByKeyboard };

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

  // 7. 展开：箭头是图标并转到朝下；刚露出来的进程行由呈现层标上进场槽位 row，只淡入（不位移），播完自己清掉；无动画档直接出现，同样清掉。
  //    再收起：交互上进程行当场没有了（行位换成别的行、aria-expanded 为 false），画面上留下的幽灵不可命中、inert、aria-hidden；
  //    无动画档没有幽灵（docs/resource_table_rows_2026-10-25.md，docs/animation_style_2026-10-26.md「总原则」）。
  const expandAndWatch = (name) => panel.evaluate(async (_panel, name) => {
    const row = document.querySelector(`.resource-table-row:not([data-exit-ghost])[aria-label="${name}"]`);
    const button = row.querySelector(".resource-row-expander");
    button.click();
    const sameTaskRows = document.querySelectorAll(".resource-table-row.process-row:not([data-exit-ghost])").length;
    await new Promise((resolveFrame) => requestAnimationFrame(() => requestAnimationFrame(resolveFrame)));
    const revealing = [...document.querySelectorAll('.resource-table-row[data-enter-slot="row"]')];
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
    const opened = { ...first, sameTaskRows, after: document.querySelectorAll('.resource-table-row[data-enter-slot="row"]').length,
      expanded: row.getAttribute("aria-expanded"), chevronAfter: getComputedStyle(row.querySelector(".resource-row-expander svg")).transform };
    // 收起：同一个任务里读交互状态；幽灵在下一帧画出来之前放好，到那一帧再读幽灵。
    const processBefore = document.querySelectorAll(".resource-table-row.process-row:not([data-exit-ghost])").length;
    row.querySelector(".resource-row-expander").click();
    const collapsedRow = document.querySelector(`.resource-table-row:not([data-exit-ghost])[aria-label="${name}"]`);
    const processAfter = document.querySelectorAll(".resource-table-row.process-row:not([data-exit-ghost])").length;
    await new Promise((resolveFrame) => requestAnimationFrame(resolveFrame));
    const ghosts = [...document.querySelectorAll(".resource-table-row[data-exit-ghost]")];
    const hits = ghosts.map((ghost) => {
      const rect = ghost.getBoundingClientRect();
      const hit = document.elementFromPoint(rect.left + rect.width / 2, rect.top + rect.height / 2);
      return hit ? Boolean(hit.closest("[data-exit-ghost]")) : false;
    });
    const collapse = {
      processBefore,
      processAfter,
      expanded: collapsedRow?.getAttribute("aria-expanded") ?? null,
      ghosts: ghosts.length,
      ghostsQuiet: ghosts.every((ghost) => ghost.inert && ghost.getAttribute("aria-hidden") === "true"
        && getComputedStyle(ghost).pointerEvents === "none" && !ghost.hasAttribute("role") && !ghost.hasAttribute("data-resource-row-id")),
      ghostHit: hits.some(Boolean),
      ghostAnimation: ghosts[0] ? getComputedStyle(ghosts[0]).animationName : null
    };
    await new Promise((resolveDelay) => setTimeout(resolveDelay, 700));
    collapse.ghostsLeft = document.querySelectorAll("[data-exit-ghost]").length;
    return { ...opened, collapse };
  }, name);
  const reveal = await expandAndWatch("示例软件 5");
  if (reveal.count < 1 || reveal.kinds.join() !== "process" || reveal.animation !== "motion-fade-in" || reveal.duration === "0s"
    || reveal.sameTaskRows < 1 || reveal.glyphText !== "" || !reveal.chevronTransition.includes("transform") || reveal.after !== 0
    || reveal.expanded !== "true" || reveal.chevronAfter !== "matrix(0, 1, -1, 0, 0, 0)") {
    throw new Error(`Expanded process rows do not reveal once: ${JSON.stringify(reveal)}`);
  }
  const collapse = reveal.collapse;
  if (collapse.processAfter >= collapse.processBefore || collapse.expanded !== "false" || collapse.ghosts < 1 || !collapse.ghostsQuiet
    || collapse.ghostHit || collapse.ghostAnimation !== "motion-fade-out" || collapse.ghostsLeft !== 0) {
    throw new Error(`Collapsed process rows must leave interaction at once and exit as ghosts: ${JSON.stringify(collapse)}`);
  }
  // 动画风格的所有者是前端偏好；直接写 body 会被 App 的运行方案 effect 在测量窗口里改回。
  // 用已保存偏好重新加载，让无动画的展开处于真实的 none 状态，测完恢复原偏好。
  const reloadWithAnimations = async (animations) => {
    await writeFrontendPreferences(page, (value) => { value.appearance.animationStyle = animations; value.appearance.autoDisableAnimations = false; });
    await page.reload({ waitUntil: "domcontentloaded" });
    await visibleRows("software").first().waitFor({ timeout: 15_000 });
    await panel.evaluate((element) => element.scrollIntoView({ block: "start" }));
    await page.waitForFunction((expected) => document.body.dataset.animations === expected, animations, { timeout: 5000 });
  };
  await reloadWithAnimations("none");
  const instant = await expandAndWatch("示例软件 6");
  await reloadWithAnimations("desktop");
  // 时长为 0 的入场两帧内多半已经结束、类已清掉；还在的话时长必须是 0。
  if (instant.count !== 0 || instant.after !== 0 || instant.expanded !== "true" || instant.sameTaskRows < 1
    || instant.chevronTransition.includes("transform") || instant.collapse.ghosts !== 0 || instant.collapse.expanded !== "false"
    || instant.collapse.processAfter >= instant.collapse.processBefore) {
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

  // 9. “更多操作”平时退一半，悬停的行完整显示。
  await page.mouse.move(5, 5);
  await page.waitForTimeout(250);
  const actionsOpacity = () => panel.locator('.resource-table-row[aria-label="示例软件 8"] .resource-row-actions')
    .evaluate((button) => getComputedStyle(button).opacity);
  const idleActions = await actionsOpacity();
  await panel.locator('.resource-table-row[aria-label="示例软件 8"] .status-cell').hover();
  await page.waitForTimeout(250);
  const hoveredActions = await actionsOpacity();
  if (idleActions !== "0.5" || hoveredActions !== "1") {
    throw new Error(`Row actions emphasis: idle ${idleActions}, hovered ${hoveredActions}`);
  }
  results.actions = { idleActions, hoveredActions };

  // 10. 搜索没结果：占位有表格的外框，图标和说明作为一组居中。
  const search = page.getByRole("searchbox", { name: app.resourceTable.searchPlaceholder });
  await search.fill("no-such-resource");
  const state = panel.locator(":scope > .content-state");
  await state.waitFor();
  const stateLayout = await state.evaluate((element) => {
    const box = element.getBoundingClientRect();
    const parts = [...element.children].filter((child) => child.getClientRects().length > 0).map((child) => child.getBoundingClientRect());
    const left = Math.min(...parts.map((part) => part.left));
    const right = Math.max(...parts.map((part) => part.right));
    const style = getComputedStyle(element);
    return { offset: Math.abs((left + right) / 2 - (box.left + box.right) / 2), border: style.borderLeftWidth, radius: style.borderTopLeftRadius };
  });
  await search.fill("");
  await panel.locator(".resource-table-frame").waitFor();
  if (stateLayout.offset > 2 || stateLayout.border !== "1px" || stateLayout.radius === "0px") {
    throw new Error(`Table content state is not framed and centred: ${JSON.stringify(stateLayout)}`);
  }
  results.contentState = stateLayout;

  // 11. 进程级没有树：名字前不留箭头的空位；PID 用等宽数字。
  await panel.locator(".resource-table-mode-button").nth(1).click();
  await visibleRows("process").first().waitFor();
  const processView = await panel.evaluate((element) => {
    const rows = [...element.querySelectorAll('.resource-table-row[aria-hidden="false"]')];
    const pid = element.querySelector('.resource-table-row.process-row[aria-hidden="false"] .text-cell');
    return { spacers: rows.reduce((sum, row) => sum + row.querySelectorAll(".resource-row-spacer, .resource-row-expander").length, 0),
      pidNumeric: getComputedStyle(pid).fontVariantNumeric };
  });
  if (processView.spacers !== 0 || processView.pidNumeric !== "tabular-nums") {
    throw new Error(`Process view presentation: ${JSON.stringify(processView)}`);
  }
  results.processView = processView;

  // 12. Windows 高对比：总计行下沿加粗，系统/驱动保留用 GrayText，当前行有系统色描边，“更多操作”平时不画按钮框。
  await page.emulateMedia({ forcedColors: "active" });
  await page.mouse.move(5, 5);
  await visibleRows("process").nth(2).locator(".status-cell").click();
  await page.mouse.move(5, 5);
  await page.waitForTimeout(250);
  const forced = await panel.evaluate((element) => {
    const systemColor = (name, property = "color") => {
      const probe = document.createElement("span");
      probe.style.setProperty(property, name);
      element.append(probe);
      const value = getComputedStyle(probe).getPropertyValue(property);
      probe.remove();
      return value;
    };
    const summary = element.querySelector('.resource-table-row.summary-row[aria-hidden="false"]');
    const residualRow = element.querySelector('.resource-table-row.residual-row[aria-hidden="false"]');
    const current = element.querySelector('.resource-table-row[aria-hidden="false"]:focus-within');
    const idleActions = element.querySelector('.resource-table-row[aria-hidden="false"]:not(:focus-within) .resource-row-actions');
    return {
      summaryBorder: getComputedStyle(summary).borderBottomWidth,
      residualColor: residualRow ? getComputedStyle(residualRow.querySelector(".resource-row-name")).color : null,
      grayText: systemColor("GrayText"),
      currentOutline: current ? `${getComputedStyle(current).outlineStyle} ${getComputedStyle(current).outlineWidth}` : null,
      actionsBorder: getComputedStyle(idleActions).borderTopColor,
      canvas: systemColor("Canvas")
    };
  });
  await page.emulateMedia({ forcedColors: "none" });
  if (forced.summaryBorder !== "2px" || forced.residualColor !== forced.grayText || forced.currentOutline !== "solid 1px"
    || forced.actionsBorder !== forced.canvas) {
    throw new Error(`Forced-colours presentation: ${JSON.stringify(forced)}`);
  }
  results.forcedColors = forced;

  // 13. 热度条是数字下面的细线，不压在读数上；共享分摊接在自有后面画成虚线，提示写明实线、虚线；总计行同一套规则。
  //     默认的内存列（驻留）带共享分摊，把种下的私有提交量来源改回自动再载入。
  await page.evaluate((key) => {
    const value = JSON.parse(localStorage.getItem(key));
    for (const column of [...value.dashboard.resourceTableColumns, ...value.dashboard.resourceTableProcessColumns]) {
      if (column.id === "memory") column.source = null;
    }
    localStorage.setItem(key, JSON.stringify(value));
  }, "resource-manager:frontend-preferences:v1");
  await page.reload({ waitUntil: "domcontentloaded" });
  await visibleRows().first().waitFor({ timeout: 15_000 });
  await panel.evaluate((element) => element.scrollIntoView({ block: "start" }));
  await panel.locator(".resource-table-mode-button").nth(0).click();
  await panel.locator('.resource-table-row[aria-label="示例软件 2"]').waitFor({ timeout: 15_000 });
  const memoryAt = await headerIndex(panel, "memory");
  const heatBar = await panel.evaluate((element, index) => {
    const read = (row) => {
      const cell = row.querySelectorAll(".resource-table-cell")[index];
      const style = getComputedStyle(cell);
      const range = document.createRange();
      range.selectNodeContents(cell.querySelector(".resource-table-cell-text"));
      return { image: style.backgroundImage, size: style.backgroundSize, position: style.backgroundPosition,
        heat: cell.style.getPropertyValue("--heat"), privateHeat: cell.style.getPropertyValue("--private-heat"),
        textBottom: range.getBoundingClientRect().bottom, barTop: cell.getBoundingClientRect().bottom - 7, title: cell.title };
    };
    return { software: read(element.querySelector('.resource-table-row[aria-label="示例软件 2"]')),
      summary: read(element.querySelector('.resource-table-row.summary-row[aria-hidden="false"]')) };
  }, memoryAt);
  for (const [kind, cell] of Object.entries(heatBar)) {
    if (!cell.image.startsWith("linear-gradient(") || !cell.image.includes("repeating-linear-gradient(") || !cell.image.includes("211, 171, 48")
      || !/^100% 3px, [\d.]+% 3px$/.test(cell.size) || Math.abs(parseFloat(cell.size.split(", ")[1]) - parseFloat(cell.heat)) > 0.01 || cell.textBottom > cell.barTop + 0.5 || parseFloat(cell.privateHeat) >= parseFloat(cell.heat)
      || !cell.title.includes(app.resourceTableView.heatSharedLegend)) {
      throw new Error(`The ${kind} heat bar must be a thin line under the number with an explained shared part: ${JSON.stringify(heatBar)}`);
    }
  }
  results.heatBar = heatBar;

  // 14. 整机内存残差拆成计数器叶子后，叶子的 PID 都是 -1、线上 name 大多为空，行身份带叶子 kind（后端合同）：进程级里每个叶子各占一行，
  //     按名称排序时线上 name 为空的叶子不合并、顺序不跳；选中一个叶子只有它是当前行。身份不来自名字：既不来自线上 name（多个叶子同为空），
  //     也不来自显示名（显示名由残差那边按 kind 给出，这里不读），所以条件只从行 id 判断——至少两个叶子的 id 以空的线上 name 结尾，四个 id 仍互不相同。
  const leafScenario = await page.request.post(`${baseUrl}/__test/scenario`, { data: { resourceMemoryResidualLeaves: true } });
  if (!leafScenario.ok()) throw new Error(`Unable to configure memory residual leaves: ${leafScenario.status()}`);
  await page.reload({ waitUntil: "domcontentloaded" });
  await visibleRows().first().waitFor({ timeout: 15_000 });
  await panel.evaluate((element) => element.scrollIntoView({ block: "start" }));
  await panel.locator(".resource-table-mode-button").nth(1).click();
  const leafPrefix = "attribution:resource-residual:memory.usage:";
  const nameHead = panel.locator('.resource-table-head-cell[data-column-id="name"]');
  for (let attempt = 0; attempt < 3 && await nameHead.getAttribute("aria-sort") !== "ascending"; attempt += 1) {
    await nameHead.locator(".resource-table-sort-button").click();
  }
  const leafRowIds = () => panel.evaluate((element, prefix) => [...element.querySelectorAll('.resource-table-row[aria-hidden="false"]')]
    .map((row) => row.dataset.resourceRowId).filter((id) => id?.startsWith(prefix)), leafPrefix);
  await page.waitForFunction((prefix) => document.querySelectorAll(`.resource-table-row[aria-hidden="false"][data-resource-row-id^="${prefix}"]`).length >= 4,
    leafPrefix, { timeout: 15_000 });
  const leafOrder = await leafRowIds();
  // 行 id 是 process:<软件>:<kind>:<PID>:<线上 name>，线上 name 为空时以 ":-1:" 结尾。
  const emptyWireNameIds = leafOrder.filter((id) => id.endsWith(":-1:"));
  const target = panel.locator(`.resource-table-row[aria-hidden="false"][data-resource-row-id="${leafOrder[1]}"]`);
  await target.locator(".resource-table-cell").nth(await headerIndex(panel, "memory")).click();
  await page.waitForTimeout(300);
  const leafSelection = await panel.evaluate((element, prefix) => [...element.querySelectorAll(`.resource-table-row[aria-hidden="false"][data-resource-row-id^="${prefix}"]`)]
    .map((row) => ({ id: row.dataset.resourceRowId, tabIndex: row.tabIndex, current: row.matches(":focus-within") })), leafPrefix);
  const focusedLeaf = await page.evaluate(() => document.activeElement?.closest(".resource-table-row")?.dataset.resourceRowId ?? null);
  const orderAfter = await leafRowIds();
  const leaves = { leafOrder, emptyWireNameIds, leafSelection, focusedLeaf, orderAfter };
  if (leafOrder.length !== 4 || new Set(leafOrder).size !== 4 || emptyWireNameIds.length < 2
    || JSON.stringify(orderAfter) !== JSON.stringify(leafOrder) || focusedLeaf !== leafOrder[1]
    || leafSelection.filter((row) => row.tabIndex === 0 || row.current).map((row) => row.id).join() !== leafOrder[1]) {
    throw new Error(`Memory residual leaves must be distinct rows with one selection: ${JSON.stringify(leaves)}`);
  }
  results.residualLeaves = leaves;

  if (errors.length > 0) throw new Error(`Page errors: ${errors.join("\n")}`);
  return results;
}

async function headerIndex(panel, columnId) {
  const ids = await panel.locator(".resource-table-head-cell").evaluateAll((cells) => cells.map((cell) => cell.dataset.columnId));
  const index = ids.indexOf(columnId);
  if (index < 0) throw new Error(`No ${columnId} column among ${JSON.stringify(ids)}`);
  return index;
}
