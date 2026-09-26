import { createSettingsLocale } from "../settingsLocaleFactory.ts";

export default createSettingsLocale("pt-PT", {
  sections: { performance: "Desempenho", appearance: "Aparência", systemIntegration: "Integração do sistema", debug: "Depuração", credits: "Créditos" },
  saveState: { saving: "A guardar", saved: "Guardado", error: "Falha ao guardar" },
  appearance: { languageTitle: "Idioma da interface", languageDescription: "Escolha o idioma da interface do Resource Manager. Sistema usa o idioma de apresentação do Windows.", languageSelectLabel: "Idioma", themeOptions: { system: "Sistema", light: "Claro", dark: "Escuro", lowContrast: "Baixo contraste" } },
  credits: { heroTitle: "Obrigado a cada pessoa e projeto que torna o Resource Manager possível", groups: { project: "Projeto e contribuição", runtime: "Runtime e ferramentas de build", windows: "Interfaces do Windows e diagnóstico", hardware: "Monitorização de hardware e suporte opcional" } }
});
