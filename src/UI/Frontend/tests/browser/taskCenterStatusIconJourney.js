// 任务中心按任务 ID 保留行：同一任务从排队变成运行时，状态图标必须从时钟换成转圈。
export default async function taskCenterStatusIconJourney(page, baseUrl) {
  const assert = (condition, message) => { if (!condition) throw new Error(message); };
  const pageErrors = [];
  page.on("pageerror", (error) => pageErrors.push(String(error)));
  const setScenario = async (patch) => {
    const response = await fetch(`${baseUrl}/__test/scenario`, {
      method: "POST", headers: { "Content-Type": "application/json" }, body: JSON.stringify(patch)
    });
    assert(response.ok, `Unable to set task-center scenario: ${response.status}`);
  };
  await setScenario({ capabilities: "ready", settings: "ready", operations: "mixed", operationCanceled: false, operationQueued: true });
  await page.setViewportSize({ width: 900, height: 640 });
  await page.goto(`${baseUrl}/`, { waitUntil: "domcontentloaded" });
  await page.locator("#taskCenterButton").click();
  const dialog = page.getByRole("dialog", { name: "任务中心" });
  await dialog.waitFor({ state: "visible", timeout: 3000 });
  const icon = dialog.locator("[data-task-center-id='operation:000000000000000000000000000000a1'] .task-center-item-leading svg");
  await icon.waitFor({ state: "attached", timeout: 3000 });
  const queuedClass = await icon.getAttribute("class");
  assert(!queuedClass?.includes("task-center-spinner"), `A queued task shows the running spinner: ${queuedClass}`);

  await setScenario({ operationQueued: false });
  await dialog.locator("[data-task-center-id='operation:000000000000000000000000000000a1'] .task-center-item-leading svg.task-center-spinner")
    .waitFor({ state: "attached", timeout: 5000 });
  const runningIcons = await dialog.locator("[data-task-center-id='operation:000000000000000000000000000000a1'] .task-center-item-leading svg").count();
  assert(runningIcons === 1, `The running task shows ${runningIcons} status icons`);
  assert(pageErrors.length === 0, pageErrors.join("\n"));
  return { queuedClass, runningIcons, pageErrors };
}
