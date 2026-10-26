import assert from "node:assert/strict";
import { isolatedSourceFunctions } from "./helpers/isolatedSourceFunctions.ts";
import { applyLanguage, uiText } from "../src/text.ts";
import { languageOptions } from "../src/i18n/settingsLanguages.ts";

const { formatProcessCount } = isolatedSourceFunctions(
  new URL("../src/features/gpuScheduling/GpuSchedulingModel.tsx", import.meta.url),
  ["formatProcessCount"], { uiText }
);
const expected: Record<string, string> = {
  "zh-CN": "3 软件 · 未请求进程明细",
  "zh-TW": "3 軟體 · 未請求程序詳細資料",
  "en-US": "3 software · Process details not requested",
  "ja-JP": "ソフトウェア 3 · プロセス詳細は未要求",
  "ko-KR": "소프트웨어 3 · 프로세스 세부 정보 미요청",
  "fr-FR": "3 logiciels · Détails des processus non demandés",
  "de-DE": "3 Software · Prozessdetails nicht angefordert",
  "es-ES": "3 programas · Detalles de procesos no solicitados",
  "ru-RU": "3 программы · Сведения о процессах не запрошены"
};
for (const language of languageOptions.filter(option => option.id !== "system")) {
  await applyLanguage(language.id);
  assert.equal(formatProcessCount({ softwareIds: ["a", "b", "c"] }), expected[language.id],
    language.id + ": an omitted detail request must not be reported as zero processes");
}
console.log("GPU summaries retain the unrequested process-detail state in all languages.");
