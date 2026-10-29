import assert from "node:assert/strict";
import { loadAppCopy } from "../../src/i18n/copy/appCopyLoader.ts";
import { languageOptions } from "../../src/i18n/settingsLanguages.ts";
import { kernelLeakReportFrames } from "../fixtures/kernelLeakReports.js";
import { writeFrontendPreferences } from "./frontendPreferencesFixture.js";

/*
 * 详细信息 → 内核泄漏报告（docs/kernel_leak_report_feature_2026-10-27.md，合同 v3：条目生命周期见 docs/report_lifecycle_2026-10-28.md）。
 * 1. 每个模拟场景（tests/fixtures/kernelLeakReports.js）按后端发来的内容显示：状态（关闭、观察中、未发现、疑似、本轮失败）、
 *    原因、本轮观察进度（照发来的数写已达到多少、需要多少）、上一轮失败及其起止时间、下一轮何时开始、读不到的持有者数、
 *    每项的资源种类、持有者（映像和服务各按自己的状态）、按单位写的增长证据、首次和最近判出时间、采集时间；不合合同的帧按读取失败显示。
 *    条目任何状态下都显示（本轮状态在上、条目在下）：活动条目在前、历史条目在后，历史条目带“历史”标记；摘要写活动、历史和因容量未记录的条目数。
 * 2. 每项两个处理操作：按钮不可用，名称来自文案；各自后面的问号里是风险等级和后端风险消息的译文；不可用的原因直接写在旁边。
 * 3. 订阅只在分页可见时存在：切到别的分页或别的页面后在可见性冷却期内退订，回来重新订阅（观察本身在后端，不随订阅重来）。
 * 4. 九种语言在最小窗口 760×520 和 1280×800 下：文字来自该语言的文案，面板和每一项都不横向溢出。
 * 5. 清空历史（docs/report_lifecycle_2026-10-28.md）：没有历史条目时按钮不可用；确认框写明要删的条数和保留什么，取消不发请求；
 *    确认后提示后端回复的删除条数，后端发来的新帧里只剩活动条目。
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

  // 清空历史：疑似场景有 6 条，其中 1 条历史。
  const clearHistory = report.getByRole("button", { name: zh.reportHistory.clear, exact: true });
  await setScenario(baseUrl, { kernelLeakReport: "empty" });
  await report.locator(".kernel-leak-item").first().waitFor({ state: "detached" });
  assert.equal(await clearHistory.isDisabled(), true, "clear history is unavailable without history entries");
  await setScenario(baseUrl, { kernelLeakReport: "suspected" });
  await report.locator(".kernel-leak-item.history").waitFor();
  const clearRequests = [];
  const recordClear = request => {
    if (new URL(request.url()).pathname === "/api/kernel-leaks/history/clear") clearRequests.push(request.method());
  };
  page.on("request", recordClear);
  const confirmClear = page.getByRole("dialog", { name: zh.reportHistory.confirmTitle, exact: true });
  await clearHistory.click();
  const confirmText = await confirmClear.textContent();
  assert(confirmText.includes(zh.reportHistory.confirmMessage.kernelLeaks("1"))
    && zh.reportHistory.details.kernelLeaks.every(line => confirmText.includes(line)),
    `clear confirmation gives the count and what is kept: ${confirmText}`);
  await confirmClear.getByRole("button", { name: zh.feedback.cancel, exact: true }).click();
  await confirmClear.waitFor({ state: "detached" });
  assert.deepEqual(clearRequests, [], "cancelling the clear confirmation sends nothing");
  await clearHistory.click();
  await confirmClear.getByRole("button", { name: zh.reportHistory.confirm, exact: true }).click();
  await page.locator(".toast.success:not([data-exit-ghost])").filter({ hasText: zh.reportHistory.cleared("1") }).waitFor();
  await report.locator(".kernel-leak-item.history").waitFor({ state: "detached" });
  assert.equal(await report.locator(".kernel-leak-item:not([data-exit-ghost])").count(), 5, "the active entries stay after clearing");
  assert.deepEqual(clearRequests, ["POST"], "clear history posts once");
  assert.equal(await clearHistory.isDisabled(), true, "clear history is unavailable once the history is gone");
  page.off("request", recordClear);

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
        if (name === "suspected") await checkDetails(page, localized, app, language, `${language} ${size.width}×${size.height}`);
        const overflow = await overflowAt(localized);
        assert.deepEqual(overflow, [], `${language} ${size.width}×${size.height} ${name} overflows: ${JSON.stringify(overflow)}`);
        layouts.push(`${language}@${size.width}x${size.height}:${name}`);
      }
    }
  }
  assert.deepEqual(errors, []);
  return { scenarios, layouts: layouts.length, subscriptions: lifecycle.logicalStarted, historyCleared: true };
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
    NotObserved: { title: copy.notObservedTitle, detail: copy.notObservedDetail },
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
      ...(frame.items.length > 0 || frame.unrecordedItemCount > 0 ? [
        `${copy.suspectedItems}=${number(frame.items.filter(item => item.state === "active").length)}`,
        `${copy.historyItems}=${number(frame.items.filter(item => item.state === "history").length)}`,
        ...(frame.unrecordedItemCount > 0 ? [`${copy.unrecordedItems}=${number(frame.unrecordedItemCount)}`] : [])
      ] : [])
    ],
    observation: !decodes || frame.status === "NotObserved" ? [] : [
      `${copy.observation.startedAt}=${observation.startedAt ? localTime(observation.startedAt) : copy.observation.notStarted}`,
      `${copy.observation.elapsed}=${copy.observation.reachedOfRequired(duration(observation.elapsedSeconds), duration(observation.requiredSeconds))}`,
      `${copy.observation.samples}=${copy.observation.reachedOfRequired(number(observation.sampleCount), number(observation.requiredSampleCount))}`
    ],
    previousRun: !decodes || !previous ? null : {
      title: copy.previousRun.title,
      rows: [
        `${copy.previousRun.reason}=${copy.failure[previous.failure]}`,
        `${copy.previousRun.startedAt}=${localTime(previous.startedAt)}`,
        `${copy.previousRun.failedAt}=${localTime(previous.failedAt)}`
      ]
    },
    restart: decodes && (frame.status === "Unavailable" || previous) ? copy.restartPolicy[observation.restartPolicy] : null,
    notice: decodes && frame.unavailableHolderCount > 0 ? copy.unavailableHolders(frame.unavailableHolderCount) : null,
    items: decodes ? frame.items.length : 0
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
    assert.equal(await report.locator(".kernel-leak-items").getAttribute("aria-label"), copy.items, `${language} ${name}: list name`);
    const items = report.locator(".kernel-leak-item");
    const ordered = [...frame.items.filter(item => item.state === "active"), ...frame.items.filter(item => item.state === "history")];
    for (const [index, wire] of ordered.entries()) {
      await checkItem(page, items.nth(index), wire, app, number, localTime, `${language} ${name}[${index}]`, hints && index === 0);
    }
  }
  return expected.state?.title ?? `Suspected:${expected.items}`;
}

function duration(value) {
  const seconds = Math.floor(value);
  const pad = part => String(part).padStart(2, "0");
  return `${Math.floor(seconds / 3600)}:${pad(Math.floor(seconds / 60) % 60)}:${pad(seconds % 60)}`;
}

async function checkItem(page, item, wire, app, number, localTime, label, hints) {
  const copy = app.kernelLeakReport;
  const holder = wire.holder;
  const image = holder.image;
  const metadata = status => status === "ProcessEnded" ? copy.holder.processEnded : copy.holder.unavailable;
  const imageCurrent = image.status === "Current";
  const title = (await item.locator(".kernel-leak-item-title strong").textContent())?.trim();
  assert.equal(title, imageCurrent ? image.name : metadata(image.status), `${label} title`);
  assert.equal((await item.locator(".kernel-leak-item-title span").textContent())?.trim(), `${copy.holder.processId} ${holder.processId}`, `${label} PID`);
  assert.equal((await item.locator(".kernel-leak-kind").textContent())?.trim(), copy.resourceKind[wire.resourceKind], `${label} kind`);
  // 历史条目带标记，底色和标题退到次要；活动条目没有标记。
  const history = wire.state === "history";
  assert.equal(await item.evaluate(node => node.classList.contains("history")), history, `${label} history class`);
  assert.deepEqual((await item.locator(".kernel-leak-history").allTextContents()).map(text => text.trim()), history ? [copy.historyEntry] : [], `${label} history tag`);
  if (history) {
    const colors = await item.evaluate(node => ({
      title: getComputedStyle(node.querySelector(".kernel-leak-item-title strong")).color,
      muted: getComputedStyle(node.querySelector(".kernel-leak-item-title span")).color,
      background: getComputedStyle(node).backgroundColor
    }));
    assert.equal(colors.title, colors.muted, `${label}: a history title uses the muted color`);
    assert.equal(colors.background, "rgba(0, 0, 0, 0)", `${label}: a history entry has no surface fill`);
  }

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
    [copy.evidence.sampleCount, number(wire.sampleCount)],
    [copy.evidence.firstObserved, localTime(wire.firstObservedAt)],
    [copy.evidence.lastObserved, localTime(wire.lastObservedAt)]
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
  assert.equal(await item.locator("button").count(), wire.actions.length * 2 + 1,
    `${label}: only the details button, the action buttons and their question marks`);
  assert.equal((await item.locator(".kernel-leak-item-tags .details-button").textContent())?.trim(), copy.details.button, `${label} details button`);
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

/**
 * 条目的“详细信息”：第一个活动条目和第一个历史条目各打开一次。本分页里换成这一条的详细信息页，焦点到页标题；
 * 页上列出这个条目发来的全部内容（持有进程含启动键，判定依据含状态和资源类型，每个操作的风险说明和暂不可用的原因），
 * 不横向溢出；返回列表后焦点回到这一条的按钮。
 */
