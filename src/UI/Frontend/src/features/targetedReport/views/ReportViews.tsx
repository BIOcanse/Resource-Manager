import { createMemo, createSignal, For, onMount, Show } from "solid-js";
import type { ConfirmDialogRequest } from "../../../ui/patterns/ConfirmDialog.tsx";
import { StandardSelect, type StandardSelectOption } from "../../../components/StandardSelect.tsx";
import { uiText } from "../../../text.ts";
import type { SeriesSet } from "./seriesModel.ts";
import { ViewChart } from "./ViewChart.tsx";
import type { PlotModel } from "./viewCompute.ts";
import { ViewEditor } from "./ViewEditor.tsx";
import {
  createView, defaultViews, duplicateView, moveView, updateView, viewLimits,
  type ReportView, type ViewPatch
} from "./viewModel.ts";
import {
  readViewTemplates, saveViewTemplates, templateFromViews, templateLimits, viewsFromTemplate,
  type TargetedReportViewTemplate
} from "./viewTemplates.ts";

/** Keep per-report views in memory and templates in preferences. Templates reference stable roles and append views without replacing existing ones. */
export function ReportViews(props: {
  set: SeriesSet;
  views: readonly ReportView[];
  onChange: (views: ReportView[]) => void;
  nextViewId: () => string;
  confirmDialog: (request: ConfirmDialogRequest) => Promise<boolean>;
}) {
  const copy = () => uiText.targetedReport.views;
  const [editing, setEditing] = createSignal<ReadonlySet<string>>(new Set());
  const [autoBins, setAutoBins] = createSignal<Record<string, number | null>>({});
  const [templates, setTemplates] = createSignal<TargetedReportViewTemplate[]>([]);
  const [templatesReady, setTemplatesReady] = createSignal(false);
  const [templatesBusy, setTemplatesBusy] = createSignal(false);
  const [templatesFailed, setTemplatesFailed] = createSignal(false);
  const [templateName, setTemplateName] = createSignal("");
  const [templateId, setTemplateId] = createSignal("");

  // Load templates once when report details open; expose failures with retry.
  const loadTemplates = async () => {
    setTemplatesBusy(true);
    try {
      setTemplates(await readViewTemplates());
      setTemplatesFailed(false);
      setTemplatesReady(true);
    } catch {
      setTemplatesFailed(true);
    } finally {
      setTemplatesBusy(false);
    }
  };
  onMount(() => void loadTemplates());

  const writeTemplates = async (next: TargetedReportViewTemplate[]) => {
    setTemplatesBusy(true);
    try {
      setTemplates(await saveViewTemplates(next));
      setTemplatesFailed(false);
      return true;
    } catch {
      setTemplatesFailed(true);
      return false;
    } finally {
      setTemplatesBusy(false);
    }
  };

  const change = (index: number, patch: ViewPatch) =>
    props.onChange(props.views.map((view, at) => at === index ? updateView(view, patch) : view));
  const toggleEditor = (id: string) => {
    const next = new Set(editing());
    if (next.has(id)) next.delete(id);
    else next.add(id);
    setEditing(next);
  };
  const add = () => {
    const view = createView(props.nextViewId(), copy().newViewName(props.views.length + 1),
      { y: props.set.series.filter((series) => series.kind === "time").slice(0, 1).map((series) => ({ seriesId: series.id, axis: "left" })) });
    props.onChange([...props.views, view]);
    setEditing(new Set([...editing(), view.id]));
  };
  const addDefaults = () => props.onChange([...props.views, ...defaultViews(copy().defaultNames, props.nextViewId)]);

  const templateOptions = createMemo<StandardSelectOption[]>(() => [
    { value: "", label: templates().length === 0 ? copy().templates.none : copy().templates.choose },
    ...templates().map((template) => ({ value: template.id, label: template.name }))
  ]);
  const selectedTemplate = () => templates().find((template) => template.id === templateId()) ?? null;
  const nextTemplateName = () => templateName().trim().slice(0, templateLimits.nameLength)
    || copy().templates.defaultName(templates().length + 1);
  const canAddTemplate = () => templatesReady() && !templatesBusy() && templates().length < templateLimits.templates;
  const saveTemplate = async (name: string, views: readonly ReportView[]) => {
    const template = templateFromViews(crypto.randomUUID(), name.slice(0, templateLimits.nameLength), views, props.set);
    if (await writeTemplates([...templates(), template])) {
      setTemplateId(template.id);
      setTemplateName("");
    }
  };
  const applyTemplate = () => {
    const template = selectedTemplate();
    if (template) props.onChange([...props.views, ...viewsFromTemplate(template, props.set, props.nextViewId)]);
  };
  const removeTemplate = async () => {
    const template = selectedTemplate();
    if (!template) return;
    const confirmed = await props.confirmDialog({
      title: copy().templates.confirmRemove,
      message: template.name,
      details: [copy().templates.removeDetail],
      tone: "warning",
      confirmLabel: copy().templates.remove
    });
    if (!confirmed) return;
    if (await writeTemplates(templates().filter((item) => item.id !== template.id))) setTemplateId("");
  };

  return (
    <section class="targeted-report-views" aria-label={copy().title}>
      <div class="targeted-report-views-toolbar">
        <h4>{copy().title}</h4>
        <div class="targeted-report-actions">
          <button class="secondary panel-refresh-button" type="button" onClick={add}>{copy().add}</button>
          <button class="secondary panel-refresh-button" type="button" onClick={addDefaults}>{copy().addDefaults}</button>
        </div>
      </div>
      <div class="targeted-report-views-templates targeted-recording-fields">
        <label class="targeted-recording-field targeted-report-view-name">
          <span>{copy().templates.name}</span>
          <input type="text" maxLength={templateLimits.nameLength} value={templateName()}
            placeholder={copy().templates.defaultName(templates().length + 1)}
            onInput={(event) => setTemplateName(event.currentTarget.value)} />
        </label>
        <button class="secondary" type="button"
          disabled={!canAddTemplate() || props.views.length === 0 || props.views.length > templateLimits.viewsPerTemplate}
          onClick={() => void saveTemplate(nextTemplateName(), props.views)}>{copy().templates.saveAll}</button>
        <div class="targeted-recording-field targeted-report-view-field wide">
          <span aria-hidden="true">{copy().templates.label}</span>
          <StandardSelect value={selectedTemplate() ? templateId() : ""} options={templateOptions()}
            ariaLabel={copy().templates.label} disabled={!templatesReady()} onChange={setTemplateId} />
        </div>
        <button class="secondary" type="button" disabled={!selectedTemplate()} onClick={applyTemplate}>{copy().templates.apply}</button>
        <button class="secondary" type="button" disabled={!selectedTemplate() || templatesBusy()}
          onClick={() => void removeTemplate()}>{copy().templates.remove}</button>
      </div>
      <Show when={templatesFailed()}>
        <div class="targeted-report-note error observation-state" role="alert">
          <strong>{copy().templates.failed}</strong>
          <button class="secondary" type="button" disabled={templatesBusy()} onClick={() => void loadTemplates()}>
            {uiText.observationNotice.retry}
          </button>
        </div>
      </Show>
      <Show when={props.views.length > 0} fallback={<p class="targeted-report-note">{copy().empty}</p>}>
        <div class="targeted-report-view-list">
          <For each={props.views}>
            {(view, index) => {
              const open = () => editing().has(view.id);
              const rangeSet = () => view.range.x.min !== null || view.range.x.max !== null;
              return (
                <section class="targeted-report-card targeted-report-view" aria-label={view.name}>
                  <div class="targeted-report-view-header">
                    <h5><span class="targeted-report-card-label">{view.name}</span>
                      <span class="targeted-report-view-kind">{copy().kinds[view.kind]}</span></h5>
                    <div class="targeted-report-view-actions">
                      <button class="secondary" type="button" aria-expanded={open()} onClick={() => toggleEditor(view.id)}>
                        {open() ? copy().closeEditor : copy().edit}
                      </button>
                      <button class="secondary" type="button" disabled={!rangeSet()}
                        onClick={() => change(index(), { range: { x: { min: null, max: null } } })}>{copy().resetRange}</button>
                      <button class="secondary" type="button" disabled={index() === 0}
                        onClick={() => props.onChange(moveView(props.views, index(), -1))}>{copy().moveUp}</button>
                      <button class="secondary" type="button" disabled={index() === props.views.length - 1}
                        onClick={() => props.onChange(moveView(props.views, index(), 1))}>{copy().moveDown}</button>
                      <button class="secondary" type="button" onClick={() => {
                        const next = [...props.views];
                        next.splice(index() + 1, 0, duplicateView(view, props.nextViewId(),
                          copy().copyName(view.name).slice(0, viewLimits.nameLength)));
                        props.onChange(next);
                      }}>{copy().duplicate}</button>
                      <button class="secondary" type="button" disabled={!canAddTemplate()}
                        onClick={() => void saveTemplate(view.name, [view])}>{copy().saveAsTemplate}</button>
                      <button class="secondary" type="button"
                        onClick={() => props.onChange(props.views.filter((item) => item.id !== view.id))}>{copy().remove}</button>
                    </div>
                  </div>
                  <ViewChart view={view} set={props.set}
                    onSelectRange={(range) => change(index(), { range: { x: range } })}
                    onPlot={(plot: PlotModel) => setAutoBins((current) => current[view.id] === plot.autoBins
                      ? current : { ...current, [view.id]: plot.autoBins })} />
                  <Show when={open()}>
                    <ViewEditor view={view} set={props.set} autoBins={autoBins()[view.id] ?? null}
                      onChange={(patch) => change(index(), patch)} />
                  </Show>
                </section>
              );
            }}
          </For>
        </div>
      </Show>
    </section>
  );
}
