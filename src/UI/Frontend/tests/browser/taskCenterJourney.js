import { loadAppCopy } from "../../src/i18n/copy/appCopyLoader.ts";
import { waitForContentSettled } from "./contentSettled.js";
import { loadSettingsText } from "../../src/i18n/settingsLoader.ts";
import { languageOptions } from "../../src/i18n/settingsLanguages.ts";
import { seedFrontendPreferences } from "./frontendPreferencesFixture.js";
import { observeSubscriptionChannel } from "./subscriptionChannelProbe.js";

export default async function taskCenterJourney(page, baseUrl = "http://127.0.0.1:4177") {
  const channelProbe = await observeSubscriptionChannel(page);
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
    resourceBreakdownGeneration: 0
  });
  // 动画效果是前端偏好：首次加载前写入常规动画。
  await seedFrontendPreferences(page, (value) => { value.appearance.animationStyle = "desktop"; value.appearance.autoDisableAnimations = false; });
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
  // 关闭只有一处（标题栏的 ×，Esc 照样关）；对话框不另外描述，筛选分页带数量，不再重复一句“几项正在进行”。
  assert(await dialog.getByRole("button", { name: "关闭", exact: true }).count() === 1, "Task Center has more than one close control");
  assert(await dialog.getAttribute("aria-describedby") === null, "Task Center still has a describing summary");
  // 高度固定：换筛选分页、有无任务都不变（用户 10-25「高度不固定，手感差」）。入场（0.97 缩放）走完再量。
  await settleDialog(dialog);
  const filledHeight = (await dialogGeometry(page, dialog)).dialog.height;
  for (const name of [/历史/, /全部/, /进行中/]) {
    await dialog.getByRole("tab", { name }).click();
    const height = (await dialogGeometry(page, dialog)).dialog.height;
    assert(Math.abs(height - filledHeight) <= 1, `Task Center height changes with the tab: ${height} vs ${filledHeight}`);
  }
  await waitForContentSettled(page);

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
  await waitForContentSettled(page);

  await dialog.locator(".task-center-item-title-row strong").filter({ hasText: /^安装组件: 硬件支持组件$/ }).waitFor({ state: "visible" });
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
    name: "取消 安装组件: 硬件支持组件（000000a1）"
  });
  await cancelButton.click();
  await page.waitForFunction(async (path) => {
    const response = await fetch("/__test/requests");
    const telemetry = await response.json();
    return (telemetry[path]?.completed ?? 0) === 1;
  }, cancelPath, { timeout: 4000 });
  await dialog.getByRole("tab", { name: /历史/ }).click();
  await dialog.getByText("已取消", { exact: true }).waitFor({ state: "visible", timeout: 3000 });
  // The history item's native "详情" disclosure must be reachable with Tab inside the modal focus trap.
  const historySummary = dialog.getByRole("tabpanel", { name: /历史/ }).locator("details > summary").first();
  await historySummary.waitFor({ state: "visible", timeout: 3000 });
  await dialog.getByRole("tab", { name: /历史/ }).focus();
  let reachedSummary = false;
  for (let step = 0; step < 12 && !reachedSummary; step += 1) {
    await page.keyboard.press("Tab");
    reachedSummary = await historySummary.evaluate((element) => element === document.activeElement);
  }
  assert(reachedSummary, "Tab never reached the history item's details summary in Task Center");

  await page.keyboard.press("Escape");
  await dialog.waitFor({ state: "hidden", timeout: 3000 });
  const resourceRequestBaseline = await requestCount("/api/resource-monitor/snapshot");
  await taskButton.click();
  const diagnosticsTab = dialog.getByRole("tab", { name: /诊断/ });
  await diagnosticsTab.click();
  assert(await diagnosticsTab.getAttribute("aria-selected") === "true",
    "Task Center did not select the diagnostics tab");
  const diagnosticsPanel = dialog.getByRole("tabpanel", { name: /诊断/ });
  await diagnosticsPanel.waitFor({ state: "visible", timeout: 2000 });
  await diagnosticsPanel.evaluate((element) => {
    window.__resourceLayoutTaskSeen = element.textContent?.includes("资源布局过渡") ?? false;
    window.__resourceLayoutTaskObserver = new MutationObserver(() => {
      if (element.textContent?.includes("资源布局过渡")) {
        window.__resourceLayoutTaskSeen = true;
      }
    });
    window.__resourceLayoutTaskObserver.observe(element, { childList: true, subtree: true });
  });
  await setScenario({ resourceBreakdownGeneration: 1 });
  await page.waitForFunction(() => {
    const item = document.querySelector('.resource-bar-track[data-metric-id="cpu.usage"]')
      ?.closest(".resource-breakdown-item");
    const value = item?.querySelector(".resource-breakdown-header span")?.textContent?.trim();
    return /^38(?:\.0+)?%/.test(value ?? "");
  }, null, { timeout: 6000 });
  assert(await requestCount("/api/resource-monitor/snapshot") === resourceRequestBaseline,
    "Pushed resource update unexpectedly used a resource snapshot request.");
  await page.waitForFunction(() => window.__resourceLayoutTaskSeen === true, null, {
    timeout: 2000
  });
  await page.evaluate(() => {
    window.__resourceLayoutTaskObserver?.disconnect();
    delete window.__resourceLayoutTaskObserver;
    delete window.__resourceLayoutTaskSeen;
  });

  await page.keyboard.press("Escape");
  await dialog.waitFor({ state: "hidden", timeout: 3000 });

  await setScenario({ operations: "many", operationCanceled: false });
  await page.setViewportSize({ width: 900, height: 640 });
  await taskButton.click();
  await dialog.waitFor({ state: "visible", timeout: 3000 });
  await dialog.getByRole("tab", { name: /历史/ }).click();
  await dialog.locator(".task-center-item").nth(23)
    .waitFor({ state: "attached", timeout: 3000 });
  await waitForContentSettled(page);
  const longListGeometry = await dialogGeometry(page, dialog);
  assertDialogContained(longListGeometry, "long-history Task Center");
  assert(longListGeometry.panel
    && longListGeometry.panel.scrollHeight > longListGeometry.panel.clientHeight,
  `Long Task Center history does not use its owned scroll region: ${JSON.stringify(longListGeometry)}`);
  await page.keyboard.press("Escape");
  await dialog.waitFor({ state: "hidden", timeout: 3000 });

  await setScenario({ operations: "empty", operationCanceled: false });
  await taskButton.click();
  await dialog.waitFor({ state: "visible", timeout: 3000 });
  await dialog.getByText("此筛选下没有任务", { exact: true }).first()
    .waitFor({ state: "visible", timeout: 3000 });
  await settleDialog(dialog);
  // 高度只跟视口走，不跟内容走：和有任务时同一个视口（900×640）比。
  const emptyGeometry = await dialogGeometry(page, dialog);
  assert(Math.abs(emptyGeometry.dialog.height - filledHeight) <= 1,
    `Empty Task Center changes height: ${JSON.stringify(emptyGeometry)} vs ${filledHeight}`);
  assert(await dialog.getByText("此筛选下没有任务", { exact: true }).count() === 1, "The empty state is said more than once");
  await page.setViewportSize({ width: 1280, height: 720 });
  const emptyDesktopGeometry = await dialogGeometry(page, dialog);
  assertDialogContained(emptyDesktopGeometry, "desktop empty Task Center");
  await page.setViewportSize({ width: 800, height: 500 });
  const emptyNarrowGeometry = await dialogGeometry(page, dialog);
  assertDialogContained(emptyNarrowGeometry, "narrow empty Task Center");
  await page.keyboard.press("Escape");
  await dialog.waitFor({ state: "hidden", timeout: 3000 });

  // Reuse the already-loaded dialog module across all shipped languages: its
  // tabs, persisted operations and progress must follow the current language.
  await setScenario({ operations: "mixed", operationCanceled: false });
  let previousLanguage = "zh-CN";
  const checkedLanguages = [];
  let languageChannelStarted = null;
  for (const option of languageOptions.filter(option => option.id !== "system")) {
    const oldApp = await loadAppCopy(previousLanguage);
    const oldSettings = await loadSettingsText(previousLanguage);
    await page.getByRole("navigation").getByRole("button", { name: oldApp.page.settings, exact: true }).click();
    await page.getByRole("button", { name: oldSettings.sections.appearance, exact: true }).click();
    await page.getByRole("button", { name: oldSettings.appearance.languageSelectLabel, exact: true }).click();
    await page.getByRole("option", { name: `${option.nativeLabel} · ${option.label}`, exact: true }).click();
    if (option.id !== previousLanguage) {
      await page.getByRole("button", { name: oldSettings.actions.save, exact: true }).click();
    }
    await page.waitForFunction(language => document.documentElement.lang === language, option.id);
    const app = await loadAppCopy(option.id);
    const settings = await loadSettingsText(option.id);
    await page.getByRole("button", { name: settings.appearance.languageSelectLabel, exact: true }).waitFor();
    await taskButton.click();
    const localizedDialog = page.getByRole("dialog", { name: app.taskCenter.title, exact: true });
    await localizedDialog.waitFor();
    const tabLabels = await localizedDialog.getByRole("tab").allTextContents();
    for (const label of [app.taskCenter.tabActive, app.taskCenter.tabHistory, app.taskCenter.tabAll]) {
      assert(tabLabels.some(value => value.includes(label)), `${option.id}: stale task center tab ${label}`);
    }
    await localizedDialog.getByRole("tab").filter({ hasText: app.taskCenter.tabActive }).click();
    await localizedDialog.getByText(`${app.taskCenter.operation.componentInstall}: ${app.status.component.nameFallback}`, { exact: true }).waitFor();
    await localizedDialog.locator(".task-center-item details").first().evaluate(element => { element.open = true; });
    await localizedDialog.getByText(app.managementFeedback.stageDownload, { exact: true }).waitFor();
    const rendered = await localizedDialog.innerText();
    for (const prose of ["安装前端测试组件", "正在安装"]) {
      assert(!rendered.includes(prose), `${option.id}: backend prose leaked into Task Center: ${prose}`);
    }
    // The details name the operation's target; the backend's internal domain key is never shown.
    await localizedDialog.locator(".task-center-item details dd")
      .filter({ hasText: app.status.component.nameFallback }).first().waitFor();
    assert(!rendered.includes("component.frontend-harness"), `${option.id}: internal domain key shown in Task Center`);
    await localizedDialog.getByRole("tab").filter({ hasText: app.taskCenter.tabHistory }).click();
    await localizedDialog.getByText(app.managementFeedback.managedRootRemoved, { exact: true }).waitFor();
    await localizedDialog.getByText(app.managementFeedback.networkFailure, { exact: true }).waitFor();
    const historyText = await localizedDialog.innerText();
    assert(!historyText.includes("component:failed-runtime"), `${option.id}: internal domain key shown in Task Center history`);
    assert(!historyText.includes("fixture connection refused"), `${option.id}: diagnostic escaped as display copy`);
    checkedLanguages.push(option.id);
    previousLanguage = option.id;
    await page.keyboard.press("Escape");
    await localizedDialog.waitFor({ state: "hidden" });
    const channel = (await requestTelemetry())["/api/subscriptions/stream"];
    if (languageChannelStarted === null) languageChannelStarted = channel.physicalStarted;
    assert(channel.physicalStarted === languageChannelStarted,
      `${option.id}: an unchanged scheduling cadence restarted the callback channel: ${JSON.stringify(channel)}`);
  }

  assert(pageErrors.length === 0 && consoleErrors.length === 0,
    `Task Center browser errors: ${JSON.stringify({ pageErrors, consoleErrors })}`);
  const operationTelemetry = await requestTelemetry();
  const physicalChannel = operationTelemetry["/api/subscriptions/stream"];
  assert(physicalChannel?.physicalInFlight === 1
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
    checkedLanguages,
    operationLogicalStarts: operationTelemetry["/api/operations/subscribe"].logicalStarted,
    physicalChannelStarted: physicalChannel.physicalStarted,
    languageChannelStarted,
    browserChannel: await channelProbe.assertSinglePhysicalChannel(),
    operationDirectGets: operationTelemetry["/api/operations"]?.started ?? 0,
    pageErrors,
    consoleErrors
  };
}

