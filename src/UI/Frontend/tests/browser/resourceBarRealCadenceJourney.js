import { seedFrontendPreferences } from "./frontendPreferencesFixture.js";
import { zhMonitorCopy } from "../../src/i18n/copy/zh/monitor.ts";

// 资源条按真机的帧间隔更新时每一帧都有过渡（混合数据 resourceSoftwareMix: "audit"）：
// - 资源条帧改成手动发（manualPublishPaths），按 10-26 抓到的已安装 .23 版本的真实帧间隔回放 13 帧
//   （real-wire/20261026T001848Z 第 10–17 和 60–63 个间隔，含最长的 2084 ms 和最短的 934 ms）；
//   前端在帧到达时开始过渡，不按自己的 1 s 时钟，所以间隔不齐也要每帧都动；
// - 每第三帧在过渡开始后约 120 ms 把外观属性原样再写一遍（同一时刻到达的自调度流会这样写），过渡不能被打断；
// - 第 6、7 帧 CPU 条按后端的“无读数”形状发（总量为空、没有软件、带原因 hardware-source-unavailable），它这一行不卸载、
//   不消失，条头的读数换成这条原因的话（简体中文），下面物理内存那一行的位置一点都不动；数据回来后原因不再显示。
// 其他订阅照常按 1 s 对齐发帧，和回放的帧错开落在过渡里。
// 第二段按 10-26 抓到的 0.3.1-beta.20261026.1 的半帧回放（real-wire/20261026T075120Z-rhythm 的前 14 个间隔，
// 约 350/650 ms 交替，每帧只有一半的条变）：A 帧只变物理内存，B 帧只变 CPU，没变的条原样重发。
// 物理内存的过渡在这一段拉长到 1500 ms，于是每个 B 帧都落在它正在走的过渡里（不能重来、不能被截、不能跳到末帧），
// 下一个 A 帧在过渡没走完时到达（要从画面当前的位置接着走）。
const replayGapsMs = [1543, 1901, 1428, 1418, 1827, 1764, 2084, 1190, 1702, 934, 1178, 1177];
const unavailableFrames = new Set([5, 6]);
const halfFrameGapsMs = [424, 319, 861, 258, 644, 238, 670, 408, 573, 356, 653, 341, 610, 319];
const halfFrameMemoryDurationMs = 1500;
const minimumPositions = 5;

