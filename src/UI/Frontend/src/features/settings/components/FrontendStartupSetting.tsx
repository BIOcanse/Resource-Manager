import { Show, createSignal, onMount } from "solid-js";
import type { SettingsTextBundle } from "../../../text.ts";
import { requestLoginStartup } from "../shellLoginStartup.ts";

export function FrontendStartupSetting(props: { text: SettingsTextBundle["systemIntegration"] }) {
  const [enabled, setEnabled] = createSignal<boolean | null>(null);
  const [busy, setBusy] = createSignal(true);
  const [error, setError] = createSignal(false);
  const update = async (value?: boolean) => {
    setBusy(true);
    setError(false);
    try { setEnabled(await requestLoginStartup(value)); }
    catch { setEnabled(null); setError(true); }
    finally { setBusy(false); }
  };
  onMount(() => { void update(); });
  return <div class="settings-row">
    <div class="settings-row-copy">
      <strong>{props.text.loginStartTitle}</strong>
      <span>{props.text.loginStartDescription}</span>
      <Show when={error()}><span role="status">{props.text.loginStartUnavailable}</span></Show>
    </div>
    <label class="settings-switch">
      <input type="checkbox" aria-label={props.text.loginStartTitle} checked={enabled() === true}
        disabled={busy() || enabled() === null} onChange={(event) => { void update(event.currentTarget.checked); }} />
      <span />
    </label>
  </div>;
}