async function checkDetails(page, report, app, language, label) {
  const copy = app.kernelLeakReport;
  const frame = kernelLeakReportFrames.suspected;
  const messages = app.backendMessage.kernelLeak;
  const riskMessages = { 2: messages.restartHolderRisk, 3: messages.forceReleaseHandlesRisk };
  const number = (value, options = {}) => new Intl.NumberFormat(language, options).format(value);
  const metadata = status => status === "ProcessEnded" ? copy.holder.processEnded : copy.holder.unavailable;
  const ordered = [...frame.items.filter(item => item.state === "active"), ...frame.items.filter(item => item.state === "history")];
  for (const index of [0, ordered.findIndex(item => item.state === "history")]) {
    const wire = ordered[index];
    const holder = wire.holder;
    const image = holder.image;
    const imageCurrent = image.status === "Current";
    const name = imageCurrent ? image.name : metadata(image.status);
    const item = report.locator(".kernel-leak-item").nth(index);
    const cardEvidence = await item.locator(".kernel-leak-evidence > div").evaluateAll(rows =>
      rows.map(row => [row.querySelector("dt").textContent.trim(), row.querySelector("dd").textContent.trim()]));
    const button = item.locator(".kernel-leak-item-tags .details-button");
    await button.click();
    const detailsPage = report.getByRole("article", { name: copy.details.title(name), exact: true });
    await detailsPage.waitFor();
    assert.equal(await report.evaluate(root => [...root.querySelectorAll(".kernel-leak-items")].filter(list => !list.closest("[data-exit-ghost]")).length), 0,
      `${label}: the details page replaces the list`);
    await page.waitForFunction(() => document.activeElement?.classList.contains("kernel-leak-item-page-title"));
    const sections = await detailsPage.locator(".user-details-section").evaluateAll(nodes => nodes.map(node => ({
      title: node.querySelector("h4")?.textContent.trim() ?? null,
      rows: [...node.querySelectorAll(".user-details-row")].map(row => [row.querySelector("dt").textContent.trim(), row.querySelector("dd").textContent.trim()])
    })));
    const services = holder.services.status !== "Current" ? metadata(holder.services.status)
      : holder.services.names.length > 0 ? new Intl.ListFormat(language, { type: "conjunction" }).format(holder.services.names)
        : copy.holder.noServices;
    assert.deepEqual(sections, [
      { title: copy.details.holderSection, rows: [
        [copy.details.processName, name],
        [copy.holder.processId, String(holder.processId)],
        [copy.details.startKey, holder.startKey],
        [copy.holder.productName, imageCurrent ? image.productName ?? copy.holder.noProductName : metadata(image.status)],
        [copy.holder.services, services],
        [copy.holder.imagePath, imageCurrent ? image.path : metadata(image.status)]
      ] },
      { title: copy.details.evidenceSection, rows: [
        [copy.details.state, wire.state === "history" ? copy.historyEntry : copy.details.active],
        [copy.details.resourceKind, copy.resourceKind[wire.resourceKind]],
        ...cardEvidence
      ] },
      { title: copy.actions.title, rows: [
        ...wire.actions.map(action => [copy.actions.name[action.action],
          copy.actions.riskText(copy.actions.riskLevel[action.riskLevel], riskMessages[action.risk.code])]),
        [copy.details.unavailableReason, messages.actionNotProvided]
      ] }
    ], `${label} details of ${wire.state} item ${number(index)}`);
    // 只给读屏器的文字（.sr-only）本来就裁在 1px 里，不算溢出。
    const overflow = await detailsPage.evaluate(root => [...root.querySelectorAll("*")].filter(element =>
      !element.closest(".sr-only")
        && element.getBoundingClientRect().width > 0 && element.scrollWidth > element.clientWidth + 1
        && getComputedStyle(element).overflowX !== "visible" && getComputedStyle(element).overflowX !== "auto")
      .map(element => element.className || element.tagName).slice(0, 5));
    assert.deepEqual(overflow, [], `${label}: the details page overflows`);
    await detailsPage.getByRole("button", { name: copy.details.back, exact: true }).click();
    // 活动条目和历史各是一个列表；回到列表时等第一个出现。
    await report.locator(".kernel-leak-items").first().waitFor();
    await page.waitForFunction(key => document.activeElement?.classList.contains("details-button")
      && document.activeElement.closest("[data-kernel-leak-item]")?.getAttribute("data-kernel-leak-item") === key,
      `${wire.resourceKind}:${holder.processId}:${holder.startKey}`);
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
