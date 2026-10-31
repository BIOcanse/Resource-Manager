import type { Request } from "playwright-core";
interface OverflowEvidence { className: string; text: string | null; cell: { left: number; right: number; top: number; bottom: number }; painted: { left: number; right: number; top: number; bottom: number } }
interface PlaceholderEvidence { reason?: string; row?: string | null; column?: string; card?: string; text?: string | null; className?: string; expected?: number; checked?: number; error?: string }
interface ResourceRow { rowIndex: number; name: string | null; length: number; ellipsized: boolean }
interface Inspection { overflow: OverflowEvidence[]; placeholders: PlaceholderEvidence[]; populatedCells: number; resourceRows?: ResourceRow[]; rowPoolSlots?: number; logicalBusinessRows?: number; rowElements?: number; pageText?: string }
interface SweepTask { start: number; duration: number }
interface SweepProbe { start: number; tasks: SweepTask[]; append(entries: PerformanceEntry[]): void; observer: PerformanceObserver }
declare global { interface Window { __realisticSweep: SweepProbe } }
interface TableReadiness { scrollAtMs: number; observedAtMs: number; before: { rowElements: number; populatedCells: number }; beforeTelemetry: unknown; waitError: string | null; afterTelemetry?: unknown }
type RowSweep = Awaited<ReturnType<typeof inspectAllResourceRows>>;
type SweepResult = Inspection & { page: string; scenario: string; seed: number; switch: string; rowCount: number; requests?: { atMs: number; path: string }[]; tableReadiness?: TableReadiness; rowSweep?: RowSweep; errors: string[]; budget: ReturnType<typeof assessInteractionLongTasks>; timing: { start: number; end: number; tasks: SweepTask[] } };
import type { Page } from "playwright-core";
import assert from "node:assert/strict";
import { writeFile } from "node:fs/promises";
import { resolve } from "node:path";
import { loadAppCopy } from "../../src/i18n/copy/appCopyLoader.ts";
import { assessInteractionLongTasks } from "./interactionLongTaskBudget.ts";
import { waitForNextPaint } from "./journeyWaits.ts";

const pages = [["monitor", "monitor"], ["details", "details"], ["control", "control"],
  ["software", "components"], ["disk", "diskUsage"], ["settings", "settings"], ["monitor", "monitor"]] as const;

