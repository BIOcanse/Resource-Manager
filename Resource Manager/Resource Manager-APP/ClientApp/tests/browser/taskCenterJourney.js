export default async function taskCenterJourney(page, baseUrl = "http://127.0.0.1:4177") {
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
    const response = await fetch(`${baseUrl}/__test/scenario`, {
      method: "POST",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify(patch)
    });
    const result = { ok: response.ok, status: response.status };
    assert(result.ok, `Unable to set task-center scenario: ${JSON.stringify(result)}`);
  };
  const requestTelemetry = async () => {
    const response = await fetch(`${baseUrl}/__test/requests`);
    const telemetry = await response.json();
    return telemetry;
  };
  const requestCount = async (path) => (await requestTelemetry())[path]?.completed ?? 0;

  await page.setViewportSize({ width: 900, height: 640 });
  const resetResponse = await fetch(`${baseUrl}/__test/requests/reset`, { method: "POST" });
  assert(resetResponse.ok, `Unable to reset request telemetry: ${resetResponse.status}`);
  await setScenario({
    capabilities: "ready",
    runtimeEffectOwners: true,
    settings: "ready",
    debugMode: true,
    operations: "mixed",
    operationCanceled: false,
    animations: "normal",
    resourceBreakdownGeneration: 0
  });
  await page.goto(`${baseUrl}/`, { waitUntil: "domcontentloaded" });

  const taskButton = page.locator("#taskCenterButton");
  await taskButton.waitFor({ state: "visible", timeout: 7000 });
  await page.waitForFunction(() => document.querySelector("#taskCenterButton")
    ?.getAttribute("aria-label")?.includes("1 项正在进行") === true, null, { timeout: 7000 });
  assert((await taskButton.getAttribute("aria-label"))?.includes("1 项正在进行"),
    `Task Center active count is missing: ${await taskButton.getAttribute("aria-label")}`);
  await taskButton.focus();
  await taskButton.click();
  const dialog = page.getByRole("dialog", { name: "任务中心" });
  await dialog.waitFor({ state: "visible", timeout: 3000 });
  const initialFocus = await page.evaluate(() => ({
    label: document.activeElement?.getAttribute("aria-label"),
    inside: Boolean(document.activeElement?.closest("[role='dialog']"))
  }));
  assert(initialFocus.inside && initialFocus.label === "关闭",
    `Task Center initial focus is invalid: ${JSON.stringify(initialFocus)}`);

  const activeTab = dialog.getByRole("tab", { name: /进行中/ });
  await activeTab.focus();
  await page.keyboard.press("ArrowRight");
  const selectedAfterArrow = await dialog.locator("[role='tab'][aria-selected='true']").textContent();
  assert(selectedAfterArrow?.includes("历史"),
    `Task Center ArrowRight did not activate History: ${selectedAfterArrow}`);
  await page.keyboard.press("Home");
  const selectedAfterHome = await dialog.locator("[role='tab'][aria-selected='true']").textContent();
  assert(selectedAfterHome?.includes("进行中"),
    `Task Center Home did not activate Active: ${selectedAfterHome}`);

  await dialog.getByText("安装组件", { exact: true }).waitFor({ state: "visible" });
  await dialog.getByText("40%", { exact: true }).waitFor({ state: "visible" });
  await dialog.getByText("详情", { exact: true }).click();
  await dialog.getByText("000000000000000000000000000000a1", { exact: true })
    .waitFor({ state: "visible" });

  const cancelPath = "/api/operations/000000000000000000000000000000a1/cancel";
  assert(await requestCount(cancelPath) === 0, "Task was canceled before an explicit action.");
  await page.keyboard.press("Escape");
  await dialog.waitFor({ state: "hidden", timeout: 3000 });
  assert(await requestCount(cancelPath) === 0, "Closing Task Center canceled a task.");
  const restoredFocus = await page.evaluate(() => document.activeElement?.id);
  assert(restoredFocus === "taskCenterButton",
    `Task Center did not restore opener focus: ${restoredFocus}`);

  await taskButton.click();
  await dialog.waitFor({ state: "visible", timeout: 3000 });
  const cancelButton = dialog.getByRole("button", {
    name: "取消 安装前端测试组件（component.frontend-harness）"
  });
  await cancelButton.click();
  await page.waitForFunction(async (path) => {
    const response = await fetch("/__test/requests");
    const telemetry = await response.json();
    return (telemetry[path]?.completed ?? 0) === 1;
  }, cancelPath, { timeout: 4000 });
  await dialog.getByRole("tab", { name: /历史/ }).click();
  await dialog.getByText("已取消", { exact: true }).waitFor({ state: "visible", timeout: 3000 });

  await page.keyboard.press("Escape");
  await dialog.waitFor({ state: "hidden", timeout: 3000 });
  const resourceRequestBaseline = await requestCount("/api/resource-monitor/snapshot");
  await setScenario({ resourceBreakdownGeneration: 1 });
  await page.waitForFunction(() => {
    const item = [...document.querySelectorAll(".resource-breakdown-item")]
      .find((element) => element.querySelector("strong")?.textContent?.trim() === "处理器");
    return item?.querySelector(".resource-breakdown-header span")
      ?.textContent?.trim().startsWith("38%") === true;
  }, null, { timeout: 6000 });
  assert(await requestCount("/api/resource-monitor/snapshot") === resourceRequestBaseline,
    "Pushed resource update unexpectedly used a resource snapshot request.");
  await taskButton.click();
  await dialog.getByRole("tab", { name: /诊断/ }).click();
  await dialog.getByText("资源布局过渡", { exact: true }).first()
    .waitFor({ state: "visible", timeout: 2000 });

  await page.keyboard.press("Escape");
  await dialog.waitFor({ state: "hidden", timeout: 3000 });

  await setScenario({ operations: "many", operationCanceled: false });
  await page.setViewportSize({ width: 900, height: 640 });
  await taskButton.click();
  await dialog.waitFor({ state: "visible", timeout: 3000 });
  await dialog.getByRole("tab", { name: /历史/ }).click();
  await dialog.getByText("已完成前端测试组件 24", { exact: true })
    .waitFor({ state: "attached", timeout: 3000 });
  const longListGeometry = await dialogGeometry(page, dialog);
  assertDialogContained(longListGeometry, "long-history Task Center");
  assert(longListGeometry.panel
    && longListGeometry.panel.scrollHeight > longListGeometry.panel.clientHeight,
  `Long Task Center history does not use its owned scroll region: ${JSON.stringify(longListGeometry)}`);
  await page.keyboard.press("Escape");
  await dialog.waitFor({ state: "hidden", timeout: 3000 });

  await setScenario({ operations: "empty", operationCanceled: false });
  await page.setViewportSize({ width: 1280, height: 720 });
  await taskButton.click();
  await dialog.waitFor({ state: "visible", timeout: 3000 });
  await dialog.getByText("此筛选下没有任务", { exact: true }).first()
    .waitFor({ state: "visible", timeout: 3000 });
  const emptyDesktopGeometry = await dialogGeometry(page, dialog);
  assert(emptyDesktopGeometry.dialog.height < 430,
    `Empty Task Center is not content-sized: ${JSON.stringify(emptyDesktopGeometry)}`);
  assertDialogContained(emptyDesktopGeometry, "desktop empty Task Center");
  await page.setViewportSize({ width: 800, height: 500 });
  const emptyNarrowGeometry = await dialogGeometry(page, dialog);
  assertDialogContained(emptyNarrowGeometry, "narrow empty Task Center");
  await page.keyboard.press("Escape");
  await dialog.waitFor({ state: "hidden", timeout: 3000 });

  assert(pageErrors.length === 0 && consoleErrors.length === 0,
    `Task Center browser errors: ${JSON.stringify({ pageErrors, consoleErrors })}`);
  const operationTelemetry = await requestTelemetry();
  const physicalChannel = operationTelemetry["/api/subscriptions/stream"];
  assert(physicalChannel?.physicalInFlight === 1
    && physicalChannel.physicalMaxInFlight === 1
    && physicalChannel.logicalInFlight === 0,
  `Frontend did not retain one physical callback channel: ${JSON.stringify(physicalChannel)}`);
  assert((operationTelemetry["/api/operations/subscribe"]?.logicalInFlight ?? 0) === 1
    && (operationTelemetry["/api/operations/subscribe"]?.physicalStarted ?? 0) === 0,
    `Operation logical subscription is not active: ${JSON.stringify(operationTelemetry["/api/operations/subscribe"])}`);
  assert((operationTelemetry["/api/operations"]?.started ?? 0) === 0,
    `Task Center issued a direct operation GET: ${JSON.stringify(operationTelemetry["/api/operations"])}`);
  return {
    initialFocus,
    selectedAfterArrow,
    selectedAfterHome,
    cancelRequests: await requestCount(cancelPath),
    hiddenTransitionVisibleInDiagnostics: true,
    longListGeometry,
    emptyDesktopGeometry,
    emptyNarrowGeometry,
    operationLogicalStarts: operationTelemetry["/api/operations/subscribe"].logicalStarted,
    physicalChannelStarted: physicalChannel.physicalStarted,
    operationDirectGets: operationTelemetry["/api/operations"]?.started ?? 0,
    pageErrors,
    consoleErrors
  };
}

