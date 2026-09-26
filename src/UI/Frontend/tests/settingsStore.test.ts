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
  normalizeAppSettings
} from "../src/stores/settingsStore.ts";

const canonicalDefaults = defaultAppSettings();
assert.equal(canonicalDefaults.performance?.preciseGpuPlacementEnabled, false);
assert.equal(canonicalDefaults.publicService?.enabled, false);
assert.equal(canonicalDefaults.publicService?.fileIndexEnabled, false);
assert.equal(canonicalDefaults.publicService?.databaseServiceEnabled, false);
assert.equal(canonicalDefaults.publicService?.aiModelCatalogEnabled, false);
assert.equal(canonicalDefaults.aiModelService?.autoStartEnabled, false);

const normalizedMissingFields = normalizeAppSettings({ version: "1.0.23" });
assert.equal(normalizedMissingFields.performance?.preciseGpuPlacementEnabled, false);
assert.equal(normalizedMissingFields.publicService?.enabled, false);
assert.equal(normalizedMissingFields.publicService?.fileIndexEnabled, false);
assert.equal(normalizedMissingFields.publicService?.databaseServiceEnabled, false);
assert.equal(normalizedMissingFields.publicService?.aiModelCatalogEnabled, false);
assert.equal(normalizedMissingFields.aiModelService?.autoStartEnabled, false);

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
      const committed = result("s2", "normal", "system");
      committed.settings.systemIntegration!.autoStartEnabled = true;
      committed.runtimeApplicationDisposition = "committedWithDeliveryFailures";
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