export default async function realisticDataSweepJourney(page: Page, baseUrl: string) {
  const copy = await loadAppCopy("zh-CN");
  const results: SweepResult[] = [];
  const errors: string[] = [];
  const requests: { atMs: number; path: string }[] = [];
  const onRequest = (request: Request) => {
    if (new URL(request.url()).pathname === "/api/subscriptions/stream") {
      for (const subscription of (request.postDataJSON() as { subscriptions?: { path: string }[] } | null)?.subscriptions ?? []) {
        if (subscription.path.startsWith("/api/resource-facts/subscribe")) requests.push({ atMs: Date.now(), ...subscription });
      }
    }
  };
  page.on('request', onRequest);
  const onError = (error: Error) => errors.push(String(error));
  page.on("pageerror", onError);
  await page.setViewportSize({ width: 1280, height: 800 });
  try {
    for (const seed of [1, 17, 104729, 8675309]) {
      requests.length = 0;
      await page.request.post(`${baseUrl}/__test/scenario/reset`);
      const response = await page.request.post(`${baseUrl}/__test/scenario`, { data: { preset: "realistic", seed } });
      assert.ok(response.ok(), `scenario seed ${seed}: ${await response.text()}`);
      const scenario = await response.json() as { generatedData?: { names: string[] }; resourceTableRowCount: number };
      await page.goto(baseUrl, { waitUntil: "domcontentloaded" });
      const nav = page.getByRole("navigation", { name: copy.shell.pageNav, exact: true });
      await nav.waitFor();
      await page.waitForTimeout(1500);
      let from = "monitor";
      for (const [name, id] of pages) {
        const errorStart = errors.length;
        await page.evaluate(() => {
          const tasks: SweepTask[] = [];
          const append = (entries: PerformanceEntry[]) => { for (const entry of entries) tasks.push({ start: entry.startTime, duration: entry.duration }); };
          const observer = new PerformanceObserver(list => append(list.getEntries()));
          observer.observe({ type: "longtask", buffered: false });
          const probe = { start: performance.now(), tasks, append, observer };
          window.__realisticSweep = probe;
          window.addEventListener("click", () => { probe.start = performance.now(); }, { capture: true, once: true });
        });
        await nav.getByRole("button", { name: copy.page[id], exact: true }).click();
        await page.waitForTimeout(900);
        let tableReadiness: TableReadiness | undefined;
        if (name === "monitor") {
          await page.locator(".resource-table-panel").evaluate(element => element.scrollIntoView({ block: "start", behavior: "instant" }));
          const scrollAtMs = Date.now();
          await page.waitForTimeout(250);
          const before = await page.evaluate(() => ({ rowElements: document.querySelectorAll<HTMLElement>('#pageStage .resource-table-row.software-row[aria-hidden="false"]').length,
            populatedCells: document.querySelectorAll<HTMLElement>('#pageStage .resource-table-row.software-row[aria-hidden="false"] .resource-table-cell').length }));
          const beforeTelemetry = (await (await page.request.get(`${baseUrl}/__test/requests`)).json() as Record<string, unknown>)["/api/resource-facts/subscribe"];
          let waitError = null;
          await page.locator('.resource-table-row.software-row[aria-hidden="false"]').first()
            .waitFor({ timeout: 8000 }).catch(error => { waitError = String(error); });
          await waitForNextPaint(page);
          tableReadiness = { scrollAtMs, observedAtMs: Date.now(), before: { rowElements: before.rowElements, populatedCells: before.populatedCells },
            beforeTelemetry, waitError };
        }
        const timing = await page.evaluate(() => {
          const probe = window.__realisticSweep;
          const end = performance.now();
          probe.append(probe.observer.takeRecords()); probe.observer.disconnect();
          return { start: probe.start, end, tasks: probe.tasks };
        });
        const step = `primary ${from}→${name}`;
        const budget = from === name ? [] : assessInteractionLongTasks(timing.tasks, [{ step, start: timing.start, end: timing.end }]);
        if (name === "software") {
          await page.locator("#managementTab-other").click();
          await page.waitForTimeout(700);
        }
        const dom = await inspectData(page, scenario.generatedData?.names ?? [], name);
        const resourceRows = name === "monitor" ? await inspectAllResourceRows(page, scenario.generatedData?.names ?? []) : undefined;
        if (resourceRows) {
          dom.overflow.push(...resourceRows.overflow);
          dom.placeholders.push(...resourceRows.placeholders);
          if (tableReadiness!.waitError) dom.placeholders.push({ reason: "table never became ready", error: tableReadiness!.waitError! });
          tableReadiness!.afterTelemetry = (await (await page.request.get(`${baseUrl}/__test/requests`)).json() as Record<string, unknown>)["/api/resource-facts/subscribe"];
        }
        const result = { page: name, scenario: "realistic", seed, switch: step, rowCount: scenario.resourceTableRowCount,
          requests: name === "monitor" ? requests.slice() : undefined, tableReadiness, rowSweep: resourceRows,
          errors: errors.slice(errorStart), ...dom, budget, timing };
        results.push(result);
        process.stdout.write(`REALISTIC_DATA_SWEEP ${JSON.stringify(result)}\n`);
        from = name;
      }
    }
  } finally {
    page.off("pageerror", onError);
    page.off('request', onRequest);
    if (process.env.RM_REALISTIC_EVIDENCE_DIR) await writeFile(resolve(process.env.RM_REALISTIC_EVIDENCE_DIR,
      "realistic-data-sweep.json"), JSON.stringify({ results, errors }, null, 2));
  }
  const findings = results.flatMap(item => [
    ...item.errors.map(error => ({ page: item.page, seed: item.seed, kind: "pageerror", error })),
    ...item.overflow.map(evidence => ({ page: item.page, seed: item.seed, kind: "text-overflow", evidence })),
    ...item.placeholders.map(evidence => ({ page: item.page, seed: item.seed, kind: "placeholder", evidence })),
    ...item.budget.filter(entry => !entry.passed).map(evidence => ({ page: item.page, seed: item.seed, kind: "interaction-budget", evidence }))
  ]);
  assert.deepEqual(errors, [], "page errors across all seeds");
  assert.deepEqual(findings, [], JSON.stringify(findings));
  return { results };
}

