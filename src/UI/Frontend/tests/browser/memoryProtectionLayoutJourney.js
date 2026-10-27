import assert from "node:assert/strict";
import { loadAppCopy } from "../../src/i18n/copy/appCopyLoader.ts";
import { loadSettingsText } from "../../src/i18n/settingsLoader.ts";
import { languageOptions } from "../../src/i18n/settingsLanguages.ts";
import { memoryProtectionShot } from "./memoryProtectionShots.js";
import { waitForContentSettled } from "./contentSettled.js";
import usabilityProbe from "./usabilityProbe.js";

// 主机内存保护两处界面在九种语言、1280×800 和窄宽度 800×600 下都不溢出：文字不超出所在区域、不被截断，
// 页面和分区都不出现横向滚动，所有可见输入都有名称。zh-CN 和 en-US 顺带截取主要状态的页面截图。
const viewports = [{ width: 1280, height: 800 }, { width: 800, height: 600 }];
const settingsScenarios = ["all-outcomes", "reserve-partial-cluster", "commit-unavailable", "tree-actions"];
const shotLanguages = new Set(["zh-CN", "en-US"]);

export default async function memoryProtectionLayoutJourney(page, baseUrl) {
  const errors = [];
  page.on("pageerror", error => errors.push(String(error)));
  await page.route("**/api/optimization/protection", route => route.fulfill({ json: [] }));
  await scenario({ gpuPlacementEnabled: true, runtimeEffectOwners: true });
  await page.setViewportSize(viewports[0]);
  await page.goto(baseUrl, { waitUntil: "domcontentloaded" });
  const checked = [];
  let previous = "zh-CN";
  const order = ["zh-CN", ...languageOptions.map(option => option.id).filter(id => id !== "system" && id !== "zh-CN")];

  async function scenario(body) {
    const response = await fetch(`${baseUrl}/__test/scenario`, { method: "POST", headers: { "Content-Type": "application/json" }, body: JSON.stringify(body) });
    assert.equal(response.ok, true, `scenario rejected: ${JSON.stringify(body)}`);
  }
  async function switchLanguage(language) {
    if (language === previous) return;
    const option = languageOptions.find(item => item.id === language);
    const oldApp = await loadAppCopy(previous);
    const oldSettings = await loadSettingsText(previous);
    // 软件详情弹窗可能还开着：重新载入页面再去外观里换语言。
    await page.goto(baseUrl, { waitUntil: "domcontentloaded" });
    await page.getByRole("navigation").getByRole("button", { name: oldApp.page.settings, exact: true }).click();
    await page.getByRole("button", { name: oldSettings.sections.appearance, exact: true }).click();
    await page.getByRole("button", { name: oldSettings.appearance.languageSelectLabel, exact: true }).click();
    await page.getByRole("option", { name: `${option.nativeLabel} · ${option.label}`, exact: true }).click();
    await page.getByRole("button", { name: oldSettings.actions.save, exact: true }).click();
    await page.waitForFunction(id => document.documentElement.lang === id, language);
    previous = language;
  }
  async function openSettings(language, name) {
    const app = await loadAppCopy(language);
    const settings = await loadSettingsText(language);
    await scenario({ memoryProtection: name });
    await page.goto(baseUrl, { waitUntil: "domcontentloaded" });
    await page.waitForFunction(id => document.documentElement.lang === id, language);
    await page.getByRole("navigation").getByRole("button", { name: app.page.settings, exact: true }).click();
    await page.getByRole("button", { name: settings.sections.memoryProtection, exact: true }).click();
    await page.getByText(app.memoryProtection.intro, { exact: true }).waitFor();
    await page.locator(".memory-protection-effective").waitFor();
    if (name === "all-outcomes" || name === "tree-actions") await page.locator(".memory-protection-record").first().waitFor();
    await waitForContentSettled(page);
  }
  async function openSoftware(language, name) {
    const app = await loadAppCopy(language);
    await scenario({ memoryProtection: name });
    await page.goto(baseUrl, { waitUntil: "domcontentloaded" });
    await page.waitForFunction(id => document.documentElement.lang === id, language);
    await page.getByRole("button", { name: app.page.components, exact: true }).click();
    await page.getByRole("tablist", { name: app.management.categoryNav }).getByRole("tab", { name: new RegExp(app.softwareKind.other) }).click();
    await page.locator(".software-item").filter({ hasText: "示例软件" })
      .getByRole("button", { name: app.management.softwareSettings, exact: true }).click();
    const dialog = page.getByRole("dialog", { name: "示例软件" });
    await dialog.getByRole("tab", { name: app.softwareDetail.tab.policy, exact: true }).click();
    await dialog.getByRole("heading", { name: app.memoryProtection.software.title, exact: true }).waitFor();
    if (name === "terminate-only") await dialog.getByText(app.memoryProtection.software.noDumpInactiveNow, { exact: true }).waitFor();
    await waitForContentSettled(page);
    await dialog.locator(".software-memory-protection-group").scrollIntoViewIfNeeded();
  }
  // 只滚动设置分区自己（滚动条所在的那一层），不让外层页面跟着被滚走。
  async function scrollPanelTo(selector) {
    // 滚动的是分区外面的某一层容器，不是分区本身：让目标自己滚到可见区顶端。
    await page.evaluate(selector => {
      document.querySelector(".memory-protection-settings")?.querySelector(selector)?.scrollIntoView({ block: "start" });
    }, selector);
  }
  async function expectFits(where, selector, boundary) {
    const result = await page.evaluate(({ selector, boundary }) => {
      const root = document.querySelector(selector);
      const frame = boundary ? root?.closest(boundary) : root;
      if (!root || !frame) return { missing: true };
      const bounds = frame.getBoundingClientRect();
      const offenders = [];
      for (const element of [root, ...root.querySelectorAll("*")]) {
        const rect = element.getBoundingClientRect();
        if (rect.width === 0 && rect.height === 0) continue;
        const style = getComputedStyle(element);
        const describe = reason => offenders.push({ reason, tag: element.tagName, className: String(element.className),
          text: (element.textContent ?? "").trim().slice(0, 80), left: Math.round(rect.left), right: Math.round(rect.right),
          frame: [Math.round(bounds.left), Math.round(bounds.right)], scrollWidth: element.scrollWidth, clientWidth: element.clientWidth });
        if (rect.right > bounds.right + 1 || rect.left < bounds.left - 1) describe("outside");
        else if (element.tagName !== "INPUT" && element.clientWidth > 0 && element.scrollWidth > element.clientWidth + 1
          && style.overflowX !== "visible") describe("clipped");
      }
      return { missing: false, offenders: offenders.slice(0, 8) };
    }, { selector, boundary });
    assert.equal(result.missing, false, `${where}: ${selector} is missing`);
    assert.deepEqual(result.offenders, [], `${where}: text overflows ${JSON.stringify(result.offenders)}`);
    const layout = await usabilityProbe(page);
    assert.equal(layout.document.scrollWidth > layout.document.clientWidth, false, `${where}: the page scrolls sideways`);
    assert.deepEqual(layout.visibleUnnamedInputs, [], `${where}: visible inputs without a name`);
  }

  for (const language of order) {
    await page.setViewportSize(viewports[0]);
    await switchLanguage(language);
    for (const viewport of viewports) {
      await page.setViewportSize(viewport);
      for (const name of settingsScenarios) {
        await openSettings(language, name);
        await expectFits(`${language} ${viewport.width}x${viewport.height} settings ${name}`, ".memory-protection-settings");
      }
      for (const name of ["disabled-ready", "terminate-only"]) {
        await openSoftware(language, name);
        await expectFits(`${language} ${viewport.width}x${viewport.height} software ${name}`, ".software-memory-protection-group", "[role='dialog']");
      }
    }
    if (shotLanguages.has(language)) {
      await page.setViewportSize(viewports[0]);
      for (const name of ["no-point", "active", "reserve-failed", "reserve-partial-cluster"]) {
        await openSettings(language, name);
        await memoryProtectionShot(page, `settings-${language}-${name}`);
      }
      await openSettings(language, "all-outcomes");
      await memoryProtectionShot(page, `settings-${language}-all-outcomes-full`, { tall: true });
      await scrollPanelTo(".memory-protection-records");
      await memoryProtectionShot(page, `settings-${language}-all-outcomes-records`);
      await openSettings(language, "tree-actions");
      await scrollPanelTo(".memory-protection-records");
      await memoryProtectionShot(page, `settings-${language}-tree-actions-records`);
      for (const name of ["disabled-ready", "terminate-only"]) {
        await openSoftware(language, name);
        await memoryProtectionShot(page, `software-${language}-${name === "disabled-ready" ? "dump-then-terminate" : name}`);
      }
      await page.setViewportSize(viewports[1]);
      await openSettings(language, "all-outcomes");
      await scrollPanelTo(".memory-protection-reserve-status");
      await memoryProtectionShot(page, `settings-${language}-narrow-reserve`);
    }
    checked.push(language);
  }
  assert.equal(checked.length, 9);
  assert.equal(errors.length, 0, `page errors: ${errors.join("\n")}`);
  return { languages: checked, viewports: viewports.map(item => `${item.width}x${item.height}`), settingsScenarios, softwareModes: ["dumpThenTerminate", "terminateOnly"] };
}
