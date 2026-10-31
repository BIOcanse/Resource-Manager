interface Backend { catalogDefault: boolean; kindDefault: boolean; user: { level: number; source: string } | null; raw: unknown }
interface ProtectionEntry { id: string; targetType: string; targetKey: string; softwareId: string | null; softwareName: string | null; softwareKind: string; protectedAt: string; protectionSource: string; createdFromReportId: string; lastVerifiedAt: string | null; state: string; allowsPlacementAvoidance: boolean; protectionLevel: number; defaultProtectionLevel: number | null; defaultProtectionSource: string | null; policyGroupId: string | null; processName: string | null; executablePath: string | null }
import type { Page } from "playwright-core";
import assert from "node:assert/strict";

// 软件详情 → 策略 → 保护：开关下面一行说当前状态从哪里来（docs/scheduling_protection_2026-10-29.md §3.2、§3.3）。
// 保护列表用页面路由模拟后端（OptimizationProtectionService）：一个软件有用户条目时用户条目决定（替换了默认就带默认的
// 等级和来源）；没有时软件目录的默认决定；都没有时软件类型的默认（software_kind，系统服务默认）决定。
// 开关写入后前端重读列表；格式不对的列表要显示失败，不能当作没有保护。
const softwareId = "frontend-harness.software";

export default async function softwareProtectionSourceJourney(page: Page, baseUrl: string) {
  const errors : string[] = [];
  page.on("pageerror", error => errors.push(String(error)));
  const scenario = await fetch(`${baseUrl}/__test/scenario`, {
    method: "POST", headers: { "Content-Type": "application/json" },
    body: JSON.stringify({ gpuPlacementEnabled: true, runtimeEffectOwners: true })
  });
  if (!scenario.ok) throw new Error("Unable to enable GPU placement and runtime controls");

  // 模拟后端的状态：软件目录默认、软件类型（other）的系统默认、用户条目；raw 不为 null 时原样返回。
  const backend: Backend = { catalogDefault: true, kindDefault: false, user: null, raw: null };
  const writes: boolean[] = [];
  await page.route("**/api/optimization/protection", route => route.fulfill({ json: backend.raw ?? protectionList(backend) }));
  await page.route("**/api/optimization/protection/software/*", async route => {
    assert.equal(route.request().method(), "PUT");
    const { enabled } = route.request().postDataJSON();
    writes.push(enabled);
    // 后端 SetSoftwareProtectionAsync：打开写 1 级用户条目；关掉时有软件目录默认就写 0 级条目，否则删掉用户条目。
    backend.user = enabled ? { level: 1, source: "software_detail" } : backend.catalogDefault ? { level: 0, source: "software_detail" } : null;
    await route.fulfill({ json: null });
  });

  await page.goto(baseUrl, { waitUntil: "domcontentloaded" });
  await page.getByRole("button", { name: "组件与软件", exact: true }).click();
  await page.getByRole("tablist", { name: "组件和软件分类" }).getByRole("tab", { name: /一般应用/ }).click();
  const card = page.locator(".software-item").filter({ hasText: "示例软件" });
  const dialog = page.getByRole("dialog", { name: "示例软件" });
  const group = dialog.getByRole("group", { name: "保护", exact: true });
  const protection = group.getByRole("checkbox", { name: "启用保护", exact: true });
  const sourceLine = group.locator(".software-protection-source");

  // 1 软件目录默认：开关开着，来源是软件目录；来源一行和说明同一样式，排在说明下面。
  await openPolicy();
  assert.equal(await protection.isChecked(), true, "A catalog default protects");
  await expectLine("来源：软件目录（保护）");
  assert.deepEqual(await group.locator("p.software-detail-hint").evaluateAll(nodes => nodes.map(node => node.className)),
    ["software-detail-hint", "software-detail-hint software-protection-source"], "The source line sits under the hint, styled like it");

  // 2 用户把默认关掉：0 级用户条目替换默认，两样都说。
  await protection.uncheck();
  await settled();
  assert.equal(await protection.isChecked(), false);
  await expectLine("默认：软件目录（保护）；已由你在软件详情中改为不保护");

  // 3 再打开：仍是用户条目，默认照样显示。
  await protection.check();
  await settled();
  await expectLine("默认：软件目录（保护）；已由你在软件详情中改为保护");
  assert.deepEqual(writes, [false, true]);

  // 4 只有软件类型的系统默认：开关显示它的状态，来源是系统服务默认。
  await reopen(() => Object.assign(backend, { catalogDefault: false, kindDefault: true, user: null }));
  assert.equal(await protection.isChecked(), true, "Only the kind default applies: it decides");
  await expectLine("来源：系统服务默认（保护）");
  // 关掉开关只删用户条目，后端这个端点不能为单个软件关掉类型默认；重读后开关如实回到系统默认的状态。
  await protection.click();
  await settled();
  assert.equal(await protection.isChecked(), true, "The switch shows the re-read state, not the click");
  await expectLine("来源：系统服务默认（保护）");

  // 5 来自报告的用户条目替换了软件目录默认。
  await reopen(() => Object.assign(backend, { catalogDefault: true, kindDefault: true, user: { level: 1, source: "report" } }));
  assert.equal(await protection.isChecked(), true);
  await expectLine("默认：软件目录（保护）；已由你在报告中改为保护");

  // 6 不认识的来源：原样显示，不隐藏这一行。
  await reopen(() => Object.assign(backend, { catalogDefault: false, kindDefault: false, user: { level: 1, source: "future_source" } }));
  await expectLine("来源：future_source（保护）");

  // 7 没有条目适用：开关关着，没有来源一行。
  await reopen(() => Object.assign(backend, { catalogDefault: false, kindDefault: false, user: null }));
  assert.equal(await protection.isChecked(), false);
  assert.equal(await sourceLine.count(), 0, "No entry applies: no source line");

  // 8 格式不对的列表：显示读取失败，开关不可用，不当作没有保护。
  await reopen(() => { backend.raw = [{ softwareId, targetKey: `software:${softwareId}`, protectionLevel: 1 }]; }, false);
  await group.getByRole("alert").getByText("无法读取或保存保护设置，请重新打开软件详情。", { exact: true }).waitFor();
  assert.equal(await protection.isDisabled(), true, "A malformed list must not leave the switch usable");
  assert.equal(await sourceLine.count(), 0);
  backend.raw = null;

  assert.equal(errors.length, 0, `Protection source page errors: ${JSON.stringify(errors)}`);
  return { sourceLines: 7, malformedListFailsVisibly: true, writes: writes.length };

  async function openPolicy(waitReady = true) {
    await card.getByRole("button", { name: "软件设置", exact: true }).click();
    await dialog.getByRole("tab", { name: "策略", exact: true }).click();
    await dialog.getByRole("heading", { name: "调度参与", exact: true }).waitFor();
    if (waitReady) await settled();
  }
  async function reopen(change: () => unknown, waitReady = true) {
    await dialog.locator("button.icon-button").first().click();
    await dialog.waitFor({ state: "detached" });
    change();
    await openPolicy(waitReady);
  }
  async function settled() {
    await page.waitForFunction(() => !document.querySelector<HTMLInputElement>(".software-protection-section:not([data-exit-ghost] *) input")?.disabled);
  }
  async function expectLine(text: string) {
    await group.getByText(text, { exact: true }).waitFor();
    assert.equal(await sourceLine.count(), 1);
    assert.equal(await sourceLine.textContent(), text);
  }
}

