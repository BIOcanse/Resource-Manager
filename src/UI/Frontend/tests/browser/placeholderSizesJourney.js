import assert from "node:assert/strict";
import { loadAppCopy } from "../../src/i18n/copy/appCopyLoader.ts";
import { loadSettingsText } from "../../src/i18n/settingsLoader.ts";
import { seedFrontendPreferences, writeFrontendPreferences } from "./frontendPreferencesFixture.js";

/*
 * 加载占位和加载后的内容占一样大的位置（用户 10-27：「让占位和加载后状态的逻辑占位大小相等」）：
 * 第一份数据到的时候，占位下面的东西不动。资源条区由 resource-bar-placeholder 旅程负责，这里是其余的占位：
 * - 页面顶部的运行能力提示、性能优化页的调度状态提示、组件页的操作状态和软件登记补充项提示：
 *   加载完成后这里什么也没有，所以加载中只读给读屏，不占位置，下面的内容不被推动；
 * - 主机内存保护分区：状态到之前，生效状态一行和阈值一行的读数按有数据时的结构各占位置（读数写“--”），
 *   正在读取照旧读给读屏；正常运行点一行的大小取决于有没有正常运行点（待用户裁定），这里只量它的上边；
 * - 监视页的卡片（读数到之前画一条占位）和资源表（加载中的框和表格一样高）：量出来本来就相等，钉住。
 * 每一处都先拦住带数据的那条请求，量完占位再放行，所以“还没到”是确定的，不靠时间。
 * 宽窗口和最小窗口、中英两种语言各走一遍；量到的前后数值都放在结果里。
 */

const streamUrl = (url) => url.pathname === "/api/subscriptions/stream";
const streamCarrying = (fragment) => ({
  url: streamUrl,
  holds: (request) => request.method() === "POST" && (request.postData() ?? "").includes(fragment)
});
const getRequest = (pathname) => ({ url: (url) => url.pathname === pathname, holds: (request) => request.method() === "GET" });

