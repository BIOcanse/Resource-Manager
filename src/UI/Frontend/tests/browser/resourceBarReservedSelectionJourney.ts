import type { Page } from "playwright-core";
import { NOT_GHOST } from "./liveLocator.ts";
import { loadAppCopy } from "../../src/i18n/copy/appCopyLoader.ts";
import { formatBytes } from "../../src/presentation/byteUnits.ts";
import { crowdedMemory, crowdedScenario } from "./crowdedResourceBarsFixture.ts";
import { pointedSegment, trackContent, waitFor } from "./resourceBarPointer.ts";

/**
 * 物理内存条上“系统/驱动保留”的选中与悬停，按后端实际的形状（crowdedResourceBarsFixture.ts）：
 * 两个大软件、一百多个很小的服务（其中 WLAN AutoConfig 3.26 MiB），最后是画了约三分之一的保留量，带 14 个计数器分项。
 *
 * 1. 指针在保留量画出来的地方（左边紧挨小服务的至多 6 px 除外）悬停，提示是保留量、逐行列出 14 个分项；
 *    点它就选中保留量，展开面板写“14 个类别”，分项轨道有 14 项；明细订阅用 residualDetails=memory.usage 选它，processDetails 不写任何软件。
 * 2. 小服务那一串里逐像素悬停，指针落到的段都画在指针附近（至多差 6 px）；点其中一段选中它，
 *    再用键盘逐段走到不到 1 px 宽的 WLAN AutoConfig，面板展开它的进程（svchost.exe），明细订阅用 processDetails 选它。
 */
