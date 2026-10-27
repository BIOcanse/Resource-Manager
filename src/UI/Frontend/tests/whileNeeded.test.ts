import assert from "node:assert/strict";
import { createRoot, createSelector, createSignal } from "solid-js";
import { frontendWorkIds, type FrontendWorkId } from "../src/frontendWork/frontendWorkIds.ts";
import { bindWhileNeeded } from "../src/frontendWork/whileNeededBinding.ts";

/*
 * 分区的活动（第 4.4 节“建造与活动”）：需求变真时进入，变假或卸下时退出；再次进入和第一次一样；
 * 需求为假时不进入；进入拿到只属于这次进入的 signal，退出后才完成的异步读取不产生任何效果。
 */
const workId = frontendWorkIds.settingsPerformanceOverlay;
const otherWorkId = frontendWorkIds.settingsTrustedAccess;
const [activeIds, setActiveIds] = createSignal<readonly FrontendWorkId[]>([]);
const work = createRoot(() => ({
  isNeeded: createSelector<readonly FrontendWorkId[], FrontendWorkId>(activeIds, (id, ids) => ids.includes(id))
}));

const events: string[] = [];
const signals: AbortSignal[] = [];
const accepted: string[] = [];
const pending: { resolve: (value: string) => void }[] = [];
const [unrelated, setUnrelated] = createSignal(0);

const state = createRoot(dispose => {
  bindWhileNeeded(work, workId, signal => {
    // 进入里读到的信号不会被跟踪：它变化不能让这次活动退出再进入。
    unrelated();
    const entry = signals.length + 1;
    events.push(`enter ${entry}`);
    signals.push(signal);
    void new Promise<string>(resolve => pending.push({ resolve })).then(value => {
      if (signal.aborted) return;
      accepted.push(`${entry}:${value}`);
    });
    return () => { events.push(`exit ${entry}`); };
  });
  return { dispose };
});

const settle = () => new Promise<void>(resolve => setTimeout(resolve, 0));

try {
  assert.deepEqual(events, [], "No enter while not needed");
  setActiveIds([otherWorkId]);
  assert.deepEqual(events, [], "Another work's need does not enter this one");

  setActiveIds([otherWorkId, workId]);
  assert.deepEqual(events, ["enter 1"], "Enter when need becomes true");
  assert.equal(signals[0].aborted, false);
  setUnrelated(1);
  setActiveIds([workId]);
  assert.deepEqual(events, ["enter 1"], "Untracked reads and unrelated work changes keep the entry");

  setActiveIds([]);
  assert.deepEqual(events, ["enter 1", "exit 1"], "Exit when need becomes false");
  assert.equal(signals[0].aborted, true, "Exit aborts the entry's signal");

  // 第一次进入的读取在退出后才完成：不产生任何效果。
  pending[0].resolve("late");
  await settle();
  assert.deepEqual(accepted, [], "A read finished after exit has no effect");

  setActiveIds([workId]);
  assert.deepEqual(events, ["enter 1", "exit 1", "enter 2"], "Re-enter does the same as the first enter");
  assert.notStrictEqual(signals[1], signals[0], "Each entry has its own signal");
  assert.equal(signals[1].aborted, false);
  pending[1].resolve("current");
  await settle();
  assert.deepEqual(accepted, ["2:current"], "The current entry's read is accepted");

  state.dispose();
  assert.deepEqual(events, ["enter 1", "exit 1", "enter 2", "exit 2"], "Exit when the owner is disposed");
  assert.equal(signals[1].aborted, true);
  setActiveIds([]);
  setActiveIds([workId]);
  assert.equal(events.length, 4, "A disposed owner never enters again");
} finally {
  state.dispose();
}

// 需求一直为假就卸下：不进入，也不退出。
const idle: string[] = [];
createRoot(dispose => {
  bindWhileNeeded({ isNeeded: () => false }, workId, () => { idle.push("enter"); return () => { idle.push("exit"); }; });
  return dispose;
})();
assert.deepEqual(idle, [], "Disposing an owner that never entered calls neither enter nor exit");

console.log("While needed: enter on need, exit on withdrawal and dispose, re-enter, no enter while not needed, late read after exit ignored passed.");
