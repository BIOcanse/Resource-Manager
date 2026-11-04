import { plural } from "../../plural.ts";
import type { AppCopy } from "../index.ts";

export const freezeReports: AppCopy["freezeReports"] = {
  systemEvents: {
    tab: "Événements système",
    intro: "Le service local recueille dans les journaux système des événements tels que les nouvelles tentatives et réinitialisations du stockage, les réinitialisations du pilote graphique, l’épuisement de la mémoire, les plantages des processus du bureau et les erreurs matérielles, puis les regroupe en entrées par catégorie et par cible. Ce logiciel ne peut pas résoudre ces problèmes ; ils sont seulement détectés et signalés ici. Les entrées sans nouvel événement pendant longtemps passent dans l’historique.",
    loading: "Chargement du rapport des événements système…",
    readFailed: "Le rapport des événements système n’a pas pu être lu.",
    notObservedTitle: "Les événements système ne sont pas enregistrés",
    notObservedDetail: "L’enregistrement des événements système n’est pas en cours : il a peut-être été désactivé dans les paramètres ou n’a pas encore démarré. Aucune entrée n’est créée tant qu’il ne fonctionne pas.",
    unavailableTitle: "Échec de l’enregistrement des événements système",
    unavailableDetail: "Le service local ne peut pas lire les journaux système. La raison est indiquée ci-dessus.",
    emptyTitle: "Aucun événement système",
    emptyDetail: "Aucun événement à signaler n’est apparu dans les journaux système.",
    status: {
      label: "État de l’enregistrement",
      observing: "Enregistrement en cours",
      "not-observed": "Aucun enregistrement",
      unavailable: "Échec de l’enregistrement"
    },
    activeCount: "Entrées actives",
    historyCount: "Entrées historiques",
    machineTarget: "Ordinateur entier",
    count: "Occurrences",
    times: (count: number) => plural("fr-FR", count, { one: "{count} fois", many: "{count} fois", other: "{count} fois" }),
    firstObserved: "Première occurrence",
    lastObserved: "Dernière occurrence",
    overview: "Vue d’ensemble",
    categoryLabel: "Catégorie",
    target: "Cible",
    state: "État",
    activeEntry: "Active",
    historyEntry: "Historique",
    category: {
      storage_retry_reset: "Nouvelles tentatives et réinitialisations du stockage",
      gpu_reset: "Réinitialisation du pilote graphique",
      memory_exhausted: "Épuisement de la mémoire",
      desktop_crash: "Plantage d’un processus du bureau",
      hardware_error: "Erreur matérielle",
      unexpected_shutdown: "Arrêt inattendu et écran bleu"
    },
    targetKindLabel: "Type de cible",
    targetKind: {
      machine: "Ordinateur entier",
      physical_disk: "Disque physique",
      storage_port: "Port du contrôleur de stockage",
      volume: "Volume",
      gpu_device: "Périphérique graphique",
      desktop_process: "Processus du bureau",
      hardware: "Matériel"
    },
    time: "Heure",
    channel: "Journal",
    channelName: {
      system: "Système",
      application: "Application"
    },
    provider: "Source",
    eventId: "ID d’événement",
    recordId: "ID d’enregistrement",
    bugcheckCode: "Code d’arrêt",
    dumpPath: "Fichier de vidage",
    failure: (name: string) => `Les journaux système n’ont pas pu être lus (${name}). Les entrées existantes sont conservées et l’enregistrement reprendra dès que la lecture sera rétablie.`,
    liveKernelFailure: (name: string) => `Le dossier LiveKernelReports n’a pas pu être analysé (${name}). Les événements des journaux système ne sont pas concernés.`,
    unrecorded: (count: number) => plural("fr-FR", count, {
      one: "{count} autre entrée n’a pas été enregistrée, car la limite d’entrées est atteinte.",
      many: "{count} autres entrées n’ont pas été enregistrées, car la limite d’entrées est atteinte.",
      other: "{count} autres entrées n’ont pas été enregistrées, car la limite d’entrées est atteinte."
    }),
    items: "Entrées des événements système",
    detailsButton: "Détails",
    back: "Retour à la liste",
    eventsTitle: (count: number) => plural("fr-FR", count, {
      one: "Dernier événement brut ({count})",
      many: "Derniers événements bruts ({count})",
      other: "Derniers événements bruts ({count})"
    }),
    xml: "XML de l’événement"
  },
  recorder: {
    tab: "Enregistreur de blocages",
    intro: "Le service local écrit en continu une petite quantité de données sur la mémoire, les pools du noyau, les disques et les interruptions dans un fichier d’enregistrement de taille fixe. Si l’ordinateur se bloque puis est arrêté de force ou s’arrête sur un écran bleu, une analyse après redémarrage est créée au démarrage suivant en comparant les enregistrements avec les journaux système. Elle présente les enregistrements précédant le blocage, les événements système de la même période et les constats. Ces informations sont seulement détectées et signalées ici.",
    loading: "Chargement de l’enregistreur de blocages…",
    readFailed: "L’enregistreur de blocages n’a pas pu être lu.",
    emptyTitle: "Aucune analyse après redémarrage",
    emptyDetail: "Aucun arrêt inattendu ni écran bleu n’a été détecté depuis le dernier démarrage.",
    recorder: "Enregistreur",
    running: "Enregistrement en cours",
    stopped: "Aucun enregistrement",
    lastRecordAt: "Dernier enregistrement",
    reviewCount: "Analyses après redémarrage",
    failure: (name: string) => `L’enregistreur a échoué (${name}) et n’écrit aucun enregistrement.`,
    stoppedNote: "L’enregistreur n’est pas en cours d’exécution ; il a peut-être été désactivé dans les paramètres. Un blocage survenant pendant cette période ne peut pas être analysé entièrement.",
    gapsTitle: "Données manquantes dans les enregistrements actuels",
    gap: {
      "no-sample": "Aucun enregistrement n’a encore été écrit",
      "interrupt-cpu-share": "Temps processeur occupé par les interruptions",
      "longest-interrupt": "Plus longue interruption unique",
      "active-kernel-leak-system-item": "Présence d’une entrée active de fuite du noyau au niveau du système",
      "process-private-commit": "Mémoire validée privée des processus",
      "physical-disk-inventory": "Liste des disques physiques",
      "physical-disk-completed-operations": "Opérations terminées des disques physiques",
      commit: "Mémoire validée et limite de validation",
      "physical-memory": "Mémoire physique",
      "kernel-pools": "Pools paginé et non paginé"
    },
    reviews: "Analyses après redémarrage",
    reviewTitle: (time: string) => `Arrêt inattendu · ${time}`,
    shutdownAt: "Heure de l’arrêt ou de l’écran bleu",
    gapDuration: "Écart",
    bugcheckCode: "Code d’arrêt",
    tagsTitle: "Constats",
    noTags: "Aucun constat.",
    inputsIncomplete: "Certains enregistrements ou journaux système n’ont pas pu être lus entièrement lors de l’analyse ; le résultat peut être incomplet.",
    tag: {
      bugcheck: {
        name: "Écran bleu",
        description: "Les journaux système contiennent un écran bleu ; le code d’arrêt figure dans la vue d’ensemble."
      },
      "commit-exhausted": {
        name: "Mémoire validée épuisée",
        description: "La mémoire validée enregistrée a atteint la ligne immédiate de la protection de la mémoire, ou le système a signalé un épuisement de la mémoire pendant la même période."
      },
      "physical-exhausted": {
        name: "Mémoire physique épuisée",
        description: "La mémoire physique disponible enregistrée est passée sous le pourcentage défini dans les paramètres."
      },
      "pool-high": {
        name: "Pool du noyau trop élevé",
        description: "Le pool non paginé a dépassé le pourcentage défini de la mémoire physique, ou une entrée de fuite du noyau au niveau du système était active."
      },
      "storage-unresponsive": {
        name: "Stockage sans réponse",
        description: "Des nouvelles tentatives ou réinitialisations du stockage ont eu lieu pendant la même période, ou un disque a rempli la condition d’absence de réponse plusieurs fois de suite."
      },
      "gpu-reset": {
        name: "Réinitialisation du pilote graphique",
        description: "Le pilote graphique a été réinitialisé pendant la même période."
      },
      "hardware-error": {
        name: "Erreur matérielle",
        description: "Le système a signalé une erreur matérielle pendant la même période."
      },
      "recording-stopped-normal": {
        name: "Enregistrements arrêtés en état normal",
        description: "Aucun des cas ci-dessus ne s’est produit et les derniers enregistrements étaient tous normaux. Les causes possibles sont un pilote de stockage ou de filtre bloqué, une coupure de courant soudaine ou un appui prolongé sur le bouton d’alimentation."
      }
    },
    details: {
      button: "Détails",
      back: "Retour à la liste",
      loading: "Chargement de l’analyse…",
      readFailed: "L’analyse n’a pas pu être lue.",
      export: "Exporter",
      exporting: "Exportation",
      exportFailed: "L’analyse n’a pas pu être exportée.",
      overview: "Vue d’ensemble",
      reviewWindow: "Période analysée",
      timeRange: (from: string, to: string) => `${from} – ${to}`,
      samplesSeen: "Enregistrements de la période",
      inputsComplete: "Données complètes",
      yes: "Oui",
      no: "Non",
      dumpPath: "Fichier de vidage",
      thresholdsTitle: "Seuils utilisés pour l’analyse",
      commitImmediatePercent: "Ligne immédiate de mémoire validée",
      physicalAvailablePercent: "Plancher de mémoire physique disponible",
      nonpagedPoolPercent: "Plafond du pool non paginé (part de la mémoire physique)",
      storageConsecutive: "Enregistrements consécutifs sans réponse du disque",
      sampleInterval: "Intervalle d’enregistrement",
      reviewWindowLength: "Durée de la période analysée",
      curvesTitle: "Courbes des enregistrements",
      curve: {
        commit: "Mémoire validée (part de la limite)",
        physicalAvailable: "Mémoire physique disponible (part du total)",
        nonpagedPool: "Pool non paginé",
        pagedPool: "Pool paginé",
        diskQueue: "Longueur de file d’attente des disques (maximale)",
        diskLatency: "Latence des disques (maximale)",
        interrupt: "Temps processeur occupé par les interruptions"
      },
      curveRange: (min: string, max: string) => `${min} – ${max}`,
      noCurveData: "Aucune donnée",
      noRecords: "Aucun enregistrement dans la période analysée.",
      lastRecordTitle: "Dernier enregistrement",
      time: "Heure",
      commit: "Mémoire validée",
      physicalAvailable: "Mémoire physique disponible",
      ofLimit: (formattedValue: string, formattedLimit: string) => `${formattedValue} / ${formattedLimit}`,
      longestInterrupt: "Plus longue interruption unique",
      kernelLeakActive: "Entrée de fuite du noyau au niveau du système active",
      processesTitle: "Processus ayant le plus de mémoire validée privée",
      processName: (name: string, pid: string) => `${name} (PID ${pid})`,
      diskName: (id: string) => `Disque physique ${id}`,
      queueDepth: "Longueur de file d’attente",
      latency: "Latence",
      completedOperations: "Opérations terminées",
      eventsTitle: (count: number) => plural("fr-FR", count, {
        one: "Événement système de la période ({count})",
        many: "Événements système de la période ({count})",
        other: "Événements système de la période ({count})"
      }),
      noEvents: "Aucun événement système n’a été reçu pendant la période analysée.",
      actualShutdownAt: "Heure réelle de l’arrêt"
    }
  },
  settings: {
    open: "Paramètres",
    back: "Retour au rapport",
    systemEventsTitle: "Paramètres des événements système",
    recorderTitle: "Paramètres de l’enregistreur de blocages",
    readFailed: "Les paramètres n’ont pas pu être lus.",
    save: "Enregistrer",
    saving: "Enregistrement",
    discard: "Annuler les modifications",
    saved: "Les paramètres ont été enregistrés.",
    saveFailed: "Les paramètres n’ont pas pu être enregistrés.",
    invalid: "Les paramètres n’ont pas passé la validation et n’ont pas été enregistrés. Vérifiez les valeurs.",
    conflict: "Les paramètres n’ont pas été enregistrés : la table des règles, le nombre d’enregistrements et l’emplacement du fichier d’enregistrement ne peuvent pas être modifiés en cours de fonctionnement. Désactivez d’abord la fonction concernée et enregistrez, puis modifiez ces éléments. Si un fichier d’enregistrement existe déjà, modifier le nombre d’enregistrements exige aussi un nouvel emplacement.",
    resourceBound: "Les paramètres n’ont pas été enregistrés : les ressources nécessaires dépassent la limite. Réduisez la capacité ou le nombre d’enregistrements.",
    draftInvalid: "Les modifications en cours sont incomplètes (par exemple, une règle n’a pas de nom de source) et ne peuvent pas être enregistrées.",
    rulesLocked: "La table des règles ne peut pas être modifiée lorsque l’enregistrement des événements système est activé. Désactivez-le et enregistrez d’abord.",
    recorderLocked: "Cet élément ne peut pas être modifié lorsque l’enregistreur est activé. Désactivez-le et enregistrez d’abord.",
    units: {
      minutes: "Minutes",
      seconds: "Secondes",
      milliseconds: "Millisecondes",
      percent: "Pourcentage",
      count: "Nombre"
    },
    systemEvents: {
      enabled: {
        title: "Enregistrer les événements système",
        description: "Recevoir les événements des journaux système et les regrouper en entrées. Une fois désactivé, aucun nouvel événement n’est reçu et les entrées existantes sont conservées."
      },
      historyAfter: {
        title: "Passage dans l’historique après",
        description: "Une entrée passe dans l’historique lorsqu’aucun nouvel événement n’arrive pendant cette durée après son dernier événement."
      },
      eventTailCapacity: {
        title: "Événements bruts conservés par entrée",
        description: "Nombre d’événements bruts les plus récents que chaque entrée conserve pour la page de détails."
      },
      entryCapacity: {
        title: "Limite d’entrées",
        description: "Nombre maximal d’entrées conservées ; une fois la limite atteinte, les nouvelles entrées sont seulement comptées, sans être enregistrées."
      },
      liveKernelRoot: {
        title: "Emplacement de LiveKernelReports",
        description: "Dossier dans lequel le système place les rapports de vidage du noyau en direct ; il sert à trouver des rapports tels que les délais d’attente du pilote graphique."
      },
      liveKernelDirectories: {
        title: "Sous-dossiers analysés",
        description: "Sous-dossiers analysés sous l’emplacement ci-dessus, un par ligne."
      },
      liveKernelEntryCapacity: {
        title: "Limite de rapports de vidage",
        description: "Nombre maximal de rapports de vidage enregistrés à chaque analyse."
      },
      rules: {
        title: "Règles d’événements",
        description: "Quels événements des journaux système appartiennent à quelle catégorie, et à quel type de cible chaque événement se rapporte."
      }
    },
    recorder: {
      enabled: {
        title: "Enregistrer les données de blocage",
        description: "Écrire des enregistrements en continu et créer une analyse au démarrage suivant après un arrêt inattendu. Une fois désactivé, l’enregistrement s’arrête et le fichier d’enregistrement est conservé."
      },
      sampleInterval: {
        title: "Intervalle d’enregistrement",
        description: "Durée entre deux enregistrements. Plus l’intervalle est court, plus le dernier enregistrement est proche du moment du blocage."
      },
      reviewWindow: {
        title: "Période analysée",
        description: "Durée avant le dernier enregistrement pour laquelle l’analyse affiche les enregistrements et les événements système."
      },
      commitImmediate: {
        title: "Ligne immédiate de mémoire validée",
        description: "Ligne utilisée pour constater l’épuisement de la mémoire validée. Elle suit la ligne immédiate de la protection de la mémoire et se modifie dans les paramètres de celle-ci."
      },
      commitImmediateNone: "Non définie",
      physicalAvailable: {
        title: "Plancher de mémoire physique disponible",
        description: "L’épuisement de la mémoire physique est constaté lorsque la mémoire disponible passe sous ce pourcentage du total."
      },
      nonpagedPool: {
        title: "Plafond du pool non paginé",
        description: "Un pool du noyau trop élevé est constaté lorsque le pool non paginé dépasse ce pourcentage de la mémoire physique."
      },
      storageConsecutive: {
        title: "Enregistrements consécutifs sans réponse du disque",
        description: "Un stockage sans réponse est constaté lorsqu’un disque remplit la condition dans ce nombre d’enregistrements consécutifs."
      },
      slots: {
        title: "Nombre d’enregistrements",
        description: "Nombre d’enregistrements que contient le fichier d’enregistrement, ce qui détermine jusqu’où l’analyse peut remonter. Si un fichier d’enregistrement existe déjà, le modifier exige aussi un nouvel emplacement."
      },
      ringPath: {
        title: "Emplacement du fichier d’enregistrement",
        description: "Chemin complet du fichier d’enregistrement. Laissez-le vide pour utiliser l’emplacement par défaut."
      },
      ringPathDefault: "Emplacement par défaut"
    },
    ruleTable: {
      channel: "Journal",
      provider: "Nom de la source",
      ids: "ID d’événement",
      idsHint: "Séparés par des virgules",
      category: "Catégorie",
      targetKind: "Type de cible",
      remove: "Supprimer",
      add: "Ajouter une règle"
    }
  }
};
