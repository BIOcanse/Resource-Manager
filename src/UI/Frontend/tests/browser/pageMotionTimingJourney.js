import { mkdirSync, writeFileSync } from "node:fs";
import { resolve } from "node:path";
import { loadAppCopy } from "../../src/i18n/copy/appCopyLoader.ts";
import { loadSettingsText } from "../../src/i18n/settingsLoader.ts";
import { installProbe, sampleClick, setAnimationStyle } from "./pageMotionJourney.js";
import { startMotionProfile, stopMotionProfile } from "./pageMotionEvidence.js";
import { assessInteractionLongTasks } from "./interactionLongTaskBudget.js";

const SWITCH_SAMPLE_MS = 700;
const INSTANT_SAMPLE_MS = 300;
const INITIAL_SETTLE_MS = 1200;
/** 与 page-motion 的共同切换序列相同；帧探针只记时间，不读取 DOM，避免测试自身强制布局。外观仍由 page-motion 验证。 */
export default async function pageMotionTimingJourney(page, baseUrl) {
  const evidenceRoot = process.env.RM_PAGE_MOTION_EVIDENCE_DIR;
  if (!evidenceRoot) throw new Error("page-motion-timing requires RM_PAGE_MOTION_EVIDENCE_DIR");
  // 全旅程 gate 可以与 page-motion 并发；各探针拥有独立的 profile 和汇总文件。
  const evidenceDirectory = resolve(evidenceRoot, "page-motion-timing");
  mkdirSync(evidenceDirectory, { recursive: true });
  const startedOn = new Date().toISOString();
  const failures = [];
  page.on("pageerror", error => failures.push(String(error)));
  const app = await loadAppCopy("zh-CN");
  const settings = await loadSettingsText("zh-CN");
  await page.setViewportSize({ width: 1280, height: 800 });
  await page.goto(baseUrl, { waitUntil: "domcontentloaded" });
  const nav = page.getByRole("navigation", { name: app.shell.pageNav, exact: true });
  await nav.waitFor();
  await page.waitForTimeout(INITIAL_SETTLE_MS);
  const primary = id => nav.getByRole("button", { name: app.page[id], exact: true });
  const details = page.locator('.page-subnav-group[data-page-id="details"]');
  const taskCenter = page.getByRole("dialog", { name: app.taskCenter.title, exact: true });
  const settle = () => page.waitForTimeout(SWITCH_SAMPLE_MS);
  for (const id of ["monitor", "components", "diskUsage", "control", "details", "settings"]) {
    await primary(id).click(); await settle();
  }
  await installProbe(page, false);
  const interactionRuns = [];
  const subscriptionStreamsOpened = {};
  let streamOpens = 0;
  page.on("request", request => { if (new URL(request.url()).pathname === "/api/subscriptions/stream") streamOpens++; });
  for (const mode of ["smooth", "none", "ultra"]) {
    await setAnimationStyle(page, app, settings, mode);
    await primary("details").click();
    await details.getByRole("tab", { name: app.misc.detailsTab.device, exact: true }).click();
    await primary(mode === "ultra" ? "components" : "monitor").click();
    await settle();
    const initialStreams = streamOpens;
    const sample = async (step, locator, duration = SWITCH_SAMPLE_MS) => {
      const profile = await startMotionProfile(page, evidenceDirectory, `${String(interactionRuns.length).padStart(2, "0")}-${mode}`);
      let run;
      let cpuProfile;
      try { run = await sampleClick(page, locator, duration); }
      finally { cpuProfile = await stopMotionProfile(profile, run?.longTasks ?? []); }
      const window = { step, start: run.t0, end: run.t1 };
      const overruns = assessInteractionLongTasks(run.longTasks, [window]);
      interactionRuns.push({ mode, ...window, longTasks: run.longTasks, overruns, cpuProfile,
        firstFrameMs: run.frames[0].dt, frameTimes: run.frames.map(frame => frame.t),
        frameGaps: run.frames.map((frame, index) => frame.t - (index ? run.frames[index - 1].t : run.t0)),
        rafGaps: run.rafGaps, longAnimationFrames: run.longAnimationFrames });
      for (const task of overruns) if (!task.passed) failures.push(`${mode} ${task.message}`);
    };
    if (mode === "ultra") {
      await sample("primary components→details", primary("details"), INSTANT_SAMPLE_MS);
      await settle();
      await sample("details device→cpu", details.getByRole("tab", { name: app.misc.detailsTab.cpu, exact: true }), INSTANT_SAMPLE_MS);
    } else {
      let source = "monitor";
      for (const id of ["details", "components", "diskUsage", "control", "monitor", "settings", "details", "monitor"]) {
        await sample(`primary ${source}→${id}`, primary(id)); source = id;
      }
      await sample("primary monitor→details", primary("details"));
      await sample("details device→gpu", details.getByRole("tab", { name: app.misc.detailsTab.gpu, exact: true }));
      await sample("details gpu→device", details.getByRole("tab", { name: app.misc.detailsTab.device, exact: true }));
      await sample("task center open", page.locator("#taskCenterButton"));
      for (const [id, name] of [["history", app.taskCenter.tabHistory], ["all", app.taskCenter.tabAll], ["active", app.taskCenter.tabActive]]) {
        await sample(`task center tab→${id}`, taskCenter.getByRole("tab", { name: new RegExp(`^${name}`) }));
      }
      await sample("task center close", taskCenter.getByRole("button", { name: app.misc.close, exact: true }));
    }
    subscriptionStreamsOpened[mode] = streamOpens - initialStreams;
  }
  await page.evaluate(() => { window.__pm.observer.disconnect(); window.__pm.loafObserver?.disconnect(); });
  const result = { measurementKind: "timing-only-no-dom-snapshots", startedOn, measuredOn: new Date().toISOString(),
    browser: page.context().browser().version(), node: process.version, baseUrl, subscriptionStreamsOpened, interactionRuns, failures };
  writeFileSync(resolve(evidenceDirectory, "page-motion.json"), JSON.stringify(result, null, 2));
  if (failures.length) throw new Error(failures.join("\n"));
  return result;
}