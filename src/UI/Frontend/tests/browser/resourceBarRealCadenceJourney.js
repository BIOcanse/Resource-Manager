import { seedFrontendPreferences } from "./frontendPreferencesFixture.js";

// 资源条按真机的帧间隔更新时每一帧都有过渡（混合数据 resourceSoftwareMix: "audit"）：
// - 资源条帧改成手动发（manualPublishPaths），按 10-26 抓到的已安装 .23 版本的真实帧间隔回放 13 帧
//   （real-wire/20261026T001848Z 第 10–17 和 60–63 个间隔，含最长的 2084 ms 和最短的 934 ms）；
//   前端在帧到达时开始过渡，不按自己的 1 s 时钟，所以间隔不齐也要每帧都动；
// - 每第三帧在过渡开始后约 120 ms 把外观属性原样再写一遍（同一时刻到达的自调度流会这样写），过渡不能被打断；
// - 第 6、7 帧 CPU 条按后端的“无读数”形状发（总量为空、没有软件、带原因），它这一行不卸载、不消失，
//   下面物理内存那一行的位置一点都不动。
// 其他订阅照常按 1 s 对齐发帧，和回放的帧错开落在过渡里。
const replayGapsMs = [1543, 1901, 1428, 1418, 1827, 1764, 2084, 1190, 1702, 934, 1178, 1177];
const unavailableFrames = new Set([5, 6]);
const minimumPositions = 5;

export default async function resourceBarRealCadenceJourney(page, baseUrl) {
  const errors = [];
  page.on("pageerror", (error) => errors.push(String(error)));
  await setScenario(page, baseUrl, { resourceSoftwareMix: "audit", resourceBreakdownGeneration: 6, resourceUnavailableBarIds: [] });
  await seedFrontendPreferences(page, (value) => {
    value.appearance.theme = "light";
    value.appearance.animations = "normal";
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
            document.body.dataset.selfCpuGrade = document.body.dataset.selfCpuGrade;
            document.documentElement.dataset.theme = document.documentElement.dataset.theme;
          }, 120);
        }
        frames.push({ index, at, published, rewrite, unavailable: unavailable.includes(index) });
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
  } finally {
    await setScenario(page, baseUrl, { manualPublishPaths: [], resourceUnavailableBarIds: [], resourceBreakdownGeneration: 0 });
  }

  const missed = result.frames.filter((frame) => frame.published < 1);
  assert(missed.length === 0, `A replayed frame had no bar subscriber to publish to: ${JSON.stringify(missed)}`);
  const still = result.frames.filter((frame) => frame.positions < minimumPositions);
  assert(still.length === 0,
    `A resource bar frame at the real cadence did not animate (fewer than ${minimumPositions} positions): ${JSON.stringify(still)} of ${JSON.stringify(result.frames)}`);
  assert(result.topRange <= 0.5, `The memory bar row moved while a bar above it had no reading: ${result.topRange}px`);
  assert(result.cpuAlwaysConnected && result.cpuSameElement, `The CPU bar row was removed or replaced while it had no reading: ${JSON.stringify(result)}`);
  assert(errors.length === 0, `Page errors: ${errors.join("; ")}`);
  return { frames: result.frames.map(({ index, positions, rewrite, unavailable }) => ({ index, positions, rewrite, unavailable })), topRange: result.topRange };
}

async function setScenario(page, baseUrl, patch) {
  const response = await page.request.post(`${baseUrl}/__test/scenario`, { data: patch });
  assert(response.ok(), `Fixture setup failed: ${response.status()}`);
}

function assert(condition, message) {
  if (!condition) throw new Error(message);
}
