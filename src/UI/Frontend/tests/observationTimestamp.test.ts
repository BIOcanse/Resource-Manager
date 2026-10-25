import assert from "node:assert/strict";
import {
  formatObservationTimestamp
} from "../src/presentation/observationTimestamp.ts";
import { userFacingDateTime } from "../src/presentation/userFacingText.ts";
import { applyLanguage } from "../src/text.ts";

const formatted = formatObservationTimestamp("2026-08-05T12:34:56.000Z");
assert.match(formatted, /2026/);
assert.match(formatted, /08|8/);
assert.match(formatted, /05|5/);
assert.equal(formatObservationTimestamp("not-a-date"), "");
assert.equal(formatObservationTimestamp(), "");

// 时间按界面语言格式化，而不是按系统或浏览器的区域设置。
const instant = "2026-08-05T12:34:56.000Z";
for (const language of ["de-DE", "ja-JP"] as const) {
  await applyLanguage(language);
  assert.equal(formatObservationTimestamp(instant), new Intl.DateTimeFormat(language, {
    year: "numeric", month: "2-digit", day: "2-digit", hour: "2-digit", minute: "2-digit", second: "2-digit"
  }).format(Date.parse(instant)));
  assert.equal(userFacingDateTime(instant), new Date(instant).toLocaleString(language));
}
const japanese = formatObservationTimestamp(instant);
await applyLanguage("de-DE");
assert.notEqual(formatObservationTimestamp(instant), japanese, "the two languages format the same instant differently");
