import type { AppCopy } from "../index.ts";

export const smartFailure: AppCopy["smartFailure"] = {
  title: {
    "cycle-failed": "Этот сегмент планирования завершился с ошибкой",
    "native-plan-failed": "Не удалось составить собственный план планирования"
  },
  hint: {
    "cycle-failed": "При выполнении этого сегмента планирования произошла ошибка, и он не был завершён. Это уведомление исчезнет, когда один из следующих сегментов завершится успешно. Подробности об ошибке записаны в журнал локальной службы.",
    "native-plan-failed": "Собственный компонент планирования не смог составить план для этого сегмента, поэтому в нём не выполнялось никаких изменений. Состояние плана указывает причину. Это уведомление исчезнет, когда один из следующих сегментов завершится успешно."
  },
  nativePlanStatus: (status: string) => `Состояние плана: ${status}`,
  nativePlanStatuses: {
    "invalid-argument": "Недопустимый аргумент",
    "abi-mismatch": "Версия интерфейса собственного компонента не совпадает",
    unavailable: "Собственный компонент недоступен",
    "no-data": "Нет доступных данных",
    "buffer-too-small": "Недостаточный объём буфера",
    "stale-generation": "Входные данные устарели",
    "out-of-memory": "Недостаточно памяти",
    "capacity-exceeded": "Превышен предел ёмкости",
    "conflicting-facts": "Входные данные противоречат друг другу",
    "feedback-mismatch": "Отчёт о выполнении не соответствует плану",
    "recreate-required": "Требуется заново создать собственную рабочую область"
  },
  exclusionTotal: (count: number) => `Исключено элементов данных в этом сегменте: ${count}`,
  exclusionsHint: "Исключённые данные — это данные, которые не участвовали в решениях планирования этого сегмента, поскольку отсутствовали, были недопустимыми или противоречивыми. Сводка показывает число исключений по элементу данных и причине; подробности указывают затронутые объекты.",
  summaryLabel: "Сводка исключений",
  viewExclusions: "Подробности исключений",
  detailTitle: "Данные, исключённые из этого сегмента",
  detailSummary: (listed: number, total: number) => `Всего исключено: ${total}; ниже перечислено: ${listed}.`,
  field: {
    processId: "ID процесса",
    processStartKey: "Идентификатор запуска процесса",
    targetId: "Идентификатор объекта планирования",
    target: "Объект планирования"
  },
  noTarget: "Не относится к конкретному объекту",
  facts: {
    inventory: "Список процессов",
    identity: "Идентичность процесса",
    "base-score": "Базовая оценка",
    attribution: "Принадлежность программе",
    "runtime-state": "Состояние выполнения объекта планирования",
    "cpu-core-residency": "Размещение на ядрах CPU",
    "cpu-score": "Оценка CPU",
    "gpu-score": "Оценка GPU",
    "compute-score": "Оценка вычислительной мощности",
    "memory-cleanup": "Очистка памяти",
    "decision-target": "Объект решения",
    "native-plan": "Собственный план планирования",
    CpuUsage: "Загрузка CPU процессом",
    MemoryUsage: "Использование памяти процессом",
    GpuUsage: "Загрузка GPU процессом",
    GpuDedicatedMemory: "Использование выделенной VRAM процессом",
    RuntimeState: "Состояние выполнения процесса"
  },
  reasons: {
    "dataset-not-current": "Данные получены не из текущей выборки",
    "unidentified-live-process": "Не удалось определить работающий процесс",
    "inventory-overflow": "Список процессов превысил свою ёмкость",
    "emitted-count-mismatch": "Число выданных записей не совпадает со списком процессов",
    "inventory-count-mismatch": "Число записей списка не совпадает с числом перечисленных",
    "capture-count-mismatch": "Счётчики сбора противоречат друг другу",
    "invalid-or-duplicate-inventory-identity": "В списке процессов есть недопустимая или повторяющаяся идентичность процесса",
    "invalid-or-duplicate-process": "Недопустимая или повторяющаяся идентичность процесса",
    "row-missing-or-invalid": "Данные отсутствуют или недопустимы",
    "gpu-row-missing-or-invalid": "Данные GPU отсутствуют или недопустимы",
    "compute-runtime-state-missing": "Отсутствует состояние выполнения",
    "cpu-residency-not-covered": "Выборка размещения на CPU не охватывает этот процесс",
    "cpu-residency-invalid": "Данные о размещении на CPU недопустимы",
    "target-process-count-not-one": "Объекту планирования соответствует не ровно один процесс",
    "target-process-identity-missing": "У объекта планирования нет идентичности процесса",
    "target-process-identity-mismatch": "Идентичность процесса у объекта планирования противоречива",
    "duplicate-target-process-identity": "Несколько объектов планирования указывают на один процесс",
    "target-runtime-state-invalid": "Состояние выполнения объекта планирования недопустимо",
    "cleanup-baseline-score-missing": "Отсутствует исходная оценка для очистки",
    "cleanup-current-process-facts-missing": "Отсутствуют текущие данные процесса",
    "cleanup-software-maximum-base-score-missing": "Отсутствует наибольшая базовая оценка программы",
    "ownership-software-attribution-mismatch": "Принадлежность изменений не совпадает с принадлежностью программе",
    "canonical-process-membership-mismatch": "Процесс не совпадает с составом программы в оценке",
    "canonical-software-score-missing": "Отсутствует оценка программы",
    "live-identity-without-decision-facts": "У работающего процесса нет данных для решения",
    "canonical-process-not-projected": "Оценка процесса не попала во входные данные планирования",
    "canonical-software-membership-incomplete": "Состав оценки программы неполон"
  }
};
