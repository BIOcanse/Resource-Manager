import assert from "node:assert/strict";
import { createComputed, createRoot, createSignal } from "solid-js";
import { AUTO_ANIMATION_OPTIMIZE_MODE, AUTO_ANIMATION_RECENT_INPUT_MS, createAnimationModeSelection } from "../src/app/selfScheduling/createAnimationModeSelection.ts";
import type { AppAnimationMode, ResourceManagerSelfGpuGrade } from "../src/types.ts";

createRoot(dispose => {
  let now = 0;
  const [mode, setMode] = createSignal<AppAnimationMode>("auto");
  const [foreground, setForeground] = createSignal(false);
  const [gpuGrade, setGrade] = createSignal<ResourceManagerSelfGpuGrade>("optimize");
  const selection = createAnimationModeSelection({ mode, foreground, gpuGrade, now: () => now });
  let changes = 0;
  let previous = selection.effectiveMode();
  createComputed(() => {
    const value = selection.effectiveMode();
    if (value !== previous) changes++;
    previous = value;
  });
  assert.equal(previous, "normal", "Auto explicitly starts normal before first foreground");
  setForeground(true);
  assert.equal(previous, AUTO_ANIMATION_OPTIMIZE_MODE, "First foreground counts as a return");
  const beforeFlips = changes;
  for (let index = 0; index < 100; index++) setGrade(index % 2 ? "normal" : "optimize");
  assert.equal(changes, beforeFlips, "Foreground grade flips cause zero tier changes/dispatches");
  setForeground(false);
  setGrade("optimize");
  setGrade("normal");
  assert.equal(changes, beforeFlips, "Background grade changes cause zero tier changes");
  setForeground(true);
  assert.equal(previous, "normal", "Inactive return samples the current grade");

  // All input kinds use this same callback from the existing input stream.
  selection.recordInput();
  setForeground(false);
  setGrade("optimize");
  now = AUTO_ANIMATION_RECENT_INPUT_MS - 1;
  setForeground(true);
  assert.equal(previous, "normal", "Recent input forbids automatic changes on return");
  now = AUTO_ANIMATION_RECENT_INPUT_MS;
  assert.equal(previous, "normal", "Activity expiry alone never changes the tier");
  setForeground(false);
  setForeground(true);
  assert.equal(previous, AUTO_ANIMATION_OPTIMIZE_MODE, "Input expires at the named interval");
  setForeground(false);
  setGrade("normal");
  selection.recordInput();
  setForeground(true);
  assert.equal(previous, "normal", "Input before the foreground signal (the activating click) is not activity");
  setForeground(false);
  setGrade("optimize");
  now += AUTO_ANIMATION_RECENT_INPUT_MS;
  setForeground(true);
  assert.equal(previous, AUTO_ANIMATION_OPTIMIZE_MODE);
  const beforeSameGradeReturn = changes;
  setForeground(false);
  setForeground(true);
  assert.equal(changes, beforeSameGradeReturn, "Same selected tier is not published again");

  for (const manual of ["normal", "none", "ultra"] as const) {
    selection.recordInput();
    setMode(manual);
    assert.equal(previous, manual, "Manual tiers apply directly despite recent input");
    setForeground(false);
    setGrade("normal");
    setGrade("optimize");
    setForeground(true);
    assert.equal(previous, manual, "Manual tiers ignore foreground and grade");
  }
  setMode("none");
  setMode("auto");
  assert.equal(previous, "none", "Entering auto retains the selected tier until an eligible return");
  now += AUTO_ANIMATION_RECENT_INPUT_MS;
  setForeground(false);
  setForeground(true);
  assert.equal(previous, AUTO_ANIMATION_OPTIMIZE_MODE);
  dispose();
});

// A foreground startup samples immediately; OS motion preferences are never read.
for (const reduced of [false, true]) {
  let mediaReads = 0;
  Object.assign(globalThis, { matchMedia: () => { mediaReads++; return { matches: reduced }; } });
  createRoot(dispose => {
    const [foreground, setForeground] = createSignal(true);
    const [gpuGrade, setGrade] = createSignal<ResourceManagerSelfGpuGrade>("optimize");
    const selection = createAnimationModeSelection({ mode: () => "auto", foreground, gpuGrade, now: () => 0 });
    assert.equal(selection.effectiveMode(), AUTO_ANIMATION_OPTIMIZE_MODE);
    setForeground(false);
    setGrade("normal");
    setForeground(true);
    assert.equal(selection.effectiveMode(), "normal");
    assert.equal(mediaReads, 0, "prefers-reduced-motion is not an input");
    dispose();
  });
}
console.log("PASS animation mode selection: startup, transitions, activity, manual tiers, OS preference, change counts");