async function inspectData(page: Page, names: string[], pageName: string): Promise<Inspection> {
  return page.evaluate(({ names, pageName }) => {
    const root = document.querySelector<HTMLElement>("#pageStage");
    if (!root || !root.textContent!.trim()) return { overflow: [], placeholders: [{ reason: "active page has no content" }], populatedCells: 0 };
    const overflow: OverflowEvidence[] = [];
    const placeholders: PlaceholderEvidence[] = [];
    let populatedCells = 0;
    const resourceRows: ResourceRow[] = [];
    const visible = (element: Element) => {
      const rect = element.getBoundingClientRect();
      return rect.width > 0 && rect.height > 0 && rect.bottom > 0 && rect.top < innerHeight
        && !element.closest('[aria-hidden="true"]') && getComputedStyle(element).visibility !== "hidden";
    };
    // Inspect text ranges against their actual cell and all clipping ancestors.
    // Ellipsis is valid; only text that can paint outside its cell is a finding.
    for (const cell of root.querySelectorAll<HTMLElement>('.resource-table-cell, td, [role="cell"], [role="gridcell"], .metric-value, .metric-label, .management-card-title, .software-item-name, .field-card-value, .control-reading')) {
      if (!visible(cell)) continue;
      const bounds = cell.getBoundingClientRect();
      const walker = document.createTreeWalker(cell, NodeFilter.SHOW_TEXT);
      let node: Node | null;
      while ((node = walker.nextNode())) {
        if (!node.textContent!.trim() || !visible(node.parentElement!)) continue;
        const range = document.createRange(); range.selectNodeContents(node);
        for (const rect of range.getClientRects()) {
          let left = rect.left, right = rect.right, top = rect.top, bottom = rect.bottom;
          let ancestor = node.parentElement;
          while (ancestor && cell.contains(ancestor)) {
            const style = getComputedStyle(ancestor), box = ancestor.getBoundingClientRect();
            if (["hidden", "clip", "auto", "scroll"].includes(style.overflowX)) { left = Math.max(left, box.left); right = Math.min(right, box.right); }
            if (["hidden", "clip", "auto", "scroll"].includes(style.overflowY)) { top = Math.max(top, box.top); bottom = Math.min(bottom, box.bottom); }
            ancestor = ancestor.parentElement;
          }
          if (right > left && bottom > top && (left < bounds.left - 1 || right > bounds.right + 1 || top < bounds.top - 1 || bottom > bounds.bottom + 1)) {
            overflow.push({ className: cell.className, text: node.textContent, cell: { left: bounds.left, right: bounds.right, top: bounds.top, bottom: bounds.bottom }, painted: { left, right, top, bottom } });
          }
        }
      }
    }
    for (const row of root.querySelectorAll<HTMLElement>('.resource-table-row.software-row[aria-hidden="false"]')) {
      if (!visible(row) || !names.some(name => row.getAttribute("aria-label") === name || row.textContent!.includes(name))) continue;
      const viewport = row.closest('.resource-table-viewport')?.getBoundingClientRect();
      const bounds = row.getBoundingClientRect();
      if (viewport && (bounds.bottom <= viewport.top || bounds.top >= viewport.bottom)) continue;
      const label = row.querySelector<HTMLElement>('.resource-row-name');
      resourceRows.push({ rowIndex: Number(row.getAttribute("aria-rowindex")), name: row.getAttribute("aria-label"),
        length: [...(row.getAttribute("aria-label") ?? "")].length, ellipsized: Boolean(label && label.scrollWidth > label.clientWidth) });
      for (const cell of row.querySelectorAll<HTMLElement>('.resource-table-cell')) {
        // CPU and memory have known observations in every generated resource fact.
        const header = row.closest('.resource-table-panel')?.querySelectorAll<HTMLElement>('[data-column-id]')[Number(cell.getAttribute("aria-colindex")) - 1];
        if (!header || !["cpu", "memory"].includes(header.dataset.columnId!)) continue;
        populatedCells++;
        if (!cell.textContent!.trim() || /^[—–-]+$/.test(cell.textContent!.trim())) placeholders.push({ row: row.getAttribute("aria-label"), column: header.dataset.columnId, text: cell.textContent });
      }
    }
    if (pageName === "monitor") {
      if (populatedCells === 0) placeholders.push({ reason: "generated CPU/memory facts exist but no populated table cells were observed" });
      for (const id of ["cpu", "memory"]) {
        const value = root.querySelector<HTMLElement>(`[data-card-id="${id}"] .metric-value`);
        if (value && visible(value) && (!value.textContent!.trim() || value.matches(".missing, .pending") || /^[—–-]+$/.test(value.textContent!.trim()))) {
          placeholders.push({ card: id, text: value.textContent, className: value.className });
        }
      }
    }
    if (pageName === "software" && names.length && !Array.from(root.querySelectorAll<HTMLElement>(".software-item")).some(item => names.some(name => item.textContent!.includes(name)))) {
      placeholders.push({ reason: "generated software exists but software list contains none of its names", text: root.textContent!.slice(0, 500) });
    }
    for (const element of root.querySelectorAll<HTMLElement>(".disk-usage-volume-name, .disk-usage-volume-size, .control-reading dd, .settings-row-value")) {
      if (!visible(element)) continue;
      if (!element.textContent!.trim() || /^[—–-]+$/.test(element.textContent!.trim())) placeholders.push({ className: element.className, text: element.textContent });
    }
    return { overflow, placeholders, populatedCells, resourceRows,
      rowPoolSlots: root.querySelectorAll<HTMLElement>('.resource-table-row').length,
      logicalBusinessRows: Number(root.querySelector<HTMLElement>('.resource-table-frame')?.getAttribute('aria-rowcount') ?? 2) - 2,
      rowElements: root.querySelectorAll<HTMLElement>('.resource-table-row.software-row[aria-hidden="false"]').length,
      pageText: root.innerText.slice(0, 1500) };
  }, { names, pageName });
}

