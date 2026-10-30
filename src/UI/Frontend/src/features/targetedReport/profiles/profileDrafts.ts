import { isIntervalInRange, sameItems } from "../../itemSelection/itemSelectionModel.ts";
import {
  maximumProfileNameLength,
  type ProfilePatch, type RecordingItem, type RecordingProfile
} from "../targetedRecordingApi.ts";

/*
 * 录制配置对话框里的草稿（docs/targeted_recording_views_2026-10-30.md §3.2）。
 * 改动先留在草稿里，显式保存才发 PATCH（新建的发 POST）；未保存的草稿在列表和编辑区都标出来，可以放弃。
 * savedId 为 null 的草稿是还没保存的新配置。
 */
export interface ProfileDraft {
  savedId: string | null;
  name: string;
  /** 默认间隔输入框的原文，保存前校验。 */
  defaultIntervalText: string;
  items: RecordingItem[];
}

export function draftFromProfile(profile: RecordingProfile): ProfileDraft {
  return { savedId: profile.id, name: profile.name, defaultIntervalText: String(profile.defaultIntervalMs), items: profile.items };
}

export interface DraftIssues {
  name: boolean;
  defaultInterval: boolean;
  itemsEmpty: boolean;
}

/** 名称去掉首尾空白后 1–64 个字符；默认间隔 100–60000 的整数；至少一个录制项（后端同样拒绝）。 */
export function draftIssues(draft: ProfileDraft): DraftIssues {
  const name = draft.name.trim();
  return {
    name: name.length < 1 || name.length > maximumProfileNameLength,
    defaultInterval: parsedDefaultInterval(draft) === null,
    itemsEmpty: draft.items.length === 0
  };
}

export function hasIssues(issues: DraftIssues) {
  return issues.name || issues.defaultInterval || issues.itemsEmpty;
}

export function parsedDefaultInterval(draft: ProfileDraft): number | null {
  const text = draft.defaultIntervalText.trim();
  if (!/^\d+$/.test(text)) return null;
  const value = Number(text);
  return isIntervalInRange(value) ? value : null;
}

/** 草稿和已保存的配置有没有不同。新配置总是算未保存。 */
export function draftChanged(draft: ProfileDraft, saved: RecordingProfile | undefined) {
  if (draft.savedId === null || saved === undefined) return true;
  return draft.name !== saved.name
    || draft.defaultIntervalText.trim() !== String(saved.defaultIntervalMs)
    || !sameItems(draft.items, saved.items);
}

/** 只带改过的字段的 PATCH 内容。调用前先确认没有问题。 */
export function profilePatch(draft: ProfileDraft, saved: RecordingProfile): ProfilePatch {
  const patch: ProfilePatch = {};
  const name = draft.name.trim();
  const interval = parsedDefaultInterval(draft);
  if (name !== saved.name) patch.name = name;
  if (interval !== null && interval !== saved.defaultIntervalMs) patch.defaultIntervalMs = interval;
  if (!sameItems(draft.items, saved.items)) patch.items = draft.items;
  return patch;
}
