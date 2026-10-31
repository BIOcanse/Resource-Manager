type MonitorRun = { start: number; end: number; frames: number[]; done: boolean; slotLifecycle: { name: string; start: number; duration: number }[]; rowPool: { rows: number; slots: number; empty: number } | null; tasks: MonitorTask[] };
type MonitorResult = Awaited<ReturnType<typeof endMonitorEvidence>> & { mode: AnimationStyleId; trial: number; step: string; slots: number; start: number; end: number; longTaskCount: number; longestTaskMs: number; longTasks: MonitorTask[]; slotLifecycle: MonitorRun["slotLifecycle"]; rowPool: MonitorRun["rowPool"]; overruns: ReturnType<typeof assessInteractionLongTasks>; firstFrameMs: number; maxFrameIntervalMs: number; queueOverlaps: ReturnType<typeof overlappingQueueJobs> };
import type { AnimationStyleId } from "../../src/presentation/animationStyle.ts";
type MonitorTask = { start: number; duration: number };
type MonitorState = { start: number | null; end: number | null; frames: number[]; done: boolean };
declare global { interface Window { __monitorTaskRun: { state: MonitorState; tasks: MonitorTask[]; observer: PerformanceObserver; record(entries: PerformanceEntry[]): void } } }
import type { Page } from "playwright-core";
import type { AppCopy } from "../../src/i18n/copy/index.ts";
import assert from "node:assert/strict";
import { writeFileSync } from "node:fs";
import { loadAppCopy } from "../../src/i18n/copy/appCopyLoader.ts";
import { writeFrontendPreferences } from "./frontendPreferencesFixture.ts";
import { assessInteractionLongTasks } from "./interactionLongTaskBudget.ts";
import { beginMonitorEvidence, endMonitorEvidence, overlappingQueueJobs } from "./monitorPageTaskEvidence.ts";

const SETTLE_MS = 700;
const TRIALS = [1, 2, 3];
const MODES: AnimationStyleId[] = ["smooth", "desktop", "terminal", "none", "ultra"];
const SWITCHES = [["control", "monitor"], ["details", "monitor"], ["monitor", "details"]] as const;

