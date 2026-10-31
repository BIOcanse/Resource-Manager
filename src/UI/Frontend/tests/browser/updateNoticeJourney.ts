import type { Locator } from "playwright-core";
import type { AppUpdateMode, BackendAppSettings } from "../../src/types.ts";
import type { ProductUpdateState } from "../../src/features/settings/productUpdatePresentation.ts";
type BackendNotice = { sequence: number; occurredAt: string } & (
  { kind: "update-available"; facts: { version: string } }
  | { kind: "access-request"; facts: { requestId: string; name: string; observedExecutablePath: string } }
);
import type { Page } from "playwright-core";
import { liveText } from "./liveLocator.ts";
import assert from "node:assert/strict";
import { loadSettingsText } from "../../src/i18n/settingsLoader.ts";
import { productUpdateCopy } from "../../src/features/settings/productUpdatePresentation.ts";
import { readFrontendPreferences } from "./frontendPreferencesFixture.ts";

/*
 * 自动更新（update-notice 合同“自动更新”一节）：更新方式三档、间隔检查与 24–168 小时间隔、等待联网与下次检查时间按 UTC 如实显示。
 * 通知（docs/notifications_2026-10-29.md）：新版本和访问请求的界面提示来自 /api/notifications/subscribe，更新状态里的
 * availableVersion 只在更新区显示；第一帧只记下，重新加载后不补显示；每类的“弹出通知”和“软件内提示”都开着才提示，
 * 关着时序号照样前移，打开后不补显示；开关在设置页“通知”，只存在前端偏好里，不进后端设置；恢复界面偏好默认值全部回到开。
 */
