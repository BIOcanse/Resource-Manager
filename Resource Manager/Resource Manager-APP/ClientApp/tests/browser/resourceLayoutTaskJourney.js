export default async function resourceLayoutTaskJourney(page, baseUrl = "http://127.0.0.1:4177") {
  const pageErrors = [];
  const consoleErrors = [];
  page.on("pageerror", (error) => pageErrors.push(String(error)));
  page.on("console", (message) => {
    if (message.type() === "error") {
      consoleErrors.push(message.text());
    }
  });

  const assert = (condition, message) => {
    if (!condition) {
      throw new Error(message);
    }
  };
  const setScenario = async (patch) => {
    const result = await page.evaluate(async (value) => {
      const response = await fetch("/__test/scenario", {
        method: "POST",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify(value)
      });
      return { ok: response.ok, status: response.status };
    }, patch);
    assert(result.ok, `Unable to set frontend scenario: ${JSON.stringify(result)}`);
  };
  const waitForCpuTotal = (displayValue) => page.waitForFunction((expected) => {
    const item = document.querySelector('.resource-bar-track[data-metric-id="cpu.usage"]')
      ?.closest(".resource-breakdown-item");
    const value = item?.querySelector(".resource-breakdown-header span")?.textContent?.trim();
    return Number.parseFloat(value ?? "") === Number.parseFloat(expected);
  }, displayValue, { timeout: 6000 });
  const installMotionObserver = async () => page.evaluate(() => {
    const track = document.querySelector('[data-metric-id="cpu.usage"].resource-bar-track');
    if (!(track instanceof HTMLElement)) {
      throw new Error("CPU resource track is unavailable.");
    }
    const events = [];
    const observer = new MutationObserver(() => {
      events.push({ moving: track.classList.contains("moving"), at: performance.now() });
    });
    observer.observe(track, { attributes: true, attributeFilter: ["class"] });
    window.__resourceLayoutMotionProbe = { track, events, observer };
  });
  const readMotion = () => page.evaluate(() => {
    const probe = window.__resourceLayoutMotionProbe;
    return {
      events: probe?.events ?? [],
      moving: probe?.track?.classList.contains("moving") ?? null
    };
  });
  const disposeMotionObserver = () => page.evaluate(() => {
    window.__resourceLayoutMotionProbe?.observer?.disconnect();
    delete window.__resourceLayoutMotionProbe;
  });

  await page.setViewportSize({ width: 1000, height: 700 });
  await page.goto(`${baseUrl}/`, { waitUntil: "domcontentloaded" });
  await setScenario({
    capabilities: "ready",
    settings: "ready",
    animations: "normal",
    resourceBreakdownGeneration: 0
  });
  await page.reload({ waitUntil: "domcontentloaded" });

  const track = page.locator('[data-metric-id="cpu.usage"].resource-bar-track');
  await track.waitFor({ state: "visible", timeout: 7000 });
  await page.waitForFunction(() => document.body.dataset.animations === "normal");
  await installMotionObserver();
  await setScenario({ resourceBreakdownGeneration: 1 });
  await waitForCpuTotal("38%");
  await page.waitForFunction(() =>
    window.__resourceLayoutMotionProbe?.events.some((event) => event.moving === true),
  null,
  { timeout: 3000 });
  await page.waitForFunction(() => {
    const events = window.__resourceLayoutMotionProbe?.events ?? [];
    const movingIndex = events.findIndex((event) => event.moving === true);
    return movingIndex >= 0
      && events.slice(movingIndex + 1).some((event) => event.moving === false);
  }, null, { timeout: 3000 });
  const normal = await readMotion();
  const movingEvent = normal.events.find((event) => event.moving === true);
  const settledEvent = normal.events.find(
    (event) => movingEvent && event.at > movingEvent.at && event.moving === false);
  assert(normal.moving === false
    && movingEvent
    && settledEvent
    && settledEvent.at - movingEvent.at >= 400,
  `Normal layout transition did not settle through the task lifecycle: ${JSON.stringify(normal)}`);
  await disposeMotionObserver();

  await setScenario({
    animations: "auto",
    gpuGrade: "optimize",
    resourceBreakdownGeneration: 4
  });
  await page.reload({ waitUntil: "domcontentloaded" });
  await track.waitFor({ state: "visible", timeout: 7000 });
  await page.waitForFunction(() => document.body.dataset.selfGpuGrade === "optimize", null, {
    timeout: 6500
  });
  await page.waitForFunction(() => document.body.dataset.animations === "normal");
  await installMotionObserver();
  await setScenario({ resourceBreakdownGeneration: 5 });
  await waitForCpuTotal("38%");
  await page.waitForFunction(() =>
    window.__resourceLayoutMotionProbe?.events.some((event) => event.moving === true),
  null,
  { timeout: 3000 });
  await page.evaluate(() => window.dispatchEvent(new Event("blur")));
  await page.waitForFunction(() => document.body.dataset.animations === "ultra", null, {
    timeout: 3000
  });
  await page.waitForFunction(() => {
    const events = window.__resourceLayoutMotionProbe?.events ?? [];
    const movingIndex = events.findIndex((event) => event.moving === true);
    return movingIndex >= 0
      && events.slice(movingIndex + 1).some((event) => event.moving === false);
  }, null, { timeout: 3000 });
  const reactive = await readMotion();
  const reactiveMoving = reactive.events.find((event) => event.moving === true);
  const reactiveSettled = reactive.events.find(
    (event) => reactiveMoving && event.at > reactiveMoving.at && event.moving === false);
  assert(reactive.moving === false
    && reactiveMoving
    && reactiveSettled
    && reactiveSettled.at - reactiveMoving.at < 400,
  `Reactive animation-mode change did not settle immediately: ${JSON.stringify(reactive)}`);
  await disposeMotionObserver();

  await setScenario({ animations: "none", resourceBreakdownGeneration: 2 });
  await page.reload({ waitUntil: "domcontentloaded" });
  await track.waitFor({ state: "visible", timeout: 7000 });
  await page.waitForFunction(() => document.body.dataset.animations === "none");
  await installMotionObserver();
  await setScenario({ resourceBreakdownGeneration: 3 });
  await waitForCpuTotal("55%");
  await page.waitForTimeout(600);
  const disabled = await readMotion();
  assert(disabled.moving === false
    && disabled.events.every((event) => event.moving === false),
  `Disabled motion entered a moving state: ${JSON.stringify(disabled)}`);
  await disposeMotionObserver();
  await setScenario({
    animations: "normal",
    gpuGrade: "normal",
    resourceBreakdownGeneration: 0
  });

  assert(pageErrors.length === 0 && consoleErrors.length === 0,
    `Resource layout browser errors: ${JSON.stringify({ pageErrors, consoleErrors })}`);
  return { normal, reactive, disabled, pageErrors, consoleErrors };
}
