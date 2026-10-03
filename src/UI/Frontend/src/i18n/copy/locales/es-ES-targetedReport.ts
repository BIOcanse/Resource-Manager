import type { AppCopy } from "../index.ts";
export const targetedReport: AppCopy["targetedReport"] = {
  tab: "Informes dirigidos", title: "Informes de supervisión dirigida",
  intro: "Registra los tiempos de fotogramas Present y los recursos de todos los procesos de este programa.",
  maximumSeconds: "Duración máxima de la grabación (segundos)", start: "Iniciar grabación", stop: "Detener grabación",
  recording: "Grabando", completed: "Finalizado", refresh: "Actualizar informes",
  empty: "Todavía no hay grabaciones.", loading: "Cargando informe…",
  failed: "Error en la operación del informe.", delete: "Eliminar informe",
  confirmDelete: "¿Eliminar este informe de grabación?", exportJson: "Exportar JSON", exportCsv: "Exportar CSV",
  frameTime: "Tiempo de fotograma a lo largo del tiempo", distribution: "Distribución de FPS", resources: "Curvas de recursos",
  noFrames: "No hay intervalos de fotogramas disponibles.", applicationPresent: "Fotogramas Present de la aplicación",
  incomplete: "La captura tiene lagunas; las estadísticas pueden ser inferiores a las reales.",
  averageFps: "FPS medios", onePercentLow: "1 % Low", pointOnePercentLow: "0,1 % Low",
  p50: "Tiempo de fotograma P50", p95: "Tiempo de fotograma P95", p99: "Tiempo de fotograma P99",
  maxFrameTime: "Tiempo máximo de fotograma", frameCount: "Intervalos de fotograma",
  software: "Programa objetivo", system: "Sistema", seconds: "segundos",
  stopReasons: { user: "Detenido manualmente", processExit: "Procesos finalizados", maxDuration: "Duración máxima alcanzada",
    interrupted: "Servicio interrumpido", captureError: "Error de captura" }
};
