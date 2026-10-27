import assert from "node:assert/strict";
import { loadAppCopy } from "../../src/i18n/copy/appCopyLoader.ts";
import { languageOptions } from "../../src/i18n/settingsLanguages.ts";
import { kernelLeakReportFrames } from "../fixtures/kernelLeakReports.js";
import { writeFrontendPreferences } from "./frontendPreferencesFixture.js";

/*
 * 详细信息 → 内核泄漏报告（docs/kernel_leak_report_feature_2026-10-27.md，合同 v2）。
 * 1. 每个模拟场景（tests/fixtures/kernelLeakReports.js）按后端发来的内容显示：状态（关闭、观察中、未发现、疑似、本轮失败）、
 *    原因、本轮观察进度（照发来的数写已达到多少、需要多少）、上一轮失败及其起止时间、下一轮何时开始、读不到的持有者数、
 *    每项的资源种类、持有者（映像和服务各按自己的状态）、按单位写的增长证据、采集时间；不合合同的帧按读取失败显示。
 * 2. 每项两个处理操作：按钮不可用，名称来自文案；各自后面的问号里是风险等级和后端风险消息的译文；不可用的原因直接写在旁边。
 * 3. 订阅只在分页可见时存在：切到别的分页或别的页面后在可见性冷却期内退订，回来重新订阅（观察本身在后端，不随订阅重来）。
 * 4. 九种语言在最小窗口 760×520 和 1280×800 下：文字来自该语言的文案，面板和每一项都不横向溢出。
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
    scenarios[name] = await checkScenario(page, report, zh, "zh-CN", name, { hints: name === "suspected" });
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

  // 九种语言 × 两种窗口：疑似项列表，和本轮失败且带着上一轮失败的报告（文字最多的非列表状态）。
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
      for (const name of ["suspected", "unavailable-after-failure"]) {
        await setScenario(baseUrl, { kernelLeakReport: name });
        await checkScenario(page, localized, app, language, name, { hints: name === "suspected" && size.width === 760 });
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
async function checkScenario(page, report, app, language, name, { hints = false } = {}) {
  const copy = app.kernelLeakReport;
  const frame = kernelLeakReportFrames[name];
  const number = (value, options = {}) => new Intl.NumberFormat(language, options).format(value);
  const decodes = name !== "invalid";
  const states = {
    Disabled: { title: copy.disabledTitle, detail: copy.disabledDetail },
    Observing: { title: copy.observingTitle, detail: copy.observingDetail },
    Empty: { title: copy.emptyTitle, detail: copy.emptyDetail },
    Suspected: null,
    Unavailable: { title: copy.unavailableTitle, detail: copy.failure[frame.failure] ?? null }
  };
  const state = !decodes ? { title: copy.readFailed, detail: null } : states[frame.status];
  const localTime = value => new Intl.DateTimeFormat(language, {
    year: "numeric", month: "numeric", day: "numeric", hour: "numeric", minute: "numeric", second: "numeric"
  }).format(new Date(value));
  const observation = frame.observation;
  const previous = observation.previousRunFailure;
  // 不合合同的帧解码失败：不显示上一帧留下的任何值。
  const expected = {
    state,
    summary: !decodes ? [] : [
      ...(frame.capturedAt ? [`${copy.capturedAt}=${localTime(frame.capturedAt)}`] : []),
      ...(frame.status === "Suspected" ? [`${copy.suspectedItems}=${number(frame.items.length)}`] : [])
    ],
    observation: !decodes || frame.status === "Disabled" ? [] : [
      `${copy.observation.startedAt}=${observation.startedAt ? localTime(observation.startedAt) : copy.observation.notStarted}`,
      `${copy.observation.elapsed}=${copy.observation.reachedOfRequired(duration(observation.elapsedSeconds), duration(observation.requiredSeconds))}`,
      `${copy.observation.samples}=${copy.observation.reachedOfRequired(number(observation.sampleCount), number(observation.requiredSampleCount))}`
    ],
    previousRun: !decodes || !previous ? null : {
      title: copy.previousRun.title,
      rows: [
        `${copy.previousRun.reason}=${copy.failure[previous.failure]}`,
        `${copy.previousRun.startedAt}=${previous.startedAt ? localTime(previous.startedAt) : copy.observation.notStarted}`,
        `${copy.previousRun.failedAt}=${localTime(previous.failedAt)}`
      ]
    },
    restart: decodes && (frame.status === "Unavailable" || previous) ? copy.restartPolicy[observation.restartPolicy] : null,
    notice: decodes && frame.unavailableHolderCount > 0 ? copy.unavailableHolders(frame.unavailableHolderCount) : null,
    items: decodes && frame.status === "Suspected" ? frame.items.length : 0
  };
  const read = () => report.evaluate(root => {
    const text = element => element?.textContent.trim() ?? null;
    const state = root.querySelector(".content-state");
    const rows = selector => [...root.querySelectorAll(selector)].map(row => `${text(row.querySelector("dt"))}=${text(row.querySelector("dd"))}`);
    const previous = root.querySelector(".kernel-leak-previous-run");
    const notes = [...root.querySelectorAll(".kernel-leak-note:not(.warning)")].slice(1);
    return {
      state: state ? { title: text(state.querySelector("strong")), detail: text(state.querySelector("p")) } : null,
      summary: rows(".kernel-leak-summary > div"),
      observation: rows(".kernel-leak-observation > div"),
      previousRun: previous ? { title: text(previous.querySelector(".kernel-leak-previous-run-title")), rows: rows(".kernel-leak-previous-run dl > div") } : null,
      restart: notes.length > 0 ? notes.map(text).join(" | ") : null,
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
    for (const [index, wire] of frame.items.entries()) {
      await checkItem(page, items.nth(index), wire, app, number, `${language} ${name}[${index}]`, hints && index === 0);
    }
  }
  return expected.state?.title ?? `Suspected:${expected.items}`;
}

function duration(value) {
  const seconds = Math.floor(value);
  const pad = part => String(part).padStart(2, "0");
  return `${Math.floor(seconds / 3600)}:${pad(Math.floor(seconds / 60) % 60)}:${pad(seconds % 60)}`;
}

async function checkItem(page, item, wire, app, number, label, hints) {
  const copy = app.kernelLeakReport;
  const holder = wire.holder;
  const image = holder.image;
  const metadata = status => status === "ProcessEnded" ? copy.holder.processEnded : copy.holder.unavailable;
  const imageCurrent = image.status === "Current";
  const title = (await item.locator(".kernel-leak-item-title strong").textContent())?.trim();
  assert.equal(title, imageCurrent ? image.name : metadata(image.status), `${label} title`);
  assert.equal((await item.locator(".kernel-leak-item-title span").textContent())?.trim(), `${copy.holder.processId} ${holder.processId}`, `${label} PID`);
  assert.equal((await item.locator(".kernel-leak-kind").textContent())?.trim(), copy.resourceKind[wire.resourceKind], `${label} kind`);

  const facts = Object.fromEntries(await item.locator(".kernel-leak-holder > div").evaluateAll(rows =>
    rows.map(row => [row.querySelector("dt").textContent.trim(), row.querySelector("dd").textContent.trim()])));
  assert.deepEqual(Object.keys(facts), [copy.holder.productName, copy.holder.services, copy.holder.imagePath], `${label} fact order`);
  assert.equal(facts[copy.holder.productName],
    imageCurrent ? image.productName ?? copy.holder.noProductName : metadata(image.status), `${label} product`);
  assert.equal(facts[copy.holder.imagePath], imageCurrent ? image.path : metadata(image.status), `${label} path`);
  const services = (await item.locator(".kernel-leak-service").allTextContents()).map(text => text.trim());
  if (holder.services.status === "Current" && holder.services.names.length > 0) {
    assert.deepEqual(services, holder.services.names, `${label} services`);
  } else {
    assert.deepEqual(services, [], `${label} no service names`);
    assert.equal(facts[copy.holder.services],
      holder.services.status === "Current" ? copy.holder.noServices : metadata(holder.services.status), `${label} services state`);
  }

  const evidence = await item.locator(".kernel-leak-evidence > div").evaluateAll(rows =>
    rows.map(row => [row.querySelector("dt").textContent.trim(), row.querySelector("dd").textContent.trim()]));
  assert.equal(wire.unit, "count", `${label}: the journey's frames publish counts`);
  assert.deepEqual(evidence, [
    [copy.evidence.currentValue, number(wire.currentValue)],
    [copy.evidence.growth, number(wire.growth, { signDisplay: "exceptZero" })],
    [copy.evidence.growthPerSecond, copy.ratePerSecond(number(wire.growthPerSecond, { maximumFractionDigits: 2, signDisplay: "exceptZero" }))],
    [copy.evidence.observationTime, duration(wire.observationSeconds)],
    [copy.evidence.sampleCount, number(wire.sampleCount)]
  ], `${label} evidence`);

  // 处理操作：按发来的顺序，每个是一个不可用的按钮，后面紧跟它的风险问号；不可用的原因（相同的只写一次）直接写在后面。
  const messages = app.backendMessage.kernelLeak;
  const riskMessages = { 2: messages.restartHolderRisk, 3: messages.forceReleaseHandlesRisk };
  const actions = await item.locator(".kernel-leak-action").evaluateAll(nodes => nodes.map(node => {
    const button = node.querySelector(".kernel-leak-action-button");
    const hint = node.querySelector(".help-hint");
    return {
      action: node.getAttribute("data-kernel-leak-action"),
      name: button?.textContent.trim() ?? null,
      disabled: button?.disabled ?? null,
      hintFollows: button?.nextElementSibling === hint && hint !== null,
      hintLabel: hint?.getAttribute("aria-label") ?? null,
      title: [...node.querySelectorAll("[title]")].length
    };
  }));
  assert.deepEqual(actions, wire.actions.map(action => ({
    action: action.action,
    name: copy.actions.name[action.action],
    disabled: true,
    hintFollows: true,
    hintLabel: copy.actions.riskLabel(copy.actions.name[action.action]),
    title: 0
  })), `${label} actions`);
  assert.deepEqual((await item.locator(".kernel-leak-action-reason").allTextContents()).map(text => text.trim()),
    [messages.actionNotProvided], `${label} disabled reason is shown once, visibly`);
  assert.equal(await item.locator("button").count(), wire.actions.length * 2, `${label}: only the action buttons and their question marks`);
  if (!hints) return;
  // 问号里的说明：风险等级加后端风险消息的译文（焦点打开，Esc 关上）。
  for (const [index, action] of wire.actions.entries()) {
    const hint = item.locator(".kernel-leak-action").nth(index).locator(".help-hint");
    await hint.focus();
    const popover = page.locator(".help-hint-popover:not([data-exit-ghost])");
    await popover.waitFor();
    assert.equal((await popover.textContent())?.trim(),
      copy.actions.riskText(copy.actions.riskLevel[action.riskLevel], riskMessages[action.risk.code]), `${label} ${action.action} risk`);
    await page.keyboard.press("Escape");
    await popover.waitFor({ state: "detached" });
  }
}

/** 面板及其中每个文字元素在当前窗口下没有横向溢出；返回溢出的元素。 */
async function overflowAt(report) {
  return report.evaluate(root => {
    const failures = [];
    const doc = document.documentElement;
    if (doc.scrollWidth > doc.clientWidth + 1) failures.push({ element: "document", scroll: doc.scrollWidth, client: doc.clientWidth });
    const panel = root.getBoundingClientRect();
    for (const element of root.querySelectorAll(".kernel-leak-item, .kernel-leak-item *, .kernel-leak-summary *, .kernel-leak-observation *, .kernel-leak-previous-run *, .content-state *, .kernel-leak-note")) {
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
