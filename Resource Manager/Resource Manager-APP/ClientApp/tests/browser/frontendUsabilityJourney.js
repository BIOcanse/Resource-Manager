export default async function frontendUsabilityJourney(page, baseUrl = "http://127.0.0.1:4177") {
  const failures = [];
  page.on("pageerror", (error) => failures.push(String(error)));

  const assert = (condition, message) => {
    if (!condition) {
      throw new Error(message);
    }
  };
  const setScenario = async (patch) => {
    const result = await page.evaluate(async (value) => {
      const response = await fetch("/__test/scenario", {
        method: "POST",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify(value)
      });
      return { ok: response.ok, status: response.status, body: await response.text() };
    }, patch);
    assert(result.ok, `Unable to set frontend scenario: ${JSON.stringify(result)}`);
  };
  await page.setViewportSize({ width: 800, height: 500 });
  await page.goto(`${baseUrl}/`, { waitUntil: "domcontentloaded" });
  await setScenario({ capabilities: "ready", settings: "ready" });
  await page.reload({ waitUntil: "domcontentloaded" });
  await page.evaluate(() => {
    window.__resourceManagerFocusTrace = [];
    const recordFocus = (event) => {
      const target = event.target instanceof HTMLElement ? event.target : null;
      window.__resourceManagerFocusTrace.push({
        type: event.type,
        at: performance.now(),
        tag: target?.tagName ?? null,
        role: target?.getAttribute("role") ?? null,
        label: target?.getAttribute("aria-label") ?? null,
        focusKey: target?.dataset.focusKey ?? null,
        connected: target?.isConnected ?? null,
        relatedTag: event.relatedTarget instanceof HTMLElement ? event.relatedTarget.tagName : null,
        relatedRole: event.relatedTarget instanceof HTMLElement ? event.relatedTarget.getAttribute("role") : null
      });
      if (window.__resourceManagerFocusTrace.length > 40) {
        window.__resourceManagerFocusTrace.shift();
      }
    };
    document.addEventListener("focusin", recordFocus, true);
    document.addEventListener("focusout", recordFocus, true);
    const recordKey = (event) => {
      if (!["Escape", "ContextMenu", "F10", "ArrowDown", "ArrowUp"].includes(event.key)) {
        return;
      }
      window.__resourceManagerFocusTrace.push({
        type: event.type,
        at: performance.now(),
        key: event.key,
        defaultPrevented: event.defaultPrevented,
        activeTag: document.activeElement?.tagName ?? null,
        activeRole: document.activeElement?.getAttribute("role") ?? null,
        activeFocusKey: document.activeElement?.getAttribute("data-focus-key") ?? null
      });
    };
    document.addEventListener("keydown", recordKey, false);
    document.addEventListener("keyup", recordKey, false);
  });

  const rowAction = page.getByRole("button", { name: "更多操作：示例软件" }).first();
  await rowAction.waitFor({ state: "visible", timeout: 7000 });
  await rowAction.evaluate((element) => {
    window.__resourceManagerOriginalMenuNodes = {
      opener: element,
      cell: element.closest("[role='cell']"),
      row: element.closest("[role='row']"),
      frame: element.closest(".resource-table-frame"),
      panel: element.closest(".resource-table-panel"),
      workRegion: element.closest(".monitor-work-region")
    };
  });
  await rowAction.focus();
  const openerKey = await rowAction.getAttribute("data-focus-key");
  await page.keyboard.press("Shift+F10");

  const menu = page.getByRole("menu");
  await menu.waitFor({ state: "visible", timeout: 2000 });
  await page.waitForFunction(() => document.activeElement?.getAttribute("role") === "menuitem", null, { timeout: 2000 });
  const firstMenuFocus = await page.evaluate(() => ({
    role: document.activeElement?.getAttribute("role"),
    text: document.activeElement?.textContent?.trim()
  }));
  assert(firstMenuFocus.role === "menuitem", `Context menu did not focus a menu item: ${JSON.stringify(firstMenuFocus)}`);
  await page.keyboard.press("ArrowDown");
  await page.waitForFunction((previous) => document.activeElement?.textContent?.trim() !== previous,
    firstMenuFocus.text, { timeout: 2000 });
  const secondMenuFocus = await page.evaluate(() => document.activeElement?.textContent?.trim());
  assert(secondMenuFocus && secondMenuFocus !== firstMenuFocus.text,
    `ArrowDown did not move context-menu focus: ${firstMenuFocus.text} -> ${secondMenuFocus}`);
  await page.keyboard.press("Escape");
  await menu.waitFor({ state: "hidden", timeout: 2000 });
  try {
    await page.waitForFunction((focusKey) =>
      document.activeElement?.getAttribute("data-focus-key") === focusKey,
    openerKey, { timeout: 2000 });
  } catch (error) {
    const diagnostic = await collectFocusRestoreDiagnostic(page, openerKey);
    throw new Error(
      `Context-menu focus restoration timed out: ${JSON.stringify(diagnostic)}`,
      { cause: error });
  }
  const restoredFocus = await page.evaluate((focusKey) => ({
    key: document.activeElement?.getAttribute("data-focus-key"),
    sameNode: document.activeElement === window.__resourceManagerOriginalMenuNodes?.opener,
    activeTag: document.activeElement?.tagName,
    activeLabel: document.activeElement?.getAttribute("aria-label"),
    matchingOpeners: focusKey
      ? document.querySelectorAll(`[data-focus-key="${CSS.escape(focusKey)}"]`).length
      : 0,
    openerState: focusKey
      ? (() => {
          const element = document.querySelector(`[data-focus-key="${CSS.escape(focusKey)}"]`);
          return element instanceof HTMLElement
            ? {
                connected: element.isConnected,
                inert: element.inert,
                disabled: element instanceof HTMLButtonElement ? element.disabled : null,
                tabIndex: element.tabIndex,
                rectCount: element.getClientRects().length
              }
            : null;
        })()
      : null
  }), openerKey);
  assert(restoredFocus.key === openerKey,
    `Context menu did not restore its opener: ${JSON.stringify(restoredFocus)} != ${openerKey}`);
  assert(restoredFocus.sameNode,
    `Resource-table query handoff replaced the menu opener: ${JSON.stringify(restoredFocus)}`);

  const dashboardEdit = page.getByRole("button", { name: "编辑监控面板布局", exact: true });
  await dashboardEdit.click();
  const metricChange = page.getByRole("button", { name: /^更换第 1 张卡片主指标/ });
  await metricChange.waitFor({ state: "visible", timeout: 3000 });
  await metricChange.click();
  const metricDialog = page.getByRole("dialog", { name: "选择监控项" });
  await metricDialog.waitFor({ state: "visible", timeout: 3000 });
  const metricDialogGeometry = await metricDialog.evaluate((element) => {
    const dialog = element.getBoundingClientRect();
    const body = element.querySelector(".dialog-body")?.getBoundingClientRect();
    const actions = element.querySelector(".modal-actions")?.getBoundingClientRect();
    return {
      viewport: { width: innerWidth, height: innerHeight },
      dialog: { top: dialog.top, right: dialog.right, bottom: dialog.bottom, left: dialog.left },
      body: body ? { top: body.top, bottom: body.bottom } : null,
      actions: actions ? { top: actions.top, bottom: actions.bottom } : null
    };
  });
  assert(metricDialogGeometry.body
    && metricDialogGeometry.actions
    && metricDialogGeometry.dialog.top >= -1
    && metricDialogGeometry.dialog.left >= -1
    && metricDialogGeometry.dialog.right <= metricDialogGeometry.viewport.width + 1
    && metricDialogGeometry.dialog.bottom <= metricDialogGeometry.viewport.height + 1
    && metricDialogGeometry.body.bottom <= metricDialogGeometry.actions.top + 1
    && metricDialogGeometry.actions.bottom <= metricDialogGeometry.dialog.bottom + 1,
  `Metric dialog escaped its 800x500 surface: ${JSON.stringify(metricDialogGeometry)}`);
  await metricDialog.getByRole("button", { name: "取消", exact: true }).click();
  await metricDialog.waitFor({ state: "hidden", timeout: 3000 });
  await page.getByRole("button", { name: "取消监控面板布局编辑", exact: true }).click();

  await page.getByRole("button", { name: "组件与软件" }).click();
  const managementNavigation = page.getByRole("navigation", { name: "组件与软件分类" });
  await managementNavigation.waitFor({ state: "visible", timeout: 5000 });
  const generalAppsButton = managementNavigation.getByRole("button", { name: /一般应用/ });
  await generalAppsButton.click();
  const settingsButton = page.getByRole("button", { name: "设置和迁移" }).first();
  await settingsButton.waitFor({ state: "visible", timeout: 5000 });
  await settingsButton.focus();
  await settingsButton.click();

  const detailDialog = page.getByRole("dialog", { name: "示例软件" });
  await detailDialog.waitFor({ state: "visible", timeout: 5000 });
  const initialDialogFocus = await page.evaluate(() => ({
    label: document.activeElement?.getAttribute("aria-label"),
    insideDialog: Boolean(document.activeElement?.closest("[role='dialog']"))
  }));
  assert(initialDialogFocus.insideDialog && initialDialogFocus.label === "关闭",
    `Dialog did not focus its close button: ${JSON.stringify(initialDialogFocus)}`);
  for (const key of ["Tab", "Tab", "Shift+Tab", "Shift+Tab"]) {
    await page.keyboard.press(key);
    const focusOwnedByDialog = await page.evaluate(() => Boolean(
      document.activeElement?.closest("[role='dialog'], [data-modal-focus-portal='true']")));
    assert(focusOwnedByDialog, `Dialog focus escaped after ${key}`);
  }
  await page.keyboard.press("Escape");
  await detailDialog.waitFor({ state: "hidden", timeout: 3000 });
  const dialogFocusRestored = await page.evaluate(() => document.activeElement?.getAttribute("aria-label"));
  assert(dialogFocusRestored === "设置和迁移",
    `Dialog did not restore its opener: ${dialogFocusRestored}`);

  await page.getByRole("button", { name: "设置", exact: true }).click();
  const settingsBoundary = page.locator(".settings-readonly-boundary");
  await settingsBoundary.waitFor({ state: "visible", timeout: 5000 });
  assert(await settingsBoundary.isEnabled(), "Ready settings boundary is unexpectedly disabled");

  const selectTrigger = page.locator(".standard-select-trigger").first();
  await selectTrigger.waitFor({ state: "visible", timeout: 3000 });
  await selectTrigger.click();
  const listbox = page.getByRole("listbox").last();
  await listbox.waitFor({ state: "visible", timeout: 2000 });
  const sequentialOptionCount = await listbox.locator("[role='option'][tabindex='0']").count();
  assert(sequentialOptionCount <= 1,
    `StandardSelect exposes multiple sequential options: ${sequentialOptionCount}`);
  await page.keyboard.press("Tab");
  await listbox.waitFor({ state: "hidden", timeout: 2000 });
  const focusedOptionAfterTab = await page.evaluate(() => document.activeElement?.getAttribute("role") === "option");
  assert(!focusedOptionAfterTab, "StandardSelect left focus on a removed option after Tab");

  await setScenario({ settings: "error" });
  await page.getByRole("button", { name: "重新加载" }).first().click();
  const staleNotice = page.locator("#settingsStaleNotice");
  await staleNotice.waitFor({ state: "visible", timeout: 5000 });
  assert(await settingsBoundary.isDisabled(), "Stale settings remain editable");
  const enabledSettingsNav = await page.locator(".settings-nav button:not([disabled])").count();
  assert(enabledSettingsNav > 0, "Stale settings navigation is unavailable");
  assert((await settingsBoundary.textContent())?.trim(), "Stale settings did not retain last-good content");
  await setScenario({ settings: "ready" });
  await staleNotice.getByRole("button", { name: "重试" }).click();
  await staleNotice.waitFor({ state: "hidden", timeout: 5000 });
  await page.waitForFunction(() => {
    const boundary = document.querySelector(".settings-readonly-boundary");
    return boundary instanceof HTMLFieldSetElement && !boundary.disabled;
  }, null, { timeout: 5000 });
  assert(await settingsBoundary.isEnabled(), "Recovered settings boundary is still disabled");

  await page.setViewportSize({ width: 760, height: 500 });
  const narrowLayout = await page.evaluate(() => {
    const drag = document.querySelector(".window-drag-region");
    const actions = document.querySelector(".window-actions");
    if (!drag || !actions) return null;
    const dragRect = drag.getBoundingClientRect();
    const actionsRect = actions.getBoundingClientRect();
    return {
      display: getComputedStyle(drag).display,
      drag: { left: dragRect.left, right: dragRect.right, width: dragRect.width },
      actions: { left: actionsRect.left, right: actionsRect.right },
      document: {
        clientWidth: document.documentElement.clientWidth,
        scrollWidth: document.documentElement.scrollWidth
      }
    };
  });
  assert(narrowLayout, "Narrow layout is missing window chrome");
  assert(narrowLayout.display !== "none" && narrowLayout.drag.width >= 31,
    `Narrow drag surface is unavailable: ${JSON.stringify(narrowLayout)}`);
  assert(narrowLayout.drag.right <= narrowLayout.actions.left + 0.5,
    `Narrow drag surface overlaps window actions: ${JSON.stringify(narrowLayout)}`);
  assert(narrowLayout.document.scrollWidth <= narrowLayout.document.clientWidth,
    `Narrow layout leaks document overflow: ${JSON.stringify(narrowLayout.document)}`);

  await setScenario({ capabilities: "hang", settings: "ready" });
  const reloadStartedAt = Date.now();
  await page.reload({ waitUntil: "domcontentloaded" });
  const independentRow = page.getByRole("button", { name: "更多操作：示例软件" }).first();
  await independentRow.waitFor({ state: "visible", timeout: 3500 });
  const independentReadyMs = Date.now() - reloadStartedAt;
  assert(independentReadyMs < 3500,
    `Capability request blocked independent monitoring reads for ${independentReadyMs}ms`);

  const capabilityError = page.locator(".observation-state.error").filter({ hasText: "运行能力暂不可用" });
  await capabilityError.waitFor({ state: "visible", timeout: 13000 });
  await setScenario({ capabilities: "ready" });
  await capabilityError.getByRole("button", { name: "重试" }).click();
  await capabilityError.waitFor({ state: "hidden", timeout: 5000 });
  await independentRow.waitFor({ state: "visible", timeout: 2000 });

  assert(failures.length === 0, `Browser page errors: ${JSON.stringify(failures)}`);
  return {
    contextMenuKeyboard: true,
    metricDialogGeometry,
    dialogFocusLifecycle: true,
    selectTabLifecycle: true,
    settingsLastGoodRecovery: true,
    narrowDragSurface: narrowLayout.drag,
    independentReadReadyMs: independentReadyMs,
    capabilityTimeoutRecovery: true,
    pageErrors: failures
  };
}