async function inspectAllResourceRows(page: Page, names: string[]) {
  const viewport = page.locator('.resource-table-viewport');
  const extent = await viewport.evaluate(element => ({ height: element.clientHeight, max: element.scrollHeight - element.clientHeight }));
  const rows = new Map<number, ResourceRow>();
  const overflow: OverflowEvidence[] = [], placeholders: PlaceholderEvidence[] = [];
  const step = Math.max(1, Math.floor(extent.height * .75));
  const positions: number[] = [];
  for (let top = 0; top < extent.max; top += step) positions.push(top);
  positions.push(Math.max(0, extent.max));
  assert.ok(positions.length <= 512, "bounded resource-row sweep");
  for (const top of positions) {
    await viewport.evaluate((element, top) => { element.scrollTop = top; }, top);
    await waitForNextPaint(page);
    const sample = await inspectData(page, names, "monitor");
    for (const row of sample.resourceRows!) rows.set(row.rowIndex, row);
    overflow.push(...sample.overflow);
    placeholders.push(...sample.placeholders);
  }
  await viewport.evaluate(element => { element.scrollTop = 0; });
  await waitForNextPaint(page);
  const sample = await inspectData(page, names, "monitor");
  if (rows.size !== sample.logicalBusinessRows) placeholders.push({ reason: "not all logical resource rows were inspected", expected: sample.logicalBusinessRows, checked: rows.size });
  return { logicalBusinessRows: sample.logicalBusinessRows, checkedRows: rows.size,
    longNameRows: [...rows.values()].filter(row => row.length >= 24).length,
    ellipsizedRows: [...rows.values()].filter(row => row.ellipsized).length,
    longestNameLength: Math.max(0, ...[...rows.values()].map(row => row.length)), rows: [...rows.values()], overflow, placeholders };
}
