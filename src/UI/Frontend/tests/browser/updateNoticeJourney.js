import { liveText } from "./liveLocator.js";
import assert from "node:assert/strict";
import { loadSettingsText } from "../../src/i18n/settingsLoader.ts";
import { productUpdateCopy } from "../../src/features/settings/productUpdatePresentation.ts";
import { readFrontendPreferences } from "./frontendPreferencesFixture.js";
import { waitForCondition, waitForStableRendering } from "./journeyWaits.js";

/*
 * 发现新版本就提示一次（update-notice 合同“自动更新”一节）：更新方式三档、间隔检查与 24–168 小时间隔、
 * 等待联网与下次检查时间按 UTC 如实显示；后端推送的 availableVersion 在界面内只提示一次，
 * 同一版本重新加载后不再提示，更高的版本再提示一次，界面内提示关掉后不提示；提示的入口打开更新与版本。
 */
export default async function updateNoticeJourney(page, baseUrl) {
  const settings = await loadSettingsText("zh-CN");
  const text = settings.updates;
  const copy = productUpdateCopy("zh-CN");
  const errors = [];
  const patches = [];
  page.on("pageerror", error => errors.push(String(error)));
  page.on("dialog", dialog => dialog.dismiss());
  page.on("request", request => {
    if (request.method() === "PATCH" && new URL(request.url()).pathname === "/api/settings/app") patches.push(request.postDataJSON());
  });
  const scenario = async value => fetch(`${baseUrl}/__test/scenario`, {
    method: "POST", headers: { "Content-Type": "application/json" }, body: JSON.stringify(value) });
  const publish = async status => assert.ok((await scenario({ productUpdateStatus: status })).ok, JSON.stringify(status));

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
  const mode = name => page.getByRole("radiogroup", { name: text.modeTitle }).getByRole("radio", { name, exact: true });
  const option = id => text.modeOptions.find(item => item.id === id).label;
  const periodic = () => page.getByRole("checkbox", { name: text.periodicTitle, exact: true });
  const interval = () => page.getByRole("textbox", { name: text.intervalTitle, exact: true });
  const settleNoticeEffect = async (version, inApp, notifiedVersion) => {
    // The version text and App's notice effect consume the same productUpdates.status.
    // After reload the status starts null; 1.3.0 is a new publication in this document.
    await liveText(page, `${text.availableVersion}${version}`).waitFor({ timeout: 5_000 });
    // settingsStore cannot project loadState=ready before preferenceOwner.read has
    // set frontendNotifications. The fieldset exposes this real owner-ready state.
    await page.locator('.settings-readonly-boundary[aria-disabled="false"]:not([data-exit-ghost])')
      .waitFor({ state: "visible", timeout: 5_000 });
    await waitForCondition(page, async () => {
      const notifications = (await readFrontendPreferences(page))?.notifications;
      return notifications?.updateInApp === inApp
        && notifications?.updateInAppNotifiedVersion === notifiedVersion
        && await page.getByRole("checkbox", { name: text.inAppNoticeTitle, exact: true }).isChecked() === (inApp === "on");
    }, { timeoutMs: 5_000, description: `notice preferences loaded for ${version}` });
    // bindProductUpdateNotice's createEffect synchronously decides show/record;
    // it has no timer. Cross real render frames after both inputs have committed
    // before retaining the original no-toast assertion at the call site.
    await waitForStableRendering(page, [".settings-page"], { timeoutMs: 5_000 });
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
  for (const id of ["off", "notify", "automatic"]) await mode(option(id)).waitFor();
  await mode(option("notify")).click();
  await save();
  assert.deepEqual(patches.at(-1).changes, { update: { mode: "notify" } });
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
  assert.deepEqual(patches.at(-1).changes, { update: { periodicCheckEnabled: true, checkIntervalHours: 48 } });
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

  // 推送的新版本：界面内提示恰好一次，记进前端偏好缓存；同一版本的其他状态变化不再提示。
  await publish({ checkResult: "available", lastCheckedAt: "2026-10-25T09:00:00Z", availableVersion: "1.1.0" });
  await notices().filter({ hasText: text.noticeMessage("1.1.0") }).waitFor();
  await liveText(page, `${text.availableVersion}1.1.0`).waitFor();
  await publish({ checkResult: "available", stage: "checking", lastCheckedAt: "2026-10-25T09:00:00Z", availableVersion: "1.1.0" });
  await page.getByRole("status").filter({ hasText: text.stages.checking }).waitFor();
  assert.equal(await notices().count(), 1, "the same version was noticed twice");
  await page.waitForFunction(() => JSON.parse(localStorage.getItem("resource-manager:frontend-preferences:v1") ?? "null")
    ?.notifications?.updateInAppNotifiedVersion === "1.1.0");
  await notices().getByRole("button", { name: "关闭", exact: true }).click();

  // 同一版本重新加载后不再提示。
  await page.reload({ waitUntil: "domcontentloaded" });
  await openUpdates();
  await liveText(page, `${text.availableVersion}1.1.0`).waitFor();
  await settleNoticeEffect("1.1.0", "on", "1.1.0");
  assert.equal(await notices().count(), 0, "the same version was noticed again after reload");

  // 更高的版本再提示一次；已准备好时提示带“立即应用”；“查看更新与版本”打开更新区。
  await page.getByRole("button", { name: "监视控制台", exact: true }).click();
  await publish({ checkResult: "available", stage: "pendingApply", targetVersion: "1.2.0", availableVersion: "1.2.0" });
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

  // 界面内提示关掉后不提示，也不记下版本；系统通知开关只保存偏好。
  await page.getByRole("checkbox", { name: text.inAppNoticeTitle, exact: true }).uncheck();
  await page.getByRole("checkbox", { name: text.systemNoticeTitle, exact: true }).uncheck();
  assert.equal(await page.getByRole("checkbox", { name: text.accessRequestNoticeTitle, exact: true }).isChecked(), true);
  await liveText(page, text.accessRequestNoticeDescription).waitFor();
  await page.getByRole("checkbox", { name: text.accessRequestNoticeTitle, exact: true }).uncheck();
  const patchCount = patches.length;
  await page.getByRole("button", { name: settings.actions.save, exact: true }).click();
  await page.waitForFunction(() => {
    const notifications = JSON.parse(localStorage.getItem("resource-manager:frontend-preferences:v1") ?? "null")?.notifications;
    return notifications?.updateSystem === "off" && notifications?.accessRequestSystem === "off";
  });
  // writePreferences sets the saved notification signal before saveDraft marks
  // this preferences-only save complete; localStorage alone is an earlier signal.
  await liveText(page, settings.saveState.saved).waitFor({ timeout: 5_000 });
  assert.equal(patches.length, patchCount, "notification switches reached the backend settings");
  const saved = await readFrontendPreferences(page);
  assert.deepEqual(saved.notifications, { updateInApp: "off", updateSystem: "off", accessRequestSystem: "off", updateInAppNotifiedVersion: "1.2.0" });
  await publish({ checkResult: "available", availableVersion: "1.3.0" });
  await liveText(page, `${text.availableVersion}1.3.0`).waitFor();
  await settleNoticeEffect("1.3.0", "off", "1.2.0");
  assert.equal(await notices().count(), 0, "a notice while the in-app switch is off");
  assert.equal((await readFrontendPreferences(page)).notifications.updateInAppNotifiedVersion, "1.2.0");
  await page.reload({ waitUntil: "domcontentloaded" });
  await openUpdates();
  assert.equal(await page.getByRole("checkbox", { name: text.inAppNoticeTitle, exact: true }).isChecked(), false);
  assert.equal(await page.getByRole("checkbox", { name: text.systemNoticeTitle, exact: true }).isChecked(), false);
  assert.equal(await page.getByRole("checkbox", { name: text.accessRequestNoticeTitle, exact: true }).isChecked(), false);

  // 恢复界面偏好默认值只重置偏好点；已提示版本是记录，保留，同一版本不再提示。
  await publish({ checkResult: "available", availableVersion: "1.2.0" });
  await page.getByRole("button", { name: settings.sections.appearance, exact: true }).click();
  await page.getByRole("button", { name: settings.appearance.resetCache, exact: true }).click();
  await page.waitForFunction(() => JSON.parse(localStorage.getItem("resource-manager:frontend-preferences:v1") ?? "null")
    ?.notifications?.updateInApp === "on");
  assert.deepEqual((await readFrontendPreferences(page)).notifications,
    { updateInApp: "on", updateSystem: "on", accessRequestSystem: "on", updateInAppNotifiedVersion: "1.2.0" });
  await page.reload({ waitUntil: "domcontentloaded" });
  await openUpdates();
  assert.equal(await page.getByRole("checkbox", { name: text.inAppNoticeTitle, exact: true }).isChecked(), true);
  assert.equal(await page.getByRole("checkbox", { name: text.accessRequestNoticeTitle, exact: true }).isChecked(), true);
  await liveText(page, `${text.availableVersion}1.2.0`).waitFor();
  await settleNoticeEffect("1.2.0", "on", "1.2.0");
  assert.equal(await notices().count(), 0, "the same version was noticed again after resetting the interface preferences");

  assert.deepEqual(errors, []);
  return { resetKeepsNoticedVersion: true, modePersisted: true, intervalRange: [24, 168], offDisablesPeriodic: true, waitingForInternet: true,
    utcTimes: true, noticesShown: ["1.1.0", "1.2.0"], sameVersionAfterReload: 0, inAppOff: 0, entryOpensUpdates: true };
}
