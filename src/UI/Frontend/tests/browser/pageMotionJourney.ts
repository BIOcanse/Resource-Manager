import type { SettingsTextBundle } from "../../src/i18n/settingsTypes.ts";
import type { AnimationStyleId } from "../../src/presentation/animationStyle.ts";
export interface ProbePage { evaluate<R>(callback: (arg: boolean) => R | Promise<R>, arg: boolean): Promise<R> }
type LongTask = { start: number; duration: number };
type LongAnimationFrame = { startTime: number; duration: number; [key: string]: unknown };
type IndicatorOffset = { dx: number; dy: number; dw: number; dh: number; opacity: number; visible: boolean };
type Translation = { x: number; y: number; transform: string };
export interface MotionSnapshot {
 t: number; bar: { node: number | null; opacity: number; transform: string };
 group: { page: string | null | undefined; node: number | null; tabs: number; opacity: number; transform: string; animations: number };
 stage: Translation & { opacity: number }; leaving: number[]; leavingX: number[];
 primaryIndicator: IndicatorOffset | null; subnavIndicator: IndicatorOffset | null; dialog: number | null;
}
type TimingSnapshot = { t: number };
type ProbeFrame = (MotionSnapshot | TimingSnapshot) & { dt: number };
type ProbeRun = { t0: number | null; t1?: number; frames: ProbeFrame[]; rafGaps: number[]; done: boolean };
export interface PageMotionProbe {
 captureGeometry: boolean; snapshot(): MotionSnapshot | TimingSnapshot;
 indicatorOffset(list: Element | null): IndicatorOffset | null; observer: PerformanceObserver;
 recordTasks(entries: PerformanceEntry[]): void; loafObserver: PerformanceObserver | null; recordLoafs(entries: PerformanceEntry[]): void;
}
export type PageMotionProbeWindow = typeof window & { __pm: PageMotionProbe; __pmRun: ProbeRun; __pmLongTasks: LongTask[]; __pmLoafs: LongAnimationFrame[] };
declare global { interface Window { __pm: PageMotionProbe; __pmRun: ProbeRun; __pmLongTasks: LongTask[]; __pmLoafs: LongAnimationFrame[] } }
type GeometryRun = { t0: number; t1: number; frames: (MotionSnapshot & { dt: number })[]; rafGaps: number[]; done: boolean; longAnimationFrames: LongAnimationFrame[]; longTasks: LongTask[] };
type ContentSummary = ReturnType<typeof summarize> & { opacities?: number[] };
type InteractionRun = { mode: string; step: string; start: number; end: number; longTasks: LongTask[]; frameTimes: number[]; firstFrameMs: number; rafGaps: number[]; longAnimationFrames: LongAnimationFrame[]; frameGaps: number[]; overruns: ReturnType<typeof assessInteractionLongTasks>; cpuProfile: Awaited<ReturnType<typeof stopMotionProfile>> | undefined };
import type { Page, Locator } from "playwright-core";
import type { AppCopy } from "../../src/i18n/copy/index.ts";
import { loadAppCopy } from "../../src/i18n/copy/appCopyLoader.ts";
import { loadSettingsText } from "../../src/i18n/settingsLoader.ts";
import { writeFileSync } from "node:fs";
import { resolve } from "node:path";
import { assessInteractionLongTasks } from "./interactionLongTaskBudget.ts";
import { startMotionProfile, stopMotionProfile } from "./pageMotionEvidence.ts";
import { TERMINAL_STEPS } from "../../src/presentation/animationStyle.ts";

const SWITCH_SAMPLE_MS = 700;

/**
 * 换页动效和分页树（docs/navigation_tree_2026-10-25.md、docs/page_motion_2026-10-25.md），简体中文，真实点击。
 * 用户 10-25：「优化切换分页的动效……不要重复一遍当前所在的分页；高度不固定，手感差」；
 * 「二级分页弹出延迟大；磁盘占用分页的弹出和其他分页不统一」；「每次都是弹出，哪怕刚切换出去」。
 *
 * - 分页树：一级页签和二级分页的名字都是树里的名字；顶栏不写当前页名；页面上没有和选中分页同名的可见标题。
 * - 二级分页条常驻：点下去之后的第一帧新的那组分页就在；A→B→A 快速往返，条和每组分页的节点都不换，组不进场（不动、不淡入）。
 * - 每一页的内容从同一个 y 开始，磁盘占用、控制面、设置也一样。
 * - 换页、换二级分页（docs/animation_style_2026-10-26.md「总原则」）：新内容从第一帧起就在终态几何上（没有位移，命中区域不动），
 *   只有不透明度由呈现层带进来；旧内容的幽灵（[data-exit-ghost]，不可命中）淡出，并往和新内容相反的方向退（往右的分页，旧内容往左退），
 *   结束后幽灵撤掉、内容不留样式。
 * - 指示条停下来时和选中项重合：一级页签、二级分页、任务中心的分页、分段选择。
 * - 任务中心：换分页时高度一帧都不变。
 * - 无动画、超高性能（设置里选）：第一帧就是终态，没有副本、没有位移，指示条已经到位。
 * - 每种会动的风格各跑一遍（docs/animation_style_2026-10-26.md）：page-motion 是流畅平滑，page-motion-desktop、page-motion-terminal 是另外两种；
 *   终端顿挫的进出按固定步数跳（新内容的不透明度最多 TERMINAL_STEPS + 1 个不同的值），幽灵不晚于新内容走完。
 * - 鼠标点完没有焦点环（:focus-visible 不成立）；按 Tab 进去有。
 * - 预热后按点击窗口归因长任务；超过 50 ms 必须匹配具名动效登记和容差；正常动画、无动画走同一串切换，动画不引起重连。
 * 帧都在页面里用 requestAnimationFrame 逐帧读（无头浏览器不节流）。
 */
