import { plural } from "../../plural.ts";
import type { AppCopy } from "../index.ts";

const nativePlanStatuses: AppCopy["smartFailure"]["nativePlanStatuses"] = {
  "invalid-argument": "Argumento no válido",
  "abi-mismatch": "La versión de interfaz del componente nativo no coincide",
  unavailable: "Componente nativo no disponible",
  "no-data": "No hay datos disponibles",
  "buffer-too-small": "Capacidad de búfer insuficiente",
  "stale-generation": "Los datos de entrada están desactualizados",
  "out-of-memory": "Memoria insuficiente",
  "capacity-exceeded": "Se ha superado el límite de capacidad",
  "conflicting-facts": "Los datos de entrada son contradictorios",
  "feedback-mismatch": "La respuesta de la ejecución no coincide con el plan",
  "recreate-required": "Es necesario volver a crear el espacio de trabajo nativo"
};

export const smartFailure: AppCopy["smartFailure"] = {
  title: {
    "cycle-failed": "Este segmento de planificación ha fallado",
    "native-plan-failed": "El plan de planificación nativo ha fallado"
  },
  hint: {
    "cycle-failed": "Se produjo un error durante la ejecución de este segmento de planificación y no se completó. Este aviso desaparece cuando un segmento posterior se completa correctamente. Los detalles del error se registran en el registro del servicio local.",
    "native-plan-failed": "El componente de planificación nativo no pudo generar un plan para este segmento, por lo que no se aplicó ningún ajuste en él. El estado del plan indica la causa. Este aviso desaparece cuando un segmento posterior se completa correctamente."
  },
  nativePlanStatus: (status: string) => `Estado del plan: ${status}`,
  nativePlanStatuses,
  exclusionTotal: (count: number) => plural("es-ES", count, {
    one: "Se excluyó {count} dato de este segmento",
    many: "Se excluyeron {count} de datos de este segmento",
    other: "Se excluyeron {count} datos de este segmento"
  }),
  exclusionsHint: "Los datos excluidos son los que no intervinieron en las decisiones de planificación de este segmento por faltar, no ser válidos o ser incoherentes. El resumen cuenta las exclusiones por dato y causa; los detalles indican los objetos afectados.",
  summaryLabel: "Resumen de exclusiones",
  viewExclusions: "Detalle de exclusiones",
  detailTitle: "Datos excluidos de este segmento",
  detailSummary: (listed: number, total: number) => `Total de exclusiones: ${total}; enumeradas a continuación: ${listed}.`,
  field: {
    processId: "ID de proceso",
    processStartKey: "Identificador de inicio del proceso",
    targetId: "Identificador del objeto de planificación",
    target: "Objeto de planificación",
    adapterKey: "Identificador del adaptador de GPU"
  },
  noTarget: "No se refiere a un objeto concreto",
  facts: {
    inventory: "Inventario de procesos",
    identity: "Identidad del proceso",
    attribution: "Atribución de software",
    "base-score": "Puntuación base",
    "cpu-usage": "Uso de CPU del proceso",
    "memory-usage": "Uso de memoria del proceso",
    "gpu-usage": "Uso de GPU del proceso",
    "gpu-dedicated-memory": "Uso de VRAM dedicada del proceso",
    "gpu-private-memory-bytes": "Memoria de GPU privada residente del proceso",
    "gpu-shared-memory-bytes": "Memoria de GPU compartida residente del proceso",
    "runtime-state": "Estado de ejecución del proceso",
    "cpu-core-residency": "Residencia en núcleos de CPU",
    "compute-score": "Puntuación de cómputo",
    "cpu-score": "Puntuación de CPU",
    "gpu-score": "Puntuación de GPU",
    "decision-target": "Objeto de decisión",
    "memory-cleanup": "Limpieza de memoria",
    "native-plan": "Plan de planificación nativo"
  },
  reasons: {
    "dataset-not-current": "Los datos no proceden del muestreo actual",
    "unidentified-live-process": "No se pudo identificar un proceso en ejecución",
    "inventory-overflow": "El inventario de procesos superó su capacidad",
    "emitted-count-mismatch": "El número emitido no coincide con el inventario de procesos",
    "inventory-count-mismatch": "El número del inventario no coincide con el enumerado",
    "capture-count-mismatch": "Los recuentos de captura son incoherentes",
    "invalid-or-duplicate-inventory-identity": "El inventario de procesos contiene una identidad de proceso no válida o duplicada",
    "invalid-or-duplicate-process": "Identidad de proceso no válida o duplicada",
    "row-missing-or-invalid": "Datos ausentes o no válidos",
    "gpu-row-missing-or-invalid": "Datos de GPU ausentes o no válidos",
    "compute-runtime-state-missing": "Falta el estado de ejecución",
    "cpu-residency-not-covered": "El muestreo de residencia de CPU no abarca este proceso",
    "cpu-residency-invalid": "Datos de residencia de CPU no válidos",
    "target-process-count-not-one": "El objeto de planificación no corresponde exactamente a un proceso",
    "target-process-identity-missing": "El objeto de planificación no tiene identidad de proceso",
    "target-process-identity-mismatch": "La identidad de proceso del objeto de planificación es incoherente",
    "duplicate-target-process-identity": "Varios objetos de planificación remiten al mismo proceso",
    "target-runtime-state-invalid": "El estado de ejecución del objeto de planificación no es válido",
    "cleanup-baseline-score-missing": "Falta la puntuación de referencia de la limpieza",
    "cleanup-current-process-facts-missing": "Faltan los datos actuales del proceso",
    "cleanup-software-maximum-base-score-missing": "Falta la puntuación base máxima del software",
    "ownership-software-attribution-mismatch": "La titularidad de los cambios no coincide con la atribución de software",
    "canonical-process-membership-mismatch": "El proceso no coincide con los miembros de software de la puntuación",
    "canonical-software-score-missing": "Falta la puntuación del software",
    "live-identity-without-decision-facts": "Un proceso en ejecución no tiene datos de decisión",
    "canonical-process-not-projected": "La puntuación del proceso no llegó a la entrada de planificación",
    "canonical-software-membership-incomplete": "Los miembros de la puntuación del software están incompletos",
    ...nativePlanStatuses
  }
};