export default async function updateNoticeJourney(page: Page, baseUrl: string) {
  const settings = await loadSettingsText("zh-CN");
  const text = settings.updates;
  const copy = productUpdateCopy("zh-CN");
  const errors: string[] = [];
  const patches: { changes: Partial<BackendAppSettings> }[] = [];
  page.on("pageerror", error => errors.push(String(error)));
  page.on("dialog", dialog => dialog.dismiss());
  page.on("request", request => {
    if (request.method() === "PATCH" && new URL(request.url()).pathname === "/api/settings/app") patches.push(request.postDataJSON());
  });
  const scenario = async (value: Record<string, unknown>) => fetch(`${baseUrl}/__test/scenario`, {
    method: "POST", headers: { "Content-Type": "application/json" }, body: JSON.stringify(value) });
  const publish = async (status: Partial<ProductUpdateState>) => assert.ok((await scenario({ productUpdateStatus: status })).ok, JSON.stringify(status));

  // 场景值按合同校验：未知字段和不带时区的时间直接拒绝，不被悄悄忽略。
  assert.equal((await scenario({ productUpdateStatus: { bogus: 1 } })).ok, false);
  assert.equal((await scenario({ productUpdateStatus: { nextCheckAt: "2026-10-26T08:00:00" } })).ok, false);
  assert.equal((await scenario({ productUpdateStatus: { nextCheckAt: "2026-10-26T16:00:00+08:00" } })).ok, false);

  const notices = () => page.locator(".toast:not([data-exit-ghost])").filter({ hasText: text.noticeTitle });
  const save = async () => {
    const response = page.waitForResponse(item => item.request().method() === "PATCH"
      && new URL(item.url()).pathname === "/api/settings/app");
    await page.getByRole("button", { name: settings.actions.save, exact: true }).click();
    assert.ok((await response).ok(), "settings PATCH rejected");
    await liveText(page, settings.saveState.saved).waitFor();
  };
  const openUpdates = async () => {
    await page.getByRole("button", { name: "设置", exact: true }).click();
    await page.getByRole("button", { name: settings.sections.updates, exact: true }).click();
    await page.getByRole("radiogroup", { name: text.modeTitle }).waitFor();
  };
  const mode = (name: string) => page.getByRole("radiogroup", { name: text.modeTitle }).getByRole("radio", { name, exact: true });
  const option = (id: AppUpdateMode) => text.modeOptions.find(item => item.id === id)!.label;
  const periodic = () => page.getByRole("checkbox", { name: text.periodicTitle, exact: true });
  const interval = () => page.getByRole("textbox", { name: text.intervalTitle, exact: true });
  const notificationText = settings.notifications;
  const notificationSwitch = (kind: keyof typeof notificationText.kinds, option: "notify" | "system" | "inApp") => page.getByRole("checkbox", {
    name: notificationText.switchLabel(notificationText.kinds[kind].title, notificationText[`${option}Title`]), exact: true });
  const openNotifications = async () => {
    await page.getByRole("button", { name: "设置", exact: true }).click();
    await page.getByRole("button", { name: settings.sections.notifications, exact: true }).click();
    await liveText(page, notificationText.intro).waitFor();
  };
  // 通知流：场景给的是整份列表（从旧到新），前端按序号自己判断哪些是新的。
  const sent: BackendNotice[] = [];
  const notify = async (...items: BackendNotice[]) => {
    sent.push(...items);
    assert.ok((await scenario({ backendNotifications: sent })).ok);
  };
  const update = (sequence: number, version: string): BackendNotice => ({ sequence, kind: "update-available", occurredAt: "2026-10-29T10:00:00Z", facts: { version } });
  const telemetry = async (): Promise<{ logicalInFlight: number; logicalStarted: number } | undefined> => (await (await fetch(`${baseUrl}/__test/requests`)).json())["/api/notifications/subscribe"];
  // 打开（或重新加载）后等这次订阅开着、偏好缓存读到，之后发的通知才是新的。
  const subscribed = async (startedBefore: number) => {
    const deadline = Date.now() + 12_000;
    for (;;) {
      const value = await telemetry();
      if (value?.logicalInFlight === 1 && value.logicalStarted > startedBefore) break;
      if (Date.now() > deadline) throw new Error(`notification subscription did not open: ${JSON.stringify(value)}`);
      await new Promise(resolve => setTimeout(resolve, 100));
    }
    await page.locator('.settings-readonly-boundary[aria-disabled="false"]:not([data-exit-ghost])').waitFor({ timeout: 5_000 });
  };
  const started = async () => (await telemetry())?.logicalStarted ?? 0;
  const quiet = () => page.waitForTimeout(400);
  const waitForChecked = async (locator: Locator, label: string) => {
    const deadline = Date.now() + 5_000;
    while (!(await locator.isChecked())) {
      if (Date.now() > deadline) throw new Error(`not checked: ${label}`);
      await page.waitForTimeout(100);
    }
  };

  await page.route("**/api/updates/product/versions", route => route.fulfill({ json: {
    installedVersion: "1.0.0", status: "loaded", complete: true, checkedAt: "2026-10-27T08:30:00+00:00",
    options: [{ choice: "latest", version: "1.3.0", selectable: true, available: true }] } }));
  await page.goto(`${baseUrl}/`, { waitUntil: "domcontentloaded" });
  await openUpdates();
  // 版本目录的“上次检查”与更新状态同样按 UTC 显示。
  await liveText(page, `${text.lastChecked}2026-10-27 08:30 UTC`).waitFor();

  // 三档：默认自动下载并应用；选“只检查并提示”保存后重新加载仍是它。
  assert.equal(await mode(option("automatic")).getAttribute("aria-checked"), "true");
  for (const id of ["off", "notify", "automatic"] as const) await mode(option(id)).waitFor();
  await mode(option("notify")).click();
  await save();
  assert.deepEqual(patches.at(-1)!.changes, { update: { mode: "notify" } });
  // 自动检查一律等联网、不可关闭：没有联网等待开关，只有一行说明；手动检查不等联网。
  await liveText(page, text.internetNote).waitFor();
  await liveText(page, text.manualCheckNote).waitFor();
  assert.equal(await page.getByRole("checkbox", { name: /联网|internet/i }).count(), 0, "a startup internet-wait switch is shown");

  // 间隔检查：范围 24–168 写在标签里，范围外的输入不被接受（落到最近的边界）。
  await liveText(page, text.intervalLabel(24, 168)).waitFor();
  assert.equal(await interval().inputValue(), "24");
  await periodic().check();
  for (const [typed, kept] of [["6", "24"], ["200", "168"], ["48", "48"]]) {
    await interval().fill(typed);
    await interval().blur();
    assert.equal(await interval().inputValue(), kept, `interval ${typed}`);
  }
  await save();
  assert.deepEqual(patches.at(-1)!.changes, { update: { periodicCheckEnabled: true, checkIntervalHours: 48 } });
  await page.reload({ waitUntil: "domcontentloaded" });
  await openUpdates();
  assert.equal(await mode(option("notify")).getAttribute("aria-checked"), "true", "the update mode did not persist");
  assert.equal(await periodic().isChecked(), true);
  assert.equal(await interval().inputValue(), "48");

  // 关闭档：间隔检查和间隔保持可见、禁用，写明原因；值不变。
  await mode(option("off")).click();
  await page.getByRole("note").filter({ hasText: text.offReason }).waitFor();
  assert.equal(await periodic().isDisabled(), true);
  assert.equal(await interval().isDisabled(), true);
  assert.equal(await periodic().isChecked(), true);
  assert.equal(await interval().inputValue(), "48");
  await mode(option("notify")).click();
  assert.equal(await periodic().isDisabled(), false);
  assert.equal(await page.getByRole("note").filter({ hasText: text.offReason }).count(), 0);

  // 状态如实显示：等待联网时下次检查为空，显示“联网后计时”；联网后显示 UTC 时间和已累计联网时长。
  await publish({ automaticCheckStatus: "waitingForInternet", checkResult: "failed", nextCheckAt: null, onlineTimeSinceLastCheckSeconds: 0 });
  await page.getByRole("status").filter({ hasText: text.waitingForInternet }).waitFor();
  await liveText(page, `${text.nextCheck}${text.nextCheckAfterInternet}`).waitFor();
  await publish({ checkResult: "noUpdate", lastCheckedAt: "2026-10-25T08:00:00+00:00",
    automaticCheckStatus: "scheduled", nextCheckAt: "2026-10-27T08:00:00Z", onlineTimeSinceLastCheckSeconds: 3725 });
  await liveText(page, `${text.nextCheck}2026-10-27 08:00 UTC`).waitFor();
  await liveText(page, `${text.lastChecked}2026-10-25 08:00 UTC`).waitFor();
  await liveText(page, `${text.connectedElapsed}1:02:05`).waitFor();
  assert.equal(await page.getByRole("status").filter({ hasText: text.waitingForInternet }).count(), 0);
  assert.equal(await notices().count(), 0, "a notice without an available version");

  // 更新状态里的新版本只在更新区显示；提示来自通知流，恰好一次。
  await subscribed(0);
  await publish({ checkResult: "available", lastCheckedAt: "2026-10-25T09:00:00Z", availableVersion: "1.1.0" });
  await liveText(page, `${text.availableVersion}1.1.0`).waitFor();
  await quiet();
  assert.equal(await notices().count(), 0, "the update status alone is not a notice");
  await notify(update(1, "1.1.0"));
  await notices().filter({ hasText: text.noticeMessage("1.1.0") }).waitFor();
  await publish({ checkResult: "available", stage: "checking", lastCheckedAt: "2026-10-25T09:00:00Z", availableVersion: "1.1.0" });
  await page.getByRole("status").filter({ hasText: text.stages.checking }).waitFor();
  await notify();
  await quiet();
  assert.equal(await notices().count(), 1, "the same notification was shown twice");
  await notices().getByRole("button", { name: "关闭", exact: true }).click();

  // 重新加载后第一帧只记下，已有的通知不补显示。
  let before = await started();
  await page.reload({ waitUntil: "domcontentloaded" });
  await openUpdates();
  await subscribed(before);
  await liveText(page, `${text.availableVersion}1.1.0`).waitFor();
  await quiet();
  assert.equal(await notices().count(), 0, "a notification from before the reload was shown again");

  // 待应用的正是通知里的版本时，提示带“立即应用”；“查看更新与版本”打开更新区。
  await publish({ checkResult: "available", stage: "pendingApply", targetVersion: "1.2.0", availableVersion: "1.2.0" });
  await page.getByRole("button", { name: copy.apply, exact: true }).waitFor();
  await page.getByRole("button", { name: "监视控制台", exact: true }).click();
  await notify(update(2, "1.2.0"));
  const ready = notices().filter({ hasText: text.noticeReadyMessage("1.2.0") });
  await ready.waitFor();
  assert.equal(await notices().count(), 1);
  await ready.getByRole("button", { name: copy.apply, exact: true }).waitFor();
  await ready.getByRole("button", { name: text.noticeOpenUpdates, exact: true }).click();
  await page.getByRole("heading", { name: settings.sections.updates, exact: true }).waitFor();
  await page.getByRole("radiogroup", { name: text.modeTitle }).waitFor();
  assert.equal(await notices().count(), 0, "the notice stayed after its entry was used");
  await page.getByRole("button", { name: copy.apply, exact: true }).waitFor();
  // 从提示打开的更新区：没有稳定版时，空状态在固定项的格子里；没有不能选的版本，顶上不写原因。
  const versionList = page.locator(".version-selector:not([data-exit-ghost]):not([data-exit-ghost] *)");
  await versionList.locator(".version-selector-pinned > .version-selector-empty").filter({ hasText: text.noStable }).waitFor();
  assert.equal(await versionList.locator(".version-selector-notes").count(), 0, "a reason note without an unavailable version");
  assert.equal(await versionList.locator('input[value="latest"]').isEnabled(), true);
  assert.equal(await page.getByRole("checkbox", { name: /通知|提示/ }).count(), 0, "notification switches are left in the updates section");

  // 设置页“通知”：每类三项默认全开；“弹出通知”关掉时两条途径不可改，显示的仍是各自的选择。
  await openNotifications();
  for (const kind of ["memoryProtection", "update", "accessRequest", "crashReport"] as const) {
    for (const option of ["notify", "system", "inApp"] as const) {
      assert.equal(await notificationSwitch(kind, option).isChecked(), true, `${kind} ${option}`);
      assert.equal(await notificationSwitch(kind, option).isDisabled(), false, `${kind} ${option}`);
    }
  }
  await notificationSwitch("update", "inApp").uncheck();
  await notificationSwitch("update", "system").uncheck();
  await notificationSwitch("accessRequest", "system").uncheck();
  await notificationSwitch("crashReport", "system").uncheck();
  await notificationSwitch("memoryProtection", "notify").uncheck();
  for (const option of ["system", "inApp"] as const) {
    assert.equal(await notificationSwitch("memoryProtection", option).isDisabled(), true, `memory protection ${option} under a closed switch`);
    assert.equal(await notificationSwitch("memoryProtection", option).isChecked(), true, `memory protection ${option} keeps its choice`);
  }
  const all = (value: "on" | "off") => Object.fromEntries(["update", "accessRequest", "memoryProtection", "crashReport"].flatMap(kind =>
    ["Notify", "InApp", "System"].map(option => [`${kind}${option}`, value])));
  const patchCount = patches.length;
  await page.getByRole("button", { name: settings.actions.save, exact: true }).click();
  await page.waitForFunction(() => JSON.parse(localStorage.getItem("resource-manager:frontend-preferences:v1") ?? "null")
    ?.notifications?.memoryProtectionNotify === "off");
  // writePreferences sets the saved notification signal before saveDraft marks
  // this preferences-only save complete; localStorage alone is an earlier signal.
  await liveText(page, settings.saveState.saved).waitFor({ timeout: 5_000 });
  assert.equal(patches.length, patchCount, "notification switches reached the backend settings");
  assert.deepEqual((await readFrontendPreferences(page))!.notifications, { ...all("on"),
    updateInApp: "off", updateSystem: "off", accessRequestSystem: "off", memoryProtectionNotify: "off", crashReportSystem: "off" });

  // 软件内提示关着：不提示；总开关关着：同样不提示。重新打开后，关着期间的通知不补显示。
  await notify(update(3, "1.3.0"));
  await quiet();
  assert.equal(await notices().count(), 0, "a notice while the in-app switch is off");
  await notificationSwitch("update", "inApp").check();
  await notificationSwitch("update", "notify").uncheck();
  await page.getByRole("button", { name: settings.actions.save, exact: true }).click();
  await page.waitForFunction(() => JSON.parse(localStorage.getItem("resource-manager:frontend-preferences:v1") ?? "null")
    ?.notifications?.updateNotify === "off");
  await liveText(page, settings.saveState.saved).waitFor({ timeout: 5_000 });
  await notify(update(4, "1.4.0"));
  await quiet();
  assert.equal(await notices().count(), 0, "a notice while the show switch is off");
  await notificationSwitch("update", "notify").check();
  await page.getByRole("button", { name: settings.actions.save, exact: true }).click();
  await page.waitForFunction(() => JSON.parse(localStorage.getItem("resource-manager:frontend-preferences:v1") ?? "null")
    ?.notifications?.updateNotify === "on");
  await liveText(page, settings.saveState.saved).waitFor({ timeout: 5_000 });
  await notify(update(5, "1.5.0"));
  await notices().filter({ hasText: text.noticeMessage("1.5.0") }).waitFor();
  assert.equal(await notices().count(), 1, "notifications skipped by a switch were shown later");
  await notices().getByRole("button", { name: "关闭", exact: true }).click();

  // 访问请求：写出请求方名称和程序的文件名；入口打开可信访问。Windows 通知关着不影响界面提示。
  await notify({ sequence: 6, kind: "access-request", occurredAt: "2026-10-29T10:00:00Z",
    facts: { requestId: "request-6", name: "Example agent", observedExecutablePath: "C:\\Tools\\agent.exe" } });
  const access = page.locator(".toast:not([data-exit-ghost])").filter({ hasText: notificationText.accessRequestNoticeTitle });
  await access.filter({ hasText: notificationText.accessRequestNoticeMessage("Example agent", "agent.exe") }).waitFor();
  await access.getByRole("button", { name: notificationText.accessRequestNoticeOpen, exact: true }).click();
  await page.getByRole("heading", { name: settings.sections.trustedAccess, exact: true }).waitFor();

  // 选择保存在前端偏好里，重新加载后还在。
  before = await started();
  await page.reload({ waitUntil: "domcontentloaded" });
  await openNotifications();
  await subscribed(before);
  assert.equal(await notificationSwitch("update", "inApp").isChecked(), true);
  assert.equal(await notificationSwitch("update", "system").isChecked(), false);
  assert.equal(await notificationSwitch("accessRequest", "system").isChecked(), false);
  assert.equal(await notificationSwitch("crashReport", "system").isChecked(), false, "crash-report channel choice survives save and reload");
  assert.equal(await notificationSwitch("memoryProtection", "notify").isChecked(), false);
  assert.equal(await notificationSwitch("memoryProtection", "inApp").isDisabled(), true);

  // 恢复界面偏好默认值：每一项回到开，已有的通知不补显示。
  await page.getByRole("button", { name: settings.sections.appearance, exact: true }).click();
  await page.getByRole("button", { name: settings.appearance.resetCache, exact: true }).click();
  // 没有浮层选择时重置删掉整份缓存，每一项按点值读取。
  await page.waitForFunction(() => localStorage.getItem("resource-manager:frontend-preferences:v1") === null);
  await page.getByRole("button", { name: settings.sections.notifications, exact: true }).click();
  for (const kind of ["memoryProtection", "update", "accessRequest", "crashReport"] as const) {
    for (const option of ["notify", "system", "inApp"] as const) {
      await waitForChecked(notificationSwitch(kind, option), `${kind} ${option} after reset`);
    }
  }
  await quiet();
  assert.equal(await notices().count(), 0, "resetting the preferences showed old notifications");

  assert.deepEqual(errors, []);
  return { modePersisted: true, intervalRange: [24, 168], offDisablesPeriodic: true, waitingForInternet: true, utcTimes: true,
    noticesShown: ["1.1.0", "1.2.0", "1.5.0"], afterReload: 0, inAppOff: 0, showOff: 0, entryOpensUpdates: true, accessRequestNotice: true };
}
