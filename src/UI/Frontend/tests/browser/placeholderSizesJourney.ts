import type { Request, Route } from "playwright-core";
import type { AppCopy } from "../../src/i18n/copy/index.ts";
type SettingsCopy = Awaited<ReturnType<typeof loadSettingsText>>;
type LoadingAnnouncement = { scope: string; text: string };
type Measurement = { [key: string]: number | null | Record<string, number> | undefined; report?: Record<string, number> };
interface PlaceholderSite {
  name: string;
  scenario?: Record<string, unknown>;
  request: { url: (url: URL) => boolean; holds: (request: Request) => boolean };
  open(page: Page, baseUrl: string, app: AppCopy, settings: SettingsCopy): Promise<void>;
  loading?: (app: AppCopy) => LoadingAnnouncement;
  pending?: string;
  loaded?: string;
  measureArgument?: (app: AppCopy) => string;
  measure: (argument: string | null) => Measurement;
  check?: (before: Measurement, after: Measurement, label: string) => void;
  close?: (page: Page, app: AppCopy) => Promise<void>;
  cleanup?: (baseUrl: string) => Promise<void>;
}
import type { Page } from "playwright-core";
import assert from "node:assert/strict";
import { loadAppCopy } from "../../src/i18n/copy/appCopyLoader.ts";
import { loadSettingsText } from "../../src/i18n/settingsLoader.ts";
import { seedFrontendPreferences, writeFrontendPreferences } from "./frontendPreferencesFixture.ts";

/*
 * 加载占位和加载后的内容占一样大的位置（用户 10-27：「让占位和加载后状态的逻辑占位大小相等」）：
 * 第一份数据到的时候，占位下面的东西不动。资源条区由 resource-bar-placeholder 旅程负责，这里是其余的占位：
 * - 页面顶部的运行能力提示、性能优化页的调度状态提示、组件页的操作状态和软件登记补充项提示：
 *   加载完成后这里什么也没有，所以加载中只读给读屏，不占位置，下面的内容不被推动；
 * - 主机内存保护分区：状态到之前，生效状态一行、阈值一行和正常运行点一行的读数按有数据时的结构各占位置（读数写“--”），
 *   正在读取照旧读给读屏；没有正常运行点时那句话叠在三项结构的同一格里，有没有运行点一样高；
 * - 监视页在看板布局读回之前不摆卡片、资源条和资源表三个区，工具栏不动，三个区随布局一起出现；
 * - 磁盘占用页的磁盘列表读回之前占一张卷卡片；资源条展开的进程面板在明细到之前是一条空轨道；
 * - 组件获取对话框读版本时，版本选择的图例和固定两项先占位；
 * - 监视页的卡片（读数到之前画一条占位）和资源表（加载中的框和表格一样高）：量出来本来就相等，钉住。
 * 每一处都先拦住带数据的那条请求，量完占位再放行，所以“还没到”是确定的，不靠时间。
 * 宽窗口和最小窗口、中英两种语言各走一遍；量到的前后数值都放在结果里。
 */

const streamUrl = (url: URL) => url.pathname === "/api/subscriptions/stream";
const streamCarrying = (fragment: string) => ({
  url: streamUrl,
  holds: (request: Request) => request.method() === "POST" && (request.postData() ?? "").includes(fragment)
});
const getRequest = (pathname: string) => ({ url: (url: URL) => url.pathname === pathname, holds: (request: Request) => request.method() === "GET" });

