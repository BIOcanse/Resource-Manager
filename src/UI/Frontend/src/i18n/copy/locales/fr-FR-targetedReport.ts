import type { AppCopy } from "../index.ts";
export const targetedReport: AppCopy["targetedReport"] = {
  tab: "Rapports de surveillance ciblée", title: "Rapports de surveillance ciblée",
  chooseSoftware: "Choisir le logiciel à enregistrer",
  noSoftware: "Aucun logiciel enregistré. Ajoutez la cible dans la gestion des logiciels.",
  oneAtATime: "Un seul enregistrement est possible à la fois. Arrêtez d'abord celui en cours.",
  workflow: "Choisissez un logiciel enregistré et en cours d'exécution, ou démarrez depuis sa vue d'ensemble. La durée est de 600 secondes par défaut, réglable de 1 à 3600. Un arrêt manuel, la fin des processus ou la limite de temps termine le rapport. Les 50 derniers enregistrements terminés sont conservés.",
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
