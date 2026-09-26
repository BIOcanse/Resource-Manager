import { createSettingsLocale } from "../settingsLocaleFactory.ts";

export default createSettingsLocale("fr-FR", {
  sections: { performance: "Performances", appearance: "Apparence", systemIntegration: "Intégration système", debug: "Débogage", credits: "Crédits" },
  saveState: { saving: "Enregistrement", saved: "Enregistré", error: "Échec de l'enregistrement" },
  appearance: {
    languageTitle: "Langue de l’interface",
    languageDescription: "Choisissez la langue de l’interface de Resource Manager. Système utilise la langue d’affichage de Windows.",
    languageSelectLabel: "Langue",
    themeOptions: { system: "Système", light: "Clair", dark: "Sombre", lowContrast: "Faible contraste" }
  },
  credits: { heroTitle: "Merci à chaque personne et projet qui rend Resource Manager possible", groups: { project: "Projet et contribution", runtime: "Runtime et outils de build", windows: "Interfaces Windows et diagnostics", hardware: "Surveillance matérielle et prise en charge optionnelle" } }
});
