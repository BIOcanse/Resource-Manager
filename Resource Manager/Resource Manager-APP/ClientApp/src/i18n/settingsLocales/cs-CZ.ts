import { createSettingsLocale } from "../settingsLocaleFactory";

export default createSettingsLocale("cs-CZ", {
  sections: { performance: "Výkon", appearance: "Vzhled", systemIntegration: "Integrace se systémem", debug: "Ladění", credits: "Poděkování" },
  saveState: { saving: "Ukládání", saved: "Uloženo", error: "Uložení selhalo" },
  appearance: { languageTitle: "Jazyk rozhraní", languageDescription: "Zvolte jazyk rozhraní Resource Manageru. Možnost Systém použije jazyk zobrazení Windows.", languageSelectLabel: "Jazyk" }
});
