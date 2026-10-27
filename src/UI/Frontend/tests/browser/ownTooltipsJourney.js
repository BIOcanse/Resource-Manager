import assert from "node:assert/strict";
import { mkdir } from "node:fs/promises";
import { join } from "node:path";
import { fileURLToPath } from "node:url";
import { loadAppCopy } from "../../src/i18n/copy/appCopyLoader.ts";
import { languageOptions } from "../../src/i18n/settingsLanguages.ts";
import { seedFrontendPreferences, writeFrontendPreferences } from "./frontendPreferencesFixture.js";
import { NOT_GHOST } from "./liveLocator.js";

/*
 * 我们自己的提示代替浏览器原生 title（ui/primitives/TooltipLayer.tsx、HelpHint.tsx、AnchoredLayer.tsx）：
 * - 没有原生提示：整个会话里（逐个一级页、详细信息的子页、设置的每个分区、任务中心、右键菜单、编辑状态）
 *   除资源表以外，任何时刻都没有元素带非空的 title 属性（MutationObserver 从页面加载起一直记）。
 * - 名称提示：图标按钮悬停时显示它的无障碍名称（aria-label），离按钮 6 px、不盖住按钮、在窗口里；离开就收起；
 *   禁用的图标按钮悬停照样显示；键盘 Tab 到按钮上显示，Esc 收起而焦点不动，Tab 走开就换成下一个或收起。
 *   点一下得到的焦点不算：点完指针移开就收起。
 * - 完整内容提示：监视卡片的指标名、读数，设备拓扑的条目，显卡专用引擎：悬停显示 data-full-content 的全文（含换行）。
 * - 会动的风格下淡入，收起时留一份退场幽灵；无动画下当场出现、当场消失。
 * - 九种语言、最小窗口（760×520 的客户区，744×520 视口）：右上角任务中心按钮的提示、对话框关闭按钮的名称提示、
 *   标题栏开机时长的问号说明都是这种语言的，而且整个在窗口里。
 * - 截图：简体中文和英文、浅色和深色，名称提示、完整内容提示、问号说明、选项说明、右键菜单里的原因各一张。
 */
const languages = languageOptions.filter((option) => option.id !== "system").map((option) => option.id);
const tooltip = `.hover-tooltip${NOT_GHOST}`;

async function tooltipState(page, anchor) {
  return anchor.evaluate((element, selector) => {
    const box = (node) => {
      if (!node) return null;
      const r = node.getBoundingClientRect();
      return { left: r.left, right: r.right, top: r.top, bottom: r.bottom };
    };
    const layer = document.querySelector(selector);
    return {
      text: layer?.textContent ?? null,
      innerText: layer instanceof HTMLElement ? layer.innerText : null,
      anchor: box(element),
      layer: box(layer),
      placement: layer?.getAttribute("data-placement") ?? null,
      viewport: { width: document.documentElement.clientWidth, height: document.documentElement.clientHeight }
    };
  }, tooltip);
}

async function waitTooltip(page, expected, label) {
  await page.waitForFunction(([selector, text]) => {
    const layer = document.querySelector(selector);
    return layer?.hasAttribute("data-placement") && layer.textContent === text;
  }, [tooltip, expected], { timeout: 3000 }).catch(async () => {
    const actual = await page.evaluate((selector) => document.querySelector(selector)?.textContent ?? null, tooltip);
    assert.fail(`${label}: the tooltip does not show ${JSON.stringify(expected)} (shows ${JSON.stringify(actual)})`);
  });
}

async function waitNoTooltip(page, label) {
  await page.waitForFunction((selector) => document.querySelector(selector) === null, tooltip, { timeout: 3000 })
    .catch(() => assert.fail(`${label}: the tooltip stays open`));
}

/** 提示整个在窗口里，离元素 gap px，不盖住元素。 */
function assertPlaced(state, label, gap = 6) {
  const { anchor, layer, viewport } = state;
  assert.ok(anchor && layer, `${label}: the element and its tooltip are on the page`);
  assert.ok(layer.left >= 0 && layer.right <= viewport.width + 0.5 && layer.top >= 0 && layer.bottom <= viewport.height + 0.5,
    `${label}: the tooltip leaves the window ${JSON.stringify(state)}`);
  const distance = state.placement === "above" ? anchor.top - layer.bottom : layer.top - anchor.bottom;
  assert.ok(Math.abs(distance - gap) <= 0.5, `${label}: the tooltip is not ${gap} px from the element ${JSON.stringify(state)}`);
}

