import { NOT_GHOST } from "./liveLocator.js";
import { loadAppCopy } from "../../src/i18n/copy/appCopyLoader.ts";

/*
 * 磁盘占用页的「开始扫描」作用于扫描范围、扫描方式和（非文件夹范围时）磁盘列表，所以排在它们全部之后、结果之前。
 * 「全部磁盘」和「指定磁盘」都检查文档顺序和画面上的上下位置；有结果后收起设置时只剩操作行和结果，展开后顺序不变。
 */
export default async function diskUsageSetupOrderJourney(page, baseUrl) {
  const assert = (value, message) => { if (!value) throw new Error(message); };
  const errors = [];
  page.on("pageerror", error => errors.push(String(error)));
  await page.setViewportSize({ width: 1200, height: 950 });
  await page.goto(baseUrl, { waitUntil: "domcontentloaded" });
  const copy = await loadAppCopy("zh-CN");
  const openDiskUsage = () => page.locator(".shell-pages")
    .getByRole("button", { name: copy.page.diskUsage, exact: true }).click();
  await openDiskUsage();
  await page.locator(".disk-usage-volume").first().waitFor();

  const order = () => page.evaluate(() => {
    const page = document.querySelector(".disk-usage-page");
    const pick = selector => page?.querySelector(selector) ?? null;
    const rows = [...(page?.querySelectorAll(".disk-usage-setup .settings-row") ?? [])];
    const parts = {
      setting: rows.at(-1) ?? null,
      volumes: pick(".disk-usage-volumes"),
      actions: pick(".disk-usage-setup-actions"),
      result: pick(".disk-usage-result")
    };
    const present = Object.entries(parts).filter(([, element]) => element);
    const inDocumentOrder = present.every(([, element], index) => index === 0
      || (present[index - 1][1].compareDocumentPosition(element) & Node.DOCUMENT_POSITION_FOLLOWING) !== 0);
    const boxes = present.map(([name, element]) => [name, element.getBoundingClientRect()]);
    const stacked = boxes.every(([, box], index) => index === 0 || box.top >= boxes[index - 1][1].bottom - 0.5);
    return {
      names: present.map(([name]) => name),
      inDocumentOrder,
      stacked,
      tops: boxes.map(([name, box]) => `${name}:${Math.round(box.top)}-${Math.round(box.bottom)}`)
    };
  });
  const expectOrder = async (label, names) => {
    const seen = await order();
    assert(seen.names.join(",") === names.join(","), `${label}: parts ${seen.names.join(",")}, expected ${names.join(",")}`);
    assert(seen.inDocumentOrder, `${label}: parts are not in document order ${seen.names.join(",")}`);
    assert(seen.stacked, `${label}: parts overlap or are out of visual order ${seen.tops.join(" ")}`);
  };

  const setup = page.locator(".disk-usage-setup");
  const scanButton = page.locator(".disk-usage-setup-actions").getByRole("button", { name: copy.diskUsage.scan, exact: true });
  await scanButton.waitFor();
  await expectOrder("all volumes", ["setting", "volumes", "actions", "result"]);

  await setup.getByText(copy.diskUsage.scopeVolume, { exact: true }).first().click();
  await page.locator(".disk-usage-volume:not([disabled])").first().click();
  await page.locator(".disk-usage-volume.selected").waitFor();
  await expectOrder("one volume", ["setting", "volumes", "actions", "result"]);

  // 有结果时设置默认展开；收起后整个设置面板和磁盘列表都不出现，不留空面板，只剩操作行。
  const scenario = await fetch(`${baseUrl}/__test/scenario`, {
    method: "POST", headers: { "Content-Type": "application/json" },
    body: JSON.stringify({ diskUsageMultiRoot: true })
  });
  assert(scenario.ok, `Could not configure disk-tree fixture: ${scenario.status}`);
  await page.goto(baseUrl, { waitUntil: "domcontentloaded" });
  await openDiskUsage();
  await page.locator(".disk-usage-result .optimization-heading h2").getByText("C:\\", { exact: true }).waitFor();
  const actions = page.locator(".disk-usage-setup-actions");
  const collapse = actions.getByRole("button", { name: copy.diskUsage.collapseSetup, exact: true });
  const expand = actions.getByRole("button", { name: copy.diskUsage.expandSetup, exact: true });
  if (await collapse.count() > 0) await collapse.click();
  await expand.waitFor();
  assert(await setup.count() === 0, "Collapsed setup still renders the setup panel");
  assert(await page.locator(".disk-usage-volumes").count() === 0, "Collapsed setup still renders the volume list");
  const collapsedFirst = await page.evaluate(() =>
    document.querySelector(".disk-usage-page")?.firstElementChild?.className ?? "");
  assert(collapsedFirst.includes("disk-usage-setup-actions"), `Collapsed page starts with ${collapsedFirst}`);
  await expand.click();
  await setup.waitFor();
  await expectOrder("expanded with result", ["setting", "volumes", "actions", "result"]);
  await page.locator(`.disk-usage-treemap-canvas${NOT_GHOST}`).waitFor();

  assert(errors.length === 0, `Page errors: ${errors.join("; ")}`);
}
