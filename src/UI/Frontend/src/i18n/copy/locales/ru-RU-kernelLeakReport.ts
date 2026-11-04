import { plural } from "../../plural.ts";
import type { AppCopy } from "../index.ts";

export const kernelLeakReport: AppCopy["kernelLeakReport"] = {
  tab: "Отчёт об утечках ядра",
  intro: "Во время работы локальная служба непрерывно наблюдает за ресурсами ядра, которые удерживает каждый процесс (сейчас — дескрипторы процессов), и показывает процессы, у которых эти ресурсы непрерывно растут и, возможно, растут без ограничений, вместе с обоснованием. Результат лишь указывает на подозрение; решение о действиях принимайте на основании приведённых данных.",
  loading: "Загрузка отчёта об утечках ядра…",
  readFailed: "Не удалось прочитать отчёт об утечках ядра.",
  capturedAt: "Время сбора",
  suspectedItems: "Подозрительные элементы",
  historyItems: "Записи истории",
  unrecordedItems: "Не записано (достигнут предел)",
  items: "Записи отчёта",
  historyEntry: "История",
  notObservedTitle: "Утечки ядра не наблюдаются",
  notObservedDetail: "Наблюдение за утечками ядра сейчас не выполняется: возможно, оно отключено, план наблюдения ещё не опубликован или наблюдение остановлено. Пока оно не запущено, выборки не собираются и отчёт не создаётся.",
  observingTitle: "Идёт наблюдение",
  observingDetail: "Оценка начнётся, когда время наблюдения и число выборок в этом цикле достигнут требуемых значений.",
  emptyTitle: "Подозрений на утечки ядра не обнаружено",
  emptyDetail: "Время наблюдения и число выборок в этом цикле достигли требуемых значений; непрерывно растущих ресурсов ядра не обнаружено.",
  unavailableTitle: "Этот цикл наблюдения завершился ошибкой",
  failure: {
    SourceUnavailable: "Не удалось прочитать данные о ресурсах процессов.",
    CapacityExceeded: "Число наблюдаемых процессов превысило предел ёмкости; этот цикл остановлен.",
    InvalidObservation: "Данные выборки недействительны; этот цикл остановлен."
  },
  observation: {
    title: "Текущий цикл",
    startedAt: "Начало цикла",
    elapsed: "Время наблюдения",
    samples: "Число выборок",
    notStarted: "Выборок пока нет",
    reachedOfRequired: (reached: string, required: string) => `${reached} (требуется: ${required})`
  },
  previousRun: {
    title: "Предыдущий цикл завершился ошибкой",
    reason: "Причина",
    startedAt: "Начало цикла",
    failedAt: "Время ошибки"
  },
  restartPolicy: {
    NextInterval: "Локальная служба автоматически начнёт новый цикл в следующем интервале выборки."
  },
  unavailableHolders: (count: number) => plural("ru-RU", count, {
    one: "Не удалось прочитать данные о ресурсах {count} процесса; он не учтён в этой оценке.",
    few: "Не удалось прочитать данные о ресурсах {count} процессов; они не учтены в этой оценке.",
    many: "Не удалось прочитать данные о ресурсах {count} процессов; они не учтены в этой оценке.",
    other: "Не удалось прочитать данные о ресурсах {count} процесса; они не учтены в этой оценке."
  }),
  resourceKind: {
    ProcessHandles: "Дескрипторы процесса"
  },
  holder: {
    processId: "PID",
    productName: "Название продукта",
    services: "Службы",
    imagePath: "Путь к образу",
    noProductName: "Не указано",
    noServices: "Нет соответствующей службы",
    unavailable: "Недоступно",
    processEnded: "Процесс завершён"
  },
  evidence: {
    currentValue: "Текущее значение",
    growth: "Рост за время наблюдения",
    growthPerSecond: "Скорость роста",
    observationTime: "Время наблюдения",
    sampleCount: "Число выборок",
    firstObserved: "Впервые обнаружено",
    lastObserved: "Последнее обнаружение"
  },
  ratePerSecond: (value: string) => `${value}/с`,
  details: {
    button: "Сведения",
    back: "Назад к списку отчёта",
    title: (name: string) => `Сведения: ${name}`,
    holderSection: "Процесс-владелец",
    evidenceSection: "Основания",
    state: "Состояние",
    active: "Активно",
    resourceKind: "Тип ресурса",
    processName: "Имя процесса",
    startKey: "Ключ запуска процесса",
    unavailableReason: "Причина недоступности"
  },
  actions: {
    title: "Действия",
    name: {
      RestartHolder: "Перезапустить процесс-владелец",
      ForceReleaseHandles: "Принудительно освободить дескрипторы"
    },
    riskLevel: {
      Warning: "Предупреждение",
      High: "Высокий"
    },
    riskText: (level: string, risk: string) => `Риск: ${level}. ${risk}`,
    riskLabel: (name: string) => `Риск: ${name}`
  }
};
