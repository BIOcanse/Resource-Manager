import assert from "node:assert/strict";
import { loadAppCopy } from "../../src/i18n/copy/appCopyLoader.ts";
import { languageOptions } from "../../src/i18n/settingsLanguages.ts";
import { kernelLeakReportFrames } from "../fixtures/kernelLeakReports.js";
import { writeFrontendPreferences } from "./frontendPreferencesFixture.js";

/*
 * 详细信息 → 内核泄漏报告（docs/kernel_leak_report_feature_2026-10-27.md 第 2 期）。
 * 1. 每个模拟场景（tests/fixtures/kernelLeakReports.js）按后端发来的内容显示：状态、原因、读不到的持有者数、
 *    每项的资源种类、持有者（各部分按自己的状态）、增长证据、采集时间；不合合同的帧按读取失败显示。
 * 2. 订阅只在分页可见时存在：切到别的分页或别的页面后在可见性冷却期内退订，回来重新订阅。
 * 3. 九种语言在最小窗口 760×520 和 1280×800 下：文字来自该语言的文案，面板和每一项都不横向溢出。
 * 每一项只有一句“尚未提供处理操作”的说明，没有按钮（动作与风险说明是第 3 期，由本地服务发出）。
 */
const subscriptionPath = "/api/kernel-leaks/subscribe";

export default async function kernelLeakReportJourney(page, baseUrl) {
  const errors = [];
  page.on("pageerror", error => errors.push(String(error)));
  await setScenario(baseUrl, { kernelLeakReport: "empty" });
  await page.setViewportSize({ width: 1280, height: 800 });
  await page.goto(`${baseUrl}/`, { waitUntil: "domcontentloaded" });
  await writeFrontendPreferences(page, value => { value.appearance.language = "zh-CN"; });
  await page.reload({ waitUntil: "domcontentloaded" });

  const zh = await loadAppCopy("zh-CN");
  const report = await openReport(page, zh);
  await waitForTelemetry(baseUrl, t => t[subscriptionPath]?.logicalInFlight === 1, "visible report subscribes");

  const scenarios = {};
  for (const name of Object.keys(kernelLeakReportFrames)) {
    await setScenario(baseUrl, { kernelLeakReport: name });
    scenarios[name] = await checkScenario(page, report, zh, "zh-CN", name);
  }

  // 可见性：同一页面里换分页、换到别的页面，都在冷却期后退订；回来重新订阅并收到当前帧。
  const tabs = page.getByRole("tablist", { name: zh.misc.detailsTab.tabsLabel, exact: true });
  await tabs.getByRole("tab", { name: zh.misc.detailsTab.device, exact: true }).click();
  await waitForTelemetry(baseUrl, t => t[subscriptionPath]?.logicalInFlight === 0, "hidden tab unsubscribes");
  await tabs.getByRole("tab", { name: zh.kernelLeakReport.tab, exact: true }).click();
  await waitForTelemetry(baseUrl, t => t[subscriptionPath]?.logicalInFlight === 1, "shown tab subscribes again");
  await setScenario(baseUrl, { kernelLeakReport: "suspected" });
  await report.locator(".kernel-leak-item").nth(5).waitFor();
  await page.getByRole("navigation").getByRole("button", { name: zh.page.monitor, exact: true }).click();
  await waitForTelemetry(baseUrl, t => t[subscriptionPath]?.logicalInFlight === 0, "other page unsubscribes");
  // 回到详细信息时停在离开时的分页（与其他分页同一条规则），并重新订阅、显示当前帧。
  await page.getByRole("navigation").getByRole("button", { name: zh.page.details, exact: true }).click();
  assert.equal(await tabs.getByRole("tab", { name: zh.kernelLeakReport.tab, exact: true }).getAttribute("aria-selected"), "true",
    "returning to 详细信息 keeps the kernel leak tab selected");
  await waitForTelemetry(baseUrl, t => t[subscriptionPath]?.logicalInFlight === 1, "returned page subscribes again");
  await report.locator(".kernel-leak-item").nth(5).waitFor();
  await page.getByRole("navigation").getByRole("button", { name: zh.page.monitor, exact: true }).click();
  await waitForTelemetry(baseUrl, t => t[subscriptionPath]?.logicalInFlight === 0, "other page unsubscribes again");
  const lifecycle = (await telemetry(baseUrl))[subscriptionPath];
  assert(lifecycle.logicalStarted >= 3 && lifecycle.logicalRetired === lifecycle.logicalStarted,
    `every kernel leak subscription was released: ${JSON.stringify(lifecycle)}`);

  // 九种语言 × 两种窗口：疑似项列表和一个不可用状态（带读不到的持有者数）。
  const layouts = [];
  for (const option of languageOptions.filter(item => item.id !== "system")) {
    const language = option.id;
    const app = await loadAppCopy(language);
    await setScenario(baseUrl, { kernelLeakReport: "suspected" });
    await page.setViewportSize({ width: 1280, height: 800 });
    await writeFrontendPreferences(page, value => { value.appearance.language = language; });
    await page.reload({ waitUntil: "domcontentloaded" });
    const localized = await openReport(page, app);
    for (const size of [{ width: 1280, height: 800 }, { width: 760, height: 520 }]) {
      await page.setViewportSize(size);
      for (const name of ["suspected", "unavailable-source"]) {
        await setScenario(baseUrl, { kernelLeakReport: name });
        await checkScenario(page, localized, app, language, name);
        const overflow = await overflowAt(localized);
        assert.deepEqual(overflow, [], `${language} ${size.width}×${size.height} ${name} overflows: ${JSON.stringify(overflow)}`);
        layouts.push(`${language}@${size.width}x${size.height}:${name}`);
      }
    }
  }
  assert.deepEqual(errors, []);
  return { scenarios, layouts: layouts.length, subscriptions: lifecycle.logicalStarted };
}

