import { frontendRefreshIntervals } from "./fixtures/frontendRefreshIntervalFixture.ts";
import { resolveSelfCpuRuntimePlan } from "../src/app/selfScheduling/resolveSelfCpuRuntimePlan.ts";
import { updateReportRuleField } from "../src/features/settings/reportRuleSettingsModel.ts";
import { RequestProblem } from "../src/frontendRuntime/request/RequestProblem.ts";
import assert from "node:assert/strict";
import { createComputed, createRoot } from "solid-js";
import type { CommittedAppSettingsResult } from "../src/data/appSettings/appSettingsResultDecoder.ts";
import type {
  SourceDemand,
  SourceHandle,
  SourceLease
} from "../src/frontendRuntime/source/SourceDescriptor.ts";
import type { SourceSnapshot } from "../src/frontendRuntime/source/SourceSnapshot.ts";
import {
  createSettingsStore,
  defaultAppSettings,
  logicRefreshIntervalKeys,
  normalizeAppSettings
} from "../src/stores/settingsStore.ts";
import { applySettingsPatch } from "../src/features/settings/settingsPatch.ts";
import type { AppSettings } from "../src/types.ts";

const canonicalDefaults = defaultAppSettings();
assert.equal(canonicalDefaults.version, "1.0.28", "Frontend defaults must carry the backend App settings format");
assert.equal(canonicalDefaults.performance?.preciseGpuPlacementEnabled, false);
assert.equal(canonicalDefaults.publicService?.enabled, false);
assert.equal(canonicalDefaults.publicService?.fileIndexEnabled, false);
assert.equal(canonicalDefaults.publicService?.databaseServiceEnabled, false);
assert.equal(canonicalDefaults.publicService?.aiModelCatalogEnabled, false);
assert.equal(canonicalDefaults.aiModelService?.autoStartEnabled, false);

assert.deepEqual(canonicalDefaults.control, { reassertionIntervalSeconds: 120 });
assert.deepEqual(normalizeAppSettings({ control: { reassertionIntervalSeconds: 0 } }).control,
  { reassertionIntervalSeconds: 1 });
assert.equal(normalizeAppSettings({ control: { reassertionIntervalSeconds: 999999 } }).control!.reassertionIntervalSeconds, 86400);
assert.equal(normalizeAppSettings({ control: { reassertionIntervalSeconds: NaN } }).control!.reassertionIntervalSeconds, 120);

const normalizedMissingFields = normalizeAppSettings({ version: "1.0.23" });
assert.equal(normalizedMissingFields.performance?.preciseGpuPlacementEnabled, false);
assert.equal(normalizedMissingFields.publicService?.enabled, false);
assert.equal(normalizedMissingFields.publicService?.fileIndexEnabled, false);
assert.equal(normalizedMissingFields.publicService?.databaseServiceEnabled, false);
assert.equal(normalizedMissingFields.publicService?.aiModelCatalogEnabled, false);
assert.equal(normalizedMissingFields.aiModelService?.autoStartEnabled, false);

