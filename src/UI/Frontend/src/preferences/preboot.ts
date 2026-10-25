import { frontendPreferencesClient } from "./frontendPreferences.ts";

void frontendPreferencesClient().read().then(value => {
  document.documentElement.dataset.uiStyle = value.appearance.uiStyle;
  document.documentElement.dataset.theme = value.appearance.theme;
}).catch(error => console.error("Frontend preferences could not be read", error));
