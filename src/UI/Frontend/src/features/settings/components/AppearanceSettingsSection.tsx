import { Show, type JSX } from "solid-js";
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
import { pointPreferences } from "../../../preferences/frontendPreferences.ts";
import { animationStyle } from "../../../presentation/animationStyle.ts";
import type { AnimationStyleId } from "../../../presentation/animationStyle.ts";
import { HelpHint } from "../../../ui/primitives/HelpHint.tsx";

interface AppearanceSettingsSectionProps {
  onResetPreferences: () => void;
  settings: AppSettings["appearance"] | undefined;
  text: SettingsTextBundle;
  onUiStyleChange: (uiStyle: AppUiStyle) => void;
  onThemeChange: (theme: AppPalette) => void;
  onAnimationStyleChange: (style: AnimationStyleId) => void;
  onResourceBarFrameRateModeChange: (mode: AppResourceBarFrameRateMode) => void;
  onResourceBarFrameRateChange: (framesPerSecond: number) => void;
  onBarColorModeChange: (barColorMode: AppBarColorMode) => void;
  onByteUnitModeChange: (byteUnitMode: ByteUnitMode) => void;
  onFontSmoothingChange: (fontSmoothing: AppFontSmoothing) => void;
  onLanguageChange: (language: AppLanguageMode) => void;
  /** Append rows after the appearance controls. */
  children?: JSX.Element;
}

export function AppearanceSettingsSection(props: AppearanceSettingsSectionProps) {
  const appearance = () => props.settings ?? pointPreferences().appearance;

  // Offer palettes in the current style catalog order.
  const paletteOptions = () => uiStylePalettes(appearance().uiStyle)
    .map((id) => ({ id, label: props.text.appearance.themeOptions[id] }));

  return (
    <div class="settings-section-panel">
      <div class="settings-row">
        <p class="settings-description">{props.text.appearance.cacheDescription}</p>
        <button type="button" class="secondary" onClick={props.onResetPreferences}>
          {props.text.appearance.resetCache}
        </button>
      </div>
      <div class="settings-row">
        <div class="settings-row-copy">
          <div class="settings-row-title">
            <strong>{props.text.appearance.uiStyleTitle}</strong>
            <HelpHint label={props.text.appearance.uiStyleTitle}>{props.text.appearance.uiStyleDescription}</HelpHint>
          </div>
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
          <div class="settings-row-title">
            <strong>{props.text.appearance.themeTitle}</strong>
            <HelpHint label={props.text.appearance.themeTitle}>{props.text.appearance.themeDescription}</HelpHint>
          </div>
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
          <div class="settings-row-title">
            <strong>{props.text.appearance.animationStyleTitle}</strong>
            <HelpHint label={props.text.appearance.animationStyleTitle}>{props.text.appearance.animationStyleDescription}</HelpHint>
          </div>
        </div>
        <SegmentedControl
          value={animationStyle(appearance().animationStyle).id}
          options={props.text.animationStyleOptions}
          ariaLabel={props.text.appearance.animationStyleTitle}
          onChange={props.onAnimationStyleChange}
        />
      </div>
      <div class="settings-row">
        <div class="settings-row-copy">
          <div class="settings-row-title">
            <strong>{props.text.appearance.resourceBarFrameRateTitle}</strong>
            <HelpHint label={props.text.appearance.resourceBarFrameRateTitle}>{props.text.appearance.resourceBarFrameRateDescription}</HelpHint>
          </div>
        </div>
        {/* Show the frame-rate input only for custom rate mode. */}
        <div class="settings-frame-rate-control">
          <SegmentedControl
            value={appearance().resourceBarFrameRateMode ?? pointPreferences().appearance.resourceBarFrameRateMode!}
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
          <div class="settings-row-title">
            <strong>{props.text.appearance.barColorTitle}</strong>
            <HelpHint label={props.text.appearance.barColorTitle}>{props.text.appearance.barColorDescription}</HelpHint>
          </div>
        </div>
        <SegmentedControl
          value={appearance().barColorMode ?? pointPreferences().appearance.barColorMode!}
          options={props.text.barColorOptions}
          ariaLabel={props.text.appearance.barColorTitle}
          onChange={props.onBarColorModeChange}
        />
      </div>
      <div class="settings-row">
        <div class="settings-row-copy">
          <div class="settings-row-title">
            <strong>{props.text.appearance.byteUnitTitle}</strong>
            <HelpHint label={props.text.appearance.byteUnitTitle}>{props.text.appearance.byteUnitDescription}</HelpHint>
          </div>
        </div>
        <SegmentedControl
          value={appearance().byteUnitMode ?? pointPreferences().appearance.byteUnitMode!}
          options={props.text.byteUnitOptions}
          ariaLabel={props.text.appearance.byteUnitTitle}
          onChange={props.onByteUnitModeChange}
        />
      </div>
      <div class="settings-row">
        <div class="settings-row-copy">
          <div class="settings-row-title">
            <strong>{props.text.appearance.fontSmoothingTitle}</strong>
            <HelpHint label={props.text.appearance.fontSmoothingTitle}>{props.text.appearance.fontSmoothingDescription}</HelpHint>
          </div>
        </div>
        <SegmentedControl
          value={appearance().fontSmoothing ?? pointPreferences().appearance.fontSmoothing!}
          options={props.text.fontSmoothingOptions}
          ariaLabel={props.text.appearance.fontSmoothingTitle}
          onChange={props.onFontSmoothingChange}
        />
      </div>
      <div class="settings-row">
        <div class="settings-row-copy">
          <div class="settings-row-title">
            <strong>{props.text.appearance.settingsLanguageTitle}</strong>
            <HelpHint label={props.text.appearance.settingsLanguageTitle}>{props.text.appearance.settingsLanguageDescription}</HelpHint>
          </div>
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
      {props.children}
    </div>
  );
}
