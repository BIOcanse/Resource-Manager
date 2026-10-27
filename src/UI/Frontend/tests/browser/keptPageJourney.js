import assert from "node:assert/strict";
import { loadAppCopy } from "../../src/i18n/copy/appCopyLoader.ts";
import { ResourceManager } from "../../src/preferences/generated/frontendPoints.values.ts";

const MONITOR_SUBSCRIPTIONS = ["/api/metrics/subscribe", "/api/resource-monitor/subscribe", "/api/resource-facts/subscribe"];

/*
 * 常驻页（src/app/navigation/pageHost.tsx，自由点 refresh-kept_pages）：离开监视页后它的 DOM 还在，但不显示、不可聚焦、
 * 不可命中、不在无障碍树里，也不持有任何订阅；未保存的编辑在离开时放弃；回来时还是同一份 DOM，订阅重新取得。
 */
export default async function keptPageJourney(page, baseUrl) {
  assert.deepEqual([...ResourceManager.FrontendPoints.KeptPages], ["monitor"], "the build keeps the monitor page by default");
  const app = await loadAppCopy("zh-CN");
  const errors = [];
  page.on("pageerror", error => errors.push(String(error)));
  await page.setViewportSize({ width: 1280, height: 800 });
  await page.goto(baseUrl, { waitUntil: "domcontentloaded" });
  const primary = id => page.getByRole("navigation", { name: app.shell.pageNav, exact: true })
    .getByRole("button", { name: app.page[id], exact: true });
  const editButton = page.locator("#monitorPage [data-focus-key='monitor-dashboard-edit']");
  await editButton.waitFor({ state: "visible" });
  await waitTelemetry(baseUrl, t => t["/api/metrics/subscribe"]?.logicalInFlight > 0, "monitor holds its metrics while shown");
  await page.evaluate(() => { window.__keptMonitorNode = document.querySelector("#monitorPage"); });

  // 进入卡片编辑，不保存就离开：离开时放弃编辑，和卸下时一样。
  await editButton.click();
  assert.equal(await editButton.getAttribute("aria-label"), app.monitorPage.saveLayoutLabel);
  await primary("settings").click();
  await page.locator(".page-host[data-page-id='settings']:not([aria-hidden='true']) .settings-page").first().waitFor({ state: "visible" });

  const hidden = await page.evaluate(() => {
    const host = document.querySelector(".page-host[data-page-id='monitor']");
    const button = host.querySelector("[data-focus-key='monitor-dashboard-edit']");
    const before = document.activeElement;
    button.focus();
    const box = button.getBoundingClientRect();
    const hit = document.elementFromPoint(box.x + box.width / 2, box.y + box.height / 2);
    return {
      sameNode: document.querySelector("#monitorPage") === window.__keptMonitorNode && window.__keptMonitorNode.isConnected,
      ariaHidden: host.getAttribute("aria-hidden"),
      contentVisibility: getComputedStyle(host).contentVisibility,
      height: host.getBoundingClientRect().height,
      focusable: document.activeElement === button || document.activeElement !== before,
      hit: Boolean(hit && host.contains(hit)),
      hosts: [...document.querySelectorAll("#pageStage > .page-host")].map(element => ({ id: element.dataset.pageId, hidden: element.getAttribute("aria-hidden") === "true", children: element.childElementCount }))
    };
  });
  assert.equal(hidden.sameNode, true, "the hidden monitor keeps its DOM");
  assert.equal(hidden.ariaHidden, "true", "the hidden monitor is hidden from assistive technology");
  assert.equal(hidden.focusable, false, "controls of the hidden monitor cannot take focus");
  assert.equal(hidden.hit, false, "controls of the hidden monitor cannot be hit");
  assert.equal(hidden.contentVisibility, "hidden", "the hidden monitor is not laid out or painted");
  assert.equal(hidden.height, 0, "the hidden monitor takes no space");
  // 只有当前页和常驻页有内容；其余页的位置是空的。
  for (const host of hidden.hosts) {
    assert.equal(host.hidden, host.id !== "settings", `${host.id} host shown only when current`);
    if (host.id !== "settings" && host.id !== "monitor") assert.equal(host.children, 0, `${host.id} is not built while away`);
  }
  assert.equal(await page.getByRole("heading", { name: app.monitorPage.liveMetrics, exact: true }).count(), 0,
    "the hidden monitor is not in the accessibility tree");
  await waitTelemetry(baseUrl, t => MONITOR_SUBSCRIPTIONS.every(path => (t[path]?.logicalInFlight ?? 0) === 0),
    "the hidden monitor holds no subscriptions");

  await primary("monitor").click();
  const shown = await page.evaluate(() => new Promise(resolve => requestAnimationFrame(() => {
    const host = document.querySelector(".page-host[data-page-id='monitor']");
    resolve({ sameNode: document.querySelector("#monitorPage") === window.__keptMonitorNode, ariaHidden: host.getAttribute("aria-hidden"),
      contentVisibility: getComputedStyle(host).contentVisibility,
      cards: document.querySelectorAll("#monitorPage .card-slot").length });
  })));
  assert.deepEqual(shown, { sameNode: true, ariaHidden: null, contentVisibility: "visible", cards: shown.cards });
  assert.ok(shown.cards > 0, "the first frame back shows the dashboard");
  assert.equal(await editButton.getAttribute("aria-label"), app.monitorPage.editLayoutLabel, "leaving discarded the unsaved edit");
  await waitTelemetry(baseUrl, t => t["/api/metrics/subscribe"]?.logicalInFlight > 0, "monitor acquires its metrics again");
  assert.deepEqual(errors, []);
  return { keptNode: true, hiddenInert: true, hiddenReleased: true, editDiscarded: true };
}

async function waitTelemetry(baseUrl, predicate, message) {
  const deadline = Date.now() + 10000;
  let telemetry = null;
  while (Date.now() < deadline) {
    telemetry = await (await fetch(`${baseUrl}/__test/requests`)).json();
    if (predicate(telemetry)) return;
    await new Promise(resolve => setTimeout(resolve, 50));
  }
  const inFlight = Object.fromEntries(MONITOR_SUBSCRIPTIONS.map(path => [path, telemetry?.[path]?.logicalInFlight ?? 0]));
  throw new Error(`${message}: ${JSON.stringify(inFlight)}`);
}
