import { loadAppCopy } from "../../src/i18n/copy/appCopyLoader.ts";

/**
 * GPU 调度模型按后端的 hasDedicatedMemoryMetrics 显示三种状态（简体中文）。夹具里 gpu.0 每种情况下都有显存读数
 * （占用 50%），所以显示只能来自后端认定的能力，不能来自「有没有显存读数」：
 * - true：显存条显示 50.0%，GPU 96% 未到 99%、显存 50% 未到 70%，可调度；
 * - false：显存条写「共享」，只按 GPU 占用判断，可调度；
 * - null：显存条写「未知」，显存一项无从判断，GPU 也没满，所以是「暂无数据」。
 * 每种状态的显存条都有各自的说明（悬停提示）。
 */
export default async function gpuDedicatedMemoryStatesJourney(page, baseUrl) {
  const app = await loadAppCopy("zh-CN");
  const text = app.gpuScheduling;
  const cases = [
    { capability: true, state: "supported", vram: "50.0%", hint: text.dedicatedMemoryHint.supported, position: text.schedulable },
    { capability: false, state: "unsupported", vram: text.shared, hint: text.dedicatedMemoryHint.unsupported, position: text.schedulable },
    { capability: null, state: "unknown", vram: text.dedicatedMemoryUnknown, hint: text.dedicatedMemoryHint.unknown, position: text.noData }
  ];
  const results = [];
  for (const expected of cases) {
    await patchScenario(baseUrl, {
      optimizationRuntime: true,
      runtimeEffectOwners: true,
      gpuPressureFixture: true,
      unknownGpuScore: true,
      gpuOverflowUsagePercent: 99,
      gpuOverflowDedicatedMemoryPercent: 70,
      gpuDedicatedMemoryMetrics: expected.capability
    });
    await page.goto(`${baseUrl}/`, { waitUntil: "domcontentloaded" });
    await page.getByRole("navigation").getByRole("button", { name: "详细信息", exact: true }).click();
    await page.getByRole("tab", { name: "GPU 调度模型", exact: true }).click();
    await page.locator(".gpu-scheduling-panel .gpu-position").waitFor();
    try {
      await page.waitForFunction(({ state, vram, position }) => {
        const meter = document.querySelector(".gpu-pressure-grid .gpu-pressure-meter[data-dedicated-memory]");
        const values = [...document.querySelectorAll(".gpu-pressure-grid .gpu-pressure-meter strong")]
          .map((element) => element.textContent?.trim());
        return meter?.getAttribute("data-dedicated-memory") === state
          && values[0] === "96.0%" && values[1] === vram
          && document.querySelector(".gpu-position-main em")?.textContent?.trim() === position;
      }, expected, { timeout: 15_000 });
    } catch (error) {
      const actual = await page.evaluate(() => ({
        state: document.querySelector(".gpu-pressure-meter[data-dedicated-memory]")?.getAttribute("data-dedicated-memory"),
        values: [...document.querySelectorAll(".gpu-pressure-grid .gpu-pressure-meter strong")].map((element) => element.textContent?.trim()),
        position: document.querySelector(".gpu-position-main em")?.textContent?.trim()
      }));
      throw new Error(`hasDedicatedMemoryMetrics=${expected.capability}: ${JSON.stringify(actual)}; ${error}`);
    }
    const meter = page.locator(".gpu-pressure-grid .gpu-pressure-meter[data-dedicated-memory]");
    const hint = await meter.getAttribute("title");
    if (hint !== expected.hint) throw new Error(`hasDedicatedMemoryMetrics=${expected.capability}: hint ${JSON.stringify(hint)}`);
    results.push({ capability: expected.capability, state: expected.state, vram: expected.vram, position: expected.position });
  }
  if (new Set(results.map((result) => result.vram)).size !== 3) throw new Error("two capability states read alike");
  return results;
}

async function patchScenario(baseUrl, patch) {
  const response = await fetch(`${baseUrl}/__test/scenario`, {
    method: "POST",
    headers: { "Content-Type": "application/json" },
    body: JSON.stringify(patch)
  });
  if (!response.ok) throw new Error(`scenario patch failed: ${response.status} ${await response.text()}`);
}
