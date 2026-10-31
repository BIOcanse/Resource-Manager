import type { Page } from "playwright-core";
import assert from "node:assert/strict";
import { dashboardPointValues } from "../../src/preferences/dashboardPreferences.ts";
import { seedFrontendPreferences, writeFrontendPreferences, customRefresh } from "./frontendPreferencesFixture.ts";

export default async function resourceTableTotalsLocaleJourney(page: Page, baseUrl: string) {
  await seedFrontendPreferences(page, value => {
    value.appearance.language = "fr-FR";
    value.refresh.resourceTable = customRefresh(500);
    value.dashboard = dashboardPointValues();
    delete value.dashboard.usesFactoryDefaults;
    for (const columns of [value.dashboard.resourceTableColumns, value.dashboard.resourceTableProcessColumns]) {
      for (const column of columns) if (column.id === "memory") column.source = "process_all_private_commit_actual";
    }
  });
  const results = [];
  for (const language of ["fr-FR", "de-DE"]) {
    if (language === "de-DE") await writeFrontendPreferences(page, value => { value.appearance.language = language; });
    await page.goto(baseUrl, { waitUntil: "domcontentloaded" });
    const panel = page.locator(".resource-table-panel");
    await panel.scrollIntoViewIfNeeded();
    const header = panel.locator('.resource-table-head-cell[data-column-id="memory"]');
    await header.waitFor();
    const columnIndex = await header.evaluate(element => [...element.parentElement!.children].indexOf(element));
    const cell = (kind: string) => panel.locator(`.resource-table-row.${kind}[aria-hidden="false"]`).first().locator(".resource-table-cell").nth(columnIndex);
    const expected = (value: number) => `${value.toLocaleString(language, { useGrouping: false, minimumFractionDigits: 2, maximumFractionDigits: 2 })} GiB`;
    await cell("summary-row").getByText(expected(6), { exact: true }).waitFor();
    await cell("residual-row").getByText(expected(5), { exact: true }).waitFor();
    const ordinary = panel.locator('.resource-table-row.software-row:not(.residual-row)[aria-hidden="false"]').first().locator(".resource-table-cell").nth(columnIndex);
    await ordinary.getByText(expected(1), { exact: true }).waitFor();
    const result = { language, total: await cell("summary-row").textContent(), attributed: await ordinary.textContent(), residual: await cell("residual-row").textContent() };
    assert.equal(result.total!.trim(), expected(6), "the total is the system commit charge, rather than the attributed process sum");
    assert.equal(result.attributed!.trim(), expected(1));
    assert.equal(result.residual!.trim(), expected(5));
    results.push(result);
  }
  return results;
}
