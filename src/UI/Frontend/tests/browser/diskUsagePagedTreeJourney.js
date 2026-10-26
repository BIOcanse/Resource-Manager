import { NOT_GHOST } from "./liveLocator.js";
import { loadAppCopy } from "../../src/i18n/copy/appCopyLoader.ts";
import { loadSettingsText } from "../../src/i18n/settingsLoader.ts";
import { languageOptions } from "../../src/i18n/settingsLanguages.ts";

export default async function diskUsagePagedTreeJourney(page, baseUrl) {
  const assert = (value, message) => { if (!value) throw new Error(message); };
  const errors = [];
  const requests = [];
  const buffers = [];
  const pendingBuffers = [];
  page.on("pageerror", error => errors.push(String(error)));
  page.on("request", request => {
    const url = new URL(request.url());
    if (url.pathname === "/api/disk-usage/tree") requests.push(url.search);
    if (url.pathname === "/api/disk-usage/layout") errors.push("Requested a server layout");
  });
  page.on("response", response => {
    if (new URL(response.url()).pathname !== "/api/disk-usage/tree" || !response.ok()) return;
    pendingBuffers.push(response.body().then(buffer => {
      buffers.push({ bytes: buffer.byteLength, version: buffer.readUInt32LE(4), nodes: buffer.readUInt32LE(8), nextCursor: buffer.readInt32LE(16) });
    }));
  });
  const patch = await fetch(`${baseUrl}/__test/scenario`, {
    method: "POST", headers: { "Content-Type": "application/json" },
    body: JSON.stringify({ diskUsageMultiRoot: true, diskUsagePaged: true })
  });
  assert(patch.ok, "Could not configure paginated disk fixture");
  await page.setViewportSize({ width: 1200, height: 950 });
  await page.goto(baseUrl, { waitUntil: "domcontentloaded" });
  let currentLanguage = "zh-CN";
  const checked = [];
  const title = page.locator(".disk-usage-result .optimization-heading h2");
  const canvas = page.locator(`.disk-usage-treemap-canvas${NOT_GHOST}`);
  for (const option of languageOptions.filter(option => option.id !== "system")) {
    if (currentLanguage !== option.id) {
      const oldCopy = await loadAppCopy(currentLanguage);
      const settings = await loadSettingsText(currentLanguage);
      await page.locator(".shell-pages").getByRole("button", { name: oldCopy.page.settings, exact: true }).click();
      await page.locator(".settings-nav").getByRole("button", { name: settings.sections.appearance, exact: true }).click();
      await page.getByRole("button", { name: settings.appearance.languageSelectLabel, exact: true }).click();
      await page.getByRole("option", { name: `${option.nativeLabel} · ${option.label}`, exact: true }).click();
      await page.getByRole("button", { name: settings.actions.save, exact: true }).click();
      await page.waitForFunction(language => document.documentElement.lang === language, option.id);
      currentLanguage = option.id;
    }
    const copy = await loadAppCopy(option.id);
    await page.locator(".shell-pages").getByRole("button", { name: copy.page.diskUsage, exact: true }).click();
    await title.getByText("C:\\", { exact: true }).waitFor();
    const pagination = page.locator(".disk-usage-query-pages");
    await pagination.getByText(copy.diskUsage.partialQuery, { exact: true }).waitFor();
    await pagination.getByText(copy.diskUsage.queryPage(1), { exact: true }).waitFor();
    const previous = pagination.getByRole("button", { name: copy.diskUsage.previousPage, exact: true });
    const next = pagination.getByRole("button", { name: copy.diskUsage.nextPage, exact: true });
    assert(await previous.isDisabled(), `${option.id}: first page enabled Previous`);
    const readsBeforePaging = requests.length;
    await canvas.hover({ position: await canvas.evaluate(element => ({ x: element.clientWidth / 2, y: element.clientHeight / 2 })) });
    await page.locator(".disk-usage-hover-tip").getByText("Projects", { exact: true }).waitFor();
    assert(requests.length === readsBeforePaging, "Incomplete branch was automatically downloaded on hover");
    await next.click();
    await pagination.getByText(copy.diskUsage.queryPage(2), { exact: true }).waitFor();
    assert(await next.isDisabled(), `${option.id}: complete final page enabled Next`);
    assert(new URLSearchParams(requests.at(-1)).get("cursor") === "5", "Next did not explicitly query the returned continuation");
    await previous.click();
    await pagination.getByText(copy.diskUsage.queryPage(1), { exact: true }).waitFor();
    assert(requests.length === readsBeforePaging + 2, "Pages were accumulated or drained without explicit controls");
    const center = await canvas.evaluate(element => ({ x: element.clientWidth / 2, y: element.clientHeight / 2 }));
    await canvas.dblclick({ position: center });
    await title.getByText("C:\\Projects", { exact: true }).waitFor();
    assert(new URLSearchParams(requests.at(-1)).get("node") === "1", "Incomplete branch drill did not query its neutral subtree");
    await pagination.waitFor({ state: "hidden" });
    const afterIncompleteDrill = requests.length;
    await canvas.dblclick({ position: center });
    await title.getByText("C:\\Projects\\Archive", { exact: true }).waitFor();
    assert(requests.length === afterIncompleteDrill, "Complete cached descendant was fetched again");
    await page.getByRole("button", { name: copy.diskUsage.navigateUp, exact: true }).click();
    await title.getByText("C:\\Projects", { exact: true }).waitFor();
    assert(requests.length === afterIncompleteDrill, "Cached parent was fetched again");
    await page.getByRole("button", { name: copy.diskUsage.navigateUp, exact: true }).click();
    await title.getByText("C:\\", { exact: true }).waitFor();
    await pagination.getByText(copy.diskUsage.queryPage(1), { exact: true }).waitFor();
    assert(requests.length === afterIncompleteDrill + 1, "Uncached parent did not issue one bounded tree query");
    checked.push(option.id);
  }
  await Promise.all(pendingBuffers);
  assert(buffers.some(buffer => buffer.nextCursor === 5), "No incomplete query was exercised");
  assert(buffers.every(buffer => buffer.version === 3 && buffer.nodes <= 400000 && buffer.bytes <= 33554432), "A scan-tree response exceeded its node/byte budget or used the old wire format");
  assert(requests.every(query => {
    const parameters = new URLSearchParams(query);
    return parameters.get("rootIndex") === "0" && parameters.get("maxNodes") === "400000"
      && parameters.get("maxBytes") === "33554432"
      && [...parameters.keys()].every(key => ["rootIndex", "node", "cursor", "maxNodes", "maxBytes"].includes(key));
  }), "A tree query contained presentation state or lacked a budget");
  assert(errors.length === 0, errors.join("\n"));
  return { languages: checked, explicitNextPrevious: true, incompleteBranchQuery: true, cachedDrillAndUp: true, uncachedParentQuery: true, boundedResponses: buffers, pageErrors: errors };
}
