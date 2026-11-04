import { plural } from "../../plural.ts";
import type { AppCopy } from "../index.ts";

export const deviceActions: AppCopy["deviceActions"] = {
  restart: {
    label: "Reiniciar dispositivo",
    busy: "Reiniciando",
    hint: "Deshabilita y vuelve a habilitar el dispositivo al que apunta este informe, lo que puede hacer que un controlador bloqueado vuelva a funcionar. Usted lo confirma cada vez; el software nunca lo hace por su cuenta.",
    confirmTitle: "¿Reiniciar este dispositivo?",
    confirmMessage: (target: string) => `Se deshabilitará y se volverá a habilitar el dispositivo de «${target}».`,
    confirmDetails: [
      "Se ejecuta una sola vez, solo después de que usted lo confirme, y nunca se repite automáticamente.",
      "Se necesitan permisos de administrador. El resultado se guarda en las acciones de dispositivo de Detalles."
    ],
    risks: {
      DeviceRestartNetworkInterrupted: "Si es un adaptador de red, las conexiones de red se interrumpen un momento.",
      DeviceRestartDisplayStateLost: "Si es un adaptador gráfico, la pantalla se queda en negro brevemente y pueden perderse las posiciones de las ventanas y la configuración de pantalla.",
      DeviceRestartMayRequireReboot: "Algunos dispositivos solo se recuperan tras reiniciar el equipo."
    },
    unknownRisk: (code: string) => `Otro riesgo (${code}).`,
    confirm: "Reiniciar dispositivo",
    doneTitle: "Dispositivo reiniciado",
    refusedTitle: "El dispositivo no se reinició",
    failed: "No se pudo reiniciar el dispositivo.",
    reason: (text: string) => `Resultado: ${text}`,
    deviceResult: (text: string) => `Resultado del dispositivo: ${text}`,
    win32Detail: (code: string) => `Código de error del sistema: ${code}`,
    recorded: (sequence: string) => `Guardado en las acciones de dispositivo (número ${sequence}).`,
    notRecorded: "Esta solicitud no se registró."
  },
  reasons: {
    completed: "Completado",
    "confirmation-required": "Se requiere confirmación",
    "report-not-found": "No se encontró la entrada del informe",
    "unsupported-report": "Este tipo de informe no admite reiniciar un dispositivo",
    "device-evidence-unavailable": "El informe no tiene datos que identifiquen el dispositivo",
    "invalid-settings": "La configuración no es válida",
    "record-capacity-reached": "Las acciones de dispositivo están llenas; no se hizo nada",
    "persistence-failed": "No se pudo escribir el registro; no se hizo nada",
    "device-resolution-failed": "No se pudo identificar el dispositivo que reiniciar",
    "device-restart-failed": "Falló el reinicio del dispositivo",
    "notification-failed": "Se actuó sobre el dispositivo, pero no se pudo enviar la notificación"
  },
  results: {
    success: "Correcto",
    "confirmation-required": "Se requiere confirmación",
    "invalid-instance-id": "Id. de instancia de dispositivo no válido",
    "administrator-required": "Se necesitan permisos de administrador",
    "device-not-found": "No se encontró el dispositivo",
    "device-unavailable": "El dispositivo no está disponible ahora",
    "disable-failed": "Falló la deshabilitación del dispositivo",
    "enable-failed": "Falló la nueva habilitación del dispositivo",
    "reboot-required": "Hay que reiniciar el equipo",
    "verification-failed": "La comprobación tras el reinicio no se superó",
    "driver-not-found": "No se encontró el controlador",
    "ambiguous-device": "Coinciden varios dispositivos; no se hizo nada",
    "invalid-module": "Módulo de controlador no válido",
    "enumeration-failed": "Falló la enumeración de dispositivos",
    "buffer-too-small": "Búfer del sistema demasiado pequeño",
    "unsupported-platform": "No compatible con este sistema",
    interrupted: "Interrumpido durante la ejecución; se desconoce el resultado"
  },
  unknownCode: (code: string) => `Código ${code}`,
  records: {
    tab: "Acciones de dispositivo",
    intro: "Cada vez que confirma «Reiniciar dispositivo» en un informe, el resultado se guarda aquí. Los registros se conservan y nunca se eliminan automáticamente; un registro en curso pasa al historial cuando termina la acción.",
    loading: "Cargando las acciones de dispositivo…",
    readFailed: "No se pudieron cargar las acciones de dispositivo",
    empty: "Todavía no hay acciones de dispositivo.",
    loadMore: "Cargar registros anteriores",
    loadingMore: "Cargando",
    refresh: "Actualizar",
    shown: (count: string) => `Registros mostrados: ${count}.`,
    states: { active: "En curso", history: "Historial" },
    device: "Instancia del dispositivo",
    report: "Entrada del informe",
    time: "Hora",
    result: "Resultado",
    detail: "Código de error del sistema",
    sequence: "Número"
  }
};

