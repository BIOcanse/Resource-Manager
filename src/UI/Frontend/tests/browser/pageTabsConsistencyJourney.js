import { loadAppCopy } from "../../src/i18n/copy/appCopyLoader.ts";

/**
 * 分页一致：一级页签之外的每组分页（组件与软件、详细信息的子页签，设置导航，任务中心，软件详情）
 * 在四种界面风格下，未选中项和选中项的样子都和一级页签一样（styles/page-tabs.css 的 .page-tab）。
 * 只比样子：底色、文字色、边框、圆角、字号字重、投影；尺寸和排列各处自己定，不比。
 * 风格直接写到根元素的 data-ui-style 上（外观的唯一发布点，见 presentation/documentAppearance.ts）：
 * 这里验的是样式表，不是外观设置的保存流程。
 */
export default async function pageTabsConsistencyJourney(page, baseUrl) {
  const app = await loadAppCopy("zh-CN");
  const styles = [["standard", "light"], ["soft", "light"], ["outline", "paper"], ["terminal", "phosphor"]];
  const properties = ["backgroundColor", "color", "borderTopColor", "borderTopStyle", "borderTopWidth", "borderTopLeftRadius", "fontSize", "fontWeight", "boxShadow"];
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
    return Object.fromEntries(names.map((name) => [name, computed[name]]));
  }, properties);
  /** 读之前把鼠标移开、等过渡走完，读到的是静止的未选中和选中，不是悬停。 */
  const settle = async () => {
    const size = page.viewportSize();
    await page.mouse.move(size.width - 4, size.height - 4);
    await page.waitForTimeout(450);
  };

  /** 在每种风格下比较一组分页和一级页签。scope 是这组分页所在的容器。 */
  const compare = async (group, scope) => {
    const active = scope.locator(".page-tab.active:visible").first();
    const inactive = scope.locator(".page-tab:not(.active):visible").first();
    await active.waitFor({ state: "visible", timeout: 7000 });
    await inactive.waitFor({ state: "visible", timeout: 7000 });
    for (const [uiStyle, theme] of styles) {
      await page.evaluate(([style, palette]) => {
        document.documentElement.dataset.uiStyle = style;
        document.documentElement.dataset.theme = palette;
      }, [uiStyle, theme]);
      await settle();
      const reference = {
        active: await look(page.locator(".shell-pages .page-tab.active")),
        inactive: await look(page.locator(".shell-pages .page-tab:not(.active)").first())
      };
      const actual = { active: await look(active), inactive: await look(inactive) };
      for (const state of ["active", "inactive"]) {
        for (const name of properties) {
          if (actual[state][name] !== reference[state][name]) {
            failures.push(`${group} ${uiStyle} ${state} ${name}: ${actual[state][name]} (top-level tab: ${reference[state][name]})`);
          }
        }
      }
    }
  };

  await setScenario({ capabilities: "ready", settings: "ready", operations: "mixed" });
  await page.setViewportSize({ width: 1280, height: 820 });
  await page.goto(`${baseUrl}/`, { waitUntil: "domcontentloaded" });
  const navigation = page.locator(".shell-pages");
  await navigation.locator(".page-tab.active").waitFor();

  // 组件与软件的子页签（导航按钮）。
  await navigation.getByRole("button", { name: app.page.components, exact: true }).click();
  const categories = page.getByRole("navigation", { name: app.management.categoryNav });
  await compare("components subnav", categories);

  // 软件详情的分页（对话框里的 tablist）。
  await categories.getByRole("button", { name: new RegExp(app.softwareKind.other) }).click();
  await page.locator(".software-item").filter({ hasText: "示例软件" }).first()
    .getByRole("button", { name: app.management.softwareSettings, exact: true }).click();
  const details = page.getByRole("dialog", { name: "示例软件" });
  await details.waitFor();
  await compare("software detail tabs", details);
  await page.keyboard.press("Escape");
  await details.waitFor({ state: "hidden" });

  // 详细信息的子页签（tablist）。
  await navigation.getByRole("button", { name: app.page.details, exact: true }).click();
  await compare("details subnav", page.locator(".page-subnav[role='tablist']"));

  // 设置导航（竖排）。
  await navigation.getByRole("button", { name: app.page.settings, exact: true }).click();
  const settingsNav = page.locator(".settings-nav");
  await page.waitForFunction(() => document.querySelector(".settings-nav .page-tab:not(:disabled)") !== null);
  await compare("settings nav", settingsNav);

  // 任务中心的分页。
  await page.locator("#taskCenterButton").click();
  const taskCenter = page.getByRole("dialog", { name: app.taskCenter.title, exact: true });
  await taskCenter.waitFor();
  await compare("task center tabs", taskCenter);

  if (failures.length > 0) throw new Error(`Page tabs differ from the top-level tabs:\n${failures.join("\n")}`);
  return { groups: 5, styles: styles.length };
}
