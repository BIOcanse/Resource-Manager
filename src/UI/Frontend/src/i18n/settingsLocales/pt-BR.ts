import { createSettingsLocale } from "../settingsLocaleFactory.ts";

export default createSettingsLocale("pt-BR", {
  sections: { performance: "Desempenho", appearance: "Aparência", systemIntegration: "Integração do sistema", debug: "Depuração", credits: "Créditos" },
  saveState: { saving: "Salvando", saved: "Salvo", error: "Falha ao salvar" },
  appearance: {
    languageTitle: "Idioma da interface",
    languageDescription: "Escolha o idioma da interface do Resource Manager. Sistema usa o idioma de exibição do Windows.",
    languageSelectLabel: "Idioma",
    themeOptions: { system: "Sistema", light: "Claro", dark: "Escuro", lowContrast: "Baixo contraste" }
  },
  credits: { heroTitle: "Obrigado a cada pessoa e projeto que torna o Resource Manager possível", groups: { project: "Projeto e contribuição", runtime: "Runtime e ferramentas de build", windows: "Interfaces do Windows e diagnóstico", hardware: "Monitoramento de hardware e suporte opcional" } }
});
