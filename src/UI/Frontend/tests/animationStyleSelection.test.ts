// 按自调度自动关闭动画（docs/animation_style_2026-10-26.md「设置」）：在选中的风格和 none 之间选，只在回到前台、不活跃时重新选。
import assert from "node:assert/strict";
import { createComputed, createRoot, createSignal } from "solid-js";
import { AUTO_ANIMATION_OPTIMIZE_STYLE, AUTO_ANIMATION_RECENT_INPUT_MS, createAnimationStyleSelection } from "../src/app/selfScheduling/createAnimationStyleSelection.ts";
import type { AnimationStyleId } from "../src/presentation/animationStyle.ts";
import type { ResourceManagerSelfGpuGrade } from "../src/types.ts";

assert.equal(AUTO_ANIMATION_OPTIMIZE_STYLE, "none", "Saving selects no animation");

createRoot(dispose => {
  let now = 0;
  const [style, setStyle] = createSignal<AnimationStyleId>("smooth");
  const [autoDisable, setAutoDisable] = createSignal(true);
  const [foreground, setForeground] = createSignal(false);
  const [gpuGrade, setGrade] = createSignal<ResourceManagerSelfGpuGrade>("optimize");
  const selection = createAnimationStyleSelection({ style, autoDisable, foreground, gpuGrade, now: () => now });
  let changes = 0;
  let previous = selection.effectiveStyle();
  createComputed(() => {
    const value = selection.effectiveStyle();
    if (value !== previous) changes++;
    previous = value;
  });
  assert.equal(previous, "smooth", "The selected style applies before the first foreground");
  setForeground(true);
  assert.equal(previous, "none", "First foreground counts as a return");
  const beforeFlips = changes;
  for (let index = 0; index < 100; index++) setGrade(index % 2 ? "normal" : "optimize");
  assert.equal(changes, beforeFlips, "Foreground grade flips cause zero style changes/dispatches");
  setForeground(false);
  setGrade("optimize");
  setGrade("normal");
  assert.equal(changes, beforeFlips, "Background grade changes cause zero style changes");
  setForeground(true);
  assert.equal(previous, "smooth", "Inactive return samples the current grade and picks the selected style");

  // All input kinds use this same callback from the existing input stream.
  selection.recordInput();
  setForeground(false);
  setGrade("optimize");
  now = AUTO_ANIMATION_RECENT_INPUT_MS - 1;
  setForeground(true);
  assert.equal(previous, "smooth", "Recent input forbids automatic changes on return");
  now = AUTO_ANIMATION_RECENT_INPUT_MS;
  assert.equal(previous, "smooth", "Activity expiry alone never changes the style");
  setForeground(false);
  setForeground(true);
  assert.equal(previous, "none", "Input expires at the named interval");
  setForeground(false);
  setGrade("normal");
  selection.recordInput();
  setForeground(true);
  assert.equal(previous, "smooth", "Input before the foreground signal (the activating click) is not activity");
  const beforeSameGradeReturn = changes;
  setForeground(false);
  setForeground(true);
  assert.equal(changes, beforeSameGradeReturn, "Same effective style is not published again");

  // Choosing another moving style keeps the latched decision and swaps the style it falls back to.
  setStyle("terminal");
  assert.equal(previous, "terminal");
  setForeground(false);
  setGrade("optimize");
  now += AUTO_ANIMATION_RECENT_INPUT_MS;
  setForeground(true);
  assert.equal(previous, "none");
  setStyle("desktop");
  assert.equal(previous, "none", "A style change in the foreground does not re-sample the grade");

  // none and ultra apply directly; the switch has no effect on them.
  for (const manual of ["none", "ultra"] as const) {
    setStyle(manual);
    assert.equal(previous, manual);
    setForeground(false);
    setGrade("normal");
    setForeground(true);
    assert.equal(previous, manual, "none and ultra ignore foreground and grade");
  }

  // Switched off: the selected style applies directly, also in an optimize grade.
  setStyle("smooth");
  setAutoDisable(false);
  assert.equal(previous, "smooth");
  setForeground(false);
  setGrade("optimize");
  now += AUTO_ANIMATION_RECENT_INPUT_MS;
  setForeground(true);
  assert.equal(previous, "smooth", "Switched off: the grade is never sampled");
  setAutoDisable(true);
  assert.equal(previous, "smooth", "Switching on retains the selected style until an eligible return");
  setForeground(false);
  now += AUTO_ANIMATION_RECENT_INPUT_MS;
  setForeground(true);
  assert.equal(previous, "none");
  dispose();
});

// A foreground startup samples immediately; OS motion preferences are never read.
for (const reduced of [false, true]) {
  let mediaReads = 0;
  Object.assign(globalThis, { matchMedia: () => { mediaReads++; return { matches: reduced }; } });
  createRoot(dispose => {
    const [foreground, setForeground] = createSignal(true);
    const [gpuGrade, setGrade] = createSignal<ResourceManagerSelfGpuGrade>("optimize");
    const selection = createAnimationStyleSelection({ style: () => "desktop", autoDisable: () => true, foreground, gpuGrade, now: () => 0 });
    assert.equal(selection.effectiveStyle(), "none");
    setForeground(false);
    setGrade("normal");
    setForeground(true);
    assert.equal(selection.effectiveStyle(), "desktop");
    assert.equal(mediaReads, 0, "prefers-reduced-motion is not an input");
    dispose();
  });
}
console.log("PASS animation style selection: startup, transitions, activity, style changes, none/ultra, switch off, OS preference, change counts");