export function pageMotionJourneyIn(style: AnimationStyleId) {
  return (page: Page, baseUrl: string) => pageMotionJourney(page, baseUrl, style);
}
export default pageMotionJourneyIn("smooth");

async function pageMotionJourney(page: Page, baseUrl: string, style: AnimationStyleId) {
  const startedOn = new Date().toISOString();
  const failures: string[] = [];
  const fail = (message: string) => failures.push(message);
  page.on("pageerror", (error) => { failures.push(`pageerror: ${String(error)}`); process.stderr.write(`PAGE_ERROR ${String(error)}\n`); });
  const app = await loadAppCopy("zh-CN");
  const settings = await loadSettingsText("zh-CN");
  let streamOpens = 0;
  page.on("request", (request) => {
    if (new URL(request.url()).pathname === "/api/subscriptions/stream") streamOpens += 1;
  });

  await page.setViewportSize({ width: 1280, height: 800 });
  await page.goto(baseUrl, { waitUntil: "domcontentloaded" });
  const nav = page.getByRole("navigation", { name: app.shell.pageNav, exact: true });
  await nav.waitFor();
  await page.waitForTimeout(1200);
  await setAnimationStyle(page, app, settings, style);
  await installProbe(page);
  const primary = (id: keyof AppCopy["page"]) => nav.getByRole("button", { name: app.page[id], exact: true });
  const groupOf = (id: string) => page.locator(`.page-subnav-group[data-page-id="${id}"]`);
  const settle = () => page.waitForTimeout(700);
  const interactionRuns: InteractionRun[] = [];
  const evidenceDirectory = process.env.RM_PAGE_MOTION_EVIDENCE_DIR;
  let animationMode: AnimationStyleId = style;
  const sampleSwitch = async (step: string, locator: Locator, durationMs: number) => {
    const index = interactionRuns.length;
    const profile = evidenceDirectory
      ? await startMotionProfile(page, evidenceDirectory, `${String(index).padStart(2, "0")}-${animationMode}`) : null;
    let run: GeometryRun | undefined;
    let cpuProfile: Awaited<ReturnType<typeof stopMotionProfile>> | undefined;
    try {
      run = await sampleClick(page, locator, durationMs) as GeometryRun;
    } finally {
      if (profile) cpuProfile = await stopMotionProfile(profile, run?.longTasks ?? []);
    }
    const window = { step, start: run.t0, end: run.t1 };
    interactionRuns.push({ mode: animationMode, ...window, longTasks: run.longTasks,
      frameTimes: run.frames.map((frame) => frame.t), firstFrameMs: run.frames[0].dt,
      rafGaps: run.rafGaps, longAnimationFrames: run.longAnimationFrames,
      frameGaps: run.frames.map((frame, index) => frame.t - (index ? run.frames[index - 1].t : run.t0)),
      overruns: assessInteractionLongTasks(run.longTasks, [window]), cpuProfile });
    return run;
  };

  // ---- 分页树：名字 ----
  const primaryOrder = ["monitor", "components", "optimization", "diskUsage", "control", "details", "settings"] as const;
  const primaryLabels = (await nav.locator(".shell-page").allTextContents()).map((text) => text.trim());
  const expectedPrimary = primaryOrder.map((id) => app.page[id]).filter((name) => name !== app.page.optimization || primaryLabels.includes(name));
  if (JSON.stringify(primaryLabels) !== JSON.stringify(expectedPrimary)) fail(`primary tabs ${JSON.stringify(primaryLabels)} are not the tree ${JSON.stringify(expectedPrimary)}`);
  const visibleIds = primaryOrder.filter((id) => primaryLabels.includes(app.page[id]));

  // 预热：每页走一遍（载入页面的代码块不算换页动效）。
  for (const id of visibleIds) {
    await primary(id).click();
    await settle();
  }
  await page.evaluate(() => { window.__pmLongTasks.length = 0; });

  // ---- 每一页：顶栏、同名标题、内容起点、分页条在 ----
  // 归别的会话改的同名标题：设备拓扑视图自己的可见标题（features/deviceTopology/DeviceTopologyView.tsx，frontend-topology-identity），
  // 已经转告；不算失败，记在结果的 repeatsOwnedElsewhere 里。那边改掉之后删掉这一项。
  const ownedElsewhere = [{ page: "details", name: app.misc.detailsTab.device, owner: "frontend-topology-identity" }];
  const repeatsOwnedElsewhere: typeof ownedElsewhere = [];
  const pageFacts: { id: keyof AppCopy["page"]; dragRegion: string | null; headings: string[]; tabName: string | null; tabBaseName: string | null; contentTop: number; barHeight: number; pageName: string }[] = [];
  for (const id of visibleIds) {
    await primary(id).click();
    await settle();
    const facts = await page.evaluate((pageName) => {
      const visible = (element: Element) => {
        const rect = element.getBoundingClientRect();
        return rect.width > 1 && rect.height > 1 && !element.closest<HTMLElement>("[inert], [hidden], [aria-hidden='true']")!;
      };
      const selectedTab = document.querySelector<HTMLElement>(".page-subnav-group:not([hidden]) [role='tab'][aria-selected='true']");
      const tabName = selectedTab ? selectedTab.textContent!.trim() : null;
      const tabBaseName = selectedTab ? tabName!.replace(/\s+\d+$/, "") : null;
      const headings = [...document.querySelectorAll<HTMLElement>("main h1, main h2, main h3, main [role='heading']")]
        .filter(visible).map((element) => element.textContent!.trim());
      const stage = document.querySelector<HTMLElement>("#pageStage")!;
      const bar = document.querySelector<HTMLElement>("#pageSubnav")!.getBoundingClientRect();
      return {
        dragRegion: document.querySelector<HTMLElement>(".window-drag-region")!.textContent,
        headings,
        tabName,
        tabBaseName,
        contentTop: Math.round(stage.firstElementChild!.getBoundingClientRect().top * 10) / 10,
        barHeight: bar.height,
        pageName
      };
    }, app.page[id]);
    pageFacts.push({ id, ...facts });
    if (facts.dragRegion!.includes(app.page[id])) fail(`${id}: the top bar repeats the page name: ${facts.dragRegion}`);
    for (const name of [app.page[id], facts.tabBaseName].filter(Boolean) as string[]) {
      if (!facts.headings.includes(name)) continue;
      const owner = ownedElsewhere.find((entry) => entry.page === id && entry.name === name);
      if (owner) repeatsOwnedElsewhere.push(owner);
      else fail(`${id}: a visible heading repeats the current page ${name}: ${JSON.stringify(facts.headings)}`);
    }
    if (!(facts.barHeight > 20)) fail(`${id}: the secondary bar is not there (${facts.barHeight})`);
  }
  const tops = new Set(pageFacts.map((facts) => facts.contentTop));
  if (tops.size !== 1) fail(`pages start at different y: ${JSON.stringify(pageFacts.map(({ id, contentTop }) => [id, contentTop]))}`);

  // 二级分页的名字也是树里的名字。
  await primary("details").click();
  const detailsTabs = (await groupOf("details").getByRole("tab").allTextContents()).map((text) => text.trim());
  const expectedDetails = [app.misc.detailsTab.device, app.misc.detailsTab.gpu, app.misc.detailsTab.cpu, app.misc.detailsTab.report, app.targetedReport.tab, app.kernelLeakReport.tab, app.memoryProtection.reportTab];
  if (detailsTabs.some((name) => !expectedDetails.includes(name)) || detailsTabs.slice(0, 3).join() !== expectedDetails.slice(0, 3).join()) {
    fail(`details tabs ${JSON.stringify(detailsTabs)} are not the tree ${JSON.stringify(expectedDetails)}`);
  }
  // 组件与软件：每一类是“名字 数量”，最后是浏览器运行时（没有数量）。
  await primary("components").click();
  const componentsTabs = (await groupOf("components").getByRole("tab").allTextContents()).map((text) => text.trim());
  if (componentsTabs.at(-1) !== app.managementPage.browserRuntimeTab || componentsTabs.slice(0, -1).some((name) => !/\S\s\d+$/.test(name))) {
    fail(`components tabs are not the tree's kinds with counts: ${JSON.stringify(componentsTabs)}`);
  }
  await settle();

  // ---- 二级分页条：第一帧就在、A→B→A 节点不换、组不进场 ----
  const barRuns: { id: string; run: GeometryRun }[] = [];
  let barSource = "components";
  for (const id of ["details", "components", "details", "monitor", "details", "diskUsage", "components"] as const) {
    const run = await sampleSwitch(`primary ${barSource}→${id}`, primary(id), 500);
    barSource = id;
    barRuns.push({ id, run });
    const first = run.frames[0];
    const target = id === "monitor" || id === "diskUsage" ? null : id;
    if ((first.group.page ?? null) !== target) fail(`${id}: first frame shows the group of ${first.group.page}`);
    if (target && !(first.group.tabs > 0)) fail(`${id}: first frame has no secondary tabs`);
    for (const frame of run.frames) {
      if (frame.bar.opacity !== 1 || frame.bar.transform !== "none" || frame.group.opacity !== 1 || frame.group.transform !== "none" || frame.group.animations > 0) {
        fail(`${id}: the secondary bar moves or fades at ${Math.round(frame.dt)} ms: ${JSON.stringify({ bar: frame.bar, group: frame.group })}`);
        break;
      }
    }
  }
  const nodeIds = (id: string) => [...new Set(barRuns.filter((entry) => entry.id === id).map((entry) => entry.run.frames[0].group.node))];
  if (nodeIds("details").length !== 1 || nodeIds("components").length !== 1) fail(`secondary groups were rebuilt: details ${nodeIds("details")}, components ${nodeIds("components")}`);
  if (new Set(barRuns.map((entry) => entry.run.frames[0].bar.node)).size !== 1) fail("the secondary bar was rebuilt");
  const barAppearMs = barRuns.map(({ id, run }) => ({ id, ms: Math.round(run.frames[0].dt * 10) / 10 }));

  // 快速往返：A→B→A 连着点，结束后只剩一份页面，副本清空。
  await settle();
  await primary("details").click();
  await primary("components").click();
  await primary("details").click();
  await settle();
  const after = await page.evaluate(() => ({ ...window.__pm.snapshot() as MotionSnapshot, pages: document.querySelectorAll<HTMLElement>("#detailsPage").length }));
  if (after.group.node !== barRuns.find((entry) => entry.id === "details")!.run.frames[0].group.node) fail("details group rebuilt after a quick round trip");
  if (after.leaving.length !== 0 || after.pages !== 1 || after.stage.x !== 0 || after.stage.opacity !== 1) fail(`quick round trip left copies or transforms: ${JSON.stringify(after)}`);

  // ---- 内容进出：次序和方向 ----
  const contentRuns: Record<string, ContentSummary> = {};
  const checkContent = (label: string, run: GeometryRun, direction: number) => {
    contentRuns[label] = summarize(run);
    const first = run.frames[0];
    if (!(first.leaving.length >= 1)) fail(`${label}: no exit ghost in the first frame`);
    // 命中几何从第一帧起就是终态：新内容没有位移，只有不透明度在变。
    const moved = run.frames.filter((frame) => frame.stage.transform !== "none");
    if (moved.length > 0) fail(`${label}: the entering content moves (its hit area would move with it): ${JSON.stringify(moved[0].stage)}`);
    // 方向在幽灵上：旧内容往和新内容相反的方向退。
    const ghostShift = run.frames.map((frame) => frame.leavingX[0]).filter((x) => x !== undefined && Math.abs(x) > 0.05).at(-1);
    if (ghostShift === undefined || Math.sign(ghostShift) !== -direction) fail(`${label}: the exit ghost leaves towards ${ghostShift} (expected sign ${-direction})`);
    if (style === "terminal") {
      // 按步跳：不透明度只取几个值，单调走向 1。
      const opacities = run.frames.map((frame) => frame.stage.opacity);
      const distinct = [...new Set(opacities)];
      if (distinct.length < 3 || distinct.length > TERMINAL_STEPS + 1) fail(`${label}: terminal content takes ${distinct.length} looks, not up to ${TERMINAL_STEPS} steps: ${JSON.stringify(distinct)}`);
      if (opacities.some((value, index) => index > 0 && value < opacities[index - 1] - 0.001)) fail(`${label}: terminal content fades back: ${JSON.stringify(distinct)}`);
      contentRuns[label].opacities = distinct;
    }
    let lastLeaving = Infinity;
    let leavingGoneAt: number | null = null;
    let settledAt: number | null = null;
    for (const frame of run.frames) {
      const opacity = frame.leaving.length ? Math.max(...frame.leaving) : 0;
      if (opacity > lastLeaving + 0.02) { fail(`${label}: the leaving copy brightens again at ${Math.round(frame.dt)} ms`); break; }
      lastLeaving = opacity;
      if (leavingGoneAt === null && opacity < 0.05) leavingGoneAt = frame.dt;
      if (settledAt === null && frame.stage.opacity === 1) settledAt = frame.dt;
    }
    // 退出看得见（不是一两帧就没了），并且在新内容到位之前走完：降到 5% 以下的时刻在进来用时的 25%～75% 之间；终端顿挫不晚于新内容。
    const exitEarly = style === "terminal" ? leavingGoneAt !== null && leavingGoneAt <= settledAt! + 1
      : leavingGoneAt !== null && leavingGoneAt >= settledAt! * 0.25 && leavingGoneAt <= settledAt! * 0.75;
    if (settledAt === null || !exitEarly) {
      fail(`${label}: the exit fades out at ${leavingGoneAt} ms, the enter settles at ${settledAt} ms`);
    }
    const last = run.frames.at(-1)!;
    if (last.leaving.length !== 0 || last.stage.transform !== "none" || last.stage.opacity !== 1) fail(`${label}: ghosts or styles stay after settling: ${JSON.stringify(last)}`);
  };
  await primary("components").click();
  await settle();
  checkContent("primary components→details", await sampleSwitch("primary components→details", primary("details"), 700), 1);
  await settle();
  const detailsGroup = groupOf("details");
  await detailsGroup.getByRole("tab", { name: app.misc.detailsTab.device, exact: true }).click();
  await settle();
  checkContent("details device→gpu", await sampleSwitch("details device→gpu", detailsGroup.getByRole("tab", { name: app.misc.detailsTab.gpu, exact: true }), 700), 1);
  await settle();
  checkContent("details gpu→device", await sampleSwitch("details gpu→device", detailsGroup.getByRole("tab", { name: app.misc.detailsTab.device, exact: true }), 700), -1);
  await settle();
  checkContent("primary details→components", await sampleSwitch("primary details→components", primary("components"), 700), -1);
  await settle();

  // ---- 指示条终点 ----
  const indicators: Record<string, IndicatorOffset | null> = {};
  const checkIndicator = async (label: string, selector: string) => {
    const offset = await page.evaluate((s) => window.__pm.indicatorOffset(document.querySelector<HTMLElement>(s)!), selector);
    indicators[label] = offset;
    if (!offset || offset.dx > 1 || offset.dy > 1 || offset.dw > 1 || offset.dh > 1 || offset.opacity !== 1 || !offset.visible) {
      fail(`${label}: indicator does not rest on the selected item: ${JSON.stringify(offset)}`);
    }
  };
  await checkIndicator("primary", ".shell-pages");
  await checkIndicator("components subnav", '.page-subnav-group[data-page-id="components"] [role="tablist"]');
  await primary("details").click();
  await settle();
  await detailsGroup.getByRole("tab", { name: app.misc.detailsTab.cpu, exact: true }).click();
  await settle();
  await checkIndicator("details subnav", '.page-subnav-group[data-page-id="details"] [role="tablist"]');
  await detailsGroup.getByRole("tab", { name: app.misc.detailsTab.device, exact: true }).click();
  await settle();
  if (await page.locator(".device-scope-tabs").count()) await checkIndicator("device scope segmented", ".device-scope-tabs");
  await primary("monitor").click();
  await settle();
  if (await page.locator(".resource-table-mode-switch").count()) await checkIndicator("resource table segmented", ".resource-table-mode-switch");

  // ---- 任务中心：高度固定、分页指示条 ----
  await page.locator("#taskCenterButton").click();
  const taskCenter = page.getByRole("dialog", { name: app.taskCenter.title, exact: true });
  await taskCenter.waitFor();
  await settle();
  const taskCenterHeights: { name: string; min: number; max: number }[] = [];
  for (const [id, name] of [["history", app.taskCenter.tabHistory], ["all", app.taskCenter.tabAll], ["active", app.taskCenter.tabActive]]) {
    const tab = taskCenter.getByRole("tab", { name: new RegExp(`^${name}`) });
    const run = await sampleSwitch(`task center tab→${id}`, tab, 500);
    const heights = run.frames.map((frame) => frame.dialog!);
    taskCenterHeights.push({ name, min: Math.min(...heights), max: Math.max(...heights) });
    if (Math.max(...heights) - Math.min(...heights) > 0.5) fail(`task center height changes on ${name}: ${Math.min(...heights)}–${Math.max(...heights)}`);
  }
  if (new Set(taskCenterHeights.map((entry) => entry.min)).size !== 1) fail(`task center tabs have different heights: ${JSON.stringify(taskCenterHeights)}`);
  await checkIndicator("task center tabs", '[role="dialog"] [role="tablist"]');
  if ((await taskCenter.getByRole("tab", { selected: true }).count()) !== 1) fail("task center tabs do not expose aria-selected");

  // ---- 焦点环：鼠标不画，键盘画 ----
  const focusAfterMouse: (Awaited<ReturnType<typeof focusState>> & { label: string })[] = [];
  const mouseCheck = async (label: string) => {
    const state = await focusState(page);
    focusAfterMouse.push({ label, ...state });
    if (state.anyVisible > 0) fail(`${label}: a focus ring after a mouse click: ${JSON.stringify(state)}`);
  };
  await mouseCheck("task center opened by mouse");
  await taskCenter.getByRole("tab", { name: new RegExp(`^${app.taskCenter.tabHistory}`) }).click();
  await mouseCheck("task center tab");
  await page.keyboard.press("Escape");
  await taskCenter.waitFor({ state: "hidden" });
  await primary("details").click();
  await mouseCheck("primary tab");
  await detailsGroup.getByRole("tab", { name: app.misc.detailsTab.gpu, exact: true }).click();
  await mouseCheck("secondary tab");
  await primary("settings").click();
  await page.getByRole("button", { name: settings.sections.appearance, exact: true }).click();
  await mouseCheck("settings nav");
  await primary("monitor").click();
  await settle();
  const segment = page.locator(".resource-table-mode-switch .segment:not(.active)").first();
  if (await segment.count()) {
    await segment.click();
    await mouseCheck("segmented option");
  }
  await primary("components").click();
  await settle();
  await mouseCheck("primary tab after settling");
  // 键盘：焦点放在选中的二级分页上，按 Tab 往后、Shift+Tab 回来，两次都看得见焦点环。
  await page.locator(".page-subnav-group:not([hidden]) [role='tab'][aria-selected='true']").focus();
  await page.keyboard.press("Tab");
  const keyboard = await focusState(page);
  if (!keyboard.activeVisible || !keyboard.ring) fail(`Tab does not show a focus ring: ${JSON.stringify(keyboard)}`);
  await page.keyboard.press("Shift+Tab");
  const keyboardBack = await focusState(page);
  if (!keyboardBack.activeVisible || !keyboardBack.ring) fail(`Shift+Tab does not show a focus ring: ${JSON.stringify(keyboardBack)}`);

  // ---- 长任务和订阅（这种会动的风格） ----
  const sequence = ["details", "components", "diskUsage", "control", "monitor", "settings", "details", "monitor"] as const;
  const runSequence = async () => {
    // Normalize the remembered details tab before counting streams in either mode.
    await primary("details").click();
    await detailsGroup.getByRole("tab", { name: app.misc.detailsTab.device, exact: true }).click();
    await primary("monitor").click();
    await settle();
    const opensBefore = streamOpens;
    let source = "monitor";
    for (const id of sequence) {
      await sampleSwitch(`primary ${source}→${id}`, primary(id), SWITCH_SAMPLE_MS);
      source = id;
    }
    await sampleSwitch("primary monitor→details", primary("details"), SWITCH_SAMPLE_MS);
    await sampleSwitch("details device→gpu", detailsGroup.getByRole("tab", { name: app.misc.detailsTab.gpu, exact: true }), SWITCH_SAMPLE_MS);
    await sampleSwitch("details gpu→device", detailsGroup.getByRole("tab", { name: app.misc.detailsTab.device, exact: true }), SWITCH_SAMPLE_MS);
    await sampleSwitch("task center open", page.locator("#taskCenterButton"), SWITCH_SAMPLE_MS);
    for (const [id, name] of [["history", app.taskCenter.tabHistory], ["all", app.taskCenter.tabAll], ["active", app.taskCenter.tabActive]]) {
      await sampleSwitch(`task center tab→${id}`, taskCenter.getByRole("tab", { name: new RegExp(`^${name}`) }), SWITCH_SAMPLE_MS);
    }
    await sampleSwitch("task center close", taskCenter.getByRole("button", { name: app.misc.close, exact: true }), SWITCH_SAMPLE_MS);
    return streamOpens - opensBefore;
  };
  await primary("monitor").click();
  await settle();
  await page.evaluate(() => { window.__pmLongTasks.length = 0; });
  const normalOpens = await runSequence();

  // ---- 无动画、超高性能：第一帧就是终态 ----
  const instant: Record<string, { primary: ContentSummary; secondary: ContentSummary }> = {};
  let noneOpens: number | null = null;
  for (const mode of ["none", "ultra"] as const) {
    await setAnimationStyle(page, app, settings, mode);
    animationMode = mode;
    await primary("components").click();
    await settle();
    const primaryRun = await sampleSwitch("primary components→details", primary("details"), 300);
    await settle();
    const secondaryRun = await sampleSwitch("details device→cpu", detailsGroup.getByRole("tab", { name: app.misc.detailsTab.cpu, exact: true }), 300);
    instant[mode] = { primary: summarize(primaryRun), secondary: summarize(secondaryRun) };
    for (const [label, run] of [["primary", primaryRun], ["secondary", secondaryRun]] as const) {
      const first = run.frames[0];
      if (first.leaving.length !== 0 || first.stage.transform !== "none" || first.stage.opacity !== 1) fail(`${mode} ${label}: the first frame is not final: ${JSON.stringify(first)}`);
      const offset = label === "primary" ? first.primaryIndicator : first.subnavIndicator;
      if (!offset || offset.dx > 1 || offset.dw > 1 || offset.opacity !== 1) fail(`${mode} ${label}: the indicator is not in place in the first frame: ${JSON.stringify(offset)}`);
    }
    if (mode === "none") {
      await primary("monitor").click();
      await settle();
      noneOpens = await runSequence();
    }
  }
  if (normalOpens !== noneOpens) fail(`the same switches open ${normalOpens} subscription streams with motion and ${noneOpens} without`);
  for (const run of interactionRuns) for (const task of run.overruns) {
    if (!task.passed) fail(`${run.mode} ${task.message}`);
  }
  await setAnimationStyle(page, app, settings, style);
  await page.evaluate(() => window.__pm.observer.disconnect());

  // Persist before throwing: an empty registry is expected to expose existing waste.
  if (evidenceDirectory) writeFileSync(resolve(evidenceDirectory, `page-motion-${style}.json`), JSON.stringify({
    startedOn, measuredOn: new Date().toISOString(), browser: page.context().browser()!.version(),
    node: process.version, baseUrl, style, subscriptionStreamsOpened: { [style]: normalOpens, none: noneOpens },
    interactionRuns, failures
  }, null, 2));
  process.stdout.write(`PAGE_MOTION_INTERACTIONS ${JSON.stringify(interactionRuns)}\n`);

  if (failures.length) throw new Error(failures.join("\n"));
  return {
    style,
    pages: pageFacts.map(({ id, contentTop }) => ({ id, contentTop })),
    repeatsOwnedElsewhere,
    barAppearMs,
    contentRuns,
    indicators,
    taskCenterHeights,
    focusAfterMouse: focusAfterMouse.map(({ label, anyVisible }) => ({ label, anyVisible })),
    keyboard: { forward: keyboard, back: keyboardBack },
    instant,
    subscriptionStreamsOpened: { [style]: normalOpens, none: noneOpens },
    interactionRuns,
    longestTaskMs: Math.max(0, ...interactionRuns.flatMap((run) => run.longTasks.map((task) => task.duration)))
  };
}