const sites = [
  {
    name: "runtime-capability-notice",
    request: getRequest("/api/runtime/capabilities"),
    async open(page, baseUrl) {
      await page.goto(baseUrl, { waitUntil: "domcontentloaded" });
      await page.locator("#monitorPage").waitFor();
    },
    loading: (app) => ({ scope: ".app-shell", text: app.observationNotice.loading(app.shell.runtimeCapability) }),
    measure: () => {
      const main = document.querySelector(".app-shell");
      const stage = document.querySelector("#pageStage");
      return { pageStageTop: stage.getBoundingClientRect().top + main.scrollTop };
    }
  },
  {
    name: "optimization-schedule-notice",
    scenario: { optimizationRuntime: true },
    request: streamCarrying("/api/optimization/smart/status/subscribe"),
    async open(page, baseUrl, app) {
      await page.goto(baseUrl, { waitUntil: "domcontentloaded" });
      await page.getByRole("navigation").getByRole("button", { name: app.page.optimization, exact: true }).click();
      await page.locator(".optimization-modebar").waitFor();
    },
    loading: (app) => ({ scope: "#optimizationPage", text: app.observationNotice.loading(app.optimization.scheduleStateLabel) }),
    measure: () => {
      const main = document.querySelector(".app-shell");
      return { modeBarTop: document.querySelector(".optimization-modebar").getBoundingClientRect().top + main.scrollTop };
    }
  },
  {
    name: "management-operation-notice",
    scenario: { runtimeEffectOwners: true },
    request: streamCarrying("/api/operations/subscribe"),
    async open(page, baseUrl, app) {
      await page.goto(baseUrl, { waitUntil: "domcontentloaded" });
      await page.getByRole("navigation").getByRole("button", { name: app.page.components, exact: true }).click();
      await page.locator("#managementTab-other").click();
      await page.locator("#componentsPage .management-list").waitFor();
    },
    loading: (app) => ({ scope: "#componentsPage", text: app.observationNotice.loading(app.managementPage.operationState) }),
    measure: () => {
      const main = document.querySelector(".app-shell");
      return { listTop: document.querySelector("#componentsPage .management-list").getBoundingClientRect().top + main.scrollTop };
    }
  },
  {
    name: "management-software-supplement-notice",
    request: getRequest("/api/software"),
    async open(page, baseUrl, app) {
      await page.goto(baseUrl, { waitUntil: "domcontentloaded" });
      await page.getByRole("navigation").getByRole("button", { name: app.page.components, exact: true }).click();
      await page.locator("#managementTab-Dependency").click();
      await page.locator("#componentsPage .component-item").first().waitFor();
    },
    loading: (app) => ({ scope: "#componentsPage", text: app.observationNotice.loading(app.managementPage.softwareRegistrySupplement) }),
    measure: () => {
      const main = document.querySelector(".app-shell");
      return { listTop: document.querySelector("#componentsPage .management-list").getBoundingClientRect().top + main.scrollTop };
    }
  },
  {
    name: "memory-protection-status",
    request: streamCarrying("/api/memory-protection/subscribe"),
    async open(page, baseUrl, app, settings) {
      await page.goto(baseUrl, { waitUntil: "domcontentloaded" });
      await page.getByRole("navigation").getByRole("button", { name: app.page.settings, exact: true }).click();
      await page.getByRole("button", { name: settings.sections.memoryProtection, exact: true }).click();
      await page.getByText(app.memoryProtection.intro, { exact: true }).waitFor();
    },
    loading: (app) => ({ scope: ".memory-protection-settings", text: app.memoryProtection.statusLoading }),
    loaded: ".memory-protection-reserve-status",
    // 正常运行点一行（含“创建正常运行点”按钮）的大小取决于有没有正常运行点：只量它的上边，它和它下面的行另行报告。
    measureArgument: (app) => app.memoryProtection.pointTitle,
    measure: (pointTitle) => {
      const main = document.querySelector(".app-shell");
      const rows = [...document.querySelectorAll(".memory-protection-settings > .settings-row")];
      const pointIndex = rows.findIndex((row) => row.querySelector(":scope > .settings-row-copy > strong")?.textContent === pointTitle);
      const top = (element) => element.getBoundingClientRect().top + main.scrollTop;
      const state = document.querySelector(".memory-protection-state");
      return {
        ...Object.fromEntries(rows.slice(0, pointIndex).filter((row) => row !== state).map((row, index) => [`row${index + 1}Top`, top(row)])),
        stateRowHeight: state ? state.getBoundingClientRect().height : null,
        pointRowTop: pointIndex >= 0 ? top(rows[pointIndex]) : null,
        report: {
          pointRowHeight: pointIndex >= 0 ? rows[pointIndex].getBoundingClientRect().height : null,
          nextRowTop: pointIndex >= 0 && rows[pointIndex + 1] ? top(rows[pointIndex + 1]) : null
        }
      };
    }
  },
  {
    name: "monitor-dashboard-pending",
    request: streamCarrying("/api/metrics/subscribe"),
    async open(page, baseUrl) {
      await page.goto(baseUrl, { waitUntil: "domcontentloaded" });
      await page.locator(".metric-card").first().waitFor();
    },
    pending: ".metric-value.pending",
    measure: () => ({
      dashboardHeight: document.querySelector(".dashboard").getBoundingClientRect().height,
      ...Object.fromEntries([...document.querySelectorAll(".metric-card")].map((card, index) => [`card${index + 1}Height`, card.getBoundingClientRect().height]))
    })
  },
  {
    name: "resource-table-pending",
    request: streamCarrying("/api/resource-monitor/subscribe"),
    async open(page, baseUrl) {
      await page.goto(baseUrl, { waitUntil: "domcontentloaded" });
      await page.locator(".resource-table-panel").scrollIntoViewIfNeeded();
    },
    pending: ".resource-table-panel > .content-state.waiting",
    measure: () => ({ tablePanelHeight: document.querySelector(".resource-table-panel").getBoundingClientRect().height })
  }
];

async function scenario(baseUrl, body) {
  const response = await fetch(`${baseUrl}/__test/scenario`, { method: "POST", headers: { "Content-Type": "application/json" }, body: JSON.stringify(body) });
  assert.equal(response.ok, true, `scenario rejected: ${JSON.stringify(body)}`);
}

