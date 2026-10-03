import type { AppCopy } from "../index.ts";
export const targetedReport: AppCopy["targetedReport"] = {
  tab: "Rapports ciblés", title: "Rapports de surveillance ciblée",
  intro: "Enregistre les temps des trames Present et les ressources de tous les processus de ce logiciel.",
  maximumSeconds: "Durée maximale d’enregistrement (secondes)", start: "Démarrer l’enregistrement", stop: "Arrêter l’enregistrement",
  recording: "Enregistrement", completed: "Terminé", refresh: "Actualiser les rapports",
  empty: "Aucun enregistrement pour le moment.", loading: "Chargement du rapport…",
  failed: "Échec de l’opération sur le rapport.", delete: "Supprimer le rapport",
  confirmDelete: "Supprimer ce rapport d’enregistrement ?", exportJson: "Exporter en JSON", exportCsv: "Exporter en CSV",
  frameTime: "Temps de trame au fil du temps", distribution: "Répartition des FPS", resources: "Courbes des ressources",
  noFrames: "Aucun intervalle de trame disponible.", applicationPresent: "Trames Present de l’application",
  incomplete: "La capture comporte des lacunes ; les statistiques peuvent être sous-estimées.",
  averageFps: "FPS moyens", onePercentLow: "1 % Low", pointOnePercentLow: "0,1 % Low",
  p50: "Temps de trame P50", p95: "Temps de trame P95", p99: "Temps de trame P99",
  maxFrameTime: "Temps de trame maximal", frameCount: "Intervalles de trame",
  software: "Logiciel ciblé", system: "Système", seconds: "secondes",
  stopReasons: { user: "Arrêt manuel", processExit: "Processus terminés", maxDuration: "Durée maximale atteinte",
    interrupted: "Service interrompu", captureError: "Erreur de capture" }
};
