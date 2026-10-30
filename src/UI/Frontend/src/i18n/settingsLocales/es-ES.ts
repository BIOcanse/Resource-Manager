import { createSettingsLocale } from "../settingsLocaleFactory.ts";
import { plural } from "../plural.ts";

export default createSettingsLocale("es-ES", {
  "notifications": {
    "intro": "Elige para cada tipo de notificación si se muestra y por qué vías. Las notificaciones de Windows las muestra la bandeja; los avisos en la aplicación aparecen mientras la interfaz está visible.",
    "kinds": {
      "memoryProtection": { "title": "Protección de memoria del equipo", "description": "Notifica cuando la protección de memoria del equipo termina un proceso o un árbol de procesos, con el objetivo, el número de procesos y el tamaño del volcado." },
      "update": { "title": "Nueva versión", "description": "Notifica cuando hay una nueva versión disponible." },
      "accessRequest": { "title": "Solicitudes de acceso", "description": "Notifica cuando una nueva solicitud de acceso de confianza espera aprobación, con el nombre y el programa del solicitante." }
    },
    "notifyTitle": "Mostrar notificaciones",
    "notifyDescription": "Si está desactivado, este tipo de notificación no se muestra por ninguna vía; se conserva la elección de ambas vías.",
    "systemTitle": "Notificación de Windows",
    "systemDescription": "La bandeja muestra una notificación del sistema de Windows, también con la ventana principal cerrada; al hacer clic se abre Resource Manager.",
    "inAppTitle": "Aviso en la aplicación",
    "inAppDescription": "Aparece dentro de la interfaz mientras está visible; las notificaciones que llegan con la interfaz cerrada no se muestran después.",
    "switchLabel": (kind: string, option: string) => `${kind}: ${option}`,
    "accessRequestNoticeTitle": "Nueva solicitud de acceso pendiente de aprobación",
    "accessRequestNoticeMessage": (name: string, program: string) => `${name} (${program}) solicita acceso de confianza.`,
    "accessRequestNoticeOpen": "Ver solicitudes de acceso"
  },
  "updates": {
    "modeTitle": "Modo de actualización",
    "modeDescription": "Desactivado no hace comprobaciones automáticas. La comprobación y la elección manual de versión funcionan en todos los modos.",
    "modeOptions": [{ id: "off", label: "Desactivado", description: "Sin comprobaciones, descargas ni instalaciones automáticas." }, { id: "notify", label: "Solo comprobar y avisar", description: "Comprueba automáticamente y avisa de una versión nueva sin descargarla." }, { id: "automatic", label: "Descargar y aplicar", description: "Comprueba, descarga y verifica automáticamente; aplica al cerrar las ventanas o con Aplicar ahora." }],
    "periodicTitle": "Comprobación periódica",
    "periodicDescription": "Tras la comprobación de inicio, vuelve a comprobar con este intervalo.",
    "internetNote": "Las comprobaciones automáticas esperan a que haya acceso a Internet; el intervalo solo cuenta el tiempo conectado.",
    "offReason": "El modo está Desactivado: no hay comprobaciones automáticas y estos dos ajustes no tienen efecto; sus valores se conservan.",
    "intervalTitle": "Intervalo de comprobación (horas)",
    "intervalLabel": (minimum: number, maximum: number) => `Intervalo (horas, ${minimum}–${maximum})`,
    "manualCheckNote": "La comprobación manual se ejecuta al momento, sin esperar a Internet, en todos los modos.",
    "waitingForInternet": "Esperando a Internet; la comprobación se hará en cuanto haya conexión.",
    "availableVersion": "Versión nueva encontrada: ",
    "nextCheck": "Próxima comprobación: ",
    "nextCheckAfterInternet": "cuenta al conectar",
    "nextCheckOff": "no activada",
    "connectedElapsed": "Tiempo conectado desde la última comprobación: ",
    "noticeTitle": "Hay una versión nueva",
    "noticeMessage": (version: string) => `Resource Manager ${version} está disponible.`,
    "noticeReadyMessage": (version: string) => `Resource Manager ${version} está descargado y verificado; puede aplicarlo ahora.`,
    "noticeOpenUpdates": "Abrir Actualizaciones y versiones",
    "installedVersionTitle": "Versión instalada",
    "unknownVersion": "Desconocido",
    "checking": "Comprobando…",
    "checkForUpdates": "Buscar actualizaciones",
    "historyIncomplete": "El historial de versiones está incompleto.",
    "historyStale": "Se muestra el catálogo anterior; vuelve a conectarte e inténtalo de nuevo.",
    "versionsTitle": "Versiones del producto",
    "versionsDescription": "De más reciente a más antigua; cada serie muestra su última versión por defecto.",
    "lastChecked": "Última comprobación: ",
    "selectedTarget": "Versión seleccionada: ",
    "prepare": "Preparar actualización seleccionada",
    "confirmPrepare": (version: string) => `¿Preparar la versión ${version}? El gestor verifica el paquete, espera a que se cierren todas las sesiones de escritorio, detiene el servicio, conserva y migra los datos y verifica la nueva versión. Si falla, restaura la versión anterior.`,
    "prepareFailed": "No se puede preparar la actualización.",
    "waitingForExit": "Actualización lista. Sal de todas las sesiones de escritorio de Resource Manager desde la bandeja para aplicarla.",
    "stageLabel": "Estado de actualización",
    "unknownStage": "Estado de actualización desconocido",
    "stages": {
      "idle": "Inactivo",
      "checking": "Buscando actualizaciones",
      "downloading": "Descargando paquete",
      "verifying": "Verificando paquete",
      "waitingForExit": "Esperando el cierre de las sesiones",
      "completed": "Actualización completada",
      "error": "Error de actualización"
    },
    "otherVersions": "Otras versiones",
    "installedUnknown": "La versión instalada es desconocida; no se puede comprobar la dirección de actualización.",
    "stable": "Estable",
    "preview": "Preliminar",
    "noLatest": "No hay una última versión disponible.",
    "noStable": "No hay una versión estable disponible.",
    "latest": "Última versión",
    "latestStable": "Última versión estable",
    "verified": "Versión verificada",
    "unavailableReasons": {
      "installedVersionUnknown": "Versión instalada desconocida; la selección de actualización está desactivada.",
      "notNewer": "Solo se pueden elegir versiones posteriores a la instalada."
    }
  },
  "navigationLabel": "Secciones de ajustes",
  "sections": {
    "runtimePolicy": "Directiva de ejecución",
    "memory": "Memoria",
    "memoryCpuTier": "Equilibrio entre memoria y CPU",
    "memoryProtection": "Protección de memoria del equipo",
    "hardwareControl": "Control de hardware",
    "reportRules": "Reglas de informes",
    "appearance": "Apariencia",
    "performanceOverlay": "Superposición de rendimiento",
    "systemIntegration": "Integración del sistema",
    "publicServices": "Servicios compartidos",
    "trustedAccess": "Acceso de confianza",
    "notifications": "Notificaciones",
    "updates": "Actualizaciones y versiones",
    "debug": "Depuración",
    "credits": "Créditos"
  },
  "saveState": {
    "saving": "Guardando",
    "saved": "Guardado",
    "constrained": "Guardado; se limitaron las opciones de ejecución no disponibles",
    "partial": "Guardado; la publicación o entrega del plan de ejecución está incompleta",
    "error": "Error al guardar",
    "dirty": "Sin guardar",
    "conflict": "Los ajustes cambiaron en otro lugar; recarga antes de guardar",
    "invalidValueType": "Guardado rechazado: un valor de configuración tiene un tipo no válido"
  },
  "loadState": {
    "loading": "Cargando ajustes guardados…",
    "errorTitle": "Ajustes no disponibles",
    "errorDescription": "Los valores predeterminados no sustituyen a los ajustes guardados. Restablece el servicio local y vuelve a intentarlo.",
    "staleTitle": "Ajustes desincronizados",
    "staleDescription": "Esta copia puede estar desactualizada. Los cambios no se enviarán hasta que la recarga termine correctamente."
  },
  "actions": {
    "save": "Guardar",
    "restoreDefaults": "Restaurar valores predeterminados",
    "retry": "Reintentar",
    "reload": "Recargar",
    "reapply": "Volver a publicar el plan de ejecución"
  },
  "performance": {
    "preciseGpuPlacementTitle": "Selección precisa de GPU",
    "preciseGpuPlacementDescription": "Permite elegir una GPU concreta para procesos de renderizado compatibles. El cambio durante la ejecución depende de la compatibilidad y se configura por aplicación.",
    "preciseGpuPlacementModeOptions": {
      "basic": {
        "label": "Desactivado",
        "description": "Usar solo las preferencias de GPU que admite Windows."
      },
      "precise": {
        "label": "Activado",
        "description": "Permitir seleccionar una GPU concreta para cada programa."
      }
    },
    "automaticMemoryCleanupTitle": "Umbrales de limpieza de emergencia",
    "automaticMemoryCleanupDescription": "La limpieza en segundo plano sigue los niveles de presión sobre los recursos. Estos umbrales determinan cuándo la poca memoria física o capacidad de compromiso restante activa la limpieza de emergencia. Solo se aplican cuando el modo de optimización incluye la gestión de memoria.",
    "physicalMemoryAutomaticCleanupLabel": "Memoria física libre %",
    "virtualMemoryAutomaticCleanupLabel": "Margen de memoria comprometida %",
    "memoryOptimizationTargetTitle": "Objetivo de optimización de memoria",
    "memoryOptimizationTargetDescription": "Por encima del objetivo continúa la liberación lenta de nivel uno, sin activar una limpieza de emergencia.",
    "physicalMemoryOptimizationTargetLabel": "Uso objetivo de memoria física %",
    "virtualMemoryOptimizationTargetLabel": "Uso objetivo de memoria comprometida %",
    "gpuOverflowTitle": "Umbrales de desbordamiento de la GPU dedicada",
    "gpuOverflowDescription": "Al alcanzar cualquiera de los umbrales, los procesos en segundo plano con baja puntuación pasan de la GPU dedicada a la integrada. Requiere planificación activa y una GPU integrada disponible.",
    "gpuOverflowUsageLabel": "Uso de la GPU dedicada %",
    "gpuOverflowDedicatedMemoryLabel": "Uso de memoria dedicada de la GPU %",
    "realtimeActionBudgetTitle": "Límite de acciones en tiempo real",
    "realtimeActionBudgetDescription": "Limita las acciones en tiempo real por ciclo a una cantidad fija o a un porcentaje de todos los procesos identificados en ese ciclo, redondeado hacia arriba. Al cambiar de modo se conservan ambos valores.",
    "realtimeActionBudgetModeOptions": [{"id":"fixed","label":"Cantidad fija"},{"id":"percent","label":"Porcentaje de procesos"}],
    "realtimeActionBudgetCountLabel": "Cantidad fija de acciones",
    "realtimeActionBudgetPercentLabel": "Todos los procesos %",
    "keepSmartRecordHistoryTitle": "Conservar el historial de la programación inteligente",
    "keepSmartRecordHistoryDescription": "Si está activado, cada registro de la programación inteligente permanece en la pestaña Informe de Detalles después de restaurarse, ordenado por número de secuencia y sin límite. Si está desactivado, los registros nuevos no se conservan después de restaurarse; el historial ya conservado no se ve afectado.",
    "keepSmartRecordHistoryOptions": {
      "on": { "label": "Activado", "description": "Conservar los registros como historial después de restaurarse." },
      "off": { "label": "Desactivado", "description": "No conservar los registros después de restaurarse." }
    },
    "refreshCadenceTitle": "Frecuencia de actualización",
    "refreshCadenceDescription": "Controla la frecuencia de actualización de las mediciones y los estados. Una frecuencia mayor usa algo más de recursos.",
    "presetNumericModeOptions": [
      {
        "id": "aotu",
        "label": "Automático",
        "description": "Usar el preajuste automático integrado."
      },
      {
        "id": "preset",
        "label": "Fijar preajuste",
        "description": "Usar siempre la frecuencia del preajuste seleccionado."
      },
      {
        "id": "custom",
        "label": "Personalizado",
        "description": "Usar siempre el valor en milisegundos introducido manualmente."
      }
    ],
    "refreshCadencePresetLabel": "Preajuste",
    "refreshCadenceCustomLabel": "Personalizado (ms)",
    "refreshCadencePresetOptions": [
      {
        "id": "responsive",
        "label": "Respuesta rápida",
        "description": "Prioriza la interacción en primer plano con actualizaciones más rápidas."
      },
      {
        "id": "balanced",
        "label": "Equilibrado",
        "description": "Una frecuencia práctica para el uso diario."
      },
      {
        "id": "lowPower",
        "label": "Bajo consumo",
        "description": "Estrategia de bajo consumo en segundo plano."
      },
      {
        "id": "quiet",
        "label": "Silencioso",
        "description": "Frecuencia mínima manteniendo las actualizaciones necesarias."
      }
    ],
    "refreshCadenceItems": {
      "monitor": {
        "label": "Mediciones y barras de recursos",
        "description": "Actualiza los valores del panel y las barras de recursos."
      },
      "resourceTable": {
        "label": "Tabla de recursos",
        "description": "Actualiza la lista de recursos de programas y procesos."
      },
      "localSystem": {
        "label": "Sistema local",
        "description": "Actualiza estados locales de baja frecuencia, como el tiempo de actividad."
      }
    }
  },
  "appearance": {
    "cacheDescription": "La apariencia, los intervalos de actualizaci\u00F3n y la apariencia de la superposici\u00F3n se guardan en la cach\u00E9 de este dispositivo. Al borrar la cach\u00E9, se restablecen los valores predeterminados.",
    "resetCache": "Restablecer preferencias de interfaz",
    "uiStyleTitle": "Estilo de la interfaz",
    "uiStyleDescription": "Cambia las esquinas, líneas, sombras y fuentes de los controles; el diseño no cambia. Cada estilo tiene sus propias paletas.",
    "themeTitle": "Paleta",
    "themeDescription": "Elige una paleta del estilo actual. Sistema sigue el ajuste claro u oscuro de Windows.",
    "animationStyleTitle": "Estilo de animación",
    "animationStyleDescription": "Elige cómo se reproducen las transiciones y cómo se colocan los controles. Máximo rendimiento elimina además las sombras, los reflejos y el desenfoque.",
    "animationStyleOptions": {
      "smooth": { "label": "Fluido", "description": "Como macOS y los sistemas móviles: movimiento de resorte que conserva la velocidad del gesto, se puede detener en cualquier momento y rebota en los bordes del desplazamiento" },
      "desktop": { "label": "Escritorio estándar", "description": "Como el software de escritorio habitual de Windows: transiciones breves con desaceleración, sin conservar la velocidad ni rebotes" },
      "terminal": { "label": "Terminal por pasos", "description": "Llega al final en un número fijo de pasos, sin curva de aceleración, como el refresco de un terminal de texto" },
      "none": { "label": "Sin animaciones", "description": "Todas las transiciones y apariciones terminan en el mismo fotograma; el aspecto no cambia" },
      "ultra": { "label": "Máximo rendimiento", "description": "Sin animaciones y sin sombras, reflejos ni desenfoque, para el menor coste de visualización" }
    },
    "resourceBarFrameRateTitle": "Fotogramas de las barras de recursos",
    "resourceBarFrameRateDescription": "Cuántos fotogramas por segundo dibujan las transiciones de las barras. Seguir la pantalla usa la frecuencia de actualización del monitor; Personalizado limita los fotogramas para reducir la carga. No tiene efecto con Sin animaciones o Máximo rendimiento.",
    "resourceBarFrameRateModeOptions": [
      {
        "id": "display",
        "label": "Seguir la pantalla",
        "description": "Dibujar un fotograma en cada actualización de la pantalla."
      },
      {
        "id": "custom",
        "label": "Personalizado",
        "description": "Dibujar como máximo los fotogramas por segundo elegidos (10–240)."
      }
    ],
    "resourceBarFrameRateCustomLabel": "Fotogramas/s (FPS)",
    "barColorTitle": "Colores de las barras",
    "barColorDescription": "Usa colores fijos por tipo de programa o un color distinto para cada programa.",
    "byteUnitTitle": "Unidades de capacidad",
    "byteUnitDescription": "1 GiB = 1024 MiB, 1 GB = 1000 MB. El cambio convierte también los valores numéricos, no solo las etiquetas.",
    "fontSmoothingTitle": "Suavizado del texto",
    "fontSmoothingDescription": "Equilibra automáticamente la claridad del texto y la carga de la interfaz, o fija un modo de renderizado.",
    "languageTitle": "Idioma de la interfaz",
    "languageDescription": "Elige el idioma de la interfaz de Resource Manager. Sistema usa el idioma de visualización de Windows.",
    "settingsLanguageTitle": "Idioma de la interfaz",
    "settingsLanguageDescription": "Cambia el idioma de toda la interfaz, incluida la bandeja y el título de ventana. Sistema usa el idioma de visualización de Windows.",
    "languageSelectLabel": "Idioma",
    "uiStyleOptions": {
      "standard": "Estándar",
      "classic": "Clásico",
      "soft": "Suave",
      "outline": "Contorno",
      "terminal": "Terminal"
    },
    "themeOptions": {
      "system": "Sistema",
      "light": "Claro",
      "dark": "Oscuro",
      "lowContrast": "Bajo contraste",
      "paper": "Papel",
      "graphite": "Grafito",
      "blueprint": "Plano",
      "phosphor": "Fósforo",
      "amber": "Ámbar",
      "ice": "Hielo"
    },
    "barColorOptions": {
      "type": {
        "label": "Fijos por tipo",
        "description": "Colores para software del sistema, adaptado y general; separadores entre segmentos adyacentes"
      },
      "distinct": {
        "label": "Colores distintos",
        "description": "Un color por programa, sin significado de tipo ni separadores"
      }
    },
    "byteUnitOptions": {
      "native": {
        "label": "Según el hardware",
        "description": "GiB para memoria, VRAM y cachés (potencias de dos); GB para discos, archivos y transferencias (como indican los fabricantes)"
      },
      "binary": {
        "label": "Siempre GiB",
        "description": "Dividir siempre entre 1024; unidades KiB / MiB / GiB"
      },
      "decimal": {
        "label": "Siempre GB",
        "description": "Dividir siempre entre 1000; unidades kB / MB / GB"
      }
    },
    "fontSmoothingOptions": {
      "auto": {
        "label": "Automático",
        "description": "Usar ClearType normalmente y reducir el suavizado en modo de máximo rendimiento"
      },
      "system": {
        "label": "ClearType del sistema",
        "description": "Renderizado subpíxel nativo de Windows para texto más nítido"
      },
      "grayscale": {
        "label": "Escala de grises",
        "description": "Suavizado en escala de grises, más suave y adecuado para algunas pantallas de alta resolución"
      },
      "disabled": {
        "label": "Desactivado",
        "description": "Desactivar el suavizado adicional y rasterizar los glifos directamente"
      }
    },
  },
  "systemIntegration": {
    "crashRecoveryTitle": "Reiniciar tras un fallo",
    "crashRecoveryDescription": "Reiniciar automáticamente el servicio en segundo plano si falla. Una parada normal no provoca un reinicio.",
    "crashRestartDelayTitle": "Espera antes del reinicio (segundos)",
    "crashRestartCountTitle": "Reinicios permitidos",
    "crashCounterResetDescription": "Días sin fallos antes de restablecer el contador: {days}. Al alcanzar el límite, inicie el servicio manualmente.",
    "crashCounterResetTitle": "Periodo de restablecimiento (días)",
    "crashRecoveryActualTitle": "Configuración actual",
    "crashRecoveryUnavailable": "La configuración de recuperación automática no está disponible.",
    "crashRecoveryApplied": "Se ha aplicado la configuración de recuperación automática.",
    "crashRecoverySkipped": "La recuperación automática no se aplica en este modo de ejecución.",
    "crashRecoveryFailed": "No se ha podido aplicar la configuración de recuperación automática.",
    "crashRecoveryMismatch": "La configuración actual difiere de sus ajustes.",
    "crashRecoveryNonCrashWarning": "La configuración actual también reinicia tras una parada normal con error.",
    "crashRecoveryWriteFailed": "No se ha podido guardar la configuración de recuperación automática.",
    "crashRecoveryReadFailed": "No se ha podido leer la configuración de recuperación automática.",
    "taskManagerTitle": "Sustituir el atajo del Administrador de tareas",
    "autoStartTitle": "Iniciar el servicio al arrancar",
    "autoStartDescription": "Iniciar automáticamente el servicio en segundo plano al arrancar Windows.",
    "loginStartTitle": "Abrir en la bandeja al iniciar sesión",
    "loginStartDescription": "Iniciar la interfaz cuando este usuario inicie sesión. El servicio se configura por separado.",
    "loginStartUnavailable": "No se pudo leer o guardar la configuración de inicio al iniciar sesión del usuario actual.",
    "loginStartLoading": "Leyendo la configuración de inicio al iniciar sesión…",
    "loginStartWriteFailed": "No se pudo guardar el inicio al iniciar sesión. El interruptor muestra el estado leído después.",
    "loginStartRetry": "Reintentar",
    "taskManagerDescription": "Al activarlo, cualquier forma de abrir el Administrador de tareas abre Resource Manager (se inicia si está cerrado o muestra su ventana principal): Ctrl+Mayús+Esc, el menú de la barra de tareas, «Administrador de tareas» en la pantalla de Ctrl+Alt+Supr y la ejecución de taskmgr.exe. La pantalla de Ctrl+Alt+Supr no cambia. La sustitución debe registrarse con permisos de administrador; sin registro, solo se sustituye Ctrl+Mayús+Esc y únicamente mientras Resource Manager está en ejecución. Al desactivarlo solo se elimina el valor del registro que escribió Resource Manager.",
    "forceTerminateTitle": "Atajo de cierre forzado",
    "forceTerminateDescription": "Cerrar la aplicación en primer plano y todas las que tengan ventanas visibles sin respuesta. Desactivado por defecto.",
    "forceTerminateWarning": "Este atajo termina los procesos inmediatamente y se pierde el trabajo sin guardar. Debe incluir Ctrl, Alt o Win y una tecla que no sea modificadora.",
    "hotkeyEmpty": "No hay teclas configuradas",
    "hotkeyAddKey": "Añadir tecla",
    "hotkeyKeyLabel": "Tecla",
    "hotkeyRemoveKey": "Quitar tecla",
    "hotkeyUnordered": "Pulsar a la vez",
    "hotkeyOrdered": "Tecla anterior primero",
    "publicServiceTitle": "Servicio local compartido",
    "publicServiceDescription": "Permitir que los programas locales usen las funciones compartidas de Resource Manager.",
    "publicFileIndexTitle": "Índice compartido de archivos de software",
    "publicFileIndexDescription": "Reutilizar el índice de Resource Manager sin volver a analizar al consultar.",
    "publicDatabaseServiceTitle": "Servicio de bases de datos SQLite",
    "publicDatabaseServiceDescription": "Proporcionar a los programas locales bases de datos aisladas, consultas parametrizadas y transacciones por lotes.",
    "publicAiModelCatalogTitle": "Catálogo unificado de modelos de IA",
    "publicAiModelCatalogDescription": "Resource Manager gestiona el catálogo y la planificación; LM Studio ejecuta los modelos.",
    "lmStudioEndpointTitle": "Punto de conexión de LM Studio",
    "lmStudioEndpointDescription": "Solo se aceptan direcciones de este equipo. Otras aplicaciones se conectan mediante Resource Manager.",
    "lmStudioAutoStartTitle": "Iniciar LM Studio bajo demanda",
    "lmStudioAutoStartDescription": "Iniciar LM Studio si un programa solicita un modelo y no está abierto; los modelos nunca se cargan automáticamente.",
    "aiGatewayTitle": "Claves de compatibilidad de IA local",
    "aiGatewayDescription": "Generar claves compatibles con OpenAI o Anthropic para programas sin soporte directo de LM Studio. Las solicitudes solo van a modelos abiertos de este PC.",
    "aiGatewayOpenAiProfile": "Compatible con OpenAI",
    "aiGatewayAnthropicProfile": "Compatible con Anthropic",
    "aiGatewayNamePlaceholder": "Nombre del propósito (opcional)",
    "aiGatewayGenerate": "Generar clave",
    "aiGatewayGenerating": "Procesando",
    "aiGatewayOneTimeTitle": "Clave generada",
    "aiGatewayOneTimeDescription": "La clave se muestra una sola vez. Guárdala ahora porque no podrás volver a verla.",
    "aiGatewayApiKeyLabel": "Clave API",
    "aiGatewayBaseUrlLabel": "URL base",
    "aiGatewayCopy": "Copiar",
    "aiGatewayRevoke": "Revocar clave",
    "aiGatewayEmpty": "No se han generado claves de compatibilidad.",
    "aiGatewayLoadFailed": "No se pueden cargar las claves de IA local",
    "trustedAccess": {
      "title": "Claves de acceso de confianza",
      "description": "Administra claves de acceso generales y solicitudes de autorización de un solo uso.",
      "loading": "Cargando claves de acceso…",
      "loadFailed": "No se pueden cargar las claves de acceso de confianza.",
      "actionFailed": "La operación de acceso de confianza ha fallado.",
      "retry": "Reintentar",
      "failureReasons": {
        "unauthorized": "El servicio local no acepta la clave de esta interfaz: puede que la clave predeterminada esté desactivada, se acabe de rotar o no sea válida. Puedes reintentar; si la clave predeterminada está desactivada, vuelve a activarla como administrador.",
        "scope-required": "La clave actual no tiene el ámbito «Administrar claves de acceso», así que esta acción no está permitida.",
        "default-scope-denied": "Para cambiar la clave predeterminada se necesita una clave con todos los ámbitos predeterminados o permisos de administrador."
      },
      "defaultTitle": "Archivo de clave predeterminada nativa",
      "defaultDescription": "La clave predeterminada se guarda en el archivo indicado abajo para los programas que necesitan acceder al servicio local. La interfaz de Resource Manager se ejecuta como administrador y no usa la clave predeterminada.",
      "defaultEnabled": "Activada",
      "defaultDisabled": "Desactivada",
      "defaultKeyName": "Clave predeterminada",
      "defaultFilePath": "Ubicación del archivo",
      "defaultFileLocation": "<carpeta de instalación>\\UserData\\Security\\default-key",
      "defaultSecretLocalOnly": "La clave en sí no se muestra aquí.",
      "defaultAccessLossWarning": "Al desactivar la clave predeterminada se revoca de inmediato, y los programas que la usan pierden el acceso. Al rotarla, la clave anterior deja de ser válida; los programas que la usan deben volver a leer el archivo de la clave predeterminada.",
      "defaultDisabledRecovery": "La clave predeterminada está desactivada. Las solicitudes de los programas que la usan se rechazan hasta que se vuelva a activar.",
      "enableDefault": "Volver a activar la clave predeterminada",
      "disableDefault": "Desactivar y revocar la clave predeterminada",
      "regenerateDefault": "Regenerar clave predeterminada",
      "confirmDisableTitle": "¿Desactivar y revocar la clave predeterminada?",
      "confirmDisableMessage": "Al desactivarla, la clave predeterminada se revoca de inmediato. Los programas que la usan pierden el acceso hasta que se vuelva a activar la clave predeterminada.",
      "confirmRegenerateTitle": "¿Regenerar la clave predeterminada?",
      "confirmRegenerateMessage": "La clave predeterminada anterior deja de funcionar de inmediato. Los programas que la usan deben volver a leer el archivo de la clave predeterminada; hasta entonces, sus solicitudes se rechazan.",
      "confirmRotateTitle": (name: string) => `¿Rotar «${name}»?`,
      "confirmRotateMessage": "La clave anterior deja de funcionar de inmediato y los programas que la usan se rechazan hasta que cambien a la nueva clave. La nueva clave solo se muestra una vez.",
      "confirmRevokeTitle": (name: string) => `¿Revocar «${name}»?`,
      "confirmRevokeMessage": "La clave deja de funcionar de inmediato y los programas que la usan se rechazan. No se puede deshacer.",
      "createTitle": "Crear clave independiente",
      "createDescription": "Elige sus ámbitos. La clave nueva se muestra una sola vez y puede rotarse o revocarse individualmente.",
      "nameLabel": "Nombre",
      "namePlaceholder": "Por ejemplo, script local",
      "createScopesLabel": "Ámbitos concedidos",
      "noScopesAvailable": "No hay ámbitos disponibles para crear claves.",
      "create": "Generar clave independiente",
      "working": "Procesando…",
      "oneTimeTitle": "Clave independiente generada",
      "oneTimeDescription": "Cópiala y guárdala ahora. No se podrá volver a mostrar.",
      "oneTimeAnnouncement": "Clave generada. Cópiala y guárdala ahora.",
      "apiKeyLabel": "Clave",
      "copy": "Copiar",
      "copied": "Copiada",
      "copyFailed": "No se puede copiar la clave.",
      "dismissSecret": "Ocultar clave de un solo uso",
      "createdAt": "Creada",
      "keysTitle": "Claves independientes",
      "keysEmpty": "No se han creado claves independientes.",
      "revoke": "Revocar",
      "rotate": "Rotar",
      "rotateKeyLabel": (name: string) => `Rotar «${name}»`,
      "revokeKeyLabel": (name: string) => `Revocar «${name}»`,
      "requestsTitle": "Solicitudes de autorización de un solo uso",
      "requestsDescription": "Las solicitudes de programas de terceros muestran el nombre elegido y los ámbitos solicitados.",
      "reviewRequests": "Revisar solicitudes",
      "requestsLoading": "Esperando solicitudes de autorización…",
      "requestsUnavailable": "La suscripción a solicitudes de autorización no está disponible; por ahora no pueden llegar solicitudes nuevas.",
      "requestsLoadFailed": "No se pueden leer las solicitudes de autorización pendientes.",
      "requestsStale": "La lista de solicitudes puede estar desactualizada mientras la suscripción se reconecta.",
      "requestsEmpty": "No hay solicitudes pendientes.",
      "requestNamed": (name: string) => `Nombre de la solicitud: ${name}`,
      "requestCount": (count: number) => `Solicitudes pendientes: ${count}`,
      "expiresAt": "Caduca",
      "observedExecutable": "Ruta del ejecutable observada",
      "observedProcess": "PID",
      "submittedAt": "Enviada",
      "observedCallerIsInformational": "La ruta y el PID observados son informativos y no identifican a quién se autoriza.",
      "requestScopesLabel": "Ámbitos solicitados",
      "requestUnavailableScopeHelp": "No se pueden seleccionar ámbitos fuera de tus permisos actuales. Solo se enviarán ámbitos solicitados que puedas conceder.",
      "administratorHolderScope": "Los programas que usen esta clave deben ejecutarse como administrador",
      "approve": "Aprobar ámbitos seleccionados",
      "deny": "Denegar",
      "denyReasonLabel": "Motivo de denegación",
      "requestActionFailed": "No se ha podido enviar la decisión de autorización.",
      "requestApproved": "Aprobada. La clave se ha entregado al programa solicitante.",
      "requestDenied": "Denegada.",
      "requestDeniedWithReason": (reason: string) => `Denegada (motivo: ${reason}).`,
      "requestExpired": "La solicitud ha caducado.",
      "requestOutcome": (name: string, result: string) => `${name}: ${result}`,
      "denialReasons": {
        "not-approved": "No aprobada",
        "unsupported": "No compatible",
        "duplicate": "Solicitud duplicada",
        "not-needed": "No necesaria"
      },
      "scopeLabels": {
        "read": "Leer la API",
        "metrics.read": "Leer métricas",
        "processes.read": "Leer procesos",
        "session": "Acciones de sesión",
        "adapters.register.self": "Registrar adaptadores de este usuario",
        "desktop.publish": "Publicar observaciones del escritorio",
        "gpu.resolve": "Resolver la ubicación de GPU",
        "gpu.report": "Informar del resultado de inicio de GPU",
        "keys.manage": "Administrar claves de acceso",
        "machine.write": "Modificar ajustes del equipo",
        "injection.configure": "Configurar la inyección al inicio"
      },
      "unknownScope": "Ámbito desconocido (esta versión no puede describirlo, así que no se puede conceder aquí)"
    }
  },
  "debug": {
    "debugModeTitle": "Modo de depuración",
    "debugModeDescription": "Mostrar y activar opciones de diagnóstico local. Actívalo solo al investigar un problema.",
    "debugLogTitle": "Guardar registros de depuración",
    "debugLogDescription": "Registrar información para investigar problemas. Puede usar un poco de disco y tiempo de procesador.",
    "hostManagerSmartCoordinatorScoreOnlyTitle": "Analizar sin aplicar cambios",
    "hostManagerSmartCoordinatorScoreOnlyDescription": "Continuar la supervisión y generar resultados de planificación sin modificar el estado del software ni del hardware.",
    "hostManagerSmartCoordinatorPerformanceLogTitle": "Registrar rendimiento de planificación",
    "hostManagerSmartCoordinatorPerformanceLogDescription": "Registrar la duración y la carga de planificación para investigar el consumo de Resource Manager."
  },
  "hardwareControl": {
    "keepAliveTitle": "Mantener los ajustes aplicados",
    "keepAliveDescription": "Si está desactivado, los ajustes guardados en la página Control se aplican una sola vez, al iniciar Resource Manager. Si está activado, se vuelven a aplicar con el intervalo indicado abajo, para que Windows, los controladores u otros programas no los dejen cambiados.",
    "reassertionTitle": "Intervalo de reaplicación",
    "reassertionDescription": "Mientras «Mantener los ajustes aplicados» esté activado, los ajustes se vuelven a aplicar tras cada intervalo."
  },
  "performanceOverlay": {
    "intro": "Enumera el software que tiene una superposición activada. Se muestran los ajustes guardados, no si la superposición se está mostrando ahora.",
    "loading": "Leyendo la configuración de superposición",
    "loadFailed": "No se pudo leer la configuración de superposición",
    "emptyTitle": "Ningún software tiene una superposición activada",
    "emptyDetail": (tab: string) => `Para activarla, abra los detalles del software y vaya a la pestaña «${tab}».`,
    "itemCount": (count: number) => plural("es-ES", count, { one: "{count} elemento", many: "{count} elementos", other: "{count} elementos" }),
    "position": (anchor: string) => `Posición: ${anchor}`,
    "open": "Abrir configuración de superposición",
    "openLabel": (name: string) => `Abrir la configuración de superposición de ${name}`,
    "softwareMissing": "Este software ya no figura en la lista de software, por lo que no se pueden abrir sus detalles."
  },
  "credits": {
    "heroTitle": "Gracias a cada persona y proyecto que hace posible Resource Manager",
    "heroBody": "Gracias a Microsoft, a quienes desarrollan Windows, .NET y WebView2 y a toda la comunidad de desarrollo. Sus años de trabajo aportan bases que una sola persona no podría construir. Cada contribución importa, aunque no se exija atribución. Resource Manager se desarrolla de forma independiente; estos agradecimientos no implican firma, certificación ni respaldo de los proyectos citados.",
    "dependencyListTitle": "Agradecimientos completos a las dependencias",
    "dependencyListBody": "Gracias a todos los autores y mantenedores siguientes. La lista incluye el archivo de bloqueo del frontend y los paquetes .NET resueltos, también de compilación, pruebas y opcionales. No implica que todos se carguen o distribuyan en este equipo.",
    "groups": {
      "project": "Proyecto y contribución",
      "runtime": "Runtime y herramientas de compilación",
      "windows": "Interfaces de Windows y diagnóstico",
      "data": "Datos sin conexión",
      "build": "Herramientas de compilación",
      "hardware": "Supervisión de hardware y soporte opcional"
    },
    "roles": {
      "project": "Proyecto",
      "frontendFramework": "Framework de la interfaz",
      "icons": "Iconos de la interfaz",
      "serialization": "Dependencias de serialización de Solid",
      "charts": "Gráficos de resultados",
      "webView": "Contenedor web de escritorio",
      "database": "Base de datos local",
      "nativeCompiler": "Compilador del núcleo nativo",
      "softwareCatalog": "Fuentes de metadatos de software",
      "buildToolchain": "Herramientas de compilación del frontend",
      "typeSystem": "Sistema de tipos del frontend",
      "buildRuntime": "Entorno de compilación y definiciones de tipos",
      "dotnetRuntime": "Entorno local de ejecución",
      "hookLibrary": "Soporte de compatibilidad con Windows",
      "startupInjectionLibrary": "Compatibilidad al iniciar programas",
      "traceLibrary": "Análisis del rendimiento del sistema",
      "windowsManagement": "Información del sistema Windows",
      "nvidiaTelemetry": "Telemetría de GPU NVIDIA",
      "nvidiaExtension": "Interfaz de extensión de GPU NVIDIA",
      "amdTelemetry": "Telemetría de GPU/iGPU AMD",
      "signedHardwareAccess": "Acceso al hardware firmado",
      "hardwareBridge": "Puente de sensores de hardware",
      "externalReference": "Referencia externa / ayuda opcional",
      "deviceIdDatabase": "Base de nombres de ID de hardware"
    },
    "linkLabels": {
      "official": "Sitio oficial",
      "github": "GitHub",
      "docs": "Documentación",
      "nuget": "NuGet",
      "npm": "npm",
      "license": "Licencia"
    },
    "sourceLabel": "Fuente",
    "dependencyColumns": {
      "name": "Nombre",
      "version": "Versión",
      "license": "Licencia",
      "source": "Origen"
    }
  }
});
