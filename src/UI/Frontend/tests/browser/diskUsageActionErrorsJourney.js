import { loadAppCopy } from "../../src/i18n/copy/appCopyLoader.ts";
import { waitForStableRendering } from "./journeyWaits.js";

// 磁盘占用页的动作失败要当场给出错误提示，不能变成未处理的 Promise 拒绝：
// 浏览器里没有桌面桥，选文件夹必然失败；打开位置和属性的接口在测试服务器上不存在；剪贴板拒绝写入。
export default async function diskUsageActionErrorsJourney(page, baseUrl) {
  const assert = (value, message) => { if (!value) throw new Error(message); };
  const errors = [];
  page.on("pageerror", error => errors.push(String(error)));
  await page.addInitScript(() => {
    Object.defineProperty(navigator, "clipboard", {
      configurable: true,
      value: { writeText: () => Promise.reject(new DOMException("Clipboard write denied", "NotAllowedError")) }
    });
  });
  await page.route(/\/api\/disk-usage\/node\/\d+$/, route => route.fulfill({
    status: 200,
    contentType: "application/json",
    body: JSON.stringify({
      nodeId: 1, parentNodeId: 0, name: "Archive", fullPath: "C:\\Projects\\Archive",
      isDirectory: true, sizeBytes: 1024, allocatedBytes: 4096, fileCount: 3
    })
  }));
  const patch = await fetch(`${baseUrl}/__test/scenario`, {
    method: "POST", headers: { "Content-Type": "application/json" },
    body: JSON.stringify({ diskUsageMultiRoot: true })
  });
  assert(patch.ok, "Could not configure disk-tree fixture");
  await page.setViewportSize({ width: 1200, height: 950 });
  await page.goto(baseUrl, { waitUntil: "domcontentloaded" });
  const copy = await loadAppCopy("zh-CN");
  await page.locator(".shell-pages").getByRole("button", { name: copy.page.diskUsage, exact: true }).click();
  await page.locator(".disk-usage-result .optimization-heading h2").getByText("C:\\", { exact: true }).waitFor();
  const errorToasts = text => page.locator(".toast.error").filter({ hasText: text });

  // 两个选文件夹入口：切到「一个文件夹」范围时自动打开，以及该行的「选择文件夹」按钮。
  const setup = page.locator(".disk-usage-setup");
  const expand = page.locator(".disk-usage-setup-actions").getByRole("button", { name: copy.diskUsage.expandSetup, exact: true });
  if (await expand.count() > 0) await expand.click();
  await setup.getByText(copy.diskUsage.scopeFolder, { exact: true }).first().click();
  await errorToasts(copy.diskUsage.chooseFolderFailed).first().waitFor();
  await setup.getByRole("button", { name: copy.diskUsage.chooseFolder, exact: true }).click();
  await page.waitForFunction(text => [...document.querySelectorAll(".toast.error")]
    .filter(toast => toast.textContent?.includes(text)).length >= 2, copy.diskUsage.chooseFolderFailed);

  // 右键菜单的三个动作。
  const canvas = page.locator(".disk-usage-treemap-canvas");
  await canvas.scrollIntoViewIfNeeded();
  const box = await canvas.boundingBox();
  assert(box && box.width > 0 && box.height > 0, "Treemap canvas has no size");
  // 方格图有可读名称；还没有选中方格时，键盘打开菜单不开（没有“当前方格”）。
  assert(await canvas.getAttribute("aria-label") === copy.diskUsage.treemapLabel,
    `Treemap canvas name: ${await canvas.getAttribute("aria-label")}`);
  await canvas.focus();
  await page.keyboard.press("Shift+F10");
  // keyboard.press has dispatched the key sequence; let its synchronous context-menu
  // handler and resulting render commit finish before asserting the absence of a menu.
  await waitForStableRendering(page, [".disk-usage-result"], { timeoutMs: 3_000 });
  assert(await page.locator(".disk-usage-context-menu").count() === 0, "Shift+F10 opened a menu with no selected tile");
  const checkedMenuActions = [];
  for (const [label, failure] of [
    [copy.diskUsage.menuOpenLocation, copy.softwareActions.openFileLocationFailed],
    [copy.diskUsage.menuProperties, copy.softwareActions.openPropertiesFailed],
    [copy.diskUsage.menuCopyPath, copy.misc.copyFailed]
  ]) {
    await canvas.click({ button: "right", position: { x: box.width / 2, y: box.height / 2 } });
    const menu = page.locator(".disk-usage-context-menu");
    await menu.waitFor();
    await menu.getByRole("menuitem", { name: label, exact: true }).click();
    await errorToasts(failure).first().waitFor();
    checkedMenuActions.push(label);
  }

  // 右键已经选中了中间的方格：Shift+F10 和菜单键对它开菜单，焦点在第一个动作上，
  // 上下键、Home/End 在动作间移动，Esc 和 Tab 关闭后焦点回到方格图。
  const menu = page.locator(".disk-usage-context-menu");
  const activeMenuItem = () => page.evaluate(() => document.activeElement?.getAttribute("role") === "menuitem"
    ? document.activeElement.textContent?.trim() : `not a menu item: ${document.activeElement?.tagName}`);
  const focusReturnedToCanvas = () => page.waitForFunction(() =>
    document.activeElement?.classList.contains("disk-usage-treemap-canvas"), null, { timeout: 3000 });
  await canvas.focus();
  await page.keyboard.press("Shift+F10");
  await menu.waitFor();
  const menuBox = await menu.boundingBox();
  assert(menuBox && menuBox.x >= box.x && menuBox.x <= box.x + box.width && menuBox.y >= box.y - 1,
    `Keyboard menu did not open at the selected tile: ${JSON.stringify({ menuBox, box })}`);
  const order = [copy.diskUsage.menuDrillDown, copy.diskUsage.menuOpenLocation, copy.diskUsage.menuProperties, copy.diskUsage.menuCopyPath];
  await page.waitForFunction(label => document.activeElement?.textContent?.trim() === label, order[0], { timeout: 3000 });
  const visited = [await activeMenuItem()];
  for (const key of ["ArrowDown", "ArrowDown", "ArrowUp", "End", "ArrowDown", "Home", "ArrowUp"]) {
    await page.keyboard.press(key);
    visited.push(await activeMenuItem());
  }
  const expected = [order[0], order[1], order[2], order[1], order[3], order[0], order[0], order[3]];
  assert(JSON.stringify(visited) === JSON.stringify(expected), `Menu arrow keys: ${JSON.stringify(visited)}`);
  await page.keyboard.press("Escape");
  await menu.waitFor({ state: "detached" });
  await focusReturnedToCanvas();
  await page.keyboard.press("ContextMenu");
  await menu.waitFor();
  await page.keyboard.press("Tab");
  await menu.waitFor({ state: "detached" });
  await focusReturnedToCanvas();

  assert(errors.length === 0, errors.join("\n"));
  return { folderPickerFailures: 2, menuActionFailures: checkedMenuActions.length, keyboardMenu: visited.length, pageErrors: errors };
}
