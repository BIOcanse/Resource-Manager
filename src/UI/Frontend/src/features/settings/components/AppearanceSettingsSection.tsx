import { StandardSelect } from "../../../components/StandardSelect";
import { languageOptions, normalizeLanguageMode } from "../../../text.ts";
import type { SettingsTextBundle } from "../../../text.ts";
import type {
  AppAdaptiveBooleanMode,
  AppAnimationMode,
  AppBarColorMode,
  AppFontSmoothing,
  AppLanguageMode,
  AppSettings,
  AppThemeMode,
  ByteUnitMode
} from "../../../types";
import { SegmentedControl } from "./SettingsControls";

interface AppearanceSettingsSectionProps {
  settings: AppSettings["appearance"] | undefined;
  text: SettingsTextBundle;
  onThemeChange: (theme: AppThemeMode) => void;
  onAnimationsChange: (animations: AppAnimationMode) => void;
  onResourceBarHardwareAccelerationModeChange: (mode: AppAdaptiveBooleanMode) => void;
  onBarColorModeChange: (barColorMode: AppBarColorMode) => void;
  onByteUnitModeChange: (byteUnitMode: ByteUnitMode) => void;
  onFontSmoothingChange: (fontSmoothing: AppFontSmoothing) => void;
  onLanguageChange: (language: AppLanguageMode) => void;
}

export function AppearanceSettingsSection(props: AppearanceSettingsSectionProps) {
  const appearance = () => props.settings ?? {
    theme: "system" as AppThemeMode,
    animations: "auto" as AppAnimationMode,
    resourceBarHardwareAccelerationMode: "auto" as AppAdaptiveBooleanMode,
    barColorMode: "type" as AppBarColorMode,
    byteUnitMode: "native" as ByteUnitMode,
    fontSmoothing: "auto" as AppFontSmoothing,
    language: "system" as AppLanguageMode
  };

  return (
    <div class="settings-section-panel">
      <div class="settings-row">
        <div class="settings-row-copy">
          <strong>{props.text.appearance.themeTitle}</strong>
          <span>{props.text.appearance.themeDescription}</span>
        </div>
        <SegmentedControl
          value={appearance().theme}
          options={props.text.themeOptions}
          ariaLabel={props.text.appearance.themeTitle}
          onChange={props.onThemeChange}
        />
      </div>
      <div class="settings-row">
        <div class="settings-row-copy">
          <strong>{props.text.appearance.animationTitle}</strong>
          <span>{props.text.appearance.animationDescription}</span>
        </div>
        <SegmentedControl
          value={appearance().animations ?? "auto"}
          options={props.text.animationOptions}
          ariaLabel={props.text.appearance.animationTitle}
          onChange={props.onAnimationsChange}
        />
      </div>
      <div class="settings-row">
        <div class="settings-row-copy">
          <strong>{props.text.appearance.resourceBarHardwareAccelerationTitle}</strong>
          <span>{props.text.appearance.resourceBarHardwareAccelerationDescription}</span>
        </div>
        <SegmentedControl
          value={appearance().resourceBarHardwareAccelerationMode ?? "auto"}
          options={props.text.appearance.resourceBarHardwareAccelerationModeOptions}
          ariaLabel={props.text.appearance.resourceBarHardwareAccelerationTitle}
          onChange={props.onResourceBarHardwareAccelerationModeChange}
        />
      </div>
      <div class="settings-row">
        <div class="settings-row-copy">
          <strong>{props.text.appearance.barColorTitle}</strong>
          <span>{props.text.appearance.barColorDescription}</span>
        </div>
        <SegmentedControl
          value={appearance().barColorMode ?? "type"}
          options={props.text.barColorOptions}
          ariaLabel={props.text.appearance.barColorTitle}
          onChange={props.onBarColorModeChange}
        />
      </div>
      <div class="settings-row">
        <div class="settings-row-copy">
          <strong>{props.text.appearance.byteUnitTitle}</strong>
          <span>{props.text.appearance.byteUnitDescription}</span>
        </div>
        <SegmentedControl
          value={appearance().byteUnitMode ?? "native"}
          options={props.text.byteUnitOptions}
          ariaLabel={props.text.appearance.byteUnitTitle}
          onChange={props.onByteUnitModeChange}
        />
      </div>
      <div class="settings-row">
        <div class="settings-row-copy">
          <strong>{props.text.appearance.fontSmoothingTitle}</strong>
          <span>{props.text.appearance.fontSmoothingDescription}</span>
        </div>
        <SegmentedControl
          value={appearance().fontSmoothing ?? "auto"}
          options={props.text.fontSmoothingOptions}
          ariaLabel={props.text.appearance.fontSmoothingTitle}
          onChange={props.onFontSmoothingChange}
        />
      </div>
      <div class="settings-row">
        <div class="settings-row-copy">
          <strong>{props.text.appearance.settingsLanguageTitle}</strong>
          <span>{props.text.appearance.settingsLanguageDescription}</span>
        </div>
        <label class="settings-select">
          <span>{props.text.appearance.languageSelectLabel}</span>
          <StandardSelect<AppLanguageMode>
            value={normalizeLanguageMode(appearance().language)}
            ariaLabel={props.text.appearance.languageSelectLabel}
            searchable
            searchPlaceholder={props.text.appearance.languageSelectLabel}
            options={languageOptions.map((option) => ({
              value: option.id,
              label: `${option.nativeLabel} · ${option.label}`
            }))}
            onChange={(value) => props.onLanguageChange(normalizeLanguageMode(value))}
          />
        </label>
      </div>
    </div>
  );
}
