import assert from "node:assert/strict";
import { mkdir } from "node:fs/promises";
import { join } from "node:path";
import { fileURLToPath } from "node:url";
import { loadAppCopy } from "../../src/i18n/copy/appCopyLoader.ts";
import { loadSettingsText } from "../../src/i18n/settingsLoader.ts";
import { languageOptions } from "../../src/i18n/settingsLanguages.ts";
import { writeFrontendPreferences } from "./frontendPreferencesFixture.js";
import { NOT_GHOST } from "./liveLocator.js";
import { waitForContentSettled } from "./contentSettled.js";

const sectionOrder = ["runtimePolicy", "memory", "memoryProtection", "hardwareControl", "reportRules", "appearance", "performanceOverlay",
  "systemIntegration", "publicServices", "trustedAccess", "updates", "debug", "credits"];
const newSections = ["runtimePolicy", "memory", "hardwareControl", "reportRules", "publicServices", "trustedAccess"];
const readOnlySections = new Set(["performanceOverlay", "trustedAccess", "credits"]);
const sizes = [{ width: 1280, height: 800 }, { width: 800, height: 600 }];

/**
 * 设置分区重新划分：
 * - 导航按 运行策略、内存、主机内存保护、硬件控制、报告规则、外观、性能浮层、系统集成、公共服务、可信访问、更新与版本、调试、致谢 排列，没有「性能」；
 * - 每个分区放的是自己的内容（信息更新频率在外观末尾，调节权限在硬件控制，公共服务和可信访问各自一节）；
 * - 「恢复为默认」只恢复当前分区的字段；只读分区（性能浮层、可信访问、致谢）没有草稿操作；
 * - 单独的数字框（重新应用间隔、报告规则）输入框固定宽度、在卡片内边距之内，单位紧跟在旁边；
 * - 九种语言、1280×800 和 800×600 下每个分区都不横向溢出，行内容不越出行；新分区截简体中文和英文。
 */
