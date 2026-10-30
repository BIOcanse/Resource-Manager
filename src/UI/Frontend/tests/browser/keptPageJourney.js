import assert from "node:assert/strict";
import { loadAppCopy } from "../../src/i18n/copy/appCopyLoader.ts";
import { loadSettingsText } from "../../src/i18n/settingsLoader.ts";
import { ResourceManager } from "../../src/preferences/generated/frontendPoints.values.ts";

const MONITOR_SUBSCRIPTIONS = ["/api/metrics/subscribe", "/api/resource-monitor/subscribe", "/api/resource-facts/subscribe"];
// 每个一级页自己读的接口（前缀）。隐藏的常驻页对它们零读取：不发请求、不持有订阅（第 4.4 节“建造与活动”的验收）。
// 只列这一页独有的；应用级运行上下文（本机状态、自调度、清单修订、访问请求等）不归任何一页，不在这里。
const PAGE_ENDPOINTS = {
  monitor: ["/api/metrics/subscribe", "/api/metrics/snapshot", "/api/resource-monitor/", "/api/resource-facts/", "/api/resource-breakdown/"],
  components: ["/api/components", "/api/software", "/api/browser-runtimes"],
  optimization: ["/api/optimization/reports", "/api/optimization/smart/status"],
  diskUsage: ["/api/disk-usage/"],
  control: ["/api/control/"],
  details: ["/api/cpu/", "/api/device-topology/", "/api/kernel-leaks/", "/api/optimization/smart/state/", "/api/gpu/"],
  settings: ["/api/memory-protection", "/api/settings/report-rules", "/api/settings/memory-cpu-tier", "/api/access-keys", "/api/startup-modules/"]
};
// 离开后观察这么久：长于各页的刷新间隔，期间窗口还回到一次前台。
const HIDDEN_OBSERVATION_MS = 2500;

/*
 * 常驻页（src/app/navigation/pageHost.tsx，自由点 refresh-kept_pages）：离开监视页后它的 DOM 还在，但不显示、不可聚焦、
 * 不可命中、不在无障碍树里，也不持有任何订阅；未保存的编辑在离开时放弃；回来时还是同一份 DOM，订阅重新取得。
 * 其余常驻页按同样的显示规则检查。默认构建只常驻监视页；RM_KEPT_PAGES_EXPECTED（逗号分隔）给出所测构建的常驻页，
 * 用于 buildKeptPagesVariant.mjs 构建的、全部可选页都常驻的那份。
 */
const PAGES = ["monitor", "components", "optimization", "diskUsage", "control", "details", "settings"];

