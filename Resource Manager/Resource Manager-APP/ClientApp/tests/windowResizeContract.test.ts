import assert from "node:assert/strict";
import { readFileSync } from "node:fs";

const appShell = readSource("../src/app/AppShell.tsx");
const refreshScheduler = readSource("../src/app/usePageRefreshScheduler.ts");
const motionCss = readSource("../src/styles/motion.css");

assert.doesNotMatch(appShell, /beginShellWindowResize/);
assert.match(refreshScheduler, /message === "host\.window-resize:start"/);
assert.match(refreshScheduler, /message === "host\.window-resize:end"/);

assert.match(motionCss, /body\[data-window-resizing="yes"\]\s*\{/);
assert.doesNotMatch(motionCss, /body\[data-window-resizing="yes"\]\s+\*/);
for (const token of ["fast", "medium", "page", "panel", "bar"]) {
  assert.match(motionCss, new RegExp(`--motion-${token}:\\s*0ms`));
}

function readSource(relativePath: string) {
  return readFileSync(new URL(relativePath, import.meta.url), "utf8");
}
