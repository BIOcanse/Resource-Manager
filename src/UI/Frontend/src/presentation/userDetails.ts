export interface UserDetailItem {
  label: string;
  value: string;
}

export interface UserDetailSection {
  title?: string;
  items: UserDetailItem[];
}

export function userDetailItem(label: string, value: unknown): UserDetailItem | null {
  const text = String(value ?? "").trim();
  return text ? { label, value: text } : null;
}

export function userDetailSection(title: string | undefined, items: Array<UserDetailItem | null | undefined>): UserDetailSection | null {
  const visibleItems = items.filter((item): item is UserDetailItem => Boolean(item));
  return visibleItems.length > 0 ? { title, items: visibleItems } : null;
}

export function compactUserDetailSections(sections: Array<UserDetailSection | null | undefined>) {
  return sections.filter((section): section is UserDetailSection => Boolean(section));
}
