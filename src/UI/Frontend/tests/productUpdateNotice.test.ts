import assert from "node:assert/strict";
import test from "node:test";
import { createRoot, createSignal } from "solid-js";
import { bindProductUpdateNotice, shouldNoticeUpdate } from "../src/stores/productUpdateNotice.ts";
import { formatConnectedDuration, formatUtcTime, parseUtcTime, productUpdateStatusDecoder } from "../src/data/productUpdateStatus.ts";
import type { ProductUpdateState } from "../src/features/settings/productUpdatePresentation.ts";
import { createFrontendPreferencesClient, type FrontendNotifications } from "../src/preferences/frontendPreferences.ts";

const on = (updateInAppNotifiedVersion: string | null): FrontendNotifications =>
  ({ updateInApp: "on", updateSystem: "on", updateInAppNotifiedVersion });

test("the in-app notice rule: null, same version, new version and switch off", () => {
  assert.equal(shouldNoticeUpdate(null, on(null)), false, "no available version");
  assert.equal(shouldNoticeUpdate(undefined, on("1.0.0")), false, "field absent");
  assert.equal(shouldNoticeUpdate("1.1.0", on("1.1.0")), false, "already noticed");
  assert.equal(shouldNoticeUpdate("1.1.0", on(null)), true, "first version");
  assert.equal(shouldNoticeUpdate("1.2.0", on("1.1.0")), true, "a higher version is noticed once more");
  assert.equal(shouldNoticeUpdate("1.2.0", { ...on("1.1.0"), updateInApp: "off" }), false, "in-app switch off");
  assert.equal(shouldNoticeUpdate("1.2.0", { ...on("1.1.0"), updateSystem: "off" }), true,
    "the system switch belongs to the native shell and does not stop the in-app notice");
});

test("the binding notices once per version, records it and catches a version found while the interface was closed", async () => {
  const shown: string[] = [];
  const recorded: string[] = [];
  const [status, setStatus] = createSignal<ProductUpdateState | null>(null);
  // The cache was written in an earlier session: 1.0.0 was noticed then; 1.1.0 was found while the UI was closed.
  const [notifications, setNotifications] = createSignal<FrontendNotifications | undefined>(undefined);
  const dispose = createRoot(dispose => {
    bindProductUpdateNotice({
      status, notifications,
      record: async (version) => {
        recorded.push(version);
        setNotifications(current => ({ ...current!, updateInAppNotifiedVersion: version }));
      },
      show: (state) => shown.push(state.availableVersion)
    });
    return dispose;
  });
  try {
    setStatus({ stage: "idle", availableVersion: "1.1.0" });
    assert.deepEqual(shown, [], "nothing is decided before the cache is read");
    setNotifications(on("1.0.0"));
    assert.deepEqual(shown, ["1.1.0"], "found while closed: noticed on the first frame after opening");
    await Promise.resolve();
    assert.deepEqual(recorded, ["1.1.0"]);
    // Other status changes for the same version (download, pending apply) do not notice again.
    setStatus({ stage: "downloading", availableVersion: "1.1.0" });
    setStatus({ stage: "pendingApply", targetVersion: "1.1.0", availableVersion: "1.1.0" });
    setStatus({ stage: "idle", availableVersion: null });
    assert.deepEqual(shown, ["1.1.0"]);
    setStatus({ stage: "idle", availableVersion: "1.2.0" });
    assert.deepEqual(shown, ["1.1.0", "1.2.0"]);
    await Promise.resolve();
    setNotifications(current => ({ ...current!, updateInApp: "off" }));
    setStatus({ stage: "idle", availableVersion: "1.3.0" });
    assert.deepEqual(shown, ["1.1.0", "1.2.0"], "switch off: no notice");
    assert.deepEqual(recorded, ["1.1.0", "1.2.0"], "switch off: the version is not recorded either");
    setNotifications(current => ({ ...current!, updateInApp: "on" }));
    assert.deepEqual(shown, ["1.1.0", "1.2.0", "1.3.0"], "turning the switch back on notices the current version once");
  } finally { dispose(); }
});

