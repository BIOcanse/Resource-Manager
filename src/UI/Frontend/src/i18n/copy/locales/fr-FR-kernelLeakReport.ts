import { plural } from "../../plural.ts";
import type { AppCopy } from "../index.ts";

export const kernelLeakReport: AppCopy["kernelLeakReport"] = {
  tab: "Rapport sur les fuites du noyau",
  intro: "Pendant son exécution, le service local observe en continu les ressources du noyau détenues par chaque processus (actuellement les handles de processus) et répertorie les processus dont ces ressources augmentent de façon continue et pourraient croître sans limite, avec les éléments probants. Le résultat indique seulement une suspicion. Décidez d’intervenir ou non au vu de ces éléments.",
  loading: "Chargement du rapport sur les fuites du noyau…",
  readFailed: "Impossible de lire le rapport sur les fuites du noyau.",
  capturedAt: "Capturé le",
  suspectedItems: "Éléments suspects",
  historyItems: "Entrées historiques",
  unrecordedItems: "Non enregistrées (limite atteinte)",
  items: "Entrées du rapport",
  historyEntry: "Historique",
  notObservedTitle: "Les fuites du noyau ne sont pas observées",
  notObservedDetail: "L’observation des fuites du noyau n’est pas en cours : elle est peut-être désactivée, son plan d’observation n’est peut-être pas encore publié, ou elle s’est arrêtée. Tant qu’elle ne s’exécute pas, aucun échantillon n’est prélevé et aucun rapport n’est produit.",
  observingTitle: "Observation en cours",
  observingDetail: "L’évaluation commence lorsque la durée d’observation et le nombre d’échantillons de ce cycle atteignent tous deux les valeurs requises.",
  emptyTitle: "Aucune fuite du noyau suspectée",
  emptyDetail: "La durée d’observation et le nombre d’échantillons de ce cycle atteignent les valeurs requises, et aucune ressource du noyau n’augmente de façon continue.",
  unavailableTitle: "Ce cycle d’observation a échoué",
  failure: {
    SourceUnavailable: "Les données de ressources des processus n’ont pas pu être lues.",
    CapacityExceeded: "Le nombre de processus observés a dépassé la limite de capacité ; ce cycle a été arrêté.",
    InvalidObservation: "Les données d’échantillonnage n’étaient pas valides ; ce cycle a été arrêté."
  },
  observation: {
    title: "Cycle en cours",
    startedAt: "Début du cycle",
    elapsed: "Durée d’observation",
    samples: "Échantillons",
    notStarted: "Aucun échantillon pour l’instant",
    reachedOfRequired: (reached: string, required: string) => `${reached} (requis : ${required})`
  },
  previousRun: {
    title: "Le cycle précédent a échoué",
    reason: "Motif",
    startedAt: "Début du cycle",
    failedAt: "Échec le"
  },
  restartPolicy: {
    NextInterval: "Le service local démarre automatiquement un nouveau cycle à l’intervalle d’échantillonnage suivant."
  },
  unavailableHolders: (count: number) => plural("fr-FR", count, {
    one: "Les données de ressources de {count} processus n’ont pas pu être lues. Il n’est pas pris en compte dans cette évaluation.",
    many: "Les données de ressources de {count} processus n’ont pas pu être lues. Ils ne sont pas pris en compte dans cette évaluation.",
    other: "Les données de ressources de {count} processus n’ont pas pu être lues. Ils ne sont pas pris en compte dans cette évaluation."
  }),
  resourceKind: {
    ProcessHandles: "Handles de processus"
  },
  holder: {
    processId: "PID",
    productName: "Nom du produit",
    services: "Services",
    imagePath: "Chemin de l’image",
    noProductName: "Non fourni",
    noServices: "Aucun service associé",
    unavailable: "Illisible",
    processEnded: "Processus terminé"
  },
  evidence: {
    currentValue: "Valeur actuelle",
    growth: "Augmentation pendant l’observation",
    growthPerSecond: "Taux d’augmentation",
    observationTime: "Durée d’observation",
    sampleCount: "Échantillons",
    firstObserved: "Première détection",
    lastObserved: "Dernière détection"
  },
  ratePerSecond: (value: string) => `${value}/s`,
  details: {
    button: "Détails",
    back: "Retour à la liste du rapport",
    title: (name: string) => `Détails de ${name}`,
    holderSection: "Processus détenteur",
    evidenceSection: "Éléments probants",
    state: "État",
    active: "Actif",
    resourceKind: "Type de ressource",
    processName: "Nom du processus",
    startKey: "Clé de démarrage du processus",
    unavailableReason: "Motif d'indisponibilité"
  },
  actions: {
    title: "Actions",
    name: {
      RestartHolder: "Redémarrer le processus détenteur",
      ForceReleaseHandles: "Forcer la libération des handles"
    },
    riskLevel: {
      Warning: "Avertissement",
      High: "Élevé"
    },
    riskText: (level: string, risk: string) => `Risque : ${level}. ${risk}`,
    riskLabel: (name: string) => `Risque : ${name}`
  }
};
