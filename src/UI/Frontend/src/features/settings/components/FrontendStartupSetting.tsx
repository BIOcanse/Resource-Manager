import { Show, createSignal } from "solid-js";
import type { SettingsTextBundle } from "../../../text.ts";
import { requestLoginStartup } from "../shellLoginStartup.ts";
import { useFrontendRuntime } from "../../../frontendRuntime/FrontendRuntimeContext.tsx";
import { useFrontendWork } from "../../../frontendWork/FrontendWorkContext.tsx";
import { useSource } from "../../../frontendRuntime/source/useSource.ts";
import { frontendWorkIds } from "../../../frontendWork/frontendWorkIds";
import { sourceCanRender } from "../../../frontendRuntime/source/SourceSnapshot.ts";
import { acceptSourceCommand } from "../../../frontendRuntime/source/acceptSourceCommand.ts";
import { HelpHint } from "../../../ui/primitives/HelpHint.tsx";

/** Use the native shell's confirmed login-startup state. Reread after write failure; disable the control after read failure until a successful retry. */
export function FrontendStartupSetting(props: { text: SettingsTextBundle["systemIntegration"] }) {
  const runtime = useFrontendRuntime();
  const work = useFrontendWork();
  // System-integration visibility owns this read.
  const source = useSource(runtime.sources.loginStartup,
    () => ({ active: work.isNeeded(frontendWorkIds.settingsSystemIntegration), refreshIntervalMs: null }));
  const confirmed = () => sourceCanRender(source.snapshot()) ? source.snapshot().data : null;
  const [working, setWorking] = createSignal<"reading" | "writing" | null>(null);
  const phase = () => working() ?? (["idle", "loading"].includes(source.snapshot().status) ? "reading" : "idle");
  const [commandFailed, setCommandFailed] = createSignal(false);
  const failure = () => ["error", "unavailable"].includes(source.snapshot().status) ? "read" : commandFailed() ? "write" : null;
  const [lastRequested, setLastRequested] = createSignal<boolean | null>(null);
  let toggle: HTMLInputElement | undefined;
  const read = async () => {
    setWorking("reading");
    const lease = runtime.sources.loginStartup.acquire({ active: false });
    try {
      await lease.refresh();
    } finally {
      lease.release();
      setWorking(null);
    }
  };
  const retryRead = async () => {
    setCommandFailed(false);
    await read();
  };
  const write = async (value: boolean) => {
    setLastRequested(value);
    setCommandFailed(false);
    setWorking("writing");
    try {
      await acceptSourceCommand(runtime.sources.loginStartup, () => requestLoginStartup(value), result => result);
    } catch {
      // Reread after write failure and display the confirmed state; report a failed reread as a read failure.
      setCommandFailed(true);
      await read();
      if (toggle) toggle.checked = confirmed() === true;
    } finally {
      setWorking(null);
    }
  };
  const statusText = () => {
    if (phase() === "reading" && confirmed() === null) return props.text.loginStartLoading;
    if (failure() === "write") return props.text.loginStartWriteFailed;
    if (failure() === "read") return props.text.loginStartUnavailable;
    return null;
  };
  const retry = () => {
    const value = lastRequested();
    if (failure() === "write" && value !== null) void write(value);
    else void retryRead();
  };
  return <div class="settings-row" aria-busy={phase() !== "idle"}>
    <div class="settings-row-copy">
      <div class="settings-row-title">
        <strong>{props.text.loginStartTitle}</strong>
        <HelpHint label={props.text.loginStartTitle}>{props.text.loginStartDescription}</HelpHint>
      </div>
      <Show when={statusText()}>{(text) => <span role="status">{text()}</span>}</Show>
      <Show when={failure() !== null && phase() === "idle"}>
        <button type="button" class="secondary" onClick={retry}>{props.text.loginStartRetry}</button>
      </Show>
    </div>
    <label class="settings-switch">
      <input type="checkbox" ref={(element) => { toggle = element; }} aria-label={props.text.loginStartTitle}
        checked={confirmed() === true} disabled={phase() !== "idle" || confirmed() === null}
        onChange={(event) => { void write(event.currentTarget.checked); }} />
      <span />
    </label>
  </div>;
}