export default async function resourceBarReservedSelectionJourney(page: Page, baseUrl: string) {
  const app = await loadAppCopy("zh-CN");
  const { capacity, used, attributed, software, leaves, wlanId, residualId } = crowdedMemory;
  const response = await page.request.post(`${baseUrl}/__test/scenario`, { data: crowdedScenario });
  if (!response.ok()) throw new Error(`Fixture setup failed: ${response.status()}`);
  await page.setViewportSize({ width: 1100, height: 800 });
  await page.goto(baseUrl, { waitUntil: "domcontentloaded" });
  const item = page.locator(".resource-breakdown-item").filter({ has: page.locator('.resource-bar-track[data-metric-id="memory.usage"]') });
  const track = item.locator('.resource-bar-track[data-metric-id="memory.usage"]');
  await track.waitFor({ timeout: 15_000 });
  await track.scrollIntoViewIfNeeded();
  await waitFor(async () => await page.evaluate(() => {
    const canvas = document.querySelector<HTMLCanvasElement>('.resource-bar-track[data-metric-id="memory.usage"] canvas');
    return canvas instanceof HTMLCanvasElement && Number(canvas.dataset.resourceSegmentCount) >= 170 || canvas?.dataset.resourceSegmentCount;
  }), "The memory bar paints the backend-shaped rows");

  const reservedName = app.resourceTable.systemResidualRow(app.metricLabel["memory.usage"]);
  const copy = app.physicalMemoryResidual;
  const leafLines = leaves.map(([kind, bytes, tag]) => {
    const name = kind.startsWith("system-memory/nonpaged-pool/tag/") ? copy.grouped(copy.kinds.nonpagedPool, copy.kinds.poolTag(tag))
      : kind === "system-memory/nonpaged-pool/other-tags" ? copy.grouped(copy.kinds.nonpagedPool, copy.kinds.otherPoolTags)
        : kind === "system-memory/nonpaged-pool/unclassified" ? copy.grouped(copy.kinds.nonpagedPool, copy.kinds.unclassifiedPool)
          : kind === "system-memory/paged-pool-resident" ? copy.kinds.pagedPoolResident
            : kind === "system-memory/driver-resident" ? copy.kinds.driverResident
              : kind === "system-memory/code-resident" ? copy.kinds.codeResident : copy.kinds.unclassified;
    return `${name} ${formatBytes(bytes, "memory")}`;
  });
  // 保留量画在 [软件之和, 已用] / 容量 这一段。
  const geometry = await trackContent(page, "memory.usage");
  const drawnLeft = geometry.left + geometry.width * attributed / capacity;
  const drawnRight = geometry.left + geometry.width * used / capacity;
  // 退场中的提示是幽灵副本（liveLocator.ts），只看活的那一个。
  const tooltip = () => page.evaluate((selector) => {
    const element = document.querySelector(selector);
    return element ? {
      title: element.querySelector<HTMLElement>(".resource-tooltip-title")?.textContent ?? "",
      lines: [...element.querySelectorAll<HTMLElement>(".resource-tooltip-line")].map((line) => line.textContent)
    } : null;
  }, `.resource-tooltip${NOT_GHOST}`);

  // 1. 保留量画出来的地方都指到它自己（左边留出小服务借走的至多 6 px）。
  for (const fraction of [0.08, 0.3, 0.5, 0.75, 0.97]) {
    const x = drawnLeft + 6 + (drawnRight - drawnLeft - 6) * fraction;
    await page.mouse.move(x, geometry.y);
    await waitFor(async () => (await tooltip())?.title.startsWith(reservedName) || await tooltip(),
      `Hovering the drawn reserved segment at ${Math.round(x - geometry.left)} px of ${Math.round(geometry.width)} px shows the reserved segment`);
  }
  const hovered = (await tooltip())!;
  if (JSON.stringify(hovered.lines.slice(-leafLines.length)) !== JSON.stringify(leafLines)) {
    throw new Error(`The reserved tooltip does not list the 14 leaves in order: ${JSON.stringify(hovered.lines)}`);
  }
  await page.mouse.click((drawnLeft + drawnRight) / 2, geometry.y);
  await waitFor(async () => await item.locator(`[role="option"][data-software-id="${residualId}"][aria-selected="true"]`).count() === 1
    || await item.locator('[role="option"][aria-selected="true"]').getAttribute("data-software-id").catch(() => null),
    "Clicking the reserved segment selects it");
  const panel = item.locator(`.resource-process-panel${NOT_GHOST}`);
  await waitFor(async () => await panel.locator(".resource-process-header span").textContent().catch(() => null) === app.resourceBreakdown.categoryCount(14)
    || await panel.locator(".resource-process-header").textContent().catch(() => null), "The reserved panel counts 14 categories");
  if (await panel.locator(".resource-process-header strong").textContent() !== reservedName) {
    throw new Error(`The expanded panel is not the reserved segment: ${await panel.locator(".resource-process-header").textContent()}`);
  }
  const processTrack = panel.locator(".resource-process-track");
  // 明细帧到了，分项轨道才是可以键盘走的列表。
  await waitFor(async () => await processTrack.getAttribute("role") === "listbox" || await processTrack.getAttribute("role"), "The reserved leaves arrive");
  await processTrack.focus();
  await page.keyboard.press("End");
  const leafOption = panel.locator('.resource-process-segment[role="option"]');
  await waitFor(async () => await leafOption.getAttribute("aria-setsize") === "14" || await leafOption.getAttribute("aria-setsize"), "The reserved panel lays out 14 leaves");
  if (!(await leafOption.getAttribute("aria-label"))?.startsWith(`${copy.kinds.unclassified} (${app.resourceBreakdown.noPidCategory})`)) {
    throw new Error(`The last leaf is not the unclassified remainder: ${await leafOption.getAttribute("aria-label")}`);
  }
  const detailsQueries = await openQueries(baseUrl, "details");
  if (!detailsQueries.some((params) => listed(params, "residualDetails").includes("memory.usage") && listed(params, "processDetails").length === 0)) {
    throw new Error(`No details subscription selects the residual alone: ${detailsQueries.map(String)}`);
  }

  // 2. 小服务那一串：指针落到的段画在指针附近；键盘走到不到 1 px 的 WLAN AutoConfig。
  // 聚焦明细轨道可能滚动了页面：按现在的位置重新取轨道坐标。
  await track.scrollIntoViewIfNeeded();
  const now = await trackContent(page, "memory.usage");
  const runLeft = now.left + now.width * (software[0][4] + software[1][4]) / capacity;
  const runRight = now.left + now.width * attributed / capacity;
  const offsets: { x: number; pointed: Awaited<ReturnType<typeof pointedSegment>> }[] = [];
  for (let x = Math.ceil(runRight + 5); x >= Math.floor(runLeft - 5); x -= 1) {
    await page.mouse.move(x, now.y);
    const pointed = await pointedSegment(page, "memory.usage");
    if (!pointed || x < pointed.left - 6 || x > pointed.right + 6) offsets.push({ x: Math.round(x - now.left), pointed });
  }
  if (offsets.length) throw new Error(`The pointer lands on segments drawn more than 6 px away: ${JSON.stringify(offsets.slice(0, 8))}`);
  await page.mouse.click(Math.round((runLeft + runRight) / 2), now.y);
  await page.mouse.move(1, 1);
  const order = software.map((row) => row[0]);
  let selectedId: string | null = null;
  await waitFor(async () => {
    selectedId = await item.locator('[role="option"][aria-selected="true"]').getAttribute("data-software-id").catch(() => null);
    return order.includes(selectedId!) && selectedId !== order[0] && selectedId !== order[1] || selectedId;
  }, "Clicking inside the run of small services selects one of them");
  const step = order.indexOf(wlanId) > order.indexOf(selectedId!) ? "ArrowRight" : "ArrowLeft";
  for (let count = 0; count < order.length && selectedId !== wlanId; count++) {
    await page.keyboard.press(step);
    selectedId = await item.locator('[role="option"][aria-selected="true"]').getAttribute("data-software-id").catch(() => null);
  }
  if (selectedId !== wlanId) throw new Error(`The keyboard did not reach WLAN AutoConfig: ${selectedId}`);
  await waitFor(async () => await panel.locator(".resource-process-header strong").textContent().catch(() => null) === "WLAN AutoConfig"
    || await panel.locator(".resource-process-header").textContent().catch(() => null), "The tiny service's panel is expanded");
  await waitFor(async () => await processTrack.getAttribute("role") === "listbox" || await panel.innerText(), "The tiny service's process arrives");
  await processTrack.focus();
  await page.keyboard.press("Home");
  await waitFor(async () => (await leafOption.getAttribute("aria-label").catch(() => ""))?.startsWith("svchost.exe")
    || await panel.innerText().catch((error) => error.message), "The tiny service lists its process");
  await waitFor(async () => (await openQueries(baseUrl, "details")).some((params) => listed(params, "processDetails").includes(wlanId)
    && !listed(params, "residualDetails").includes("memory.usage")) || (await openQueries(baseUrl, "details")).map(String),
    "The details subscription selects the tiny service only");
  return { segments: software.length + 2, leaves: leaves.length };
}

async function openQueries(baseUrl: string, scope: string) {
  const telemetry = await (await fetch(`${baseUrl}/__test/requests`)).json() as Record<string, { inFlightByQuery: Record<string, number> }>;
  return Object.entries(telemetry["/api/resource-monitor/subscribe"]?.inFlightByQuery ?? {})
    .filter(([, count]) => count > 0).map(([query]) => new URLSearchParams(query)).filter((params) => params.get("scope") === scope);
}

function listed(params: URLSearchParams, name: string) {
  return params.getAll(name).flatMap((value) => value.split(",")).map((value) => value.trim()).filter(Boolean);
}