async function openReport(page, app) {
  await page.getByRole("navigation").getByRole("button", { name: app.page.details, exact: true }).click();
  const tab = page.getByRole("tablist", { name: app.misc.detailsTab.tabsLabel, exact: true })
    .getByRole("tab", { name: app.kernelLeakReport.tab, exact: true });
  await tab.click();
  // 分页面板和报告区同名；报告区是 region。
  const report = page.getByRole("region", { name: app.kernelLeakReport.tab, exact: true });
  await report.locator(".kernel-leak-note").first().waitFor();
  assert.equal((await report.locator(".kernel-leak-note").first().textContent())?.trim(), app.kernelLeakReport.intro);
  return report;
}

/** 等这个场景的帧显示出来（整块状态、汇总、提示和项数都对上），再逐项核对；返回核对过的摘要。 */
async function checkScenario(page, report, app, language, name) {
  const copy = app.kernelLeakReport;
  const frame = kernelLeakReportFrames[name];
  const number = (value, options = {}) => new Intl.NumberFormat(language, options).format(value);
  const decodes = name !== "invalid";
  const state = !decodes ? { title: copy.readFailed, detail: null }
    : frame.status === "Empty" ? { title: copy.emptyTitle, detail: copy.emptyDetail }
      : frame.status === "Suspected" ? null
        : frame.failure === "NotObserved" ? { title: copy.notObservedTitle, detail: copy.failure.NotObserved }
          : { title: copy.unavailableTitle, detail: copy.failure[frame.failure] };
  const localTime = value => new Intl.DateTimeFormat(language, {
    year: "numeric", month: "numeric", day: "numeric", hour: "numeric", minute: "numeric", second: "numeric"
  }).format(new Date(value));
  // 不合合同的帧解码失败：不显示上一帧留下的任何值。
  const expected = {
    state,
    summary: !decodes ? [] : [
      ...(frame.capturedAt ? [`${copy.capturedAt}=${localTime(frame.capturedAt)}`] : []),
      ...(frame.status === "Suspected" ? [`${copy.suspectedItems}=${number(frame.items.length)}`] : [])
    ],
    notice: decodes && frame.unavailableHolderCount > 0 ? copy.unavailableHolders(frame.unavailableHolderCount) : null,
    items: decodes && frame.status === "Suspected" ? frame.items.length : 0
  };
  const read = () => report.evaluate(root => {
    const text = element => element?.textContent.trim() ?? null;
    const state = root.querySelector(".content-state");
    return {
      state: state ? { title: text(state.querySelector("strong")), detail: text(state.querySelector("p")) } : null,
      summary: [...root.querySelectorAll(".kernel-leak-summary > div")].map(row => `${text(row.querySelector("dt"))}=${text(row.querySelector("dd"))}`),
      notice: text(root.querySelector(".kernel-leak-note.warning")),
      items: root.querySelectorAll(".kernel-leak-item").length
    };
  });
  const deadline = Date.now() + 8000;
  let shown = await read();
  while (JSON.stringify(shown) !== JSON.stringify(expected) && Date.now() < deadline) {
    await page.waitForTimeout(50);
    shown = await read();
  }
  assert.deepEqual(shown, expected, `${language} ${name}: report state`);
  if (expected.items > 0) {
    const items = report.locator(".kernel-leak-item");
    for (const [index, wire] of frame.items.entries()) await checkItem(items.nth(index), wire, copy, number, `${language} ${name}[${index}]`);
  }
  return expected.state?.title ?? `Suspected:${expected.items}`;
}

