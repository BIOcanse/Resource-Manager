import { useFrontendReadIntervals } from "../../../app/FrontendReadIntervalsContext.tsx";
import { For, Match, Show, Switch } from "solid-js";
import { StandardSelect } from "../../../components/StandardSelect";
import {
  defaultAppSettings,
  logicRefreshIntervalKeys,
  normalizeLogicRefreshIntervalSetting
} from "../../../stores/settingsStore";
import type { SettingsTextBundle } from "../../../text.ts";
import type {
  AppLogicRefreshIntervalKey,
  AppLogicRefreshIntervalPreset,
  AppPerformanceSettings,
  AppPresetNumericSetting,
  AppPresetNumericSettingMode
} from "../../../types";
import { NumberField, SegmentedControl } from "./SettingsControls";
import { HelpHint } from "../../../ui/primitives/HelpHint.tsx";

/** Save interface refresh cadence in performance settings through the shared draft. */
export function RefreshCadenceSetting(props: {
  settings: AppPerformanceSettings | undefined;
  text: SettingsTextBundle;
  onLogicRefreshIntervalChange: (key: AppLogicRefreshIntervalKey, setting: AppPresetNumericSetting) => void;
}) {
  const publishedIntervals = useFrontendReadIntervals();
  const presetInterval = (key: AppLogicRefreshIntervalKey, preset: AppLogicRefreshIntervalPreset) =>
    publishedIntervals.configuration()!.presets[key][preset];
  // Share placeholder values with reset defaults.
  const performance = (): AppPerformanceSettings => props.settings ?? defaultAppSettings().performance!;
  const logicRefreshIntervalSetting = (key: AppLogicRefreshIntervalKey) => {
    const current = performance();
    const setting = key === "monitor"
      ? current.monitorRefreshIntervalMs
      : key === "resourceTable"
        ? current.resourceTableRefreshIntervalMs
        : current.localSystemRefreshIntervalMs;
    return normalizeLogicRefreshIntervalSetting(key, setting);
  };
  const updateLogicRefreshInterval = (
    key: AppLogicRefreshIntervalKey,
    patch: Partial<AppPresetNumericSetting>
  ) => {
    props.onLogicRefreshIntervalChange(
      key,
      normalizeLogicRefreshIntervalSetting(key, {
        ...logicRefreshIntervalSetting(key),
        ...patch
      }));
  };
  const selectLogicRefreshMode = (key: AppLogicRefreshIntervalKey, mode: AppPresetNumericSettingMode) => {
    const setting = logicRefreshIntervalSetting(key);
    updateLogicRefreshInterval(key, {
      mode,
      customValue: mode === "preset"
        ? presetInterval(key, setting.preset)
        : setting.customValue
    });
  };
  const selectLogicRefreshPreset = (key: AppLogicRefreshIntervalKey, preset: AppLogicRefreshIntervalPreset) => {
    updateLogicRefreshInterval(key, {
      mode: "preset",
      preset,
      customValue: presetInterval(key, preset)
    });
  };
  const updateLogicRefreshCustomValue = (key: AppLogicRefreshIntervalKey, customValue: number) => {
    updateLogicRefreshInterval(key, { mode: "custom", customValue });
  };
  const presetOptionLabel = (key: AppLogicRefreshIntervalKey, preset: AppLogicRefreshIntervalPreset) => {
    const option = props.text.performance.refreshCadencePresetOptions.find((item) => item.id === preset);
    return `${option?.label ?? preset} · ${formatMilliseconds(presetInterval(key, preset))}`;
  };

  return (
    <Show when={publishedIntervals.configuration()}>
      <div class="settings-row settings-row-block">
        <div class="settings-row-copy">
          <div class="settings-row-title">
            <strong>{props.text.performance.refreshCadenceTitle}</strong>
            <HelpHint label={props.text.performance.refreshCadenceTitle}>{props.text.performance.refreshCadenceDescription}</HelpHint>
          </div>
        </div>
        <div class="settings-interval-grid">
          <For each={logicRefreshIntervalKeys}>
            {(key) => {
              const itemText = () => props.text.performance.refreshCadenceItems[key];
              const setting = () => logicRefreshIntervalSetting(key);
              return (
                <div class="settings-interval-item">
                  <div class="settings-interval-copy">
                    <strong>{itemText().label}</strong>
                    <span>{itemText().description}</span>
                  </div>
                  <SegmentedControl<AppPresetNumericSettingMode>
                    value={setting().mode}
                    options={props.text.performance.presetNumericModeOptions}
                    ariaLabel={itemText().label}
                    onChange={(mode) => selectLogicRefreshMode(key, mode)}
                  />
                  {/* Show only the preset or custom input used by the selected mode. */}
                  <Switch>
                    <Match when={setting().mode === "preset"}>
                      <label class="settings-select settings-inline-select">
                        <span>{props.text.performance.refreshCadencePresetLabel}</span>
                        <StandardSelect<AppLogicRefreshIntervalPreset>
                          value={setting().preset}
                          ariaLabel={`${itemText().label} ${props.text.performance.refreshCadencePresetLabel}`}
                          options={props.text.performance.refreshCadencePresetOptions.map((option) => ({
                            value: option.id,
                            label: presetOptionLabel(key, option.id)
                          }))}
                          onChange={(value) => selectLogicRefreshPreset(key, value)}
                        />
                      </label>
                    </Match>
                    <Match when={setting().mode === "custom"}>
                      <NumberField
                        label={props.text.performance.refreshCadenceCustomLabel}
                        ariaLabel={`${itemText().label} ${props.text.performance.refreshCadenceCustomLabel}`}
                        value={setting().customValue}
                        min={500}
                        max={300000}
                        step={100}
                        onChange={(value) => updateLogicRefreshCustomValue(key, value)}
                      />
                    </Match>
                  </Switch>
                </div>
              );
            }}
          </For>
        </div>
      </div>
    </Show>
  );
}

function formatMilliseconds(milliseconds: number) {
  if (milliseconds >= 1000 && milliseconds % 1000 === 0) {
    return `${milliseconds / 1000}s`;
  }

  return `${milliseconds}ms`;
}