test("a record that fails still does not repeat the notice in the same session", async () => {
  const shown: string[] = [];
  const [status, setStatus] = createSignal<ProductUpdateState | null>({ stage: "idle", availableVersion: "2.0.0" });
  const dispose = createRoot(dispose => {
    bindProductUpdateNotice({ status, notifications: () => on(null),
      record: () => Promise.reject(new Error("preferences-unavailable")), show: (state) => shown.push(state.availableVersion) });
    return dispose;
  });
  try {
    assert.deepEqual(shown, ["2.0.0"]);
    setStatus({ stage: "checking", availableVersion: "2.0.0" });
    await Promise.resolve();
    assert.deepEqual(shown, ["2.0.0"]);
  } finally { dispose(); }
});

test("resetting the interface preferences keeps the noticed version, so the same version is not noticed again", async () => {
  const values = new Map<string, string>();
  const client = createFrontendPreferencesClient({ getItem: key => values.get(key) ?? null,
    setItem: (key, value) => { values.set(key, value); }, removeItem: key => { values.delete(key); } });
  const before = await client.read();
  await client.save({ ...before, appearance: { ...before.appearance, theme: "dark" },
    notifications: { updateInApp: "off", updateSystem: "off", updateInAppNotifiedVersion: "1.2.0" } }, "global");
  const reset = await client.reset();
  assert.equal(reset.appearance.theme, "system");
  assert.deepEqual(reset.notifications, { updateInApp: "on", updateSystem: "on", updateInAppNotifiedVersion: "1.2.0" });
  assert.deepEqual((await client.read()).notifications, reset.notifications);
  const shown: string[] = [];
  const dispose = createRoot(dispose => {
    bindProductUpdateNotice({ status: () => ({ stage: "idle", availableVersion: "1.2.0" }), notifications: () => reset.notifications,
      record: async () => undefined, show: (state) => shown.push(state.availableVersion) });
    return dispose;
  });
  dispose();
  assert.deepEqual(shown, [], "the same version was noticed again after a reset");
  assert.equal(shouldNoticeUpdate("1.3.0", reset.notifications), true, "a higher version is still noticed after a reset");
});

test("status times are UTC only and are shown as UTC", () => {
  assert.ok(parseUtcTime("2026-10-26T08:00:00Z"));
  assert.ok(parseUtcTime("2026-10-26T08:00:00.1234567+00:00"));
  assert.equal(parseUtcTime("2026-10-26T08:00:00"), null, "no offset");
  assert.equal(parseUtcTime("2026-10-26T16:00:00+08:00"), null, "not UTC");
  assert.equal(formatUtcTime("2026-10-26T08:00:59.9Z"), "2026-10-26 08:00 UTC");
  assert.equal(formatConnectedDuration(90249), "25:04:09");
  const decoded = productUpdateStatusDecoder.decode({ stage: "idle", availableVersion: "1.1.0",
    nextCheckAt: "2026-10-26T08:00:00Z", lastCheckedAt: null, checkResult: "available", automaticCheckStatus: "waitingForInternet", onlineTimeSinceLastCheckSeconds: 30,
    updatedAt: "2026-10-25T19:00:00+00:00", workspace: null, error: null, checkedInstalledVersion: "0.2.4", incompatibleVersions: [] });
  assert.equal(decoded.availableVersion, "1.1.0");
  assert.equal(decoded.nextCheckAt, "2026-10-26T08:00:00Z");
  assert.equal(decoded.onlineTimeSinceLastCheckSeconds, 30);
  assert.equal(decoded.automaticCheckStatus, "waitingForInternet");
  assert.equal(productUpdateStatusDecoder.decode({ stage: "idle" }).availableVersion, null);
  assert.throws(() => productUpdateStatusDecoder.decode({ stage: "idle", lastCheckedAt: "2026-10-26T08:00:00" }));
  assert.throws(() => productUpdateStatusDecoder.decode({ stage: "idle", nextCheckAt: "2026-10-26T16:00:00+08:00" }));
  assert.throws(() => productUpdateStatusDecoder.decode({ stage: "idle", onlineTimeSinceLastCheckSeconds: -1 }));
  assert.throws(() => productUpdateStatusDecoder.decode({ invalid: true }));
  assert.throws(() => productUpdateStatusDecoder.decode({ stage: "idle", automaticCheckStatus: "offline" }));
  assert.throws(() => productUpdateStatusDecoder.decode({ stage: "idle", updatedAt: "2026-10-25T19:00:00" }));
});
