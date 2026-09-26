import assert from "node:assert/strict";
import {
  dashboardSettingsDecoder,
  metricCatalogDecoder,
  metricSnapshotDecoder
} from "../src/data/monitor/monitorSourceDecoders.ts";
import {
  buildMetricSnapshotSubscriptionUrl,
  normalizeMetricSnapshotQuery
} from "../src/data/monitor/monitorSourcesApi.ts";

const catalog = metricCatalogDecoder.decode([{
  id: "cpu.usage",
  label: "CPU",
  group: "CPU",
  unit: "%",
  preferredSlot: "main"
}]);
assert.equal(catalog[0].id, "cpu.usage");
assert.throws(() => metricCatalogDecoder.decode([{ id: "cpu.usage" }]));

const snapshot = metricSnapshotDecoder.decode({
  version: 4,
  capturedAt: "2026-08-22T12:00:00.000Z",
  items: {
    "cpu.usage": {
      displayValue: "25%",
      numericValue: 25,
      percent: 25
    }
  }
});
assert.equal(snapshot.items["cpu.usage"].numericValue, 25);
assert.throws(() => metricSnapshotDecoder.decode({ items: [] }));
const emptySnapshot = metricSnapshotDecoder.decode({
  version: 4,
  capturedAt: null,
  items: {
    "cpu.usage": {
      displayValue: "-",
      numericValue: null,
      percent: null
    }
  }
});
assert.equal(emptySnapshot.items["cpu.usage"].displayValue, "-");
assert.equal(emptySnapshot.items["cpu.usage"].numericValue, null);
assert.throws(() => metricSnapshotDecoder.decode({
  ...snapshot,
  items: { "cpu.usage": { numericValue: 25 } }
}), /displayValue/);

const settingsPayload = {
  settings: {
    version: 14,
    cards: [{
      id: "cpu",
      main: "cpu.usage",
      small: ["cpu.frequency"],
      mainBinding: null,
      smallBindings: null
    }],
    resourceBars: [{ id: "cpu", metricId: "cpu.usage", scaleMode: "capacity" }],
    resourceTableColumns: [{ id: "name", visible: true }],
    resourceTableProcessColumns: [{ id: "name", visible: true }]
  },
  updatedAt: "2026-08-22T12:00:00.000Z",
  source: {
    kind: "Persisted",
    sourceVersion: 14,
    recoveryDisposition: "none"
  }
};
const settings = dashboardSettingsDecoder.decode(settingsPayload);
assert.equal(settings, settingsPayload);
assert.equal(settings.settings?.cards?.[0].id, "cpu");
assert.deepEqual(settings.settings?.cards?.[0].smallBindings, null);

assert.deepEqual(normalizeMetricSnapshotQuery({
  ids: [" memory.usage ", "cpu.usage", "cpu.usage", ""]
}), {
  ids: ["cpu.usage", "memory.usage"]
});
assert.equal(
  buildMetricSnapshotSubscriptionUrl({ ids: [] }, 1_000),
  "/api/metrics/subscribe?ids=&intervalMs=1000");
assert.equal(
  buildMetricSnapshotSubscriptionUrl({ ids: null }, 1_000),
  "/api/metrics/subscribe?intervalMs=1000");

console.log("Monitor source decoder tests passed");
