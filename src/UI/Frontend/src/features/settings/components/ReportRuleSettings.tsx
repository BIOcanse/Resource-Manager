import { For, Show } from "solid-js";
import { useFrontendRuntime } from "../../../frontendRuntime/FrontendRuntimeContext";
import { useFrontendWork } from "../../../frontendWork/FrontendWorkContext";
import { useSource } from "../../../frontendRuntime/source/useSource.ts";
import { sourceCanRender } from "../../../frontendRuntime/source/SourceSnapshot.ts";
import { uiText } from "../../../text.ts";
import type { AppReportSettings, ReportRuleField, ReportRuleSetting } from "../../../types.ts";
import { emptyReportRuleValues, parseExecutableNames, parseReportRuleRejection,
  reportRuleNumberField, updateReportRuleField } from "../reportRuleSettingsModel.ts";
import { NumberField } from "./SettingsControls";

export function ReportRuleSettings(props: {
  reports: AppReportSettings;
  runtimeFailureCode?: string | null;
  onChange: (reports: AppReportSettings) => void;
}) {
  const runtime = useFrontendRuntime();
  const frontendWork = useFrontendWork();
  const source = useSource(runtime.sources.settingsReads.reportRules,
    () => ({ active: frontendWork.frontendVisible(), refreshIntervalMs: null }));
  const catalog = () => sourceCanRender(source.snapshot()) ? source.snapshot().data : null;
  const loading = () => source.snapshot().status === "idle" || source.snapshot().status === "loading";
  const failed = () => source.snapshot().status === "error" || source.snapshot().status === "unavailable";
  const load = () => source.refresh();
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
      <For each={catalog()!.rules.filter(rule => rule.reportType === group).map(rule => rule.name)}>{name => {
        const rule = () => catalog()!.rules.find(rule => rule.name === name)!;
        return <section class="report-rule" aria-label={ruleLabel(name)}>
          <strong>{ruleLabel(name)}</strong>
          <Show when={reason(name, "rule")}><p role="alert">{reason(name, "rule")}</p></Show>
          <For each={rule().editableFields}>{field => <div class="settings-row report-rule-field">
            <Show when={field === "executableNames"} fallback={(() => {
              const numericField = field as Exclude<ReportRuleField, "executableNames">;
              const model = () => reportRuleNumberField(rule(), overrides(rule()), numericField);
              return <>
                <NumberField label={text().fields[numericField]}
                  ariaLabel={`${ruleLabel(name)} · ${text().fields[numericField]}`}
                  value={model().value} min={model().min} max={model().max} step={model().step}
                  onChange={value => change(rule(), numericField, value * model().scale)} />
                <span>{text().units[model().unit]}</span>
                <span class="report-rule-default">{text().defaultValue} {model().defaultValue} {text().units[model().unit]}</span>
              </>;
            })()}>
              <div class="settings-row-copy report-rule-names">
                <label>
                  <strong>{text().fields.executableNames}</strong>
                  <textarea class="settings-text-field" rows={6}
                    aria-label={`${ruleLabel(name)} · ${text().fields.executableNames}`}
                    value={(overrides(rule()).executableNames ?? rule().defaults.executableNames ?? []).join("\n")}
                    onBlur={event => {
                      const names = parseExecutableNames(event.currentTarget.value);
                      event.currentTarget.value = names.join("\n");
                      if (JSON.stringify(names) !== JSON.stringify(overrides(rule()).executableNames ?? rule().defaults.executableNames)) {
                        change(rule(), "executableNames", names);
                      }
                    }} />
                </label>
                <span>{text().executableNamesHelp}</span>
                <span>{text().defaultValue}: {(rule().defaults.executableNames ?? []).join(", ")}</span>
              </div>
            </Show>
            <Show when={overrides(rule())[field] !== null}>
              <button type="button" class="secondary-button"
                aria-label={`${ruleLabel(name)} · ${text().fields[field]} · ${text().reset}`}
                onClick={() => change(rule(), field, null)}>{text().reset}</button>
            </Show>
            <Show when={reason(name, field)}><p class="report-rule-error" role="alert">{reason(name, field)}</p></Show>
          </div>}</For>
        </section>;
      }}</For>
    </section>}</For>
  </section>;
}