export default async function resourceBarRealCadenceJourney(page, baseUrl) {
  const errors = [];
  page.on("pageerror", (error) => errors.push(String(error)));
  await setScenario(page, baseUrl, { resourceSoftwareMix: "audit", resourceBreakdownGeneration: 6, resourceUnavailableBarIds: [] });
  await seedFrontendPreferences(page, (value) => {
    value.appearance.theme = "light";
    value.appearance.animationStyle = "desktop";
    value.appearance.language = "zh-CN";
  });
  await page.setViewportSize({ width: 1440, height: 900 });
  await page.goto(`${baseUrl}/`, { waitUntil: "domcontentloaded" });
  await page.waitForFunction(() => {
    const canvas = document.querySelector('.resource-bar-track[data-metric-id="memory.usage"] canvas');
    return canvas instanceof HTMLCanvasElement && canvas.width > 100 && Number(canvas.dataset.resourceSegmentCount) >= 10
      && document.querySelector('.resource-bar-track[data-metric-id="cpu.usage"]');
  }, null, { timeout: 10000 });
  await setScenario(page, baseUrl, { manualPublishPaths: ["/api/resource-monitor/subscribe"] });
  // 切到手动之前排好的那一次定时发帧还会来一次，等它过去再开始。
  await page.waitForTimeout(1300);

  let result;
  let halfFrames;
  try {
    result = await page.evaluate(async ({ gaps, unavailable }) => {
      const track = document.querySelector('.resource-bar-track[data-metric-id="memory.usage"]');
      const canvas = track.querySelector("canvas");
      const context = canvas.getContext("2d");
      const ratio = canvas.width / canvas.getBoundingClientRect().width;
      const overscan = Number.parseFloat(getComputedStyle(canvas.parentElement).getPropertyValue("--segment-paint-overscan")) * ratio;
      const cpuRow = document.querySelector('.resource-bar-track[data-metric-id="cpu.usage"]').closest(".resource-breakdown-item");
      const samples = [];
      let sampling = true;
      const sample = () => {
        if (!sampling) return;
        const data = context.getImageData(0, Math.floor(canvas.height - overscan - 3 * ratio), canvas.width, 1).data;
        let boundary = -1;
        for (let x = 1; x < canvas.width; x++) {
          if (data[x * 4] !== data[0] || data[x * 4 + 1] !== data[1] || data[x * 4 + 2] !== data[2]) { boundary = x; break; }
        }
        samples.push({ t: performance.now(), boundary, top: track.getBoundingClientRect().top, cpuConnected: cpuRow.isConnected });
        requestAnimationFrame(sample);
      };
      requestAnimationFrame(sample);
      const publish = async (patch) => {
        const response = await fetch("/__test/subscriptions/publish", {
          method: "POST",
          headers: { "content-type": "application/json" },
          body: JSON.stringify({ path: "/api/resource-monitor/subscribe", patch })
        });
        return (await response.json()).published;
      };
      const sleep = (ms) => new Promise((resolve) => setTimeout(resolve, ms));
      const frames = [];
      for (let index = 0; index <= gaps.length; index++) {
        if (index > 0) await sleep(gaps[index - 1]);
        const at = performance.now();
        const published = await publish({
          resourceBreakdownGeneration: index % 2 === 0 ? 1 : 6,
          resourceUnavailableBarIds: unavailable.includes(index) ? ["cpu.usage"] : []
        });
        const rewrite = index % 3 === 2;
        if (rewrite) {
          setTimeout(() => {
            document.body.dataset.animations = document.body.dataset.animations;
            document.documentElement.dataset.theme = document.documentElement.dataset.theme;
          }, 120);
        }
        const frame = { index, at, published, rewrite, unavailable: unavailable.includes(index), cpuHeader: null };
        // 帧到后 300 ms 记下 CPU 条头的读数文字（无读数时应是原因）。
        setTimeout(() => { frame.cpuHeader = cpuRow.querySelector(".resource-breakdown-header > span")?.textContent ?? null; }, 300);
        frames.push(frame);
      }
      await sleep(1100);
      sampling = false;
      const perFrame = frames.map((frame, index) => {
        const end = frame.at + Math.min(index < gaps.length ? gaps[index] : 1000, 1000);
        const window = samples.filter((s) => s.t >= frame.at && s.t < end);
        let positions = 0;
        for (let i = 1; i < window.length; i++) if (window[i].boundary !== window[i - 1].boundary) positions++;
        return { ...frame, at: Math.round(frame.at), positions };
      });
      const tops = samples.map((s) => s.top);
      return {
        frames: perFrame,
        topRange: Math.max(...tops) - Math.min(...tops),
        cpuAlwaysConnected: samples.every((s) => s.cpuConnected),
        cpuSameElement: cpuRow === document.querySelector('.resource-bar-track[data-metric-id="cpu.usage"]')?.closest(".resource-breakdown-item"),
        samples: samples.length
      };
    }, { gaps: replayGapsMs, unavailable: [...unavailableFrames] });
    halfFrames = await page.evaluate(async ({ gaps, durationMs }) => {
      const boundaryReader = (metricId) => {
        const canvas = document.querySelector(`.resource-bar-track[data-metric-id="${metricId}"] canvas`);
        const context = canvas.getContext("2d");
        const ratio = canvas.width / canvas.getBoundingClientRect().width;
        const overscan = Number.parseFloat(getComputedStyle(canvas.parentElement).getPropertyValue("--segment-paint-overscan")) * ratio;
        return { canvas, read() {
          const data = context.getImageData(0, Math.floor(canvas.height - overscan - 3 * ratio), canvas.width, 1).data;
          for (let x = 1; x < canvas.width; x++) {
            if (data[x * 4] !== data[0] || data[x * 4 + 1] !== data[1] || data[x * 4 + 2] !== data[2]) return x;
          }
          return -1;
        } };
      };
      const memory = boundaryReader("memory.usage");
      const cpu = boundaryReader("cpu.usage");
      memory.canvas.style.setProperty("--motion-bar", `${durationMs}ms`);
      const samples = [];
      let sampling = true;
      const sample = () => {
        if (!sampling) return;
        samples.push({ t: performance.now(), memory: memory.read(), cpu: cpu.read() });
        requestAnimationFrame(sample);
      };
      requestAnimationFrame(sample);
      const sleep = (ms) => new Promise((resolve) => setTimeout(resolve, ms));
      const frames = [];
      // 第一段停在第 1 代。代数 6,6,1,1,…：A 帧（偶数帧）物理内存 6,1,6…，B 帧 CPU 6,1,6…，各自每次都变，另一条冻结。
      for (let index = 0; index <= gaps.length; index++) {
        if (index > 0) await sleep(gaps[index - 1]);
        const group = index % 2 === 0 ? "A" : "B";
        const generation = [6, 6, 1, 1][index % 4];
        const at = performance.now();
        await fetch("/__test/subscriptions/publish", {
          method: "POST",
          headers: { "content-type": "application/json" },
          body: JSON.stringify({ path: "/api/resource-monitor/subscribe", patch: {
            resourceBreakdownGeneration: generation,
            resourceFrozenBarIds: group === "A" ? ["cpu.usage"] : ["memory.usage"]
          } })
        });
        frames.push({ index, group, at });
      }
      await sleep(durationMs + 300);
      sampling = false;
      memory.canvas.style.removeProperty("--motion-bar");
      const values = samples.map((s) => s.memory);
      const travel = Math.max(...values) - Math.min(...values);
      let maxStep = 0;
      for (let i = 1; i < samples.length; i++) maxStep = Math.max(maxStep, Math.abs(samples[i].memory - samples[i - 1].memory));
      const aFrames = frames.filter((frame) => frame.group === "A");
      const legs = aFrames.map((frame, k) => {
        const end = k + 1 < aFrames.length ? aFrames[k + 1].at : frame.at + durationMs;
        const window = samples.filter((s) => s.t >= frame.at + 50 && s.t < end);
        let positions = 0;
        let direction = 0;
        let reversals = 0;
        for (let i = 1; i < window.length; i++) {
          const step = Math.sign(window[i].memory - window[i - 1].memory);
          if (step === 0) continue;
          positions++;
          if (direction !== 0 && step !== direction) reversals++;
          direction = step;
        }
        const crossings = frames.filter((other) => other.group === "B" && other.at > frame.at && other.at < end).length;
        return { index: frame.index, positions, reversals, crossings };
      });
      const cpuFrames = frames.filter((frame) => frame.group === "B").map((frame) => {
        const next = frames.find((other) => other.group === "B" && other.at > frame.at);
        const window = samples.filter((s) => s.t >= frame.at && s.t < Math.min(frame.at + 900, next ? next.at : Infinity));
        let positions = 0;
        for (let i = 1; i < window.length; i++) if (window[i].cpu !== window[i - 1].cpu) positions++;
        return { index: frame.index, positions };
      });
      return { travel, maxStep, legs, cpuFrames, samples: samples.length };
    }, { gaps: halfFrameGapsMs, durationMs: halfFrameMemoryDurationMs });
  } finally {
    await setScenario(page, baseUrl, { manualPublishPaths: [], resourceUnavailableBarIds: [], resourceFrozenBarIds: [], resourceBreakdownGeneration: 0 });
  }

  const missed = result.frames.filter((frame) => frame.published < 1);
  assert(missed.length === 0, `A replayed frame had no bar subscriber to publish to: ${JSON.stringify(missed)}`);
  const still = result.frames.filter((frame) => frame.positions < minimumPositions);
  assert(still.length === 0,
    `A resource bar frame at the real cadence did not animate (fewer than ${minimumPositions} positions): ${JSON.stringify(still)} of ${JSON.stringify(result.frames)}`);
  assert(result.topRange <= 0.5, `The memory bar row moved while a bar above it had no reading: ${result.topRange}px`);
  assert(result.cpuAlwaysConnected && result.cpuSameElement, `The CPU bar row was removed or replaced while it had no reading: ${JSON.stringify(result)}`);
  const reasonText = zhMonitorCopy.resourceBreakdown.unavailableReason.sourceUnavailable;
  const reasonFrames = result.frames.filter((frame) => frame.unavailable);
  assert(reasonFrames.length === 2 && reasonFrames.every((frame) => frame.cpuHeader?.includes(reasonText)),
    `A no-reading CPU bar did not show its reason "${reasonText}" in the header: ${JSON.stringify(reasonFrames.map((frame) => frame.cpuHeader))}`);
  const recovered = result.frames.filter((frame) => frame.index > Math.max(...unavailableFrames));
  assert(recovered.every((frame) => frame.cpuHeader !== null && !frame.cpuHeader.includes(reasonText)),
    `The reason stayed in the CPU header after its reading came back: ${JSON.stringify(recovered.map((frame) => frame.cpuHeader))}`);
  // 半帧：物理内存每一段（两个 A 帧之间，中间夹着它没变的 B 帧）都在动、不回头（B 帧没让它重来），
  // 任何一帧的跳动都不超过总行程的 35%（没有跳到末帧，下一个 A 帧从当前位置接着走）；CPU 每个 B 帧都有过渡。
  // 最后一个 A 帧后面不再有 B 帧，只查前面的段。
  assert(halfFrames.legs.length >= 7 && halfFrames.legs.slice(0, -1).every((leg) => leg.crossings >= 1),
    `A memory leg had no unchanged-bar frame inside it, so the half-frame case was not exercised: ${JSON.stringify(halfFrames.legs)}`);
  const flatLegs = halfFrames.legs.filter((leg) => leg.positions < minimumPositions || leg.reversals > 0);
  assert(flatLegs.length === 0,
    `A frame where the memory bar was unchanged restarted or stopped its running transition: ${JSON.stringify(flatLegs)} of ${JSON.stringify(halfFrames.legs)}`);
  assert(halfFrames.travel > 20 && halfFrames.maxStep <= 0.35 * halfFrames.travel,
    `The memory bar snapped instead of moving on from its animated position: max step ${halfFrames.maxStep}px of ${halfFrames.travel}px`);
  const stillCpu = halfFrames.cpuFrames.filter((frame) => frame.positions < minimumPositions);
  assert(stillCpu.length === 0, `A half frame that changed the CPU bar did not animate it: ${JSON.stringify(stillCpu)}`);
  assert(errors.length === 0, `Page errors: ${errors.join("; ")}`);
  return {
    frames: result.frames.map(({ index, positions, rewrite, unavailable }) => ({ index, positions, rewrite, unavailable })),
    topRange: result.topRange,
    halfFrames: { legs: halfFrames.legs, cpuFrames: halfFrames.cpuFrames, travel: halfFrames.travel, maxStep: halfFrames.maxStep }
  };
}

async function setScenario(page, baseUrl, patch) {
  const response = await page.request.post(`${baseUrl}/__test/scenario`, { data: patch });
  assert(response.ok(), `Fixture setup failed: ${response.status()}`);
}

function assert(condition, message) {
  if (!condition) throw new Error(message);
}
