import { NOT_GHOST } from "./liveLocator.js";
import { loadAppCopy } from "../../src/i18n/copy/appCopyLoader.ts";
import { crowdedCpu, crowdedMemory, crowdedScenario } from "./crowdedResourceBarsFixture.js";
import { selectedSegments, trackContent, waitFor } from "./resourceBarPointer.js";

/**
 * 资源条上选中和展开是同一件事（用户：「这个细化展开无法通过再点一次相同的关闭，只能一直开着」「系统驱动保留无法被选中」），
 * 按后端实际形状的 CPU 条和物理内存条（crowdedResourceBarsFixture.js）：
 * 1. 软件段、系统/驱动保留、空余各自：点它选中并展开（空余只有说明行，没有明细面板），再点一次取消选中、收起，明细订阅随之释放；
 *    键盘：Enter 取消、再按 Enter 选回同一段，空格同样来回一次，Esc 取消并收起。
 * 2. 点 CPU 条的保留量选中它、展开它；再点物理内存条的保留量，CPU 条的面板随之收起（同时只有一个选中、一个展开）；
 *    再点一次物理内存条的保留量，什么都不选、没有面板。
 */
export default async function resourceBarSelectionToggleJourney(page, baseUrl) {
  const app = await loadAppCopy("zh-CN");
  const response = await page.request.post(`${baseUrl}/__test/scenario`, { data: crowdedScenario });
  if (!response.ok()) throw new Error(`Fixture setup failed: ${response.status()}`);
  await page.setViewportSize({ width: 1100, height: 900 });
  await page.goto(baseUrl, { waitUntil: "domcontentloaded" });
  for (const metricId of ["cpu.usage", "memory.usage"]) {
    await waitFor(async () => await page.evaluate((id) => {
      const canvas = document.querySelector(`.resource-bar-track[data-metric-id="${id}"] canvas`);
      return canvas instanceof HTMLCanvasElement && Number(canvas.dataset.resourceSegmentCount) >= 120 || canvas?.dataset.resourceSegmentCount;
    }, metricId), `${metricId} paints the backend-shaped rows`);
  }
  await page.locator(".resource-breakdown-panel").scrollIntoViewIfNeeded();

  const itemOf = (metricId) => page.locator(".resource-breakdown-item").filter({ has: page.locator(`.resource-bar-track[data-metric-id="${metricId}"]`) });
  const panelsOpen = () => page.locator(`.resource-process-panel${NOT_GHOST}`).evaluateAll((panels) => panels.map((panel) => ({
    metricId: panel.closest(".resource-breakdown-item")?.querySelector(".resource-bar-track")?.getAttribute("data-metric-id"),
    name: panel.querySelector(".resource-process-header strong")?.textContent ?? ""
  })));
  const selectionDetail = (metricId) => itemOf(metricId).locator(`.resource-selection-detail${NOT_GHOST}`);
  const state = async () => {
    await page.mouse.move(1, 1);
    return { selected: await selectedSegments(page), panels: await panelsOpen(), details: await detailsSubscriptions(baseUrl) };
  };
  const expectState = async (expected, what) => waitFor(async () => {
    const current = await state();
    const panelsMatch = JSON.stringify(current.panels) === JSON.stringify(expected.panels);
    const selectedMatch = JSON.stringify(current.selected) === JSON.stringify(expected.selected);
    const detailsMatch = expected.details === undefined || current.details === expected.details;
    return panelsMatch && selectedMatch && detailsMatch || current;
  }, what);
  // 段画出来的中点（布局：值 / 容量，和画布同一份）。
  const centre = async (metricId, fromFraction, toFraction) => {
    const content = await trackContent(page, metricId);
    return { x: content.left + content.width * (fromFraction + toFraction) / 2, y: content.y };
  };
  const memory = crowdedMemory;
  const memoryCentres = {
    software: () => centre("memory.usage", 0, memory.software[0][4] / memory.capacity),
    reserved: () => centre("memory.usage", memory.attributed / memory.capacity, memory.used / memory.capacity),
    empty: () => centre("memory.usage", memory.used / memory.capacity, 1)
  };
  const cpuAttributed = crowdedCpu.software.reduce((sum, row) => sum + row[4], 0);
  const cpuReservedCentre = () => centre("cpu.usage", cpuAttributed / 100, (cpuAttributed + crowdedCpu.residual) / 100);
  const reservedName = (metricId) => app.resourceTable.systemResidualRow(app.metricLabel[metricId]);
  const segments = [
    { name: "software", id: memory.software[0][0], panel: memory.software[0][1], at: memoryCentres.software },
    { name: "reserved", id: memory.residualId, panel: reservedName("memory.usage"), at: memoryCentres.reserved },
    { name: "empty", id: "__resource-empty:memory.usage", panel: null, at: memoryCentres.empty }
  ];
  const none = { selected: {}, panels: [], details: 0 };
  const opened = (segment) => ({ selected: { "memory.usage": segment.id }, panels: segment.panel === null ? [] : [{ metricId: "memory.usage", name: segment.panel }] });

  // 1. 点、再点；Enter、空格、Esc。
  for (const segment of segments) {
    let point = await segment.at();
    await page.mouse.click(point.x, point.y);
    await expectState(opened(segment), `Clicking the ${segment.name} segment selects and expands it`);
    if (segment.panel === null && await selectionDetail("memory.usage").count() !== 1) throw new Error("The free segment shows no selection line");
    point = await segment.at();
    await page.mouse.click(point.x, point.y);
    await expectState(none, `Clicking the selected ${segment.name} segment again deselects it and closes its details`);
    if (await selectionDetail("memory.usage").count() !== 0) throw new Error(`The ${segment.name} selection line stays after deselecting`);

    point = await segment.at();
    await page.mouse.click(point.x, point.y);
    await expectState(opened(segment), `Clicking the ${segment.name} segment before the keyboard toggles`);
    await itemOf("memory.usage").locator(".resource-bar-track").focus();
    for (const key of ["Enter", "Space"]) {
      await page.keyboard.press(key);
      await expectState(none, `${key} on the selected ${segment.name} segment deselects it`);
      await page.keyboard.press(key);
      await expectState(opened(segment), `${key} again selects the same ${segment.name} segment`);
    }
    await page.keyboard.press("Escape");
    await expectState(none, `Escape deselects the ${segment.name} segment and closes its details`);
  }

  // 2. 两条资源条的保留量：只有一个选中、一个展开。
  let point = await cpuReservedCentre();
  await page.mouse.click(point.x, point.y);
  await expectState({ selected: { "cpu.usage": crowdedCpu.residualId }, panels: [{ metricId: "cpu.usage", name: reservedName("cpu.usage") }] },
    "Clicking the reserved segment of the CPU bar selects and expands it");
  point = await memoryCentres.reserved();
  await page.mouse.click(point.x, point.y);
  await expectState({ selected: { "memory.usage": memory.residualId }, panels: [{ metricId: "memory.usage", name: reservedName("memory.usage") }] },
    "Selecting the memory reserved segment closes the CPU bar's panel");
  point = await memoryCentres.reserved();
  await page.mouse.click(point.x, point.y);
  await expectState(none, "Clicking the memory reserved segment again leaves nothing selected or expanded");
  point = await cpuReservedCentre();
  await page.mouse.click(point.x, point.y);
  await expectState({ selected: { "cpu.usage": crowdedCpu.residualId }, panels: [{ metricId: "cpu.usage", name: reservedName("cpu.usage") }] },
    "Clicking the CPU reserved segment selects it again");
  point = await cpuReservedCentre();
  await page.mouse.click(point.x, point.y);
  await expectState(none, "Clicking the CPU reserved segment again deselects it");
  return { segments: segments.length + 1 };
}

async function detailsSubscriptions(baseUrl) {
  const telemetry = await (await fetch(`${baseUrl}/__test/requests`)).json();
  return Object.entries(telemetry["/api/resource-monitor/subscribe"]?.inFlightByQuery ?? {})
    .filter(([query, count]) => count > 0 && new URLSearchParams(query).get("scope") === "details")
    .reduce((sum, [, count]) => sum + count, 0);
}
