import { createSettingsLocale } from "../settingsLocaleFactory";

export default createSettingsLocale("ar-SA", {
  sections: { performance: "الأداء", appearance: "المظهر", systemIntegration: "تكامل النظام", debug: "التصحيح", credits: "الشكر" },
  saveState: { saving: "جار الحفظ", saved: "تم الحفظ", error: "فشل الحفظ" },
  appearance: {
    languageTitle: "لغة الواجهة",
    languageDescription: "اختر اللغة المستخدمة في واجهة Resource Manager. يستخدم خيار النظام لغة عرض Windows.",
    languageSelectLabel: "اللغة",
    themeOptions: { system: "النظام", light: "فاتح", dark: "داكن", lowContrast: "تباين منخفض" }
  },
  credits: { heroTitle: "شكرًا لكل شخص ومشروع يجعل Resource Manager ممكنًا", groups: { project: "المشروع والمساهمة", runtime: "بيئة التشغيل وأدوات البناء", windows: "واجهات Windows والتشخيص", hardware: "مراقبة العتاد والدعم الاختياري" } }
});
