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
    if (!toggles.some(x => x.includes("关闭"))) throw new Error("Localized false toggle was dropped");
    if (reading.textContent.includes("Voltage")) throw new Error("Root-locked editor became read-only");
    return { top, toggles, reading: reading.textContent };
  });
  await page.getByRole("radio", { name: "软件管理", exact: true }).click();
  const boost = page.getByRole("radiogroup", { name: "Dynamic Boost 开关", exact: true });
  await boost.getByRole("radio", { name: "开启", exact: true }).waitFor();
  if (await page.getByRole("checkbox", { name: "Dynamic Boost 开关", exact: true }).count() !== 0)
    throw new Error("Toggle capability must not keep the row checkbox next to its three-way choice");
  const refresh = page.waitForResponse(response => response.url().endsWith("/api/control/objects"));
  await page.evaluate(() => window.dispatchEvent(new Event("focus")));
  await refresh;
  await page.waitForTimeout(100);
  if (await boost.getByRole("radio", { name: "开启", exact: true }).isDisabled())
    throw new Error("Catalog refresh lost software-management intent and relocked the control");
  await boost.getByRole("radio", { name: "开启", exact: true }).click();
  const editedRefresh = page.waitForResponse(response => response.url().endsWith("/api/control/objects"));
  await page.evaluate(() => window.dispatchEvent(new Event("focus")));
  await editedRefresh;
  await page.waitForTimeout(100);
  if (await boost.getByRole("radio", { name: "开启", exact: true }).getAttribute("aria-checked") !== "true")
    throw new Error("Catalog refresh discarded the edited setting");
  if (await page.getByRole("checkbox", { name: "Voltage", exact: true }).count() !== 0)
    throw new Error("Normal mode exposed Root-only setting");
  await page.getByRole("radio", { name: "固件自动管理", exact: true }).click();
  if (!await boost.getByRole("radio", { name: "开启", exact: true }).isDisabled())
    throw new Error("Explicit firmware ownership did not lock controls");
  return { ...result, takeoverSurvivesRefresh: true };
}
