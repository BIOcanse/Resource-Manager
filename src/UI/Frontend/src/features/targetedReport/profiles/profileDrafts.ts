import { isIntervalInRange, sameItems } from "../../itemSelection/itemSelectionModel.ts";
import {
  maximumProfileNameLength,
  type ProfilePatch, type RecordingItem, type RecordingProfile
} from "../targetedRecordingApi.ts";

/* Save drafts explicitly with PATCH or POST; savedId null identifies an unsaved profile. */
export interface ProfileDraft {
  savedId: string | null;
  name: string;
  /** Preserve raw interval text for validation before saving. */
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

/** Match backend name, integer-interval, and nonempty-item constraints. */
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

/** New profiles are always unsaved; compare existing drafts with saved profiles. */
export function draftChanged(draft: ProfileDraft, saved: RecordingProfile | undefined) {
  if (draft.savedId === null || saved === undefined) return true;
  return draft.name !== saved.name
    || draft.defaultIntervalText.trim() !== String(saved.defaultIntervalMs)
    || !sameItems(draft.items, saved.items);
}

/** Include changed fields only; validate before calling. */
export function profilePatch(draft: ProfileDraft, saved: RecordingProfile): ProfilePatch {
  const patch: ProfilePatch = {};
  const name = draft.name.trim();
  const interval = parsedDefaultInterval(draft);
  if (name !== saved.name) patch.name = name;
  if (interval !== null && interval !== saved.defaultIntervalMs) patch.defaultIntervalMs = interval;
  if (!sameItems(draft.items, saved.items)) patch.items = draft.items;
  return patch;
}
