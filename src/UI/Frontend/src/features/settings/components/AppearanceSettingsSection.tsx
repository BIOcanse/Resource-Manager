import { Show } from "solid-js";
import { StandardSelect } from "../../../components/StandardSelect";
import { languageOptions, normalizeLanguageMode } from "../../../text.ts";
import { uiStylePalettes } from "../../../presentation/uiStyles.ts";
import {
  resourceBarFrameRateDefault,
  resourceBarFrameRateMax,
  resourceBarFrameRateMin
} from "../../../presentation/resourceBarFrameRate.ts";
import type { SettingsTextBundle } from "../../../text.ts";
import type {
  AppAdaptiveBooleanMode,
  AppAnimationMode,
  AppBarColorMode,
  AppFontSmoothing,
  AppLanguageMode,
  AppPalette,
  AppResourceBarFrameRateMode,
  AppSettings,
  AppUiStyle,
  ByteUnitMode
} from "../../../types";
import { NumberField, SegmentedControl } from "./SettingsControls";

interface AppearanceSettingsSectionProps {
  settings: AppSettings["appearance"] | undefined;
  text: SettingsTextBundle;
  onUiStyleChange: (uiStyle: AppUiStyle) => void;
  onThemeChange: (theme: AppPalette) => void;
  onAnimationsChange: (animations: AppAnimationMode) => void;
  onResourceBarHardwareAccelerationModeChange: (mode: AppAdaptiveBooleanMode) => void;
  onResourceBarFrameRateModeChange: (mode: AppResourceBarFrameRateMode) => void;
  onResourceBarFrameRateChange: (framesPerSecond: number) => void;
  onBarColorModeChange: (barColorMode: AppBarColorMode) => void;
  onByteUnitModeChange: (byteUnitMode: ByteUnitMode) => void;
  onFontSmoothingChange: (fontSmoothing: AppFontSmoothing) => void;
  onLanguageChange: (language: AppLanguageMode) => void;
}

export function AppearanceSettingsSection(props: AppearanceSettingsSectionProps) {
  const appearance = () => props.settings ?? {
    uiStyle: "standard" as AppUiStyle,
    theme: "system" as AppPalette,
    animations: "auto" as AppAnimationMode,
    resourceBarHardwareAccelerationMode: "auto" as AppAdaptiveBooleanMode,
    barColorMode: "type" as AppBarColorMode,
    byteUnitMode: "native" as ByteUnitMode,
    fontSmoothing: "auto" as AppFontSmoothing,
    language: "system" as AppLanguageMode
  };

  // 配色只列当前风格提供的那几种，顺序按目录。
  const paletteOptions = () => uiStylePalettes(appearance().uiStyle)
    .map((id) => ({ id, label: props.text.appearance.themeOptions[id] }));

  return (
    <div class="settings-section-panel">
      <div class="settings-row">
        <div class="settings-row-copy">
          <strong>{props.text.appearance.uiStyleTitle}</strong>
          <span>{props.text.appearance.uiStyleDescription}</span>
        </div>
        <SegmentedControl
          value={appearance().uiStyle}
          options={props.text.uiStyleOptions}
          ariaLabel={props.text.appearance.uiStyleTitle}
          onChange={props.onUiStyleChange}
        />
      </div>
      <div class="settings-row">
        <div class="settings-row-copy">
          <strong>{props.text.appearance.themeTitle}</strong>
          <span>{props.text.appearance.themeDescription}</span>
        </div>
        <SegmentedControl
          value={appearance().theme}
          options={paletteOptions()}
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
          <strong>{props.text.appearance.resourceBarFrameRateTitle}</strong>
          <span>{props.text.appearance.resourceBarFrameRateDescription}</span>
        </div>
        {/* 帧率输入只在自定义时显示，跟随屏幕时它不生效。 */}
        <div class="settings-frame-rate-control">
          <SegmentedControl
            value={appearance().resourceBarFrameRateMode ?? "display"}
            options={props.text.appearance.resourceBarFrameRateModeOptions}
            ariaLabel={props.text.appearance.resourceBarFrameRateTitle}
            onChange={props.onResourceBarFrameRateModeChange}
          />
          <Show when={appearance().resourceBarFrameRateMode === "custom"}>
            <NumberField
              label={props.text.appearance.resourceBarFrameRateCustomLabel}
              ariaLabel={`${props.text.appearance.resourceBarFrameRateTitle} ${props.text.appearance.resourceBarFrameRateCustomLabel}`}
              value={appearance().resourceBarFrameRate ?? resourceBarFrameRateDefault}
              min={resourceBarFrameRateMin}
              max={resourceBarFrameRateMax}
              step={1}
              onChange={props.onResourceBarFrameRateChange}
            />
          </Show>
        </div>
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
