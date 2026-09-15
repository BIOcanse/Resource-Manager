import { createSettingsLocale } from "../settingsLocaleFactory.ts";

export default createSettingsLocale("ru-RU", {
  sections: { performance: "Производительность", appearance: "Внешний вид", systemIntegration: "Интеграция с системой", debug: "Отладка", credits: "Благодарности" },
  saveState: { saving: "Сохранение", saved: "Сохранено", error: "Не удалось сохранить" },
  appearance: {
    languageTitle: "Язык интерфейса",
    languageDescription: "Выберите язык интерфейса Resource Manager. Вариант «Система» использует язык интерфейса Windows.",
    languageSelectLabel: "Язык",
    themeOptions: { system: "Система", light: "Светлая", dark: "Темная", lowContrast: "Низкая контрастность" }
  },
  credits: { heroTitle: "Спасибо всем людям и проектам, благодаря которым существует Resource Manager", groups: { project: "Проект и вклад", runtime: "Runtime и инструменты сборки", windows: "Интерфейсы Windows и диагностика", hardware: "Мониторинг оборудования и дополнительная поддержка" } }
});
