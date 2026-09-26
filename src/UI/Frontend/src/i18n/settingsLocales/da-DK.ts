import { createSettingsLocale } from "../settingsLocaleFactory.ts";

export default createSettingsLocale("da-DK", {
  sections: { performance: "Ydeevne", appearance: "Udseende", systemIntegration: "Systemintegration", debug: "Fejlfinding", credits: "Tak" },
  saveState: { saving: "Gemmer", saved: "Gemt", error: "Kunne ikke gemme" },
  appearance: { languageTitle: "Sprog i brugerfladen", languageDescription: "Vælg sproget i Resource Managers brugerflade. System bruger Windows' visningssprog.", languageSelectLabel: "Sprog" }
});
