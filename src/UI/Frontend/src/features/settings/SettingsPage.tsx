import { For, Show } from "solid-js";
import { currentSettingsText, isRightToLeftLanguage } from "../../text.ts";
import { useSelectionIndicator } from "../../interactions/solid/selectionIndicator.ts";
import { useContentTransition } from "../../interactions/solid/contentTransition.ts";
import type { SettingsSection } from "../../types";
import type { SettingsPanelBindings } from "./settingsPanelBindings.ts";
import { SettingsPanelActions, SettingsPanelBody } from "./SettingsPanelParts.tsx";

export { SegmentedControl } from "./components/SettingsControls";

interface SettingsPageProps {
  /** Share draft state and actions with settings panels on other pages. */
  bindings: SettingsPanelBindings;
  activeSection: SettingsSection;
  onSectionChange: (section: SettingsSection) => void;
}

/** Keep application-wide settings sections here. */
export const settingSections: readonly SettingsSection[] = [
  "appearance", "systemIntegration", "publicServices", "trustedAccess", "notifications", "updates", "debug", "credits"
];

export function SettingsPage(props: SettingsPageProps) {
  // Follow the single appTextStore language owner,
  // so draft language changes do not switch this page ahead of the rest of the app.
  const text = () => currentSettingsText();
  const sectionLabel = (section: SettingsSection) => text().sections[section] ?? section;
  // Use navigation order for vertical section transition direction.
  let nav: HTMLElement | undefined;
  let navIndicator: HTMLElement | undefined;
  let content: HTMLDivElement | undefined;
  let sectionStage: HTMLFieldSetElement | undefined;
  const sectionOrder = () => Math.max(0, settingSections.indexOf(props.activeSection));
  useSelectionIndicator({
    name: "设置导航",
    container: () => nav,
    indicator: () => navIndicator,
    selected: () => nav?.querySelector<HTMLElement>(".settings-nav-item.active") ?? null,
    items: () => [...(nav?.querySelectorAll<HTMLElement>(".settings-nav-item") ?? [])],
    order: sectionOrder
  });
  useContentTransition({
    name: "设置分区",
    key: () => props.activeSection,
    order: sectionOrder,
    stage: () => sectionStage,
    snapshot: () => "move",
    axis: "y",
    distance: 5
  });
  const settingsReady = () => props.bindings.loadState === "ready";
  const canRenderSettings = () => props.bindings.loadState === "ready"
    || props.bindings.loadState === "refreshing"
    || props.bindings.loadState === "stale";
  const loadStateTitle = () => props.bindings.loadState === "stale"
    ? text().loadState.staleTitle
    : text().loadState.errorTitle;
  const loadStateDescription = () => props.bindings.loadState === "stale"
    ? text().loadState.staleDescription
    : text().loadState.errorDescription;

  return (
    <section
      class="settings-page page active-page"
      aria-label={sectionLabel(props.activeSection)}
      lang={text().language}
      dir={isRightToLeftLanguage(text().language) ? "rtl" : "ltr"}
    >
      <nav ref={nav} class="settings-nav has-selection-indicator" aria-label={text().navigationLabel}>
        <span ref={navIndicator} class="selection-indicator page-tab-indicator" aria-hidden="true" />
        <For each={settingSections}>
          {(section) => (
            <button
              type="button"
              class="settings-nav-item page-tab"
              classList={{ active: props.activeSection === section }}
              disabled={!canRenderSettings()}
              aria-current={props.activeSection === section ? "page" : undefined}
              onClick={() => props.onSectionChange(section)}
            >
              {sectionLabel(section)}
            </button>
          )}
        </For>
      </nav>
      <div
        ref={content}
        class="settings-content"
        aria-busy={props.bindings.loadState === "loading" || props.bindings.loadState === "refreshing"}
      >
        <div class="settings-content-header">
          <h2>{sectionLabel(props.activeSection)}</h2>
          <div class="settings-header-actions">
            <SettingsPanelActions panel={props.activeSection} bindings={props.bindings} />
          </div>
        </div>
        <Show when={props.bindings.loadState === "stale"}>
          <div id="settingsStaleNotice" class="settings-load-state stale" role="alert">
            <div>
              <strong>{loadStateTitle()}</strong>
              <p>{loadStateDescription()}</p>
            </div>
            <button type="button" onClick={() => props.bindings.onReload()}>
              {text().actions.retry}
            </button>
          </div>
        </Show>
        <Show
          when={canRenderSettings()}
          fallback={
            <div
              class="settings-load-state"
              role={props.bindings.loadState === "loading" ? "status" : "alert"}
            >
              <Show
                when={props.bindings.loadState !== "loading"}
                fallback={<strong>{text().loadState.loading}</strong>}
              >
                <strong>{loadStateTitle()}</strong>
                <p>{loadStateDescription()}</p>
                <button type="button" onClick={() => props.bindings.onReload()}>
                  {text().actions.retry}
                </button>
              </Show>
            </div>
          }
        >
          <fieldset
            ref={sectionStage}
            class="settings-readonly-boundary"
            disabled={!settingsReady()}
            aria-disabled={!settingsReady()}
            aria-describedby={props.bindings.loadState === "stale" ? "settingsStaleNotice" : undefined}
          >
          <SettingsPanelBody panel={props.activeSection} bindings={props.bindings} />
          </fieldset>
        </Show>
      </div>
    </section>
  );
}
