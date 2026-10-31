type ConcreteLanguageOption = (typeof languageOptions)[number] & { id: Parameters<typeof loadAppCopy>[0] };
interface LoginRequest { type: string; requestId: string; enabled?: boolean }
interface LoginResult { data: { type: string; requestId: string; enabled: boolean | null; error: string | null } }
type LoginWindow = typeof window & {
  __loginStartupEnabled: boolean; __loginStartupError: string | null; __loginStartupSetError: string | null; __loginStartupRequests: LoginRequest[];
  unboundWebView: { addEventListener(type: string, listener: (event: LoginResult) => void): void; removeEventListener(type: string, listener: (event: LoginResult) => void): void; postMessage(request: LoginRequest): void };
};
import type { Page } from "playwright-core";
import assert from "node:assert/strict";
import { loadAppCopy } from "../../src/i18n/copy/appCopyLoader.ts";
import { loadSettingsText } from "../../src/i18n/settingsLoader.ts";
import { languageOptions } from "../../src/i18n/settingsLanguages.ts";

export default async function loginStartupJourney(page: Page, baseUrl: string) {
  const errors: string[] = [];
  const backendWrites: unknown[] = [];
  page.on("pageerror", error => errors.push(String(error)));
  page.on("request", request => {
    if (new URL(request.url()).pathname === "/api/settings/app" && request.method() === "PATCH") backendWrites.push(request.postDataJSON());
  });
  await page.setViewportSize({ width: 1180, height: 900 });
  await page.goto(baseUrl, { waitUntil: "domcontentloaded" });
  await page.getByRole("navigation").getByRole("button", { name: (await loadAppCopy("zh-CN")).page.settings, exact: true }).click();
  // Install only the native login bridge after standalone browser bootstrap.
  // Preferences and backend session ownership keep their existing browser route.
  await page.evaluate(() => {
    const listeners = new Set<(event: LoginResult) => void>();
    (window as LoginWindow).__loginStartupEnabled = false;
    (window as LoginWindow).__loginStartupError = null;
    (window as LoginWindow).__loginStartupSetError = null;
    (window as LoginWindow).__loginStartupRequests = [];
    (window as LoginWindow).unboundWebView = {
      addEventListener(type, listener) { if (type === "message") listeners.add(listener); },
      removeEventListener(type, listener) { if (type === "message") listeners.delete(listener); },
      postMessage(request) {
        if (!["shell.loginStartup.get", "shell.loginStartup.set"].includes(request.type)) return;
        (window as LoginWindow).__loginStartupRequests.push(request);
        const error = (window as LoginWindow).__loginStartupError
          ?? (request.type === "shell.loginStartup.set" ? (window as LoginWindow).__loginStartupSetError : null);
        if (request.type === "shell.loginStartup.set" && !error) (window as LoginWindow).__loginStartupEnabled = request.enabled!;
        queueMicrotask(() => {
          for (const listener of [...listeners]) listener({ data: {
            type: "shell.loginStartup.result", requestId: request.requestId,
            enabled: error ? null : (window as LoginWindow).__loginStartupEnabled,
            error
          } });
        });
      }
    };
  });
  const languages = languageOptions.filter((option): option is ConcreteLanguageOption => option.id !== "system");
  let previous: Parameters<typeof loadAppCopy>[0] = "zh-CN";
  const checked: string[] = [];
  for (const option of languages) {
    const old = await loadSettingsText(previous);
    await page.getByRole("button", { name: old.sections.appearance, exact: true }).click();
    if (option.id !== previous) {
      await page.getByRole("button", { name: old.appearance.languageSelectLabel, exact: true }).click();
      await page.getByRole("option", { name: `${option.nativeLabel} · ${option.label}`, exact: true }).click();
      await page.getByRole("button", { name: old.actions.save, exact: true }).click();
      await page.waitForFunction(language => document.documentElement.lang === language, option.id);
    }
    const text = await loadSettingsText(option.id);
    const copy = text.systemIntegration;
    await page.evaluate(() => { (window as LoginWindow).__loginStartupError = null; (window as LoginWindow).__loginStartupEnabled = false; });
    await page.getByRole("button", { name: text.sections.systemIntegration, exact: true }).click();
    const login = page.getByRole("checkbox", { name: copy.loginStartTitle, exact: true });
    await page.getByText(copy.loginStartDescription, { exact: true }).waitFor();
    await page.getByText(copy.autoStartDescription, { exact: true }).waitFor();
    await page.getByRole("checkbox", { name: copy.autoStartTitle, exact: true }).waitFor();
    await page.waitForFunction(label => !document.querySelector<HTMLInputElement>(`input[aria-label="${CSS.escape(label)}"]`)?.disabled, copy.loginStartTitle);
    assert.equal(await login.isChecked(), false, option.id);
    await login.check();
    await page.waitForFunction(() => (window as LoginWindow).__loginStartupRequests.at(-1)?.type === "shell.loginStartup.set"
      && (window as LoginWindow).__loginStartupRequests.at(-1)?.enabled === true);
    await page.getByRole("button", { name: text.sections.appearance, exact: true }).click();
    await page.getByRole("button", { name: text.sections.systemIntegration, exact: true }).click();
    await page.waitForFunction(label => document.querySelector<HTMLInputElement>(`input[aria-label="${CSS.escape(label)}"]`)?.checked === true, copy.loginStartTitle);
    // 写入失败：开关回到重新读取的实际状态（仍为开），显示失败原因和重试入口，并且确实重新读取了一次。
    await page.evaluate(() => { (window as LoginWindow).__loginStartupSetError = "fixture-registry-write-failed"; });
    const before = await page.evaluate(() => (window as LoginWindow).__loginStartupRequests.length);
    await login.click();
    await page.getByRole("status").filter({ hasText: copy.loginStartWriteFailed }).waitFor();
    await page.waitForFunction(label => {
      const input = document.querySelector<HTMLInputElement>(`input[aria-label="${CSS.escape(label)}"]`);
      return input?.checked === true && !input.disabled;
    }, copy.loginStartTitle);
    assert.deepEqual(await page.evaluate(count => (window as LoginWindow).__loginStartupRequests.slice(count).map(item => item.type), before),
      ["shell.loginStartup.set", "shell.loginStartup.get"], `${option.id}: a failed write is followed by one read`);
    await page.evaluate(() => { (window as LoginWindow).__loginStartupSetError = null; });
    await page.getByRole("button", { name: copy.loginStartRetry, exact: true }).click();
    await page.waitForFunction(label => document.querySelector<HTMLInputElement>(`input[aria-label="${CSS.escape(label)}"]`)?.checked === false, copy.loginStartTitle);
    assert.equal(await page.getByRole("status").filter({ hasText: copy.loginStartWriteFailed }).count(), 0);
    await login.check();
    await page.waitForFunction(label => document.querySelector<HTMLInputElement>(`input[aria-label="${CSS.escape(label)}"]`)?.checked === true, copy.loginStartTitle);
    await page.evaluate(() => { (window as LoginWindow).__loginStartupError = "fixture-registry-unavailable"; });
    await page.getByRole("button", { name: text.sections.appearance, exact: true }).click();
    await page.getByRole("button", { name: text.sections.systemIntegration, exact: true }).click();
    await page.getByRole("status").filter({ hasText: copy.loginStartUnavailable }).waitFor();
    assert.equal(await login.isDisabled(), true, `${option.id}: failed registry read stays unavailable`);
    await page.getByRole("button", { name: copy.loginStartRetry, exact: true }).waitFor();
    checked.push(option.id);
    previous = option.id;
  }
  assert.equal(checked.length, 9);
  assert.deepEqual(backendWrites, [], "Login startup and frontend language changes must not mutate backend settings");
  assert.deepEqual(errors, []);
  return { languages: checked, nativeReadWriteAndReload: true, nativeFailureRendered: true, writeFailureRereadAndRetry: true, backendSettingsUntouched: true };
}
