import { mkdir } from "node:fs/promises";
import { join } from "node:path";
import { fileURLToPath } from "node:url";
import { loadAppCopy } from "../../src/i18n/copy/appCopyLoader.ts";

/**
 * 资源占用条目编辑栏“先选项目、再选来源”的交互，和资源表列编辑栏同一套规则（用户 2026-10-25：「子项勾选判定范围减小，
 * 现在必须点那个方格子才能改变勾选状态，点其他位置都是选中」，简体中文）：
 * - 进入编辑时没有选中的条目，也不摆来源行。
 * - 点芯片的方格子只切换这一条显示与否，不改选中；点芯片其他位置（名字或内边距）只选中、不切换；缩放下拉框照常打开，不选中。
 * - 同时只有一个选中项；选中有来源、已勾选的条目（物理内存）才摆它的来源行，选中别的条目来源行跟着换或不摆；
 *   选中的条目取消勾选后来源行收起、选中还在，再勾上来源行回来。再点一次选中的芯片取消选中。
 * - 键盘：芯片上的名字是按钮，Tab 可到，Enter／空格选中（再按取消）；左右方向键和 Home/End 在芯片之间移动焦点、不选中；
 *   方格子照常用空格切换，切换后焦点还在方格子上。
 * - 读屏：芯片按钮用 aria-pressed 表示选中，方格子名为“显示 X”、保留自己的勾选状态。
 * - 选中态在四种界面风格（各取自己的浅色和深色配色）下都和未选中的芯片底色、文字色不同。
 * 截图（选中物理内存、选中 CPU，以及各风格配色下选中物理内存）存在 RM_BROWSER_ARTIFACT_DIR。
 */
