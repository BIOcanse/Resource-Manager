import assert from "node:assert/strict";
import { mkdir } from "node:fs/promises";
import { join } from "node:path";
import { fileURLToPath } from "node:url";
import { loadAppCopy } from "../../src/i18n/copy/appCopyLoader.ts";
import { loadSettingsText } from "../../src/i18n/settingsLoader.ts";
import { writeFrontendPreferences } from "./frontendPreferencesFixture.js";

/**
 * 设置 → 系统集成 → 任务管理器快捷键替换：说明写的是实际的替换范围（tests/taskManagerReplacementCopy.test.ts 核对九语内容），
 * 这里核对界面上显示的就是这段说明、整行不溢出，并截简体中文和英文两张。
 */
export default async function taskManagerCopyJourney(page, baseUrl) {
  const shots = process.env.RM_BROWSER_ARTIFACT_DIR
    ?? fileURLToPath(new URL("../../../../../.local/browser-gate/task-manager-copy/", import.meta.url));
  await mkdir(shots, { recursive: true });
  const errors = [];
  page.on("pageerror", error => errors.push(String(error)));
  await page.setViewportSize({ width: 1280, height: 800 });
  await page.goto(`${baseUrl}/`, { waitUntil: "domcontentloaded" });
  const results = [];
  for (const language of ["zh-CN", "en-US"]) {
    const app = await loadAppCopy(language);
    const settings = await loadSettingsText(language);
    await writeFrontendPreferences(page, (value) => { value.appearance.language = language; });
    await page.reload({ waitUntil: "domcontentloaded" });
    await page.waitForFunction(expected => document.documentElement.lang === expected, language);
    await page.getByRole("navigation").getByRole("button", { name: app.page.settings, exact: true }).click();
    await page.getByRole("button", { name: settings.sections.systemIntegration, exact: true }).click();
    const row = page.locator(".settings-row").filter({ hasText: settings.systemIntegration.taskManagerTitle });
    await row.waitFor();
    const description = (await row.locator(".settings-row-copy span").first().textContent())?.trim();
    assert.equal(description, settings.systemIntegration.taskManagerDescription, `${language}: the row does not show the current description`);
    const layout = await row.evaluate(element => ({ scroll: element.scrollWidth, client: element.clientWidth }));
    assert.ok(layout.scroll <= layout.client + 1, `${language}: the row overflows: ${JSON.stringify(layout)}`);
    const shot = join(shots, `task-manager-copy-${language}.png`);
    await row.screenshot({ path: shot });
    results.push({ language, description, screenshot: shot });
  }
  assert.deepEqual(errors, []);
  return { languages: results };
}