export default async function keptPageJourney(page, baseUrl) {
  const expectedVariant = process.env.RM_KEPT_PAGES_EXPECTED;
  if (!expectedVariant) {
    assert.deepEqual([...ResourceManager.FrontendPoints.KeptPages], ["monitor"], "the build keeps the monitor page by default");
  }
  const kept = new Set(expectedVariant ? expectedVariant.split(",") : ResourceManager.FrontendPoints.KeptPages);
  assert.ok(kept.has("monitor"), "the tested build keeps the monitor page");
  const visited = new Set(["monitor"]);
  const app = await loadAppCopy("zh-CN");
  const errors = [];
  page.on("pageerror", error => errors.push(String(error)));
  await page.setViewportSize({ width: 1280, height: 800 });
  // 性能优化页只在后端报告优化运行时可用时出现在导航里；细节页的评分编辑要有运行时效果的所有者。
  if (kept.has("optimization") || kept.has("details")) {
    const response = await fetch(`${baseUrl}/__test/scenario`, { method: "POST", headers: { "Content-Type": "application/json" },
      body: JSON.stringify({ optimizationRuntime: true, runtimeEffectOwners: true }) });
    assert.equal(response.ok, true, "the mock backend enables the optimization runtime and runtime effect owners");
  }
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
  // 离开时页里还有一段过渡在播（这里起一段 1 s 的）：隐藏时它暂停、回来接着播。隐藏的页不再算样式，
  // 播完的过渡清不掉，会一直让主线程每个刷新周期出一帧空帧。
  await page.evaluate(() => {
    const target = document.querySelector("#monitorPage .monitor-toolbar h2");
    // important：无动画、超高性能风格把所有过渡时长清零，这一段不受它影响。
    target.style.setProperty("transition", "opacity 1000ms linear", "important");
    getComputedStyle(target).opacity;
    target.style.opacity = "0.5";
    getComputedStyle(target).opacity;
    window.__keptTransitionTarget = target;
  });
  await primary("settings").click();
  visited.add("settings");
  await page.locator(".page-host[data-page-id='settings']:not([aria-hidden='true']) .settings-page").first().waitFor({ state: "visible" });

  // 先看过渡，再做下面的聚焦、命中探测：探测要读隐藏页里元素的样式和布局，会让浏览器临时给它算一次样式。
  // 只看过渡：卡在隐藏页里的是过渡；隐藏页里循环播放的等待读数呼吸（CSS 动画）实测不让主线程出帧。
  const animations = await page.evaluate(() => {
    const host = document.querySelector(".page-host[data-page-id='monitor']");
    return {
      playing: document.getAnimations().filter(animation => animation instanceof CSSTransition
        && host.contains(animation.effect?.target ?? null) && animation.playState !== "paused")
        .map(animation => `${animation.transitionProperty} ${animation.playState}`),
      startedTransition: window.__keptTransitionTarget.getAnimations().map(animation => animation.playState)
    };
  });
  assert.deepEqual(animations.playing, [], "the hidden monitor plays no transition and keeps no finished one");
  assert.deepEqual(animations.startedTransition, ["paused"], "the transition in flight when the monitor was hidden is paused");
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
  // 只有当前页和打开过的常驻页有内容；其余页的位置是空的。
  assertHosts(hidden.hosts, "settings", kept, visited);
  assert.equal(await page.getByRole("heading", { name: app.monitorPage.liveMetrics, exact: true }).count(), 0,
    "the hidden monitor is not in the accessibility tree");
  await waitTelemetry(baseUrl, t => MONITOR_SUBSCRIPTIONS.every(path => (t[path]?.logicalInFlight ?? 0) === 0),
    "the hidden monitor holds no subscriptions");
  await assertNoReadsWhileHidden(page, baseUrl, "monitor");

  await primary("monitor").click();
  const shown = await page.evaluate(() => new Promise(resolve => requestAnimationFrame(() => {
    const host = document.querySelector(".page-host[data-page-id='monitor']");
    resolve({ sameNode: document.querySelector("#monitorPage") === window.__keptMonitorNode, ariaHidden: host.getAttribute("aria-hidden"),
      contentVisibility: getComputedStyle(host).contentVisibility,
      cards: document.querySelectorAll("#monitorPage .card-slot").length,
      resumedTransition: window.__keptTransitionTarget.getAnimations().map(animation => animation.playState) });
  })));
  assert.deepEqual(shown, { sameNode: true, ariaHidden: null, contentVisibility: "visible", cards: shown.cards, resumedTransition: ["running"] },
    "the monitor comes back as the same DOM and the paused transition plays on");
  await page.evaluate(() => {
    window.__keptTransitionTarget.style.removeProperty("transition");
    window.__keptTransitionTarget.style.removeProperty("opacity");
  });
  assert.ok(shown.cards > 0, "the first frame back shows the dashboard");
  assert.equal(await editButton.getAttribute("aria-label"), app.monitorPage.editLayoutLabel, "leaving discarded the unsaved edit");
  await waitTelemetry(baseUrl, t => t["/api/metrics/subscribe"]?.logicalInFlight > 0, "monitor acquires its metrics again");

  // 其余常驻页：打开、离开（回监视页）、再回来，检查和监视页相同的显示规则。
  const others = PAGES.filter(id => id !== "monitor" && kept.has(id));
  const leaveChecks = pageLeaveChecks(page, app, await loadSettingsText("zh-CN"));
  for (const id of others) {
    await primary(id).click();
    visited.add(id);
    await shownHost(page, id).waitFor({ state: "attached" });
    // 离开前进入这一页的编辑等状态；离开时 onPageLeave 要把它放弃。
    await leaveChecks[id]?.prepare();
    await page.evaluate(id => { window.__keptPageNode = document.querySelector(`.page-host[data-page-id='${id}']`).firstElementChild; }, id);
    await primary("monitor").click();
    await editButton.waitFor({ state: "visible" });
    const away = await page.evaluate(id => {
      const host = document.querySelector(`.page-host[data-page-id='${id}']`);
      const control = host.querySelector("button, [tabindex], input, select, textarea, a[href]");
      const before = document.activeElement;
      control?.focus();
      const box = control?.getBoundingClientRect();
      const hit = box ? document.elementFromPoint(box.x + box.width / 2, box.y + box.height / 2) : null;
      return {
        sameNode: host.firstElementChild === window.__keptPageNode && Boolean(window.__keptPageNode?.isConnected),
        ariaHidden: host.getAttribute("aria-hidden"),
        contentVisibility: getComputedStyle(host).contentVisibility,
        height: host.getBoundingClientRect().height,
        hasControl: Boolean(control),
        focusable: Boolean(control) && (document.activeElement === control || document.activeElement !== before),
        hit: Boolean(hit && host.contains(hit)),
        hosts: [...document.querySelectorAll("#pageStage > .page-host")].map(element => ({ id: element.dataset.pageId, hidden: element.getAttribute("aria-hidden") === "true", children: element.childElementCount }))
      };
    }, id);
    assert.equal(away.sameNode, true, `the hidden ${id} page keeps its DOM`);
    assert.equal(away.ariaHidden, "true", `the hidden ${id} page is hidden from assistive technology`);
    assert.equal(away.contentVisibility, "hidden", `the hidden ${id} page is not laid out or painted`);
    assert.equal(away.height, 0, `the hidden ${id} page takes no space`);
    assert.equal(away.hasControl, true, `the ${id} page has a control to probe`);
    assert.equal(away.focusable, false, `controls of the hidden ${id} page cannot take focus`);
    assert.equal(away.hit, false, `controls of the hidden ${id} page cannot be hit`);
    assertHosts(away.hosts, "monitor", kept, visited);
    await assertNoReadsWhileHidden(page, baseUrl, id);
    await leaveChecks[id]?.whileAway?.();
    await primary(id).click();
    await shownHost(page, id).waitFor({ state: "attached" });
    const back = await page.evaluate(id => {
      const host = document.querySelector(`.page-host[data-page-id='${id}']`);
      return { sameNode: host.firstElementChild === window.__keptPageNode, ariaHidden: host.getAttribute("aria-hidden"), contentVisibility: getComputedStyle(host).contentVisibility };
    }, id);
    assert.deepEqual(back, { sameNode: true, ariaHidden: null, contentVisibility: "visible" }, `${id} comes back as the same DOM`);
    await leaveChecks[id]?.verify();
  }
  // 隐藏的常驻页里不留没走完的进场、退场：动画停下后没有幽灵，也没有挂着 will-change 的进场节点。
  const unsettled = await page.evaluate(async () => {
    const deadline = performance.now() + 5000;
    const pending = () => [...document.querySelectorAll("[data-exit-ghost], [style*='will-change: opacity']")];
    while (pending().length > 0 && performance.now() < deadline) await new Promise(resolve => setTimeout(resolve, 100));
    return pending().map(element => ({
      host: element.closest(".page-host")?.dataset.pageId ?? null,
      ghost: element.hasAttribute("data-exit-ghost"),
      node: element.outerHTML.slice(0, 160)
    }));
  });
  assert.deepEqual(unsettled, [], "no entrance or exit is left running, on the current page or a hidden kept page");
  assert.deepEqual(errors, []);
  return { keptNode: true, hiddenInert: true, hiddenReleased: true, editDiscarded: true, otherKeptPages: others };
}

