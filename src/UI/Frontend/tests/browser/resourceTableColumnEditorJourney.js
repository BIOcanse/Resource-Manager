import { mkdir } from "node:fs/promises";
import { join } from "node:path";
import { fileURLToPath } from "node:url";
import { loadAppCopy } from "../../src/i18n/copy/appCopyLoader.ts";
import { waitForCondition, waitForNextPaint, waitForStableRendering } from "./journeyWaits.js";

/**
 * 资源表列编辑栏“先选项目、再选来源”的交互（用户 2026-10-25：「子项勾选判定范围减小，现在必须点那个方格子才能改变勾选状态，
 * 点其他位置都是选中」，简体中文）：
 * - 进入编辑时没有选中的列，也不摆来源行。
 * - 点芯片的方格子只切换这一列显示与否，不改选中；点芯片其他位置（文字或内边距）只选中、不切换显示。
 * - 同时只有一个选中项；选中有来源的列（内存）才摆它的来源行，选中别的列来源行跟着换，选中没有来源的列（名称）不摆来源行。
 *   再点一次选中的芯片取消选中。
 * - 键盘：芯片文字是按钮，Tab 可到，Enter／空格选中（再按取消）；左右方向键和 Home/End 在芯片之间移动焦点、不选中；
 *   方格子照常用空格切换，切换后焦点还在方格子上。
 * - 读屏：芯片按钮用 aria-pressed 表示选中，方格子名为“显示 X 列”、保留自己的勾选状态。
 * - 选中态在四种界面风格（各取自己的浅色和深色配色）下都和未选中的芯片底色、文字色不同。
 * 截图（选中内存、选中 CPU，以及各风格配色下选中内存）存在 RM_BROWSER_ARTIFACT_DIR。
 */
