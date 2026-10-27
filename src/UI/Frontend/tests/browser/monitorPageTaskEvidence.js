import { existsSync, readFileSync, readdirSync, statSync, writeFileSync } from "node:fs";
import { resolve } from "node:path";
import { CPU_PROFILE_SAMPLE_INTERVAL_US } from "./pageMotionEvidence.js";

const DURATION_METRICS = ["LayoutDuration", "RecalcStyleDuration", "ScriptDuration", "TaskDuration"];
const COUNT_METRICS = ["LayoutCount", "RecalcStyleCount"];

export async function beginMonitorEvidence(page, name) {
  const cdp = await page.context().newCDPSession(page);
  try {
    await cdp.send("Performance.enable");
    const { metrics } = await cdp.send("Performance.getMetrics");
    const before = Object.fromEntries(metrics.map(item => [item.name, item.value]));
    if (!Number.isFinite(before.NavigationStart)) throw new Error("No NavigationStart for CPU profile clock");
    const directory = process.env.RM_SLOT_PROFILE_DIR;
    if (directory) {
      await cdp.send("Profiler.enable");
      await cdp.send("Profiler.setSamplingInterval", { interval: CPU_PROFILE_SAMPLE_INTERVAL_US });
      await cdp.send("Profiler.start");
    }
    // Long Tasks API omits tasks below 50 ms. Opt-in timeline evidence measures
    // actual headroom without changing the interaction budget or product code.
    const trace = process.env.RM_SLOT_TRACE === "1" ? [] : null;
    if (trace) {
      cdp.on("Tracing.dataCollected", ({ value }) => trace.push(...value));
      await cdp.send("Tracing.start", { categories: "-*,toplevel,devtools.timeline", transferMode: "ReportEvents" });
    }
    return { cdp, before, trace, path: directory ? resolve(directory, `${name}.cpuprofile`) : null };
  } catch (error) { await cdp.detach(); throw error; }
}

export async function endMonitorEvidence(session) {
  try {
    const { metrics } = await session.cdp.send("Performance.getMetrics");
    const after = Object.fromEntries(metrics.map(item => [item.name, item.value]));
    const cdp = {};
    for (const name of DURATION_METRICS) {
      if (!Number.isFinite(after[name]) || !Number.isFinite(session.before[name])) throw new Error(`Missing CDP metric ${name}`);
      cdp[`${name}Ms`] = (after[name] - session.before[name]) * 1000;
    }
    for (const name of COUNT_METRICS) {
      if (!Number.isFinite(after[name]) || !Number.isFinite(session.before[name])) throw new Error(`Missing CDP metric ${name}`);
      cdp[name] = after[name] - session.before[name];
    }
    if (session.path) {
      const { profile } = await session.cdp.send("Profiler.stop");
      writeFileSync(session.path, JSON.stringify(profile));
    }
    let tracePath = null;
    if (session.trace) {
      const complete = new Promise(resolveEnd => session.cdp.once("Tracing.tracingComplete", resolveEnd));
      await session.cdp.send("Tracing.end");
      await complete;
      if (session.path) {
        tracePath = session.path.replace(/\.cpuprofile$/, ".trace.json");
        writeFileSync(tracePath, JSON.stringify({ traceEvents: session.trace }));
      }
    }
    return { cdp, profilePath: session.path, tracePath, navigationStart: session.before.NavigationStart };
  } finally { await session.cdp.detach(); }
}

// 包括窗口期间结束的短作业；只留标识，不输出环境变量。当前合同允许重叠，不再拒收。
export function overlappingQueueJobs(start, end) {
  const root = process.env.RM_MONITOR_QUEUE_ROOT;
  if (!root) return null;
  const ownDirectory = resolve(process.cwd()).toLowerCase();
  const jobs = resolve(root, "jobs");
  const overlaps = [];
  for (const file of readdirSync(jobs).filter(name => name.endsWith(".job.json"))) {
    const job = JSON.parse(readFileSync(resolve(jobs, file), "utf8").replace(/^\uFEFF/, ""));
    if (resolve(job.workingDirectory).toLowerCase() === ownDirectory) continue;
    const startedPath = resolve(root, "slots", `${String(job.slot).padStart(6, "0")}.started`);
    if (!existsSync(startedPath)) continue;
    const started = Date.parse(readFileSync(startedPath, "utf8").trim());
    if (!Number.isFinite(started)) throw new Error(`Invalid queue start marker: ${job.id}`);
    if (started > end) continue;
    const resultPath = resolve(jobs, `${job.id}.result.json`);
    let ended = null;
    let status = "running";
    let receiptTime = false;
    if (existsSync(resultPath)) {
      const result = JSON.parse(readFileSync(resultPath, "utf8").replace(/^\uFEFF/, ""));
      status = result.status;
      receiptTime = !result.endedUtc;
      ended = result.endedUtc ? Date.parse(result.endedUtc) : statSync(resultPath).mtimeMs;
      if (ended < start) continue;
    }
    overlaps.push({ id: job.id, label: job.label, status, startedUtc: new Date(started).toISOString(),
      endedUtc: ended === null ? null : new Date(ended).toISOString(), endIsReceiptTime: receiptTime });
  }
  return overlaps;
}
