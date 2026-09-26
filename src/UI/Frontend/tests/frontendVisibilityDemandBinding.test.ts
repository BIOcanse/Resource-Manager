import assert from "node:assert/strict";
import { createRoot, createSignal } from "solid-js";
import {
  bindFrontendVisibilityDemand,
  type FrontendVisibilityDemandRegistration
} from "../src/frontendWork/frontendVisibilityDemandRegistration.ts";
import { frontendWorkIds } from "../src/frontendWork/frontendWorkIds.ts";

const events: string[] = [];
const [controllerRevision, setControllerRevision] = createSignal(0);
const [registration, setRegistration] = createSignal<FrontendVisibilityDemandRegistration>({
  demandId: "alpha",
  workIds: [frontendWorkIds.detailsCpuModel]
});

let disposeRoot = () => undefined;
createRoot((dispose) => {
  disposeRoot = dispose;
  bindFrontendVisibilityDemand({
    registerVisibilityDemand(demandId, workIds) {
      controllerRevision();
      setControllerRevision((current) => current + 1);
      events.push(`register:${demandId}:${workIds.join(",")}`);
      return () => {
        controllerRevision();
        setControllerRevision((current) => current + 1);
        events.push(`unregister:${demandId}`);
      };
    }
  }, registration);
});

await flushEffects();
assert.deepEqual(events, [
  `register:alpha:${frontendWorkIds.detailsCpuModel}`
]);

setControllerRevision((current) => current + 1);
await flushEffects();
assert.equal(
  events.length,
  1,
  "controller-internal signal changes must not replace the visibility owner");

setRegistration({
  demandId: "alpha",
  workIds: [frontendWorkIds.detailsCpuModel, frontendWorkIds.detailsCpuModel]
});
await flushEffects();
assert.equal(
  events.length,
  1,
  "a semantically equivalent work set must not replace the visibility owner");

setRegistration({
  demandId: "beta",
  workIds: [frontendWorkIds.detailsGpuModel]
});
await flushEffects();
assert.deepEqual(events, [
  `register:alpha:${frontendWorkIds.detailsCpuModel}`,
  "unregister:alpha",
  `register:beta:${frontendWorkIds.detailsGpuModel}`
]);

disposeRoot();
assert.deepEqual(events, [
  `register:alpha:${frontendWorkIds.detailsCpuModel}`,
  "unregister:alpha",
  `register:beta:${frontendWorkIds.detailsGpuModel}`,
  "unregister:beta"
]);

async function flushEffects() {
  await Promise.resolve();
  await Promise.resolve();
}
