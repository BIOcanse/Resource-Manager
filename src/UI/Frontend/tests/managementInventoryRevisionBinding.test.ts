import assert from "node:assert/strict";
import { createRoot, createSignal } from "solid-js";
import type { CurrentValueSource } from "../src/frontendRuntime/push/PushValueSourceFamily.ts";
import type { SourceSnapshot } from "../src/frontendRuntime/source/SourceSnapshot.ts";
import type { ManagementInventoryRevisionSnapshot } from "../src/data/managementInventoryRevision.ts";
import { bindManagementInventoryRevisions } from "../src/stores/managementInventoryRevisionBinding.ts";
import type { ManagementSubpageId } from "../src/features/management/managementNavigation.ts";
import type { PageId } from "../src/types.ts";

const initialRevision: ManagementInventoryRevisionSnapshot = {
  softwareRevision: "0",
  componentRevision: "0",
  browserRuntimeRevision: "0"
};

class RevisionSource implements CurrentValueSource<ManagementInventoryRevisionSnapshot> {
  readonly key = "management.inventory-revision";
  private readonly listeners = new Set<(snapshot: SourceSnapshot<ManagementInventoryRevisionSnapshot>) => void>();
  private value = initialRevision;

  subscribeSnapshot(listener: (snapshot: SourceSnapshot<ManagementInventoryRevisionSnapshot>) => void) {
    this.listeners.add(listener);
    listener(this.snapshot());
    return () => this.listeners.delete(listener);
  }

  publish(value: ManagementInventoryRevisionSnapshot) {
    this.value = value;
    for (const listener of this.listeners) {
      listener(this.snapshot());
    }
  }

  get subscriptionCount() {
    return this.listeners.size;
  }

  private snapshot(): SourceSnapshot<ManagementInventoryRevisionSnapshot> {
    return {
      key: this.key,
      status: "ready",
      data: this.value,
      error: null,
      backendEpoch: "test",
      revision: 1,
      acceptedAttempt: 1,
      domainRevision: null,
      capturedAt: null
    };
  }
}

function setup(initial: {
  page: PageId;
  subpage: ManagementSubpageId;
  inventoryActive: boolean;
  browserActive: boolean;
}) {
  const source = new RevisionSource();
  const calls = { components: 0, software: 0, browserRuntimes: 0 };
  const state = createRoot((dispose) => {
    const [activePage, setActivePage] = createSignal<PageId>(initial.page);
    const [activeSubpage, setActiveSubpage] = createSignal<ManagementSubpageId>(initial.subpage);
    const [inventoryActive, setInventoryActive] = createSignal(initial.inventoryActive);
    const [browserRuntimesActive, setBrowserRuntimesActive] = createSignal(initial.browserActive);
    bindManagementInventoryRevisions({
      activePage,
      activeSubpage,
      inventoryRevisionSource: source,
      inventoryActive,
      browserRuntimesActive,
      mutablePersistenceEnabled: () => true,
      refreshComponents: async () => { calls.components += 1; },
      refreshSoftware: async () => { calls.software += 1; },
      refreshBrowserRuntimes: async () => { calls.browserRuntimes += 1; }
    });
    return {
      dispose,
      setActivePage,
      setActiveSubpage,
      setInventoryActive,
      setBrowserRuntimesActive
    };
  });
  return { source, calls, state };
}

async function settleEffects() {
  await new Promise((resolve) => setTimeout(resolve, 0));
}

