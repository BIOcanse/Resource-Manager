import { createSettingsLocale } from "../settingsLocaleFactory.ts";

export default createSettingsLocale("es-ES", {
  "updates": {
    "autoUpdateTitle": "Actualizaciones automáticas",
    "autoUpdateDescription": "Busca, descarga y verifica actualizaciones periódicamente. Se aplican automáticamente al cerrar las ventanas de Resource Manager en todas las sesiones de escritorio, o mediante Aplicar ahora.",
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
    "performance": "Rendimiento",
    "appearance": "Apariencia",
    "systemIntegration": "Integración del sistema",
    "updates": "Actualizaciones y versiones",
    "debug": "Depuración",
    "credits": "Créditos"
  },
  "saveState": {
    "saving": "Guardando",
    "saved": "Guardado",
    "constrained": "Guardado; se limitaron las opciones de ejecución no disponibles",
    "partial": "Guardado, pero no aplicado por completo",
    "error": "Error al guardar",
    "dirty": "Sin guardar",
    "conflict": "Los ajustes cambiaron en otro lugar; recarga antes de guardar"
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
    "reapply": "Aplicar de nuevo"
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
    "refreshCadenceTitle": "Frecuencia de actualización",
    "refreshCadenceDescription": "Controla la frecuencia de actualización de las mediciones y los estados. Una frecuencia mayor usa algo más de recursos.",
    "presetNumericModeOptions": [
      {
        "id": "aotu",
        "label": "Automático",
        "description": "Elegir la frecuencia según el modo de rendimiento y el estado de la ventana."
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
    "animationTitle": "Animaciones",
    "animationDescription": "Elige animaciones completas, ninguna animación o efectos simplificados para reducir la carga de la interfaz.",
    "resourceBarFrameRateTitle": "Fotogramas de las barras de recursos",
    "resourceBarFrameRateDescription": "Cuántos fotogramas por segundo dibujan las transiciones de las barras. Seguir la pantalla usa la frecuencia de actualización del monitor; Personalizado limita los fotogramas para reducir la carga. Solo se aplica con animaciones normales.",
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
    "readingSourceTitle": "Fuentes de las lecturas",
    "readingSourceDescription": "Cómo se muestran las lecturas con más de una fuente en la consola de supervisión y en los detalles del dispositivo. De forma predeterminada solo se muestra la fuente más precisa en este equipo; al mostrar todas las fuentes disponibles, la más precisa se marca como «Preferida». La planificación siempre usa la fuente más precisa, sin importar este ajuste.",
    "readingSourceModeOptions": [
      {
        "id": "preferred",
        "label": "Solo la más precisa",
        "description": "Mostrar solo la fuente más precisa de cada lectura en este equipo."
      },
      {
        "id": "all",
        "label": "Todas las disponibles",
        "description": "Mostrar todas las fuentes disponibles de cada lectura y marcar la más precisa."
      }
    ],
    "fontSmoothingTitle": "Suavizado del texto",
    "fontSmoothingDescription": "Equilibra automáticamente la claridad del texto y la carga de la interfaz, o fija un modo de renderizado.",
    "languageTitle": "Idioma de la interfaz",
    "languageDescription": "Elige el idioma de la interfaz de Resource Manager. Sistema usa el idioma de visualización de Windows.",
    "settingsLanguageTitle": "Idioma de la interfaz",
    "settingsLanguageDescription": "Cambia el idioma de toda la interfaz, incluida la bandeja y el título de ventana. Sistema usa el idioma de visualización de Windows.",
    "languageSelectLabel": "Idioma",
    "uiStyleOptions": {
      "standard": "Estándar",
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
    "animationOptions": {
      "auto": {
        "label": "Automático",
        "description": "Usa animaciones normales en primer plano y el aspecto de máximo rendimiento en segundo plano de bajo consumo"
      },
      "normal": {
        "label": "Normal motion",
        "description": "Enable restrained global transitions"
      },
      "none": {
        "label": "No motion",
        "description": "Desactiva transiciones y animaciones y conserva el aspecto cuidado"
      },
      "ultra": {
        "label": "Ultra performance",
        "description": "Desactiva animaciones, sombras, reflejos y desenfoque para minimizar el coste de visualización"
      }
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
    "autoStartTitle": "Iniciar con Windows",
    "autoStartDescription": "Ejecutar el servicio al arrancar y permanecer en la bandeja tras iniciar sesión.",
    "taskManagerDescription": "Sustituir Ctrl+Mayús+Esc: tras registrarlo, Resource Manager se inicia si está cerrado o muestra su ventana principal en primer plano. Ctrl+Alt+Supr no cambia.",
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
    "aiGatewayLoadFailed": "No se pueden cargar las claves de IA local"
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
    "notes": {
      "windowsFoundation": "Gracias a Microsoft y a los equipos de Windows por Win32, Shell, COM, Direct3D/DXGI, PDH, ETW, servicios, criptografía y redes proporcionados por el sistema.",
      "microsoftTools": "Gracias a los autores y mantenedores de .NET SDK, MSBuild, NuGet y PowerShell por la compilación, publicación, gestión de paquetes y automatización.",
      "managedServices": "Gracias a Microsoft y a los colaboradores de .NET por las API básicas y la integración de servicios. TypeExtensions es un marcador resuelto para este destino, no una DLL adicional.",
      "nativeToolchain": "Gracias a los autores y mantenedores de GCC y MinGW por la compilación y las bases de ejecución nativa. Algunos componentes enlazan libgcc/libstdc++ estáticamente; la redistribución se revisa por separado.",
      "buildContributors": "Gracias a los autores y mantenedores de todas las dependencias de compilación, incluidos los instrumentos de Solid, mapas de código, datos de navegadores y utilidades. La lista conserva todos los paquetes y versiones fijados, también de plataformas opcionales.",
      "testContributors": "Gracias a los colaboradores de Playwright, xUnit.net, Coverlet y la plataforma de pruebas de Microsoft por la automatización, las regresiones y la cobertura. Son herramientas de desarrollo, no dependencias de ejecución del producto.",
      "upstreamContributors": "Gracias a todos los autores de los avisos originales de .NET y WebView2, incluidos ANTLR, Unicode, Mono, Brotli y LLVM. Se conservan íntegros; no son un inventario de las DLL en ejecución.",
      "resourceManager": "Copyright (c) 2026 BIOcanse. Este proyecto usa Apache-2.0. Los componentes de terceros mantienen sus propias licencias.",
      "solid": "Proporciona el modelo de componentes reactivos de la interfaz web ligera actual.",
      "lucide": "La interfaz usa lucide-solid y conserva la licencia ISC de Lucide y la licencia MIT y atribuciones de los iconos derivados de Feather.",
      "seroval": "Dependencias transitivas de Solid; se conservan la licencia MIT y la atribución a Alexis Munsayac.",
      "webView2": "El contenedor de escritorio usa el SDK de WebView2. Microsoft distribuye por separado el entorno WebView2 Evergreen bajo sus condiciones.",
      "sqlite": "Almacenamiento local. Microsoft.Data.Sqlite usa MIT y SQLitePCLRaw 3.x Apache-2.0. El proyecto original dedica al dominio público el núcleo SQLite distribuido por SourceGear.",
      "zig": "Compila el núcleo nativo; los usuarios no necesitan el compilador para ejecutar la aplicación.",
      "softwareCatalog": "El catálogo sin conexión usa metadatos WinGet con MIT y datos estructurados Wikidata con CC0. El proyecto redacta los resúmenes y traducciones chinas.",
      "vite": "Gestiona las compilaciones de desarrollo y el empaquetado del frontend Solid.",
      "typescript": "Mejora la fiabilidad del código de la interfaz y de los modelos de ajustes.",
      "node": "Permite las compilaciones del frontend y las herramientas de desarrollo.",
      "dotnet": "Proporciona el entorno local de ejecución de Resource Manager.",
      "minHook": "Gracias a Tsuda Kageyu, a los colaboradores y al autor de HDE Vyacheslav Patkov por los hooks y la decodificación. Se conservan íntegros las licencias y las atribuciones originales.",
      "detours": "Proyecto de código abierto de Microsoft Research para la compatibilidad al iniciar software. Se conservan la licencia y el aviso de copyright originales.",
      "traceEvent": "Proporciona acceso a información de rendimiento de Windows.",
      "systemManagement": "Proporciona acceso a información del sistema y hardware de Windows.",
      "nvml": "Proporciona frecuencia, memoria, potencia y temperatura de GPU NVIDIA.",
      "nvapi": "Añade información de ventiladores, refrigeración y alimentación de GPU NVIDIA.",
      "adlx": "Proporciona uso, frecuencia, potencia, temperatura y voltaje de GPU AMD.",
      "pawnIo": "Controlador con firma oficial compartido por las lecturas SMU de AMD, el control de procesadores Intel y los sensores de placa base y memoria; nunca se instala en silencio.",
      "pawnIoModules": "Módulos con firma oficial (LGPL-2.1). RyzenSMU e IntelMSR se descargan al verificar el componente; los módulos de acceso LPC, EC y SMBus van integrados en LibreHardwareMonitorLib.",
      "libreHardwareMonitor": "Biblioteca principal oficial 0.9.6 incluida (MPL-2.0), solo con los grupos de placa base y memoria; lee mediante PawnIO temperaturas, voltajes y ventiladores de la placa base y la temperatura de la memoria. No inicia interfaz gráfica ni servidor web.",
      "blacktempelToolkits": "Bibliotecas de información de discos, lectura SPD de memoria y base que usa LibreHardwareMonitorLib (1.1.2, 1.4.2, 1.0.7), de Florian K., bajo MPL-2.0.",
      "hidSharp": "Biblioteca de acceso a dispositivos USB HID 2.6.4 que usa LibreHardwareMonitorLib, de James F. Bellinger, bajo Apache-2.0.",
      "monoPosix": "Interfaz POSIX de Mono 1.0.0 que usa LibreHardwareMonitorLib, publicada por Microsoft bajo la licencia MIT de las bibliotecas de clases de Mono.",
      "usbIds": "Proporciona sin conexión nombres de fabricantes y dispositivos USB VID/PID; solo se carga al solicitar detalles.",
      "pciIds": "Proporciona sin conexión nombres de fabricantes, dispositivos y subsistemas PCI VEN/DEV/SUBSYS; solo se carga al solicitar detalles."
    },
    "linkLabels": {
      "official": "Website",
      "github": "GitHub",
      "docs": "Docs",
      "license": "Licencia",
      "nuget": "NuGet",
      "gpuOpen": "GPUOpen",
      "eula": "EULA",
      "runtime": "Entorno de ejecución",
      "aspnet": "ASP.NET Core",
      "vite": "Vite",
      "solidPlugin": "Solid plugin"
    }
  }
});
