import type { AppCopy } from "../index.ts";
export const targetedReport: AppCopy["targetedReport"] = {
  tab: "Целевые отчёты", title: "Отчёты по целевому мониторингу",
  intro: "Записывает время кадров Present и использование ресурсов всеми процессами этого приложения.",
  maximumSeconds: "Максимальная длительность записи (секунды)", start: "Начать запись", stop: "Остановить запись",
  recording: "Идёт запись", completed: "Завершено", refresh: "Обновить отчёты",
  empty: "Записей пока нет.", loading: "Загрузка отчёта…",
  failed: "Не удалось выполнить действие с отчётом.", delete: "Удалить отчёт",
  confirmDelete: "Удалить этот отчёт о записи?", exportJson: "Экспорт JSON", exportCsv: "Экспорт CSV",
  frameTime: "Время кадра по времени", distribution: "Распределение FPS", resources: "Графики ресурсов",
  noFrames: "Нет доступных интервалов кадров.", applicationPresent: "Кадры Present приложения",
  incomplete: "В записи есть пропуски; статистика может быть занижена.",
  averageFps: "Средний FPS", onePercentLow: "1 % Low", pointOnePercentLow: "0,1 % Low",
  p50: "Время кадра P50", p95: "Время кадра P95", p99: "Время кадра P99",
  maxFrameTime: "Максимальное время кадра", frameCount: "Интервалы кадров",
  software: "Целевое приложение", system: "Система", seconds: "секунды",
  stopReasons: { user: "Остановлено вручную", processExit: "Процессы завершились", maxDuration: "Достигнут предел времени",
    interrupted: "Служба прервана", captureError: "Ошибка записи" }
};