async function checkItem(item, wire, copy, number, label) {
  const holder = wire.holder;
  const metadata = status => status === "ProcessEnded" ? copy.holder.processEnded : copy.holder.unavailable;
  const imageCurrent = holder.imageStatus === "Current";
  const title = (await item.locator(".kernel-leak-item-title strong").textContent())?.trim();
  assert.equal(title, imageCurrent ? holder.name : metadata(holder.imageStatus), `${label} title`);
  assert.equal((await item.locator(".kernel-leak-item-title span").textContent())?.trim(), `${copy.holder.processId} ${holder.processId}`, `${label} PID`);
  assert.equal((await item.locator(".kernel-leak-kind").textContent())?.trim(), copy.resourceKind[wire.resourceKind], `${label} kind`);

  const facts = Object.fromEntries(await item.locator(".kernel-leak-holder > div").evaluateAll(rows =>
    rows.map(row => [row.querySelector("dt").textContent.trim(), row.querySelector("dd").textContent.trim()])));
  assert.deepEqual(Object.keys(facts), [copy.holder.productName, copy.holder.services, copy.holder.imagePath], `${label} fact order`);
  assert.equal(facts[copy.holder.productName],
    imageCurrent ? holder.productName ?? copy.holder.noProductName : metadata(holder.imageStatus), `${label} product`);
  assert.equal(facts[copy.holder.imagePath], imageCurrent ? holder.imagePath : metadata(holder.imageStatus), `${label} path`);
  const services = (await item.locator(".kernel-leak-service").allTextContents()).map(text => text.trim());
  if (holder.serviceStatus === "Current" && holder.serviceNames.length > 0) {
    assert.deepEqual(services, holder.serviceNames, `${label} services`);
  } else {
    assert.deepEqual(services, [], `${label} no service names`);
    assert.equal(facts[copy.holder.services], holder.serviceStatus === "Current" ? copy.holder.noServices : metadata(holder.serviceStatus), `${label} services state`);
  }

  const evidence = await item.locator(".kernel-leak-evidence > div").evaluateAll(rows =>
    rows.map(row => [row.querySelector("dt").textContent.trim(), row.querySelector("dd").textContent.trim()]));
  const seconds = Math.floor(wire.observationSeconds);
  const pad = value => String(value).padStart(2, "0");
  assert.deepEqual(evidence, [
    [copy.evidence.currentValue, number(wire.currentValue)],
    [copy.evidence.growth, number(wire.growth, { signDisplay: "exceptZero" })],
    [copy.evidence.growthPerSecond, copy.ratePerSecond(number(wire.growthPerSecond, { maximumFractionDigits: 2, signDisplay: "exceptZero" }))],
    [copy.evidence.observationTime, `${Math.floor(seconds / 3600)}:${pad(Math.floor(seconds / 60) % 60)}:${pad(seconds % 60)}`],
    [copy.evidence.sampleCount, number(wire.sampleCount)]
  ], `${label} evidence`);

  // 第 3 期之前：一句说明，没有任何动作按钮或风险说明按钮。
  assert.equal((await item.locator(".kernel-leak-actions-pending").textContent())?.trim(), copy.actionsPending, `${label} actions note`);
  assert.equal(await item.locator("button").count(), 0, `${label} has no action buttons`);
}

/** 面板及其中每个文字元素在当前窗口下没有横向溢出；返回溢出的元素。 */
async function overflowAt(report) {
  return report.evaluate(root => {
    const failures = [];
    const doc = document.documentElement;
    if (doc.scrollWidth > doc.clientWidth + 1) failures.push({ element: "document", scroll: doc.scrollWidth, client: doc.clientWidth });
    const panel = root.getBoundingClientRect();
    for (const element of root.querySelectorAll(".kernel-leak-item, .kernel-leak-item *, .kernel-leak-summary *, .content-state *, .kernel-leak-note")) {
      const box = element.getBoundingClientRect();
      if (box.width === 0) continue;
      if (element.scrollWidth > element.clientWidth + 1 && getComputedStyle(element).overflowX !== "visible") {
        failures.push({ element: element.className || element.tagName, text: element.textContent.slice(0, 40), scroll: element.scrollWidth, client: element.clientWidth });
      }
      if (box.right > panel.right + 1 || box.left < panel.left - 1) {
        failures.push({ element: element.className || element.tagName, text: element.textContent.slice(0, 40), right: box.right, panelRight: panel.right });
      }
    }
    return failures.slice(0, 5);
  });
}

async function setScenario(baseUrl, patch) {
  const response = await fetch(`${baseUrl}/__test/scenario`, {
    method: "POST", headers: { "Content-Type": "application/json" }, body: JSON.stringify(patch)
  });
  if (!response.ok) throw new Error(`Unable to set scenario ${JSON.stringify(patch)}: ${response.status} ${await response.text()}`);
}

async function telemetry(baseUrl) {
  return (await fetch(`${baseUrl}/__test/requests`)).json();
}

async function waitForTelemetry(baseUrl, predicate, label) {
  // 默认可见性冷却 5000 ms（refresh-visibility_cooldown_ms），留出余量。
  const deadline = Date.now() + 12000;
  while (Date.now() < deadline) {
    if (predicate(await telemetry(baseUrl))) return;
    await new Promise(resolve => setTimeout(resolve, 100));
  }
  throw new Error(`Kernel leak subscription telemetry did not reach: ${label}; ${JSON.stringify((await telemetry(baseUrl))[subscriptionPath] ?? null)}`);
}