export default async function resourceTableColumnEditorJourney(page, baseUrl) {
  const app = await loadAppCopy("zh-CN");
  const table = app.resourceTable;
  const view = app.resourceTableView;
  const shots = process.env.RM_BROWSER_ARTIFACT_DIR
    ?? fileURLToPath(new URL("../../../../../.local/browser-gate/resource-table-column-editor/", import.meta.url));
  await mkdir(join(shots, "styles"), { recursive: true });
  const memory = table.columns.memory;
  const cpu = table.columns.cpu;
  const name = table.columns.name;

  const panel = page.locator(".resource-table-panel");
  const editor = panel.getByRole("group", { name: view.columnEditorLabel, exact: true });
  const selectButton = (label) => editor.getByRole("button", { name: label, exact: true });
  // has 里的定位从每个芯片自己找起，所以用不带前缀的 page.getByRole。
  const chip = (label) => editor.locator(".resource-table-column-option").filter({ has: page.getByRole("button", { name: label, exact: true }) });
  const checkbox = (label) => editor.getByRole("checkbox", { name: view.showColumn(label), exact: true });
  const sourceRows = editor.locator(".resource-table-column-source");
  const sourceGroup = (label) => editor.getByRole("radiogroup", { name: table.columnSourceLabel(label), exact: true });
  const pressed = async () => (await editor.locator('.resource-table-column-select[aria-pressed="true"]').allTextContents()).map((text) => text.trim());
  const headers = async () => (await panel.locator(".resource-table-head-cell .resource-table-sort-button > span").allTextContents()).map((text) => text.trim());
  const activeName = () => page.evaluate(() => {
    const active = document.activeElement;
    return active ? `${active.tagName.toLowerCase()}:${active.getAttribute("aria-label") ?? active.textContent?.trim()}` : null;
  });
  const expect = async (what, read, expected) => {
    // Several checks deliberately expect the previous selection to remain.
    // Cross the completed click/key dispatch before accepting that old value.
    await waitForNextPaint(page, { timeoutMs: 5000 });
    let last;
    await waitForCondition(page, async () => {
      last = await read();
      return JSON.stringify(last) === JSON.stringify(expected);
    }, { timeoutMs: 5000, description: `${what}: expected ${JSON.stringify(expected)}` }).catch(() => {
      throw new Error(`${what}: expected ${JSON.stringify(expected)}, got ${JSON.stringify(last)}`);
    });
  };
  // 选中某列后来源行只能是这一列的：没有来源可选时一行都没有，有来源时恰好一行、名字是这一列的来源。
  const expectSourceRowFor = async (label, hasSources) => {
    await expect(`Source rows after selecting ${label}`, () => sourceRows.count(), hasSources ? 1 : 0);
    if (hasSources && await sourceGroup(label).count() !== 1) throw new Error(`The source row is not ${label}'s`);
  };
  const hasSources = async (label) => {
    // The caller has proved this chip's aria-pressed update. Cross its paint
    // before reading an optional source row (which may legitimately be absent).
    await expect(`Selected chip before reading ${label} sources`, pressed, [label]);
    await waitForNextPaint(page, { timeoutMs: 3000 });
    return await sourceGroup(label).count() === 1;
  };

  // 给用户看的截图：资源列表面板的上部（编辑栏、来源行和前几行），只截页面。
  const shoot = async (file) => {
    await panel.evaluate((element) => element.scrollIntoView({ block: "start" }));
    await waitForStableRendering(page, [".resource-table-panel", ".resource-table-column-editor"], { timeoutMs: 3000 });
    const box = await panel.boundingBox();
    await page.screenshot({ path: join(shots, file), clip: { x: box.x, y: Math.max(0, box.y), width: box.width, height: Math.min(box.height, 460) } });
  };

  await page.goto(baseUrl, { waitUntil: "domcontentloaded" });
  await panel.scrollIntoViewIfNeeded();
  await panel.locator('.resource-table-row.software-row[aria-hidden="false"]').first().waitFor({ timeout: 15_000 });
  await expect("Memory column header", async () => (await headers()).includes(memory), true);
  await panel.getByRole("button", { name: view.editLabel, exact: true }).click();
  await editor.waitFor();

  // 进入编辑：没有选中，没有来源行；每个芯片按钮都说明“未按下”。
  await expect("Pressed chips on entry", pressed, []);
  await expectSourceRowFor("(none)", false);
  const chipLabels = (await editor.locator(".resource-table-column-select").allTextContents()).map((text) => text.trim());
  for (const label of [name, cpu, memory]) if (!chipLabels.includes(label)) throw new Error(`No ${label} chip among ${JSON.stringify(chipLabels)}`);
  if (await editor.locator('.resource-table-column-select[aria-pressed="false"]').count() !== chipLabels.length) {
    throw new Error("Not every chip states that it is not pressed");
  }

  // 点内存芯片的文字：选中内存，摆内存的来源（第一项自动选择已勾），显示状态不变。
  await selectButton(memory).click();
  await expect("Pressed after clicking memory", pressed, [memory]);
  await expectSourceRowFor(memory, true);
  if (await sourceGroup(memory).getByRole("radio").nth(0).getAttribute("aria-checked") !== "true") throw new Error("Automatic is not checked");
  if (!await checkbox(memory).isChecked()) throw new Error("Selecting memory toggled its visibility");
  if (!(await headers()).includes(memory)) throw new Error("Selecting memory hid its column");
  if (!await chip(memory).evaluate((element) => element.classList.contains("selected"))) throw new Error("The memory chip has no selected style");
  await shoot("memory-selected.png");

  // 点 CPU 芯片右侧的内边距（文字按钮之外、方格子之外）：只选中 CPU，内存的来源行换成 CPU 的（目录没有 CPU 来源时没有来源行）。
  await chip(cpu).scrollIntoViewIfNeeded();
  const cpuChip = await chip(cpu).boundingBox();
  const cpuText = await selectButton(cpu).boundingBox();
  if (!cpuChip || !cpuText || cpuChip.x + cpuChip.width - (cpuText.x + cpuText.width) < 4) throw new Error("The CPU chip has no padding to click");
  await page.mouse.click(cpuChip.x + cpuChip.width - 3, cpuChip.y + cpuChip.height / 2);
  await expect("Pressed after clicking CPU padding", pressed, [cpu]);
  if (await sourceGroup(memory).count() !== 0) throw new Error("The memory source row stayed after selecting CPU");
  const cpuHasSources = await hasSources(cpu);
  await expectSourceRowFor(cpu, cpuHasSources);
  if (!await checkbox(cpu).isChecked() || !await checkbox(memory).isChecked()) throw new Error("Selecting CPU toggled a column");
  await shoot("cpu-selected.png");

  // 点名称芯片左侧的内边距：选中名称；名称没有来源可选，不摆来源行，也不留空行。
  await chip(name).scrollIntoViewIfNeeded();
  const nameChip = await chip(name).boundingBox();
  await page.mouse.click(nameChip.x + 3, nameChip.y + nameChip.height / 2);
  await expect("Pressed after clicking name padding", pressed, [name]);
  await expectSourceRowFor(name, false);

  // 方格子只切换显示：取消勾选 CPU，CPU 列不再显示，选中项还是名称；再勾上恢复。
  await checkbox(cpu).click();
  await expect("CPU header after unchecking", async () => (await headers()).includes(cpu), false);
  if (await checkbox(cpu).isChecked()) throw new Error("Clicking the CPU box did not uncheck it");
  await expect("Pressed after the CPU box", pressed, [name]);
  await checkbox(cpu).click();
  await expect("CPU header after checking", async () => (await headers()).includes(cpu), true);
  await expect("Pressed after the CPU box again", pressed, [name]);
  await expectSourceRowFor(name, false);

  // 再点一次选中的芯片取消选中。
  await selectButton(name).click();
  await expect("Pressed after clicking the selected chip", pressed, []);

  // 键盘：方向键在芯片按钮之间移动焦点、不选中。
  const memoryIndex = chipLabels.indexOf(memory);
  await selectButton(chipLabels[0]).focus();
  await page.keyboard.press("End");
  await expect("Focus after End", activeName, `button:${chipLabels.at(-1)}`);
  await page.keyboard.press("Home");
  await expect("Focus after Home", activeName, `button:${chipLabels[0]}`);
  for (let step = 0; step < memoryIndex; step += 1) await page.keyboard.press("ArrowRight");
  await expect("Focus after ArrowRight", activeName, `button:${memory}`);
  await page.keyboard.press("ArrowLeft");
  await expect("Focus after ArrowLeft", activeName, `button:${chipLabels[memoryIndex - 1]}`);
  await page.keyboard.press("ArrowRight");
  await expect("Pressed after arrow keys", pressed, []);
  // Enter 选中，空格取消、再选中。
  await page.keyboard.press("Enter");
  await expect("Pressed after Enter", pressed, [memory]);
  await expectSourceRowFor(memory, true);
  await page.keyboard.press(" ");
  await expect("Pressed after Space", pressed, []);
  await expectSourceRowFor(memory, false);
  await page.keyboard.press(" ");
  await expect("Pressed after Space again", pressed, [memory]);
  // Shift+Tab 回到内存的方格子，空格切换显示，焦点留在方格子上，选中不变。
  await page.keyboard.press("Shift+Tab");
  await expect("Focus after Shift+Tab", activeName, `input:${view.showColumn(memory)}`);
  await page.keyboard.press(" ");
  await expect("Memory header after Space on its box", async () => (await headers()).includes(memory), false);
  await expect("Focus after toggling the box", activeName, `input:${view.showColumn(memory)}`);
  await expect("Pressed after Space on the box", pressed, [memory]);
  await page.keyboard.press(" ");
  await expect("Memory header after Space again", async () => (await headers()).includes(memory), true);
  await page.keyboard.press("Tab");
  await expect("Focus after Tab", activeName, `button:${memory}`);

  // 四种界面风格：选中的芯片底色和文字色都和未选中的不同。
  const styles = [];
  const originalLook = await page.evaluate(() => ({ uiStyle: document.documentElement.dataset.uiStyle, theme: document.documentElement.dataset.theme }));
  for (const [uiStyle, theme] of [["standard", "light"], ["standard", "dark"], ["soft", "dark"], ["terminal", "phosphor"], ["outline", "paper"], ["outline", "blueprint"]]) {
    await page.evaluate(([style, palette]) => { document.documentElement.dataset.uiStyle = style; document.documentElement.dataset.theme = palette; }, [uiStyle, theme]);
    await waitForStableRendering(page, [".resource-table-column-option", ".resource-table-column-select"], { timeoutMs: 3000 });
    const look = async (label) => chip(label).evaluate((element) => {
      const style = getComputedStyle(element);
      return { background: style.backgroundColor, color: style.color, border: style.borderColor };
    });
    const selected = await look(memory);
    const unselected = await look(cpu);
    if (selected.background === unselected.background || selected.color === unselected.color) {
      throw new Error(`${uiStyle}/${theme}: the selected chip looks like the others ${JSON.stringify({ selected, unselected })}`);
    }
    await editor.screenshot({ path: join(shots, "styles", `${uiStyle}-${theme}-memory-selected.png`) });
    styles.push({ uiStyle, theme, selected, unselected });
  }
  await page.evaluate((look) => { document.documentElement.dataset.uiStyle = look.uiStyle; document.documentElement.dataset.theme = look.theme; }, originalLook);

  // 取消编辑后再进入：回到没有选中。
  await panel.getByRole("button", { name: view.cancelEditLabel, exact: true }).click();
  await panel.getByRole("button", { name: view.editLabel, exact: true }).click();
  await editor.waitFor();
  await expect("Pressed after re-entering", pressed, []);
  await expectSourceRowFor("(none)", false);
  await panel.getByRole("button", { name: view.cancelEditLabel, exact: true }).click();
  return { chips: chipLabels, cpuHasSources, styles, shots };
}