/** 对话框和遮罩的入场动画都走完。 */
async function settleDialog(dialog) {
  // 入场由磁石逐帧画（src/interactions/solid/dialogEntrance.ts），getAnimations 看不见：等它到 1 时清掉内联的变换和 will-change。
  await dialog.evaluate((element) => new Promise((resolve, reject) => {
    const started = performance.now();
    const check = () => {
      if (!element.style.transform && !element.style.willChange) resolve();
      else if (performance.now() - started > 3000) reject(new Error(`dialog entrance did not settle: ${element.style.cssText}`));
      else requestAnimationFrame(check);
    };
    check();
  }));
  await dialog.evaluate((element) => Promise.all(element.getAnimations().map((animation) => animation.finished)));
}

async function dialogGeometry(page, dialog) {
  return dialog.evaluate((element) => {
    const rect = element.getBoundingClientRect();
    const header = element.querySelector(".modal-header")?.getBoundingClientRect();
    const body = element.querySelector(".task-center-body")?.getBoundingClientRect();
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
      body: body ? { top: body.top, bottom: body.bottom } : null,
      panel: panel
        ? { clientHeight: panel.clientHeight, scrollHeight: panel.scrollHeight }
        : null
    };
  });
}

function assertDialogContained(geometry, label) {
  const epsilon = 1;
  if (!geometry.header
    || !geometry.body
    || geometry.dialog.top < -epsilon
    || geometry.dialog.left < -epsilon
    || geometry.dialog.right > geometry.viewport.width + epsilon
    || geometry.dialog.bottom > geometry.viewport.height + epsilon
    || geometry.header.top < geometry.dialog.top - epsilon
    || geometry.body.bottom > geometry.dialog.bottom + epsilon
    || geometry.dialog.scrollWidth > geometry.dialog.clientWidth + epsilon) {
    throw new Error(`${label} escaped its viewport or surface: ${JSON.stringify(geometry)}`);
  }
}
