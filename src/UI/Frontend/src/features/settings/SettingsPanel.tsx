import { Show, createUniqueId } from "solid-js";
import { currentSettingsText, isRightToLeftLanguage } from "../../text.ts";
import type { SettingsPanel as SettingsPanelId } from "../../types";
import { useSettingsPanelBindings } from "./SettingsPanelContext.tsx";
import { SettingsPanelActions, SettingsPanelBody, settingsPanelLabel } from "./SettingsPanelParts.tsx";

/** Share the settingsStore draft, save actions, and load states with the settings page. Reserve header and notice geometry while loading. */
export function SettingsPanel(props: { readonly panel: SettingsPanelId }) {
  const bindings = useSettingsPanelBindings();
  const text = () => currentSettingsText();
  const staleNoticeId = createUniqueId();
  const ready = () => bindings.loadState === "ready";
  const canRender = () => bindings.loadState === "ready" || bindings.loadState === "refreshing" || bindings.loadState === "stale";
  const loadStateTitle = () => bindings.loadState === "stale" ? text().loadState.staleTitle : text().loadState.errorTitle;
  const loadStateDescription = () => bindings.loadState === "stale" ? text().loadState.staleDescription : text().loadState.errorDescription;
  return (
    <section
      class="settings-panel"
      data-settings-panel={props.panel}
      aria-label={settingsPanelLabel(props.panel)}
      aria-busy={bindings.loadState === "loading" || bindings.loadState === "refreshing"}
      lang={text().language}
      dir={isRightToLeftLanguage(text().language) ? "rtl" : "ltr"}
    >
      <div class="settings-content-header">
        <h3>{settingsPanelLabel(props.panel)}</h3>
        <div class="settings-header-actions">
          <SettingsPanelActions panel={props.panel} bindings={bindings} />
        </div>
      </div>
      <Show when={bindings.loadState === "stale"}>
        <div id={staleNoticeId} class="settings-load-state stale" role="alert">
          <div>
            <strong>{loadStateTitle()}</strong>
            <p>{loadStateDescription()}</p>
          </div>
          <button type="button" onClick={() => bindings.onReload()}>{text().actions.retry}</button>
        </div>
      </Show>
      <Show
        when={canRender()}
        fallback={
          <div class="settings-load-state" role={bindings.loadState === "loading" ? "status" : "alert"}>
            <Show when={bindings.loadState !== "loading"} fallback={<strong>{text().loadState.loading}</strong>}>
              <strong>{loadStateTitle()}</strong>
              <p>{loadStateDescription()}</p>
              <button type="button" onClick={() => bindings.onReload()}>{text().actions.retry}</button>
            </Show>
          </div>
        }
      >
        <fieldset
          class="settings-readonly-boundary"
          disabled={!ready()}
          aria-disabled={!ready()}
          aria-describedby={bindings.loadState === "stale" ? staleNoticeId : undefined}
        >
          <SettingsPanelBody panel={props.panel} bindings={bindings} />
        </fieldset>
      </Show>
    </section>
  );
}
