import { plural } from "../../plural.ts";
import type { AppCopy } from "../index.ts";

export const kernelLeakReport: AppCopy["kernelLeakReport"] = {
  tab: "Rapport sur les fuites du noyau",
  intro: "Répertorie les processus dont les ressources du noyau (actuellement les handles de processus) augmentent de façon continue et pourraient croître sans limite, avec les éléments probants. Le résultat indique seulement une suspicion. Décidez d’intervenir ou non au vu de ces éléments. L’observation n’a lieu que pendant l’affichage de cette page et reprend depuis le début lorsque la page est rouverte.",
  loading: "Chargement du rapport sur les fuites du noyau…",
  readFailed: "Impossible de lire le rapport sur les fuites du noyau.",
  capturedAt: "Capturé le",
  suspectedItems: "Éléments suspects",
  emptyTitle: "Aucune fuite du noyau suspectée",
  emptyDetail: "Ce résultat s’affiche également tant que la durée d’observation ne suffit pas encore pour conclure. Les éléments suspects apparaîtront ici dès qu’ils seront détectés.",
  notObservedTitle: "Aucun échantillon n’a encore été prélevé",
  unavailableTitle: "Le rapport sur les fuites du noyau n’est pas disponible",
  failure: {
    NotObserved: "L’observation a commencé. Les résultats s’afficheront après le premier échantillon.",
    SourceUnavailable: "Les données de ressources des processus ne peuvent pas être lues.",
    CapacityExceeded: "Le nombre de processus observés dépasse la limite de capacité. Cette observation a été arrêtée.",
    InvalidObservation: "Les données échantillonnées ne sont pas valides. Cette observation a été arrêtée."
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
    currentValue: "Nombre actuel",
    growth: "Augmentation pendant l’observation",
    growthPerSecond: "Taux d’augmentation",
    observationTime: "Durée d’observation",
    sampleCount: "Échantillons"
  },
  ratePerSecond: (value: string) => `${value}/s`,
  actionsPending: "Le service local ne fournit pas encore d’actions pour cet élément."
};