/** 指针移到窗口右下角，像手移动一样分几步走（问号说明按离开处和说明围成的区域判断是不是还在路上，一步跳过去会落在那块区域里）。 */
async function moveAway(page) {
  const size = page.viewportSize();
  await page.mouse.move(size.width - 2, size.height - 2, { steps: 12 });
}

async function hoverCenter(page, locator) {
  await locator.scrollIntoViewIfNeeded();
  const box = await locator.boundingBox();
  assert.ok(box, "the element to hover is laid out");
  await page.mouse.move(box.x + box.width / 2, box.y + box.height / 2, { steps: 4 });
}

async function openPage(page, app, id) {
  await page.getByRole("navigation").getByRole("button", { name: app.page[id], exact: true }).click();
  await page.waitForFunction(() => !document.querySelector(".page-content-layer > [data-exit-ghost], [data-exit-slot='content']"), null, { timeout: 5000 })
    .catch(() => undefined);
}

/** 组件页回到第一个分类（走一遍时停在最后一个分类上，那里可能没有卡片）。 */
async function openComponents(page, app) {
  await openPage(page, app, "components");
  await page.locator(`[role='tab']${NOT_GHOST}:visible`).first().click();
}

async function reloadWith(page, change) {
  await writeFrontendPreferences(page, change);
  await page.reload({ waitUntil: "domcontentloaded" });
}

/** 只截提示和它的元素周围一块。 */
async function shotAround(page, path, anchor, layerSelector) {
  const clip = await anchor.evaluate((element, selector) => {
    const boxes = [element, document.querySelector(selector)].filter(Boolean).map((node) => node.getBoundingClientRect());
    const left = Math.max(0, Math.min(...boxes.map((box) => box.left)) - 24);
    const top = Math.max(0, Math.min(...boxes.map((box) => box.top)) - 24);
    const right = Math.min(document.documentElement.clientWidth, Math.max(...boxes.map((box) => box.right)) + 24);
    const bottom = Math.min(document.documentElement.clientHeight, Math.max(...boxes.map((box) => box.bottom)) + 24);
    return { x: left, y: top, width: right - left, height: bottom - top };
  }, layerSelector);
  await page.screenshot({ path, clip });
}