export const crashDumpKeys: AppCopy["crashDumpKeys"] = {
  title: "Teclas de volcado por bloqueo",
  description: "Si se activa, al mantener pulsada la tecla Ctrl derecha y pulsar dos veces Bloq Despl, el sistema se detiene con una pantalla azul y escribe un volcado de memoria. Si el equipo se congela y no se puede usar con normalidad, así queda constancia del estado para analizarlo después. Desactivado de forma predeterminada.",
  details: [
    "Solo se activa cuando pulsa estas teclas; en otro caso no hace nada.",
    "El equipo se reinicia inmediatamente después y se pierde el trabajo no guardado.",
    "Los cambios surten efecto tras reiniciar el equipo y requieren permisos de administrador para escribirse. Al desactivarlo se restaura la configuración del sistema anterior a su activación."
  ],
  notProvided: "El servicio no proporcionó esta configuración.",
  statusTitle: "Estado en el sistema",
  states: {
    takesEffectAfterRestart: "Escrito en el sistema; surte efecto tras reiniciar el equipo.",
    off: "No activado.",
    restorePending: "Restaurando la configuración del sistema anterior; termina tras reiniciar el equipo.",
    configurationMismatch: "La configuración de teclas del sistema no coincide con la elección de aquí.",
    failed: "No se pudo escribir la configuración del sistema."
  },
  combinations: {
    rightCtrlScrollLockTwice: "Teclas: Ctrl derecha + Bloq Despl dos veces",
    custom: "Teclas: teclas personalizadas definidas por otro programa o a mano"
  },
  kernelDumpMissing: "El sistema no está configurado para escribir volcados de memoria del kernel, así que puede que no se escriba ningún volcado al activarlo.",
  reason: (text: string) => `Motivo: ${text}`,
  win32Detail: (code: string) => `Código de error del sistema: ${code}`,
  reasons: {
    "invalid-crash-dump-keys": "La configuración de teclas no es válida",
    "crash-dump-keys-read-failed": "No se pudo leer la configuración del sistema",
    "crash-dump-keys-write-failed": "No se pudo escribir la configuración del sistema",
    "crash-dump-keys-verify-failed": "La comprobación tras escribir no se superó",
    "crash-dump-keys-restore-pending": "La configuración anterior del sistema aún no se ha restaurado del todo",
    "invalid-crash-dump-key-snapshot": "La configuración original guardada no es válida",
    "crash-dump-keys-access-denied": "Se necesitan permisos de administrador",
    "crash-dump-key-value-too-large": "Un valor original del sistema es demasiado grande para guardarlo",
    "crash-dump-keys-configuration-mismatch": "Otro programa cambió la configuración del sistema",
    "crash-dump-keys-bridge-failed": "El servicio local no pudo procesar esta configuración"
  }
};