/** 页面里的探针：每帧一份快照（二级分页条、内容、退出副本、指示条、对话框高度）。 */
async function installProbe(page: ProbePage, captureGeometry = true) {
  await page.evaluate((captureGeometry) => {
    let seq = 0;
    const nodeId = (element: (Element & { __pmId?: number }) | null) => {
      if (!element) return null;
      if (!element.__pmId) { seq += 1; element.__pmId = seq; }
      return element.__pmId;
    };
    const translate = (element: Element) => {
      const value = getComputedStyle(element).transform;
      if (!value || value === "none") return { x: 0, y: 0, transform: "none" };
      const matrix = new DOMMatrixReadOnly(value);
      const x = Math.round(matrix.m41 * 100) / 100;
      const y = Math.round(matrix.m42 * 100) / 100;
      return { x, y, transform: x === 0 && y === 0 && matrix.isIdentity ? "none" : value };
    };
    const indicatorOffset = (list: Element | null) => {
      if (!list) return null;
      const indicator = list.querySelector<HTMLElement>(".selection-indicator")!;
      const selected = [...list.querySelectorAll<HTMLElement>(".active, [aria-selected='true'], [aria-checked='true'], [aria-current='page']")]
        .find((element) => element !== indicator && element.closest<HTMLElement>(".has-selection-indicator")! === list);
      if (!indicator || !selected) return null;
      const a = indicator.getBoundingClientRect();
      const b = selected.getBoundingClientRect();
      const style = getComputedStyle(indicator);
      const round = (value: number) => Math.round(Math.abs(value) * 10) / 10;
      return { dx: round(a.left - b.left), dy: round(a.top - b.top), dw: round(a.width - b.width), dh: round(a.height - b.height), opacity: Number(style.opacity), visible: style.visibility === "visible" };
    };
    const snapshot = () => {
      const bar = document.querySelector<HTMLElement>("#pageSubnav")!;
      const group = document.querySelector<HTMLElement>(".page-subnav-group:not([hidden])");
      const groupTargets = new Set<Element | null | undefined>([bar, group, group?.firstElementChild, group?.querySelector<HTMLElement>("[role='tablist']")!].filter(Boolean));
      const animations = bar ? bar.getAnimations({ subtree: true }).filter((animation) => groupTargets.has((animation.effect as KeyframeEffect | null)?.target)).length : 0;
      const stage = document.querySelector<HTMLElement>("#pageStage")!;
      const stageMove = translate(stage);
      const ghosts = [...document.querySelectorAll<HTMLElement>(".page-leaving-layer > [data-exit-ghost]")];
      const leaving = ghosts.map((element) => Math.round(Number(getComputedStyle(element).opacity) * 1000) / 1000);
      const leavingX = ghosts.map((element) => translate(element).x);
      const dialog = document.querySelector<HTMLElement>("[role='dialog']")!;
      return {
        t: performance.now(),
        bar: { node: nodeId(bar), opacity: Number(getComputedStyle(bar).opacity), transform: translate(bar).transform },
        group: group
          ? { page: group.dataset.pageId, node: nodeId(group), tabs: group.querySelectorAll<HTMLElement>("[role='tab']").length, opacity: Number(getComputedStyle(group).opacity), transform: translate(group.querySelector<HTMLElement>("[role='tablist']")!).transform, animations }
          : { page: null, node: null, tabs: 0, opacity: 1, transform: "none", animations },
        stage: { x: stageMove.x, y: stageMove.y, transform: stageMove.transform, opacity: Math.round(Number(getComputedStyle(stage).opacity) * 1000) / 1000 },
        leaving,
        leavingX,
        primaryIndicator: indicatorOffset(document.querySelector<HTMLElement>(".shell-pages")!),
        subnavIndicator: indicatorOffset(group?.querySelector<HTMLElement>("[role='tablist']")! ?? null),
        dialog: dialog ? Math.round(dialog.getBoundingClientRect().height * 10) / 10 : null
      };
    };
    window.__pmLongTasks = [];
    window.__pmLoafs = [];
    const loafSupported = PerformanceObserver.supportedEntryTypes.includes("long-animation-frame");
    const recordLoafs = (entries: PerformanceEntry[]) => {
      for (const entry of entries) window.__pmLoafs.push(entry.toJSON() as LongAnimationFrame);
    };
    const loafObserver = loafSupported ? new PerformanceObserver((list) => recordLoafs(list.getEntries())) : null;
    loafObserver?.observe({ type: "long-animation-frame" });
    const recordTasks = (entries: PerformanceEntry[]) => {
      for (const entry of entries) window.__pmLongTasks.push({ start: entry.startTime, duration: entry.duration });
    };
    const observer = new PerformanceObserver((list) => recordTasks(list.getEntries()));
    observer.observe({ type: "longtask", buffered: false });
    window.__pm = { captureGeometry, snapshot: captureGeometry ? snapshot : () => ({ t: performance.now() }), indicatorOffset, observer, recordTasks, loafObserver, recordLoafs };
  }, captureGeometry);
}

