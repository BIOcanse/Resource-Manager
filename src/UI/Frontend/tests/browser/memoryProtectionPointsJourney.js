import assert from "node:assert/strict";
import { loadAppCopy } from "../../src/i18n/copy/appCopyLoader.ts";
import { loadSettingsText } from "../../src/i18n/settingsLoader.ts";
import { waitForContentSettled } from "./contentSettled.js";
import { memoryProtectionShot } from "./memoryProtectionShots.js";

// 设置 → 主机内存保护 → 已保存的正常运行点（用户 10-28：「可以存多个基准点并选择生效的基准点」）。
// 新建的点成为生效的点，之前的点留在列表里；可以把别的点设为生效、删除任意一个（删除先确认，删生效的点写明后果）；
// 满额时显示后端给的原因；别处改了生效的点，列表随之重读；读取中的占位和没有已保存的点时一样高。
// 列表和生效的是哪一个都以后端为准：每一步都和 GET /api/memory-protection/normal-running-points 比对。
export default async function memoryProtectionPointsJourney(page, baseUrl) {
  const errors = [];
  page.on("pageerror", error => errors.push(String(error)));
  const app = await loadAppCopy("zh-CN");
  const settingsText = await loadSettingsText("zh-CN");
  const copy = app.memoryProtection;
  await page.setViewportSize({ width: 1280, height: 800 });
  const timeFormat = new Intl.DateTimeFormat("zh-CN", { year: "numeric", month: "numeric", day: "numeric", hour: "numeric", minute: "numeric", second: "numeric" });
  const time = at => timeFormat.format(new Date(at));
  const writes = [];
  page.on("request", request => {
    const path = new URL(request.url()).pathname;
    if (path.startsWith("/api/memory-protection/normal-running-point") && request.method() !== "GET") {
      writes.push(`${request.method()} ${path}${request.postData() ? ` ${request.postData()}` : ""}`);
    }
  });

  const scenario = async body => {
    const response = await fetch(`${baseUrl}/__test/scenario`, { method: "POST", headers: { "Content-Type": "application/json" }, body: JSON.stringify(body) });
    assert.equal(response.ok, true, `scenario ${JSON.stringify(body)} rejected`);
  };
  const backendList = async () => (await fetch(`${baseUrl}/api/memory-protection/normal-running-points`)).json();
  const createElsewhere = async () => (await fetch(`${baseUrl}/api/memory-protection/normal-running-point`, { method: "POST" })).json();
  const panel = () => page.locator(".memory-protection-settings");
  const savedRow = () => panel().locator(".memory-protection-saved-points");
  const pointRow = () => panel().locator(".settings-row").filter({ has: page.getByText(copy.pointTitle, { exact: true }) });
  const items = () => savedRow().locator(".memory-protection-saved-point");
  const fact = (scope, label) => scope.locator(".memory-protection-fact")
    .filter({ has: page.locator("dt").getByText(label, { exact: true }) }).locator("dd").first();
  const factText = async (scope, label) => (await fact(scope, label).innerText()).trim();
  const item = at => items().filter({ has: page.locator("dd").getByText(time(at), { exact: true }) });
  const selectButton = at => savedRow().getByRole("button", { name: `${copy.pointItemLabel(time(at))} · ${copy.selectPoint}`, exact: true });
  const inEffectButton = at => savedRow().getByRole("button", { name: `${copy.pointItemLabel(time(at))} · ${copy.pointInEffect}`, exact: true });
  const deleteButton = at => savedRow().getByRole("button", { name: `${copy.pointItemLabel(time(at))} · ${copy.deletePoint}`, exact: true });
  const confirmDelete = () => page.getByRole("dialog", { name: copy.confirmDeletePoint, exact: true });
  async function open() {
    await page.goto(baseUrl, { waitUntil: "domcontentloaded" });
    await page.getByRole("navigation").getByRole("button", { name: app.page.settings, exact: true }).click();
    await page.getByRole("button", { name: settingsText.sections.memoryProtection, exact: true }).click();
    await page.getByText(copy.intro, { exact: true }).waitFor();
    await panel().locator(".memory-protection-effective").waitFor();
    await waitForContentSettled(page);
  }
  // 界面列出的点（时间、是否生效）和后端列表逐项相同。
  async function expectShown(expected) {
    const list = await backendList();
    assert.deepEqual(list.points.map(point => point.id), expected.map(point => point.id), "backend list");
    await page.waitForFunction(count => document.querySelectorAll(".memory-protection-saved-point").length === count, expected.length);
    const shown = await items().evaluateAll(nodes => nodes.map(node => ({
      at: node.querySelector(".memory-protection-fact dd")?.textContent?.trim(), inEffect: String(node.classList.contains("in-effect")),
      current: node.getAttribute("aria-current") })));
    assert.deepEqual(shown, list.points.map(point => ({ at: time(point.at), inEffect: String(point.id === list.activeId),
      current: point.id === list.activeId ? "true" : null })), "shown points and the one in effect follow the backend list");
    assert.equal(await factText(savedRow(), copy.savedPointsCount), `${list.points.length} / ${list.limit}`);
    return list;
  }

  // 1. 读取中的占位：列表请求挂起时这一行只占一行，和没有已保存的点时同高，正在读取读给读屏。
  await scenario({ memoryProtection: "no-point" });
  let release;
  const held = new Promise(resolve => { release = resolve; });
  await page.route("**/api/memory-protection/normal-running-points", async route => {
    if (route.request().method() === "GET") await held;
    await route.continue();
  });
  await page.goto(baseUrl, { waitUntil: "domcontentloaded" });
  await page.getByRole("navigation").getByRole("button", { name: app.page.settings, exact: true }).click();
  await page.getByRole("button", { name: settingsText.sections.memoryProtection, exact: true }).click();
  await savedRow().getByRole("status").getByText(copy.savedPointsLoading, { exact: true }).waitFor({ state: "attached" });
  assert.equal(await factText(savedRow(), copy.savedPointsCount), "--");
  const loadingHeight = await savedRow().evaluate(node => node.getBoundingClientRect().height);
  release();
  await savedRow().getByText(copy.savedPointsEmpty, { exact: true }).waitFor();
  const emptyHeight = await savedRow().evaluate(node => node.getBoundingClientRect().height);
  assert.ok(Math.abs(loadingHeight - emptyHeight) < 0.5, `loading ${loadingHeight}px and empty ${emptyHeight}px rows differ`);
  await page.unroute("**/api/memory-protection/normal-running-points");
  await expectShown([]);

  // 2. 新建两次：每个新点成为生效的点，之前的点留在列表里，上面那一格显示生效的点。
  await pointRow().getByRole("button", { name: copy.createPoint, exact: true }).click();
  await pointRow().getByText(copy.pointCreated, { exact: true }).waitFor();
  let list = await expectShown((await backendList()).points);
  assert.equal(list.points.length, 1);
  await pointRow().getByRole("button", { name: copy.createPoint, exact: true }).click();
  await page.waitForFunction(() => document.querySelectorAll(".memory-protection-saved-point").length === 2);
  list = await expectShown((await backendList()).points);
  const [first, second] = list.points;
  assert.equal(list.activeId, second.id, "the new point is in effect");
  await fact(pointRow(), copy.pointAt).getByText(time(second.at), { exact: true }).waitFor();
  assert.equal(await inEffectButton(second.at).isDisabled(), true, "the point in effect cannot be selected again");
  assert.equal(await factText(item(first.at), copy.pointCommit), "200 / 1000 B");
  assert.equal(await factText(item(first.at), copy.pointProcessCount), "2");

  // 3. 把较早的点设为生效：发 PUT（带 id），进行中按钮写“正在切换…”且其他按钮不可按；完成后标记和上面那一格都换成它。
  await scenario({ memoryProtectionPointChangeDelayMs: 600 });
  await selectButton(first.at).click();
  await selectButton(first.at).getByText(copy.selectingPoint, { exact: true }).waitFor();
  assert.equal(await deleteButton(second.at).isDisabled(), true, "other point actions wait for the running one");
  assert.equal(await pointRow().getByRole("button", { name: copy.createPoint, exact: true }).isDisabled(), true);
  await savedRow().getByText(copy.pointSelected, { exact: true }).waitFor();
  await scenario({ memoryProtectionPointChangeDelayMs: 0 });
  assert.equal(writes.at(-1), `PUT /api/memory-protection/normal-running-points/active {"id":"${first.id}"}`);
  list = await expectShown([first, second]);
  assert.equal(list.activeId, first.id);
  await fact(pointRow(), copy.pointAt).getByText(time(first.at), { exact: true }).waitFor();
  await savedRow().scrollIntoViewIfNeeded();
  await memoryProtectionShot(page, "saved-points-two");

  // 4. 删除不生效的点：确认框写明点和后果；取消不删，确认后删掉。
  await deleteButton(second.at).click();
  let dialogText = await confirmDelete().textContent();
  assert.ok(dialogText.includes(copy.pointItemLabel(time(second.at))) && dialogText.includes(copy.deletePointDetail), dialogText);
  await confirmDelete().getByRole("button", { name: app.feedback.cancel, exact: true }).click();
  await confirmDelete().waitFor({ state: "detached" });
  assert.equal((await backendList()).points.length, 2, "cancelling deletes nothing");
  await deleteButton(second.at).click();
  await confirmDelete().getByRole("button", { name: copy.deletePoint, exact: true }).click();
  await savedRow().getByText(copy.pointDeleted, { exact: true }).waitFor();
  assert.equal(writes.at(-1), `DELETE /api/memory-protection/normal-running-points/${second.id}`);
  await expectShown([first]);

  // 5. 别处新建了一个点（另一个客户端或脚本）：生效的点换了，列表随订阅重读，新点标为生效。
  const elsewhere = await createElsewhere();
  await inEffectButton(elsewhere.at).waitFor();
  list = await expectShown([first, elsewhere]);
  assert.equal(list.activeId, elsewhere.id);

  // 6. 删除生效的点：确认框写明删除后保护停止生效；删除后上面那一格写“没有正常运行点”，保护未生效。
  await deleteButton(elsewhere.at).click();
  dialogText = await confirmDelete().textContent();
  assert.ok(dialogText.includes(copy.deleteActivePointDetail), dialogText);
  await confirmDelete().getByRole("button", { name: copy.deletePoint, exact: true }).click();
  await savedRow().getByText(copy.pointDeleted, { exact: true }).waitFor();
  list = await expectShown([first]);
  assert.equal(list.activeId, null, "deleting the point in effect leaves none in effect");
  await pointRow().getByText(copy.pointNone, { exact: true }).waitFor();
  await page.waitForFunction(text => document.querySelector(".memory-protection-effective")?.textContent === text,
    copy.inactive(copy.reasons["no-normal-running-point"]));
  await selectButton(first.at).click();
  await savedRow().getByText(copy.pointSelected, { exact: true }).waitFor();
  await fact(pointRow(), copy.pointAt).getByText(time(first.at), { exact: true }).waitFor();

  // 7. 满额：后端以 normal-running-point-limit 拒绝，界面写出后端的原因，什么也不删。
  await scenario({ memoryProtectionPointLimit: 1 });
  await open();
  await expectShown([first]);
  await pointRow().getByRole("button", { name: copy.createPoint, exact: true }).click();
  await pointRow().getByRole("alert").getByText(copy.failedBecause(copy.createPointFailed, copy.reasons["normal-running-point-limit"]),
    { exact: true }).waitFor();
  await expectShown([first]);

  // 8. 不生效的点已经被别处删掉（生效的点没变，没有推送）时再删：后端 404 normal-running-point-not-found，
  //    界面写出原因，列表重读后那一项消失。
  await scenario({ memoryProtectionPointLimit: 2 });
  const latest = await createElsewhere();
  await inEffectButton(latest.at).waitFor();
  await fetch(`${baseUrl}/api/memory-protection/normal-running-points/${first.id}`, { method: "DELETE" });
  await deleteButton(first.at).click();
  await confirmDelete().getByRole("button", { name: copy.deletePoint, exact: true }).click();
  await savedRow().getByRole("alert").getByText(copy.failedBecause(copy.deletePointFailed, copy.reasons["normal-running-point-not-found"]),
    { exact: true }).waitFor();
  await expectShown([latest]);

  assert.deepEqual(errors, []);
  await fetch(`${baseUrl}/__test/scenario/reset`, { method: "POST" });
  return { steps: 8, writes };
}
