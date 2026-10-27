import { plural } from "../../plural.ts";
import type { AppCopy } from "../index.ts";

export const kernelLeakReport: AppCopy["kernelLeakReport"] = {
  tab: "Informe de fugas del kernel",
  intro: "Mientras se ejecuta, el servicio local observa de forma continua los recursos del kernel que mantiene cada proceso (actualmente, identificadores de proceso) y muestra los procesos cuyos recursos crecen de forma sostenida y podrían estar creciendo sin límite, junto con las pruebas. El resultado solo indica una sospecha; decida si debe actuar en función de las pruebas.",
  loading: "Cargando el informe de fugas del kernel…",
  readFailed: "No se pudo leer el informe de fugas del kernel.",
  capturedAt: "Capturado el",
  suspectedItems: "Elementos sospechosos",
  disabledTitle: "La observación de fugas del kernel está desactivada",
  disabledDetail: "La configuración del servicio local desactiva la observación de fugas del kernel, por lo que no se toman muestras ni se genera ningún informe.",
  observingTitle: "Observación en curso",
  observingDetail: "La evaluación comienza cuando el tiempo de observación y el número de muestras de esta ronda cumplen los requisitos.",
  emptyTitle: "No se han encontrado fugas del kernel sospechosas",
  emptyDetail: "El tiempo de observación y el número de muestras de esta ronda cumplen los requisitos, y ningún recurso del kernel crece de forma sostenida.",
  unavailableTitle: "Esta ronda de observación ha fallado",
  failure: {
    SourceUnavailable: "No se pudieron leer los datos de recursos de los procesos.",
    CapacityExceeded: "El número de procesos observados superó el límite de capacidad, por lo que esta ronda se detuvo.",
    InvalidObservation: "Los datos de muestra no eran válidos, por lo que esta ronda se detuvo."
  },
  observation: {
    title: "Ronda actual",
    startedAt: "Inicio de la ronda",
    elapsed: "Tiempo de observación",
    samples: "Muestras",
    notStarted: "Aún sin muestras",
    reachedOfRequired: (reached: string, required: string) => `${reached} (necesario: ${required})`
  },
  previousRun: {
    title: "La ronda anterior falló",
    reason: "Motivo",
    startedAt: "Inicio de la ronda",
    failedAt: "Hora del fallo"
  },
  restartPolicy: {
    NextInterval: "El servicio local inicia automáticamente una nueva ronda en el siguiente intervalo de muestreo."
  },
  unavailableHolders: (count: number) => plural("es-ES", count, {
    one: "No se pudieron leer los datos de recursos de {count} proceso; no se incluye en esta evaluación.",
    many: "No se pudieron leer los datos de recursos de {count} procesos; no se incluyen en esta evaluación.",
    other: "No se pudieron leer los datos de recursos de {count} procesos; no se incluyen en esta evaluación."
  }),
  resourceKind: {
    ProcessHandles: "Identificadores de proceso"
  },
  holder: {
    processId: "PID",
    productName: "Nombre del producto",
    services: "Servicios",
    imagePath: "Ruta de la imagen",
    noProductName: "No indicado",
    noServices: "Ningún servicio asociado",
    unavailable: "No se puede leer",
    processEnded: "Proceso finalizado"
  },
  evidence: {
    currentValue: "Valor actual",
    growth: "Aumento durante la observación",
    growthPerSecond: "Tasa de aumento",
    observationTime: "Tiempo de observación",
    sampleCount: "Muestras"
  },
  ratePerSecond: (value: string) => `${value}/s`,
  actions: {
    title: "Acciones",
    name: {
      RestartHolder: "Reiniciar el proceso propietario",
      ForceReleaseHandles: "Forzar la liberación de identificadores"
    },
    riskLevel: {
      Warning: "Advertencia",
      High: "Alto"
    },
    riskText: (level: string, risk: string) => `Riesgo: ${level}. ${risk}`,
    riskLabel: (name: string) => `Riesgo: ${name}`
  }
};