/** 加载中：读屏能读到“正在加载…”（role=status），而且它看得见时占着位置——这里只量，不判断。 */
async function loadingAnnouncement(page, loading) {
  return page.evaluate(({ scope, text }) => [...document.querySelectorAll(`${scope} [role=status]`)]
    .some((element) => element.textContent.includes(text)), loading);
}

export default async function placeholderSizesJourney(page, baseUrl) {
  const errors = [];
  page.on("pageerror", (error) => errors.push(String(error)));
  await seedFrontendPreferences(page, (value) => {
    value.appearance.language = "zh-CN";
    value.appearance.animationStyle = "none";
  });
  const results = [];
  const mismatches = [];
  let first = true;
  for (const { language, width, height } of [
    { language: "zh-CN", width: 1280, height: 900 },
    { language: "en-US", width: 744, height: 520 }
  ]) {
    const app = await loadAppCopy(language);
    const settings = await loadSettingsText(language);
    for (const site of sites) {
      const label = `${site.name} ${language} ${width}`;
      if (site.scenario) await scenario(baseUrl, site.scenario);
      let release;
      const held = new Promise((resolve) => { release = resolve; });
      const hold = async (route) => {
        if (!site.request.holds(route.request())) return route.continue().catch(() => {});
        await held;
        await route.continue().catch(() => {});
      };
      await page.route(site.request.url, hold);
      await page.setViewportSize({ width, height });
      if (first) first = false;
      else await writeFrontendPreferences(page, (value) => { value.appearance.language = language; });
      await site.open(page, baseUrl, app, settings);

      if (site.loading) {
        const loading = site.loading(app);
        await page.waitForFunction(({ scope, text }) => [...document.querySelectorAll(`${scope} [role=status]`)]
          .some((element) => element.textContent.includes(text)), loading, { timeout: 8000 })
          .catch(() => assert.fail(`${label}: the loading state is not announced while the data is held`));
        assert.equal(await loadingAnnouncement(page, loading), true, `${label}: loading is announced`);
      } else {
        await page.locator(site.pending).first().waitFor({ state: "visible", timeout: 8000 })
          .catch(() => assert.fail(`${label}: no pending placeholder while the data is held`));
      }
      const measureArgument = site.measureArgument ? site.measureArgument(app) : null;
      const before = await page.evaluate(site.measure, measureArgument);

      release();
      await page.unroute(site.request.url, hold);
      if (site.loading) {
        const loading = site.loading(app);
        await page.waitForFunction(({ scope, text, loaded }) => ![...document.querySelectorAll(`${scope} [role=status]`)]
          .some((element) => element.textContent.includes(text)) && (!loaded || document.querySelector(loaded) !== null),
        { ...loading, loaded: site.loaded ?? null }, { timeout: 8000 })
          .catch(() => assert.fail(`${label}: the data does not arrive after release`));
      } else {
        await page.waitForFunction((selector) => document.querySelector(selector) === null, site.pending, { timeout: 8000 })
          .catch(() => assert.fail(`${label}: the placeholder is not replaced after release`));
      }
      await page.evaluate(() => new Promise((resolve) => requestAnimationFrame(() => requestAnimationFrame(resolve))));
      const after = await page.evaluate(site.measure, measureArgument);

      const { report: reportBefore, ...checkedBefore } = before;
      const { report: reportAfter, ...checkedAfter } = after;
      for (const [key, value] of Object.entries(checkedBefore)) {
        const next = checkedAfter[key];
        if (value === null || next === null || next === undefined || Math.abs(next - value) > 0.5) {
          mismatches.push(`${label}: ${key} ${value} -> ${next}`);
        }
      }
      for (const key of Object.keys(checkedAfter)) {
        if (!(key in checkedBefore)) mismatches.push(`${label}: ${key} appears only after load (${checkedAfter[key]})`);
      }
      results.push({ site: site.name, language, width, before, after, ...(reportBefore ? { reported: { before: reportBefore, after: reportAfter } } : {}) });
      if (site.scenario) await fetch(`${baseUrl}/__test/scenario/reset`, { method: "POST" });
    }
  }
  console.log(JSON.stringify({ placeholderSizes: results }, null, 1));
  assert.deepEqual(mismatches, [], `placeholders change size when the data arrives ${JSON.stringify(results)}`);
  assert.deepEqual(errors, [], "no page errors");
  return { results };
}
