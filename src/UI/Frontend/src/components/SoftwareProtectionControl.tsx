import { createEffect, createSignal, on, onCleanup, Show } from "solid-js";
import { getJson, putJson } from "../api";
import type { ProtectedOptimizationTarget } from "../types";
import { uiText } from "../text";

export function SoftwareProtectionControl(props: { softwareId: string }) {
  const [enabled, setEnabled] = createSignal(false);
  const [state, setState] = createSignal<"loading" | "ready" | "saving" | "error">("loading");
  let request = 0;
  onCleanup(() => { request++; });
  createEffect(on(() => props.softwareId, (softwareId) => {
    const current = ++request;
    setState("loading");
    void getJson<ProtectedOptimizationTarget[]>("/api/optimization/protection")
      .then((targets) => {
        if (current !== request) return;
        setEnabled(targets.some((target) => target.protectionLevel === 2
          && (target.softwareId?.toLowerCase() === softwareId.toLowerCase()
            || target.targetKey.toLowerCase() === `software:${softwareId}`.toLowerCase())));
        setState("ready");
      }).catch(() => { if (current === request) setState("error"); });
  }));

  async function toggle() {
    if (state() !== "ready") return;
    const softwareId = props.softwareId;
    const current = request;
    const previous = enabled();
    const next = !previous;
    setEnabled(next);
    setState("saving");
    try {
      await putJson(`/api/optimization/protection/software/${encodeURIComponent(softwareId)}`, { enabled: next }, uiText.softwareDetail.protection.failed);
      if (current === request) { setEnabled(next); setState("ready"); }
    } catch { if (current === request) { setEnabled(previous); setState("error"); } }
  }

  return <section class="software-detail-section">
    <h3>{uiText.softwareDetail.protection.title}</h3>
    <p>{uiText.softwareDetail.protection.hint}</p>
    <label class="policy-toggle">
      <input type="checkbox" checked={enabled()} disabled={state() !== "ready"} onChange={() => void toggle()} />
      {uiText.softwareDetail.protection.enabled}
    </label>
    <Show when={state() === "error"}><p role="alert">{uiText.softwareDetail.protection.failed}</p></Show>
    <Show when={state() === "loading" || state() === "saving"}><p role="status">{uiText.softwareDetail.processing}</p></Show>
  </section>;
}
