import { NOT_GHOST } from "./liveLocator.js";
import { loadAppCopy } from "../../src/i18n/copy/appCopyLoader.ts";
import { formatBytes } from "../../src/presentation/byteUnits.ts";
import { formatPercent } from "../../src/utils.ts";

/**
 * 物理内存“系统/驱动保留”的分项与页帧明细（docs/runtime_process_attribution_and_io_network_telemetry.md，
 * Physical memory residual；简体中文）。
 *
 * 条订阅（scope=bars）为物理内存条带上 residualDetails=memory.usage，条上的保留量这一段的提示按后端顺序
 * 列出计数器分项的名称（池标记还原成四个字符，含不可打印字节的显示十六进制）。选中这一段就展开它：
 * 明细订阅（scope=details）同样用 residualDetails=memory.usage 选它；任何订阅都不把残差的软件 id 写进 processDetails。
 * 展开面板写“N 个类别”、分项不显示成进程（没有 PID），面板下方是页帧明细：
 * 就绪时列出各类别（非分页池下的标记归在一组）、未分类、保留量合计、已用和已归属的进程私有内存，并写出采集时间；
 * 按 Esc 收起后这条明细订阅随即释放。不可用的帧写出原因说明和代码；没有帧时写“尚未采集”。
 */
export default async function physicalMemoryResidualJourney(page, baseUrl) {
  const app = await loadAppCopy("zh-CN");
  const copy = app.physicalMemoryResidual;
  const GiB = 1024 ** 3;
  const residualId = "resource-residual:memory.usage";
  const bar = page.locator(".resource-breakdown-item").filter({ has: page.locator('.resource-bar-track[data-metric-id="memory.usage"]') });
  const track = bar.locator('.resource-bar-track[data-metric-id="memory.usage"]');
  const details = bar.locator(`.resource-residual-details${NOT_GHOST}`);
  const setScenario = async (patch) => {
    const response = await fetch(`${baseUrl}/__test/scenario`, { method: "POST", headers: { "Content-Type": "application/json" }, body: JSON.stringify(patch) });
    if (!response.ok) throw new Error(`Scenario ${JSON.stringify(patch)} was rejected`);
  };
  const everySubscription = async () => {
    const telemetry = await (await fetch(`${baseUrl}/__test/requests`)).json();
    return Object.entries(telemetry["/api/resource-monitor/subscribe"]?.inFlightByQuery ?? {})
      .map(([query, count]) => ({ params: new URLSearchParams(query), count }));
  };
  const subscriptions = async () => (await everySubscription()).filter(({ count }) => count > 0);
  const listed = (params, name) => params.getAll(name).flatMap((value) => value.split(",")).map((value) => value.trim()).filter(Boolean);
  const selectsResidual = (params) => listed(params, "residualDetails").includes("memory.usage");
  // 后端 processDetails 只认真实软件：残差的软件 id 写在那里什么也选不到，前端任何时候都不能这样请求。
  const assertNoResidualSoftwareId = async () => {
    const offending = (await everySubscription()).filter(({ params }) => listed(params, "processDetails").some((id) => id.toLowerCase().startsWith("resource-residual:")));
    if (offending.length) throw new Error(`A subscription names the residual software id in processDetails: ${offending.map(({ params }) => params.toString())}`);
  };
  const residualDetailsOpen = async () => (await subscriptions())
    .filter(({ params }) => params.get("scope") === "details" && selectsResidual(params))
    .reduce((sum, { count }) => sum + count, 0);
  const waitFor = async (check, what) => {
    const deadline = Date.now() + 15_000;
    let last;
    while (Date.now() < deadline) {
      last = await check().catch((error) => error.message);
      if (last === true) return;
      await page.waitForTimeout(150);
    }
    throw new Error(`${what}: ${JSON.stringify(last)}`);
  };
  // 页帧帧自己的采集时间（mock 的合成值，和外层明细帧的 capturedAt 不同），按页面同样的格式写出。
  const pfnCapturedAt = "2026-10-26T05:59:50.000Z";
  const capturedAtText = async () => copy.panel.capturedAt(await page.evaluate((value) => new Intl.DateTimeFormat("zh-CN", {
    year: "numeric", month: "2-digit", day: "2-digit", hour: "2-digit", minute: "2-digit", second: "2-digit"
  }).format(Date.parse(value)), pfnCapturedAt));
  const selectResidual = async () => {
    await track.focus();
    for (let step = 0; step < 12; step++) {
      if (await bar.locator(`[role="option"][data-software-id="${residualId}"][aria-selected="true"]`).count() === 1) return;
      await page.keyboard.press("ArrowRight");
    }
    throw new Error("The keyboard could not select the system/driver reserved segment");
  };

  await setScenario({ physicalMemoryResidual: "ready" });
  await page.goto(baseUrl, { waitUntil: "domcontentloaded" });
  await track.waitFor({ timeout: 15_000 });
  await track.scrollIntoViewIfNeeded();

  // 条订阅用 residualDetails 选保留量，processDetails 不写任何软件；收起状态下没有任何明细订阅。
  await waitFor(async () => (await subscriptions()).some(({ params }) => params.get("scope") === "bars"
    && JSON.stringify(listed(params, "residualDetails")) === JSON.stringify(["memory.usage"]) && listed(params, "processDetails").length === 0)
    || await subscriptions().then((items) => items.map(({ params }) => params.toString())),
    "Bars subscription with the residual leaves");
  await assertNoResidualSoftwareId();
  if (await residualDetailsOpen() !== 0) throw new Error("A residual details subscription was open before the segment was expanded");

  // 选中保留量这一段：提示列出分项的名称和大小，名称由前端按标识给出。
  await selectResidual();
  const option = bar.locator(`[role="option"][data-software-id="${residualId}"]`);
  const tooltip = await option.getAttribute("aria-label");
  const leafNames = [
    copy.grouped(copy.kinds.nonpagedPool, copy.kinds.poolTag("NtFC")),
    copy.grouped(copy.kinds.nonpagedPool, copy.kinds.poolTag("File")),
    copy.grouped(copy.kinds.nonpagedPool, copy.kinds.poolTag("01020304")),
    copy.grouped(copy.kinds.nonpagedPool, copy.kinds.otherPoolTags),
    copy.grouped(copy.kinds.nonpagedPool, copy.kinds.unclassifiedPool),
    copy.kinds.pagedPoolResident, copy.kinds.driverResident, copy.kinds.codeResident, copy.kinds.unclassified
  ];
  const leafBytes = [2.5, 0.5, 0.25, 0.25, 0.125, 1, 0.375, 0.125, 2.875].map((value) => value * GiB);
  const expectedLeaves = leafNames.map((name, index) => `${name} ${formatBytes(leafBytes[index], "memory")}`);
  const tooltipLines = (tooltip ?? "").split("\n");
  if (JSON.stringify(tooltipLines.slice(-expectedLeaves.length)) !== JSON.stringify(expectedLeaves)) {
    throw new Error(`The reserved segment tooltip does not list the leaves in order: ${JSON.stringify(tooltipLines)}`);
  }
  if (!tooltipLines[0].startsWith(app.resourceTable.systemResidualRow(app.metricLabel["memory.usage"]))) {
    throw new Error(`The reserved segment lost its name: ${tooltipLines[0]}`);
  }

  // 悬停时页面上看得到的提示浮层同样逐行列出分项：沿条移动指针，直到浮层的名字是保留量这一段。
  const box = await track.boundingBox();
  let hoverLines = null;
  for (let step = 0; step < 60 && hoverLines === null; step += 1) {
    await page.mouse.move(box.x + 2 + (box.width - 4) * step / 59, box.y + box.height / 2);
    await page.waitForTimeout(60);
    hoverLines = await page.evaluate((reservedName) => {
      const tooltip = document.querySelector(".resource-tooltip");
      const title = tooltip?.querySelector(".resource-tooltip-title")?.textContent ?? "";
      return title.startsWith(reservedName) ? [...tooltip.querySelectorAll(".resource-tooltip-line")].map((line) => line.textContent) : null;
    }, app.resourceTable.systemResidualRow(app.metricLabel["memory.usage"]));
  }
  if (hoverLines === null || JSON.stringify(hoverLines.slice(-expectedLeaves.length)) !== JSON.stringify(expectedLeaves)) {
    throw new Error(`The hover tooltip of the reserved segment does not list the leaves in order: ${JSON.stringify(hoverLines)}`);
  }
  await page.mouse.move(1, 1);

  // 展开：明细订阅打开，面板写类别数，分项不显示成进程。
  await waitFor(async () => await residualDetailsOpen() === 1 || await residualDetailsOpen(), "Residual details subscription opens once");
  const panel = bar.locator(".resource-process-panel");
  await panel.waitFor();
  const panelCount = () => panel.locator(".resource-process-header span").textContent();
  await waitFor(async () => await panelCount() === app.resourceBreakdown.categoryCount(expectedLeaves.length) || await panelCount(),
    "The panel counts the leaves as categories, not processes");
  const panelText = await panel.innerText();
  if (/PID\s*-1|PID -1/.test(panelText) || panelText.includes(app.resourceBreakdown.processCount(expectedLeaves.length))) {
    throw new Error(`A leaf is shown as a process: ${panelText}`);
  }
  const processTrack = panel.locator(".resource-process-track");
  await processTrack.focus();
  await page.keyboard.press("Home");
  const leafLabel = await panel.locator('.resource-process-segment[role="option"]').getAttribute("aria-label");
  if (!leafLabel?.startsWith(`${copy.grouped(copy.kinds.nonpagedPool, copy.kinds.poolTag("NtFC"))} (${app.resourceBreakdown.noPidCategory})`)) {
    throw new Error(`The first leaf is not named by its kind or is given a PID: ${leafLabel}`);
  }

  // 就绪的页帧明细：按帧原样列出。
  await details.waitFor();
  await waitFor(async () => await details.getAttribute("data-residual-state") === "ready" || await details.getAttribute("data-residual-state"), "Ready page frame breakdown");
  // 后端 r3 只在明细选了 memory.usage、物理私有内存度量或页帧项时附页帧帧；前端的明细订阅满足这个条件。
  const detailsQueries = (await subscriptions()).filter(({ params }) => params.get("scope") === "details" && selectsResidual(params));
  if (detailsQueries.length === 0) throw new Error("No details subscription selects the residual through residualDetails");
  if (!detailsQueries.every(({ params }) => params.getAll("ids").flatMap((value) => value.split(",")).includes("memory.usage")
      || params.getAll("measures").includes("process_all_private_working_set_actual"))) {
    throw new Error(`The residual details request selects no physical memory metric: ${detailsQueries.map(({ params }) => params.toString())}`);
  }
  // 采集时间是页帧帧自己的 capturedAt，不是外层明细帧的时间。
  const header = await details.locator(".resource-residual-details-header span").textContent();
  if (header !== await capturedAtText()) throw new Error(`The capture time is not the page frame's own: ${header}`);
  const rows = await details.locator("tbody tr").evaluateAll((elements) => elements.map((row) => ({
    kind: row.getAttribute("data-kind"), group: row.getAttribute("data-group"),
    cells: [...row.children].map((cell) => cell.textContent?.trim() ?? "")
  })));
  const kinds = rows.map((row) => row.kind ?? `group:${row.group}`);
  const expectedKinds = ["system-memory/mapped-file", "system-memory/shareable", "system-memory/page-table", "system-memory/paged-pool-resident",
    "system-memory/system-pte", "system-memory/metafile", "system-memory/driver-locked", "system-memory/kernel-stack",
    "system-memory/working-set-internals", "group:nonpaged-pool", "system-memory/nonpaged-pool/tag/4346744E",
    "system-memory/nonpaged-pool/tag/656C6946", "system-memory/nonpaged-pool/tag/01020304", "system-memory/nonpaged-pool/other-tags",
    "system-memory/nonpaged-pool/unclassified", "system-memory/modified", "system-memory/process-private-not-attributed", "unclassified"];
  if (JSON.stringify(kinds) !== JSON.stringify(expectedKinds)) throw new Error(`Page frame rows: ${JSON.stringify(kinds)}`);
  const row = (kind) => rows.find((item) => item.kind === kind).cells;
  if (JSON.stringify(row("system-memory/nonpaged-pool/tag/4346744E")) !== JSON.stringify([copy.kinds.poolTag("NtFC"), formatBytes(2.5 * GiB, "memory"), formatPercent(31.25)])) {
    throw new Error(`NtFC row: ${JSON.stringify(row("system-memory/nonpaged-pool/tag/4346744E"))}`);
  }
  if (row("system-memory/nonpaged-pool/tag/01020304")[0] !== copy.kinds.poolTag("01020304")) throw new Error("A nonprintable tag is not shown as its hex value");
  if (JSON.stringify(row("unclassified").slice(0, 2)) !== JSON.stringify([copy.kinds.unclassified, formatBytes(0.125 * GiB, "memory")])) {
    throw new Error(`Unclassified row: ${JSON.stringify(row("unclassified"))}`);
  }
  const groupRow = rows.find((item) => item.group === "nonpaged-pool").cells;
  if (JSON.stringify(groupRow) !== JSON.stringify([copy.kinds.nonpagedPool])) throw new Error(`Group row: ${JSON.stringify(groupRow)}`);
  const total = await details.locator('tfoot tr[data-kind="residual"]').evaluate((element) => [...element.children].map((cell) => cell.textContent?.trim() ?? ""));
  if (JSON.stringify(total.slice(0, 2)) !== JSON.stringify([copy.panel.residualRow, formatBytes(8 * GiB, "memory")])) throw new Error(`Residual total: ${JSON.stringify(total)}`);
  if (await details.locator('[data-total="used"]').textContent() !== formatBytes(12 * GiB, "memory")
    || await details.locator('[data-total="attributed-private"]').textContent() !== formatBytes(4 * GiB, "memory")) {
    throw new Error("Used or attributed private bytes are not shown as sent");
  }
  if (!(await details.innerText()).includes(copy.panel.identity)) throw new Error("The parts + unclassified = residual statement is missing");
  await assertNoResidualSoftwareId();

  // 收起：明细订阅释放，页帧明细消失。
  await page.keyboard.press("Escape");
  await waitFor(async () => await residualDetailsOpen() === 0 || await residualDetailsOpen(), "Residual details subscription released after Esc");
  await details.waitFor({ state: "detached" });

  // 改选另一段同样释放它。
  await selectResidual();
  await waitFor(async () => await residualDetailsOpen() === 1 || await residualDetailsOpen(), "Residual details subscription reopens");
  await track.focus();
  await page.keyboard.press("ArrowLeft");
  await waitFor(async () => await residualDetailsOpen() === 0 || await residualDetailsOpen(), "Residual details subscription released after selecting another segment");
  await page.keyboard.press("Escape");

  // 不可用：原因说明和原样的代码。
  await setScenario({ physicalMemoryResidual: "unavailable", physicalMemoryResidualReason: "profile-privilege:0xC0000061" });
  await selectResidual();
  await details.waitFor();
  await waitFor(async () => await details.getAttribute("data-residual-state") === "unavailable" || await details.getAttribute("data-residual-state"), "Unavailable page frame breakdown");
  const unavailableText = await details.locator(".resource-residual-details-state").innerText();
  for (const expected of [copy.panel.unavailable, copy.reasonFamilies.profilePrivilege("0xC0000061"), copy.panel.reasonCode("profile-privilege:0xC0000061")]) {
    if (!unavailableText.includes(expected)) throw new Error(`Unavailable text lacks ${expected}: ${unavailableText}`);
  }
  if (await details.locator("table").count() !== 0) throw new Error("An unavailable frame rendered a table");
  if (await details.locator(".resource-residual-details-header span").textContent() !== await capturedAtText()) {
    throw new Error("The unavailable frame does not show its own capture time");
  }
  // 清单以外的代码原样显示代码本身，不配猜测的说明。
  await setScenario({ physicalMemoryResidualReason: "future-reason" });
  const codeLine = details.locator(".resource-residual-details-code");
  await waitFor(async () => await codeLine.getAttribute("data-reason-code") === "future-reason" || await codeLine.getAttribute("data-reason-code"), "Unknown reason code");
  const unknownState = await details.locator(".resource-residual-details-state").evaluate((element) => [...element.children].map((child) => child.textContent));
  if (JSON.stringify(unknownState) !== JSON.stringify([copy.panel.unavailable, copy.panel.reasonCode("future-reason")])) {
    throw new Error(`An unknown code is not shown as itself: ${JSON.stringify(unknownState)}`);
  }
  await page.keyboard.press("Escape");
  await waitFor(async () => await residualDetailsOpen() === 0 || await residualDetailsOpen(), "Released after the unavailable state");

  // 没有帧：尚未采集，不是零。
  await setScenario({ physicalMemoryResidual: "absent" });
  await selectResidual();
  await details.waitFor();
  await waitFor(async () => await residualDetailsOpen() === 1 || await residualDetailsOpen(), "Details subscription for the absent frame");
  // 同一查询重新订阅时先显示上一次接受的值，新帧（没有 physicalMemoryResidual）到达后变成“尚未采集”。
  await waitFor(async () => await details.getAttribute("data-residual-state") === "not-collected" || await details.getAttribute("data-residual-state"),
    "A missing frame is shown as not yet collected");
  if (await details.locator(".resource-residual-details-state").textContent() !== copy.panel.notCollected) throw new Error("Not-collected copy is missing");
  if (await details.locator("table, .resource-residual-details-header span").count() !== 0) throw new Error("A missing frame shows readings or a capture time");
  await page.keyboard.press("Escape");
  await waitFor(async () => await residualDetailsOpen() === 0 || await residualDetailsOpen(), "Released after the absent state");

  // 资源列表“按进程”：保留量下面的分项不是进程，是归属行（attribution-row，不是 process-row），
  // 状态写保留量（与保留量那一行相同），不写“运行中”。
  await setScenario({ physicalMemoryResidual: "ready" });
  const table = page.locator(".resource-table-panel");
  await table.scrollIntoViewIfNeeded();
  await table.locator(".resource-table-mode-button", { hasText: app.resourceTable.modes.process }).click();
  const namedRows = (selector) => table.locator(selector).evaluateAll((rows, names) => rows
    .filter((row) => names.includes(row.getAttribute("aria-label")))
    .map((row) => ({ name: row.getAttribute("aria-label"), status: row.querySelector(".status-cell")?.textContent?.trim() ?? null })), leafNames);
  const leafRows = () => namedRows('.resource-table-row.attribution-row[aria-hidden="false"]');
  await waitFor(async () => (await leafRows()).length === leafNames.length || await leafRows(), "Residual leaves in the process view");
  const leafProcessRows = await namedRows(".resource-table-row.process-row");
  if (leafProcessRows.length !== 0) throw new Error(`Residual leaves are shown as process rows: ${JSON.stringify(leafProcessRows)}`);
  for (const { name, status } of await leafRows()) {
    if (status === app.status.state.running || status !== app.softwareKind.system_residual) {
      throw new Error(`Residual leaf ${name} shows status ${JSON.stringify(status)} in the process view`);
    }
  }

  await assertNoResidualSoftwareId();
  return { leaves: expectedLeaves.length, pfnRows: expectedKinds.length };
}
