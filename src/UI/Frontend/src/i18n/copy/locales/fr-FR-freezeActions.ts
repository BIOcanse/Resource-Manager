import { plural } from "../../plural.ts";
import type { AppCopy } from "../index.ts";

export const deviceActions: AppCopy["deviceActions"] = {
  restart: {
    label: "Redémarrer le périphérique",
    busy: "Redémarrage",
    hint: "Désactive puis réactive le périphérique visé par ce rapport, ce qui peut remettre en marche un pilote bloqué. Vous confirmez à chaque fois ; le logiciel ne le fait jamais de lui-même.",
    confirmTitle: "Redémarrer ce périphérique ?",
    confirmMessage: (target: string) => `Le périphérique associé à « ${target} » sera désactivé puis réactivé.`,
    confirmDetails: [
      "Cette action est exécutée une seule fois, après votre confirmation, et n’est jamais répétée automatiquement.",
      "Les droits d’administrateur sont nécessaires. Le résultat est inscrit dans les actions sur les périphériques, dans Détails."
    ],
    risks: {
      DeviceRestartNetworkInterrupted: "S’il s’agit d’une carte réseau, les connexions réseau sont brièvement interrompues.",
      DeviceRestartDisplayStateLost: "S’il s’agit d’une carte graphique, l’écran devient noir un instant et la position des fenêtres ainsi que les paramètres d’affichage peuvent être perdus.",
      DeviceRestartMayRequireReboot: "Certains périphériques ne se rétablissent qu’après le redémarrage de l’ordinateur."
    },
    unknownRisk: (code: string) => `Autre risque (${code}).`,
    confirm: "Redémarrer le périphérique",
    doneTitle: "Périphérique redémarré",
    refusedTitle: "Périphérique non redémarré",
    failed: "Impossible de redémarrer le périphérique.",
    reason: (text: string) => `Résultat : ${text}`,
    deviceResult: (text: string) => `Résultat du périphérique : ${text}`,
    win32Detail: (code: string) => `Code d’erreur système : ${code}`,
    recorded: (sequence: string) => `Inscrit dans les actions sur les périphériques (numéro ${sequence}).`,
    notRecorded: "Cette demande n’a pas été enregistrée."
  },
  reasons: {
    completed: "Terminé",
    "confirmation-required": "Confirmation requise",
    "report-not-found": "L’entrée du rapport est introuvable",
    "unsupported-report": "Ce type de rapport ne permet pas de redémarrer un périphérique",
    "device-evidence-unavailable": "Le rapport ne contient aucune donnée permettant d’identifier le périphérique",
    "invalid-settings": "Les paramètres ne sont pas valides",
    "record-capacity-reached": "Les actions sur les périphériques sont pleines ; rien n’a été fait",
    "persistence-failed": "L’enregistrement n’a pas pu être écrit ; rien n’a été fait",
    "device-resolution-failed": "Le périphérique à redémarrer n’a pas pu être identifié",
    "device-restart-failed": "Le redémarrage du périphérique a échoué",
    "notification-failed": "Le périphérique a été traité, mais la notification n’a pas pu être envoyée"
  },
  results: {
    success: "Réussi",
    "confirmation-required": "Confirmation requise",
    "invalid-instance-id": "ID d’instance de périphérique non valide",
    "administrator-required": "Droits d’administrateur requis",
    "device-not-found": "Périphérique introuvable",
    "device-unavailable": "Périphérique indisponible pour le moment",
    "disable-failed": "La désactivation du périphérique a échoué",
    "enable-failed": "La réactivation du périphérique a échoué",
    "reboot-required": "L’ordinateur doit redémarrer",
    "verification-failed": "La vérification après le redémarrage a échoué",
    "driver-not-found": "Pilote introuvable",
    "ambiguous-device": "Plusieurs périphériques correspondent ; rien n’a été fait",
    "invalid-module": "Module de pilote non valide",
    "enumeration-failed": "L’énumération des périphériques a échoué",
    "buffer-too-small": "Tampon système trop petit",
    "unsupported-platform": "Non pris en charge sur ce système",
    interrupted: "Interrompu en cours d’exécution ; le résultat est inconnu"
  },
  unknownCode: (code: string) => `Code inconnu ${code}`,
  records: {
    tab: "Actions sur les périphériques",
    intro: "Chaque fois que vous confirmez « Redémarrer le périphérique » sur un rapport, le résultat est inscrit ici. Les enregistrements sont conservés et jamais supprimés automatiquement ; un enregistrement en cours passe dans l’historique à la fin de l’action.",
    loading: "Chargement des actions sur les périphériques…",
    readFailed: "Impossible de charger les actions sur les périphériques",
    empty: "Aucune action sur les périphériques pour l’instant.",
    loadMore: "Charger les enregistrements précédents",
    loadingMore: "Chargement",
    refresh: "Actualiser",
    shown: (count: string) => `Enregistrements affichés : ${count}.`,
    states: { active: "En cours", history: "Historique" },
    device: "Instance du périphérique",
    report: "Entrée du rapport",
    time: "Heure",
    result: "Résultat",
    detail: "Code d’erreur système",
    sequence: "Numéro"
  }
};

