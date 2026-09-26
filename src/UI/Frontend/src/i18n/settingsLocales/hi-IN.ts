import { createSettingsLocale } from "../settingsLocaleFactory.ts";

export default createSettingsLocale("hi-IN", {
  sections: { performance: "प्रदर्शन", appearance: "रूप", systemIntegration: "सिस्टम एकीकरण", debug: "डिबग", credits: "आभार" },
  saveState: { saving: "सहेजा जा रहा है", saved: "सहेजा गया", error: "सहेजना विफल" },
  appearance: { languageTitle: "इंटरफ़ेस भाषा", languageDescription: "Resource Manager इंटरफ़ेस की भाषा चुनें। सिस्टम Windows की प्रदर्शन भाषा का उपयोग करता है।", languageSelectLabel: "भाषा" }
});