// 用户裁定删除的死设置：默认设置和归一化结果里都不能再有这些键；旧文件里带着也只会被丢弃。
// 按需监控只剩开、关：旧值 auto 读成开，显式 disabled 保持，缺省为开。
{
  const retiredPerformanceKeys = [
    "smartMonitoringEnabled",
    "monitoringIdleSeconds",
    "pauseFrontendRefreshWhenHiddenInNormalMode",
    "frontendHiddenRefreshMode",
    "managementRefreshIntervalMs",
    "discoveryRefreshIntervalMs",
    "optimizationRefreshIntervalMs"
  ];
  const retiredAppearanceKeys = [
    "resourceBarHardwareAccelerationEnabled",
    "resourceBarHardwareAccelerationMode"
  ];
  const legacy = {
    version: "1.0.26",
    performance: {
      smartMonitoringEnabled: false,
      monitoringIdleSeconds: 5,
      pauseFrontendRefreshWhenHiddenInNormalMode: false,
      frontendHiddenRefreshMode: "continueWhenHidden",
      smartMonitoringMode: "auto",
      managementRefreshIntervalMs: { mode: "custom", preset: "balanced", customValue: 7000 },
      discoveryRefreshIntervalMs: { mode: "preset", preset: "quiet", customValue: 30000 },
      optimizationRefreshIntervalMs: { mode: "aotu", preset: "balanced", customValue: 10000 }
    },
    appearance: {
      uiStyle: "standard",
      theme: "system",
      resourceBarHardwareAccelerationEnabled: false,
      resourceBarHardwareAccelerationMode: "disabled"
    }
  } as unknown as AppSettings;
  const normalizedLegacy = normalizeAppSettings(legacy);
  for (const [label, settings] of [
    ["defaults", canonicalDefaults],
    ["normalized legacy", normalizedLegacy],
    ["normalized missing", normalizedMissingFields]
  ] as const) {
    assert.equal(settings.version, "1.0.28", `${label}: version`);
    for (const key of retiredPerformanceKeys) {
      assert.equal(key in settings.performance!, false, `${label}: performance.${key} must be gone`);
    }
    for (const key of retiredAppearanceKeys) {
      assert.equal(key in settings.appearance!, false, `${label}: appearance.${key} must be gone`);
    }
  }
  assert.deepEqual(logicRefreshIntervalKeys, ["monitor", "resourceTable", "localSystem"]);
  assert.equal(canonicalDefaults.performance!.smartMonitoringMode, "enabled");
  assert.equal(normalizedMissingFields.performance!.smartMonitoringMode, "enabled");
  assert.equal(normalizedLegacy.performance!.smartMonitoringMode, "enabled",
    "Legacy auto must read as on, regardless of the retired boolean copy");
  assert.equal(normalizeAppSettings({
    version: "1.0.26",
    performance: { smartMonitoringMode: "disabled" }
  } as unknown as AppSettings).performance!.smartMonitoringMode, "disabled", "Explicit off must stay off");
}

// 独显溢出阈值：默认值与后端一致（95 / 80），各自按 1–100 取整归一化。
{
  assert.equal(canonicalDefaults.performance!.gpuOverflowUsagePercent, 95);
  assert.equal(canonicalDefaults.performance!.gpuOverflowDedicatedMemoryPercent, 80);
  assert.equal(normalizedMissingFields.performance!.gpuOverflowUsagePercent, 95);
  assert.equal(normalizedMissingFields.performance!.gpuOverflowDedicatedMemoryPercent, 80);
  const clamped = normalizeAppSettings({
    version: "1.0.28",
    performance: {
      gpuOverflowUsagePercent: 120,
      gpuOverflowDedicatedMemoryPercent: -4
    }
  } as unknown as AppSettings).performance!;
  assert.equal(clamped.gpuOverflowUsagePercent, 100);
  assert.equal(clamped.gpuOverflowDedicatedMemoryPercent, 1);
  const rounded = normalizeAppSettings({
    version: "1.0.28",
    performance: {
      gpuOverflowUsagePercent: 33.6,
      gpuOverflowDedicatedMemoryPercent: Number.NaN
    }
  } as unknown as AppSettings).performance!;
  assert.equal(rounded.gpuOverflowUsagePercent, 34);
  assert.equal(rounded.gpuOverflowDedicatedMemoryPercent, 80, "A non-number falls back to the default");
}

class FakeSettingsSource implements SourceHandle<CommittedAppSettingsResult> {
  readonly key = "app.settings";
  private readonly listeners = new Set<
    (snapshot: SourceSnapshot<CommittedAppSettingsResult>) => void
  >();
  private snapshotValue = snapshot("loading", null, 0);
  private refreshResult: Promise<SourceSnapshot<CommittedAppSettingsResult>> | null = null;
  private resolveRefresh: ((value: SourceSnapshot<CommittedAppSettingsResult>) => void) | null = null;

  acquire(_initialDemand?: SourceDemand): SourceLease<CommittedAppSettingsResult> {
    const owner = this;
    return {
      key: this.key,
      get snapshot() {
        return owner.snapshotValue;
      },
      subscribe: (listener) => {
        this.listeners.add(listener);
        return () => this.listeners.delete(listener);
      },
      setDemand: () => undefined,
      refresh: () => this.beginRefresh(),
      acceptAuthoritative: (value) => this.publishReady(value),
      release: () => this.listeners.clear()
    };
  }