export const cpuMonopoly: AppCopy["cpuMonopoly"] = {
  intro: "Cuando un proceso con prioridad alta o en tiempo real mantiene la CPU totalmente ocupada, el sistema puede congelarse y dejar de responder. Si se activa, ese proceso vuelve a prioridad normal. Solo cambia la prioridad: el proceso no se finaliza ni se suspende, y su prioridad no se vuelve a cambiar. También se aplica la lista de procesos excluidos de la protección de memoria del equipo. Desactivado de forma predeterminada.",
  notProvided: "El servicio no proporcionó la configuración de la protección contra el monopolio de la CPU.",
  unsavedNote: "Hay cambios sin guardar; surten efecto al guardar.",
  rejected: (reason: string) => `No se aceptó la configuración: ${reason}`,
  enabledTitle: "Activar la protección contra el monopolio de la CPU",
  enabledDescription: "Si se activa, se comprueban en segundo plano el uso de CPU y la prioridad de los procesos; si se desactiva, no se comprueba nada.",
  thresholdTitle: "Umbral de uso",
  thresholdDescription: "El tiempo empieza a contar cuando un proceso con prioridad alta o en tiempo real alcanza este uso de CPU, en porcentaje del total de procesadores.",
  thresholdLabel: "Porcentaje",
  durationTitle: "Duración",
  durationDescription: "Cuánto tiempo debe mantenerse el uso en el umbral antes de volver el proceso a prioridad normal.",
  durationLabel: "Segundos",
  intervalTitle: "Intervalo de comprobación",
  intervalDescription: "Cada cuánto se lee el uso de CPU de los procesos.",
  intervalLabel: "Segundos",
  whitelistNote: "La lista de excluidos se edita en la sección Protección de memoria del equipo.",
  statusTitle: "Estado actual",
  statusLoading: "Cargando el estado de la protección contra el monopolio de la CPU…",
  statusUnavailable: "No se pudo cargar el estado de la protección contra el monopolio de la CPU",
  statusUnavailableDetail: "La configuración se puede seguir editando y guardando; el estado aparecerá aquí cuando esté disponible.",
  stateActive: "Comprobando",
  stateInactive: (reason: string) => `Sin comprobar: ${reason}`,
  inactiveReasons: {
    disabled: "Desactivada",
    "not-running": "La protección aún no se ha iniciado",
    "process-data-unavailable": "No se pueden leer los datos de los procesos",
    "process-exit-proof-unavailable": "No se puede confirmar si los procesos han terminado",
    "process-capacity": "Hay más procesos de los que se pueden seguir",
    stopped: "Detenida"
  },
  currentAction: "Acción vigente",
  exclusionsTitle: "Procesos excluidos",
  exclusionsEmpty: "Ahora no hay procesos excluidos.",
  exclusionReasons: {
    "resource-manager": "Un proceso de este software",
    "resource-manager-process": "Un proceso de este software",
    "protected-process": "En la lista de excluidos",
    "critical-process": "Un proceso crítico del sistema",
    "unsupported-platform": "No compatible con este sistema",
    "identity-changed": "El proceso ha cambiado",
    "cannot-open": "No se puede acceder al proceso",
    "criticality-unavailable": "No se puede confirmar si es un proceso crítico del sistema"
  },
  process: (name: string, processId: number) => `${name} (PID ${processId})`,
  percent: (value: string) => `${value} %`,
  seconds: (value: string) => `${value} s`,
  priorities: { High: "Alta", RealTime: "Tiempo real" },
  records: {
    tab: "Protección contra el monopolio de la CPU",
    intro: "La protección contra el monopolio de la CPU guarda un registro por cada proceso que trata. Tras volver un proceso a prioridad normal, su registro pasa al historial cuando el proceso termina o usted fija su prioridad; una acción fallida va directamente al historial. Los registros se conservan y nunca se eliminan automáticamente.",
    loading: "Cargando los registros de la protección contra el monopolio de la CPU…",
    unavailable: "No se pudieron cargar los registros de la protección contra el monopolio de la CPU",
    empty: "Todavía no hay acciones registradas.",
    count: (count: string, capacity: string) => `Registros: ${count}; como máximo ${capacity}.`,
    unrecorded: (count: number) => plural("es-ES", count, {
      one: "La lista está llena; {count} acción posterior no se registró. El registro se reanuda al vaciar el historial.",
      many: "La lista está llena; {count} acciones posteriores no se registraron. El registro se reanuda al vaciar el historial.",
      other: "La lista está llena; {count} acciones posteriores no se registraron. El registro se reanuda al vaciar el historial."
    }),
    outcomes: { normalized: "Devuelto a prioridad normal", failed: "Acción fallida" },
    states: { active: "Aún vigente", history: "Historial" },
    time: "Hora",
    cpu: "Uso de CPU",
    previousPriority: "Prioridad anterior",
    threshold: "Umbral en ese momento",
    duration: "Duración en ese momento",
    reason: "Motivo",
    detail: "Detalles",
    endedAt: "Finalizado",
    endReason: "Motivo del final"
  },
  actionReasons: {
    "process-exited": "El proceso terminó",
    "user-priority-set": "Su prioridad se fijó por separado",
    "write-failed": "No se pudo escribir la prioridad",
    "identity-changed": "El proceso ha cambiado",
    "priority-changed": "Otro programa cambió su prioridad",
    "priority-unavailable": "No se puede leer su prioridad",
    "priority-write-failed": "No se pudo escribir la prioridad",
    "priority-readback-unavailable": "No se pudo volver a leer la prioridad tras escribirla",
    "priority-readback-mismatch": "La prioridad leída tras escribirla no coincide"
  },
  notice: {
    title: "Protección contra el monopolio de la CPU",
    normalized: (target: string) => `${target} volvió a prioridad normal.`,
    failed: (target: string, reason: string) => `No se pudo ajustar ${target}: ${reason}`,
    openRecords: "Ver registros"
  }
};
