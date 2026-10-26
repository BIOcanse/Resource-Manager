import assert from "node:assert/strict";
import { writeFileSync } from "node:fs";
import { loadAppCopy } from "../../src/i18n/copy/appCopyLoader.ts";
import { writeFrontendPreferences } from "./frontendPreferencesFixture.js";
import { assessInteractionLongTasks } from "./interactionLongTaskBudget.js";
import { beginMonitorEvidence, endMonitorEvidence, overlappingQueueJobs } from "./monitorPageTaskEvidence.js";

const SETTLE_MS = 700;
const TRIALS = [1, 2, 3];
const MODES = ["normal", "none", "ultra"];
const SWITCHES = [["control", "monitor"], ["details", "monitor"], ["monitor", "details"]];

// 与 page-motion 相同的点击窗口相交规则；帧内只记时间，不读布局。
export default async function monitorPageTaskJourney(page, baseUrl) {
  const app = await loadAppCopy("zh-CN");
  const errors = [];
  page.on("pageerror", error => errors.push(String(error)));
  await page.setViewportSize({ width: 1280, height: 800 });
  await page.goto(baseUrl, { waitUntil: "domcontentloaded" });
  const primary = id => page.getByRole("navigation", { name: app.shell.pageNav, exact: true })
    .getByRole("button", { name: app.page[id], exact: true });
  const settle = () => page.waitForTimeout(SETTLE_MS);
  const requestedTrial = process.env.RM_MONITOR_TASK_TRIAL;
  if (requestedTrial !== undefined && !/^[1-3]$/.test(requestedTrial)) throw new Error("RM_MONITOR_TASK_TRIAL must be 1, 2 or 3");
  const trials = requestedTrial === undefined ? TRIALS : [Number(requestedTrial)];
  const results = [];
  for (const mode of MODES) {
    await writeFrontendPreferences(page, preferences => { preferences.appearance.animations = mode; });
    await page.reload({ waitUntil: "domcontentloaded" });
    await page.locator(`body[data-animations='${mode}']`).waitFor();
    for (const id of ["monitor", "control", "details", "monitor"]) {
      await primary(id).click();
      await settle();
    }
    const slots = await page.locator(".card-slot").count();
    assert.ok(slots > 0, "measures real dashboard slots");
    for (const trial of trials) {
      for (const [from, to] of SWITCHES) {
        await primary(from).click();
        await settle();
        const hostStart = Date.now();
        const evidence = await beginMonitorEvidence(page, `${trial}-${mode}-${from}-to-${to}`);
        let run;
        let measured;
        try {
          await page.evaluate(duration => {
            const tasks = [];
            const record = entries => { for (const entry of entries) tasks.push({ start: entry.startTime, duration: entry.duration }); };
            const observer = new PerformanceObserver(list => record(list.getEntries()));
            observer.observe({ type: "longtask", buffered: false });
            const state = { start: null, end: null, frames: [], done: false };
            window.__monitorTaskRun = { state, tasks, observer, record };
            window.addEventListener("click", () => {
              state.start = performance.now();
              const tick = () => {
                const now = performance.now();
                state.frames.push(now);
                if (now - state.start < duration) requestAnimationFrame(tick);
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
              .filter(entry => entry.name.startsWith("rm-slot-cost:") && entry.startTime >= state.start && entry.startTime < state.end)
              .map(entry => ({ name: entry.name, start: entry.startTime, duration: entry.duration }));
            return { ...state, slotLifecycle, tasks: tasks.filter(task => task.start < state.end && task.start + task.duration > state.start) };
          });
        } finally { measured = await endMonitorEvidence(evidence); }
        const step = `primary ${from}→${to}`;
        const frameIntervals = run.frames.map((time, index) => time - (index === 0 ? run.start : run.frames[index - 1]));
        const result = { mode, trial, step, slots, start: run.start, end: run.end,
          longTaskCount: run.tasks.length, longestTaskMs: Math.max(0, ...run.tasks.map(task => task.duration)),
          longTasks: run.tasks, slotLifecycle: run.slotLifecycle,
          overruns: assessInteractionLongTasks(run.tasks, [{ step, start: run.start, end: run.end }]),
          firstFrameMs: frameIntervals[0], maxFrameIntervalMs: Math.max(...frameIntervals),
          ...measured, queueOverlaps: overlappingQueueJobs(hostStart, Date.now()) };
        results.push(result);
        process.stdout.write(`MONITOR_PAGE_TASK ${JSON.stringify(result)}\n`);
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
