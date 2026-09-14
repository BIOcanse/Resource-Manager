import { createSettingsLocale } from "../settingsLocaleFactory";

export default createSettingsLocale("es-ES", {
  sections: { performance: "Rendimiento", appearance: "Apariencia", systemIntegration: "Integración del sistema", debug: "Depuración", credits: "Créditos" },
  saveState: { saving: "Guardando", saved: "Guardado", error: "Error al guardar" },
  appearance: {
    languageTitle: "Idioma de la interfaz",
    languageDescription: "Elige el idioma de la interfaz de Resource Manager. Sistema usa el idioma de visualización de Windows.",
    languageSelectLabel: "Idioma",
    themeOptions: { system: "Sistema", light: "Claro", dark: "Oscuro", lowContrast: "Bajo contraste" }
  },
  credits: { heroTitle: "Gracias a cada persona y proyecto que hace posible Resource Manager", groups: { project: "Proyecto y contribución", runtime: "Runtime y herramientas de compilación", windows: "Interfaces de Windows y diagnóstico", hardware: "Supervisión de hardware y soporte opcional" } }
});