// 每页离开时要放弃的东西：prepare 在离开前把它做出来，verify 在回来后确认已放弃；whileAway 检查隐藏时不做的事。
function pageLeaveChecks(page, app, settingsText) {
  // 窗口回到前台时，隐藏的页不重读自己的数据。
  const noReadsOnFocus = (prefix, what) => async () => {
    const paths = [];
    const record = request => {
      const path = new URL(request.url()).pathname;
      if (request.method() === "GET" && path.startsWith(prefix)) paths.push(path);
    };
    page.on("request", record);
    await page.evaluate(() => window.dispatchEvent(new Event("focus")));
    await page.waitForTimeout(600);
    page.off("request", record);
    assert.deepEqual(paths, [], `the hidden page reads no ${what} when the window regains focus`);
  };
  return {
    diskUsage: {
      async prepare() {},
      async verify() {},
      whileAway: noReadsOnFocus("/api/disk-usage/", "disk usage")
    },
    settings: {
      // 改主机内存保护的开关不保存：离开时放弃草稿，回来时开关是已保存的值（同一个节点，Switch 在隐藏的页里也量得到尺寸）。
      async prepare() {
        await page.getByRole("button", { name: settingsText.sections.memoryProtection, exact: true }).click();
        const box = page.locator(".page-host[data-page-id='settings'] .memory-protection-settings input[type='checkbox']:not(:disabled)").first();
        await box.waitFor({ state: "attached" });
        const initial = await box.isChecked();
        await box.setChecked(!initial, { force: true });
        assert.equal(await box.isChecked(), !initial, "the settings switch changed in the draft");
        await box.evaluate((element, initial) => { window.__keptSwitch = { element, initial }; }, initial);
      },
      async verify() {
        await page.waitForFunction(() => window.__keptSwitch.element.isConnected
          && window.__keptSwitch.element.checked === window.__keptSwitch.initial);
      }
    },
    control: {
      // 只改草稿、不应用：选软件管理（草稿），再填一个配置名；离开时两样都放弃，回来时归属是已保存的固件管理。
      async prepare() {
        const card = page.locator(".control-object").filter({ hasText: "Fixture GPU" });
        await card.getByRole("radio", { name: app.control.ownership.app, exact: true }).click();
        await card.getByRole("textbox", { name: app.control.presets.namePlaceholder, exact: true }).fill("kept-page");
      },
      async verify() {
        const card = page.locator(".control-object").filter({ hasText: "Fixture GPU" });
        assert.equal(await card.getByRole("radio", { name: app.control.ownership.firmware, exact: true }).isChecked(), true,
          "leaving the control page discarded the unapplied ownership draft");
        assert.equal(await card.getByRole("textbox", { name: app.control.presets.namePlaceholder, exact: true }).count(), 0,
          "with the draft discarded the card is back to firmware management and shows no preset name field");
      },
      whileAway: noReadsOnFocus("/api/control/", "control state")
    },
    details: {
      async prepare() {
        await page.getByRole("tab", { name: app.misc.detailsTab.cpu, exact: true }).click();
        const panel = page.getByLabel(app.cpuTopology.modelLabel);
        // 模拟后端的拓扑首帧来得慢（见 coreLeaseContractJourney）。
        await panel.getByRole("button", { name: app.cpuTopology.edit, exact: true }).click({ timeout: 65_000 });
        await panel.getByRole("button", { name: app.cpuTopology.save, exact: true }).waitFor();
      },
      async verify() {
        const panel = page.getByLabel(app.cpuTopology.modelLabel);
        await panel.getByRole("button", { name: app.cpuTopology.edit, exact: true }).waitFor();
        assert.equal(await panel.getByRole("button", { name: app.cpuTopology.save, exact: true }).count(), 0,
          "leaving the details page discarded the unsaved CPU score edit");
      }
    }
  };
}

