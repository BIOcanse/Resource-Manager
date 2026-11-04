import { createEffect, createSignal, on, onCleanup, Show } from "solid-js";
import { getJson, putJson } from "../api";
import type { ProtectedOptimizationTarget } from "../types";
import { protectedTargetListDecoder } from "../data/optimization/optimizationReportsDecoder.ts";
import { isKnownProtectionSource, normalizedLevel, resolveSoftwareProtection } from "../data/optimization/softwareProtection.ts";
import { uiText } from "../text";
import { SwitchControl } from "../ui/primitives/Switch.tsx";

export function SoftwareProtectionControl(props: { softwareId: string; softwareKind: string }) {
  // Prefer the software-ID rule, then its type rule; null means no protection applies.
  const [deciding, setDeciding] = createSignal<ProtectedOptimizationTarget | null>(null);
  const [enabled, setEnabled] = createSignal(false);
  const [state, setState] = createSignal<"loading" | "ready" | "saving" | "error">("loading");
  let request = 0;
  onCleanup(() => { request++; });

  async function read(softwareId: string, softwareKind: string, current: number) {
    const targets = protectedTargetListDecoder.decode(await getJson<unknown>("/api/optimization/protection"));
    if (current !== request) return;
    const entry = resolveSoftwareProtection(targets, softwareId, softwareKind);
    setDeciding(entry);
    setEnabled(entry !== null && normalizedLevel(entry.protectionLevel) > 0);
    setState("ready");
  }

  createEffect(on(() => [props.softwareId, props.softwareKind] as const, ([softwareId, softwareKind]) => {
    const current = ++request;
    setState("loading");
    read(softwareId, softwareKind, current).catch(() => { if (current === request) setState("error"); });
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
    } catch {
      if (current === request) { setEnabled(previous); setState("error"); }
      return;
    }
    // Reread after saving: removing an override can reactivate a default rule and change its source.
    try { await read(softwareId, props.softwareKind, current); }
    catch { if (current === request) setState("error"); }
  }

  // Protection saves immediately and remains independent of the scheduling opt-out draft.
  // Label the entire switch row so clicking its text also toggles it.
  return <div class="software-protection-section" role="group" aria-label={uiText.softwareDetail.protection.title}>
    <label class="policy-toggle">
      <span>{uiText.softwareDetail.protection.enabled}</span>
      <SwitchControl checked={enabled()} disabled={state() !== "ready"} as="span" onChange={() => void toggle()} />
    </label>
    <p class="software-detail-hint">{uiText.softwareDetail.protection.hint}</p>
    <Show when={deciding()}>
      {(entry) => <p class="software-detail-hint software-protection-source">{protectionSourceLine(entry())}</p>}
    </Show>
    <Show when={state() === "error"}><p class="software-protection-message error" role="alert">{uiText.softwareDetail.protection.failed}</p></Show>
    <Show when={state() === "loading" || state() === "saving"}><p class="software-protection-message" role="status">{uiText.softwareDetail.processing}</p></Show>
  </div>;
}

/** Show rule source and state, including overridden defaults; preserve unknown source names. */
function protectionSourceLine(entry: ProtectedOptimizationTarget) {
  const copy = uiText.softwareDetail.protection;
  const source = sourceName(entry.protectionSource);
  const state = stateName(entry.protectionLevel);
  return entry.defaultProtectionSource != null && entry.defaultProtectionLevel != null
    ? copy.replacedSourceLine(sourceName(entry.defaultProtectionSource), stateName(entry.defaultProtectionLevel), source, state)
    : copy.sourceLine(source, state);
}

function sourceName(source: string) {
  return isKnownProtectionSource(source) ? uiText.softwareDetail.protection.sources[source] : source;
}

function stateName(level: number) {
  const copy = uiText.softwareDetail.protection.state;
  return normalizedLevel(level) > 0 ? copy.protected : copy.unprotected;
}
