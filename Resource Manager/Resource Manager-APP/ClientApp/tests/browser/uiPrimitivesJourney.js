export default async function uiPrimitivesJourney(page, baseUrl = "http://127.0.0.1:4177") {
  const pageErrors = [];
  const consoleErrors = [];
  page.on("pageerror", (error) => pageErrors.push(String(error)));
  page.on("console", (message) => {
    if (message.type() === "error") {
      consoleErrors.push(message.text());
    }
  });

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
      return { ok: response.ok, status: response.status };
    }, patch);
    assert(result.ok, `Unable to set UI primitive scenario: ${JSON.stringify(result)}`);
  };

  await page.setViewportSize({ width: 900, height: 640 });
  await page.goto(`${baseUrl}/`, { waitUntil: "domcontentloaded" });
  await setScenario({ capabilities: "ready", settings: "ready" });
  await page.reload({ waitUntil: "domcontentloaded" });

  const pageNavigation = page.getByRole("navigation", { name: "页面切换" });
  await pageNavigation.getByRole("button", { name: "详细信息", exact: true }).click();
  await assertRouteEntry(page, "详细信息");
  const detailsTabs = page.getByRole("tablist", { name: "详细信息分页" });
  await detailsTabs.waitFor({ state: "visible", timeout: 5000 });
  const deviceTab = detailsTabs.getByRole("tab", { name: "设备管理", exact: true });
  await deviceTab.focus();
  await page.keyboard.press("ArrowRight");
  const selectedAfterArrow = (await detailsTabs.locator("[role='tab'][aria-selected='true']")
    .textContent())?.trim();
  assert(selectedAfterArrow === "GPU 调度",
    `Details tabs did not activate the next item: ${selectedAfterArrow}`);
  await page.getByRole("tabpanel", { name: "GPU 调度" })
    .waitFor({ state: "visible", timeout: 3000 });
  await page.keyboard.press("Home");
  const selectedAfterHome = (await detailsTabs.locator("[role='tab'][aria-selected='true']")
    .textContent())?.trim();
  assert(selectedAfterHome === "设备管理",
    `Details tabs did not return to the first item: ${selectedAfterHome}`);

  const deviceScope = page.getByRole("radiogroup", { name: "设备管理分类" });
  await deviceScope.waitFor({ state: "visible", timeout: 5000 });
  const externalScope = deviceScope.getByRole("radio", { name: "外部接口", exact: true });
  await externalScope.focus();
  await page.keyboard.press("ArrowRight");
  const internalScope = deviceScope.getByRole("radio", { name: "内部接口", exact: true });
  assert(await internalScope.getAttribute("aria-checked") === "true",
    "Device scope did not activate the next radio item.");
  await page.getByLabel("内部接口清单").waitFor({ state: "visible", timeout: 3000 });
  assert(await deviceScope.locator("[role='radio'][tabindex='0']").count() === 1,
    "Device scope does not expose exactly one keyboard tab stop.");

  await detailsTabs.getByRole("tab", { name: "CPU 拓扑", exact: true }).click();
  const cpuPanel = page.getByLabel("CPU 拓扑模型");
  await cpuPanel.getByText("Frontend Harness CPU", { exact: true })
    .waitFor({ state: "visible", timeout: 65000 });
  const ccdButton = cpuPanel.getByRole("button", { name: /CCD 0/ });
  assert(await ccdButton.getAttribute("aria-pressed") === "false",
    "CCD selection state is missing.");
  const coreButton = cpuPanel.getByRole("button", { name: /^核心 0，使用率/ });
  assert(await coreButton.locator("input").count() === 0,
    "CPU score input is nested inside the core selection button.");
  await coreButton.focus();
  await page.keyboard.press("Space");
  assert(await coreButton.getAttribute("aria-pressed") === "true",
    "Space did not select the CPU core.");
  const coreFocus = await coreButton.evaluate((element) => {
    const style = getComputedStyle(element);
    return {
      focusVisible: element.matches(":focus-visible"),
      outlineStyle: style.outlineStyle,
      outlineWidth: style.outlineWidth
    };
  });
  assert(coreFocus.focusVisible
    && coreFocus.outlineStyle !== "none"
    && coreFocus.outlineWidth !== "0px",
  `CPU core keyboard focus is not visible: ${JSON.stringify(coreFocus)}`);
  await page.keyboard.press("Enter");
  assert(await coreButton.getAttribute("aria-pressed") === "false",
    "Enter did not clear the CPU core selection.");
  assert(await cpuPanel.getByRole("button", { name: /^逻辑处理器 3/ }).isDisabled(),
    "An unavailable logical processor remains selectable.");

  await pageNavigation.getByRole("button", { name: "组件与软件", exact: true }).click();
  await assertRouteEntry(page, "组件与软件");
  const managementNavigation = page.getByRole("navigation", { name: "组件和软件分类" });
  await managementNavigation.waitFor({ state: "visible", timeout: 5000 });
  await managementNavigation.getByRole("button", { name: /一般应用/ }).click();
  const managementSearch = page.getByRole("searchbox", { name: /一般应用搜索/ });
  await managementSearch.fill("不存在的前端测试条目");
  await page.getByText("没有与“不存在的前端测试条目”匹配的记录。", { exact: true })
    .waitFor({ state: "visible", timeout: 3000 });
  await page.getByRole("button", { name: "清除搜索", exact: true }).click();
  await page.getByText("示例软件", { exact: true }).first().waitFor({ state: "visible", timeout: 3000 });
  await managementSearch.fill("示例");
  await managementNavigation.getByRole("button", { name: /^游戏/ }).click();
  assert(await page.getByRole("searchbox", { name: /游戏搜索/ }).inputValue() === "",
    "Management search was not reset when the category changed.");
  await page.getByText("当前分类没有记录。", { exact: true })
    .waitFor({ state: "visible", timeout: 3000 });
  await managementNavigation.getByRole("button", { name: /一般应用/ }).click();
  assert(await page.getByRole("searchbox", { name: /一般应用搜索/ }).inputValue() === "",
    "Management search leaked back into a previous category.");
  const addButton = page.getByRole("button", { name: "添加", exact: true });
  await addButton.waitFor({ state: "visible", timeout: 3000 });
  await addButton.focus();
  await addButton.click();
  const manualDialog = page.getByRole("dialog", { name: "添加一般应用" });
  await manualDialog.waitFor({ state: "visible", timeout: 3000 });
  await page.locator(".modal-backdrop").click({ position: { x: 4, y: 4 } });
  assert(await manualDialog.isVisible(),
    "Manual software dialog discarded its draft on backdrop click.");
  await page.keyboard.press("Escape");
  await manualDialog.waitFor({ state: "hidden", timeout: 3000 });
  assert(await addButton.evaluate((element) => document.activeElement === element),
    "Manual software dialog did not restore focus to its opener.");

  await pageNavigation.getByRole("button", { name: "设置", exact: true }).click();
  await assertRouteEntry(page, "设置");
  const settingsNavigation = page.getByRole("navigation", { name: "设置分区" });
  await settingsNavigation.waitFor({ state: "visible", timeout: 3000 });
  await settingsNavigation.getByRole("button", { name: "外观", exact: true }).click();
  assert(await settingsNavigation.getAttribute("aria-label") === "设置分区",
    "Settings navigation changed its landmark name with the active section.");
  assert(await settingsNavigation.getByRole("button", { name: "外观", exact: true })
    .getAttribute("aria-current") === "page",
  "Settings navigation did not expose the current section.");

  assert(pageErrors.length === 0 && consoleErrors.length === 0,
    `UI primitive browser errors: ${JSON.stringify({ pageErrors, consoleErrors })}`);
  return {
    routeEntry: true,
    detailsTabs: { selectedAfterArrow, selectedAfterHome },
    deviceScope: "internal",
    cpuTopology: { selection: true, focus: coreFocus },
    managementSearch: true,
    settingsNavigation: true,
    manualDialog: { backdropPreserved: true, escapeDismissed: true, focusRestored: true },
    pageErrors,
    consoleErrors
  };
}

async function assertRouteEntry(page, title) {
  const heading = page.getByRole("heading", { level: 1, name: title, exact: true });
  await heading.waitFor({ state: "visible", timeout: 5000 });
  await page.waitForFunction((expectedTitle) => {
    const active = document.activeElement;
    return active?.id === "activePageTitle"
      && active.textContent?.trim() === expectedTitle
      && document.title.includes(expectedTitle);
  }, title, { timeout: 3000 });
}
