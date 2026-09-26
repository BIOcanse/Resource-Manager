import { For, Show } from "solid-js";
import { Plus, Trash2 } from "lucide-solid";
import { StandardSelect } from "../StandardSelect";
import {
  decodeEditableHotkey,
  encodeEditableHotkey,
  hotkeyKeyOptions,
  isSafeDestructiveHotkeyEncoding
} from "../../settings/editableHotkeys";
import type { SettingsTextBundle } from "../../text.ts";
import type { AppEditableHotkeySettings } from "../../types";
import { RadioGroupItem, RadioGroupRoot } from "../../ui/primitives/RadioGroup.tsx";
import { uiText } from "../../text.ts";

const addKeyDefaults = [17, 18, 46, 123];

export function EditableHotkeyEditor(props: {
  hotkey: AppEditableHotkeySettings;
  labels: SettingsTextBundle["systemIntegration"];
  onChange: (hotkey: AppEditableHotkeySettings) => void;
}) {
  const model = () => decodeEditableHotkey(props.hotkey.encoding);
  const safeToEnable = () => isSafeDestructiveHotkeyEncoding(props.hotkey.encoding);
  const commit = (keys: number[], relations: Array<0 | 1>, enabled = props.hotkey.enabled) => {
    const encoding = encodeEditableHotkey({ keys, relations });
    props.onChange({
      ...props.hotkey,
      enabled: enabled && isSafeDestructiveHotkeyEncoding(encoding),
      encoding
    });
  };
  const updateKey = (index: number, key: number) => {
    const current = model();
    const keys = [...current.keys];
    if (keys.some((value, keyIndex) => keyIndex !== index && value === key)) return;
    keys[index] = key;
    commit(keys, current.relations);
  };
  const updateRelation = (index: number, relation: 0 | 1) => {
    const current = model();
    const relations = [...current.relations];
    relations[index] = relation;
    commit(current.keys, relations);
  };
  const removeKey = (index: number) => {
    const current = model();
    const keys = [...current.keys];
    const relations = [...current.relations];
    keys.splice(index, 1);
    if (relations.length > 0) relations.splice(index === 0 ? 0 : index - 1, 1);
    commit(keys, relations);
  };
  const addKey = () => {
    const current = model();
    const fallback = addKeyDefaults.find((key) => !current.keys.includes(key))
      ?? hotkeyKeyOptions().find((option) => !current.keys.includes(option.code))?.code;
    if (!fallback || current.keys.length >= 4) return;
    commit([...current.keys, fallback], [...current.relations, 0]);
  };

  return (
    <div class="settings-hotkey-editor">
      <div class="settings-hotkey-editor-head">
        <div class="settings-row-copy">
          <strong>{props.labels.forceTerminateTitle}</strong>
          <span>{props.labels.forceTerminateDescription}</span>
        </div>
        <label class="settings-switch">
          <input
            type="checkbox"
            aria-label={props.labels.forceTerminateTitle}
            checked={props.hotkey.enabled}
            disabled={!safeToEnable()}
            title={safeToEnable() ? undefined : uiText.hotkeyEditor.destructiveModifierRequired}
            onChange={(event) => commit(model().keys, model().relations, event.currentTarget.checked)}
          />
          <span />
        </label>
      </div>
      <div class="settings-hotkey-warning">{props.labels.forceTerminateWarning}</div>
      <Show when={model().keys.length > 0} fallback={<div class="settings-hotkey-empty">{props.labels.hotkeyEmpty}</div>}>
        <div class="settings-hotkey-keys">
          <For each={model().keys}>
            {(key, index) => (
              <>
                <Show when={index() > 0}>
                  <RadioGroupRoot
                    value={String(model().relations[index() - 1] ?? 0)}
                    ariaLabel={`${props.labels.hotkeyUnordered} / ${props.labels.hotkeyOrdered}`}
                    class="settings-hotkey-relation"
                    onChange={(value) => updateRelation(index() - 1, value === "1" ? 1 : 0)}
                  >
                    <RadioGroupItem value="0">
                      {props.labels.hotkeyUnordered}
                    </RadioGroupItem>
                    <RadioGroupItem value="1">
                      {props.labels.hotkeyOrdered}
                    </RadioGroupItem>
                  </RadioGroupRoot>
                </Show>
                <div class="settings-hotkey-key-row">
                  <span class="settings-hotkey-key-index">{index() + 1}</span>
                  <label>
                    <span>{props.labels.hotkeyKeyLabel}</span>
                    <StandardSelect
                      value={String(key)}
                      ariaLabel={`${props.labels.hotkeyKeyLabel} ${index() + 1}`}
                      searchable
                      searchPlaceholder={uiText.hotkeyEditor.searchKey}
                      options={hotkeyKeyOptions().map((option) => ({
                        value: String(option.code),
                        label: option.label,
                        group: option.group,
                        disabled: option.code !== key && model().keys.includes(option.code)
                      }))}
                      onChange={(value) => updateKey(index(), Number(value))}
                    />
                  </label>
                  <button
                    type="button"
                    class="settings-icon-button"
                    title={props.labels.hotkeyRemoveKey}
                    aria-label={props.labels.hotkeyRemoveKey}
                    onClick={() => removeKey(index())}
                  >
                    <Trash2 size={17} />
                  </button>
                </div>
              </>
            )}
          </For>
        </div>
      </Show>
      <button
        type="button"
        class="settings-hotkey-add"
        disabled={model().keys.length >= 4}
        onClick={addKey}
      >
        <Plus size={17} />
        <span>{props.labels.hotkeyAddKey}</span>
      </button>
    </div>
  );
}