function protectionList(backend: Backend) {
  const entries = [];
  if (backend.kindDefault) {
    entries.push(entry({ id: "software_kind:software-kind:other", targetType: "software_kind", targetKey: "software-kind:other",
      softwareId: null, softwareName: null, protectionSource: "system_default", createdFromReportId: "system-default" }));
  }
  if (backend.user) {
    entries.push(entry({ protectionSource: backend.user.source, protectionLevel: backend.user.level,
      defaultProtectionLevel: backend.catalogDefault ? 1 : null,
      defaultProtectionSource: backend.catalogDefault ? "software_catalog" : null,
      policyGroupId: backend.catalogDefault ? "meeting-capture-password" : null }));
  } else if (backend.catalogDefault) {
    entries.push(entry({ protectionSource: "software_catalog", createdFromReportId: "software-catalog",
      policyGroupId: "meeting-capture-password" }));
  }
  return entries;
}

function entry(overrides: Partial<ProtectionEntry>): ProtectionEntry {
  return {
    id: `software:software:${softwareId}`, targetType: "software", targetKey: `software:${softwareId}`,
    softwareId, softwareName: "示例软件", softwareKind: "other", protectedAt: "2026-10-29T08:00:00.000Z",
    protectionSource: "software_detail", createdFromReportId: "software-detail", lastVerifiedAt: null,
    state: "active", allowsPlacementAvoidance: true, protectionLevel: 1,
    defaultProtectionLevel: null, defaultProtectionSource: null, policyGroupId: null, processName: null, executablePath: null,
    ...overrides
  };
}
