import { createSettingsLocale } from "../settingsLocaleFactory";

export default createSettingsLocale("pl-PL", {
  sections: { performance: "Wydajność", appearance: "Wygląd", systemIntegration: "Integracja z systemem", debug: "Debugowanie", credits: "Podziękowania" },
  saveState: { saving: "Zapisywanie", saved: "Zapisano", error: "Nie udało się zapisać" },
  appearance: { languageTitle: "Język interfejsu", languageDescription: "Wybierz język interfejsu Resource Managera. System używa języka wyświetlania systemu Windows.", languageSelectLabel: "Język" }
});
