import assert from "node:assert/strict";
import { loadAppCopy } from "../../src/i18n/copy/appCopyLoader.ts";
import { loadSettingsText } from "../../src/i18n/settingsLoader.ts";
import { languageOptions } from "../../src/i18n/settingsLanguages.ts";
import { installElevatedBackendBridge } from "./elevatedBackendBridge.js";

// 可信访问的失败与恢复，全部由模拟后端的显式 trustedAccess 场景驱动：
// 密钥列表读取失败、关闭默认密钥后后端对界面的每个请求返回 401、以管理员身份重新启用；
// 最后在九种语言下渲染可信访问区和关闭确认框。
export default async function trustedAccessRecoveryJourney(page, baseUrl) {
  const errors = [];
  const defaultKeyWrites = [];
  page.on("pageerror", error => errors.push(String(error)));
  page.on("request", request => {
    if (new URL(request.url()).pathname === "/api/access-keys/default") defaultKeyWrites.push(request.postDataJSON());
  });
  const patchScenario = value => fetch(`${baseUrl}/__test/scenario`, {
    method: "POST",
    headers: { "Content-Type": "application/json" },
    body: JSON.stringify(value)
  });
  const scenario = async () => (await fetch(`${baseUrl}/__test/scenario`)).json();

  // 场景值是封闭集合：未知值被拒绝，不会被当成 ready 悄悄忽略。
  assert.equal((await patchScenario({ trustedAccess: "sometimes-fails" })).ok, false, "an unknown trustedAccess value is rejected");
  assert.equal((await scenario()).trustedAccess, "ready");
  assert.equal((await patchScenario({ trustedAccess: "read-failure" })).ok, true);

  const app = await loadAppCopy("zh-CN");
  const settingsText = await loadSettingsText("zh-CN");
  const copy = settingsText.systemIntegration.trustedAccess;
  await page.setViewportSize({ width: 1180, height: 900 });
  await page.goto(baseUrl, { waitUntil: "domcontentloaded" });
  await page.getByRole("navigation").getByRole("button", { name: app.page.settings, exact: true }).click();
  const openSystemIntegration = async text => {
    await page.getByRole("button", { name: text.sections.appearance, exact: true }).click();
    await page.getByRole("button", { name: text.sections.systemIntegration, exact: true }).click();
  };
  await page.getByRole("button", { name: settingsText.sections.systemIntegration, exact: true }).click();
  const panel = page.getByRole("region", { name: copy.title, exact: true });

  // H1：读取失败显示原因和重试，设置页不崩溃，待处理请求区照常显示。
  const loadFailure = panel.locator(".trusted-access-load-failure");
  await loadFailure.filter({ hasText: copy.loadFailed }).waitFor();
  assert.equal(await loadFailure.getAttribute("role"), "alert");
  await panel.getByRole("region", { name: copy.requestsTitle, exact: true }).getByText(copy.requestsEmpty, { exact: true }).waitFor();
  assert.equal(await panel.getByRole("button", { name: copy.enableDefaultElevated, exact: true }).count(), 0,
    "an ordinary read failure does not offer the default-key recovery");
  assert.deepEqual(errors, [], "a failed key read must not throw into the page boundary");
  assert.equal((await patchScenario({ trustedAccess: "ready" })).ok, true);
  await loadFailure.getByRole("button", { name: copy.retry, exact: true }).click();
  await panel.getByText(copy.defaultEnabled, { exact: true }).waitFor();
  await panel.getByText(copy.defaultKeyName, { exact: true }).waitFor();

  // 九种语言下实际渲染可信访问区与关闭确认框。
  // 依次切到其余八种语言，最后切回简体中文继续后面的步骤。
  const languages = [...languageOptions.filter(option => option.id !== "system" && option.id !== "zh-CN"),
    languageOptions.find(option => option.id === "zh-CN")];
  let previous = "zh-CN";
  const rendered = [];
  for (const option of languages) {
    const old = await loadSettingsText(previous);
    {
      await page.getByRole("button", { name: old.sections.appearance, exact: true }).click();
      await page.getByRole("button", { name: old.appearance.languageSelectLabel, exact: true }).click();
      await page.getByRole("option", { name: `${option.nativeLabel} · ${option.label}`, exact: true }).click();
      await page.getByRole("button", { name: old.actions.save, exact: true }).click();
      await page.waitForFunction(language => document.documentElement.lang === language, option.id);
    }
    const text = await loadSettingsText(option.id);
    const feedback = (await loadAppCopy(option.id)).feedback;
    const localized = text.systemIntegration.trustedAccess;
    await openSystemIntegration(text);
    const localizedPanel = page.getByRole("region", { name: localized.title, exact: true });
    await localizedPanel.getByText(localized.defaultEnabled, { exact: true }).waitFor();
    for (const value of [localized.description, localized.defaultTitle, localized.defaultFileLocation,
      localized.defaultAccessLossWarning, localized.defaultSecretLocalOnly, localized.createTitle, localized.keysTitle,
      localized.keysEmpty, localized.requestsTitle, localized.requestsEmpty]) {
      await localizedPanel.getByText(value, { exact: true }).first().waitFor();
    }
    const scopeGroup = localizedPanel.getByRole("group", { name: localized.createScopesLabel, exact: true });
    for (const label of Object.values(localized.scopeLabels)) {
      await scopeGroup.getByText(label, { exact: true }).waitFor();
    }
    await localizedPanel.getByRole("button", { name: localized.disableDefault, exact: true }).click();
    const dialog = page.getByRole("dialog", { name: localized.confirmDisableTitle, exact: true });
    await dialog.getByText(localized.confirmDisableMessage, { exact: true }).waitFor();
    await dialog.getByRole("button", { name: feedback.cancel, exact: true }).click();
    await dialog.waitFor({ state: "detached" });
    rendered.push(option.id);
    previous = option.id;
  }
  // H2：关闭默认密钥前必须确认；取消不发请求。
  await installElevatedBackendBridge(page);
  const confirmDisable = page.getByRole("dialog", { name: copy.confirmDisableTitle, exact: true });
  await panel.getByRole("button", { name: copy.disableDefault, exact: true }).click();
  await confirmDisable.getByText(copy.confirmDisableMessage, { exact: true }).waitFor();
  await confirmDisable.getByRole("button", { name: app.feedback.cancel, exact: true }).click();
  await confirmDisable.waitFor({ state: "detached" });
  assert.deepEqual(defaultKeyWrites, [], "canceling the confirmation (in every language) sends nothing");
  await panel.getByRole("button", { name: copy.disableDefault, exact: true }).click();
  await confirmDisable.getByRole("button", { name: copy.disableDefault, exact: true }).click();
  await confirmDisable.waitFor({ state: "detached" });
  await panel.getByText(copy.defaultDisabled, { exact: true }).waitFor();
  assert.deepEqual(defaultKeyWrites, [{ enabled: false, rotate: false }]);
  assert.equal((await scenario()).trustedAccess, "default-key-off");
  assert.equal(await page.evaluate(async () => (await fetch("/api/access-keys", { cache: "no-store" })).status), 401,
    "after the default key is turned off the backend rejects the interface");

  // 关闭后不再显示走普通通道的启用按钮：只给出恢复说明和显式的管理员恢复入口，焦点在恢复入口上。
  const defaultSection = panel.locator(".trusted-access-default");
  await defaultSection.getByText(copy.defaultDisabledRecovery, { exact: true }).waitFor();
  const defaultButtons = await defaultSection.getByRole("button").allTextContents();
  assert.deepEqual(defaultButtons.map(text => text.trim()), [copy.enableDefaultElevated]);
  await page.waitForFunction(label => document.activeElement?.textContent?.trim() === label, copy.enableDefaultElevated);
  // 后端结束了旧密钥的订阅流，重连得到 401：请求区显示读取失败，不保留旧列表。
  await panel.getByRole("region", { name: copy.requestsTitle, exact: true })
    .getByRole("alert").filter({ hasText: copy.requestsLoadFailed }).waitFor({ timeout: 15_000 });

  // 重新进入这一节：密钥列表读取得到 401，显示对应原因、重试和管理员恢复入口。
  await openSystemIntegration(settingsText);
  await loadFailure.filter({ hasText: copy.failureReasons.unauthorized }).waitFor();
  assert.deepEqual(errors, [], "a 401 key read must not throw into the page boundary");
  await loadFailure.getByRole("button", { name: copy.retry, exact: true }).waitFor();
  const elevatedSent = page.waitForRequest(request => request.url().endsWith("/__test/elevated-backend-request"));
  await loadFailure.getByRole("button", { name: copy.enableDefaultElevated, exact: true }).click();
  const command = (await elevatedSent).postDataJSON();
  assert.equal(command.type, "backend.elevatedRequest");
  assert.equal(command.method, "POST");
  assert.equal(command.path, "/api/access-keys/default");
  assert.deepEqual(JSON.parse(command.body), { enabled: true, rotate: false });
  assert.equal(Object.keys(command).some(key => /key|token|secret|authorization/i.test(key)), false);
  await panel.getByText(copy.defaultEnabled, { exact: true }).waitFor();
  await page.waitForFunction(() => document.activeElement?.id === "trustedAccessDefaultTitle");
  assert.equal(defaultKeyWrites.length, 1, "re-enabling never uses the ordinary request channel");
  assert.equal((await scenario()).trustedAccess, "ready");
  // 401 对订阅是终止性失败，订阅集合变化时才重新连接：离开设置页再回来，请求区恢复。
  await page.getByRole("navigation").getByRole("button", { name: app.page.monitor, exact: true }).click();
  await page.getByRole("navigation").getByRole("button", { name: app.page.settings, exact: true }).click();
  await page.getByRole("button", { name: settingsText.sections.systemIntegration, exact: true }).click();
  await panel.getByRole("region", { name: copy.requestsTitle, exact: true })
    .getByText(copy.requestsEmpty, { exact: true }).waitFor({ timeout: 20_000 });

  assert.equal(rendered.length, 9);
  assert.deepEqual(errors, []);
  return {
    unknownScenarioRejected: true,
    readFailureVisible: true,
    disableConfirmed: true,
    rejectedAfterDisable: 401,
    subscriptionFailureVisible: true,
    elevatedRecovery: command.method,
    languages: rendered
  };
}
