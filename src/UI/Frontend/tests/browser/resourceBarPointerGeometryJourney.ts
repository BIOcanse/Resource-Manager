import type { Page } from "playwright-core";
import { crowdedScenario } from "./crowdedResourceBarsFixture.ts";
import { pointedSegment, trackContent, waitFor } from "./resourceBarPointer.ts";

/**
 * 资源条的指针位置和选中的段对得上（用户：「选中和实际鼠标位置产生偏移」「系统驱动保留无法被选中」），
 * 按后端实际形状的 CPU 条和物理内存条（crowdedResourceBarsFixture.ts：一串细段后跟系统/驱动保留和空余），
 * 显示缩放 125% 和 150%（deviceScaleFactor 1.25、1.5）各查一遍：
 * 1. 沿每条等距取 80 个点悬停，指针落到的段画出来的范围（交互层的布局，和画布同一份）离指针至多 6 px（细段借给命中的宽度）；
 * 2. 画布上保留量和空余的分界（最右边空余颜色开始的地方）就在布局里保留量的右边界上（差不到 2 个设备像素），
 *    画出来的位置和命中用的是同一个位置，往右也不累积偏差；
 * 3. 点保留量画出来的中间，选中的是保留量。
 */
export default async function resourceBarPointerGeometryJourney(page: Page, baseUrl: string) {
  const response = await page.request.post(`${baseUrl}/__test/scenario`, { data: crowdedScenario });
  if (!response.ok()) throw new Error(`Fixture setup failed: ${response.status()}`);
  const results = [];
  for (const deviceScaleFactor of [1.25, 1.5]) {
    const context = await page.context().browser()!.newContext({ locale: "zh-CN", viewport: { width: 1100, height: 800 }, deviceScaleFactor });
    try {
      const scaled = await context.newPage();
      await scaled.goto(baseUrl, { waitUntil: "domcontentloaded" });
      for (const [metricId, residualId] of [["cpu.usage", "resource-residual:cpu.usage"], ["memory.usage", "resource-residual:memory.usage"]]) {
        const track = scaled.locator(`.resource-bar-track[data-metric-id="${metricId}"]`);
        await track.waitFor({ timeout: 15_000 });
        await track.scrollIntoViewIfNeeded();
        await waitFor(async () => await scaled.evaluate((id) => {
          const canvas = document.querySelector<HTMLCanvasElement>(`.resource-bar-track[data-metric-id="${id}"] canvas`)!;
          const track = canvas?.closest(".resource-bar-track");
          return canvas instanceof HTMLCanvasElement && Number(canvas.dataset.resourceSegmentCount) >= 120 && !track!.classList.contains("moving")
            || (canvas as HTMLCanvasElement | null)?.dataset.resourceSegmentCount;
        }, metricId), `${metricId} paints the backend-shaped rows at ${deviceScaleFactor}x`);
        await scaled.waitForTimeout(300);
        const content = await trackContent(scaled, metricId);

        // 1. 指针落到的段画在指针附近。
        const offsets = [];
        let residual: Awaited<ReturnType<typeof pointedSegment>> = null;
        for (let index = 0; index < 80; index++) {
          const x = content.left + 1 + (content.width - 2) * (index + 0.5) / 80;
          await scaled.mouse.move(x, content.y);
          const pointed = await pointedSegment(scaled, metricId);
          if (!pointed || x < pointed.left - 6 || x > pointed.right + 6) offsets.push({ x: Math.round((x - content.left) * 10) / 10, pointed });
          if (pointed?.id === residualId) residual = pointed;
        }
        if (offsets.length) {
          throw new Error(`${metricId} at ${deviceScaleFactor}x: the pointer lands on segments drawn more than 6 px away: ${JSON.stringify(offsets.slice(0, 6))}`);
        }
        if (!residual) throw new Error(`${metricId} at ${deviceScaleFactor}x: no sampled point lands on the reserved segment`);

        // 2. 画布上保留量的右边界（空余开始的地方）和布局的右边界一致。
        const paintedRight = await scaled.evaluate((id) => {
          const canvas = document.querySelector<HTMLCanvasElement>(`.resource-bar-track[data-metric-id="${id}"] canvas`)!;
          const rect = canvas.getBoundingClientRect();
          // 取靠近段底部的一行：段名画在垂直居中，空余中间的“空余”二字会被当成分界。
          const overscan = Number.parseFloat(getComputedStyle(canvas.parentElement!).getPropertyValue("--segment-paint-overscan")) * canvas.width / rect.width || 0;
          const data = canvas.getContext("2d")!.getImageData(0, Math.floor(canvas.height - overscan - 3 * canvas.width / rect.width), canvas.width, 1).data;
          const same = (x: number, y: number) => [0, 1, 2, 3].every((offset) => Math.abs(data[x * 4 + offset] - data[y * 4 + offset]) <= 2);
          // 空余段最右边往右数第 4 个设备像素以内是空余颜色；从那里往左找到第一个不是空余颜色的像素。
          const empty = canvas.width - 4;
          let x = empty;
          while (x > 0 && same(x - 1, empty)) x -= 1;
          return { css: rect.left + x * rect.width / canvas.width, devicePixel: rect.width / canvas.width };
        }, metricId);
        if (Math.abs(paintedRight.css - residual.right) > 2 * paintedRight.devicePixel) {
          throw new Error(`${metricId} at ${deviceScaleFactor}x: the reserved segment is painted to ${paintedRight.css} but hit-tested to ${residual.right}`);
        }

        // 3. 点保留量的中间选中保留量。
        await scaled.mouse.click((residual.left + residual.right) / 2, content.y);
        await scaled.mouse.move(1, 1);
        await waitFor(async () => await track.locator('[role="option"][aria-selected="true"]').getAttribute("data-software-id").catch(() => null) === residualId
          || await track.locator('[role="option"][aria-selected="true"]').getAttribute("data-software-id").catch(() => null),
          `${metricId} at ${deviceScaleFactor}x: clicking the middle of the reserved segment selects it`);
        results.push({ deviceScaleFactor, metricId, residualPx: Math.round(residual.right - residual.left) });
      }
    } finally {
      await context.close();
    }
  }
  return { results };
}