const sites: PlaceholderSite[] = [
  {
    name: "runtime-capability-notice",
    request: getRequest("/api/runtime/capabilities"),
    async open(page, baseUrl) {
      await page.goto(baseUrl, { waitUntil: "domcontentloaded" });
      await page.locator("#monitorPage").waitFor();
    },
    loading: (app) => ({ scope: ".app-shell", text: app.observationNotice.loading(app.shell.runtimeCapability) }),
    measure: () => {
      const main = document.querySelector<HTMLElement>(".app-shell")!;
      const stage = document.querySelector<HTMLElement>("#pageStage")!;
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
      const main = document.querySelector<HTMLElement>(".app-shell")!;
      return { modeBarTop: document.querySelector<HTMLElement>(".optimization-modebar")!.getBoundingClientRect().top + main.scrollTop };
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
      const main = document.querySelector<HTMLElement>(".app-shell")!;
      return { listTop: document.querySelector<HTMLElement>("#componentsPage .management-list")!.getBoundingClientRect().top + main.scrollTop };
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
      const main = document.querySelector<HTMLElement>(".app-shell")!;
      return { listTop: document.querySelector<HTMLElement>("#componentsPage .management-list")!.getBoundingClientRect().top + main.scrollTop };
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
    measureArgument: (app) => app.memoryProtection.pointTitle,
    measure: measureMemoryProtectionRows
  },
  {
    // 没有正常运行点时，那句话叠在三项结构的同一格里，和读取中的占位一样高。
    name: "memory-protection-no-point",
    scenario: { memoryProtection: "no-point" },
    request: streamCarrying("/api/memory-protection/subscribe"),
    async open(page, baseUrl, app, settings) {
      await page.goto(baseUrl, { waitUntil: "domcontentloaded" });
      await page.getByRole("navigation").getByRole("button", { name: app.page.settings, exact: true }).click();
      await page.getByRole("button", { name: settings.sections.memoryProtection, exact: true }).click();
      await page.getByText(app.memoryProtection.intro, { exact: true }).waitFor();
    },
    loading: (app) => ({ scope: ".memory-protection-settings", text: app.memoryProtection.statusLoading }),
    loaded: ".memory-protection-point-none",
    measureArgument: (app) => app.memoryProtection.pointTitle,
    measure: measureMemoryProtectionRows,
    // 把运行点建回来，后面的站点看到的仍是夹具起始的状态。
    async cleanup(baseUrl) {
      const response = await fetch(`${baseUrl}/api/memory-protection/normal-running-point`, { method: "POST" });
      assert.equal(response.ok, true, "the normal running point is created again after the no-point site");
    }
  },
  {
    // 看板布局（卡片、资源条、表列）读回之前下面三个区都不摆：已经显示的工具栏不动，三个区在布局到时一起出现。
    name: "monitor-before-layout",
    request: getRequest("/api/metrics/catalog"),
    async open(page, baseUrl) {
      await page.goto(baseUrl, { waitUntil: "domcontentloaded" });
      await page.locator("#monitorPage .monitor-toolbar").waitFor();
    },
    loading: (app) => ({ scope: "#monitorPage", text: app.observationNotice.loading(app.monitorPage.liveMetrics) }),
    loaded: "#monitorPage .resource-table-panel",
    measure: () => {
      const main = document.querySelector<HTMLElement>(".app-shell")!;
      const toolbar = document.querySelector<HTMLElement>("#monitorPage .monitor-toolbar")!.getBoundingClientRect();
      return {
        toolbarTop: toolbar.top + main.scrollTop,
        toolbarHeight: toolbar.height,
        report: { regions: document.querySelectorAll<HTMLElement>("#monitorPage [data-frontend-visibility-surface]").length }
      };
    },
    // 布局到之前一个区也不在：没有先摆出来、后来又被推开的东西。
    check: (before, after, label) => {
      assert.equal(before.report!.regions, 0, `${label}: no monitor region is shown before the layout`);
      assert.ok(after.report!.regions >= 3, `${label}: the monitor regions appear with the layout`);
    }
  },
  {
    // 磁盘列表读回之前占一张卷卡片：一行放得下的盘数以内，下面的扫描按钮不动（夹具一块盘）。
    name: "disk-usage-volumes",
    request: getRequest("/api/disk-usage/volumes"),
    async open(page, baseUrl, app) {
      await page.goto(baseUrl, { waitUntil: "domcontentloaded" });
      await page.getByRole("navigation").getByRole("button", { name: app.page.diskUsage, exact: true }).click();
      await page.locator(".disk-usage-volumes").waitFor();
    },
    loading: (app) => ({ scope: ".disk-usage-volumes", text: app.observationNotice.loading(app.diskUsage.volumeColumnLabel) }),
    loaded: ".disk-usage-volume:not(.pending)",
    measure: () => {
      const main = document.querySelector<HTMLElement>(".app-shell")!;
      return {
        volumeListHeight: document.querySelector<HTMLElement>(".disk-usage-volume-list")!.getBoundingClientRect().height,
        volumeCardHeight: document.querySelector<HTMLElement>(".disk-usage-volume")!.getBoundingClientRect().height,
        scanActionsTop: document.querySelector<HTMLElement>(".disk-usage-setup-actions")!.getBoundingClientRect().top + main.scrollTop
      };
    }
  },
  {
    // 进程明细到之前，展开的面板里是一条空轨道（高度固定），面板和下面的资源表不动。
    name: "resource-process-panel",
    request: streamCarrying("scope=details"),
    async open(page, baseUrl) {
      await page.goto(baseUrl, { waitUntil: "domcontentloaded" });
      const track = page.locator(".resource-bar-track[data-metric-id]").first();
      await track.waitFor({ state: "visible", timeout: 8000 });
      await track.click({ position: { x: 12, y: 12 } });
      await page.locator(".resource-process-panel").waitFor();
    },
    pending: ".resource-process-track.pending",
    measure: () => {
      const main = document.querySelector<HTMLElement>(".app-shell")!;
      return {
        panelHeight: document.querySelector<HTMLElement>(".resource-process-panel")!.getBoundingClientRect().height,
        trackHeight: document.querySelector<HTMLElement>(".resource-process-track")!.getBoundingClientRect().height,
        tableTop: document.querySelector<HTMLElement>(".resource-table-panel")!.getBoundingClientRect().top + main.scrollTop
      };
    }
  },
  {
    // 组件获取对话框读版本时：版本选择的图例和固定两项先占位；已验证一项、不能选的原因和各系列取决于数据，
    // 到时接在固定项前后。对话框在可用区域里居中，这部分长出来时对话框上边随之上移半个增量：只报告版本区高度和对话框上边。
    name: "component-acquisition-versions",
    scenario: { runtimeEffectOwners: true },
    request: getRequest("/api/components/hardware-bridge/versions"),
    async open(page, baseUrl, app) {
      await page.goto(baseUrl, { waitUntil: "domcontentloaded" });
      await page.getByRole("navigation").getByRole("button", { name: app.page.components, exact: true }).click();
      await page.locator("#managementTab-Dependency").click();
      const bridge = page.locator(".component-item:not([data-exit-ghost])").filter({ hasText: app.status.component.names.hardwareBridge });
      await bridge.getByRole("button", { name: app.management.downloadAndInstall, exact: true }).click();
      await page.locator(".component-acquisition-versions").waitFor();
    },
    loading: (app) => ({ scope: ".component-acquisition-versions", text: app.componentAcquisition.loadingVersions }),
    loaded: ".component-acquisition-versions .version-selector:not([aria-hidden])",
    measure: () => {
      const versions = document.querySelector<HTMLElement>(".component-acquisition-versions")!;
      const dialog = versions.closest("[role=dialog]")!.getBoundingClientRect();
      return {
        legendTop: versions.querySelector<HTMLElement>("legend")!.getBoundingClientRect().top - dialog.top,
        report: { versionsHeight: versions.getBoundingClientRect().height, dialogTop: dialog.top }
      };
    },
    async close(page, app) {
      await page.getByRole("dialog").getByRole("button", { name: app.feedback.cancel, exact: true }).click();
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
      dashboardHeight: document.querySelector<HTMLElement>(".dashboard")!.getBoundingClientRect().height,
      ...Object.fromEntries([...document.querySelectorAll<HTMLElement>(".metric-card")].map((card, index) => [`card${index + 1}Height`, card.getBoundingClientRect().height]))
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
    measure: () => ({ tablePanelHeight: document.querySelector<HTMLElement>(".resource-table-panel")!.getBoundingClientRect().height })
  }
];

// 内存保护分区各行的上边（到正常运行点下面一行为止）、生效状态一行和正常运行点一行的高。
function measureMemoryProtectionRows(pointTitle: string | null) {
  const main = document.querySelector<HTMLElement>(".app-shell")!;
  const rows = [...document.querySelectorAll<HTMLElement>(".memory-protection-settings > .settings-row")];
  const pointIndex = rows.findIndex((row) => row.querySelector<HTMLElement>(":scope > .settings-row-copy > strong")!?.textContent === pointTitle);
  const top = (element: Element) => element.getBoundingClientRect().top + main.scrollTop;
  const state = document.querySelector<HTMLElement>(".memory-protection-state")!;
  return {
    ...Object.fromEntries(rows.slice(0, pointIndex + 2).filter((row) => row !== state).map((row, index) => [`row${index + 1}Top`, top(row)])),
    stateRowHeight: state ? state.getBoundingClientRect().height : null,
    pointRowHeight: pointIndex >= 0 ? rows[pointIndex].getBoundingClientRect().height : null
  };
}

async function scenario(baseUrl: string, body: Record<string, unknown>) {
  const response = await fetch(`${baseUrl}/__test/scenario`, { method: "POST", headers: { "Content-Type": "application/json" }, body: JSON.stringify(body) });
  assert.equal(response.ok, true, `scenario rejected: ${JSON.stringify(body)}`);
}

/** 加载中：读屏能读到“正在加载…”（role=status），而且它看得见时占着位置——这里只量，不判断。 */
async function loadingAnnouncement(page: Page, loading: LoadingAnnouncement) {
  return page.evaluate(({ scope, text }) => [...document.querySelectorAll<HTMLElement>(`${scope} [role=status]`)]
    .some((element) => element.textContent!.includes(text)), loading);
}

export default async function placeholderSizesJourney(page: Page, baseUrl: string) {
  const errors: string[] = [];
  page.on("pageerror", (error) => errors.push(String(error)));
  await seedFrontendPreferences(page, (value) => {
    value.appearance.language = "zh-CN";
    value.appearance.animationStyle = "none";
  });
  const results = [];
  const mismatches: string[] = [];
  let first = true;
  for (const { language, width, height } of [
    { language: "zh-CN", width: 1280, height: 900 },
    { language: "en-US", width: 744, height: 520 }
  ] as const) {
    const app = await loadAppCopy(language);
    const settings = await loadSettingsText(language);
    for (const site of sites) {
      const label = `${site.name} ${language} ${width}`;
      if (site.scenario) await scenario(baseUrl, site.scenario);
      let release!: () => void;
      const held = new Promise<void>((resolve) => { release = resolve; });
      const hold = async (route: Route) => {
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
        await page.waitForFunction(({ scope, text }) => [...document.querySelectorAll<HTMLElement>(`${scope} [role=status]`)]
          .some((element) => element.textContent!.includes(text)), loading, { timeout: 8000 })
          .catch(() => assert.fail(`${label}: the loading state is not announced while the data is held`));
        assert.equal(await loadingAnnouncement(page, loading), true, `${label}: loading is announced`);
      } else {
        await page.locator(site.pending!).first().waitFor({ state: "visible", timeout: 8000 })
          .catch(() => assert.fail(`${label}: no pending placeholder while the data is held`));
      }
      const measureArgument = site.measureArgument ? site.measureArgument(app) : null;
      const before = await page.evaluate(site.measure, measureArgument);

      release();
      await page.unroute(site.request.url, hold);
      if (site.loading) {
        const loading = site.loading(app);
        await page.waitForFunction(({ scope, text, loaded }) => ![...document.querySelectorAll<HTMLElement>(`${scope} [role=status]`)]
          .some((element) => element.textContent!.includes(text)) && (!loaded || document.querySelector<HTMLElement>(loaded)! !== null),
        { ...loading, loaded: site.loaded ?? null }, { timeout: 8000 })
          .catch(() => assert.fail(`${label}: the data does not arrive after release`));
      } else {
        await page.waitForFunction((selector) => document.querySelector<HTMLElement>(selector)! === null, site.pending!, { timeout: 8000 })
          .catch(() => assert.fail(`${label}: the placeholder is not replaced after release`));
      }
      await page.evaluate(() => new Promise((resolve) => requestAnimationFrame(() => requestAnimationFrame(resolve))));
      const after = await page.evaluate(site.measure, measureArgument);

      const { report: reportBefore, ...checkedBefore } = before;
      const { report: reportAfter, ...checkedAfter } = after;
      for (const [key, value] of Object.entries(checkedBefore) as [string, number | null][]) {
        const next = checkedAfter[key] as number | null | undefined;
        if (value === null || next === null || next === undefined || Math.abs(next - value) > 0.5) {
          mismatches.push(`${label}: ${key} ${value} -> ${next}`);
        }
      }
      for (const key of Object.keys(checkedAfter)) {
        if (!(key in checkedBefore)) mismatches.push(`${label}: ${key} appears only after load (${checkedAfter[key]})`);
      }
      if (site.check) site.check(before, after, label);
      results.push({ site: site.name, language, width, before, after, ...(reportBefore ? { reported: { before: reportBefore, after: reportAfter } } : {}) });
      if (site.close) await site.close(page, app);
      if (site.scenario) await fetch(`${baseUrl}/__test/scenario/reset`, { method: "POST" });
      if (site.cleanup) await site.cleanup(baseUrl);
    }
  }
  console.log(JSON.stringify({ placeholderSizes: results }, null, 1));
  assert.deepEqual(mismatches, [], `placeholders change size when the data arrives ${JSON.stringify(results)}`);
  assert.deepEqual(errors, [], "no page errors");
  return { results };
}