  publishReady(value: CommittedAppSettingsResult): SourceSnapshot<CommittedAppSettingsResult> {
    return this.publish(snapshot(
      "ready",
      value,
      this.snapshotValue.revision + 1));
  }

  beginRefresh(): Promise<SourceSnapshot<CommittedAppSettingsResult>> {
    this.publish(snapshot(
      "refreshing",
      this.snapshotValue.data,
      this.snapshotValue.revision + 1));
    this.refreshResult = new Promise((resolve) => {
      this.resolveRefresh = resolve;
    });
    return this.refreshResult;
  }

  finishRefresh(value: CommittedAppSettingsResult): void {
    const next = this.publishReady(value);
    this.resolveRefresh?.(next);
    this.resolveRefresh = null;
    this.refreshResult = null;
  }

  private publish(
    next: SourceSnapshot<CommittedAppSettingsResult>
  ): SourceSnapshot<CommittedAppSettingsResult> {
    this.snapshotValue = next;
    for (const listener of this.listeners) {
      listener(next);
    }
    return next;
  }
}

await new Promise<void>((resolve, reject) => {
  createRoot((dispose) => {
    void (async () => {
      const source = new FakeSettingsSource();
      const requests: unknown[] = [];
      const store = createSettingsStore({
        source,
        requestClient: {
          request: async (descriptor) => {
            requests.push(JSON.parse(String(descriptor.request?.body)));
            throw new Error("Recorded only; do not persist fixture settings.");
          }
        }
      });
      const readyOptimizationModes: string[] = [];
      createComputed(() => {
        if (store.loadState() === "ready") {
          readyOptimizationModes.push(
            store.settings().performance?.optimizationMode ?? "missing");
        }
      });

      source.publishReady(result("1", "limited", "system"));
      assert.equal(store.loadState(), "ready");
      assert.equal(store.isDirty(), false);
      assert.deepEqual(readyOptimizationModes, ["limited"]);

      source.publishReady(result("2", "smart", "system"));
      assert.equal(store.settings().performance?.optimizationMode, "smart");
      assert.equal(store.draftSettings().performance?.optimizationMode, "smart");
      assert.equal(store.isDirty(), false);

      store.updateTheme("dark");
      assert.equal(store.isDirty(), true);
      const updated = result("3", "limited", "light");
      updated.settings.appearance!.barColorMode = "distinct";
      source.publishReady(updated);
      assert.equal(store.settings().appearance?.theme, "light");
      assert.equal(store.draftSettings().appearance?.theme, "dark");
      assert.equal(store.draftSettings().performance?.optimizationMode, "limited");
      assert.equal(store.isDirty(), true);
      await store.saveDraft();
      assert.deepEqual(requests.pop(), {
        expectedRevision: "3",
        changes: { appearance: { theme: "dark" } }
      }, "Saving an edited theme must preserve the newly committed, unedited bar color");

      const explicitReload = store.refresh();
      assert.equal(store.loadState(), "refreshing");
      assert.equal(store.draftSettings().appearance?.theme, "dark");
      source.finishRefresh(result("4", "normal", "system"));
      await explicitReload;
      assert.equal(store.loadState(), "ready");
      assert.equal(store.draftSettings().appearance?.theme, "system");
      assert.equal(store.isDirty(), false);

      const debugSettings = result("5", "normal", "system");
      debugSettings.settings.debug!.debugLogEnabled = true;
      debugSettings.settings.debug!.hostManagerSmartCoordinatorPerformanceLogEnabled = true;
      source.publishReady(debugSettings);
      store.updateDebugLog(false);
      await store.saveDraft();
      assert.deepEqual(requests.pop(), {
        expectedRevision: "5",
        changes: { debug: { debugLogEnabled: false } }
      }, "Disabling debug logging must not erase the separate performance-log choice");
      assert.equal(normalizeAppSettings(store.draftSettings()).debug!
        .hostManagerSmartCoordinatorPerformanceLogEnabled, true);

      store.updateTheme("dark");
      assert.equal(store.isDirty(), true);
      store.discardDraftChanges();
      assert.equal(store.draftSettings().appearance?.theme, "system");
      assert.equal(store.isDirty(), false);

      // 读数来源：默认只列首选；改成显示所有可用来源后只 PATCH 这一项，提交后读回；认不出的值回到只列首选。
      source.publishReady(result("6", "normal", "system"));
      assert.equal(store.settings().appearance?.readingSourceMode, "preferred");
      store.updateReadingSourceMode("all");
      await store.saveDraft();
      assert.deepEqual(requests.pop(), {
        expectedRevision: "6",
        changes: { appearance: { readingSourceMode: "all" } }
      }, "Showing all reading sources must patch only that choice");
      store.discardDraftChanges();
      const readingSources = result("7", "normal", "system");
      readingSources.settings.appearance!.readingSourceMode = "all";
      source.publishReady(readingSources);
      assert.equal(store.settings().appearance?.readingSourceMode, "all");
      assert.equal(store.draftSettings().appearance?.readingSourceMode, "all");
      const unknownReadingSources = result("8", "normal", "system");
      (unknownReadingSources.settings.appearance as { readingSourceMode?: string }).readingSourceMode = "everything";
      source.publishReady(unknownReadingSources);
      assert.equal(store.settings().appearance?.readingSourceMode, "preferred", "Unknown values list only the preferred source");

      dispose();
      resolve();
    })().catch((error) => {
      dispose();
      reject(error);
    });
  });
});

