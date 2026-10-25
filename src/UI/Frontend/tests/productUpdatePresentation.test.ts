import assert from "node:assert/strict";
import test from "node:test";
import { languageOptions } from "../src/i18n/settingsLanguages.ts";
import { loadSettingsText } from "../src/i18n/settingsLoader.ts";
import { isUpdateBusy, productUpdateCopy, updateErrors, updateFailure } from "../src/features/settings/productUpdatePresentation.ts";

test("every selectable locale resolves update controls and all stable failure codes", () => {
  const english = productUpdateCopy("en-US");
  for (const { id } of languageOptions.filter(({ id }) => id !== "system")) {
    const copy = productUpdateCopy(id);
    for (const [key, value] of Object.entries(copy)) {
      if (typeof value !== "string") continue;
      assert.ok(value.trim(), `${id}.${key}`);
      if (id !== "en-US") assert.notEqual(value, english[key as keyof typeof english], `${id}.${key}`);
    }
    for (const code of Object.keys(updateErrors)) {
      const message = copy.error(code, { targetVersion: "1.2.3" });
      assert.ok(message.endsWith("(1.2.3)"), `${id}.${code}`);
      if (id !== "en-US") assert.notEqual(message, english.error(code, { targetVersion: "1.2.3" }), `${id}.${code}`);
    }
  }
  assert.match(productUpdateCopy("zh-TW").pending, /套件/);
  assert.match(productUpdateCopy("zh-CN").pending, /更新包/);
});

test("every failure message is keyed by exactly the selectable languages", () => {
  const languages = languageOptions.filter(({ id }) => id !== "system").map(({ id }) => id).sort();
  for (const [code, values] of Object.entries(updateErrors)) {
    assert.deepEqual(Object.keys(values).sort(), languages, code);
  }
  // 按语言取值，不按位置：繁体与简体、日文与韩文各自取到自己的文字。
  assert.equal(productUpdateCopy("ja-JP").apply, "今すぐ適用");
  assert.equal(productUpdateCopy("ko-KR").apply, "지금 적용");
  assert.equal(productUpdateCopy("zh-TW").apply, "立即套用");
});

test("failure mapping retains stable parameters and never exposes exception prose", () => {
  const state = updateFailure({ message: "private exception", payload: {
    errorCode: "update.versionMismatch", errorParameters: { targetVersion: "1.2.3" }
  } }, "update.preparationFailed");
  assert.equal(state.errorCode, "update.versionMismatch");
  assert.equal(state.errorParameters?.targetVersion, "1.2.3");
  assert.deepEqual(updateFailure(new Error("raw failure"), "update.applyFailed"), {
    stage: "error", errorCode: "update.applyFailed", errorParameters: undefined
  });
  assert.equal(productUpdateCopy("en-US").error("raw failure"), "Unable to prepare the update.");
});

test("pending packages allow explicit application without masquerading as active update work", () => {
  for (const stage of ["checking", "downloading", "verifying", "waitingForExit"]) assert.equal(isUpdateBusy(stage), true);
  for (const stage of [undefined, "idle", "pendingApply", "completed", "error"]) assert.equal(isUpdateBusy(stage), false);
});

test("version eligibility facts resolve through every selectable settings bundle", async () => {
  const english = (await loadSettingsText("en-US")).updates;
  for (const { id } of languageOptions.filter(({ id }) => id !== "system")) {
    const updates = (await loadSettingsText(id)).updates;
    for (const eligibility of ["installedVersionUnknown", "notNewer"]) {
      const reason = updates.unavailableReasons[eligibility];
      assert.ok(reason?.trim(), `${id}.${eligibility}`);
      if (id !== "en-US") assert.notEqual(reason, english.unavailableReasons[eligibility], `${id}.${eligibility}`);
    }
    assert.equal("只能选择高于当前安装版本的发行版。" in updates.unavailableReasons, false);
  }
});
