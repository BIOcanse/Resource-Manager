import type { ConcreteAppLanguageMode } from "../../src/i18n/settingsTypes.ts";
import type { Page } from "playwright-core";
import assert from "node:assert/strict";
import { loadAppCopy } from "../../src/i18n/copy/appCopyLoader.ts";
import { loadSettingsText } from "../../src/i18n/settingsLoader.ts";
import { languageOptions } from "../../src/i18n/settingsLanguages.ts";

// 可信访问的失败与恢复，全部由模拟后端的显式 trustedAccess 场景驱动：
// 密钥列表读取失败；关闭默认密钥（界面以管理员身份运行、不带密钥，照常可用）后重新启用；
// 后端不接受界面的密钥时（401）写明原因；并在九种语言下渲染可信访问区和关闭确认框。
export default async function trustedAccessRecoveryJourney(page: Page, baseUrl: string) {
  const errors: string[] = [];
  const defaultKeyWrites: { enabled: boolean; rotate: boolean }[] = [];
  page.on("pageerror", error => errors.push(String(error)));
  page.on("request", request => {
    if (new URL(request.url()).pathname === "/api/access-keys/default") defaultKeyWrites.push(request.postDataJSON());
  });
  const patchScenario = (value: Record<string, unknown>) => fetch(`${baseUrl}/__test/scenario`, {
    method: "POST",
    headers: { "Content-Type": "application/json" },
    body: JSON.stringify(value)
  });
  const scenario = async (): Promise<{ trustedAccess: string }> => (await fetch(`${baseUrl}/__test/scenario`)).json();

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
  const openTrustedAccess = async (text: Awaited<ReturnType<typeof loadSettingsText>>) => {
    await page.getByRole("button", { name: text.sections.appearance, exact: true }).click();
    await page.getByRole("button", { name: text.sections.trustedAccess, exact: true }).click();
  };
  await page.getByRole("button", { name: settingsText.sections.trustedAccess, exact: true }).click();
  const panel = page.getByRole("region", { name: copy.title, exact: true });

  // H1：读取失败显示原因和重试，设置页不崩溃，待处理请求区照常显示。
  const loadFailure = panel.locator(".trusted-access-load-failure");
  await loadFailure.filter({ hasText: copy.loadFailed }).waitFor();
  assert.equal(await loadFailure.getAttribute("role"), "alert");
  await panel.getByRole("region", { name: copy.requestsTitle, exact: true }).getByText(copy.requestsEmpty, { exact: true }).waitFor();
  assert.equal(await panel.getByRole("button", { name: copy.enableDefault, exact: true }).count(), 0,
    "a read failure does not offer to re-enable the default key");
  assert.deepEqual(errors, [], "a failed key read must not throw into the page boundary");
  assert.equal((await patchScenario({ trustedAccess: "ready" })).ok, true);
  await loadFailure.getByRole("button", { name: copy.retry, exact: true }).click();
  await panel.getByText(copy.defaultEnabled, { exact: true }).waitFor();
  await panel.getByText(copy.defaultKeyName, { exact: true }).waitFor();

  // 九种语言下实际渲染可信访问区与关闭确认框。
  // 依次切到其余八种语言，最后切回简体中文继续后面的步骤。
  const languages = [...languageOptions.filter(option => option.id !== "system" && option.id !== "zh-CN"),
    languageOptions.find(option => option.id === "zh-CN")!];
  let previous = "zh-CN";
  const rendered: string[] = [];
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
    const feedback = (await loadAppCopy(option.id as ConcreteAppLanguageMode)).feedback;
    const localized = text.systemIntegration.trustedAccess;
    await openTrustedAccess(text);
    const localizedPanel = page.getByRole("region", { name: localized.title, exact: true });
    await localizedPanel.getByText(localized.defaultEnabled, { exact: true }).waitFor();
    for (const value of [localized.description, localized.defaultTitle, localized.defaultFileLocation,
      localized.defaultAccessLossWarning, localized.defaultSecretLocalOnly, localized.createTitle, localized.keysTitle,
      localized.keysEmpty, localized.requestsTitle, localized.requestsEmpty]) {
      await localizedPanel.getByText(value, { exact: true }).first().waitFor();
    }
    const scopeGroup = localizedPanel.getByRole("group", { name: localized.createScopesLabel, exact: true });
    // 持有者须为管理员的范围（模拟后端发的 administratorHolderScopes）在名字后写明，每种语言都核对。
    for (const [scope, label] of Object.entries(localized.scopeLabels)) {
      const text = ["machine.write", "injection.configure"].includes(scope) ? `${label}${localized.administratorHolderScope}` : label;
      await scopeGroup.getByText(text, { exact: true }).waitFor();
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
  assert.equal(await page.evaluate(async () => (await fetch("/api/access-keys", { cache: "no-store" })).status), 200,
    "the interface sends no key, so turning the default key off does not cut it off");

  // 关闭后显示说明和重新启用按钮，焦点在按钮上；请求区照常（界面的订阅流不用默认密钥）。
  const defaultSection = panel.locator(".trusted-access-default");
  await defaultSection.getByText(copy.defaultDisabledRecovery, { exact: true }).waitFor();
  const defaultButtons = await defaultSection.getByRole("button").allTextContents();
  assert.deepEqual(defaultButtons.map(text => text.trim()), [copy.enableDefault]);
  await page.waitForFunction(label => document.activeElement?.textContent!?.trim() === label, copy.enableDefault);
  await panel.getByRole("region", { name: copy.requestsTitle, exact: true })
    .getByText(copy.requestsEmpty, { exact: true }).waitFor();

  // 重新进入这一节：列表照常读出，默认密钥仍是关闭的；在这里重新启用走普通请求。
  await openTrustedAccess(settingsText);
  await defaultSection.getByText(copy.defaultDisabledRecovery, { exact: true }).waitFor();
  await defaultSection.getByRole("button", { name: copy.enableDefault, exact: true }).click();
  await panel.getByText(copy.defaultEnabled, { exact: true }).waitFor();
  await page.waitForFunction(() => document.activeElement?.id === "trustedAccessDefaultTitle");
  assert.deepEqual(defaultKeyWrites, [{ enabled: false, rotate: false }, { enabled: true, rotate: false }]);
  assert.equal((await scenario()).trustedAccess, "ready");
  await panel.getByRole("region", { name: copy.requestsTitle, exact: true })
    .getByText(copy.requestsEmpty, { exact: true }).waitFor({ timeout: 10_000 });

  // H3：后端不接受界面带的密钥（未提权、用默认密钥连接的界面在默认密钥关闭后）：读取得到 401，写明原因并给出重试；
  // 这样的界面没有管理员身份，也就不提供重新启用。
  assert.equal((await patchScenario({ trustedAccess: "key-rejected" })).ok, true);
  await openTrustedAccess(settingsText);
  await loadFailure.filter({ hasText: copy.failureReasons.unauthorized }).waitFor();
  assert.deepEqual(errors, [], "a 401 key read must not throw into the page boundary");
  assert.equal(await loadFailure.getByRole("button", { name: copy.enableDefault, exact: true }).count(), 0,
    "a rejected key cannot re-enable the default key");
  assert.equal((await patchScenario({ trustedAccess: "ready" })).ok, true);
  await loadFailure.getByRole("button", { name: copy.retry, exact: true }).click();
  await panel.getByText(copy.defaultEnabled, { exact: true }).waitFor();

  assert.equal(rendered.length, 9);
  assert.deepEqual(errors, []);
  return {
    unknownScenarioRejected: true,
    readFailureVisible: true,
    disableConfirmed: true,
    interfaceUnaffectedByDisable: true,
    reenabled: true,
    rejectedKeyVisible: 401,
    languages: rendered
  };
}
