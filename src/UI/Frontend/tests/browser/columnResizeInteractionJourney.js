import { waitForNextPaint, waitForStableRendering } from "./journeyWaits.js";

/**
 * 资源表列宽把手走交互协议的运行时（Lodestone 协议第 4.4 节第 1 处迁移），真实鼠标事件：
 * 编辑模式里拖动时把手跟着指针走（表格把多余宽度分给各列，存的宽度变得比指针位移多，把手的屏幕位置仍和指针一致），
 * 松开停在松手处；拖动中按 Esc 回起点宽度；松开丢失（窗口失焦后指针报告左键已松开）校正为松开，之后指针再动不再改宽度；
 * 方向键一步 12 px；不在编辑模式时拖不动。不保存布局。
 */
export default async function columnResizeInteractionJourney(page, baseUrl) {
  await page.addInitScript(() => {
    window.__columnGeometryReads = 0;
    const measure = Element.prototype.getBoundingClientRect;
    Element.prototype.getBoundingClientRect = function (...args) {
      if (this.classList.contains("resource-table-header") || this.classList.contains("resource-table-head-cell")) {
        window.__columnGeometryReads++;
      }
      return measure.apply(this, args);
    };
  });
  const scenario = await page.request.post(`${baseUrl}/__test/scenario`, { data: { resourceTableVariant: "many", resourceTableRowCount: 20 } });
  if (!scenario.ok()) throw new Error(`Unable to configure resource-table fixture: ${scenario.status()}`);
  await page.setViewportSize({ width: 1400, height: 900 });
  await page.goto(`${baseUrl}/`, { waitUntil: "domcontentloaded" });
  await page.locator(".resource-table-frame").waitFor({ state: "visible", timeout: 7000 });

  const resizer = page.locator(".resource-table-column-resizer").nth(1);
  const width = async () => Number(await resizer.getAttribute("aria-valuenow"));
  const waitWidth = async (expected, step) => {
    try {
      await page.waitForFunction(([element, value]) => Math.abs(Number(element.getAttribute("aria-valuenow")) - value) <= 1,
        [await resizer.elementHandle(), expected], { timeout: 3000 });
    } catch {
      throw new Error(`${step}: width ${await width()}, expected ${expected}`);
    }
  };
  const center = async () => {
    await resizer.scrollIntoViewIfNeeded();
    const box = await resizer.boundingBox();
    if (!box) throw new Error("The column resizer has no box");
    return { x: box.x + box.width / 2, y: box.y + box.height / 2 };
  };
  /** 把手中线跟到了 x（±2 px：多余宽度按整数 px 分配）。 */
  const waitHandleAt = async (x, step) => {
    try {
      await page.waitForFunction(([element, target]) => {
        const box = element.getBoundingClientRect();
        return Math.abs(box.left + box.width / 2 - target) <= 2;
      }, [await resizer.elementHandle(), x], { timeout: 3000 });
    } catch {
      const box = await resizer.boundingBox();
      throw new Error(`${step}: handle at ${box ? box.x + box.width / 2 : "none"}, expected ${x}; width ${await width()}`);
    }
  };
  const steps = async (from, dx) => {
    for (let i = 1; i <= 8; i += 1) await page.mouse.move(from.x + dx * i / 8, from.y);
  };

  // 不在编辑模式：把手禁用，拖不动。
  const idle = await width();
  let start = await center();
  await page.mouse.move(start.x, start.y);
  await page.mouse.down();
  await steps(start, 40);
  await page.mouse.up();
  if (await width() !== idle) throw new Error(`Dragging outside edit mode changed the width: ${idle} -> ${await width()}`);
  const idleGeometryReads = await page.evaluate(() => window.__columnGeometryReads);
  if (idleGeometryReads !== 0) throw new Error(`Inactive column handles measured geometry ${idleGeometryReads} times`);

  await page.locator('[data-focus-key="resource-table-edit"]').click();

  // 拖动：把手跟着指针，松开停在松手处。
  const w0 = await width();
  start = await center();
  await page.mouse.move(start.x, start.y);
  await page.mouse.down();
  await steps(start, 40);
  await waitHandleAt(start.x + 40, "While dragging");
  await page.mouse.up();
  await waitHandleAt(start.x + 40, "After release");
  const dragged = await width();
  if (dragged <= w0) throw new Error(`Dragging right did not widen the column: ${w0} -> ${dragged}`);

  // Esc 取消：回到这次拖动前的宽度。
  const w1 = await width();
  start = await center();
  await page.mouse.move(start.x, start.y);
  await page.mouse.down();
  await steps(start, 60);
  await waitHandleAt(start.x + 60, "Before cancelling");
  await page.keyboard.press("Escape");
  await waitWidth(w1, "After Escape");
  await page.mouse.up();
  await waitWidth(w1, "Release after Escape");

  // 松开丢失：窗口失焦，回来后指针报告左键已松开，校正为松开；之后指针再动，宽度不跟着变。
  start = await center();
  await page.mouse.move(start.x, start.y);
  await page.mouse.down();
  await steps(start, 30);
  await waitHandleAt(start.x + 30, "Before losing the release");
  const lost = await width();
  await page.evaluate(({ x, y }) => {
    window.dispatchEvent(new Event("blur"));
    window.dispatchEvent(new Event("focus"));
    for (const dx of [0, 80]) {
      document.dispatchEvent(new PointerEvent("pointermove", { bubbles: true, isPrimary: true, pointerId: 1, pointerType: "mouse", buttons: 0, clientX: x + dx, clientY: y }));
    }
  }, { x: start.x + 30, y: start.y });
  // Dispatch above has completed; cross its render commit before accepting the
  // unchanged width/handle position (which were also true before these events).
  await waitForNextPaint(page);
  await waitForStableRendering(page, [".resource-table-column-resizer"]);
  await waitWidth(lost, "After the corrected release");
  await waitHandleAt(start.x + 30, "Handle after the corrected release");
  await page.mouse.up();

  // 方向键一步 12 px。
  const w2 = await width();
  await resizer.focus();
  await page.keyboard.press("ArrowLeft");
  await waitWidth(w2 - 12, "ArrowLeft");
  await page.keyboard.press("ArrowRight");
  await page.keyboard.press("ArrowRight");
  await waitWidth(w2 + 12, "ArrowRight twice");

  await page.locator(".resource-table-panel").getByRole("button", { name: /取消/ }).click();
  await waitWidth(idle, "Cancelling the layout edit");
  return { idle, dragged, cancelledAt: w1, lostRelease: lost, keyboard: w2 + 12 };
}
