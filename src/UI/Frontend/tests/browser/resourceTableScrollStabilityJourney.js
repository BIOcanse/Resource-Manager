export default async function resourceTableScrollStabilityJourney(
  page,
  baseUrl = "http://127.0.0.1:4177"
) {
  page.on("console", message => { if (message.type() === "error") process.stderr.write(`Table browser console: ${message.text()}\n`); });
  const factSubscriptions = [];
  page.on("request", request => {
    if (new URL(request.url()).pathname !== "/api/subscriptions/stream") return;
    const body = request.postDataJSON();
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
  await page.locator(".resource-table-frame").waitFor({ state: "visible", timeout: 7000 });
  await page.locator(".resource-breakdown-item").first().waitFor({ state: "visible", timeout: 7000 });
  await page.waitForTimeout(500);

  const before = await page.evaluate(() => {
    const viewport = document.querySelector("main.app-shell");
    const sortButton = [...document.querySelectorAll(".resource-table-sort-button")]
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
      regions: [...document.querySelectorAll(".monitor-work-region")]
        .map((element) => ({ label: element.getAttribute("aria-label"), height: element.getBoundingClientRect().height })),
      bars: [...document.querySelectorAll(".resource-breakdown-item")]
        .map((element) => element.getAttribute("data-column-id"))
    };
  });
  if (before.top < 100) {
    throw new Error(`Fixture did not create a scrollable monitor page: ${JSON.stringify(before)}`);
  }

  const subscriptionIds = [...new Set(factSubscriptions.map(subscription => subscription.id))];
  const assertSameFactsLease = action => {
    if (factSubscriptions.some(subscription => !subscriptionIds.includes(subscription.id))) throw new Error(`${action} recreated the neutral facts lease.`);
  };
  const initialNames = await page.locator('.resource-table-row.software-row[aria-hidden="false"]').evaluateAll(rows => rows.map(row => row.getAttribute("aria-label")));
  await page.locator(".resource-table-sort-button", { hasText: "名称" }).click();
  await page.waitForTimeout(150);

  const after = await page.evaluate(() => {
    const viewport = document.querySelector("main.app-shell");
    const sortButton = [...document.querySelectorAll(".resource-table-sort-button")]
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
      tablePresent: Boolean(document.querySelector(".resource-table-frame")),
      regions: [...document.querySelectorAll(".monitor-work-region")]
        .map((element) => ({ label: element.getAttribute("aria-label"), height: element.getBoundingClientRect().height })),
      bars: [...document.querySelectorAll(".resource-breakdown-item")]
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
  await page.locator(".resource-table-sort-button", { hasText: "名称" }).click();
  await page.waitForTimeout(50);
  const sortedNames = await page.locator('.resource-table-row.software-row[aria-hidden="false"]').evaluateAll(rows => rows.map(row => row.getAttribute("aria-label")));
  if (sortedNames.length < 2 || sortedNames.some((name, index) => index > 0 && sortedNames[index - 1].localeCompare(name, undefined, { sensitivity: "base" }) > 0)) {
    throw new Error(`Name sorting did not reorder the frontend rows: ${JSON.stringify(sortedNames)}`);
  }
  await page.locator('.resource-table-row.software-row[aria-hidden="false"] .resource-row-expander').first().click();
  await page.locator('.resource-table-row.process-row[aria-hidden="false"]').first().waitFor({ state: "visible" });
  assertSameFactsLease("Expanding a software group");
  await page.getByRole("searchbox", { name: "搜索名称、PID、状态" }).fill("no-such-resource");
  await page.locator(".resource-table-panel .content-state").waitFor({ state: "visible" });
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
  await page.waitForTimeout(150);
  assertSameFactsLease("Resizing a column");
  const editor = page.locator(".resource-table-column-editor");
  await editor.locator("label").filter({ hasText: /^CPU$/ }).getByRole("checkbox").uncheck();
  await editor.locator("label").filter({ hasText: /内存（驻留）/ }).getByRole("checkbox").uncheck();
  await editor.locator("label").filter({ hasText: /^磁盘$/ }).getByRole("checkbox").uncheck();
  await editor.locator("label").filter({ hasText: /^网络$/ }).getByRole("checkbox").uncheck();
  if (await page.locator('.resource-table-row.software-row[aria-hidden="false"]').count() < 2) throw new Error("Hiding numeric columns erased software identities.");
  if (await page.locator('.resource-table-row[aria-hidden="false"] .value-cell').count() !== 0) throw new Error("Hidden numeric columns retained visible cells.");
  // 可见列提交的正是它显示的读取项、隐藏的不占订阅（仪表布局移到前端时的合同）：隐藏全部数值列后，订阅只剩软件/进程身份，
  // 不再带任何数值读取项；身份行保留（上面已查）。
  const identityOnlyItems = ["process.attribution", "process.inventory"];
  const latestItems = () => {
    const latest = factSubscriptions.at(-1);
    return latest ? new URL(latest.path, baseUrl).searchParams.getAll("items").sort() : [];
  };
  const hideDeadline = Date.now() + 5_000;
  while (JSON.stringify(latestItems()) !== JSON.stringify(identityOnlyItems) && Date.now() < hideDeadline) await page.waitForTimeout(100);
  if (JSON.stringify(latestItems()) !== JSON.stringify(identityOnlyItems)) {
    throw new Error(`Hiding numeric columns did not submit exactly the shown items: ${JSON.stringify(factSubscriptions.at(-1))}`);
  }
  await page.locator(".resource-table-panel").getByRole("button", { name: /取消/ }).click();

  return { before, after, factSubscriptions, initialNames, sortedNames, widthBefore, widthAfter, nameOnlyRowsPreserved: true };
}
