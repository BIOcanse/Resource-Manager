import assert from "node:assert/strict";
import { requestLoginStartup } from "../src/features/settings/shellLoginStartup.ts";
import type { HostMessageTransport } from "../src/host/hostMessageTransport.ts";
import { loadSettingsText } from "../src/i18n/settingsLoader.ts";
import { languageOptions } from "../src/i18n/settingsLanguages.ts";

const listeners = new Set<(message: unknown) => void>();
const sent: Array<{ type?: string; requestId?: string; enabled?: boolean }> = [];
let reply: { enabled?: unknown; error?: string | null } = { enabled: false, error: null };
const transport: HostMessageTransport = {
  canSubscribeMessages: true,
  subscribeMessages(listener) { listeners.add(listener); return () => { listeners.delete(listener); }; },
  postMessage(message) {
    const request = message as typeof sent[number];
    sent.push(request);
    for (const listener of [...listeners]) listener({ type: "shell.loginStartup.result", requestId: "other", enabled: true });
    for (const listener of [...listeners]) listener({ type: "shell.loginStartup.result", requestId: request.requestId, ...reply });
  }
};

assert.equal(await requestLoginStartup(undefined, transport), false);
assert.equal(sent.at(-1)?.type, "shell.loginStartup.get");
assert.equal(Object.hasOwn(sent.at(-1)!, "enabled"), false);
reply = { enabled: true, error: null };
assert.equal(await requestLoginStartup(true, transport), true);
assert.equal(sent.at(-1)?.type, "shell.loginStartup.set");
assert.equal(sent.at(-1)?.enabled, true);
reply = { enabled: null, error: "write-failed" };
await assert.rejects(requestLoginStartup(false, transport), /write-failed/);
reply = { enabled: "false", error: null };
await assert.rejects(requestLoginStartup(undefined, transport), /unavailable/);
await assert.rejects(requestLoginStartup(undefined, null), /unavailable/);
assert.equal(listeners.size, 0);

for (const option of languageOptions) {
  if (option.id === "system") continue;
  const text = await loadSettingsText(option.id);
  for (const field of ["loginStartTitle", "loginStartDescription", "loginStartUnavailable"] as const)
    assert.ok(text.systemIntegration[field]?.trim(), `${option.id}: ${field}`);
  assert.notEqual(text.systemIntegration.autoStartTitle, text.systemIntegration.loginStartTitle, option.id);
}
console.log("login startup: native get/set/failure cleanup and nine language controls passed");
