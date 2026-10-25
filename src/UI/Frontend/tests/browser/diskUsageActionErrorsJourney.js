import { loadAppCopy } from "../../src/i18n/copy/appCopyLoader.ts";

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
  const expand = setup.getByRole("button", { name: copy.diskUsage.expandSetup, exact: true });
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

  assert(errors.length === 0, errors.join("\n"));
  return { folderPickerFailures: 2, menuActionFailures: checkedMenuActions.length, pageErrors: errors };
}
