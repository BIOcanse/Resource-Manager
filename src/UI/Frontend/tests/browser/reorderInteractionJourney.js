import { waitForCondition, waitForNextPaint } from "./journeyWaits.js";

/**
 * 拖动排序走交互协议的运行时（Lodestone 协议第 4.4 节第 3 处），真实鼠标事件：
 * 仪表盘卡片拖到另一张卡片上，松开时提交一次（卡片移到目标的位置）；拖动中源半透明、目标虚线框；拖完的松开不产生点击；
 * 拖动中按 Esc、右键、窗口失焦都取消，次序不变；表头列拖到另一列上，列序提交；资源条拖到另一条上，次序提交；
 * 在拖动框里的小移动当作单击：表头的排序按钮照常排序，次序不变。都只改编辑中的草稿，最后取消编辑。
 */
export default async function reorderInteractionJourney(page, baseUrl) {
  const scenario = await page.request.post(`${baseUrl}/__test/scenario`, { data: { resourceTableVariant: "many", resourceTableRowCount: 20 } });
  if (!scenario.ok()) throw new Error(`Unable to configure the fixture: ${scenario.status()}`);
  await page.setViewportSize({ width: 1400, height: 900 });
  await page.goto(`${baseUrl}/`, { waitUntil: "domcontentloaded" });
  await page.locator(".metric-card").first().waitFor({ state: "visible", timeout: 7000 });
  await page.locator(".resource-table-frame").waitFor({ state: "visible", timeout: 7000 });

  const order = (selector, attribute) => page.evaluate(([s, a]) =>
    [...document.querySelectorAll(s)].map((element) => element.getAttribute(a)), [selector, attribute]);
  const cardOrder = () => order(".metric-card", "data-card-id");
  const columnOrder = () => order(".resource-table-head-cell", "data-column-id");
  const barOrder = () => order(".resource-bar-track", "data-metric-id");
  const same = (a, b) => JSON.stringify(a) === JSON.stringify(b);
  const moved = (items, from, to) => {
    const next = [...items];
    const [item] = next.splice(from, 1);
    next.splice(to, 0, item);
    return next;
  };
  const waitOrder = async (read, expected, step) => {
    let actual = await read();
    await waitForCondition(page, async () => {
      actual = await read();
      return same(actual, expected);
    }, { timeoutMs: 3000, description: step }).catch(() => {
      throw new Error(`${step}: order ${JSON.stringify(actual)}, expected ${JSON.stringify(expected)}`);
    });
    if (!same(actual, expected)) throw new Error(`${step}: order ${JSON.stringify(actual)}, expected ${JSON.stringify(expected)}`);
  };
  const box = async (locator) => {
    await locator.scrollIntoViewIfNeeded();
    const b = await locator.boundingBox();
    if (!b) throw new Error("Element has no box");
    return b;
  };
  /** 从 from 分 8 步移到 to。 */
  const glide = async (from, to) => {
    for (let i = 1; i <= 8; i += 1) await page.mouse.move(from.x + (to.x - from.x) * i / 8, from.y + (to.y - from.y) * i / 8);
  };
  const classes = (locator) => locator.evaluate((element) => [...element.classList]);
  // 点击计数：拖完的松开被认领，紧随其后的 click 在捕获阶段被拦掉，到不了页面。
  await page.evaluate(() => {
    window.__reorderClicks = 0;
    window.__reorderPointerUps = 0;
    window.addEventListener("pointerup", () => { window.__reorderPointerUps += 1; }, true);
    document.addEventListener("click", () => { window.__reorderClicks += 1; });
  });
  const clicks = () => page.evaluate(() => window.__reorderClicks);
  // Observe this release before crossing a paint: unchanged order/click counts
  // cannot themselves prove that pointerup and its synthesized click ran.
  const release = async (options) => {
    const before = await page.evaluate(() => window.__reorderPointerUps);
    await page.mouse.up(options);
    await page.waitForFunction(before => window.__reorderPointerUps > before, before, { timeout: 3000 });
    await waitForNextPaint(page, { timeoutMs: 3000 });
  };
  const waitForDragClear = () => page.waitForFunction(() =>
    document.querySelectorAll(".drag-source, .drag-over").length === 0
      && !document.body.classList.contains("reordering"), null, { timeout: 3000 });

  // ---- 仪表盘卡片 ----
  await page.locator('[data-focus-key="monitor-dashboard-edit"]').click();
  await page.locator(".metric-card.editing").first().waitFor({ state: "visible", timeout: 3000 });
  const cards0 = await cardOrder();
  if (cards0.length < 2) throw new Error(`Need at least two dashboard cards, found ${cards0.length}`);
  const card = (index) => page.locator(".metric-card").nth(index);
  /** 按在卡片的左内边距上（不在指标槽上，拖的是卡片）。 */
  const cardGrip = async (index) => {
    const b = await box(card(index));
    return { x: b.x + 6, y: b.y + b.height / 2 };
  };
  const cardCenter = async (index) => {
    const b = await box(card(index));
    return { x: b.x + b.width / 2, y: b.y + b.height / 2 };
  };

  // 拖到第二张卡片上：拖动中源半透明、目标虚线框，松开提交一次（提交两次会换回原来的次序），之后没有点击。
  let from = await cardGrip(0);
  let to = await cardCenter(1);
  const clicksBefore = await clicks();
  await page.mouse.move(from.x, from.y);
  await page.mouse.down();
  await glide(from, to);
  await page.waitForFunction(() => document.querySelectorAll(".metric-card")[0]?.classList.contains("drag-source"), null, { timeout: 3000 })
    .catch(async () => { throw new Error(`Dragged card is not marked as the source: ${await classes(card(0))}`); });
  const targetClasses = await classes(card(1));
  if (!targetClasses.includes("drag-over")) throw new Error(`Target card has no dashed frame: ${targetClasses}`);
  await release();
  const cards1 = moved(cards0, 0, 1);
  await waitOrder(cardOrder, cards1, "Card drag committed once");
  await waitForDragClear();
  if (await clicks() !== clicksBefore) throw new Error("The release after a card drag produced a click");
  const leftover = await page.evaluate(() => ({
    source: document.querySelectorAll(".metric-card.drag-source").length,
    target: document.querySelectorAll(".drag-over").length,
    body: document.body.classList.contains("reordering")
  }));
  if (leftover.source || leftover.target || leftover.body) throw new Error(`Drag visuals left after release: ${JSON.stringify(leftover)}`);

  // Esc 取消。
  from = await cardGrip(0);
  to = await cardCenter(1);
  await page.mouse.move(from.x, from.y);
  await page.mouse.down();
  await glide(from, to);
  await page.keyboard.press("Escape");
  await release();
  await waitForDragClear();
  await waitOrder(cardOrder, cards1, "Escape cancels the card drag");

  // 右键取消。
  await page.mouse.move(from.x, from.y);
  await page.mouse.down();
  await glide(from, to);
  await page.mouse.down({ button: "right" });
  // With the left button still held, releasing right emits mouseup but no
  // pointerup; the final left release below supplies the sequence barrier.
  await page.mouse.up({ button: "right" });
  await release();
  await waitForDragClear();
  await waitOrder(cardOrder, cards1, "Right click cancels the card drag");

  // 窗口失焦取消：回来后指针报告左键已松开，也不提交。
  await page.mouse.move(from.x, from.y);
  await page.mouse.down();
  await glide(from, to);
  await page.evaluate(({ x, y }) => {
    window.dispatchEvent(new Event("blur"));
    window.dispatchEvent(new Event("focus"));
    document.dispatchEvent(new PointerEvent("pointermove", { bubbles: true, isPrimary: true, pointerId: 1, pointerType: "mouse", buttons: 0, clientX: x, clientY: y }));
  }, to);
  await release();
  await waitForDragClear();
  await waitOrder(cardOrder, cards1, "Blur cancels the card drag");
  if (await page.evaluate(() => document.querySelectorAll(".drag-source, .drag-over").length || document.body.classList.contains("reordering"))) {
    throw new Error("Drag visuals left after cancelling");
  }

  await page.locator('[data-focus-key="monitor-dashboard-edit"]').locator("xpath=..").getByRole("button", { name: /取消/ }).click();
  await waitOrder(cardOrder, cards0, "Cancelling the dashboard edit");

  // ---- 表头列 ----
  await page.locator('[data-focus-key="resource-table-edit"]').click();
  await page.locator(".resource-table-head-cell.editing").first().waitFor({ state: "visible", timeout: 3000 });
  const columns0 = await columnOrder();
  const head = (index) => page.locator(".resource-table-head-cell").nth(index);
  /** 表头格的标签处（排序按钮上，不在列宽把手和左右移按钮上）。 */
  const headGrip = async (index) => {
    const b = await box(head(index));
    return { x: b.x + Math.min(24, b.width / 3), y: b.y + b.height / 2 };
  };

  // 在拖动框里的小移动：当作单击，列序不变（编辑时排序按钮停用，也不排序）。
  const sortBefore = await head(1).getAttribute("aria-sort");
  from = await headGrip(1);
  await page.mouse.move(from.x, from.y);
  await page.mouse.down();
  await page.mouse.move(from.x + 1, from.y + 1);
  await release();
  await waitForDragClear();
  await waitOrder(columnOrder, columns0, "A tiny movement does not reorder columns");
  if (await head(1).getAttribute("aria-sort") !== sortBefore) throw new Error("A tiny movement on the header in edit mode changed the sort");

  // 拖到第四列上：列序提交（源移到目标的位置），之后没有点击、不排序。
  const clicksBeforeColumn = await clicks();
  from = await headGrip(1);
  const toBox = await box(head(3));
  to = { x: toBox.x + toBox.width / 2, y: toBox.y + toBox.height / 2 };
  await page.mouse.move(from.x, from.y);
  await page.mouse.down();
  await glide(from, to);
  await release();
  const columns1 = moved(columns0, 1, 3);
  await waitOrder(columnOrder, columns1, "Column drag committed");
  await waitForDragClear();
  if (await clicks() !== clicksBeforeColumn) throw new Error("The release after a column drag produced a click");
  const movedHead = page.locator(`.resource-table-head-cell[data-column-id="${columns0[1]}"]`);
  if (await movedHead.getAttribute("aria-sort") !== sortBefore) throw new Error("The column drag also changed the sort");

  // 表头格里的按钮照常是按钮（按在上面交给浏览器，不开始拖）：点左移把刚拖过去的列移回一位；用键盘按也一样。
  await movedHead.locator(".resource-table-column-order-button").first().click();
  const columns2 = moved(columns1, 3, 2);
  await waitOrder(columnOrder, columns2, "Clicking the move-left button inside a header cell");
  await movedHead.locator(".resource-table-column-order-button").first().focus();
  await page.keyboard.press("Enter");
  const columns3 = moved(columns2, 2, 1);
  await waitOrder(columnOrder, columns3, "Pressing Enter on the move-left button");

  await page.locator(".resource-table-panel").getByRole("button", { name: /取消/ }).click();
  await waitOrder(columnOrder, columns0, "Cancelling the table edit");

  // ---- 资源条 ----
  let barsCommitted = null;
  let barDragGeometry = null;
  const bars0 = await barOrder();
  if (bars0.length >= 2) {
    await page.locator('[data-focus-key="resource-breakdown-edit"]').click();
    await page.locator(".resource-breakdown-item.editing").first().waitFor({ state: "visible", timeout: 3000 });
    const bar = (index) => page.locator(".resource-breakdown-item").nth(index);
    const grip = bar(0).locator(".resource-breakdown-header strong");
    const beforeTargetScroll = await box(grip);
    const barBox = await box(bar(1));
    // Scrolling the target can move the source; sample both endpoints after it.
    const fromBox = await grip.boundingBox();
    if (!fromBox) throw new Error("Resource bar source has no box after target scroll");
    barDragGeometry = { beforeTargetScroll, afterTargetScroll: fromBox, target: barBox };
    from = { x: fromBox.x + 4, y: fromBox.y + fromBox.height / 2 };
    to = { x: barBox.x + barBox.width / 2, y: barBox.y + barBox.height / 2 };
    await page.mouse.move(from.x, from.y);
    await page.mouse.down();
    await glide(from, to);
    await release();
    barsCommitted = moved(bars0, 0, 1);
    await waitOrder(barOrder, barsCommitted, "Resource bar drag committed");
    await page.locator(".resource-breakdown-panel").getByRole("button", { name: /取消/ }).click();
    await waitOrder(barOrder, bars0, "Cancelling the resource bar edit");
  }

  return { cards: { before: cards0, committed: cards1 }, columns: { before: columns0, committed: columns1, buttons: columns3 }, bars: { before: bars0, committed: barsCommitted, dragGeometry: barDragGeometry } };
}