// 与 page-motion 相同的点击窗口相交规则；帧内只记时间，不读布局。
export default async function monitorPageTaskJourney(page: Page, baseUrl: string) {
  const app = await loadAppCopy("zh-CN");
  const errors: string[] = [];
  page.on("pageerror", error => errors.push(String(error)));
  const scenario = await page.request.post(`${baseUrl}/__test/scenario`, {
    data: { resourceTableVariant: "many", resourceTableRowCount: 80 }
  });
  assert.ok(scenario.ok(), "configures enough real rows to fill the virtual row pool");
  await page.setViewportSize({ width: 1280, height: 800 });
  await page.goto(baseUrl, { waitUntil: "domcontentloaded" });
  const primary = (id: keyof AppCopy["page"]) => page.getByRole("navigation", { name: app.shell.pageNav, exact: true })
    .getByRole("button", { name: app.page[id], exact: true });
  const settle = () => page.waitForTimeout(SETTLE_MS);
  const requestedTrial = process.env.RM_MONITOR_TASK_TRIAL;
  if (requestedTrial !== undefined && !/^[1-3]$/.test(requestedTrial)) throw new Error("RM_MONITOR_TASK_TRIAL must be 1, 2 or 3");
  const trials = requestedTrial === undefined ? TRIALS : [Number(requestedTrial)];
  const results: MonitorResult[] = [];
  for (const mode of MODES) {
    await writeFrontendPreferences(page, preferences => { preferences.appearance.animationStyle = mode; });
    await page.reload({ waitUntil: "domcontentloaded" });
    await page.locator(`body[data-animations='${mode}']`).waitFor();
    for (const id of ["monitor", "control", "details", "monitor"] as const) {
      await primary(id).click();
      await settle();
    }
    // Warm the table's actual retained value, including when it starts below the
    // viewport. The measured switch then mounts a full pool, never empty slots.
    await page.locator("#pageStage .resource-table-panel").scrollIntoViewIfNeeded();
    await page.waitForFunction(() => Number(document.querySelector<HTMLElement>("#pageStage .resource-table-frame")?.getAttribute("aria-rowcount")!) > 80);
    await page.locator("#pageStage .dashboard").scrollIntoViewIfNeeded();
    await settle();
    const slots = await page.locator(".card-slot").count();
    assert.ok(slots > 0, "measures real dashboard slots");
    for (const trial of trials) {
      for (const [from, to] of SWITCHES) {
        await primary(from).click();
        await settle();
        const hostStart = Date.now();
        const evidence = await beginMonitorEvidence(page, `${trial}-${mode}-${from}-to-${to}`);
        let run: MonitorRun;
        let measured: Awaited<ReturnType<typeof endMonitorEvidence>>;
        try {
          await page.evaluate(duration => {
            const tasks: MonitorTask[] = [];
            const record = (entries: PerformanceEntry[]) => { for (const entry of entries) tasks.push({ start: entry.startTime, duration: entry.duration }); };
            const observer = new PerformanceObserver(list => record(list.getEntries()));
            observer.observe({ type: "longtask", buffered: false });
            const state: MonitorState = { start: null, end: null, frames: [], done: false };
            window.__monitorTaskRun = { state, tasks, observer, record };
            window.addEventListener("click", () => {
              state.start = performance.now();
              const tick = () => {
                const now = performance.now();
                state.frames.push(now);
                if (now - state.start! < duration) requestAnimationFrame(tick);
                else { state.end = performance.now(); state.done = true; }
              };
              requestAnimationFrame(tick);
            }, { capture: true, once: true });
          }, SETTLE_MS);
          await primary(to).click();
          await page.waitForFunction(() => window.__monitorTaskRun.state.done);
          run = await page.evaluate(() => {
            const { state, tasks, observer, record } = window.__monitorTaskRun;
            record(observer.takeRecords());
            observer.disconnect();
            const slotLifecycle = performance.getEntriesByType("measure")
              .filter(entry => entry.name.startsWith("rm-slot-cost:") && entry.startTime >= state.start! && entry.startTime < state.end!)
              .map(entry => ({ name: entry.name, start: entry.startTime, duration: entry.duration }));
            const table = document.querySelector<HTMLElement>("#pageStage .resource-table-frame")!;
            const rowPool = table ? {
              rows: Number(table.getAttribute("aria-rowcount")) - 1,
              slots: table.querySelectorAll<HTMLElement>(".resource-table-row").length,
              empty: table.querySelectorAll<HTMLElement>('.resource-table-row[aria-hidden="true"]').length
            } : null;
            return { ...state, slotLifecycle, rowPool, tasks: tasks.filter(task => task.start < state.end! && task.start + task.duration > state.start!) } as MonitorRun;
          });
        } finally { measured = await endMonitorEvidence(evidence); }
        const step = `primary ${from}→${to}`;
        const frameIntervals = run.frames.map((time, index) => time - (index === 0 ? run.start : run.frames[index - 1]));
        const result = { mode, trial, step, slots, start: run.start, end: run.end,
          longTaskCount: run.tasks.length, longestTaskMs: Math.max(0, ...run.tasks.map(task => task.duration)),
          longTasks: run.tasks, slotLifecycle: run.slotLifecycle, rowPool: run.rowPool,
          overruns: assessInteractionLongTasks(run.tasks, [{ step, start: run.start, end: run.end }]),
          firstFrameMs: frameIntervals[0], maxFrameIntervalMs: Math.max(...frameIntervals),
          ...measured, queueOverlaps: overlappingQueueJobs(hostStart, Date.now()) };
        results.push(result);
        process.stdout.write(`MONITOR_PAGE_TASK ${JSON.stringify(result)}\n`);
        if (to === "monitor") {
          assert.ok(run.rowPool?.rows! >= 80 && run.rowPool!.slots > 0
            && run.rowPool!.rows > run.rowPool!.slots && run.rowPool!.empty === 0,
          `measures a full row pool: ${JSON.stringify(run.rowPool)}`);
        }
      }
    }
  }
  if (process.env.RM_MONITOR_TASK_RESULT) writeFileSync(process.env.RM_MONITOR_TASK_RESULT, JSON.stringify(results, null, 2));
  assert.deepEqual(errors, []);
  // 显式诊断批次不在超预算时丢掉后续样本；结果中仍保留每条未登记失败，普通门禁严格检查。
  if (process.env.RM_SLOT_TASK_BASELINE !== "1") {
    const failures = results.flatMap(result => result.overruns.filter(item => !item.passed).map(item => `${result.mode}: ${item.message}`));
    assert.deepEqual(failures, []);
  }
  return results;
}
