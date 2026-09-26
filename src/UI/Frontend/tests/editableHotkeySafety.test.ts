import assert from "node:assert/strict";
import test from "node:test";
import {
  forceTerminateHotkeyActionId,
  isSafeDestructiveHotkeyEncoding,
  normalizeEditableHotkeys
} from "../src/features/settings/editableHotkeys.ts";

test("destructive hotkey safety requires an approved modifier and action key", () => {
  const cases: Array<[number[], boolean]> = [
    [[65, 0, 0, 0, 0, 0, 0], false],
    [[1, 0, 0, 0, 0, 0, 0], false],
    [[16, 0, 65, 0, 0, 0, 0], false],
    [[17, 0, 18, 0, 0, 0, 0], false],
    [[17, 0, 65, 0, 0, 0, 0], true],
    [[91, 0, 1, 0, 0, 0, 0], true]
  ];
  for (const [encoding, expected] of cases) {
    assert.equal(isSafeDestructiveHotkeyEncoding(encoding), expected);
  }
});

test("persisted unsafe destructive hotkeys fail closed", () => {
  const [hotkey] = normalizeEditableHotkeys([{
    actionId: forceTerminateHotkeyActionId,
    enabled: true,
    encoding: [13, 0, 0, 0, 0, 0, 0]
  }]);

  assert.equal(hotkey.enabled, false);
  assert.deepEqual(hotkey.encoding, [13, 0, 0, 0, 0, 0, 0]);
});
