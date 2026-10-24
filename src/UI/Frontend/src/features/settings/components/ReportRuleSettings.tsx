import { createSignal, For, onCleanup, onMount, Show } from "solid-js";
import { getReportRuleSettings } from "../../../api.ts";
import { uiText } from "../../../text.ts";
import type { AppReportSettings, ReportRuleField, ReportRuleSetting, ReportRuleSettingsResponse } from "../../../types.ts";
import { emptyReportRuleValues, parseExecutableNames, parseReportRuleRejection,
  reportRuleNumberField, updateReportRuleField } from "../reportRuleSettingsModel.ts";
import { NumberField } from "./SettingsControls";

export function ReportRuleSettings(props: {
  reports: AppReportSettings;
  runtimeFailureCode?: string | null;
  onChange: (reports: AppReportSettings) => void;
}) {
  const [catalog, setCatalog] = createSignal<ReportRuleSettingsResponse>();
  const [loading, setLoading] = createSignal(true);
  const [failed, setFailed] = createSignal(false);
  let controller: AbortController | undefined;
  const load = async () => {
    controller?.abort();
    const request = new AbortController();
    controller = request;
    setLoading(true);
    setFailed(false);
    try {
      const result = await getReportRuleSettings(request.signal);
      if (!request.signal.aborted) setCatalog(result);
    } catch {
      if (!request.signal.aborted) setFailed(true);
    } finally {
      if (!request.signal.aborted) setLoading(false);
    }
  };
  onMount(() => { void load(); });
  onCleanup(() => controller?.abort());
  const text = () => uiText.reportRules;
  const groups = () => [...new Set(catalog()?.rules.map(rule => rule.reportType) ?? [])];
  const title = (type: string) => {
    const titles = uiText.optimizationReport.title;
    switch (type) {
      case "cpu_sustained_thermal_throttling": return titles.cpuSustainedThermalThrottling;
      case "system_interrupt_pressure": return titles.systemInterruptPressure;
      case "background_high_usage": return text().backgroundMemoryTitle;
      case "suspected_stray_process": return text().strayProcessTitle;
      default: return titles.generic(type);
    }
  };
  const ruleLabel = (name: string) => text().ruleLabels[name as keyof typeof uiText.reportRules.ruleLabels] ?? name;
  const overrides = (rule: ReportRuleSetting) => props.reports.ruleOverrides.find(entry => entry.rule === rule.name)
    ?? emptyReportRuleValues;
  const change = <K extends ReportRuleField>(rule: ReportRuleSetting, field: K, value: typeof rule.defaults[K]) =>
    props.onChange(updateReportRuleField(props.reports, rule.name, field, value));
  const rejection = () => parseReportRuleRejection(props.runtimeFailureCode);
  const reason = (rule: string, field: string) => {
    const error = rejection();
    return error?.rule === rule && error.field === field ? text().reasons[error.reason] : null;
  };

  return <section class="settings-section-panel report-rule-settings" aria-label={text().title}>
    <strong>{text().title}</strong>
    <p>{text().note}</p>
    <Show when={loading()}><p role="status">{text().loading}</p></Show>
    <Show when={failed()}><p role="alert">{text().loadFailed}</p>
      <button type="button" class="secondary-button" onClick={() => { void load(); }}>{text().retry}</button>
    </Show>
    <Show when={rejection() && !catalog()?.rules.some(rule => rule.name === rejection()!.rule)}>
      <p role="alert">{rejection()?.rule}: {text().reasons[rejection()!.reason]}</p>
    </Show>
    <For each={groups()}>{group => <section class="report-rule-group" aria-label={title(group)}>
      <h3>{title(group)}</h3>
      <For each={catalog()!.rules.filter(rule => rule.reportType === group)}>{rule =>
        <section class="report-rule" aria-label={ruleLabel(rule.name)}>
          <strong>{ruleLabel(rule.name)}</strong>
          <Show when={reason(rule.name, "rule")}><p role="alert">{reason(rule.name, "rule")}</p></Show>
          <For each={rule.editableFields}>{field => <div class="settings-row report-rule-field">
            <Show when={field === "executableNames"} fallback={(() => {
              const numericField = field as Exclude<ReportRuleField, "executableNames">;
              const model = () => reportRuleNumberField(rule, overrides(rule), numericField);
              return <>
                <NumberField label={text().fields[numericField]}
                  ariaLabel={`${ruleLabel(rule.name)} · ${text().fields[numericField]}`}
                  value={model().value} min={model().min} max={model().max} step={model().step}
                  onChange={value => change(rule, numericField, value * model().scale)} />
                <span>{text().units[model().unit]}</span>
                <span class="report-rule-default">{text().defaultValue} {model().defaultValue} {text().units[model().unit]}</span>
              </>;
            })()}>
              <div class="settings-row-copy report-rule-names">
                <label>
                  <strong>{text().fields.executableNames}</strong>
                  <textarea class="settings-text-field" rows={6}
                    aria-label={`${ruleLabel(rule.name)} · ${text().fields.executableNames}`}
                    value={(overrides(rule).executableNames ?? rule.defaults.executableNames ?? []).join("\n")}
                    onBlur={event => {
                      const names = parseExecutableNames(event.currentTarget.value);
                      event.currentTarget.value = names.join("\n");
                      if (JSON.stringify(names) !== JSON.stringify(overrides(rule).executableNames ?? rule.defaults.executableNames)) {
                        change(rule, "executableNames", names);
                      }
                    }} />
                </label>
                <span>{text().executableNamesHelp}</span>
                <span>{text().defaultValue}: {(rule.defaults.executableNames ?? []).join(", ")}</span>
              </div>
            </Show>
            <Show when={overrides(rule)[field] !== null}>
              <button type="button" class="secondary-button"
                aria-label={`${ruleLabel(rule.name)} · ${text().fields[field]} · ${text().reset}`}
                onClick={() => change(rule, field, null)}>{text().reset}</button>
            </Show>
            <Show when={reason(rule.name, field)}><p class="report-rule-error" role="alert">{reason(rule.name, field)}</p></Show>
          </div>}</For>
        </section>
      }</For>
    </section>}</For>
  </section>;
}