/** 点一下，从点下去之后的第一帧起逐帧记 durationMs。 */
async function sampleClick(page: Page, locator: Locator, durationMs: number) {
  await locator.waitFor({ state: "visible" });
  await page.evaluate((duration) => {
    const ARM_TIMEOUT_MS = 5000;
    const AFTER_RENDER_TASK_DELAY_MS = 0;
    const armedAt = performance.now();
    const run: ProbeRun = { t0: null, frames: [], rafGaps: [], done: false };
    let previousRaf: number | null = null;
    window.__pmRun = run;
    const tick = (timestamp: number) => {
      const now = performance.now();
      if (run.t0 === null) {
        previousRaf = timestamp;
        if (now - armedAt < ARM_TIMEOUT_MS) requestAnimationFrame(tick);
        return;
      }
      if (previousRaf !== null) run.rafGaps.push(timestamp - previousRaf);
      previousRaf = timestamp;
      const lastFrame = now - run.t0 >= duration;
      const capture = () => {
        run.frames.push({ ...window.__pm.snapshot(), dt: performance.now() - run.t0! });
        if (lastFrame) { run.t1 = performance.now(); run.done = true; }
      };
      // 外观快照等该帧的浏览器渲染更新完成再读；rAF 时间戳仍在原回调记录，CDP 覆盖整个窗口。
      // 只计时的探针不读 DOM，继续在 rAF 中采样，不把产品工作挪到另一个任务。
      if (window.__pm.captureGeometry) setTimeout(capture, AFTER_RENDER_TASK_DELAY_MS);
      else capture();
      if (!lastFrame) requestAnimationFrame(tick);
    };
    window.addEventListener("click", () => {
      run.t0 = performance.now();
    }, { once: true, capture: true });
    requestAnimationFrame(tick);
  }, durationMs);
  await locator.click();
  await page.waitForFunction(() => window.__pmRun.done, undefined, { timeout: durationMs + 5000 });
  return page.evaluate(() => {
    window.__pm.recordTasks(window.__pm.observer.takeRecords());
    window.__pm.recordLoafs(window.__pm.loafObserver?.takeRecords() ?? []);
    const run = window.__pmRun;
    return { ...run, longAnimationFrames: window.__pmLoafs.filter((frame) => frame.startTime < run.t1! && frame.startTime + frame.duration > run.t0!), longTasks: window.__pmLongTasks.filter((task) => task.start < run.t1! && task.start + task.duration > run.t0!) } as ProbeRun & { t0: number; t1: number; longAnimationFrames: LongAnimationFrame[]; longTasks: LongTask[] };
  });
}

