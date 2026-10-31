import type { ConcreteAppLanguageMode } from "../../src/i18n/settingsTypes.ts";
import type { PerformanceOverlayEntry, PerformanceOverlayItem, AppLanguageMode, AppSettingsResult } from "../../src/types.ts";
type OverlayFixture = { overlays: Record<string, Partial<PerformanceOverlayEntry>>; software?: { id: string; name: string }[]; modules?: { softwareId: string; moduleIds: string[] }[] };
import type { Page, Locator } from "playwright-core";
import assert from "node:assert/strict";
import { mkdir } from "node:fs/promises";
import { join } from "node:path";
import { fileURLToPath } from "node:url";
import { loadAppCopy } from "../../src/i18n/copy/appCopyLoader.ts";
import { loadSettingsText } from "../../src/i18n/settingsLoader.ts";
import { languageOptions } from "../../src/i18n/settingsLanguages.ts";
import { writeFrontendPreferences } from "./frontendPreferencesFixture.ts";
import { NOT_GHOST } from "./liveLocator.ts";

const injectedModules = ["performance-overlay-preload", "performance-overlay-vulkan"];
// 浮窗设置是后端设置 performanceOverlay 的条目：省略的字段由后端展开成默认值（docs/frontend_block_trees_2026-10-26.md）。
const overlay = (patch: Partial<PerformanceOverlayEntry>) => patch;
const item = (kind: PerformanceOverlayItem["kind"], id: string): PerformanceOverlayItem => ({ kind, id, source: "auto", intervalMs: null, syncPrecision: "mid" });
// 多软件的情形：外置、注入（模块已登记）、注入但模块没有登记（开关是后端的值，照样列出）各一个启用；一个关闭的不列出；
// 一个启用但软件列表里已经没有它，按软件 ID 列出、不能打开。
const mixed: OverlayFixture = {
  overlays: {
    "frontend-harness.software": overlay({ enabled: true, mode: "external", items: [item("frames", "frames.fps"), item("software", "cpu.usage"), item("metric", "gpu.0.usage")], anchor: "topRight" }),
    "overlay.injected.app": overlay({ enabled: true, mode: "injected", items: [item("frames", "frames.fps")], anchor: "bottomLeft" }),
    "overlay.disabled.app": overlay({ enabled: false, mode: "external" }),
    "overlay.unregistered.app": overlay({ enabled: true, mode: "injected" }),
    "overlay.removed.app": overlay({ enabled: true, mode: "external", items: [item("frames", "frames.fps"), item("frames", "frames.present")], anchor: "bottomRight" })
  },
  software: [
    { id: "overlay.injected.app", name: "Injected Sample" },
    { id: "overlay.disabled.app", name: "Disabled Sample" },
    { id: "overlay.unregistered.app", name: "Unregistered Sample" }
  ],
  modules: [{ softwareId: "overlay.injected.app", moduleIds: injectedModules }]
};

/**
 * 设置 → 性能浮层（外观之后）：启用浮层的软件总览。
 * - 没有：写没有软件启用，并说明在软件详细信息的浮层分页里启用；
 * - 一个外置：一行，写显示方式、指标数和位置；按钮打开这个软件的详细信息并停在浮层分页，关上后总览重新读；
 * - 多个混合：只列后端设置里启用的，软件列表里没有的按 ID 列出且不能打开；
 * - 九种语言在 1280×800 和 800×600 下不溢出、没有被截掉的按钮；简体中文和英文截图。
 * 只读后端设置的 performanceOverlay 一节和浮层分页本来就读的启动模块登记。
 */
