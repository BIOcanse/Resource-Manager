import type { AppThemeMode } from "../types";

export function publishCommittedDocumentTheme(theme: AppThemeMode) {
  const root = document.documentElement;
  root.dataset.theme = theme;

  // Remove attributes emitted by builds that predate the single root owner.
  root.removeAttribute("data-preboot-theme");
  document.body.removeAttribute("data-theme");
}

export function readDocumentTheme(): AppThemeMode {
  const theme = document.documentElement.dataset.theme;
  return theme === "light" || theme === "dark" || theme === "lowContrast"
    ? theme
    : "system";
}
