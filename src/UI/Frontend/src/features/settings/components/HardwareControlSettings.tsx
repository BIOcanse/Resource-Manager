import { uiText } from "../../../text.ts";
import type { SettingsTextBundle } from "../../../text.ts";
import type { AppSettings } from "../../../types.ts";
import { ControlAccessLevelSetting } from "./ControlAccessLevelSetting";
import { useFrontendVisibilityDemand } from "../../../frontendWork/useFrontendVisibilityDemand";
import { frontendVisibilitySurface } from "../../../frontendWork/frontendVisibilitySurface";
import { frontendWorkIds } from "../../../frontendWork/frontendWorkIds";
import { NumberField } from "./SettingsControls";

/**
 * 设置 → 硬件控制：保活维持设置、重新应用间隔和调节权限档位。
 * 保活关时控制页保存的设定只在启动时下发一次，开时按间隔反复下发（用户 10-29：「默认只会设置一次，
 * 但是如果设置了保活维持设置，那么久可以按照设定的频率进行反复下发保证值正确」）。
 * 间隔只在保活打开时有意义，所以紧跟在开关下面，关着时照样显示数值、但不可编辑，版面不随开关跳动。
 * 保活和间隔随设置页草稿保存；调节权限档位不走草稿，选完就生效（见 ControlAccessLevelSetting）。
 */
export function HardwareControlSettings(props: {
  control: NonNullable<AppSettings["control"]>;
  text: SettingsTextBundle;
  onChange: (control: NonNullable<AppSettings["control"]>) => void;
}) {
  // 这个分区的可见区域：调节权限档位按它读取（常驻页隐藏时退掉，回来时重读）。
  const workId = frontendWorkIds.settingsHardwareControl;
  useFrontendVisibilityDemand(workId, [workId]);
  return <div class="settings-section-panel" {...frontendVisibilitySurface(workId, [workId])}>
    <div class="settings-row">
      <div class="settings-row-copy">
        <strong>{props.text.hardwareControl.keepAliveTitle}</strong>
        <span>{props.text.hardwareControl.keepAliveDescription}</span>
      </div>
      <label class="settings-switch">
        <input type="checkbox" aria-label={props.text.hardwareControl.keepAliveTitle}
          checked={props.control.keepAlive}
          onChange={(event) => props.onChange({ ...props.control, keepAlive: event.currentTarget.checked })} />
        <span />
      </label>
    </div>
    <div class="settings-row">
      <div class="settings-row-copy">
        <strong>{props.text.hardwareControl.reassertionTitle}</strong>
        <span>{props.text.hardwareControl.reassertionDescription}</span>
      </div>
      <NumberField label={uiText.control.reassertionInterval}
        value={props.control.reassertionIntervalSeconds}
        min={1} max={86400} step={1}
        disabled={!props.control.keepAlive}
        onChange={reassertionIntervalSeconds => props.onChange({ ...props.control, reassertionIntervalSeconds })} />
    </div>
    <ControlAccessLevelSetting />
  </div>;
}
