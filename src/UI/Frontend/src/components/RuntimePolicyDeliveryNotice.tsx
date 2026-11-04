import { createSignal, Show } from "solid-js";
import { reapplyAppSettings } from "../data/appSettings/appSettingsApi";
import { useFrontendRuntime } from "../frontendRuntime/FrontendRuntimeContext";
import { userFacingErrorMessage } from "../presentation/userFacingText";
import { uiText } from "../text.ts";

export function RuntimePolicyDeliveryNotice(props: { onPublished: () => void }) {
  const runtime = useFrontendRuntime();
  const [busy, setBusy] = createSignal(false);
  const [error, setError] = createSignal<string | null>(null);
  async function republish() {
    if (busy()) return;
    setBusy(true);
    setError(null);
    try {
      const result = await reapplyAppSettings(runtime.requestClient);
      if (result.runtimeApplicationDisposition === "committedAndApplied") props.onPublished();
      else setError(uiText.misc.settingsSavedNotApplied);
    } catch (failure) {
      setError(userFacingErrorMessage(failure, uiText.misc.reapplySettingsFailed));
    } finally {
      setBusy(false);
    }
  }
  return <div role="status">
    <span>{uiText.softwareActions.processPolicySavedRuntimePending}</span>
    <button class="secondary" type="button" disabled={busy()} onClick={() => void republish()}>
      {uiText.softwareActions.retryRuntimePublication}
    </button>
    <Show when={error()}>{(message) => <p role="alert">{message()}</p>}</Show>
  </div>;
}