export default async function performanceOverlaySettingsJourney(page: Page, baseUrl: string) {
  const shots = process.env.RM_BROWSER_ARTIFACT_DIR
    ?? fileURLToPath(new URL("../../../../../.local/browser-gate/performance-overlay-settings/", import.meta.url));
  await mkdir(shots, { recursive: true });
  const errors: string[] = [];
  page.on("pageerror", error => errors.push(String(error)));
  const scenario = async (value: Record<string, unknown>) => {
    const response = await fetch(`${baseUrl}/__test/scenario`, {
      method: "POST", headers: { "Content-Type": "application/json" }, body: JSON.stringify(value)
    });
    assert.ok(response.ok, "Scenario update failed");
  };
  // 后端设置里整节换成这些条目：已有而这里没有的软件写 null 删掉。
  const writeBackendOverlays = async (overlays: OverlayFixture["overlays"]) => {
    const current = await (await fetch(`${baseUrl}/api/settings/app`)).json() as AppSettingsResult;
    const software = Object.fromEntries(Object.keys(current.settings!.performanceOverlay?.software ?? {}).map(id => [id, null]));
    const response = await fetch(`${baseUrl}/api/settings/app`, {
      method: "PATCH", headers: { "Content-Type": "application/json" },
      body: JSON.stringify({ expectedRevision: current.revision, changes: { performanceOverlay: { software: { ...software, ...overlays } } } })
    });
    assert.ok(response.ok, "Backend overlay settings fixture failed");
  };
  const prepare = async ({ overlays, software = [], modules = [] }: OverlayFixture, language: AppLanguageMode) => {
    await scenario({ extraSoftware: software, startupModuleSelections: modules });
    await writeBackendOverlays(overlays);
    await writeFrontendPreferences(page, (value) => { value.appearance.language = language; });
    await page.reload({ waitUntil: "domcontentloaded" });
    await page.waitForFunction(expected => document.documentElement.lang === expected, language);
  };
  const openSection = async (language: ConcreteAppLanguageMode) => {
    const app = await loadAppCopy(language as ConcreteAppLanguageMode);
    const settings = await loadSettingsText(language);
    await page.getByRole("navigation").getByRole("button", { name: app.page.settings, exact: true }).click();
    await page.getByRole("button", { name: settings.sections.performanceOverlay, exact: true }).click();
    // 换分区时旧分区留一层淡出的幽灵，量布局前等它走完。
    await page.waitForFunction(() => !document.querySelector<HTMLElement>(".settings-content > [data-exit-ghost]")!);
    return { app, settings, panel: page.locator(`.performance-overlay-settings${NOT_GHOST}`) };
  };
  const rowsOf = (panel: Locator) => panel.locator(".performance-overlay-row");

  await page.setViewportSize({ width: 1280, height: 800 });
  await page.goto(`${baseUrl}/`, { waitUntil: "domcontentloaded" });
  const screenshots: string[] = [];

  // 导航次序：外观之后紧跟性能浮层。
  {
    const settings = await loadSettingsText("zh-CN");
    await openSection("zh-CN");
    const order = (await page.locator(".settings-nav .settings-nav-item").allTextContents()).map(text => text.trim());
    assert.equal(order[order.indexOf(settings.sections.appearance) + 1], settings.sections.performanceOverlay, `Overlay section is not right after appearance: ${order}`);
    assert.equal(await page.locator(".settings-header-actions button").count(), 0, "The read-only overview shows draft actions");
  }

  for (const language of ["zh-CN", "en-US"] as const) {
    // 没有。
    await prepare({ overlays: {} }, language);
    let { app, settings, panel } = await openSection(language);
    const empty = panel.locator(".content-state.empty");
    await empty.filter({ hasText: settings.performanceOverlay.emptyTitle }).waitFor();
    assert.ok((await empty.textContent())!.includes(settings.performanceOverlay.emptyDetail(app.performanceOverlay.tab)), `${language}: the empty state does not say how to turn an overlay on`);
    await panel.getByText(settings.performanceOverlay.intro, { exact: true }).waitFor();
    screenshots.push(join(shots, `performance-overlay-none-${language}.png`));
    await panel.screenshot({ path: screenshots.at(-1) });

    // 一个外置。
    await prepare({ overlays: { "frontend-harness.software": mixed.overlays["frontend-harness.software"] } }, language);
    ({ app, settings, panel } = await openSection(language));
    await rowsOf(panel).first().waitFor();
    assert.equal(await rowsOf(panel).count(), 1, `${language}: one external overlay should give one row`);
    const one = rowsOf(panel).first();
    const expectedSummary = [app.performanceOverlay.external, settings.performanceOverlay.itemCount(3),
      settings.performanceOverlay.position(app.performanceOverlay.topRight)].join(" · ");
    assert.equal((await one.locator(".settings-row-copy strong").textContent())!.trim(), "示例软件");
    assert.equal((await one.locator(".settings-row-copy span").textContent())!.trim(), expectedSummary, `${language}: wrong row summary`);
    screenshots.push(join(shots, `performance-overlay-one-${language}.png`));
    await panel.screenshot({ path: screenshots.at(-1) });
    // 按钮打开这个软件的详细信息，停在浮层分页；改动在那里保存，关上后这里重新读。
    await one.getByRole("button", { name: settings.performanceOverlay.openLabel("示例软件"), exact: true }).click();
    const dialog = page.locator("#softwareDetailModal");
    await dialog.getByRole("tab", { name: app.performanceOverlay.tab, exact: true, selected: true }).waitFor();
    await page.locator(`.overlay-settings-fields${NOT_GHOST}`).waitFor({ state: "visible" });
    // 软件元数据之后到达也不把分页切回概览。
    await page.waitForTimeout(1500);
    assert.ok(await dialog.getByRole("tab", { name: app.performanceOverlay.tab, exact: true, selected: true }).isVisible(), `${language}: the detail dialog left the overlay tab`);
    await page.keyboard.press("Escape");
    await dialog.waitFor({ state: "hidden" });
    await writeBackendOverlays({ "frontend-harness.software": { ...mixed.overlays["frontend-harness.software"], anchor: "bottomLeft" } });
    await page.getByRole("button", { name: settings.performanceOverlay.openLabel("示例软件"), exact: true }).click();
    await dialog.getByRole("tab", { name: app.performanceOverlay.tab, exact: true, selected: true }).waitFor();
    await page.keyboard.press("Escape");
    await dialog.waitFor({ state: "hidden" });
    await one.locator(".settings-row-copy span").filter({ hasText: settings.performanceOverlay.position(app.performanceOverlay.bottomLeft) }).waitFor();

    // 多个混合。
    await prepare(mixed, language);
    ({ app, settings, panel } = await openSection(language));
    await rowsOf(panel).nth(3).waitFor();
    const ids = await rowsOf(panel).evaluateAll((rows: HTMLElement[]) => rows.map(row => row.dataset.softwareId));
    assert.deepEqual([...ids].sort(), ["frontend-harness.software", "overlay.injected.app", "overlay.removed.app", "overlay.unregistered.app"],
      `${language}: wrong rows ${ids}`);
    const injected = rowsOf(panel).filter({ hasText: "Injected Sample" });
    assert.equal((await injected.locator(".settings-row-copy span:not([role='note'])").textContent())!.trim(), [app.performanceOverlay.injected,
      settings.performanceOverlay.itemCount(1), settings.performanceOverlay.position(app.performanceOverlay.bottomLeft)].join(" · "));
    const removed = panel.locator('.performance-overlay-row[data-software-id="overlay.removed.app"]');
    assert.equal((await removed.locator(".settings-row-copy strong").textContent())!.trim(), "overlay.removed.app");
    const removedButton = removed.getByRole("button");
    assert.ok(await removedButton.isDisabled(), `${language}: a software missing from the list can still be opened`);
    // 打不开的原因写在这一行的说明里（禁用的按钮收不到指针）。
    assert.equal(await removedButton.getAttribute("title"), null);
    assert.equal((await removed.locator(".settings-row-copy [role='note']").textContent())!.trim(), settings.performanceOverlay.softwareMissing);
    screenshots.push(join(shots, `performance-overlay-mixed-${language}.png`));
    await panel.screenshot({ path: screenshots.at(-1) });
  }

  // 九种语言、两种窗口大小：多软件情形不溢出，按钮完整可见。
  const layouts: { language: string; width: number; height: number }[] = [];
  for (const option of languageOptions.filter(option => option.id !== "system")) {
    await prepare(mixed, option.id);
    const { panel } = await openSection(option.id as ConcreteAppLanguageMode);
    await rowsOf(panel).nth(3).waitFor();
    await page.waitForFunction(() => !document.querySelector(".settings-content > [data-exit-ghost]"));
    for (const size of [{ width: 1280, height: 800 }, { width: 800, height: 600 }]) {
      await page.setViewportSize(size);
      const layout = await page.evaluate(() => {
        const content = document.querySelector<HTMLElement>(".settings-content")!;
        const rows = [...document.querySelectorAll<HTMLElement>(".performance-overlay-row")].map(row => {
          const box = row.getBoundingClientRect();
          const button = row.querySelector<HTMLButtonElement>("button")!.getBoundingClientRect();
          const copy = row.querySelector<HTMLElement>(".settings-row-copy")!.getBoundingClientRect();
          return { inside: button.left >= box.left - 1 && button.right <= box.right + 1, overlap: copy.right > button.left + 1 && copy.bottom > button.top + 1 && button.bottom > copy.top + 1 };
        });
        return {
          document: document.documentElement.scrollWidth <= document.documentElement.clientWidth,
          content: content.scrollWidth <= content.clientWidth + 1,
          rows
        };
      });
      assert.ok(layout.document && layout.content && layout.rows.every(row => row.inside && !row.overlap),
        `${option.id} ${size.width}x${size.height}: overlay overview overflows ${JSON.stringify(layout)}`);
      layouts.push({ language: option.id, ...size });
    }
    await page.setViewportSize({ width: 1280, height: 800 });
  }

  assert.deepEqual(errors, []);
  return { screenshots, layouts: layouts.length };
}