await new Promise<void>((resolve, reject) => {
  createRoot((dispose) => {
    void (async () => {
      const source = new FakeSettingsSource();
      const pending: Array<(value: CommittedAppSettingsResult) => void> = [];
      const store = createSettingsStore({ source, requestClient: {
        request: <T>() => new Promise<T>((complete) => pending.push((value) => complete(value as T)))
      } });
      source.publishReady(result("s1", "normal", "system"));
      store.updateAutoStart(true);
      const saved = store.saveDraft();
      store.updateTheme("dark");
      const duplicate = store.saveDraft();
      assert.equal(pending.length, 1, "A saving request must not be duplicated");
      const committed = { ...result("s2", "normal", "system"),
        runtimeApplicationDisposition: "committedWithDeliveryFailures" as const };
      committed.settings.systemIntegration!.autoStartEnabled = true;
      pending.shift()!(committed);
      await saved;
      await duplicate;
      assert.equal(store.settings().systemIntegration?.autoStartEnabled, true);
      assert.equal(store.draftSettings().appearance?.theme, "dark", "Save response erased newer draft edits");
      assert.equal(store.isDirty(), true);
      const reapplied = store.reapply();
      store.updateLanguage("en-US");
      pending.shift()!(committed);
      await reapplied;
      assert.equal(store.draftSettings().appearance?.theme, "dark");
      assert.equal(store.draftSettings().appearance?.language, "en-US", "Reapply erased newer draft edits");
      dispose();
      resolve();
    })().catch((error) => { dispose(); reject(error); });
  });
});

