import type { Page } from "playwright-core";
interface RequestTelemetry {
  started: number; completed: number; aborted: number; inFlight: number; maxInFlight: number;
  logicalStarted: number; logicalInFlight: number; logicalRetired: number; publications: number;
  physicalStarted: number; physicalInFlight: number; physicalCompleted: number; physicalAborted: number; physicalMaxInFlight: number;
  queries: string[]; subscriptionSets: string[][]; inFlightByQuery: Record<string, number>;
}
type Telemetry = Record<string, RequestTelemetry>;

declare global { var __resourceManagerScrollSamples: number[] | undefined; }
import { waitForCondition, waitForStableRendering } from "./journeyWaits.ts";

export default async function resourceTableScrollStabilityJourney(
  page: Page,
  baseUrl = "http://127.0.0.1:4177"
) {
  page.on("console", message => { if (message.type() === "error") process.stderr.write(`Table browser console: ${message.text()}\n`); });
  const factSubscriptions: { id: string; path: string }[] = [];
  page.on("request", request => {
    if (new URL(request.url()).pathname !== "/api/subscriptions/stream") return;
    const body = request.postDataJSON() as { subscriptions?: { id: string; path: string }[] } | null;
    for (const subscription of body?.subscriptions ?? []) {
      const url = new URL(subscription.path, baseUrl);
      if (url.pathname !== "/api/resource-facts/subscribe") continue;
      for (const key of ["columns", "sort", "mode", "expanded", "scope", "modes"]) {
        if (url.searchParams.has(key)) throw new Error(`Frontend table state crossed into backend demand: ${subscription.path}`);
      }
      factSubscriptions.push({ id: subscription.id, path: subscription.path });
    }
  });
  const scenario = await page.request.post(`${baseUrl}/__test/scenario`, {
    data: {
      resourceTableVariant: "many",
      resourceTableRowCount: 80
    }
  });
  if (!scenario.ok()) {
    throw new Error(`Unable to configure resource-table fixture: ${scenario.status()}`);
  }

  await page.setViewportSize({ width: 1067, height: 667 });
  await page.goto(`${baseUrl}/`, { waitUntil: "domcontentloaded" });
  // 资源条区在上、资源表在下，数据都跟可见区域走；资源条的加载占位和有数据时一样高，资源表一开始在视野外。
  // 先等资源条有数据，再把资源表滚进视野等它的行（资源条离开视野后保留数据）。
  await page.locator(".resource-breakdown-item").first().waitFor({ state: "visible", timeout: 7000 });
  await page.locator(".resource-table-panel").scrollIntoViewIfNeeded();
  await page.locator(".resource-table-frame").waitFor({ state: "visible", timeout: 7000 });
  await page.locator('.resource-table-row.software-row[aria-hidden="false"]').first().waitFor({ timeout: 7000 });
  await waitForCondition(page, () => factSubscriptions.length > 0, { timeoutMs: 7000, description: "Initial facts subscription" });
  await waitForStableRendering(page, ["main.app-shell", ".monitor-work-region", ".resource-table-frame", ".resource-breakdown-item"], { timeoutMs: 7000 });

  const before = await page.evaluate(() => {
    const viewport = document.querySelector<HTMLElement>("main.app-shell")!;
    const sortButton = [...document.querySelectorAll<HTMLElement>(".resource-table-sort-button")]
      .find((element) => element.textContent?.trim().startsWith("名称"));
    if (!(viewport instanceof HTMLElement)) {
      throw new Error("Main application viewport is missing.");
    }
    if (!(sortButton instanceof HTMLElement)) {
      throw new Error("Name sort button is missing.");
    }
    const viewportTop = viewport.getBoundingClientRect().top;
    viewport.scrollTop += sortButton.getBoundingClientRect().top - viewportTop - 120;
    const samples = [viewport.scrollTop];
    viewport.addEventListener("scroll", () => samples.push(viewport.scrollTop), { passive: true });
    globalThis.__resourceManagerScrollSamples = samples;
    return {
      top: viewport.scrollTop,
      height: viewport.scrollHeight,
      buttonTop: sortButton.getBoundingClientRect().top,
      viewportTop,
      regions: [...document.querySelectorAll<HTMLElement>(".monitor-work-region")]
        .map((element) => ({ label: element.getAttribute("aria-label"), height: element.getBoundingClientRect().height })),
      bars: [...document.querySelectorAll<HTMLElement>(".resource-breakdown-item")]
        .map((element) => element.getAttribute("data-column-id"))
    };
  });
  if (before.top < 100) {
    throw new Error(`Fixture did not create a scrollable monitor page: ${JSON.stringify(before)}`);
  }

  // 滚动让表格重新进入可见区域；先等可见性调度完成，再检查排序等纯呈现操作的订阅身份。
  const demandDeadline = Date.now() + 10000;
  let visibleDemand: Telemetry | undefined;
  while (Date.now() < demandDeadline) {
    visibleDemand = await (await page.request.get(`${baseUrl}/__test/requests`)).json() as Telemetry;
    if (visibleDemand["/api/resource-facts/subscribe"]?.logicalInFlight === 1
      && visibleDemand["/api/metrics/subscribe"]?.logicalInFlight === 0
      && visibleDemand["/api/metrics/fields/catalog/subscribe"]?.logicalInFlight === 0) break;
    await page.waitForTimeout(50);
  }
  if (visibleDemand?.["/api/resource-facts/subscribe"]?.logicalInFlight !== 1
    || visibleDemand?.["/api/metrics/subscribe"]?.logicalInFlight !== 0
    || visibleDemand?.["/api/metrics/fields/catalog/subscribe"]?.logicalInFlight !== 0) {
    throw new Error("Scrolling did not settle the visible table facts demand before the interaction baseline.");
  }
  const subscriptionIds = [...new Set(factSubscriptions.map(subscription => subscription.id))];
  const assertSameFactsLease = (action: string) => {
    if (factSubscriptions.some(subscription => !subscriptionIds.includes(subscription.id))) throw new Error(`${action} recreated the neutral facts lease.`);
  };
  const initialNames = await page.locator('.resource-table-row.software-row[aria-hidden="false"]').evaluateAll(rows => rows.map(row => row.getAttribute("aria-label")));
  await page.locator(".resource-table-sort-button", { hasText: "名称" }).click();
  await waitForNameSort(page, "descending", initialNames);
  await waitForStableRendering(page, ["main.app-shell", ".monitor-work-region", ".resource-table-frame", ".resource-table-row"], { timeoutMs: 3000 });

  const after = await page.evaluate(() => {
    const viewport = document.querySelector<HTMLElement>("main.app-shell")!;
    const sortButton = [...document.querySelectorAll<HTMLElement>(".resource-table-sort-button")]
      .find((element) => element.textContent?.trim().startsWith("名称"));
    const samples = globalThis.__resourceManagerScrollSamples ?? [];
    return {
      top: viewport instanceof HTMLElement ? viewport.scrollTop : null,
      height: viewport instanceof HTMLElement ? viewport.scrollHeight : null,
      minimum: samples.length > 0 ? Math.min(...samples) : null,
      maximum: samples.length > 0 ? Math.max(...samples) : null,
      samples,
      buttonTop: sortButton instanceof HTMLElement ? sortButton.getBoundingClientRect().top : null,
      active: document.activeElement?.className ?? null,
      tablePresent: Boolean(document.querySelector<HTMLElement>(".resource-table-frame")!),
      regions: [...document.querySelectorAll<HTMLElement>(".monitor-work-region")]
        .map((element) => ({ label: element.getAttribute("aria-label"), height: element.getBoundingClientRect().height })),
      bars: [...document.querySelectorAll<HTMLElement>(".resource-breakdown-item")]
        .map((element) => element.getAttribute("data-column-id"))
    };
  });
  if (after.top === null
    || after.minimum === null
    || Math.abs(after.top - before.top) > 1
    || after.samples.some((top) => !Number.isFinite(top) || Math.abs(top - before.top) > 1)) {
    throw new Error(`Resource-table interaction moved the page viewport: ${JSON.stringify({ before, after })}`);
  }

  assertSameFactsLease("Sorting");
  // A new sort column starts descending; repeat it to exercise ascending too.
  const descendingNames = await page.locator('.resource-table-row.software-row[aria-hidden="false"]').evaluateAll(rows => rows.map(row => row.getAttribute("aria-label")));
  await page.locator(".resource-table-sort-button", { hasText: "名称" }).click();
  await waitForNameSort(page, "ascending", descendingNames);
  await waitForStableRendering(page, [".resource-table-frame", ".resource-table-row"], { timeoutMs: 3000 });
  const sortedNames = await page.locator('.resource-table-row.software-row[aria-hidden="false"]').evaluateAll(rows => rows.map(row => row.getAttribute("aria-label")));
  if (sortedNames.length < 2 || sortedNames.some((name, index) => index > 0 && sortedNames[index - 1]!.localeCompare(name!, undefined, { sensitivity: "base", numeric: true }) > 0)) {
    throw new Error(`Name sorting did not reorder the frontend rows: ${JSON.stringify(sortedNames)}`);
  }
  await page.locator('.resource-table-row.software-row[aria-hidden="false"] .resource-row-expander').first().click();
  await page.locator('.resource-table-row.process-row[aria-hidden="false"]').first().waitFor({ state: "visible" });
  // 可展开的软件行意味着树形表格：表格是 treegrid，软件行第 1 层带展开状态，展开出的进程行第 2 层，单元格是 gridcell。
  const treeSemantics = await page.evaluate(() => {
    const frame = document.querySelector<HTMLElement>(".resource-table-frame")!;
    const expanded = document.querySelector<HTMLElement>('.resource-table-row.software-row[aria-hidden="false"][aria-expanded="true"]')!;
    const process = document.querySelector<HTMLElement>('.resource-table-row.process-row[aria-hidden="false"]')!;
    return {
      role: frame?.getAttribute("role"),
      softwareLevel: expanded?.getAttribute("aria-level"),
      processLevel: process?.getAttribute("aria-level"),
      processExpanded: process?.hasAttribute("aria-expanded"),
      plainCells: frame?.querySelectorAll<HTMLElement>("[role='cell']").length,
      gridCells: expanded?.querySelectorAll<HTMLElement>("[role='gridcell']").length
    };
  });
  if (treeSemantics.role !== "treegrid" || treeSemantics.softwareLevel !== "1" || treeSemantics.processLevel !== "2"
    || treeSemantics.processExpanded || treeSemantics.plainCells !== 0 || !(treeSemantics.gridCells! > 0)) {
    throw new Error(`Resource table tree semantics: ${JSON.stringify(treeSemantics)}`);
  }
  // 键盘行为不变：左键收起、右键展开当前软件行。
  const expandedName = await page.evaluate(() => {
    const row = document.querySelector<HTMLElement>('.resource-table-row.software-row[aria-hidden="false"][aria-expanded="true"]')!;
    row?.focus();
    return row?.getAttribute("aria-label");
  });
  await page.keyboard.press("ArrowLeft");
  await page.waitForFunction(name => [...document.querySelectorAll<HTMLElement>(".resource-table-row.software-row")]
    .some(row => row.getAttribute("aria-label") === name && row.getAttribute("aria-expanded") === "false"), expandedName);
  await page.keyboard.press("ArrowRight");
  await page.waitForFunction(name => [...document.querySelectorAll<HTMLElement>(".resource-table-row.software-row")]
    .some(row => row.getAttribute("aria-label") === name && row.getAttribute("aria-expanded") === "true"), expandedName);
  assertSameFactsLease("Expanding a software group");
  await page.getByRole("searchbox", { name: "搜索名称、PID、状态" }).fill("示例软件 79");
  await page.locator('.resource-table-row[aria-hidden="false"]').filter({ hasText: "筛选隐藏项" }).waitFor({ state: "visible" });
  assertSameFactsLease("Filtering to matching rows");
  await page.getByRole("searchbox", { name: "搜索名称、PID、状态" }).fill("no-such-resource");
  await page.locator(".resource-table-panel > .content-state").waitFor({ state: "visible" });
  assertSameFactsLease("Filtering");
  await page.getByRole("searchbox", { name: "搜索名称、PID、状态" }).fill("");
  await page.locator(".resource-table-frame").waitFor({ state: "visible" });
  await page.locator('[data-focus-key="resource-table-edit"]').click();
  const nameResizer = page.locator('.resource-table-column-resizer').first();
  const widthBefore = Number(await nameResizer.getAttribute("aria-valuenow"));
  await nameResizer.focus();
  await nameResizer.press("ArrowRight");
  const widthAfter = Number(await nameResizer.getAttribute("aria-valuenow"));
  if (widthAfter <= widthBefore || widthAfter > 520) throw new Error(`Local column resize failed: ${widthBefore} -> ${widthAfter}`);
  // 列宽只是前端呈现：调整宽度不得改变提交的读取项，也就不重建资源事实订阅。
  // Keep the full 150 ms negative observation window: a width commit alone
  // cannot prove that a delayed demand effect will not recreate the lease.
  await page.waitForTimeout(150);
  assertSameFactsLease("Resizing a column");
  const editor = page.locator(".resource-table-column-editor");
  // 只有方格子切换显示（方格子的读屏名是“显示 X 列”）。
  for (const column of ["CPU", "内存（驻留）", "磁盘", "网络"]) await editor.getByRole("checkbox", { name: `显示 ${column} 列`, exact: true }).uncheck();
  if (await page.locator('.resource-table-row.software-row[aria-hidden="false"]').count() < 2) throw new Error("Hiding numeric columns erased software identities.");
  if (await page.locator('.resource-table-row[aria-hidden="false"] .value-cell').count() !== 0) throw new Error("Hidden numeric columns retained visible cells.");
  // 可见列提交的正是它显示的读取项、隐藏的不占订阅（仪表布局移到前端时的合同）：隐藏全部数值列后，订阅只剩软件/进程身份，
  // 不再带任何数值读取项；身份行保留（上面已查）。
  const identityOnlyItems = ["process.attribution", "process.inventory"];
  const latestItems = () => {
    const latest = factSubscriptions.at(-1);
    return latest ? new URL(latest.path, baseUrl).searchParams.getAll("items").sort() : [];
  };
  await waitForCondition(page, () => JSON.stringify(latestItems()) === JSON.stringify(identityOnlyItems), {
    timeoutMs: 5000, description: "Identity-only facts demand after hiding numeric columns"
  }).catch(() => {
    throw new Error(`Hiding numeric columns did not submit exactly the shown items: ${JSON.stringify(factSubscriptions.at(-1))}`);
  });
  if (JSON.stringify(latestItems()) !== JSON.stringify(identityOnlyItems)) {
    throw new Error(`Hiding numeric columns did not submit exactly the shown items: ${JSON.stringify(factSubscriptions.at(-1))}`);
  }
  await page.locator(".resource-table-panel").getByRole("button", { name: /取消/ }).click();

  // A full pool includes clipped reserve rows. A native scroll must expose
  // current text on the next paint, and keyboard navigation must cross the pool.
  const scrolledPool = await page.locator(".resource-table-viewport").evaluate(async viewport => {
    viewport.scrollTop = 30 * 38 + 7;
    await new Promise<number>(requestAnimationFrame);
    const clip = viewport.getBoundingClientRect();
    const rows = [...viewport.querySelectorAll<HTMLElement>('.resource-table-row[aria-hidden="false"]')];
    const visible = rows.filter(row => {
      const box = row.getBoundingClientRect();
      return box.top < clip.bottom && box.bottom > clip.top;
    });
    visible[0]?.focus({ preventScroll: true });
    return {
      visible: visible.map(row => ({ rendered: getComputedStyle(row!).contentVisibility !== "hidden",
        name: row!.querySelector<HTMLElement>(".resource-row-name")?.textContent,
        values: [...row.querySelectorAll<HTMLElement>(".value-cell")].map(cell => cell.textContent?.trim()) })),
      retainedNames: rows.every(row => Boolean(row!.querySelector<HTMLElement>(".resource-row-name")?.textContent)!),
      poolSize: rows.length
    };
  });
  if (!scrolledPool.visible.length || !scrolledPool.retainedNames
    || scrolledPool.visible.some(row => !row.rendered || !row.name || row.values.some(value => !value))) {
    throw new Error(`Scrolling exposed missing current row contents: ${JSON.stringify(scrolledPool)}`);
  }
  for (const key of ["End", "Home"]) {
    await page.keyboard.press(key);
    await page.waitForFunction(key => {
      const frame = document.querySelector<HTMLElement>(".resource-table-frame")!;
      const row = document.activeElement?.closest<HTMLElement>(".resource-table-row")!;
      const expected = key === "End" ? Number(frame?.getAttribute("aria-rowcount")) : 3;
      return Number(row?.getAttribute("aria-rowindex")) === expected
        && getComputedStyle(row!).contentVisibility !== "hidden"
        && Boolean(row!.querySelector<HTMLElement>(".resource-row-name")?.textContent)!;
    }, key);
  }
  // 切回页面的同步 DOM 已有当前数据；RO 可继续按真实尺寸校准行池。
  await page.setViewportSize({ width: 1067, height: 800 });
  await page.locator('.shell-page[data-page-id="control"]').click();
  const initialPool = await page.evaluate(() => {
    document.querySelector<HTMLElement>('.shell-page[data-page-id="monitor"]')!.click();
    return [...document.querySelectorAll<HTMLElement>('#pageStage .resource-table-row[aria-hidden="false"]')]
      .map(row => row!.querySelector<HTMLElement>(".resource-row-name")?.textContent)!;
  });
  await waitForStableRendering(page, ["#pageStage .resource-table-frame"], { timeoutMs: 3000 });
  const settledPoolSize = await page.locator('#pageStage .resource-table-row[aria-hidden="false"]').count();
  if (!initialPool.length || initialPool.some(name => !name)) {
    throw new Error(`Initial row contents were missing: ${JSON.stringify({ initialPool, settledPoolSize })}`);
  }
  // 原生列宽属性仍须让表头、行在小窗口、常规窗口、大窗口逐列对齐。
  for (const height of [500, 800, 1080]) {
    await page.setViewportSize({ width: 1067, height });
    await waitForStableRendering(page, ["#pageStage .resource-table-frame"], { timeoutMs: 3000 });
    const aligned = await page.locator("#pageStage .resource-table-frame").evaluate(frame => {
      const header = [...frame.querySelectorAll<HTMLElement>(".resource-table-head-cell")];
      const cells = [...frame.querySelector<HTMLElement>(".software-row")!.querySelectorAll<HTMLElement>(".resource-table-cell")];
      return header.length === cells.length && header.every((cell, index) => {
        const heading = cell.getBoundingClientRect();
        const row = cells[index].getBoundingClientRect();
        return Math.abs(heading.left - row.left) <= 1 && Math.abs(heading.right - row.right) <= 1;
      });
    });
    if (!aligned) throw new Error(`Header and row columns diverged at viewport height ${height}`);
  }
  return { before, after, factSubscriptions, initialNames, sortedNames, widthBefore, widthAfter,
    nameOnlyRowsPreserved: true, scrolledPool, initialPoolSize: initialPool.length, settledPoolSize };
}

async function waitForNameSort(page: Page, direction: string, previousNames: (string | null)[]) {
  await page.waitForFunction(({ direction, previousNames }) => {
    const button = [...document.querySelectorAll<HTMLElement>(".resource-table-sort-button")]
      .find(element => element.textContent?.trim().startsWith("名称"));
    const names = [...document.querySelectorAll<HTMLElement>('.resource-table-row.software-row[aria-hidden="false"]')]
      .map(row => row.getAttribute("aria-label"));
    return button?.closest<HTMLElement>(".resource-table-head-cell")?.getAttribute("aria-sort")! === direction
      && names.length >= 2 && JSON.stringify(names) !== JSON.stringify(previousNames);
  }, { direction, previousNames }, { timeout: 3000 });
}