export const crashDumpKeys: AppCopy["crashDumpKeys"] = {
  title: "Touches de vidage sur incident",
  description: "Lorsque cette option est activée, maintenir la touche Ctrl de droite et appuyer deux fois sur Arrêt défil arrête le système avec un écran bleu et écrit un vidage de la mémoire. Si l’ordinateur se fige et ne peut plus être utilisé normalement, l’état est ainsi conservé pour une analyse ultérieure. Désactivée par défaut.",
  details: [
    "Elle ne se déclenche que lorsque vous appuyez sur ces touches ; sinon, elle n’a aucun effet.",
    "L’ordinateur redémarre aussitôt après, et le travail non enregistré est perdu.",
    "Les modifications prennent effet après le redémarrage de l’ordinateur et exigent les droits d’administrateur pour être écrites. La désactivation rétablit les paramètres système d’avant l’activation."
  ],
  notProvided: "Le service n’a pas fourni ce paramètre.",
  statusTitle: "État dans le système",
  states: {
    takesEffectAfterRestart: "Écrit dans le système ; prend effet après le redémarrage de l’ordinateur.",
    off: "Non activée.",
    restorePending: "Rétablissement des paramètres système précédents ; terminé après le redémarrage de l’ordinateur.",
    configurationMismatch: "Les paramètres de touches du système ne correspondent pas au choix fait ici.",
    failed: "L’écriture des paramètres système a échoué."
  },
  combinations: {
    rightCtrlScrollLockTwice: "Touches : Ctrl de droite + deux fois Arrêt défil",
    custom: "Touches : touches personnalisées définies par un autre programme ou manuellement"
  },
  kernelDumpMissing: "Le système n’est pas configuré pour écrire des vidages de la mémoire du noyau ; aucun vidage ne sera peut-être écrit au déclenchement.",
  reason: (text: string) => `Motif : ${text}`,
  win32Detail: (code: string) => `Code d’erreur système : ${code}`,
  reasons: {
    "invalid-crash-dump-keys": "Les paramètres de touches ne sont pas valides",
    "crash-dump-keys-read-failed": "La lecture des paramètres système a échoué",
    "crash-dump-keys-write-failed": "L’écriture des paramètres système a échoué",
    "crash-dump-keys-verify-failed": "La vérification après écriture a échoué",
    "crash-dump-keys-restore-pending": "Les paramètres système précédents ne sont pas encore entièrement rétablis",
    "invalid-crash-dump-key-snapshot": "Les paramètres d’origine enregistrés ne sont pas valides",
    "crash-dump-keys-access-denied": "Les droits d’administrateur sont nécessaires",
    "crash-dump-key-value-too-large": "Une valeur d’origine du système est trop grande pour être enregistrée",
    "crash-dump-keys-configuration-mismatch": "Un autre programme a modifié les paramètres du système",
    "crash-dump-keys-bridge-failed": "Le service local n’a pas pu traiter ce paramètre"
  }
};

