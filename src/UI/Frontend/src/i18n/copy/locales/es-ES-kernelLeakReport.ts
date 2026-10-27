import { plural } from "../../plural.ts";
import type { AppCopy } from "../index.ts";

export const kernelLeakReport: AppCopy["kernelLeakReport"] = {
  tab: "Informe de fugas del kernel",
  intro: "Muestra los procesos cuyos recursos del kernel (actualmente, identificadores de proceso) crecen de forma sostenida y podrían estar creciendo sin límite, junto con las pruebas. El resultado solo indica una sospecha; decida si debe actuar en función de las pruebas. La observación solo se realiza mientras se muestra esta página y vuelve a empezar al abrirla de nuevo.",
  loading: "Cargando el informe de fugas del kernel…",
  readFailed: "No se pudo leer el informe de fugas del kernel.",
  capturedAt: "Capturado el",
  suspectedItems: "Elementos sospechosos",
  emptyTitle: "No se han encontrado fugas del kernel sospechosas",
  emptyDetail: "Este resultado también se muestra mientras el tiempo de observación todavía no basta para una evaluación. Los elementos sospechosos aparecerán aquí cuando se detecten.",
  notObservedTitle: "Todavía no se ha tomado ninguna muestra",
  unavailableTitle: "El informe de fugas del kernel no está disponible",
  failure: {
    NotObserved: "La observación ha comenzado. Los resultados aparecerán tras la primera muestra.",
    SourceUnavailable: "No se pueden leer los datos de recursos de los procesos.",
    CapacityExceeded: "El número de procesos observados supera el límite de capacidad. Esta observación se ha detenido.",
    InvalidObservation: "Los datos de muestreo no son válidos. Esta observación se ha detenido."
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
    currentValue: "Cantidad actual",
    growth: "Aumento durante la observación",
    growthPerSecond: "Tasa de aumento",
    observationTime: "Tiempo de observación",
    sampleCount: "Muestras"
  },
  ratePerSecond: (value: string) => `${value}/s`,
  actionsPending: "El servicio local todavía no ofrece acciones para este elemento."
};
