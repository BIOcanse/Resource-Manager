import { loadAppCopy } from "../../src/i18n/copy/appCopyLoader.ts";
import { loadSettingsText } from "../../src/i18n/settingsLoader.ts";
import { languageOptions } from "../../src/i18n/settingsLanguages.ts";

/*
 * 会切换文字的按钮（刷新 / 刷新中、扫描 / 正在扫描、编辑 / 保存 / 保存中）在九种语言下都不改变宽度。
 * 每个页面上的每个 StableLabel 按钮依次显示它的每个候选，按钮宽度必须不变，候选文字也不能被裁掉。
 */
export default async function stableButtonLabelsJourney(page, baseUrl) {
  const failures = [];
  page.on("pageerror", error => failures.push(String(error)));
  // 性能优化页只在优化运行时可用时出现在导航里。
  const scenario = await fetch(`${baseUrl}/__test/scenario`, {
    method: "POST", headers: { "Content-Type": "application/json" },
    body: JSON.stringify({ optimizationRuntime: true })
  });
  if (!scenario.ok) throw new Error(`Unable to enable the optimization runtime: ${scenario.status}`);
  await page.setViewportSize({ width: 1280, height: 860 });
  await page.goto(`${baseUrl}/`, { waitUntil: "domcontentloaded" });
  let previous = "zh-CN";
  const switchLanguage = async (option) => {
    const oldApp = await loadAppCopy(previous);
    const oldSettings = await loadSettingsText(previous);
    await page.getByRole("navigation").getByRole("button", { name: oldApp.page.settings, exact: true }).click();
    await page.getByRole("button", { name: oldSettings.sections.appearance, exact: true }).click();
    await page.getByRole("button", { name: oldSettings.appearance.languageSelectLabel, exact: true }).click();
    await page.getByRole("option", { name: `${option.nativeLabel} · ${option.label}`, exact: true }).click();
    if (option.id !== previous) await page.getByRole("button", { name: oldSettings.actions.save, exact: true }).click();
    await page.waitForFunction(language => document.documentElement.lang === language, option.id);
    previous = option.id;
  };
  const checked = [];
  for (const option of languageOptions.filter(option => option.id !== "system")) {
    await switchLanguage(option);
    const app = await loadAppCopy(option.id);
    // 每页等到它自己的那颗按钮出现再量，免得量到上一页正在退场的按钮。
    const pages = [
      ["settings", ".settings-page .stable-label"],
      ["components", ".panel-refresh-button .stable-label"],
      ["diskUsage", ".disk-usage-setup-actions .stable-label"],
      ["optimization", ".optimization-report-filter ~ button .stable-label"],
      ["monitor", "[data-focus-key=\"monitor-dashboard-edit\"] .stable-label"]
    ];
    for (const [pageId, readySelector] of pages) {
      if (pageId !== "settings") {
        await page.getByRole("navigation").getByRole("button", { name: app.page[pageId], exact: true }).click();
      }
      await page.locator(readySelector).first().waitFor();
      const buttons = await page.evaluate(() => {
        const visible = (element) => element.getClientRects().length > 0;
        return [...document.querySelectorAll("button .stable-label")].filter(visible).map((label) => {
          const button = label.closest("button");
          const candidates = [...label.children];
          const initial = candidates.map((candidate) => candidate.classList.contains("stable-label-inactive"));
          const widths = candidates.map((_, index) => {
            candidates.forEach((candidate, other) => candidate.classList.toggle("stable-label-inactive", other !== index));
            return Math.round(button.getBoundingClientRect().width * 100) / 100;
          });
          const clipped = candidates.some((candidate) => candidate.getBoundingClientRect().width > label.getBoundingClientRect().width + 0.5);
          candidates.forEach((candidate, index) => candidate.classList.toggle("stable-label-inactive", initial[index]));
          return { labels: candidates.map((candidate) => candidate.textContent), widths, clipped, name: button.innerText.trim() };
        });
      });
      if (buttons.length === 0) failures.push(`${option.id}/${pageId}: no toggling button was found`);
      for (const button of buttons) {
        if (new Set(button.widths).size !== 1 || button.clipped) {
          failures.push(`${option.id}/${pageId}: button width changes with its label: ${JSON.stringify(button)}`);
        }
        if (!button.labels.includes(button.name)) {
          failures.push(`${option.id}/${pageId}: visible text is not one of the candidates: ${JSON.stringify(button)}`);
        }
      }
      checked.push(`${option.id}/${pageId}:${buttons.length}`);
    }
  }
  // 结束时回到开始时的简体中文，后续检查看到的页面和进来时一样。
  await switchLanguage(languageOptions.find(option => option.id === "zh-CN"));
  if (failures.length) throw new Error(failures.join("\n"));
  return { checked };
}