export const cpuMonopoly: AppCopy["cpuMonopoly"] = {
  intro: "Lorsqu’un processus en priorité haute ou temps réel occupe le processeur en permanence, le système peut se figer et ne plus répondre. Lorsque cette protection est activée, un tel processus est remis en priorité normale. Seule la priorité change : le processus n’est ni arrêté ni suspendu, et sa priorité n’est pas rétablie ensuite. La liste d’exceptions de la protection de la mémoire de l’hôte s’applique aussi. Désactivée par défaut.",
  notProvided: "Le service n’a pas fourni les paramètres de la protection contre le monopole du processeur.",
  unsavedNote: "Des modifications ne sont pas enregistrées ; elles prennent effet après l’enregistrement.",
  rejected: (reason: string) => `Les paramètres n’ont pas été acceptés : ${reason}`,
  enabledTitle: "Activer la protection contre le monopole du processeur",
  enabledDescription: "Lorsqu’elle est activée, l’utilisation du processeur et la priorité des processus sont vérifiées en arrière-plan ; sinon, rien n’est vérifié.",
  thresholdTitle: "Seuil d’utilisation",
  thresholdDescription: "Le décompte commence lorsqu’un processus en priorité haute ou temps réel atteint cette utilisation du processeur, en pourcentage de l’ensemble des processeurs.",
  thresholdLabel: "Pourcentage",
  durationTitle: "Durée",
  durationDescription: "Durée pendant laquelle l’utilisation doit rester au seuil avant que le processus soit remis en priorité normale.",
  durationLabel: "Secondes",
  intervalTitle: "Intervalle de vérification",
  intervalDescription: "Fréquence de lecture de l’utilisation du processeur par les processus.",
  intervalLabel: "Secondes",
  whitelistNote: "La liste d’exceptions se modifie dans la section Protection de la mémoire de l’hôte.",
  statusTitle: "État actuel",
  statusLoading: "Chargement de l’état de la protection contre le monopole du processeur…",
  statusUnavailable: "Impossible de charger l’état de la protection contre le monopole du processeur",
  statusUnavailableDetail: "Les paramètres restent modifiables et enregistrables ; l’état s’affichera ici dès qu’il sera disponible.",
  stateActive: "Vérification en cours",
  stateInactive: (reason: string) => `Aucune vérification : ${reason}`,
  inactiveReasons: {
    disabled: "Désactivée",
    "not-running": "La protection n’a pas encore démarré",
    "process-data-unavailable": "Les données des processus sont illisibles",
    "process-exit-proof-unavailable": "Impossible de confirmer si les processus se sont arrêtés",
    "process-capacity": "Plus de processus qu’il n’est possible d’en suivre",
    stopped: "Arrêtée"
  },
  currentAction: "Action en vigueur",
  exclusionsTitle: "Processus exclus",
  exclusionsEmpty: "Aucun processus n’est exclu pour l’instant.",
  exclusionReasons: {
    "resource-manager": "Un processus de ce logiciel",
    "resource-manager-process": "Un processus de ce logiciel",
    "protected-process": "Dans la liste d’exceptions",
    "critical-process": "Un processus système critique",
    "unsupported-platform": "Non pris en charge sur ce système",
    "identity-changed": "Le processus a changé",
    "cannot-open": "Le processus est inaccessible",
    "criticality-unavailable": "Impossible de confirmer s’il s’agit d’un processus système critique"
  },
  process: (name: string, processId: number) => `${name} (PID ${processId})`,
  percent: (value: string) => `${value} %`,
  seconds: (value: string) => `${value} s`,
  priorities: { High: "Haute", RealTime: "Temps réel" },
  records: {
    tab: "Protection contre le monopole du processeur",
    intro: "La protection contre le monopole du processeur crée un enregistrement pour chaque processus traité. Une fois le processus remis en priorité normale, son enregistrement passe dans l’historique lorsque le processus s’arrête ou que vous définissez vous-même sa priorité ; une action échouée va directement dans l’historique. Les enregistrements sont conservés et jamais supprimés automatiquement.",
    loading: "Chargement des enregistrements de la protection contre le monopole du processeur…",
    unavailable: "Impossible de charger les enregistrements de la protection contre le monopole du processeur",
    empty: "Aucune action enregistrée pour l’instant.",
    count: (count: string, capacity: string) => `Enregistrements : ${count} ; au plus ${capacity}.`,
    unrecorded: (count: number) => plural("fr-FR", count, {
      one: "La liste est pleine ; {count} action ultérieure n’a pas été enregistrée. L’enregistrement reprend après la suppression de l’historique.",
      many: "La liste est pleine ; {count} actions ultérieures n’ont pas été enregistrées. L’enregistrement reprend après la suppression de l’historique.",
      other: "La liste est pleine ; {count} actions ultérieures n’ont pas été enregistrées. L’enregistrement reprend après la suppression de l’historique."
    }),
    outcomes: { normalized: "Remis en priorité normale", failed: "Action échouée" },
    states: { active: "Toujours en vigueur", history: "Historique" },
    time: "Heure",
    cpu: "Utilisation du processeur",
    previousPriority: "Priorité précédente",
    threshold: "Seuil à ce moment",
    duration: "Durée à ce moment",
    reason: "Motif",
    detail: "Détails",
    endedAt: "Fin",
    endReason: "Motif de fin"
  },
  actionReasons: {
    "process-exited": "Le processus s’est arrêté",
    "user-priority-set": "Sa priorité a été définie séparément",
    "write-failed": "L’écriture de la priorité a échoué",
    "identity-changed": "Le processus a changé",
    "priority-changed": "Un autre programme a modifié sa priorité",
    "priority-unavailable": "Sa priorité est illisible",
    "priority-write-failed": "L’écriture de la priorité a échoué",
    "priority-readback-unavailable": "La priorité n’a pas pu être relue après l’écriture",
    "priority-readback-mismatch": "La priorité relue après l’écriture ne correspond pas"
  },
  notice: {
    title: "Protection contre le monopole du processeur",
    normalized: (target: string) => `${target} a été remis en priorité normale.`,
    failed: (target: string, reason: string) => `Impossible d’ajuster ${target} : ${reason}`,
    openRecords: "Voir les enregistrements"
  }
};
