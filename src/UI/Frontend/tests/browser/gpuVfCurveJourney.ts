import assert from "node:assert/strict";
import type { Page } from "playwright-core";
import type { GpuVfCurveBinding, GpuVfCurveEditorRules, GpuVfCurvePoint, GpuVfCurvePreset, GpuVfCurveCommand } from "../../src/features/control/gpuVfCurveTypes.ts";

/** A backend-owned mock draft; every edit is logged, with no hardware route. */
export default async function gpuVfCurveJourney(page: Page, baseUrl: string) {
  const readAt = "2026-10-31T12:00:00Z", objectId = "gpu:curve-test";
  const binding: GpuVfCurveBinding = { vendor: "nvidia", deviceIdentity: objectId, model: "Test GPU", pointSignature: "native-table", scope: "device" };
  const rules: GpuVfCurveEditorRules = { fineStepKhz: 15000, coarseStepKhz: 75000, dragQuantizationKhz: 15000, frequencyDecimals: 1, voltageDecimals: 1,
    axisPaddingRatio: .1, hitRadiusPixels: 7, maximumPresetCount: 16, maximumNameLength: 48, maximumDraftCount: 4, undoCapacity: 20,
    defaultBindingScope: "device", safetyMinimumOffsetKhz: null, safetyMaximumOffsetKhz: null, readbackToleranceKhz: 0 };
  const points = [7, 9, 11].map((pointId, index) => ({ pointId, voltageMicrovolts: 800000 + index * 50000, defaultFrequencyKhz: 1500000 + index * 150000,
    currentFrequencyKhz: 1500000 + index * 150000, defaultOffsetKhz: 0, currentOffsetKhz: 0, minimumOffsetKhz: -150000, maximumOffsetKhz: 300000 }));
  const descriptor = { objectId, scalarOnly: false, available: true, writable: false, unavailableReason: { code: "nvidia-vf-restore-protocol-unverified" }, binding, points, rules, readAt };
  let presets: GpuVfCurvePreset[] = [], values: GpuVfCurvePoint[] = points.map(point => ({ pointId: point.pointId, voltageMicrovolts: point.voltageMicrovolts, frequencyOffsetKhz: 0 }));
  const undo: GpuVfCurvePoint[][] = [], redo: GpuVfCurvePoint[][] = [], commands: GpuVfCurveCommand[] = [], hardwareWrites: unknown[] = [];
  const response = () => ({ id: "backend-draft", objectId, binding, points: values, descriptor, canUndo: undo.length > 0, canRedo: redo.length > 0 });
  await page.route("**/api/control/objects", route => route.fulfill({ json: { readAt, objects: [{ id: objectId, kind: "gpu", reportedName: "Curve test GPU",
    platform: { vendor: "nvidia", operatingSystem: "windows" }, terms: [], isControllable: true, gpuAttachment: "discrete", capabilities: [{ id: "gpu.core-vf-curve",
      valueKind: "gpu-vf-curve-reference", supported: false, unavailableKind: "not-implemented", unavailableReason: descriptor.unavailableReason, requiredAccessLevel: "normal", range: null, channel: "nvapi" }] }] } }));
  await page.route("**/api/control/state**", route => {
    if (route.request().method() === "PUT") hardwareWrites.push(route.request().postDataJSON());
    return route.fulfill({ json: { desired: { objects: [] }, lastApply: { outcomes: [], appliedAt: readAt } } });
  });
  await page.route("**/api/control/objects/*/gpu-vf-curve", route => route.fulfill({ json: descriptor }));
  await page.route("**/api/control/gpu-vf-curves**", route => {
    const request = route.request(), path = new URL(request.url()).pathname;
    if (path.endsWith("/editor/command")) {
      const command = request.postDataJSON() as GpuVfCurveCommand; commands.push(command);
      if (command.operation === "close") return route.fulfill({ json: { closed: true } });
      if (command.operation === "begin") {
        undo.length = 0; redo.length = 0;
        values = command.curveId ? structuredClone(presets.find(preset => preset.id === command.curveId)!.points) as GpuVfCurvePoint[]
          : points.map(point => ({ pointId: point.pointId, voltageMicrovolts: point.voltageMicrovolts, frequencyOffsetKhz: point.currentOffsetKhz }));
      } else if (command.operation === "undo") { redo.push(values); values = undo.pop()!; }
      else if (command.operation === "redo") { undo.push(values); values = redo.pop()!; }
      else {
        undo.push(structuredClone(values)); redo.length = 0;
        const ids = command.pointIds ?? [];
        if (command.operation === "uniform-offset") values = values.map(point => ({ ...point, frequencyOffsetKhz: command.offsetKhz! }));
        else if (command.operation === "step" || command.operation === "move-selection") values = values.map(point => ({ ...point,
          frequencyOffsetKhz: point.frequencyOffsetKhz + (ids.includes(point.pointId) ? command.operation === "step"
            ? command.direction! * (command.coarse ? rules.coarseStepKhz : rules.fineStepKhz) : command.deltaKhz! : 0) }));
        else if (command.operation === "reset-default") values = values.map(point => ({ ...point, frequencyOffsetKhz: 0 }));
      }
      return route.fulfill({ json: response() });
    }
    if (path.endsWith("/references")) return route.fulfill({ json: { references: [{ kind: "preset", id: "gpu-preset", objectId, name: "GPU shared profile" }] } });
    if (request.method() === "PUT") {
      const save = request.postDataJSON() as { name: string }, preset = { id: "saved-curve", name: save.name, binding, points: structuredClone(values), createdAt: readAt, updatedAt: readAt };
      presets = [preset]; return route.fulfill({ json: preset });
    }
    return route.fulfill({ json: { presets: presets.map(preset => ({ preset, applicable: true, unavailableReason: null })), rules } });
  });
  await page.setViewportSize({ width: 1400, height: 1000 });
  await page.goto(baseUrl, { waitUntil: "domcontentloaded" });
  await page.getByRole("navigation", { name: "页面切换" }).getByRole("button", { name: "控制面", exact: true }).click();
  const card = page.locator(".control-object").filter({ hasText: "Curve test GPU" });
  await card.getByRole("radio", { name: "软件管理", exact: true }).click();
  await card.getByRole("checkbox", { name: "电压–频率曲线", exact: true }).check();
  await card.getByRole("button", { name: "打开曲线库", exact: true }).click();
  const editor = card.locator(".gpu-vf-editor"); await editor.locator(".fan-curve-chart").waitFor();
  assert.equal(await card.getByRole("button", { name: "应用", exact: true }).isDisabled(), true, "Read-only hardware cannot be applied");
  await editor.getByRole("button", { name: "全选", exact: true }).click();
  const selection = editor.getByRole("group", { name: "选择曲线点", exact: true });
  await selection.focus(); await page.keyboard.press("ArrowUp");
  await page.waitForFunction(() => [...document.querySelectorAll(".gpu-vf-points tbody tr td:last-child")].every(cell => cell.textContent === "15.0"));
  assert.deepEqual(commands.at(-1)!.pointIds, [7, 9, 11], "Group commands carry all native IDs");
  await editor.getByLabel("统一偏移 (MHz)", { exact: true }).fill("30"); await page.keyboard.press("Tab");
  await page.waitForFunction(() => [...document.querySelectorAll(".gpu-vf-points tbody tr td:last-child")].every(cell => cell.textContent === "30.0"));
  assert.equal(commands.at(-1)!.operation, "uniform-offset");
  await editor.getByRole("button", { name: "撤销", exact: true }).click();
  await page.waitForFunction(() => [...document.querySelectorAll(".gpu-vf-points tbody tr td:last-child")].every(cell => cell.textContent === "15.0"));
  await editor.getByRole("button", { name: "重做", exact: true }).click();
  await page.waitForFunction(() => [...document.querySelectorAll(".gpu-vf-points tbody tr td:last-child")].every(cell => cell.textContent === "30.0"));
  await editor.getByLabel("曲线名称", { exact: true }).fill("Shared GPU curve");
  await editor.getByRole("button", { name: "保存曲线", exact: true }).click();
  await page.waitForFunction(() => document.querySelector(".gpu-vf-editor .standard-select-trigger")?.textContent!.includes("Shared GPU curve"));
  await editor.getByRole("button", { name: "删除", exact: true }).click();
  await editor.getByText("GPU shared profile", { exact: false }).waitFor();
  assert.equal(presets.length, 1, "Referenced curves remain in the library");
  const closed = page.waitForResponse(response => response.url().endsWith("/gpu-vf-curves/editor/command")
    && response.request().postDataJSON()?.operation === "close");
  await editor.getByRole("button", { name: "关闭编辑器", exact: true }).click(); await closed;
  assert.equal(commands.at(-1)!.operation, "close", "Closing releases the backend draft");
  assert.deepEqual(hardwareWrites, [], "Editing and saving a curve never applies hardware");
  return { commands: commands.map(command => command.operation), hardwareWrites: hardwareWrites.length };
}
