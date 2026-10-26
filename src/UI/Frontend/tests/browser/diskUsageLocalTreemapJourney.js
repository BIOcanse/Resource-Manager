import { NOT_GHOST } from "./liveLocator.js";
import { loadAppCopy } from "../../src/i18n/copy/appCopyLoader.ts";

export default async function diskUsageLocalTreemapJourney(page, baseUrl) {
  const assert = (value, message) => { if (!value) throw new Error(message); };
  const errors = [];
  const diskRequests = [];
  page.on("pageerror", error => errors.push(String(error)));
  page.on("request", request => {
    const url = new URL(request.url());
    if (url.pathname.startsWith("/api/disk-usage/")) diskRequests.push(`${url.pathname}${url.search}`);
  });
  // Wrap the real browser Worker. Messages still run in its separate execution context.
  await page.addInitScript(() => {
    window.__diskWorkerEvidence = [];
    const NativeWorker = window.Worker;
    window.Worker = class extends NativeWorker {
      constructor(url, options) {
        super(url, options);
        const evidence = { url: String(url), terminated: false, requests: [], layouts: [] };
        this.evidence = evidence;
        window.__diskWorkerEvidence.push(evidence);
        this.addEventListener("message", ({ data }) => {
          if (data.layout) evidence.layouts.push({ rootPath: data.layout.rootPath, view: data.layout.view });
        });
      }
      postMessage(request, transfer) {
        this.evidence.requests.push({ rootNodeId: request.rootNodeId, tree: request.buffer instanceof ArrayBuffer });
        super.postMessage(request, transfer);
      }
      set onmessage(handler) {
        super.onmessage = event => {
          if (window.__delayDiskWorkerDelivery) setTimeout(() => handler(event), 180);
          else handler(event);
        };
      }
      terminate() {
        this.evidence.terminated = true;
        super.terminate();
      }
    };
  });
  const patch = await fetch(`${baseUrl}/__test/scenario`, {
    method: "POST", headers: { "Content-Type": "application/json" },
    body: JSON.stringify({ diskUsageMultiRoot: true })
  });
  assert(patch.ok, "Could not configure disk-tree fixture");
  await page.setViewportSize({ width: 800, height: 500 });
  await page.goto(baseUrl, { waitUntil: "domcontentloaded" });
  const copy = await loadAppCopy("zh-CN");
  const navigation = page.locator(".shell-pages");
  await navigation.getByRole("button", { name: copy.page.diskUsage, exact: true }).click();
  const title = page.locator(".disk-usage-result .optimization-heading h2");
  await title.getByText("C:\\", { exact: true }).waitFor();
  const acceptedReadCount = diskRequests.length;
  const canvas = page.locator(`.disk-usage-treemap-canvas${NOT_GHOST}`);
  const checked = [];
  for (const size of [{ width: 800, height: 500 }, { width: 1200, height: 950 }, { width: 1920, height: 1080 }]) {
    await page.setViewportSize(size);
    await canvas.scrollIntoViewIfNeeded();
    const box = await canvas.boundingBox();
    assert(box && box.width > 0 && box.height > 0, `Canvas collapsed at ${size.width}x${size.height}`);
    const center = { x: box.width / 2, y: box.height / 2 };
    await canvas.hover({ position: center });
    const tip = page.locator(".disk-usage-hover-tip");
    await tip.getByText("Archive", { exact: true }).waitFor();
    assert((await tip.innerText()).includes("C:\\Projects\\Archive"), "Hover did not resolve the full descendant path from local facts");
    const scales = [];
    for (const ticks of [8, 4]) {
      for (let tick = 0; tick < ticks; tick++) {
        await canvas.dispatchEvent("wheel", { clientX: box.x + center.x, clientY: box.y + center.y, deltaY: -100 });
      }
      const minimumScale = scales.length === 0 ? 4 : 8;
      await page.waitForFunction(scale => window.__diskWorkerEvidence.some(worker =>
        worker.layouts.at(-1)?.view.scale >= scale), minimumScale);
      scales.push(await page.evaluate(() => window.__diskWorkerEvidence.at(-1).layouts.at(-1).view.scale));
      await canvas.hover({ position: center });
      await tip.getByText("Archive", { exact: true }).waitFor();
    }
    // Keep a real Worker result pending while wheel/resize events still see the old root.
    // A view request for that old root must not supersede the pending drill or alter its stack.
    await page.evaluate(() => { window.__delayDiskWorkerDelivery = true; });
    await canvas.dblclick({ position: center });
    for (let tick = 0; tick < 5; tick++) {
      await canvas.dispatchEvent("wheel", { clientX: box.x + center.x, clientY: box.y + center.y, deltaY: -100 });
    }
    await page.setViewportSize({ width: size.width + 1, height: size.height });
    await title.getByText("C:\\Projects\\Archive", { exact: true }).waitFor();
    await page.getByRole("button", { name: copy.diskUsage.navigateUp, exact: true }).click();
    for (let tick = 0; tick < 5; tick++) {
      await canvas.dispatchEvent("wheel", { clientX: box.x + center.x, clientY: box.y + center.y, deltaY: -100 });
    }
    await title.getByText("C:\\", { exact: true }).waitFor();
    await page.evaluate(() => { window.__delayDiskWorkerDelivery = false; });
    const layoutsBeforeReset = await page.evaluate(() => window.__diskWorkerEvidence.at(-1).layouts.length);
    await page.getByRole("button", { name: copy.diskUsage.resetView, exact: true }).click();
    await page.waitForFunction(previous => {
      const layouts = window.__diskWorkerEvidence.at(-1).layouts;
      return layouts.length > previous && layouts.at(-1)?.view.scale === 1;
    }, layoutsBeforeReset);
    assert(diskRequests.length === acceptedReadCount, `Local interaction fetched disk facts at ${size.width}x${size.height}: ${JSON.stringify(diskRequests)}`);
    checked.push({ ...size, scales, hover: true, drill: true, up: true, reset: true, pendingRootViewRace: true });
  }
  const beforeExit = await page.evaluate(() => window.__diskWorkerEvidence);
  assert(beforeExit.length === 1 && !beforeExit[0].terminated, "Local views replaced or stopped the page's Worker");
  assert(beforeExit[0].requests.filter(request => request.tree).length === 1, "The Worker received the same scan tree repeatedly");
  const treeRequests = diskRequests.filter(url => url.startsWith("/api/disk-usage/tree"));
  const treeQuery = new URL(treeRequests[0], baseUrl).searchParams;
  assert(treeRequests.length === 1 && treeQuery.get("rootIndex") === "0"
    && treeQuery.get("maxNodes") === "400000" && treeQuery.get("maxBytes") === "33554432"
    && [...treeQuery.keys()].every(key => ["rootIndex", "maxNodes", "maxBytes"].includes(key)), "Tree endpoint received a view query, missing budgets or repeated read");
  assert(!diskRequests.some(url => url.startsWith("/api/disk-usage/layout")), "A server layout route was requested");
  await navigation.getByRole("button", { name: copy.page.monitor, exact: true }).click();
  await page.waitForFunction(() => window.__diskWorkerEvidence.every(worker => worker.terminated));
  assert(errors.length === 0, errors.join("\n"));
  return { sizes: checked, treeReads: 1, workerRetainedForLocalViews: true, workerTerminatedOnExit: true, pageErrors: errors };
}
