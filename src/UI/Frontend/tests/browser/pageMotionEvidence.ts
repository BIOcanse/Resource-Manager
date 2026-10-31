import type { Page, CDPSession } from "playwright-core";
export type PerformanceMetric = { name: string; value: number };
export type CpuCallFrame = { functionName: string; scriptId: string; url: string; lineNumber: number; columnNumber: number };
export type CpuProfile = { nodes: { id: number; children?: number[]; callFrame: CpuCallFrame }[]; startTime: number; samples?: number[]; timeDeltas?: number[] };
export type ProfileTask = { start: number; duration: number };
export type MotionProfileSession = { cdp: CDPSession; threadCdp: CDPSession; threadMetrics: PerformanceMetric[]; navigationStart: number; metrics: PerformanceMetric[]; path: string };
import { writeFileSync } from "node:fs";
import { resolve } from "node:path";

export const CPU_PROFILE_SAMPLE_INTERVAL_US = 1000;
export const CPU_PROFILE_TOP_FUNCTIONS = 8;
const LAYOUT_METRICS = ["LayoutDuration", "RecalcStyleDuration", "LayoutCount", "RecalcStyleCount"];

/** Explicit opt-in: caller supplies an existing registered output directory. */
export async function startMotionProfile(page: Page, outputDirectory: string, name: string): Promise<MotionProfileSession> {
  const cdp = await page.context().newCDPSession(page);
  let threadCdp: CDPSession | undefined;
  try {
    threadCdp = await page.context().newCDPSession(page);
    await threadCdp.send("Performance.enable", { timeDomain: "threadTicks" });
    const { metrics: threadMetrics } = await threadCdp.send("Performance.getMetrics");
    await cdp.send("Performance.enable");
    const { metrics } = await cdp.send("Performance.getMetrics");
    const navigationStart = metrics.find((item) => item.name === "NavigationStart")?.value!;
    if (!Number.isFinite(navigationStart)) throw new Error("CPU profile has no NavigationStart clock anchor");
    await cdp.send("Profiler.enable");
    await cdp.send("Profiler.setSamplingInterval", { interval: CPU_PROFILE_SAMPLE_INTERVAL_US });
    await cdp.send("Profiler.start");
    return { cdp, threadCdp, threadMetrics, navigationStart, metrics, path: resolve(outputDirectory, `${name}.cpuprofile`) };
  } catch (error) {
    await threadCdp?.detach();
    await cdp.detach();
    throw error;
  }
}

export async function stopMotionProfile<T extends ProfileTask>(session: MotionProfileSession, tasks: T[]) {
  try {
    const { profile } = await session.cdp.send("Profiler.stop");
    const [{ metrics }, { metrics: threadMetrics }] = await Promise.all([
      session.cdp.send("Performance.getMetrics"), session.threadCdp.send("Performance.getMetrics")
    ]);
    const delta = (after: PerformanceMetric[], before: PerformanceMetric[]) => Object.fromEntries(LAYOUT_METRICS.map((name) =>
      [name, after.find((m) => m.name === name)!.value - before.find((m) => m.name === name)!.value]));
    const layoutMetrics = delta(metrics, session.metrics);
    const layoutCpuMetrics = delta(threadMetrics, session.threadMetrics);
    writeFileSync(session.path, JSON.stringify(profile));
    return { path: session.path, navigationStart: session.navigationStart, layoutMetrics, layoutCpuMetrics,
      tasks: tasks.map((task) => ({ ...task, cpu: summarizeTaskCpu(profile, session.navigationStart, task) })) };
  } finally {
    await session.threadCdp.detach();
    await session.cdp.detach();
  }
}

/** Self and inclusive sampled CPU weights in the long task, on the performance.now clock.
 * These are sampling estimates, not exact native layout durations. No recursive tree walk.
 */
export function summarizeTaskCpu(profile: CpuProfile, navigationStart: number, task: ProfileTask) {
  const nodes = new Map(profile.nodes.map((node) => [node.id, node]));
  const parents = new Map<number, number>();
  for (const node of profile.nodes) for (const child of node.children ?? []) parents.set(child, node.id);
  const self = new Map<number, number>();
  const inclusive = new Map<number, number>();
  let time = profile.startTime / 1000 - navigationStart * 1000;
  let sampledMs = 0;
  for (let index = 0; index < (profile.samples?.length ?? 0); index += 1) {
    const next = time + profile.timeDeltas![index] / 1000;
    const weight = Math.max(0, Math.min(next, task.start + task.duration) - Math.max(time, task.start));
    time = next;
    if (!weight) continue;
    sampledMs += weight;
    const id = profile.samples![index];
    self.set(id, (self.get(id) ?? 0) + weight);
    let cursor: number | undefined = id;
    const visited = new Set<number>();
    while (cursor !== undefined && !visited.has(cursor) && visited.size < nodes.size) {
      visited.add(cursor);
      inclusive.set(cursor, (inclusive.get(cursor) ?? 0) + weight);
      cursor = parents.get(cursor);
    }
  }
  const top = (weights: Map<number, number>) => [...weights].sort((a, b) => b[1] - a[1]).slice(0, CPU_PROFILE_TOP_FUNCTIONS)
    .map(([id, ms]) => ({ ...nodes.get(id)!.callFrame, ms: Math.round(ms * 100) / 100 }));
  return { sampledMs: Math.round(sampledMs * 100) / 100, self: top(self), inclusive: top(inclusive) };
}