async function dialogGeometry(page, dialog) {
  return dialog.evaluate((element) => {
    const rect = element.getBoundingClientRect();
    const header = element.querySelector(".modal-header")?.getBoundingClientRect();
    const actions = element.querySelector(".modal-actions")?.getBoundingClientRect();
    const panel = element.querySelector(".task-center-panel:not([hidden])");
    return {
      viewport: { width: window.innerWidth, height: window.innerHeight },
      dialog: {
        top: rect.top,
        right: rect.right,
        bottom: rect.bottom,
        left: rect.left,
        width: rect.width,
        height: rect.height,
        clientWidth: element.clientWidth,
        scrollWidth: element.scrollWidth
      },
      header: header ? { top: header.top, bottom: header.bottom } : null,
      actions: actions ? { top: actions.top, bottom: actions.bottom } : null,
      panel: panel
        ? { clientHeight: panel.clientHeight, scrollHeight: panel.scrollHeight }
        : null
    };
  });
}

function assertDialogContained(geometry, label) {
  const epsilon = 1;
  if (!geometry.header
    || !geometry.actions
    || geometry.dialog.top < -epsilon
    || geometry.dialog.left < -epsilon
    || geometry.dialog.right > geometry.viewport.width + epsilon
    || geometry.dialog.bottom > geometry.viewport.height + epsilon
    || geometry.header.top < geometry.dialog.top - epsilon
    || geometry.actions.bottom > geometry.dialog.bottom + epsilon
    || geometry.dialog.scrollWidth > geometry.dialog.clientWidth + epsilon) {
    throw new Error(`${label} escaped its viewport or surface: ${JSON.stringify(geometry)}`);
  }
}