export default async function resourceBarEditorJourney(page, baseUrl) {
  const app = await loadAppCopy("zh-CN");
  const view = app.resourceBreakdownView;
  const table = app.resourceTable;
  const shots = process.env.RM_BROWSER_ARTIFACT_DIR
    ?? fileURLToPath(new URL("../../../../../.local/browser-gate/resource-bar-editor/", import.meta.url));
  await mkdir(join(shots, "styles"), { recursive: true });
  const memory = app.metricLabel["memory.usage"];
  const cpu = app.metricLabel["cpu.usage"];

  const editButton = page.getByRole("button", { name: view.editLabel, exact: true });
  const cancelButton = page.getByRole("button", { name: view.cancelEditLabel, exact: true });
  const editor = page.getByRole("group", { name: view.editorLabel, exact: true });
  const selectButton = (label) => editor.getByRole("button", { name: label, exact: true });
  // has 里的定位从每个芯片自己找起，所以用不带前缀的 page.getByRole。
  const chip = (label) => editor.locator(".resource-bar-option").filter({ has: page.getByRole("button", { name: label, exact: true }) });
  const checkbox = (label) => editor.getByRole("checkbox", { name: view.showBar(label), exact: true });
  const scale = (label) => editor.getByRole("combobox", { name: view.scaleModeLabel(label), exact: true });
  const sourceRows = editor.locator(".resource-bar-source");
  const sourceGroup = (label) => editor.getByRole("radiogroup", { name: table.columnSourceLabel(label), exact: true });
  const pressed = async () => (await editor.locator('.resource-bar-select[aria-pressed="true"]').allTextContents()).map((text) => text.trim());
  const activeName = () => page.evaluate(() => {
    const active = document.activeElement;
    return active ? `${active.tagName.toLowerCase()}:${active.getAttribute("aria-label") ?? active.textContent?.trim()}` : null;
  });
  const expect = async (what, read, expected) => {
    const deadline = Date.now() + 5_000;
    let last;
    while (Date.now() < deadline) {
      last = await read();
      if (JSON.stringify(last) === JSON.stringify(expected)) return;
      await page.waitForTimeout(50);
    }
    throw new Error(`${what}: expected ${JSON.stringify(expected)}, got ${JSON.stringify(last)}`);
  };
  // 选中某一条后来源行只能是这一条的：没有来源可选时一行都没有，有来源时恰好一行、名字是这一条的来源。
  const expectSourceRowFor = async (label, hasSources) => {
    await expect(`Source rows after selecting ${label}`, () => sourceRows.count(), hasSources ? 1 : 0);
    if (hasSources && await sourceGroup(label).count() !== 1) throw new Error(`The source row is not ${label}'s`);
  };
  const hasSources = async (label) => {
    await page.waitForTimeout(50);
    return await sourceGroup(label).count() === 1;
  };
  // 给用户看的截图：编辑栏和来源行，只截页面。
  const shoot = async (file) => {
    await editor.scrollIntoViewIfNeeded();
    await page.waitForTimeout(200);
    const box = await editor.boundingBox();
    await page.screenshot({ path: join(shots, file), clip: { x: box.x - 8, y: Math.max(0, box.y - 8), width: box.width + 16, height: box.height + 16 } });
  };

  await page.goto(baseUrl, { waitUntil: "domcontentloaded" });
  await page.locator('.resource-bar-track[data-metric-id="memory.usage"]').waitFor({ timeout: 15_000 });
  await editButton.scrollIntoViewIfNeeded();
  await editButton.click();
  await editor.waitFor();

  // 进入编辑：没有选中，没有来源行；每个芯片按钮都说明“未按下”。
  await expect("Pressed chips on entry", pressed, []);
  await expectSourceRowFor("(none)", false);
  const chipLabels = (await editor.locator(".resource-bar-select").allTextContents()).map((text) => text.trim());
  for (const label of [cpu, memory]) if (!chipLabels.includes(label)) throw new Error(`No ${label} chip among ${JSON.stringify(chipLabels)}`);
  if (await editor.locator('.resource-bar-select[aria-pressed="false"]').count() !== chipLabels.length) {
    throw new Error("Not every chip states that it is not pressed");
  }
  if (!await checkbox(memory).isChecked() || !await checkbox(cpu).isChecked()) throw new Error("Memory and CPU bars are not both shown on entry");

  // 点物理内存芯片上的名字：选中它，摆它的来源（第一项自动选择已勾），显示状态不变。
  await selectButton(memory).click();
  await expect("Pressed after clicking memory", pressed, [memory]);
  await expectSourceRowFor(memory, true);
  if (await sourceGroup(memory).getByRole("radio").nth(0).getAttribute("aria-checked") !== "true") throw new Error("Automatic is not checked");
  if (!await checkbox(memory).isChecked()) throw new Error("Selecting memory toggled it");
  if (!await chip(memory).evaluate((element) => element.classList.contains("selected"))) throw new Error("The memory chip has no selected style");
  await shoot("memory-selected.png");

  // 缩放下拉框是它自己的控件：打开它不改选中，也不切换显示。
  await scale(memory).click();
  await page.locator(".standard-select-popover").waitFor({ timeout: 5_000 });
  await expect("Pressed after opening the scale select", pressed, [memory]);
  await page.keyboard.press("Escape");
  await page.locator(".standard-select-popover").waitFor({ state: "detached", timeout: 5_000 });
  if (!await checkbox(memory).isChecked()) throw new Error("Opening the scale select toggled memory");

  // 点 CPU 芯片左侧的内边距（方格子之外）：只选中 CPU，物理内存的来源行换成 CPU 的（没有来源时不摆）。
  await chip(cpu).scrollIntoViewIfNeeded();
  const cpuChip = await chip(cpu).boundingBox();
  const cpuBox = await checkbox(cpu).boundingBox();
  if (!cpuChip || !cpuBox || cpuBox.x - cpuChip.x < 6) throw new Error("The CPU chip has no padding to click");
  await page.mouse.click(cpuChip.x + 3, cpuChip.y + cpuChip.height / 2);
  await expect("Pressed after clicking CPU padding", pressed, [cpu]);
  if (await sourceGroup(memory).count() !== 0) throw new Error("The memory source row stayed after selecting CPU");
  const cpuHasSources = await hasSources(cpu);
  await expectSourceRowFor(cpu, cpuHasSources);
  if (!await checkbox(cpu).isChecked() || !await checkbox(memory).isChecked()) throw new Error("Selecting CPU toggled a bar");
  await shoot("cpu-selected.png");

  // 点物理内存芯片名字右边的空白（名字按钮占满中间一栏，空白也在按钮上）：选中物理内存。
  const memoryText = await selectButton(memory).boundingBox();
  await page.mouse.click(memoryText.x + memoryText.width - 3, memoryText.y + memoryText.height / 2);
  await expect("Pressed after clicking beside the memory name", pressed, [memory]);

  // 方格子只切换显示：取消勾选物理内存，来源行收起、选中不变；再勾上来源行回来。
  await checkbox(memory).click();
  if (await checkbox(memory).isChecked()) throw new Error("Clicking the memory box did not uncheck it");
  await expect("Pressed after the memory box", pressed, [memory]);
  await expectSourceRowFor(memory, false);
  await checkbox(memory).click();
  if (!await checkbox(memory).isChecked()) throw new Error("Clicking the memory box again did not check it");
  await expect("Pressed after the memory box again", pressed, [memory]);
  await expectSourceRowFor(memory, true);

  // 再点一次选中的芯片取消选中。
  await selectButton(memory).click();
  await expect("Pressed after clicking the selected chip", pressed, []);
  await expectSourceRowFor("(none)", false);

  // 键盘：方向键在芯片按钮之间移动焦点、不选中。
  const memoryIndex = chipLabels.indexOf(memory);
  const neighbour = memoryIndex > 0 ? chipLabels[memoryIndex - 1] : chipLabels[memoryIndex + 1];
  await selectButton(chipLabels[0]).focus();
  await page.keyboard.press("End");
  await expect("Focus after End", activeName, `button:${chipLabels.at(-1)}`);
  await page.keyboard.press("Home");
  await expect("Focus after Home", activeName, `button:${chipLabels[0]}`);
  for (let step = 0; step < memoryIndex; step += 1) await page.keyboard.press("ArrowRight");
  await expect("Focus after ArrowRight", activeName, `button:${memory}`);
  await page.keyboard.press(memoryIndex > 0 ? "ArrowLeft" : "ArrowRight");
  await expect("Focus after an arrow away", activeName, `button:${neighbour}`);
  await page.keyboard.press(memoryIndex > 0 ? "ArrowRight" : "ArrowLeft");
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
  // Shift+Tab 回到物理内存的方格子，空格切换，焦点留在方格子上，选中不变。
  await page.keyboard.press("Shift+Tab");
  await expect("Focus after Shift+Tab", activeName, `input:${view.showBar(memory)}`);
  await page.keyboard.press(" ");
  await expect("Memory box after Space", () => checkbox(memory).isChecked(), false);
  await expect("Focus after toggling the box", activeName, `input:${view.showBar(memory)}`);
  await expect("Pressed after Space on the box", pressed, [memory]);
  await page.keyboard.press(" ");
  await expect("Memory box after Space again", () => checkbox(memory).isChecked(), true);
  await page.keyboard.press("Tab");
  await expect("Focus after Tab", activeName, `button:${memory}`);

  // 四种界面风格：选中的芯片底色和文字色都和未选中的不同。
  const styles = [];
  const originalLook = await page.evaluate(() => ({ uiStyle: document.documentElement.dataset.uiStyle, theme: document.documentElement.dataset.theme }));
  for (const [uiStyle, theme] of [["standard", "light"], ["standard", "dark"], ["soft", "dark"], ["terminal", "phosphor"], ["outline", "paper"], ["outline", "blueprint"]]) {
    await page.evaluate(([style, palette]) => { document.documentElement.dataset.uiStyle = style; document.documentElement.dataset.theme = palette; }, [uiStyle, theme]);
    await page.waitForTimeout(250);
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
  await cancelButton.click();
  await editButton.click();
  await editor.waitFor();
  await expect("Pressed after re-entering", pressed, []);
  await expectSourceRowFor("(none)", false);
  await cancelButton.click();
  return { chips: chipLabels, cpuHasSources, styles, shots };
}