/** 一次切换的摘要：第一帧、退出副本消失和内容停下的时刻。 */
function summarize(run: GeometryRun) {
  const first = run.frames[0];
  const goneAt = run.frames.find((frame) => frame.leaving.every((opacity) => opacity < 0.05))?.dt ?? null;
  const settledAt = run.frames.find((frame) => frame.stage.transform === "none" && frame.stage.opacity === 1 && frame.leaving.length === 0)?.dt ?? null;
  const round = (value: number | null) => (value === null ? null : Math.round(value));
  return { frames: run.frames.length, firstStageX: first.stage.x, firstLeaving: first.leaving, exitDoneMs: round(goneAt), settledMs: round(settledAt) };
}

async function focusState(page: Page) {
  return page.evaluate(() => {
    const active = document.activeElement;
    const style = active ? getComputedStyle(active) : null;
    const ring = Boolean(style && ((style.outlineStyle !== "none" && Number.parseFloat(style.outlineWidth) > 0) || style.boxShadow !== "none"));
    return {
      active: active ? `${active.tagName.toLowerCase()}${active.id ? `#${active.id}` : ""}.${String(active.className).split(" ").filter(Boolean).slice(0, 2).join(".")}` : null,
      activeVisible: Boolean(active?.matches(":focus-visible")),
      anyVisible: document.querySelectorAll<HTMLElement>(":focus-visible").length,
      ring
    };
  });
}

/** 设置 → 外观 → 动画风格，真实点击并保存，等 body[data-animations] 换成这种风格（模拟后端的自调度结果是正常，自动关闭不改它）。 */
async function setAnimationStyle(page: Page, app: AppCopy, settings: SettingsTextBundle, style: AnimationStyleId) {
  await page.getByRole("navigation", { name: app.shell.pageNav, exact: true }).getByRole("button", { name: app.page.settings, exact: true }).click();
  await page.getByRole("button", { name: settings.sections.appearance, exact: true }).click();
  const label = settings.animationStyleOptions.find((option) => option.id === style)!.label;
  const radio = page.getByRole("radiogroup", { name: settings.appearance.animationStyleTitle, exact: true }).getByRole("radio", { name: label, exact: true });
  if (!(await radio.isChecked())) {
    await radio.click();
    await page.getByRole("button", { name: settings.actions.save, exact: true }).click();
  }
  await page.waitForFunction((value) => document.body.dataset.animations === value, style, { timeout: 5000 });
}

export { installProbe, sampleClick, setAnimationStyle };
