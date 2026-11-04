import { createSignal, Show } from "solid-js";
import type { AppCrashDumpMode, CrashDumpDiagnostics } from "../../types.ts";
import { currentSettingsText } from "../../i18n/appTextStore.ts";

/** Show only when the effective backend mode is ask; either choice immediately updates the crash-dump mode. */
export function CrashDumpRequestCard(props: {
  readonly diagnostics: CrashDumpDiagnostics | undefined;
  readonly hidden?: boolean;
  readonly onChoose: (mode: Extract<AppCrashDumpMode, "on" | "off">) => Promise<void>;
}) {
  const copy = () => currentSettingsText().crashDumps;
  const [busy, setBusy] = createSignal(false);
  const [failed, setFailed] = createSignal(false);
  const visible = () => !props.hidden && props.diagnostics?.effectiveMode === "ask";
  async function choose(mode: "on" | "off") {
    if (busy()) return;
    setBusy(true);
    setFailed(false);
    try { await props.onChoose(mode); }
    catch { setFailed(true); }
    finally { setBusy(false); }
  }
  return (
    <Show when={visible()}>
      <aside class="crash-dump-request" role="region" aria-label={copy().requestTitle}>
        <div class="crash-dump-request-copy">
          <strong>{copy().requestTitle}</strong>
          <span>{copy().requestWhat}</span>
          <span>{copy().requestWhere(props.diagnostics?.dumpFolder ?? "")}</span>
          <span>{copy().requestSize}</span>
          <span>{copy().requestContent}</span>
          <Show when={failed()}><span class="crash-dump-request-error" role="alert">{copy().requestFailed}</span></Show>
        </div>
        <div class="crash-dump-request-actions">
          <button type="button" disabled={busy()} onClick={() => void choose("on")}>{copy().requestEnable}</button>
          <button type="button" class="secondary" disabled={busy()} onClick={() => void choose("off")}>{copy().requestDecline}</button>
        </div>
      </aside>
    </Show>
  );
}