// 两个下移危险阈值的保存往返：改一项只 PATCH 这一项，提交结果读回后草稿干净；
// 越界输入先按 0–95 归一化再提交。按需监控的开关同样只 PATCH 自己。
await new Promise<void>((resolve, reject) => {
  createRoot((dispose) => {
    void (async () => {
      const source = new FakeSettingsSource();
      const patches: Array<{ expectedRevision: string; changes: Record<string, unknown> }> = [];
      let committed = structuredClone(defaultAppSettings());
      let committedRevision = 1;
      const store = createSettingsStore({ source, requestClient: {
        request: async <T>(descriptor: { request?: { body?: unknown } }) => {
          const body = JSON.parse(String(descriptor.request?.body));
          patches.push(body);
          committed = applySettingsPatch(committed, body.changes);
          committedRevision += 1;
          // 不用 committedAndApplied：那条路径会调 window.setTimeout，Node 里没有 window。
          return {
            settings: committed,
            revision: `m${committedRevision}`,
            runtimeApplicationDisposition: "committedWithDeliveryFailures"
          } as T;
        }
      } as never });
      source.publishReady({ settings: committed, revision: "m1", runtimeApplicationDisposition: "notRequested" });

      store.updateGpuOverflowThresholds(25, 80);
      await store.saveDraft();
      assert.deepEqual(patches.pop(), {
        expectedRevision: "m1",
        changes: { performance: { gpuOverflowUsagePercent: 25 } }
      }, "Changing the GPU usage threshold must patch only that value");
      assert.equal(store.settings().performance!.gpuOverflowUsagePercent, 25);
      assert.equal(store.settings().performance!.gpuOverflowDedicatedMemoryPercent, 80);
      assert.equal(store.isDirty(), false);

      store.updateGpuOverflowThresholds(25, 40);
      await store.saveDraft();
      assert.deepEqual(patches.pop(), {
        expectedRevision: "m2",
        changes: { performance: { gpuOverflowDedicatedMemoryPercent: 40 } }
      }, "Changing the dedicated GPU memory threshold must patch only that value");
      assert.equal(store.settings().performance!.gpuOverflowDedicatedMemoryPercent, 40);
      assert.equal(store.isDirty(), false);

      store.updateGpuOverflowThresholds(120, 40);
      assert.equal(store.draftSettings().performance!.gpuOverflowUsagePercent, 100);
      await store.saveDraft();
      assert.deepEqual(patches.pop(), {
        expectedRevision: "m3",
        changes: { performance: { gpuOverflowUsagePercent: 100 } }
      }, "Out-of-range input must be clamped before it is submitted");

      store.updateSmartMonitoringMode("disabled");
      await store.saveDraft();
      assert.deepEqual(patches.pop(), {
        expectedRevision: "m4",
        changes: { performance: { smartMonitoringMode: "disabled" } }
      }, "Turning on-demand monitoring off must patch only that choice");
      assert.equal(store.settings().performance!.smartMonitoringMode, "disabled");
      assert.equal(store.isDirty(), false);

      dispose();
      resolve();
    })().catch((error) => { dispose(); reject(error); });
  });
});

// The frontend selects a fully expanded context; it never expands settings.
assert.equal(resolveSelfCpuRuntimePlan(frontendRefreshIntervals, "optimize", false), frontendRefreshIntervals.backgroundOptimized);
assert.equal(resolveSelfCpuRuntimePlan(frontendRefreshIntervals, "optimize", true), frontendRefreshIntervals.foreground);
assert.equal(resolveSelfCpuRuntimePlan(frontendRefreshIntervals, "normal", false), frontendRefreshIntervals.foreground);
assert.equal(resolveSelfCpuRuntimePlan(null, "normal", true), null);

function result(
  revision: string,
  optimizationMode: "normal" | "smart" | "limited",
  theme: "system" | "light" | "dark"
): CommittedAppSettingsResult {
  const settings = structuredClone(defaultAppSettings());
  settings.performance!.optimizationMode = optimizationMode;
  settings.appearance!.theme = theme;
  return {
    settings,
    revision,
    frontendRefreshIntervals,
    runtimeApplicationDisposition: "notRequested"
  };
}

function snapshot(
  status: SourceSnapshot<CommittedAppSettingsResult>["status"],
  data: CommittedAppSettingsResult | null,
  revision: number
): SourceSnapshot<CommittedAppSettingsResult> {
  return {
    key: "app.settings",
    status,
    data,
    error: null,
    backendEpoch: "test",
    revision,
    acceptedAttempt: data ? revision : null,
    domainRevision: data?.revision ?? null,
    capturedAt: null
  };
}