async function run() {
  {
    const { source, calls, state } = setup({
      page: "components",
      subpage: "Dependency",
      inventoryActive: true,
      browserActive: false
    });
    await settleEffects();
    source.publish({ ...initialRevision, softwareRevision: "1" });
    source.publish({ ...initialRevision, softwareRevision: "1", componentRevision: "1" });
    await settleEffects();
    assert.deepEqual(calls, { components: 1, software: 1, browserRuntimes: 0 });

    state.setActiveSubpage("BrowserRuntime");
    state.setInventoryActive(false);
    state.setBrowserRuntimesActive(true);
    await settleEffects();
    source.publish({ softwareRevision: "1", componentRevision: "1", browserRuntimeRevision: "1" });
    await settleEffects();
    assert.deepEqual(calls, { components: 1, software: 1, browserRuntimes: 1 });
    state.dispose();
  }

  {
    const { source, calls, state } = setup({
      page: "monitor",
      subpage: "Dependency",
      inventoryActive: false,
      browserActive: false
    });
    await settleEffects();
    assert.equal(source.subscriptionCount, 0);
    source.publish({ softwareRevision: "5", componentRevision: "7", browserRuntimeRevision: "3" });
    assert.deepEqual(calls, { components: 0, software: 0, browserRuntimes: 0 });

    state.setActivePage("components");
    state.setInventoryActive(true);
    await settleEffects();
    source.publish({ softwareRevision: "6", componentRevision: "7", browserRuntimeRevision: "3" });
    await settleEffects();
    assert.deepEqual(calls, { components: 0, software: 1, browserRuntimes: 0 });
    state.dispose();
  }

  {
    const source = new RevisionSource();
    const calls = { components: 0, software: 0, browserRuntimes: 0 };
    let finishFirstRead: (() => void) | null = null;
    const firstRead = new Promise<void>((resolve) => { finishFirstRead = resolve; });
    const state = createRoot((dispose) => {
      const [activePage] = createSignal<PageId>("components");
      const [activeSubpage] = createSignal<ManagementSubpageId>("Dependency");
      bindManagementInventoryRevisions({
        activePage,
        activeSubpage,
        inventoryRevisionSource: source,
        inventoryActive: () => true,
        browserRuntimesActive: () => false,
        mutablePersistenceEnabled: () => true,
        refreshComponents: () => {
          calls.components += 1;
          return calls.components === 1 ? firstRead : Promise.resolve();
        },
        refreshSoftware: async () => { calls.software += 1; },
        refreshBrowserRuntimes: async () => { calls.browserRuntimes += 1; }
      });
      return { dispose };
    });
    await settleEffects();
    source.publish({ ...initialRevision, componentRevision: "1" });
    source.publish({ ...initialRevision, componentRevision: "2" });
    await settleEffects();
    assert.equal(calls.components, 2, "a second changed fact reaches the existing in-flight refresh owner");
    finishFirstRead?.();
    await firstRead;
    state.dispose();
  }

  {
    const source = new RevisionSource();
    const calls = { components: 0, software: 0, browserRuntimes: 0 };
    const unhandled: unknown[] = [];
    const onUnhandled = (reason: unknown) => unhandled.push(reason);
    process.on("unhandledRejection", onUnhandled);
    const state = createRoot((dispose) => {
      const [activePage] = createSignal<PageId>("components");
      const [activeSubpage] = createSignal<ManagementSubpageId>("Dependency");
      bindManagementInventoryRevisions({
        activePage,
        activeSubpage,
        inventoryRevisionSource: source,
        inventoryActive: () => true,
        browserRuntimesActive: () => false,
        mutablePersistenceEnabled: () => true,
        refreshComponents: () => {
          calls.components += 1;
          return Promise.reject(new Error("components failed"));
        },
        refreshSoftware: async () => { calls.software += 1; },
        refreshBrowserRuntimes: async () => { calls.browserRuntimes += 1; }
      });
      return { dispose };
    });
    try {
      await settleEffects();
      source.publish({ ...initialRevision, componentRevision: "1", softwareRevision: "1" });
      await settleEffects();
      assert.deepEqual(calls, { components: 1, software: 1, browserRuntimes: 0 });
      assert.deepEqual(unhandled, []);
    } finally {
      state.dispose();
      process.off("unhandledRejection", onUnhandled);
    }
  }

  console.log("Management inventory revision binding tests passed.");
}

await run();