export default async function ownTooltipsJourney(page, baseUrl) {
  const shots = process.env.RM_BROWSER_ARTIFACT_DIR
    ?? fileURLToPath(new URL("../../../../../.local/reports/screenshots/own-tooltips/", import.meta.url));
  await mkdir(shots, { recursive: true });
  const failures = [];
  page.on("pageerror", (error) => failures.push(String(error)));
  // 从页面加载起记下每一个带非空 title 的元素（资源表除外：它的挂载另有人在改，留到以后）。
  await page.addInitScript(() => {
    if (location.origin === "null") return;
    const seen = new Map();
    window.__nativeTitles = seen;
    const describe = (element) => {
      const parts = [];
      let node = element;
      for (let depth = 0; node instanceof Element && depth < 4; depth += 1) {
        const className = typeof node.className === "string" ? node.className.trim().split(/\s+/).slice(0, 2).join(".") : "";
        parts.unshift(node.tagName.toLowerCase() + (className ? `.${className}` : ""));
        node = node.parentElement;
      }
      return parts.join(" > ");
    };
    const record = (element) => {
      if (!(element instanceof Element)) return;
      const text = element.getAttribute("title");
      if (!text || !text.trim() || element.closest(".resource-table-panel")) return;
      const key = `${describe(element)} | ${text.slice(0, 80)}`;
      if (!seen.has(key)) seen.set(key, { path: describe(element), title: text.slice(0, 120) });
    };
    const scan = (root) => {
      if (!(root instanceof Element)) return;
      record(root);
      for (const element of root.querySelectorAll("[title]")) record(element);
    };
    new MutationObserver((mutations) => {
      for (const mutation of mutations) {
        if (mutation.type === "attributes") record(mutation.target);
        else for (const node of mutation.addedNodes) scan(node);
      }
    }).observe(document, { subtree: true, childList: true, attributes: true, attributeFilter: ["title"] });
  });
  await seedFrontendPreferences(page, (value) => {
    value.appearance.language = "zh-CN";
    value.appearance.theme = "light";
    value.appearance.animationStyle = "none";
    value.appearance.autoDisableAnimations = false;
  });
  await page.setViewportSize({ width: 1280, height: 900 });
  await page.goto(baseUrl, { waitUntil: "domcontentloaded" });
  let app = await loadAppCopy("zh-CN");

  // 1. 走一遍：每个一级页、子页、设置分区、编辑状态、对话框和右键菜单。
  const resourceRow = page.locator(`.resource-table-panel .resource-table-row${NOT_GHOST}`).first();
  await resourceRow.waitFor({ timeout: 30_000 });
  await page.getByRole("button", { name: app.monitorPage.editLayoutLabel, exact: true }).click();
  await page.locator(`.dashboard .slot-actions${NOT_GHOST}`).first().waitFor({ state: "attached" });
  await page.getByRole("button", { name: app.monitorPage.cancelEditLabel, exact: true }).click();
  await page.getByRole("button", { name: app.resourceBreakdownView.editLabel, exact: true }).click();
  const moveUp = page.locator(`.resource-breakdown-order-actions${NOT_GHOST} button`).first();
  await moveUp.waitFor();
  // 禁用的图标按钮（第一条不能上移）悬停照样有名称提示。
  assert.ok(await moveUp.isDisabled(), "the first bar cannot move up");
  await hoverCenter(page, moveUp);
  await waitTooltip(page, await moveUp.getAttribute("aria-label"), "disabled move-up button");
  assertPlaced(await tooltipState(page, moveUp), "disabled move-up button");
  await moveAway(page);
  await waitNoTooltip(page, "leaving the disabled button");
  await page.getByRole("button", { name: app.resourceBreakdownView.cancelEditLabel, exact: true }).click();
  // 右键菜单（行尾的「更多操作」打开同一个菜单）：不能用的项在名称下面写原因。
  const rowActions = page.locator(".resource-table-panel [data-focus-key^='resource-row-actions:']:visible").first();
  await rowActions.click();
  const menu = page.locator(`.standard-context-menu${NOT_GHOST}`);
  await menu.waitFor();
  // 原因相同的相邻几项只在第一项下写一次；每个不能用的项都以那一行作为描述。
  const menuReasons = await menu.locator(".standard-context-menu-item").evaluateAll((items) => items.map((item) => ({
    label: item.getAttribute("aria-label"),
    disabled: item.disabled,
    own: item.querySelector(".standard-context-menu-reason")?.textContent ?? null,
    described: document.getElementById(item.getAttribute("aria-describedby") ?? "")?.textContent ?? null
  })));
  assert.ok(menuReasons.every((entry) => !entry.disabled || entry.described), `every disabled menu item is described by a visible reason ${JSON.stringify(menuReasons)}`);
  assert.ok(menuReasons.every((entry, index) => entry.own === null || index === 0
    || !(menuReasons[index - 1].disabled && menuReasons[index - 1].described === entry.own)),
    `a reason is written once for a run of neighbouring items that share it ${JSON.stringify(menuReasons)}`);
  await page.keyboard.press("Escape");
  await menu.waitFor({ state: "detached" });
  await page.locator(".shell-task-button").click();
  await page.getByRole("dialog", { name: app.taskCenter.title, exact: true }).waitFor();
  await page.keyboard.press("Escape");
  await page.getByRole("dialog", { name: app.taskCenter.title, exact: true }).waitFor({ state: "detached" });
  for (const id of ["components", "optimization", "diskUsage", "control", "details", "settings"]) {
    const nav = page.getByRole("navigation").getByRole("button", { name: app.page[id], exact: true });
    if (await nav.count() === 0) continue;
    await openPage(page, app, id);
    if (id === "details") {
      for (const tab of Object.values(app.misc.detailsTab)) {
        const button = page.getByRole("tab", { name: tab, exact: true });
        if (await button.count() > 0) await button.first().click();
      }
    }
    if (id === "components") {
      const tabs = page.locator(`[role='tab']${NOT_GHOST}:visible`);
      for (let index = 0; index < await tabs.count(); index += 1) await tabs.nth(index).click();
    }
    if (id === "settings") {
      const sections = page.locator(".settings-nav .settings-nav-item");
      for (let index = 0; index < await sections.count(); index += 1) await sections.nth(index).click();
    }
  }
  await openPage(page, app, "monitor");
  await resourceRow.waitFor();
  const titles = [...(await page.evaluate(() => [...window.__nativeTitles.values()]))];
  assert.deepEqual(titles, [], `native title tooltips are still rendered outside the resource table: ${JSON.stringify(titles, null, 1)}`);

  // 2. 名称提示：悬停、离开、键盘聚焦、Esc。
  await openComponents(page, app);
  const nameButton = page.locator(`.management-settings-button[data-name-tooltip]${NOT_GHOST}`).first();
  await nameButton.waitFor({ timeout: 15_000 });
  const name = await nameButton.getAttribute("aria-label");
  assert.ok(name, "the icon button has an accessible name");
  assert.equal(await nameButton.getAttribute("title"), null, "the icon button has no native title");
  await hoverCenter(page, nameButton);
  await waitTooltip(page, name, "hovered icon button");
  assertPlaced(await tooltipState(page, nameButton), "hovered icon button");
  assert.equal(await page.locator(tooltip).getAttribute("aria-hidden"), "true", "screen readers read the name from aria-label, not the tooltip");
  assert.equal(await page.locator(tooltip).evaluate((element) => getComputedStyle(element).pointerEvents), "none", "the tooltip does not catch the pointer");
  await page.keyboard.press("Escape");
  await waitNoTooltip(page, "Escape on a hovered tooltip");
  await moveAway(page);
  // 键盘：Tab 到按钮上显示，Esc 收起、焦点不动，Tab 走开就不再是它的提示。
  await page.locator(`.management-surface${NOT_GHOST} input[type='search']`).first().focus();
  let reached = false;
  for (let step = 0; step < 80 && !reached; step += 1) {
    await page.keyboard.press("Tab");
    reached = await nameButton.evaluate((element) => document.activeElement === element);
  }
  assert.ok(reached, "Tab reaches the icon button");
  await waitTooltip(page, name, "keyboard-focused icon button");
  assertPlaced(await tooltipState(page, nameButton), "keyboard-focused icon button");
  await page.keyboard.press("Escape");
  await waitNoTooltip(page, "Escape on a focused tooltip");
  assert.ok(await nameButton.evaluate((element) => document.activeElement === element), "Escape keeps focus on the button");
  await page.keyboard.press("Tab");
  const next = await page.evaluate(() => {
    const active = document.activeElement?.closest("[data-name-tooltip], [data-full-content]");
    return active ? (active.getAttribute("data-full-content") ?? active.getAttribute("aria-label")) : null;
  });
  if (next) await waitTooltip(page, next, "the next focused control");
  else await waitNoTooltip(page, "Tab away");
  await page.evaluate(() => (document.activeElement instanceof HTMLElement ? document.activeElement.blur() : undefined));
  // 点一下得到的焦点不显示：点完指针移开就收起（用不会打开别的东西的名称按钮：先按住再在别处松开）。
  await hoverCenter(page, nameButton);
  await waitTooltip(page, name, "hover before pressing");
  await page.mouse.down();
  await waitNoTooltip(page, "pressing the button");
  await moveAway(page);
  await page.mouse.up();
  await waitNoTooltip(page, "pointer focus does not show the tooltip");

  // 3. 完整内容提示：监视卡片的指标名和读数、设备拓扑的条目、显卡专用引擎。
  await openPage(page, app, "monitor");
  const metricLabel = page.locator(`.dashboard .metric-label[data-full-content]${NOT_GHOST}`).first();
  await metricLabel.waitFor({ timeout: 15_000 });
  const labelText = await metricLabel.getAttribute("data-full-content");
  await hoverCenter(page, metricLabel);
  await waitTooltip(page, labelText, "dashboard metric name");
  assert.equal((await tooltipState(page, metricLabel)).innerText, labelText, "line breaks in the full content are kept");
  assertPlaced(await tooltipState(page, metricLabel), "dashboard metric name");
  await moveAway(page);
  await waitNoTooltip(page, "leaving the metric name");
  await openPage(page, app, "details");
  await page.getByRole("tab", { name: app.misc.detailsTab.device, exact: true }).click();
  const deviceItem = page.locator(`.device-port-item[data-full-content]${NOT_GHOST}`).first();
  await deviceItem.waitFor({ timeout: 30_000 });
  await hoverCenter(page, deviceItem);
  await waitTooltip(page, await deviceItem.getAttribute("data-full-content"), "device topology item");
  await moveAway(page);
  await page.getByRole("tab", { name: app.misc.detailsTab.gpu, exact: true }).click();
  const engine = page.locator(`.gpu-specialized-meter[data-full-content]${NOT_GHOST}`).first();
  if (await engine.count() > 0) {
    await hoverCenter(page, engine);
    await waitTooltip(page, await engine.getAttribute("data-full-content"), "GPU specialized engine");
    await moveAway(page);
  }

  // 4. 动效：会动的风格下淡入、收起时留退场幽灵；无动画下当场出现、当场消失（上面一直是无动画）。
  await reloadWith(page, (value) => { value.appearance.animationStyle = "smooth"; });
  await openComponents(page, app);
  await nameButton.waitFor({ timeout: 15_000 });
  await hoverCenter(page, nameButton);
  await waitTooltip(page, name, "smooth style");
  assert.equal(await page.locator(tooltip).evaluate((element) => getComputedStyle(element).animationName), "motion-fade-in", "the tooltip fades in");
  await moveAway(page);
  await page.waitForFunction(() => document.querySelector(".hover-tooltip[data-exit-ghost]") !== null, null, { timeout: 2000 })
    .catch(() => assert.fail("the closing tooltip leaves no exit ghost"));
  const ghost = await page.locator(".hover-tooltip[data-exit-ghost]").evaluate((element) => ({
    hidden: element.getAttribute("aria-hidden"), inert: element.inert, pointer: getComputedStyle(element).pointerEvents
  }));
  assert.deepEqual(ghost, { hidden: "true", inert: true, pointer: "none" }, "the exit ghost is only a picture");

  // 标题栏开机时长的问号（1280 宽）：说明在问号下方（标题栏在窗口顶上，上方放不下），整个在窗口里。
  const uptimeHint = page.locator(".shell-uptime .help-hint");
  await hoverCenter(page, uptimeHint);
  const uptimePopover = page.locator(`.help-hint-popover${NOT_GHOST}`);
  await uptimePopover.waitFor();
  assert.equal((await uptimePopover.textContent()).trim(), app.shell.uptimeTitle, "the uptime explanation");
  assert.equal(await uptimePopover.getAttribute("data-placement"), "below", "the uptime explanation flips below the title bar");
  await moveAway(page);
  await uptimePopover.waitFor({ state: "detached" });

  // 5. 九种语言、最小窗口。
  await page.setViewportSize({ width: 744, height: 520 });
  for (const language of languages) {
    await reloadWith(page, (value) => { value.appearance.language = language; value.appearance.animationStyle = "none"; });
    await page.waitForFunction((expected) => document.documentElement.lang === expected, language);
    app = await loadAppCopy(language);
    await moveAway(page);
    // 右上角任务中心按钮：名称提示（任务列表不正常时是它的状态）。
    const taskButton = page.locator(".shell-task-button");
    await hoverCenter(page, taskButton);
    await waitTooltip(page, (await taskButton.getAttribute("data-full-content")) ?? await taskButton.getAttribute("aria-label"), `${language} task center button`);
    assertPlaced(await tooltipState(page, taskButton), `${language} task center button`);
    // 对话框关闭按钮：名称提示是这种语言的。
    await taskButton.click();
    const close = page.getByRole("dialog", { name: app.taskCenter.title, exact: true }).getByRole("button", { name: app.misc.close, exact: true });
    await close.waitFor();
    await hoverCenter(page, close);
    await waitTooltip(page, app.misc.close, `${language} dialog close button`);
    assertPlaced(await tooltipState(page, close), `${language} dialog close button`);
    await page.keyboard.press("Escape");
    await page.keyboard.press("Escape");
    await page.getByRole("dialog", { name: app.taskCenter.title, exact: true }).waitFor({ state: "detached" });
    // 资源条读数后面的问号：名称和说明是这种语言的，说明整个在窗口里、不盖住问号。
    // （标题栏开机时长的问号在这么窄的窗口里随标题区一起被页签挤出可见范围，原来的原生 title 也一样；它在 1280 宽下验。）
    const barHint = page.locator(`.resource-breakdown-header .help-hint${NOT_GHOST}`).first();
    assert.equal(await barHint.getAttribute("aria-label"), app.common.helpHint, `${language}: the question mark is named in this language`);
    await hoverCenter(page, barHint);
    const popover = page.locator(`.help-hint-popover${NOT_GHOST}`);
    await popover.waitFor();
    const explanation = await popover.innerText();
    assert.ok(explanation.includes(app.resourceBreakdown.scaleCapacityHint) || explanation.includes(app.resourceBreakdown.scaleActiveHint),
      `${language}: the bar explanation is in this language: ${JSON.stringify(explanation)}`);
    const layout = await barHint.evaluate((element) => {
      const button = element.getBoundingClientRect();
      const layer = document.querySelector(".help-hint-popover:not([data-exit-ghost])").getBoundingClientRect();
      return { button: { top: button.top, bottom: button.bottom }, layer: { left: layer.left, right: layer.right, top: layer.top, bottom: layer.bottom },
        width: document.documentElement.clientWidth, height: document.documentElement.clientHeight };
    });
    assert.ok(layout.layer.left >= 0 && layout.layer.right <= layout.width && layout.layer.top >= 0 && layout.layer.bottom <= layout.height
      && (layout.layer.bottom <= layout.button.top || layout.layer.top >= layout.button.bottom),
      `${language}: the bar explanation leaves the window or covers the question mark ${JSON.stringify(layout)}`);
    await moveAway(page);
    await popover.waitFor({ state: "detached" });
  }

  // 6. 截图：简体中文和英文、浅色和深色。
  await page.setViewportSize({ width: 1280, height: 900 });
  for (const language of ["zh-CN", "en-US"]) {
    for (const theme of ["light", "dark"]) {
      await reloadWith(page, (value) => { value.appearance.language = language; value.appearance.theme = theme; value.appearance.animationStyle = "none"; });
      app = await loadAppCopy(language);
      const tag = `${language}-${theme}`;
      await moveAway(page);
      await page.locator(`.resource-table-panel .resource-table-row${NOT_GHOST}`).first().waitFor({ timeout: 30_000 });
      // 完整内容提示：监视卡片的指标名。
      const label = page.locator(`.dashboard .metric-label[data-full-content]${NOT_GHOST}`).first();
      await hoverCenter(page, label);
      await waitTooltip(page, await label.getAttribute("data-full-content"), `${tag} metric name`);
      await shotAround(page, join(shots, `full-content-${tag}.png`), label, tooltip);
      // 问号说明：资源条读数后面的统计口径和刻度含义。
      const barHint = page.locator(`.resource-breakdown-header .help-hint${NOT_GHOST}`).first();
      await hoverCenter(page, barHint);
      await page.locator(`.help-hint-popover${NOT_GHOST}`).waitFor();
      await shotAround(page, join(shots, `help-hint-${tag}.png`), barHint, ".help-hint-popover:not([data-exit-ghost])");
      await moveAway(page);
      // 右键菜单里不能用的项的原因。
      await page.locator(".resource-table-panel [data-focus-key^='resource-row-actions:']:visible").first().click();
      const contextMenu = page.locator(`.standard-context-menu${NOT_GHOST}`);
      await contextMenu.waitFor();
      await contextMenu.screenshot({ path: join(shots, `context-menu-reason-${tag}.png`) });
      await page.keyboard.press("Escape");
      // 名称提示：组件页的图标按钮。
      await openComponents(page, app);
      const iconButton = page.locator(`.management-settings-button[data-name-tooltip]${NOT_GHOST}`).first();
      await iconButton.waitFor({ timeout: 15_000 });
      await hoverCenter(page, iconButton);
      await waitTooltip(page, await iconButton.getAttribute("aria-label"), `${tag} icon button`);
      await shotAround(page, join(shots, `name-tooltip-${tag}.png`), iconButton, tooltip);
      await moveAway(page);
      // 选项说明：设置 → 更新与版本，更新方式旁的问号，每项一段。
      await openPage(page, app, "settings");
      const settingsNav = page.locator(".settings-nav .settings-nav-item");
      for (let index = 0; index < await settingsNav.count(); index += 1) {
        await settingsNav.nth(index).click();
        const optionsHint = page.locator(`.settings-readonly-boundary${NOT_GHOST} .with-help-hint > .help-hint`).first();
        if (await optionsHint.count() === 0) continue;
        await hoverCenter(page, optionsHint);
        await page.locator(`.help-hint-popover${NOT_GHOST} .help-hint-entry`).first().waitFor();
        await shotAround(page, join(shots, `options-hint-${tag}.png`), optionsHint, ".help-hint-popover:not([data-exit-ghost])");
        await moveAway(page);
        break;
      }
      await openPage(page, app, "monitor");
    }
  }

  assert.deepEqual(failures, [], "no page errors");
  return { languages, screenshots: shots };
}
