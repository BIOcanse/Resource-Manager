import { plural } from "../../plural.ts";
import type { AppCopy } from "../index.ts";

export const smartFailure: AppCopy["smartFailure"] = {
  title: {
    "cycle-failed": "Ce segment de planification a échoué",
    "native-plan-failed": "Le plan de planification natif a échoué"
  },
  hint: {
    "cycle-failed": "Une erreur s’est produite pendant l’exécution de ce segment de planification, qui n’a pas abouti. Cet avis disparaît dès qu’un segment ultérieur se termine correctement. Le détail de l’erreur est consigné dans le journal du service local.",
    "native-plan-failed": "Le composant de planification natif n’a pas pu établir de plan pour ce segment ; aucun ajustement n’y a donc été effectué. L’état du plan indique la cause. Cet avis disparaît dès qu’un segment ultérieur se termine correctement."
  },
  nativePlanStatus: (status: string) => `État du plan : ${status}`,
  nativePlanStatuses: {
    "invalid-argument": "Argument non valide",
    "abi-mismatch": "Version d’interface du composant natif incompatible",
    unavailable: "Composant natif indisponible",
    "no-data": "Aucune donnée disponible",
    "buffer-too-small": "Capacité de la mémoire tampon insuffisante",
    "stale-generation": "Les données d’entrée sont obsolètes",
    "out-of-memory": "Mémoire insuffisante",
    "capacity-exceeded": "Limite de capacité dépassée",
    "conflicting-facts": "Les données d’entrée sont contradictoires",
    "feedback-mismatch": "Le retour d’exécution ne correspond pas au plan",
    "recreate-required": "L’espace de travail natif doit être recréé"
  },
  exclusionTotal: (count: number) => plural("fr-FR", count, {
    one: "{count} donnée a été exclue de ce segment",
    many: "{count} de données ont été exclues de ce segment",
    other: "{count} données ont été exclues de ce segment"
  }),
  exclusionsHint: "Les données exclues sont celles qui n’ont pas été prises en compte dans les décisions de planification de ce segment, parce qu’elles manquaient, n’étaient pas valides ou étaient incohérentes. Le récapitulatif compte les exclusions par donnée et par cause ; le détail indique les objets concernés.",
  summaryLabel: "Récapitulatif des exclusions",
  viewExclusions: "Détail des exclusions",
  detailTitle: "Données exclues de ce segment",
  detailSummary: (listed: number, total: number) => `Total des exclusions : ${total} ; listées ci-dessous : ${listed}.`,
  field: {
    processId: "ID du processus",
    processStartKey: "Identifiant de démarrage du processus",
    targetId: "Identifiant de l’objet de planification",
    target: "Objet de planification"
  },
  noTarget: "Ne concerne pas un objet précis",
  facts: {
    inventory: "Inventaire des processus",
    identity: "Identité du processus",
    "base-score": "Score de base",
    attribution: "Attribution logicielle",
    "runtime-state": "État d’exécution de l’objet de planification",
    "cpu-core-residency": "Résidence sur les cœurs CPU",
    "cpu-score": "Score CPU",
    "gpu-score": "Score GPU",
    "compute-score": "Score de calcul",
    "memory-cleanup": "Nettoyage de la mémoire",
    "decision-target": "Objet de décision",
    "native-plan": "Plan de planification natif",
    CpuUsage: "Utilisation CPU du processus",
    MemoryUsage: "Utilisation mémoire du processus",
    GpuUsage: "Utilisation GPU du processus",
    GpuDedicatedMemory: "Utilisation de la VRAM dédiée par le processus",
    RuntimeState: "État d’exécution du processus"
  },
  reasons: {
    "dataset-not-current": "Les données ne proviennent pas de l’échantillon actuel",
    "unidentified-live-process": "Un processus en cours d’exécution n’a pas pu être identifié",
    "inventory-overflow": "L’inventaire des processus a dépassé sa capacité",
    "emitted-count-mismatch": "Le nombre émis ne correspond pas à l’inventaire des processus",
    "inventory-count-mismatch": "Le nombre de l’inventaire ne correspond pas au nombre énuméré",
    "capture-count-mismatch": "Les compteurs de capture sont incohérents",
    "invalid-or-duplicate-inventory-identity": "L’inventaire des processus contient une identité de processus non valide ou en double",
    "invalid-or-duplicate-process": "Identité de processus non valide ou en double",
    "row-missing-or-invalid": "Données manquantes ou non valides",
    "gpu-row-missing-or-invalid": "Données GPU manquantes ou non valides",
    "compute-runtime-state-missing": "État d’exécution manquant",
    "cpu-residency-not-covered": "L’échantillonnage de résidence CPU ne couvre pas ce processus",
    "cpu-residency-invalid": "Données de résidence CPU non valides",
    "target-process-count-not-one": "L’objet de planification ne correspond pas à un seul processus",
    "target-process-identity-missing": "L’objet de planification n’a pas d’identité de processus",
    "target-process-identity-mismatch": "L’identité de processus de l’objet de planification est incohérente",
    "duplicate-target-process-identity": "Plusieurs objets de planification désignent le même processus",
    "target-runtime-state-invalid": "L’état d’exécution de l’objet de planification n’est pas valide",
    "cleanup-baseline-score-missing": "Score de référence du nettoyage manquant",
    "cleanup-current-process-facts-missing": "Données actuelles du processus manquantes",
    "cleanup-software-maximum-base-score-missing": "Le score de base le plus élevé du logiciel est manquant",
    "ownership-software-attribution-mismatch": "La responsabilité des modifications ne correspond pas à l’attribution logicielle",
    "canonical-process-membership-mismatch": "Le processus ne correspond pas aux membres logiciels du score",
    "canonical-software-score-missing": "Score du logiciel manquant",
    "live-identity-without-decision-facts": "Un processus en cours d’exécution n’a pas de données de décision",
    "canonical-process-not-projected": "Le score du processus n’est pas parvenu à l’entrée de planification",
    "canonical-software-membership-incomplete": "Les membres du score du logiciel sont incomplets"
  }
};
