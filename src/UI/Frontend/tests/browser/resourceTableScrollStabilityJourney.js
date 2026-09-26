export default async function resourceTableScrollStabilityJourney(
  page,
  baseUrl = "http://127.0.0.1:4177"
) {
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
        .map((element) => element.getAttribute("data-pointer-reorder-id"))
    };
  });
  if (before.top < 100) {
    throw new Error(`Fixture did not create a scrollable monitor page: ${JSON.stringify(before)}`);
  }

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
        .map((element) => element.getAttribute("data-pointer-reorder-id"))
    };
  });
  if (after.top === null
    || after.minimum === null
    || Math.abs(after.top - before.top) > 1
    || after.samples.some((top) => !Number.isFinite(top) || Math.abs(top - before.top) > 1)) {
    throw new Error(`Resource-table interaction moved the page viewport: ${JSON.stringify({ before, after })}`);
  }

  return { before, after };
}
