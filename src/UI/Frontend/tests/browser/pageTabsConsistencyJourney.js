import { loadAppCopy } from "../../src/i18n/copy/appCopyLoader.ts";
import { loadSettingsText } from "../../src/i18n/settingsLoader.ts";

/**
 * 选择项一致，四种界面风格下逐组比对未选中项和选中项的样子：
 * - 分页（styles/page-tabs.css 的 .page-tab）：组件与软件、详细信息的子页签，设置导航，软件详情，任务中心，都和一级页签一样。
 * - 分段选择（styles/segmented.css 的 .segment）：设备拓扑的外部／内部、设置→外观的分段，都和监视页资源表的软件级／进程级／性能一样。
 * 只比样子：底色、文字色、边框、圆角、字号字重、投影；尺寸和排列各处自己定，不比，分段的字号也是各处自己定。
 * 带选中指示条的组（docs/page_motion_2026-10-25.md）：项本身只有文字，未选中的样子画在项的 ::before 上，选中的样子是组里那条指示条，
 * 三处都比：项、项的 ::before，以及选中项所在组的指示条。
 * 基准先在监视页按四种风格各记一次，切到别的页后不再读它（离开的页面可能已卸载）。
 * 风格直接写到根元素的 data-ui-style 上（外观的唯一发布点，见 presentation/documentAppearance.ts）：
 * 这里验的是样式表，不是外观设置的保存流程。
 */
export default async function pageTabsConsistencyJourney(page, baseUrl) {
  const app = await loadAppCopy("zh-CN");
  const settingsText = await loadSettingsText("zh-CN");
  const styles = [["standard", "light"], ["soft", "light"], ["outline", "paper"], ["terminal", "phosphor"]];
  const properties = ["backgroundColor", "color", "borderTopColor", "borderTopStyle", "borderTopWidth", "borderTopLeftRadius", "fontSize", "fontWeight", "boxShadow"];
  const segmentProperties = properties.filter((name) => name !== "fontSize");
  const failures = [];

  const setScenario = async (value) => {
    const response = await fetch(`${baseUrl}/__test/scenario`, {
      method: "POST",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify(value)
    });
    if (!response.ok) throw new Error(`Unable to update scenario: ${response.status}`);
  };
  const look = (locator) => locator.evaluate((element, names) => {
    const computed = getComputedStyle(element);
    const before = getComputedStyle(element, "::before");
    const indicator = element.classList.contains("active")
      ? element.closest(".has-selection-indicator")?.querySelector(":scope > .selection-indicator")
      : null;
    const indicatorStyle = indicator ? getComputedStyle(indicator) : null;
    return Object.fromEntries([
      ...names.map((name) => [name, computed[name]]),
      ...names.map((name) => [`::before ${name}`, before[name]]),
      ...names.map((name) => [`indicator ${name}`, indicatorStyle ? indicatorStyle[name] : null])
    ]);
  }, properties);
  const applyStyle = async (uiStyle, theme) => {
    await page.evaluate(([style, palette]) => {
      document.documentElement.dataset.uiStyle = style;
      document.documentElement.dataset.theme = palette;
    }, [uiStyle, theme]);
    // 鼠标移开、等过渡走完：读到的是静止的未选中和选中，不是悬停。
    const size = page.viewportSize();
    await page.mouse.move(size.width - 4, size.height - 4);
    await page.waitForTimeout(450);
  };
  /** 一组选择项里静止的选中项和一个未选中项。 */
  const pair = (scope, item) => ({
    active: scope.locator(`${item}.active:visible`).first(),
    inactive: scope.locator(`${item}:not(.active):visible`).first()
  });
  /** 四种风格下一组的样子：{ 风格: { active, inactive } }。 */
  const record = async (scope, item) => {
    const items = pair(scope, item);
    await items.active.waitFor({ state: "visible", timeout: 7000 });
    await items.inactive.waitFor({ state: "visible", timeout: 7000 });
    const looks = {};
    for (const [uiStyle, theme] of styles) {
      await applyStyle(uiStyle, theme);
      looks[uiStyle] = { active: await look(items.active), inactive: await look(items.inactive) };
    }
    return looks;
  };
  const compare = async (group, scope, item, expected, names) => {
    const actual = await record(scope, item);
    for (const [uiStyle] of styles) {
      for (const state of ["active", "inactive"]) {
        for (const name of names.flatMap((property) => [property, `::before ${property}`, `indicator ${property}`])) {
          if (actual[uiStyle][state][name] !== expected[uiStyle][state][name]) {
            failures.push(`${group} ${uiStyle} ${state} ${name}: ${actual[uiStyle][state][name]} (reference: ${expected[uiStyle][state][name]})`);
          }
        }
      }
    }
  };

  await setScenario({ capabilities: "ready", settings: "ready", operations: "mixed" });
  await page.setViewportSize({ width: 1280, height: 820 });
  await page.goto(`${baseUrl}/`, { waitUntil: "domcontentloaded" });
  const navigation = page.locator(".shell-pages");
  const pageTabs = await record(navigation, ".page-tab");
  const segments = await record(page.locator(".resource-table-mode-switch"), ".segment");
  const tabs = (group, scope) => compare(group, scope, ".page-tab", pageTabs, properties);
  const segmentGroup = (group, scope) => compare(group, scope, ".segment", segments, segmentProperties);

  // 组件与软件的二级分页（外壳的二级分页条，页签组）。
  await navigation.getByRole("button", { name: app.page.components, exact: true }).click();
  const categories = page.getByRole("tablist", { name: app.management.categoryNav });
  await tabs("components subnav", categories);

  // 软件详情的分页（对话框里的 tablist）。
  await categories.getByRole("tab", { name: new RegExp(app.softwareKind.other) }).click();
  await page.locator(".software-item").filter({ hasText: "示例软件" }).first()
    .getByRole("button", { name: app.management.softwareSettings, exact: true }).click();
  const details = page.getByRole("dialog", { name: "示例软件" });
  await details.waitFor();
  await tabs("software detail tabs", details);
  await page.keyboard.press("Escape");
  await details.waitFor({ state: "hidden" });

  // 详细信息的子页签（tablist）和设备拓扑的外部／内部（分段）。
  await navigation.getByRole("button", { name: app.page.details, exact: true }).click();
  await tabs("details subnav", page.getByRole("tablist", { name: app.misc.detailsTab.tabsLabel, exact: true }));
  await segmentGroup("device topology scope", page.locator(".device-scope-tabs"));

  // 设置导航（竖排）和外观的分段。
  await navigation.getByRole("button", { name: app.page.settings, exact: true }).click();
  const settingsNav = page.locator(".settings-nav");
  await page.waitForFunction(() => document.querySelector(".settings-nav .page-tab:not(:disabled)") !== null);
  await tabs("settings nav", settingsNav);
  await settingsNav.getByRole("button", { name: settingsText.sections.appearance, exact: true }).click();
  await segmentGroup("settings segments", page.locator(".settings-content .segmented").first());

  // 任务中心的分页。
  await page.locator("#taskCenterButton").click();
  const taskCenter = page.getByRole("dialog", { name: app.taskCenter.title, exact: true });
  await taskCenter.waitFor();
  await tabs("task center tabs", taskCenter);

  if (failures.length > 0) throw new Error(`Selection items differ from their reference:\n${failures.join("\n")}`);
  return { pageTabGroups: 5, segmentGroups: 2, styles: styles.length };
}