function shownHost(page, id) {
  return page.locator(`.page-host[data-page-id='${id}']:not([aria-hidden='true']) > *`).first();
}

// 当前页显示；打开过的常驻页建着但不显示；其余页的位置是空的。
function assertHosts(hosts, current, kept, visited) {
  for (const host of hosts) {
    assert.equal(host.hidden, host.id !== current, `${host.id} host shown only when current`);
    if (host.id !== current) {
      assert.equal(host.children > 0, kept.has(host.id) && visited.has(host.id), `${host.id} is built while away only when kept and opened`);
    }
  }
}

// 离开一页后：先等它撤掉的订阅都退掉，再观察一段时间（其间窗口回到前台一次），这一页的接口不能有新的请求或订阅。
async function assertNoReadsWhileHidden(page, baseUrl, id) {
  const prefixes = PAGE_ENDPOINTS[id];
  const own = telemetry => Object.entries(telemetry).filter(([path]) => prefixes.some(prefix => path.startsWith(prefix)));
  const held = telemetry => own(telemetry).filter(([, item]) => (item.logicalInFlight ?? 0) > 0 || (item.physicalInFlight ?? 0) > 0)
    .map(([path]) => path);
  const deadline = Date.now() + 10000;
  let before = await readTelemetry(baseUrl);
  while (held(before).length > 0 && Date.now() < deadline) {
    await new Promise(resolve => setTimeout(resolve, 50));
    before = await readTelemetry(baseUrl);
  }
  assert.deepEqual(held(before), [], `the hidden ${id} page releases its reads and subscriptions`);
  await page.evaluate(() => window.dispatchEvent(new Event("focus")));
  await page.waitForTimeout(HIDDEN_OBSERVATION_MS);
  const after = await readTelemetry(baseUrl);
  const reads = own(after)
    .map(([path, item]) => ({ path, started: (item.started ?? 0) - (before[path]?.started ?? 0), held: item.logicalInFlight ?? 0 }))
    .filter(read => read.started > 0 || read.held > 0);
  assert.deepEqual(reads, [], `the hidden ${id} page makes no reads while hidden`);
}

async function readTelemetry(baseUrl) {
  return (await fetch(`${baseUrl}/__test/requests`)).json();
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
