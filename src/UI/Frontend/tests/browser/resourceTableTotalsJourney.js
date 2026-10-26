import { loadAppCopy } from "../../src/i18n/copy/appCopyLoader.ts";

export default async function resourceTableTotalsJourney(page, baseUrl) {
  const app = await loadAppCopy("zh-CN");
  const response = await page.request.post(`${baseUrl}/__test/scenario`, { data: {
    gpuPressureFixture: true, resourceTableAccountingFixture: true, resourceTableRowCount: 2
  } });
  if (!response.ok()) throw new Error("Cannot prepare totals fixture");
  await page.setViewportSize({ width: 1500, height: 1000 });
  await page.goto(baseUrl, { waitUntil: "domcontentloaded" });
  const panel = page.locator(".resource-table-panel");
  await panel.scrollIntoViewIfNeeded();
  await panel.locator('.summary-row[aria-hidden="false"]').waitFor();
  const verify = async (mode) => {
    await page.waitForFunction(({ mode }) => {
      const root = document.querySelector(".resource-table-panel");
      const peers = root.querySelectorAll(`.${mode}-row[aria-hidden="false"]`);
      const cells = root.querySelectorAll('.summary-row[aria-hidden="false"] .value-cell');
      return peers.length >= 2 && cells.length === 6 && [...cells].every(cell => /^\d/.test(cell.textContent.trim()));
    }, { mode });
    const values = await panel.evaluate((root, mode) => {
      const number = (text) => {
        const match = text.trim().match(/^([\d.]+)\s*(.*)$/);
        if (!match) return null;
        const scale = { KiB: 1024, MiB: 1024 ** 2, GiB: 1024 ** 3, KB: 1000, MB: 1000 ** 2, GB: 1000 ** 3 }[match[2]] ?? 1;
        return Number(match[1]) * scale;
      };
      const read = row => [...row.querySelectorAll(".value-cell")].map(cell => number(cell.textContent));
      const summary = read(root.querySelector('.summary-row[aria-hidden="false"]'));
      const peers = [...root.querySelectorAll(`.${mode}-row[aria-hidden="false"], .attribution-row[aria-hidden="false"], .filtered-row[aria-hidden="false"]`)];
      return { summary, sums: summary.map((_, index) => peers.reduce((sum, row) => sum + (read(row)[index] ?? 0), 0)), count: peers.length };
    }, mode);
    for (let index = 0; index < values.summary.length; index++) {
      const total = values.summary[index];
      if (total === null || Math.abs(total - values.sums[index]) > Math.max(0.11, total * 0.0001)) {
        throw new Error(`${mode}: visible total mismatch in column ${index}: ${JSON.stringify(values)}`);
      }
    }
    return values;
  };
  const software = await verify("software");
  await panel.getByRole("searchbox").fill("示例软件");
  await panel.getByText(app.resourceTable.filteredRows, { exact: true }).waitFor();
  await verify("software");
  await panel.getByRole("searchbox").fill("no-match-anywhere");
  await panel.locator(":scope > .content-state").getByText(app.resourceTableView.noMatchTitle, { exact: true }).waitFor();
  // With no matching peers the table shows its empty state; retained accounting still closes.
  await panel.getByText(app.resourceTable.filteredRows, { exact: true }).waitFor({ state: "attached" });
  const hidden = await panel.locator('.filtered-row[aria-hidden="false"] .value-cell').allTextContents();
  const totals = await panel.locator('.summary-row[aria-hidden="false"] .value-cell').allTextContents();
  if (JSON.stringify(hidden) !== JSON.stringify(totals)) throw new Error(`Filtered rows lost values: ${JSON.stringify({ hidden, totals })}`);
  await panel.getByRole("searchbox").fill("");
  await panel.getByRole("radio", { name: app.resourceTable.modes.process, exact: true }).click();
  const process = await verify("process");
  // Round 4: the GPU0 VRAM no process owns is part of the residual entry, a PID -1 leaf keyed by entry and kind.
  const vramLeaf = panel.locator('.attribution-row[aria-hidden="false"]').filter({ hasText: app.resourceBreakdown.residualVram });
  await vramLeaf.waitFor();
  if (await vramLeaf.getAttribute("data-resource-row-id") !== "attribution:resource-residual:gpu.0.vram:system-residual:-1:") {
    throw new Error("The GPU0 residual leaf lost its backend identity");
  }
  // Automatic bars currently do not publish a window in ResourceFactsSnapshot; the UI must not invent one.
  const summaryTitles = await panel.locator('.summary-row[aria-hidden="false"] .value-cell').evaluateAll(cells => cells.map(cell => cell.title));
  if (summaryTitles.some(title => title.includes(app.resourceTable.factsWindow("2")))) throw new Error(`An automatic metric invented a sampling window: ${JSON.stringify(summaryTitles)}`);
  if ((await panel.locator('[aria-hidden="false"] .resource-table-cell').allTextContents()).some(text => text.trim() === "-1")) throw new Error("Placeholder PID leaked into output");
  await panel.getByRole("searchbox").fill("example-");
  const bucket = panel.getByRole("row", { name: app.resourceTable.filteredRows, exact: true, includeHidden: true });
  await bucket.waitFor();
  await verify("process");
  await panel.getByRole("searchbox").fill("no-match-anywhere");
  await panel.locator(":scope > .content-state").getByText(app.resourceTableView.noMatchTitle, { exact: true }).waitFor();
  if (await panel.locator('.filtered-row[aria-hidden="false"]').count() !== 1) throw new Error("Hidden values counted twice");
  const processHidden = await bucket.locator('.value-cell').allTextContents();
  const processTotals = await panel.locator('.summary-row[aria-hidden="false"] .value-cell').allTextContents();
  if (JSON.stringify(processHidden) !== JSON.stringify(processTotals)) throw new Error("Search lost standalone attributions");
  await panel.getByRole("searchbox").fill("");
  await bucket.waitFor({ state: "hidden" });


  await panel.getByRole("button", { name: app.resourceTableView.editLabel, exact: true }).click();
  // Sources and their names come from the backend catalog: the CPU field by its name, a GPU engine by its scope and class.
  const cpuField = "process_all_cpu_usage_actual";
  const engineField = "process_all_gpu_residency_attributed_gpu_0_luid_0000000000000017_physical_0_engine_1";
  const cpuSourceName = app.resourceTable.processFieldNames[cpuField];
  const engineSourceName = app.resourceTable.gpuEngineBusy(1, 0, app.resourceTable.gpuEngineClasses.Compute);
  const networkField = "process_all_network_receive_rate_attributed";
  const networkSourceName = app.resourceTable.processFieldNames[networkField];
  for (const [label, name] of [[app.resourceTable.columns.cpu, cpuSourceName], [app.resourceTable.gpuUsageLabel("0"), engineSourceName],
    [app.resourceTable.columns.network, networkSourceName]]) {
    await panel.getByRole("group", { name: app.resourceTableView.columnEditorLabel, exact: true }).getByRole("button", { name: label, exact: true }).click();
    const group = panel.getByRole("radiogroup", { name: app.resourceTable.columnSourceLabel(label), exact: true });
    await group.getByRole("radio", { name, exact: true }).click();
  }
  await panel.getByRole("button", { name: app.resourceTableView.saveLabel, exact: true }).click();
  await panel.locator(".resource-table-head-cell").filter({ hasText: engineSourceName }).waitFor();
  const telemetry = await (await fetch(`${baseUrl}/__test/requests`)).json();
  const items = new URLSearchParams(telemetry["/api/resource-facts/subscribe"].queries.at(-1)).getAll("items");
  for (const field of [cpuField, engineField, networkField]) {
    if (!items.includes(field)) throw new Error(`Column choice did not request ${field}: ${JSON.stringify(items)}`);
  }
  await verify("process");
  const sourcedTitles = await panel.locator('.summary-row[aria-hidden="false"] .value-cell').evaluateAll(cells => cells.map(cell => cell.title));
  if (sourcedTitles.filter(title => title.includes(app.resourceTable.factsWindow("2"))).length !== 1)
    throw new Error(`Only the selected network measure must show its own window: ${JSON.stringify(sourcedTitles)}`);

  // The same catalog entries must also work in bar choices and measures subscriptions.
  await page.getByRole("button", { name: app.resourceBreakdownView.editLabel, exact: true }).click();
  const gpuLabel = app.metricLabel["gpu.0.usage"] ?? app.resourceTable.gpuUsageLabel("0");
  await page.locator(".resource-bar-option").filter({ hasText: gpuLabel }).getByRole("checkbox").check();
  const barEditor = page.getByRole("group", { name: app.resourceBreakdownView.editorLabel, exact: true });
  await barEditor.getByRole("button", { name: app.metricLabel["cpu.usage"], exact: true }).click();
  const cpuSources = page.getByRole("radiogroup", { name: app.resourceTable.columnSourceLabel(app.metricLabel["cpu.usage"]), exact: true });
  await cpuSources.getByRole("radio", { name: cpuSourceName, exact: true }).click();
  await barEditor.getByRole("button", { name: gpuLabel, exact: true }).click();
  const gpuSources = page.getByRole("radiogroup", { name: app.resourceTable.columnSourceLabel(gpuLabel), exact: true });
  await gpuSources.getByRole("radio", { name: engineSourceName, exact: true }).click();
  await page.getByRole("button", { name: app.resourceBreakdownView.saveLabel, exact: true }).click();
  await page.locator(".resource-breakdown-header strong").filter({ hasText: cpuSourceName }).waitFor();
  await page.locator(".resource-breakdown-header strong").filter({ hasText: engineSourceName }).waitFor();
  const barRequests = await (await fetch(`${baseUrl}/__test/requests`)).json();
  const barQueries = barRequests["/api/resource-monitor/subscribe"].queries.filter(query => new URLSearchParams(query).get("scope") === "bars");
  const measures = new URLSearchParams(barQueries.at(-1)).getAll("measures");
  for (const field of [cpuField, engineField]) if (!measures.includes(field)) {
    throw new Error(`Bar source did not request ${field}: ${JSON.stringify(measures)}`);
  }
  // Automatic bars without a total publish an explicit unavailable fact with no process rows.
  await page.request.post(`${baseUrl}/__test/scenario`, { data: { resourceTableTotalsMissing: ["memory.usage", "gpu.0.vram"] } });
  const reasons = app.resourceTable.unavailableReasons;
  const columnCells = (label) => page.evaluate((label) => {
    const root = document.querySelector(".resource-table-panel");
    const heads = [...root.querySelectorAll(".resource-table-head-cell")];
    const index = heads.findIndex(head => head.textContent.trim().startsWith(label));
    return [...root.querySelectorAll('.resource-table-row[aria-hidden="false"]')].map(row => ({
      kind: row.classList.contains("summary-row") ? "summary" : row.classList.contains("attribution-row") ? "attribution" : "other",
      text: row.querySelectorAll(".resource-table-cell")[index]?.textContent.trim() ?? "",
      title: row.querySelectorAll(".resource-table-cell")[index]?.title ?? "" }));
  }, label);
  await page.waitForFunction(({ memory, vram, memoryReason, vramReason }) => {
    const root = document.querySelector(".resource-table-panel");
    const heads = [...root.querySelectorAll(".resource-table-head-cell")];
    const summary = root.querySelector('.summary-row[aria-hidden="false"]')?.querySelectorAll(".resource-table-cell") ?? [];
    const text = (label) => summary[heads.findIndex(head => head.textContent.trim().startsWith(label))]?.textContent.trim();
    return text(memory) === memoryReason && text(vram) === vramReason;
  }, { memory: app.resourceTable.columns.memory, vram: app.resourceTable.gpuVramLabel("0"),
    memoryReason: reasons["system-total-unavailable"].label, vramReason: reasons["gpu-dedicated-memory-total-unavailable"].label });
  const memoryCells = await columnCells(app.resourceTable.columns.memory);
  if (!memoryCells[0].title.includes(reasons["system-total-unavailable"].hint)) throw new Error("The unavailable total lost its reason sentence");
  if (memoryCells.slice(1).some(cell => /^\d/.test(cell.text))) throw new Error(`Numeric rows under an unavailable memory total: ${JSON.stringify(memoryCells)}`);
  const vramCells = await columnCells(app.resourceTable.gpuVramLabel("0"));
  if (!vramCells[0].title.includes(reasons["gpu-dedicated-memory-total-unavailable"].hint)) throw new Error("The shared-only GPU lost its reason sentence");
  if (vramCells.slice(1).some(cell => /^\d/.test(cell.text))) throw new Error(`Numeric rows under an unavailable GPU memory total: ${JSON.stringify(vramCells)}`);
  return { software, process, hidden, items };
}
