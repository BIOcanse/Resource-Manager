import assert from "node:assert/strict";

/**
 * X80：开关型能力的三选一（交还固件／开启／关闭）。
 *
 * 交还固件 = 期望状态里没有这一项；开启／关闭 = 保留这一项，toggle 为 true／false。
 * 检查保存发出的整份设定、重载后 false 仍显示为「关闭」，以及交还固件失败时的反馈。
 */
export default async function controlToggleChoiceJourney(page, baseUrl) {
  const objectId = "gpu:toggle";
  const capabilityId = "gpu.dynamic-boost-enabled";
  let settings = [];
  let outcomes = [];
  const writes = [];
  const state = () => ({
    desired: { objects: settings.length ? [{ objectId, settings }] : [] },
    lastApply: { outcomes, appliedAt: new Date().toISOString() }
  });
  await page.route("**/api/control/objects", route => route.fulfill({ json: {
    readAt: new Date().toISOString(),
    objects: [{
      id: objectId, kind: "gpu", displayName: "Toggle GPU", isControllable: true,
      platform: { operatingSystem: "windows", vendor: "amd" },
      capabilities: [{ id: capabilityId, label: "Dynamic Boost", valueKind: "toggle", supported: true, requiredAccessLevel: "normal" }]
    }]
  } }));
  await page.route("**/api/control/state**", route => {
    if (route.request().method() !== "PUT") return route.fulfill({ json: state() });
    const next = route.request().postDataJSON();
    writes.push(next);
    const released = settings.filter(previous => !next.some(entry => entry.capabilityId === previous.capabilityId));
    settings = next;
    outcomes = [
      ...next.map(entry => ({ objectId, capabilityId: entry.capabilityId, status: "applied", message: null })),
      ...released.map(entry => ({ objectId, capabilityId: entry.capabilityId, status: "failed", message: "Restore failed" }))
    ];
    return route.fulfill({ json: state() });
  });

  const openControl = async () => {
    await page.goto(baseUrl, { waitUntil: "domcontentloaded" });
    await page.getByRole("navigation", { name: "页面切换" }).getByRole("button", { name: "控制面", exact: true }).click();
    return page.getByRole("radiogroup", { name: "Dynamic Boost 开关", exact: true });
  };
  const checked = async (group, name) =>
    await group.getByRole("radio", { name, exact: true }).getAttribute("aria-checked") === "true";
  const apply = async () => {
    const before = writes.length;
    await page.getByRole("button", { name: "应用", exact: true }).click();
    await page.waitForFunction(() => document.querySelector(".control-object-actions button:last-child")?.disabled === true);
    assert.equal(writes.length, before + 1, "Apply must send exactly one write");
    return writes.at(-1);
  };

  let group = await openControl();
  await page.getByRole("radio", { name: "软件管理", exact: true }).click();
  assert.equal(await checked(group, "交还固件"), true, "No saved setting shows Return to firmware");
  assert.equal(await page.getByRole("checkbox", { name: "Dynamic Boost 开关", exact: true }).count(), 0);

  await group.getByRole("radio", { name: "关闭", exact: true }).click();
  assert.deepEqual(await apply(), [{ capabilityId, toggle: false }], "Off is saved as toggle=false, not as an absent setting");

  group = await openControl();
  await group.getByRole("radio", { name: "关闭", exact: true }).waitFor();
  assert.equal(await checked(group, "关闭"), true, "Reloaded false setting shows Off");
  assert.equal(await page.getByRole("radio", { name: "软件管理", exact: true }).getAttribute("aria-checked"), "true");

  await group.getByRole("radio", { name: "开启", exact: true }).click();
  assert.deepEqual(await apply(), [{ capabilityId, toggle: true }]);

  await group.getByRole("radio", { name: "交还固件", exact: true }).click();
  assert.deepEqual(await apply(), [], "Return to firmware removes the setting instead of writing false");
  const tag = page.locator(".control-capability-tag").filter({ hasText: "没应用上" });
  await tag.waitFor();
  assert.equal(await tag.getAttribute("title"), "Restore failed", "A failed release shows the backend reason");
  return { writes };
}
