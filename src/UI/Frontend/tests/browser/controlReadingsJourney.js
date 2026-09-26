export default async function controlReadingsJourney(page, baseUrl) {
  await page.goto(baseUrl, { waitUntil: "domcontentloaded" });
  await page.getByRole("navigation", { name: "页面切换" }).getByRole("button", { name: "控制面", exact: true }).click();
  await page.locator(".control-readings dd").filter({ hasText: "89" }).waitFor();
  const result = await page.evaluate(() => {
    const reading = document.querySelector(".control-readings");
    const ownership = document.querySelector(".control-ownership");
    const top = Boolean(reading.compareDocumentPosition(ownership) & Node.DOCUMENT_POSITION_FOLLOWING);
    const toggles = [...document.querySelectorAll(".control-capability-actual")].map(x => x.textContent);
    if (!top || reading.querySelector("h4")) throw new Error("Readings must precede controls without heading");
    if (!toggles.some(x => x.includes("OFF"))) throw new Error("False toggle was dropped");
    if (reading.textContent.includes("Voltage")) throw new Error("Root-locked editor became read-only");
    return { top, toggles, reading: reading.textContent };
  });
  await page.getByRole("radio", { name: "软件管理", exact: true }).click();
  await page.getByRole("checkbox", { name: "Dynamic Boost", exact: true }).waitFor();
  const refresh = page.waitForResponse(response => response.url().endsWith("/api/control/objects"));
  await page.evaluate(() => window.dispatchEvent(new Event("focus")));
  await refresh;
  await page.waitForTimeout(100);
  if (await page.getByRole("checkbox", { name: "Dynamic Boost", exact: true }).count() !== 1)
    throw new Error("Catalog refresh lost software-management intent and relocked the control");
  await page.getByRole("checkbox", { name: "Dynamic Boost", exact: true }).check();
  const editedRefresh = page.waitForResponse(response => response.url().endsWith("/api/control/objects"));
  await page.evaluate(() => window.dispatchEvent(new Event("focus")));
  await editedRefresh;
  await page.waitForTimeout(100);
  if (!await page.getByRole("checkbox", { name: "Dynamic Boost", exact: true }).isChecked())
    throw new Error("Catalog refresh discarded the edited setting");
  if (await page.getByRole("checkbox", { name: "Voltage", exact: true }).count() !== 0)
    throw new Error("Normal mode exposed Root-only setting");
  await page.getByRole("radio", { name: "固件自动管理", exact: true }).click();
  if (await page.getByRole("checkbox", { name: "Dynamic Boost", exact: true }).count() !== 0)
    throw new Error("Explicit firmware ownership did not lock controls");
  return { ...result, takeoverSurvivesRefresh: true };
}
