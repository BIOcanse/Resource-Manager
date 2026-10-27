import assert from "node:assert/strict";
import { languageOptions } from "../../src/i18n/settingsLanguages.ts";
import { loadAppCopy } from "../../src/i18n/copy/appCopyLoader.ts";
import { writeFrontendPreferences } from "./frontendPreferencesFixture.js";

/*
 * 资源列表标题行在窗口最小宽度下放得下：主窗口无边框，MinimumSize 760×520（src/UI/Core/Shell/MainForm.cs），
 * 所以 100% 缩放下页面视口最窄是 760 CSS 像素；另测 744（help-hint 那轮按带边框窗口的客户区量到 zh-CN 在这里凸出 4 px）。
 * 九种语言、看和编辑两种状态下，右侧操作区本身以及标题、计数、搜索框、视图切换、编辑（编辑时还有取消）都在标题行的框内，
 * 标题行也没有横向溢出（scrollWidth 不超过 clientWidth）；放不下时标题行换行，控件不挪到别处。
 */
const narrowWidths = [760, 744];

export default async function resourceTableHeaderFitJourney(page, baseUrl) {
  const results = [];
  for (const width of narrowWidths) {
  await page.setViewportSize({ width, height: 520 });
  await page.goto(baseUrl, { waitUntil: "domcontentloaded" });
  for (const language of languageOptions.map(option => option.id).filter(id => id !== "system")) {
    const app = await loadAppCopy(language);
    await writeFrontendPreferences(page, value => { value.appearance.language = language; });
    await page.goto(baseUrl, { waitUntil: "domcontentloaded" });
    await page.getByRole("navigation").getByRole("button", { name: app.page.monitor, exact: true }).click();
    const panel = page.locator(".resource-table-panel");
    await panel.locator(".panel-header .resource-table-search input").waitFor();
    await panel.locator(":scope > .panel-header h2").getByText(app.resourceTable.panel, { exact: true }).waitFor();
    await panel.scrollIntoViewIfNeeded();
    const viewing = await headerFit(panel);
    await panel.getByRole("button", { name: app.resourceTableView.editLabel, exact: true }).click();
    await panel.getByRole("button", { name: app.resourceTableView.cancelEditLabel, exact: true }).waitFor();
    const editing = await headerFit(panel);
    await panel.getByRole("button", { name: app.resourceTableView.cancelEditLabel, exact: true }).click();
    for (const [state, fit] of [["viewing", viewing], ["editing", editing]]) {
      assert.deepEqual(fit.outside, [], `${width}px ${language} ${state}: header controls stay inside the header ${JSON.stringify(fit)}`);
    }
    results.push({ width, language, viewing: viewing.header, editing: editing.header });
  }
  }
  await writeFrontendPreferences(page, value => { value.appearance.language = "zh-CN"; });
  return results;
}

async function headerFit(panel) {
  return panel.locator(":scope > .panel-header").evaluate(header => {
    const box = header.getBoundingClientRect();
    const controls = [...header.querySelectorAll(".panel-header-actions, h2, .count-badge, .save-state, .resource-table-search, .resource-table-mode-switch, button")]
      .filter(element => !element.closest(".resource-table-mode-switch") || element.matches(".resource-table-mode-switch"));
    const outside = controls.flatMap(element => {
      const rect = element.getBoundingClientRect();
      if (rect.width === 0) return [];
      const overflow = Math.max(box.left - rect.left, rect.right - box.right);
      return overflow > 0.5 ? [{ control: element.className || element.tagName, left: rect.left, right: rect.right, overflow }] : [];
    });
    const actions = header.querySelector(".panel-header-actions").getBoundingClientRect();
    const scrollOverflow = header.scrollWidth - header.clientWidth;
    if (scrollOverflow > 0) outside.push({ control: "panel-header scroll", overflow: scrollOverflow });
    return { header: { left: box.left, right: box.right, height: box.height, actionsLeft: actions.left, actionsRight: actions.right, actionsTop: actions.top - box.top }, outside };
  });
}
