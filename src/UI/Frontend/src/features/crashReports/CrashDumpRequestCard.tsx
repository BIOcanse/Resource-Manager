import { createSignal, Show } from "solid-js";
import type { AppCrashDumpMode, CrashDumpDiagnostics } from "../../types.ts";
import { currentSettingsText } from "../../i18n/appTextStore.ts";

/**
 * 请求开启崩溃转储（docs/crash_dumps_2026-10-31.md“设置”）：后端给出的实际模式是 ask（尚未决定）时显示。
 * 说明转储是什么、存在哪、多大、里面可能有什么；“开启”“不开启”立即写回 systemIntegration.crashDumps.mode。
 * 出现在两处：主界面顶部的通知区（除设置 · 系统集成以外的页面）和设置 · 系统集成分区的最上方。
 */
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