// Report overrides replace the array, retain unrelated fields and rules, and keep rejected drafts.
await new Promise<void>((resolve, reject) => {
  createRoot(dispose => {
    void (async () => {
      const source = new FakeSettingsSource();
      let rejected = true;
      const patches: Array<{ changes: { reports: AppSettings["reports"] } }> = [];
      const store = createSettingsStore({ source, requestClient: {
        request: async <T>(descriptor: { request?: { body?: unknown } }) => {
          const patch = JSON.parse(String(descriptor.request?.body));
          patches.push(patch);
          if (rejected) throw new RequestProblem("rejected", {
            kind: "http", retryable: false, status: 409,
            payload: { runtimeApplicationDisposition: "rejectedBeforeCommit",
              runtimeFailureCode: "report-rule-invalid:cpu_throttle_temperature:activationThreshold:out-of-range" },
            attempt: { id: "1", sequence: 1, key: "app.settings.patch", decoderId: "app.settings.result.v1",
              method: "PATCH", backendEpoch: null, startedAt: 0, finishedAt: 1, durationMs: 1 }
          });
          return { frontendRefreshIntervals, settings: applySettingsPatch(store.settings(), patch.changes), revision: "r2",
            runtimeApplicationDisposition: "committedWithDeliveryFailures" } as T;
        }
      } as never });
      source.publishReady(result("r1", "normal", "system"));
      assert.deepEqual(store.draftSettings().reports, { ruleOverrides: [] });
      store.updateReports(updateReportRuleField(store.draftSettings().reports!, "cpu_throttle_temperature", "activationThreshold", 92));
      store.updateReports(updateReportRuleField(store.draftSettings().reports!, "stray_process_idle", "executableNames", []));
      await store.saveDraft();
      assert.equal(store.saveState(), "error");
      assert.equal(store.runtimeFailureCode(), "report-rule-invalid:cpu_throttle_temperature:activationThreshold:out-of-range");
      assert.equal(store.draftSettings().reports!.ruleOverrides[0].activationThreshold, 92);
      assert.deepEqual(store.settings().reports!.ruleOverrides, []);
      store.updateReports(updateReportRuleField(store.draftSettings().reports!, "cpu_throttle_temperature", "clearThreshold", 84));
      assert.equal(store.runtimeFailureCode(), null, "An edit clears the previous rejection");
      await store.saveDraft();
      assert.notEqual(store.runtimeFailureCode(), null);
      rejected = false;
      const saving = store.saveDraft();
      assert.equal(store.runtimeFailureCode(), null, "A save clears the previous rejection");
      await saving;
      assert.equal(patches.at(-1)!.changes.reports!.ruleOverrides.length, 2);
      assert.equal(store.isDirty(), false);
      store.updateReports(updateReportRuleField(store.draftSettings().reports!, "cpu_throttle_temperature", "activationThreshold", null));
      assert.equal(store.draftSettings().reports!.ruleOverrides[0].clearThreshold, 84);
      store.updateReports(updateReportRuleField(store.draftSettings().reports!, "cpu_throttle_temperature", "clearThreshold", null));
      assert.deepEqual(store.draftSettings().reports!.ruleOverrides.map(entry => entry.rule), ["stray_process_idle"]);
      assert.deepEqual(store.draftSettings().reports!.ruleOverrides[0].executableNames, []);
      store.resetDraftToDefaults();
      assert.deepEqual(store.draftSettings().reports, { ruleOverrides: [] });
      dispose(); resolve();
    })().catch(error => { dispose(); reject(error); });
  });
});

// New authoritative interval values take effect independently of unsaved choices.
await new Promise<void>((resolve, reject) => {
  createRoot(dispose => {
    void (async () => {
      const source = new FakeSettingsSource();
      const store = createSettingsStore({ source, requestClient: { request: async () => { throw new Error("No write expected"); } } });
      source.publishReady(result("intervals-a", "normal", "system"));
      await Promise.resolve();
      assert.deepEqual(store.frontendRefreshIntervals(), frontendRefreshIntervals);
      store.updateLogicRefreshInterval("monitor", { mode: "custom", preset: "balanced", customValue: 1750 });
      const next = { ...result("intervals-b", "normal", "system"), frontendRefreshIntervals: { ...frontendRefreshIntervals,
        foreground: { monitor: 2345, resourceTable: 3456, localSystem: 4567 },
        backgroundOptimized: { monitor: 5678, resourceTable: 6789, localSystem: 7890 } } };
      source.publishReady(next);
      await Promise.resolve();
      assert.equal(store.frontendRefreshIntervals()!.foreground.monitor, 2345);
      assert.equal(resolveSelfCpuRuntimePlan(store.frontendRefreshIntervals(), "optimize", false)!.monitor, 5678);
      assert.equal(store.draftSettings().performance!.monitorRefreshIntervalMs.customValue, 1750);
      assert.equal(store.isDirty(), true);
      store.resetDraftToDefaults();
      assert.deepEqual(store.draftSettings().performance!.monitorRefreshIntervalMs, frontendRefreshIntervals.defaults.monitor);
      dispose(); resolve();
    })().catch(error => { dispose(); reject(error); });
  });
});