export default async function settingsSectionsLayoutJourney(page, baseUrl) {
  const shots = process.env.RM_BROWSER_ARTIFACT_DIR
    ?? fileURLToPath(new URL("../../../../../.local/browser-gate/settings-sections-layout/", import.meta.url));
  await mkdir(shots, { recursive: true });
  const errors = [];
  page.on("pageerror", error => errors.push(String(error)));
  const setLanguage = async (language) => {
    await writeFrontendPreferences(page, (value) => { value.appearance.language = language; });
    await page.reload({ waitUntil: "domcontentloaded" });
    await page.waitForFunction(expected => document.documentElement.lang === expected, language);
  };
  const openSettings = async (language) => {
    const app = await loadAppCopy(language);
    const settings = await loadSettingsText(language);
    await page.getByRole("navigation").getByRole("button", { name: app.page.settings, exact: true }).click();
    await page.locator(`.settings-readonly-boundary${NOT_GHOST}`).waitFor();
    return { app, settings };
  };
  const openSection = async (settings, section) => {
    await page.locator(".settings-nav").getByRole("button", { name: settings.sections[section], exact: true }).click();
    await page.waitForFunction(label => document.querySelector(".settings-content-header h2")?.textContent?.trim() === label
      && !document.querySelector(".settings-content > [data-exit-ghost]"), settings.sections[section]);
  };

  await page.setViewportSize(sizes[0]);
  await page.goto(`${baseUrl}/`, { waitUntil: "domcontentloaded" });
  await setLanguage("zh-CN");
  let { app, settings } = await openSettings("zh-CN");

  // 导航次序和默认分区。
  const order = (await page.locator(".settings-nav .settings-nav-item").allTextContents()).map(text => text.trim());
  assert.deepEqual(order, sectionOrder.map(section => settings.sections[section]), `Wrong section order: ${order}`);
  assert.equal((await page.locator(".settings-content-header h2").textContent()).trim(), settings.sections.runtimePolicy,
    "Settings does not open on the runtime policy section");

  // 每个分区放的是自己的内容。
  const live = (selector) => page.locator(`.settings-readonly-boundary${NOT_GHOST} ${selector}`);
  const contains = async (section, present, absent = []) => {
    await openSection(settings, section);
    const text = await live(".settings-section-panel").first().innerText();
    for (const expected of present) assert.ok(text.includes(expected), `${section} is missing "${expected}"`);
    for (const unexpected of absent) assert.ok(!text.includes(unexpected), `${section} still shows "${unexpected}"`);
  };
  await contains("runtimePolicy", [settings.performance.preciseGpuPlacementTitle, settings.performance.gpuOverflowTitle,
    settings.performance.realtimeActionBudgetTitle], [settings.performance.automaticMemoryCleanupTitle, settings.performance.refreshCadenceTitle]);
  await contains("memory", [settings.performance.automaticMemoryCleanupTitle, settings.performance.memoryOptimizationTargetTitle],
    [settings.performance.gpuOverflowTitle]);
  await contains("memoryProtection", [app.memoryProtection.intro], [settings.performance.automaticMemoryCleanupTitle]);
  await contains("hardwareControl", [settings.hardwareControl.reassertionTitle, app.control.reassertionInterval, app.control.accessLevel.title]);
  await contains("reportRules", [app.reportRules.note]);
  await contains("appearance", [settings.appearance.uiStyleTitle, settings.performance.refreshCadenceTitle]);
  await contains("systemIntegration", [settings.systemIntegration.autoStartTitle, settings.systemIntegration.taskManagerTitle],
    [settings.systemIntegration.publicServiceTitle, settings.systemIntegration.lmStudioEndpointTitle]);
  await contains("publicServices", [settings.systemIntegration.publicServiceTitle, settings.systemIntegration.publicFileIndexTitle,
    settings.systemIntegration.publicDatabaseServiceTitle, settings.systemIntegration.publicAiModelCatalogTitle,
    settings.systemIntegration.lmStudioEndpointTitle, settings.systemIntegration.lmStudioAutoStartTitle],
    [settings.systemIntegration.autoStartTitle]);
  await openSection(settings, "trustedAccess");
  await page.locator("#trustedAccessManager").waitFor({ state: "visible" });
  // 只重复分区标题的内层标题已去掉：可信访问和报告规则里没有与分区同义的标题。
  assert.equal(await live(".trusted-access-manager > .settings-row-copy strong").count(), 0, "Trusted access repeats the section title");
  await openSection(settings, "reportRules");
  await live(".report-rule-group").first().waitFor();
  assert.equal(await live(".report-rule-settings > strong").count(), 0, "Report rules repeats the section title");

  // 只读分区没有草稿操作；可编辑分区有「恢复为默认」。
  for (const section of sectionOrder) {
    await openSection(settings, section);
    const restore = await page.locator(".settings-header-actions").getByRole("button", { name: settings.actions.restoreDefaults, exact: true }).count();
    assert.equal(restore, readOnlySections.has(section) ? 0 : 1, `${section}: wrong draft actions`);
  }

  // 「恢复为默认」只恢复当前分区。
  await openSection(settings, "runtimePolicy");
  const usage = page.getByRole("spinbutton", { name: settings.performance.gpuOverflowUsageLabel, exact: true });
  await usage.fill("60"); await usage.blur();
  await openSection(settings, "memory");
  const cleanup = page.getByRole("spinbutton", { name: settings.performance.physicalMemoryAutomaticCleanupLabel, exact: true });
  const defaultCleanup = await cleanup.inputValue();
  await cleanup.fill("20"); await cleanup.blur();
  await openSection(settings, "hardwareControl");
  const interval = page.getByRole("textbox", { name: app.control.reassertionInterval, exact: true });
  await interval.fill("337"); await interval.press("Enter");
  await openSection(settings, "memory");
  await page.locator(".settings-header-actions").getByRole("button", { name: settings.actions.restoreDefaults, exact: true }).click();
  assert.equal(await cleanup.inputValue(), defaultCleanup, "Memory reset did not restore the cleanup threshold");
  await openSection(settings, "runtimePolicy");
  assert.equal(await usage.inputValue(), "60", "Memory reset changed the runtime policy draft");
  await openSection(settings, "hardwareControl");
  assert.equal(await interval.inputValue(), "337", "Memory reset changed the hardware control draft");
  await page.locator(".settings-header-actions").getByRole("button", { name: settings.actions.restoreDefaults, exact: true }).click();
  assert.equal(await interval.inputValue(), "120", "Hardware control reset did not restore the interval");
  await openSection(settings, "runtimePolicy");
  assert.equal(await usage.inputValue(), "60", "Hardware control reset changed the runtime policy draft");
  await page.locator(".settings-header-actions").getByRole("button", { name: settings.actions.restoreDefaults, exact: true }).click();
  assert.equal(await usage.inputValue(), "95", "Runtime policy reset did not restore the overflow threshold");

  // 数字框：输入框固定宽度、在卡片内边距之内，含单位的标签紧贴输入框；报告规则的单位紧跟在输入框右侧。
  const fieldGeometry = () => page.evaluate(() => {
    const panel = document.querySelector(".settings-readonly-boundary:not([data-exit-ghost]) .settings-section-panel");
    const style = getComputedStyle(panel);
    const box = panel.getBoundingClientRect();
    const inner = { left: box.left + parseFloat(style.paddingLeft), right: box.right - parseFloat(style.paddingRight) };
    return [...panel.querySelectorAll(".settings-row > .settings-number-field")].map(field => {
      const input = field.querySelector("input").getBoundingClientRect();
      const own = field.getBoundingClientRect();
      const label = field.querySelector("span").getBoundingClientRect();
      const next = field.nextElementSibling?.tagName === "SPAN" ? field.nextElementSibling.getBoundingClientRect() : null;
      return { width: input.width, inside: input.left >= inner.left - 1 && input.right <= inner.right + 1,
        labelGap: input.top - label.bottom, unitGap: next ? next.left - Math.max(input.right, own.right) : null, fieldWiderThanInput: own.width - input.width,
        unitSameLine: next ? Math.abs((next.top + next.bottom) / 2 - (input.top + input.bottom) / 2) <= 4 : null };
    });
  });
  for (const section of ["memoryProtection", "hardwareControl", "reportRules"]) {
    await openSection(settings, section);
    if (section === "reportRules") await live(".report-rule-field .settings-number-field").first().waitFor();
    if (section === "memoryProtection") await live(".memory-protection-settings .settings-row > .settings-number-field").first().waitFor();
    const fields = await fieldGeometry();
    assert.ok(fields.length > 0, `${section}: no number field`);
    for (const field of fields) {
      assert.ok(Math.abs(field.width - 140) <= 1 && field.inside && field.labelGap <= 8,
        `${section}: number field is not a fixed-width field inside the card: ${JSON.stringify(field)}`);
      if (section === "reportRules") assert.ok(field.unitSameLine && field.unitGap >= 0 && field.unitGap <= 12 && field.fieldWiderThanInput <= 1,
        `${section}: the unit is not right beside the field: ${JSON.stringify(field)}`);
    }
  }

  // 新分区截图：简体中文和英文。
  const screenshots = [];
  for (const language of ["zh-CN", "en-US"]) {
    await setLanguage(language);
    ({ app, settings } = await openSettings(language));
    for (const section of [...newSections, "appearance", "systemIntegration"]) {
      await openSection(settings, section);
      if (section === "reportRules") await live(".report-rule-field").first().waitFor();
      if (section === "trustedAccess") await page.locator("#trustedAccessManager").waitFor({ state: "visible" });
      if (section === "hardwareControl") await live('.control-access-level [role="radio"][aria-checked="true"]').waitFor();
      screenshots.push(join(shots, `settings-${section}-${language}.png`));
      // 换页和换分区的淡出层都走完再截，画面里只有这个分区。
      await page.waitForFunction(() => document.body.dataset.page === "settings" && !document.querySelector("[data-exit-ghost]"));
      await page.screenshot({ path: screenshots.at(-1) });
    }
  }

  // 九种语言、两种窗口大小：每个分区都不横向溢出，行内容不越出行，按钮不被截掉。
  const layouts = [];
  const layoutFailures = [];
  for (const option of languageOptions.filter(option => option.id !== "system")) {
    await page.setViewportSize(sizes[0]);
    await setLanguage(option.id);
    ({ settings } = await openSettings(option.id));
    for (const section of sectionOrder) {
      await openSection(settings, section);
      for (const size of sizes) {
        await page.setViewportSize(size);
        // 改窗口大小后等内容区里的动画放完再量：动画中的位移会临时撑出横向滚动。
        await waitForContentSettled(page);
        await page.evaluate(async () => {
          await new Promise(resolve => requestAnimationFrame(() => requestAnimationFrame(resolve)));
          const running = document.querySelector(".settings-content").getAnimations({ subtree: true });
          await Promise.race([Promise.allSettled(running.map(animation => animation.finished)), new Promise(resolve => setTimeout(resolve, 3000))]);
        });
        const layout = await page.evaluate(() => {
          const content = document.querySelector(".settings-content");
          const stage = document.querySelector(".settings-readonly-boundary:not([data-exit-ghost])");
          const rows = [...stage.querySelectorAll(".settings-row")].filter(row => row.getClientRects().length > 0);
          const clippedRows = rows.filter(row => {
            const box = row.getBoundingClientRect();
            return [...row.querySelectorAll("button, input, select, textarea, [role=radiogroup]")]
              .filter(control => control.getClientRects().length > 0)
              .some(control => { const own = control.getBoundingClientRect(); return own.left < box.left - 1 || own.right > box.right + 1; });
          }).map(row => row.textContent.trim().slice(0, 60));
          // 溢出时写出越过内容区右边的最内层元素，失败信息直接指向原因。
          const limit = content.getBoundingClientRect().right + 1;
          const past = [...content.querySelectorAll("*")].filter(node => node.getClientRects().length > 0
            && node.getBoundingClientRect().right > limit);
          const widest = past.filter(node => !past.some(other => other !== node && node.contains(other)))
            .slice(0, 5).map(node => `${node.tagName.toLowerCase()}.${[...node.classList].join(".")} ${Math.round(node.getBoundingClientRect().right - limit)}px "${node.textContent.trim().slice(0, 60)}"`);
          return {
            document: document.documentElement.scrollWidth <= document.documentElement.clientWidth,
            content: content.scrollWidth <= content.clientWidth + 1,
            clippedRows,
            ...(widest.length ? { widest } : {}),
            ...(content.scrollWidth > content.clientWidth + 1 ? { scrollWidth: content.scrollWidth, clientWidth: content.clientWidth } : {})
          };
        });
        if (!(layout.document && layout.content && layout.clippedRows.length === 0))
          layoutFailures.push(`${option.id} ${section} ${size.width}x${size.height}: ${JSON.stringify(layout)}`);
        layouts.push({ language: option.id, section, ...size });
      }
      await page.setViewportSize(sizes[0]);
    }
  }

  assert.deepEqual(layoutFailures, [], "Settings layout overflows:\n" + layoutFailures.join("\n"));
  assert.deepEqual(errors, []);
  return { screenshots, layouts: layouts.length };
}
