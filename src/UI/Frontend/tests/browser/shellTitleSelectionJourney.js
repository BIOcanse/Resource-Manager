import assert from "node:assert/strict";

/**
 * 顶栏的产品名和开机时长可以选中（用户 10-27「让这几个字可选中，鼠标放到上面优先准备选中而不是拖动窗口」）：
 * 按在文字上不发 window.drag，拖过文字得到选区；按在文字两侧的空白处仍然拖动窗口。
 */
export default async function shellTitleSelectionJourney(page, baseUrl) {
  const errors = [];
  page.on("pageerror", error => errors.push(String(error)));
  await page.addInitScript(() => {
    window.__shellMessages = [];
    window.chrome = { webview: { postMessage: message => window.__shellMessages.push(message) } };
  });
  await page.setViewportSize({ width: 1400, height: 900 });
  await page.goto(baseUrl, { waitUntil: "domcontentloaded" });
  const productName = page.locator(".window-drag-region strong");
  await productName.waitFor({ state: "visible", timeout: 10000 });

  const geometry = await page.evaluate(() => {
    const region = document.querySelector(".window-drag-region").getBoundingClientRect();
    const name = document.querySelector(".window-drag-region strong").getBoundingClientRect();
    const uptime = document.querySelector(".window-drag-region .shell-uptime").getBoundingClientRect();
    return {
      region: { left: region.left, right: region.right, top: region.top, bottom: region.bottom },
      name: { left: name.left, right: name.right, top: name.top, bottom: name.bottom },
      uptime: { left: uptime.left, right: uptime.right },
      nameCursor: getComputedStyle(document.querySelector(".window-drag-region strong")).cursor,
      uptimeCursor: getComputedStyle(document.querySelector(".window-drag-region .shell-uptime")).cursor
    };
  });
  assert.equal(geometry.nameCursor, "text", "Product name does not offer a text cursor");
  assert.equal(geometry.uptimeCursor, "text", "Uptime does not offer a text cursor");

  const middleY = (geometry.name.top + geometry.name.bottom) / 2;
  await page.mouse.move(geometry.name.left + 1, middleY);
  await page.mouse.down();
  await page.mouse.move(geometry.uptime.right - 1, middleY, { steps: 6 });
  await page.mouse.up();
  const afterTextDrag = await page.evaluate(() => ({ messages: window.__shellMessages.slice(), selection: String(window.getSelection()) }));
  assert(!afterTextDrag.messages.includes("window.drag"), `Pressing the title text started a window drag: ${JSON.stringify(afterTextDrag.messages)}`);
  const expectedName = await productName.textContent();
  assert(afterTextDrag.selection.includes(expectedName.trim()), `Dragging across the title did not select it: ${JSON.stringify(afterTextDrag.selection)}`);

  assert(geometry.name.left - geometry.region.left > 4, `No empty drag space left of the title: ${JSON.stringify(geometry)}`);
  await page.evaluate(() => { window.__shellMessages.length = 0; window.getSelection().removeAllRanges(); });
  await page.mouse.move(geometry.region.left + 2, middleY);
  await page.mouse.down();
  await page.mouse.up();
  const afterEmptyPress = await page.evaluate(() => window.__shellMessages.slice());
  assert(afterEmptyPress.includes("window.drag"), `Pressing the empty title area no longer drags the window: ${JSON.stringify(afterEmptyPress)}`);

  assert.deepEqual(errors, []);
  return { assertions: 6 };
}
