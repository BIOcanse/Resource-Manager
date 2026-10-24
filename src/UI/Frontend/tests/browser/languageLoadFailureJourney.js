import assert from "node:assert/strict";
import { loadAppCopy } from "../../src/i18n/copy/appCopyLoader.ts";
import { loadSettingsText } from "../../src/i18n/settingsLoader.ts";
import { languageFailureText } from "../../src/i18n/languageFailureText.ts";

export default async function languageLoadFailureJourney(page, baseUrl) {
  const failures = [];
  let failedUrl = null;
  let intercepted = 0;
  let recoveredRequests = 0;
  const failedBundle = process.env.RM_LANGUAGE_FAILURE_BUNDLE === "settingsText" ? "settingsText" : "appCopy";
  page.on("pageerror", error => failures.push(String(error)));
  page.on("console", message => {
    if (message.type() !== "error") return;
    if (message.text().startsWith("[i18n]") && message.text().includes("ja-JP")) return;
    if (failedUrl && message.location().url === failedUrl && message.text().includes("Failed to load resource")) return;
    failures.push(message.text());
  });
  await page.route(/\/assets\/ja-JP-[^/]+\.js(?:\?.*)?$/, async route => {
    intercepted++;
    const response = await route.fetch();
    const isSettings = (await response.text()).includes(nextSettings.appearance.settingsLanguageDescription);
    if (!failedUrl && isSettings === (failedBundle === "settingsText")) {
      failedUrl = route.request().url();
      await route.abort("failed");
    } else {
      if (failedUrl && new URL(route.request().url()).pathname === new URL(failedUrl).pathname) recoveredRequests++;
      await route.fulfill({ response });
    }
  });
  const [oldApp, oldSettings, nextApp, nextSettings] = await Promise.all([
    loadAppCopy("zh-CN"), loadSettingsText("zh-CN"), loadAppCopy("ja-JP"), loadSettingsText("ja-JP")
  ]);
  await page.setViewportSize({ width: 1180, height: 850 });
  await page.goto(baseUrl, { waitUntil: "domcontentloaded" });
  await page.getByRole("navigation").getByRole("button", { name: oldApp.page.settings, exact: true }).click();
  await page.getByRole("button", { name: oldSettings.sections.appearance, exact: true }).click();
  assert.equal(await page.locator("html").getAttribute("lang"), "zh-CN");
  // Install the shell observer after browser bootstrap: advertising a host before
  // startup would correctly select the native session bootstrap transport.
  await page.evaluate(() => {
    window.__languageMessages = [];
    window.unboundWebView = { postMessage: message => {
      if (message.type === "shell.language") window.__languageMessages.push(message.language);
    } };
  });
  await page.getByRole("button", { name: oldSettings.appearance.languageSelectLabel, exact: true }).click();
  await page.getByRole("option", { name: "日本語 · Japanese", exact: true }).click();
  await page.getByRole("button", { name: oldSettings.actions.save, exact: true }).click();
  const notice = page.locator("[data-language-load-error]");
  await notice.waitFor({ state: "visible" });
  assert.equal(await notice.getAttribute("role"), "alert");
  assert.ok((await notice.innerText()).includes(languageFailureText("zh-CN").message));
  assert.equal(await page.locator("html").getAttribute("lang"), "zh-CN");
  assert.equal(await page.locator("html").getAttribute("dir"), "ltr");
  assert.equal((await page.locator("#activePageTitle").innerText()).trim(), oldApp.page.settings);
  await page.getByRole("button", { name: oldSettings.appearance.languageSelectLabel, exact: true }).waitFor();
  await page.getByText(oldSettings.appearance.settingsLanguageDescription, { exact: true }).waitFor();
  assert.deepEqual(await page.evaluate(() => window.__languageMessages), []);

  await page.evaluate(({ title, description }) => {
    window.__languageCommitSnapshots = [];
    window.__languageObserver = new MutationObserver(() => {
      if (document.documentElement.lang !== "ja-JP") return;
      window.__languageCommitSnapshots.push({
        language: document.documentElement.lang,
        title: document.querySelector("#activePageTitle")?.textContent?.trim(),
        descriptionPresent: document.body.textContent.includes(description),
        shell: window.__languageMessages.at(-1),
        expectedTitle: title
      });
    });
    window.__languageObserver.observe(document.documentElement, { subtree: true, childList: true, characterData: true, attributes: true });
  }, { title: nextApp.page.settings, description: nextSettings.appearance.settingsLanguageDescription });
  await notice.getByRole("button", { name: languageFailureText("zh-CN").retry, exact: true }).click();
  await page.waitForFunction(() => document.documentElement.lang === "ja-JP", null, { timeout: 10000 });
  await notice.waitFor({ state: "hidden" });
  await page.getByRole("button", { name: nextSettings.appearance.languageSelectLabel, exact: true }).waitFor();
  await page.getByText(nextSettings.appearance.settingsLanguageDescription, { exact: true }).waitFor();
  const snapshots = await page.evaluate(() => {
    window.__languageObserver.disconnect();
    return window.__languageCommitSnapshots;
  });
  assert.ok(snapshots.length > 0);
  for (const snapshot of snapshots) {
    assert.equal(snapshot.title, snapshot.expectedTitle);
    assert.equal(snapshot.descriptionPresent, true);
    assert.equal(snapshot.shell, "ja-JP");
  }
  assert.deepEqual(await page.evaluate(() => window.__languageMessages), ["ja-JP"]);
  assert.ok(recoveredRequests > 0, "Retry must refetch the failed locale asset");
  assert.deepEqual(failures, []);
  return { failedBundle, intercepted, recoveredRequests, retainedLanguage: "zh-CN", recoveredLanguage: "ja-JP", atomicSnapshots: snapshots.length };
}
