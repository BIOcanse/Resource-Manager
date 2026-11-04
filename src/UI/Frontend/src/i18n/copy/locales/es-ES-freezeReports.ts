import { plural } from "../../plural.ts";
import type { AppCopy } from "../index.ts";

export const freezeReports: AppCopy["freezeReports"] = {
  systemEvents: {
    tab: "Eventos del sistema",
    intro: "El servicio local recoge de los registros del sistema eventos como reintentos y restablecimientos de almacenamiento, restablecimientos del controlador de gráficos, agotamiento de memoria, bloqueos de procesos del escritorio y errores de hardware, y los agrupa en entradas por categoría y destino. Este software no puede resolver estos problemas, por lo que aquí solo se detectan y notifican; las entradas sin eventos nuevos durante mucho tiempo pasan al historial.",
    loading: "Cargando el informe de eventos del sistema…",
    readFailed: "No se pudo leer el informe de eventos del sistema.",
    notObservedTitle: "No se están registrando eventos del sistema",
    notObservedDetail: "El registro de eventos del sistema no está en ejecución: puede haberse desactivado en la configuración o aún no ha comenzado. No se crean entradas hasta que se ejecute.",
    unavailableTitle: "Error en el registro de eventos del sistema",
    unavailableDetail: "El servicio local no puede leer los registros del sistema. El motivo se indica arriba.",
    emptyTitle: "No hay eventos del sistema",
    emptyDetail: "No ha aparecido en los registros del sistema ningún evento que deba notificarse.",
    status: {
      label: "Estado del registro",
      observing: "Registrando",
      "not-observed": "Sin registrar",
      unavailable: "Error en el registro"
    },
    activeCount: "Entradas activas",
    historyCount: "Entradas del historial",
    machineTarget: "Equipo completo",
    count: "Repeticiones",
    times: (count: number) => plural("es-ES", count, { one: "{count} vez", many: "{count} veces", other: "{count} veces" }),
    firstObserved: "Primera aparición",
    lastObserved: "Última aparición",
    overview: "Resumen",
    categoryLabel: "Categoría",
    target: "Destino",
    state: "Estado",
    activeEntry: "Activa",
    historyEntry: "Historial",
    category: {
      storage_retry_reset: "Reintentos y restablecimientos de almacenamiento",
      gpu_reset: "Restablecimiento del controlador de gráficos",
      memory_exhausted: "Agotamiento de memoria",
      desktop_crash: "Bloqueo de un proceso del escritorio",
      hardware_error: "Error de hardware",
      unexpected_shutdown: "Apagado inesperado y pantalla azul"
    },
    targetKindLabel: "Tipo de destino",
    targetKind: {
      machine: "Equipo completo",
      physical_disk: "Disco físico",
      storage_port: "Puerto del controlador de almacenamiento",
      volume: "Volumen",
      gpu_device: "Dispositivo gráfico",
      desktop_process: "Proceso del escritorio",
      hardware: "Hardware"
    },
    time: "Hora",
    channel: "Registro",
    channelName: {
      system: "Sistema",
      application: "Aplicación"
    },
    provider: "Origen",
    eventId: "Id. de evento",
    recordId: "Id. de registro",
    bugcheckCode: "Código de detención",
    dumpPath: "Archivo de volcado",
    failure: (name: string) => `No se pudieron leer los registros del sistema (${name}). Las entradas existentes se conservan y el registro continuará cuando se recupere la lectura.`,
    liveKernelFailure: (name: string) => `No se pudo examinar la carpeta LiveKernelReports (${name}). Los eventos de los registros del sistema no se ven afectados.`,
    unrecorded: (count: number) => plural("es-ES", count, {
      one: "{count} entrada más no se registró porque se alcanzó el límite de entradas.",
      many: "{count} entradas más no se registraron porque se alcanzó el límite de entradas.",
      other: "{count} entradas más no se registraron porque se alcanzó el límite de entradas."
    }),
    items: "Entradas de eventos del sistema",
    detailsButton: "Detalles",
    back: "Volver a la lista",
    eventsTitle: (count: number) => plural("es-ES", count, {
      one: "Último evento original ({count})",
      many: "Últimos eventos originales ({count})",
      other: "Últimos eventos originales ({count})"
    }),
    xml: "XML del evento"
  },
  recorder: {
    tab: "Registro de bloqueos",
    intro: "El servicio local escribe continuamente una pequeña cantidad de datos de memoria, grupos del kernel, discos e interrupciones en un archivo de registro de tamaño fijo. Si el equipo se bloquea y se apaga de forma forzada o se detiene con una pantalla azul, en el siguiente inicio se crea una revisión posterior al reinicio comparando los registros con los registros del sistema. Incluye los registros previos al bloqueo, los eventos del sistema del mismo período y las conclusiones. Esta información aquí solo se detecta y notifica.",
    loading: "Cargando el registro de bloqueos…",
    readFailed: "No se pudo leer el registro de bloqueos.",
    emptyTitle: "No hay revisiones posteriores al reinicio",
    emptyDetail: "No se ha detectado ningún apagado inesperado ni pantalla azul desde el último inicio.",
    recorder: "Registrador",
    running: "Registrando",
    stopped: "Sin registrar",
    lastRecordAt: "Último registro",
    reviewCount: "Revisiones posteriores al reinicio",
    failure: (name: string) => `El registrador ha fallado (${name}) y no está escribiendo registros.`,
    stoppedNote: "El registrador no está en ejecución; puede haberse desactivado en la configuración. Un bloqueo que ocurra mientras está desactivado no puede revisarse por completo.",
    gapsTitle: "Datos que faltan en los registros actuales",
    gap: {
      "no-sample": "Todavía no se ha escrito ningún registro",
      "interrupt-cpu-share": "Tiempo de procesador ocupado por interrupciones",
      "longest-interrupt": "Interrupción individual más larga",
      "active-kernel-leak-system-item": "Si hay activa una entrada de fuga del kernel a nivel de sistema",
      "process-private-commit": "Memoria confirmada privada de los procesos",
      "physical-disk-inventory": "Lista de discos físicos",
      "physical-disk-completed-operations": "Operaciones completadas de los discos físicos",
      commit: "Memoria confirmada y límite de confirmación",
      "physical-memory": "Memoria física",
      "kernel-pools": "Grupos paginado y no paginado"
    },
    reviews: "Revisiones posteriores al reinicio",
    reviewTitle: (time: string) => `Apagado inesperado · ${time}`,
    shutdownAt: "Hora del apagado o de la pantalla azul",
    gapDuration: "Intervalo",
    bugcheckCode: "Código de detención",
    tagsTitle: "Conclusiones",
    noTags: "No hay conclusiones.",
    inputsIncomplete: "Algunos registros o registros del sistema no pudieron leerse por completo durante la revisión, por lo que el resultado puede estar incompleto.",
    tag: {
      bugcheck: {
        name: "Pantalla azul",
        description: "Los registros del sistema contienen una pantalla azul; el código de detención figura en el resumen."
      },
      "commit-exhausted": {
        name: "Memoria confirmada agotada",
        description: "La memoria confirmada registrada alcanzó la línea inmediata de la protección de memoria, o el sistema notificó un agotamiento de memoria en el mismo período."
      },
      "physical-exhausted": {
        name: "Memoria física agotada",
        description: "La memoria física disponible registrada quedó por debajo del porcentaje establecido en la configuración."
      },
      "pool-high": {
        name: "Grupo del kernel demasiado alto",
        description: "El grupo no paginado superó el porcentaje establecido de la memoria física, o había activa una entrada de fuga del kernel a nivel de sistema."
      },
      "storage-unresponsive": {
        name: "Almacenamiento sin respuesta",
        description: "En el mismo período hubo reintentos o restablecimientos de almacenamiento, o un disco cumplió la condición de falta de respuesta varias veces seguidas."
      },
      "gpu-reset": {
        name: "Restablecimiento del controlador de gráficos",
        description: "En el mismo período se restableció el controlador de gráficos."
      },
      "hardware-error": {
        name: "Error de hardware",
        description: "El sistema notificó un error de hardware en el mismo período."
      },
      "recording-stopped-normal": {
        name: "Registros detenidos con normalidad",
        description: "No se produjo ninguno de los casos anteriores y los últimos registros eran todos normales. Las causas posibles son un controlador de almacenamiento o de filtro bloqueado, un corte repentino de corriente o mantener pulsado el botón de encendido."
      }
    },
    details: {
      button: "Detalles",
      back: "Volver a la lista",
      loading: "Cargando la revisión…",
      readFailed: "No se pudo leer la revisión.",
      export: "Exportar",
      exporting: "Exportando",
      exportFailed: "No se pudo exportar la revisión.",
      overview: "Resumen",
      reviewWindow: "Período de revisión",
      timeRange: (from: string, to: string) => `${from} – ${to}`,
      samplesSeen: "Registros en el período",
      inputsComplete: "Datos completos",
      yes: "Sí",
      no: "No",
      dumpPath: "Archivo de volcado",
      thresholdsTitle: "Umbrales utilizados en la revisión",
      commitImmediatePercent: "Línea inmediata de memoria confirmada",
      physicalAvailablePercent: "Mínimo de memoria física disponible",
      nonpagedPoolPercent: "Máximo del grupo no paginado (respecto a la memoria física)",
      storageConsecutive: "Registros consecutivos sin respuesta del disco",
      sampleInterval: "Intervalo de registro",
      reviewWindowLength: "Duración del período de revisión",
      curvesTitle: "Curvas de registro",
      curve: {
        commit: "Memoria confirmada (respecto al límite)",
        physicalAvailable: "Memoria física disponible (respecto al total)",
        nonpagedPool: "Grupo no paginado",
        pagedPool: "Grupo paginado",
        diskQueue: "Longitud de la cola de disco (máxima)",
        diskLatency: "Latencia de disco (máxima)",
        interrupt: "Tiempo de procesador ocupado por interrupciones"
      },
      curveRange: (min: string, max: string) => `${min} – ${max}`,
      noCurveData: "Sin datos",
      noRecords: "No hay registros en el período de revisión.",
      lastRecordTitle: "Último registro",
      time: "Hora",
      commit: "Memoria confirmada",
      physicalAvailable: "Memoria física disponible",
      ofLimit: (formattedValue: string, formattedLimit: string) => `${formattedValue} / ${formattedLimit}`,
      longestInterrupt: "Interrupción individual más larga",
      kernelLeakActive: "Entrada de fuga del kernel a nivel de sistema activa",
      processesTitle: "Procesos con más memoria confirmada privada",
      processName: (name: string, pid: string) => `${name} (PID ${pid})`,
      diskName: (id: string) => `Disco físico ${id}`,
      queueDepth: "Longitud de la cola",
      latency: "Latencia",
      completedOperations: "Operaciones completadas",
      eventsTitle: (count: number) => plural("es-ES", count, {
        one: "Evento del sistema en el período ({count})",
        many: "Eventos del sistema en el período ({count})",
        other: "Eventos del sistema en el período ({count})"
      }),
      noEvents: "No se recibieron eventos del sistema en el período de revisión.",
      actualShutdownAt: "Hora real del apagado"
    }
  },
  settings: {
    open: "Configuración",
    back: "Volver al informe",
    systemEventsTitle: "Configuración de eventos del sistema",
    recorderTitle: "Configuración del registro de bloqueos",
    readFailed: "No se pudo leer la configuración.",
    save: "Guardar",
    saving: "Guardando",
    discard: "Descartar cambios",
    saved: "La configuración se ha guardado.",
    saveFailed: "No se pudo guardar la configuración.",
    invalid: "La configuración no superó la validación y no se guardó. Revise los valores.",
    conflict: "La configuración no se guardó: la tabla de reglas, el número de registros y la ubicación del archivo de registro no pueden cambiarse durante la ejecución. Desactive primero la función correspondiente y guarde; después cambie estos elementos. Si ya existe un archivo de registro, cambiar el número de registros también requiere una nueva ubicación.",
    resourceBound: "La configuración no se guardó: los recursos necesarios superan el límite. Reduzca la capacidad o el número de registros.",
    draftInvalid: "Los cambios actuales están incompletos (por ejemplo, una regla no tiene nombre de origen) y no pueden guardarse.",
    rulesLocked: "La tabla de reglas no puede cambiarse mientras el registro de eventos del sistema está activado. Desactívelo y guarde primero.",
    recorderLocked: "Este elemento no puede cambiarse mientras el registrador está activado. Desactívelo y guarde primero.",
    units: {
      minutes: "Minutos",
      seconds: "Segundos",
      milliseconds: "Milisegundos",
      percent: "Porcentaje",
      count: "Cantidad"
    },
    systemEvents: {
      enabled: {
        title: "Registrar eventos del sistema",
        description: "Recibir eventos de los registros del sistema y agruparlos en entradas. Al desactivarlo, no se reciben eventos nuevos y las entradas existentes se conservan."
      },
      historyAfter: {
        title: "Pasar al historial tras",
        description: "Una entrada pasa al historial cuando no llega ningún evento nuevo durante este tiempo después de su último evento."
      },
      eventTailCapacity: {
        title: "Eventos originales conservados por entrada",
        description: "Cuántos de los eventos originales más recientes conserva cada entrada para la página de detalles."
      },
      entryCapacity: {
        title: "Límite de entradas",
        description: "Número máximo de entradas conservadas; una vez alcanzado, las entradas nuevas solo se cuentan y no se registran."
      },
      liveKernelRoot: {
        title: "Ubicación de LiveKernelReports",
        description: "Carpeta en la que el sistema guarda los informes de volcado del kernel en vivo; se usa para encontrar informes como los tiempos de espera del controlador de gráficos."
      },
      liveKernelDirectories: {
        title: "Subcarpetas examinadas",
        description: "Subcarpetas que se examinan dentro de la ubicación anterior, una por línea."
      },
      liveKernelEntryCapacity: {
        title: "Límite de informes de volcado",
        description: "Número máximo de informes de volcado registrados en cada examen."
      },
      rules: {
        title: "Reglas de eventos",
        description: "Qué eventos de los registros del sistema pertenecen a cada categoría y a qué tipo de destino se refiere cada evento."
      }
    },
    recorder: {
      enabled: {
        title: "Registrar datos de bloqueos",
        description: "Escribir registros de forma continua y crear una revisión en el siguiente inicio tras un apagado inesperado. Al desactivarlo, el registro se detiene y el archivo de registro se conserva."
      },
      sampleInterval: {
        title: "Intervalo de registro",
        description: "Tiempo entre dos registros. Cuanto más corto sea, más cerca estará el último registro del momento del bloqueo."
      },
      reviewWindow: {
        title: "Período de revisión",
        description: "Cuánto tiempo antes del último registro muestra la revisión registros y eventos del sistema."
      },
      commitImmediate: {
        title: "Línea inmediata de memoria confirmada",
        description: "Línea usada para detectar el agotamiento de la memoria confirmada. Sigue la línea inmediata de la protección de memoria y se cambia en su configuración."
      },
      commitImmediateNone: "Sin establecer",
      physicalAvailable: {
        title: "Mínimo de memoria física disponible",
        description: "Se considera agotada la memoria física cuando la disponible cae por debajo de este porcentaje del total."
      },
      nonpagedPool: {
        title: "Máximo del grupo no paginado",
        description: "Se considera demasiado alto el grupo del kernel cuando el grupo no paginado supera este porcentaje de la memoria física."
      },
      storageConsecutive: {
        title: "Registros consecutivos sin respuesta del disco",
        description: "Se considera que el almacenamiento no responde cuando un disco cumple la condición en este número de registros seguidos."
      },
      slots: {
        title: "Número de registros",
        description: "Cuántos registros caben en el archivo de registro, lo que determina hasta dónde puede remontarse la revisión. Si ya existe un archivo de registro, cambiarlo también requiere una nueva ubicación."
      },
      ringPath: {
        title: "Ubicación del archivo de registro",
        description: "Ruta completa del archivo de registro. Déjela vacía para usar la ubicación predeterminada."
      },
      ringPathDefault: "Ubicación predeterminada"
    },
    ruleTable: {
      channel: "Registro",
      provider: "Nombre del origen",
      ids: "Id. de evento",
      idsHint: "Separados por comas",
      category: "Categoría",
      targetKind: "Tipo de destino",
      remove: "Quitar",
      add: "Agregar regla"
    }
  }
};