async function collectFocusRestoreDiagnostic(page, focusKey) {
  return page.evaluate((key) => {
    const opener = key
      ? document.querySelector(`[data-focus-key="${CSS.escape(key)}"]`)
      : null;
    return {
      key,
      activeTag: document.activeElement?.tagName,
      activeRole: document.activeElement?.getAttribute("role"),
      activeLabel: document.activeElement?.getAttribute("aria-label"),
      activeFocusKey: document.activeElement?.getAttribute("data-focus-key"),
      focusTrace: window.__resourceManagerFocusTrace ?? [],
      matchingOpeners: key
        ? document.querySelectorAll(`[data-focus-key="${CSS.escape(key)}"]`).length
        : 0,
      opener: opener instanceof HTMLElement
        ? {
            sameNode: opener === window.__resourceManagerOriginalMenuNodes?.opener,
            originalConnected: window.__resourceManagerOriginalMenuNodes?.opener?.isConnected ?? null,
            identity: Object.fromEntries(Object.entries(window.__resourceManagerOriginalMenuNodes ?? {})
              .map(([name, original]) => {
                const current = name === "opener"
                  ? opener
                  : opener.closest(name === "cell"
                    ? "[role='cell']"
                    : name === "row"
                      ? "[role='row']"
                      : name === "frame"
                        ? ".resource-table-frame"
                        : name === "panel"
                          ? ".resource-table-panel"
                          : ".monitor-work-region");
                return [name, {
                  sameNode: current === original,
                  originalConnected: original?.isConnected ?? null,
                  currentConnected: current?.isConnected ?? null
                }];
              })),
            connected: opener.isConnected,
            inert: opener.inert,
            inertAncestor: Boolean(opener.closest("[inert]")),
            disabled: opener instanceof HTMLButtonElement ? opener.disabled : null,
            tabIndex: opener.tabIndex,
            rectCount: opener.getClientRects().length,
            manualFocusSucceeded: (() => {
              opener.focus({ preventScroll: true });
              return document.activeElement === opener;
            })()
          }
        : null
    };
  }, focusKey);
}
