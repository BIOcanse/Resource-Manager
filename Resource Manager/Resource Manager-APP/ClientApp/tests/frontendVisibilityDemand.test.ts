import assert from "node:assert/strict";
import { FrontendVisibilityDemandController } from "../src/frontendWork/FrontendVisibilityDemandController.ts";
import { frontendWorkIds } from "../src/frontendWork/frontendWorkIds.ts";
import {
  frontendVisibilitySurface,
  frontendVisibilityDemandId
} from "../src/frontendWork/frontendVisibilitySurface.ts";
import {
  isFrontendSurfaceVisible,
  visibleFrontendDemandIds
} from "../src/frontendWork/frontendVisibility.ts";

assert.deepEqual(
  frontendVisibilitySurface("surface.one", ["beta", "alpha", "alpha"]),
  {
    "data-frontend-visibility-surface": "surface.one",
    "data-frontend-visibility-demands": "alpha beta"
  });
assert.equal(
  frontendVisibilityDemandId("management.software", "software / one"),
  "management.software.software%20%2F%20one");

const longSurface = {
  isConnected: true,
  getClientRects: () => [{}],
  getBoundingClientRect: () => ({ top: -1_200, right: 1_000, bottom: 2_000, left: 0 })
} as unknown as HTMLElement;
assert.equal(
  isFrontendSurfaceVisible(longSurface, { top: 0, right: 1_000, bottom: 800, left: 0 }),
  true,
  "a long surface must remain visible while both vertical edges are outside the viewport");

const detachedSurface = {
  isConnected: false,
  getClientRects: () => [{}],
  getBoundingClientRect: () => ({ top: 0, right: 100, bottom: 100, left: 0 })
} as unknown as HTMLElement;
assert.equal(
  isFrontendSurfaceVisible(detachedSurface, { top: 0, right: 1_000, bottom: 800, left: 0 }),
  false);

assert.deepEqual(
  visibleFrontendDemandIds([
    { demandIds: ["upper"], visible: true },
    { demandIds: ["lower"], visible: false }
  ]),
  ["upper"]);
assert.deepEqual(
  visibleFrontendDemandIds([
    { demandIds: ["upper", "lower"], visible: true },
    { demandIds: ["lower"], visible: true }
  ]),
  ["lower", "upper"]);

const workReferences = new Map<string, number>();
const changeReference = (workId: string, delta: 1 | -1) => {
  const next = (workReferences.get(workId) ?? 0) + delta;
  if (next <= 0) {
    workReferences.delete(workId);
  } else {
    workReferences.set(workId, next);
  }
};
const alphaController = new FrontendVisibilityDemandController(
  "alpha",
  [frontendWorkIds.detailsCpuModel, frontendWorkIds.detailsGpuModel],
  changeReference);
const betaController = new FrontendVisibilityDemandController(
  "beta",
  [frontendWorkIds.detailsCpuModel],
  changeReference);
assert.equal(alphaController.setActive(true), true);
assert.equal(
  alphaController.setActive(true),
  false,
  "the same visibility state must not duplicate direct work references");
betaController.setActive(true);
assert.equal(workReferences.get(frontendWorkIds.detailsCpuModel), 2);
alphaController.setActive(false);
assert.equal(workReferences.get(frontendWorkIds.detailsCpuModel), 1);
assert.equal(workReferences.has(frontendWorkIds.detailsGpuModel), false);
betaController.dispose();
assert.equal(workReferences.size, 0);

assert.throws(
  () => new FrontendVisibilityDemandController(" ", [], changeReference),
  /non-empty demand ID/);
assert.throws(
  () => new FrontendVisibilityDemandController("empty", [], changeReference),
  /at least one work ID/,
  "visibility-only bookkeeping without real frontend work must stay rejected");
