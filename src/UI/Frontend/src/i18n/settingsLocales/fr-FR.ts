import { createSettingsLocale } from "../settingsLocaleFactory.ts";
import { plural } from "../plural.ts";

export default createSettingsLocale("fr-FR", {
  "notifications": {
    "intro": "Choisissez pour chaque type de notification s’il s’affiche et par quelles voies. Les notifications Windows proviennent de la zone de notification ; les avis dans l’application apparaissent lorsque l’interface est affichée.",
    "kinds": {
      "memoryProtection": { "title": "Protection de la mémoire de l’hôte", "description": "Notifie lorsque la protection de la mémoire de l’hôte arrête un processus ou une arborescence de processus, en indiquant la cible, le nombre de processus et la taille du vidage." },
      "update": { "title": "Nouvelle version", "description": "Notifie lorsqu’une nouvelle version est disponible." },
      "accessRequest": { "title": "Demandes d’accès", "description": "Notifie lorsqu’une nouvelle demande d’accès approuvé attend une validation, en indiquant le nom et le programme du demandeur." }
    },
    "notifyTitle": "Afficher les notifications",
    "notifyDescription": "Désactivé, ce type de notification ne s’affiche par aucune voie ; le choix des deux voies est conservé.",
    "systemTitle": "Notification Windows",
    "systemDescription": "La zone de notification affiche une notification système Windows, y compris lorsque la fenêtre principale est fermée ; un clic ouvre Resource Manager.",
    "inAppTitle": "Avis dans l’application",
    "inAppDescription": "S’affiche dans l’interface lorsqu’elle est affichée ; les notifications reçues pendant qu’elle est fermée ne sont pas affichées ensuite.",
    "switchLabel": (kind: string, option: string) => `${kind} : ${option}`,
    "accessRequestNoticeTitle": "Nouvelle demande d’accès en attente de validation",
    "accessRequestNoticeMessage": (name: string, program: string) => `${name} (${program}) demande un accès approuvé.`,
    "accessRequestNoticeOpen": "Voir les demandes d’accès"
  },
  "updates": {
    "modeTitle": "Mode de mise à jour",
    "modeDescription": "Désactivé : aucune vérification automatique. La vérification et le choix manuels de version restent disponibles dans tous les modes.",
    "modeOptions": [{ id: "off", label: "Désactivé", description: "Aucune vérification, aucun téléchargement ni installation automatiques." }, { id: "notify", label: "Vérifier et signaler", description: "Vérifie automatiquement et signale une nouvelle version sans la télécharger." }, { id: "automatic", label: "Télécharger et appliquer", description: "Vérifie, télécharge et valide automatiquement ; applique après la fermeture des fenêtres, ou via Appliquer maintenant." }],
    "periodicTitle": "Vérification périodique",
    "periodicDescription": "Après la vérification au démarrage, vérifier de nouveau à cet intervalle.",
    "internetNote": "Les vérifications automatiques attendent l’accès à Internet ; l’intervalle ne compte que le temps connecté.",
    "offReason": "Le mode est Désactivé : aucune vérification automatique, ces deux réglages sont sans effet ; leurs valeurs sont conservées.",
    "intervalTitle": "Intervalle de vérification (heures)",
    "intervalLabel": (minimum: number, maximum: number) => `Intervalle (heures, ${minimum}–${maximum})`,
    "manualCheckNote": "Une vérification manuelle s’exécute tout de suite, sans attendre Internet, dans tous les modes.",
    "waitingForInternet": "En attente d’Internet ; la vérification démarre dès la connexion.",
    "availableVersion": "Nouvelle version trouvée : ",
    "nextCheck": "Prochaine vérification : ",
    "nextCheckAfterInternet": "décompte après connexion",
    "nextCheckOff": "non activée",
    "connectedElapsed": "Temps connecté depuis la dernière vérification : ",
    "noticeTitle": "Nouvelle version disponible",
    "noticeMessage": (version: string) => `Resource Manager ${version} est disponible.`,
    "noticeReadyMessage": (version: string) => `Resource Manager ${version} est téléchargé et validé ; vous pouvez l’appliquer maintenant.`,
    "noticeOpenUpdates": "Ouvrir Mises à jour et versions",
    "installedVersionTitle": "Version installée",
    "unknownVersion": "Inconnu",
    "checking": "Vérification…",
    "checkForUpdates": "Rechercher des mises à jour",
    "historyIncomplete": "L’historique des versions est incomplet.",
    "historyStale": "Le catalogue précédent est affiché ; reconnectez-vous puis réessayez.",
    "versionsTitle": "Versions du produit",
    "versionsDescription": "Les plus récentes d’abord ; chaque série affiche sa dernière version par défaut.",
    "lastChecked": "Dernière vérification : ",
    "selectedTarget": "Version cible : ",
    "prepare": "Préparer la mise à jour choisie",
    "confirmPrepare": (version: string) => `Préparer la version ${version} ? Le gestionnaire vérifie le paquet, attend la fermeture de toutes les sessions de bureau, arrête le service, conserve et migre les données, puis vérifie la nouvelle version. En cas d’échec, il restaure la version précédente.`,
    "prepareFailed": "Impossible de préparer la mise à jour.",
    "waitingForExit": "Mise à jour prête. Quittez toutes les sessions de bureau Resource Manager depuis la zone de notification pour l’appliquer.",
    "stageLabel": "État de la mise à jour",
    "unknownStage": "État de mise à jour inconnu",
    "stages": {
      "idle": "Inactif",
      "checking": "Recherche de mises à jour",
      "downloading": "Téléchargement du paquet",
      "verifying": "Vérification du paquet",
      "waitingForExit": "En attente de fermeture des sessions",
      "completed": "Mise à jour terminée",
      "error": "Échec de la mise à jour"
    },
    "otherVersions": "Autres versions",
    "installedUnknown": "La version installée est inconnue ; l’ordre de mise à niveau ne peut pas être vérifié.",
    "stable": "Stable",
    "preview": "Préversion",
    "noLatest": "Aucune dernière version disponible.",
    "noStable": "Aucune version stable disponible.",
    "latest": "Dernière version",
    "latestStable": "Dernière version stable",
    "verified": "Version vérifiée",
    "unavailableReasons": {
      "installedVersionUnknown": "Version installée inconnue ; sélection de mise à niveau désactivée.",
      "notNewer": "Seules les versions plus récentes que celle installée peuvent être sélectionnées."
    }
  },
  "navigationLabel": "Rubriques des paramètres",
  "sections": {
    "runtimePolicy": "Stratégie d’exécution",
    "memory": "Mémoire",
    "memoryCpuTier": "Compromis mémoire et processeur",
    "memoryProtection": "Protection de la mémoire de l’hôte",
    "hardwareControl": "Contrôle matériel",
    "reportRules": "Règles de rapport",
    "appearance": "Apparence",
    "performanceOverlay": "Superposition de performances",
    "systemIntegration": "Intégration système",
    "publicServices": "Services partagés",
    "trustedAccess": "Accès approuvé",
    "notifications": "Notifications",
    "updates": "Mises à jour et versions",
    "debug": "Débogage",
    "credits": "Crédits"
  },
  "saveState": {
    "saving": "Enregistrement",
    "saved": "Enregistré",
    "constrained": "Enregistré ; les options indisponibles ont été limitées",
    "partial": "Enregistré ; la publication ou la transmission du plan d’exécution est incomplète",
    "error": "Échec de l'enregistrement",
    "dirty": "Non enregistré",
    "conflict": "Paramètres modifiés ailleurs ; rechargez avant d’enregistrer",
    "invalidValueType": "Enregistrement refusé : une valeur de paramètre a un type incorrect"
  },
  "loadState": {
    "loading": "Chargement des paramètres enregistrés…",
    "errorTitle": "Paramètres indisponibles",
    "errorDescription": "Les valeurs par défaut ne remplacent pas les paramètres enregistrés. Rétablissez le service local puis réessayez.",
    "staleTitle": "Paramètres désynchronisés",
    "staleDescription": "Cette copie peut être obsolète. Aucune modification ne sera envoyée avant un rechargement réussi."
  },
  "actions": {
    "save": "Enregistrer",
    "restoreDefaults": "Rétablir les valeurs par défaut",
    "retry": "Réessayer",
    "reload": "Recharger",
    "reapply": "Republier le plan d’exécution"
  },
  "performance": {
    "preciseGpuPlacementTitle": "Sélection précise du GPU",
    "preciseGpuPlacementDescription": "Permet de choisir précisément le GPU des processus de rendu compatibles. Le changement en cours d’exécution dépend de leur compatibilité et reste configurable par application.",
    "preciseGpuPlacementModeOptions": {
      "basic": {
        "label": "Désactivé",
        "description": "Utiliser uniquement les préférences GPU prises en charge par Windows."
      },
      "precise": {
        "label": "Activé",
        "description": "Permettre de sélectionner un GPU précis pour un logiciel."
      }
    },
    "automaticMemoryCleanupTitle": "Seuils de nettoyage d’urgence",
    "automaticMemoryCleanupDescription": "Le nettoyage en arrière-plan suit les niveaux de pression sur les ressources. Ces seuils déterminent quand le faible niveau de mémoire physique ou de capacité d’engagement restante déclenche un nettoyage d’urgence. Ils ne s’appliquent que si le mode d’optimisation inclut la gestion mémoire.",
    "physicalMemoryAutomaticCleanupLabel": "Mémoire physique libre %",
    "virtualMemoryAutomaticCleanupLabel": "Marge d’engagement mémoire %",
    "memoryOptimizationTargetTitle": "Cible d’optimisation mémoire",
    "memoryOptimizationTargetDescription": "Au-dessus de la cible, la libération lente de niveau un continue sans déclencher de nettoyage d’urgence.",
    "physicalMemoryOptimizationTargetLabel": "Utilisation cible de mémoire physique %",
    "virtualMemoryOptimizationTargetLabel": "Utilisation cible de mémoire engagée %",
    "gpuOverflowTitle": "Seuils de débordement du GPU dédié",
    "gpuOverflowDescription": "Dès que l’un des seuils est atteint, les processus en arrière-plan à faible score passent du GPU dédié au GPU intégré. Nécessite un ordonnancement actif et un GPU intégré disponible.",
    "gpuOverflowUsageLabel": "Utilisation du GPU dédié %",
    "gpuOverflowDedicatedMemoryLabel": "Occupation de la mémoire dédiée du GPU %",
    "realtimeActionBudgetTitle": "Limite des actions en temps réel",
    "realtimeActionBudgetDescription": "Limitez les actions en temps réel par cycle à un nombre fixe ou à un pourcentage de tous les processus identifiés pendant ce cycle, arrondi au supérieur. Les deux valeurs sont conservées lors du changement de mode.",
    "realtimeActionBudgetModeOptions": [{"id":"fixed","label":"Nombre fixe"},{"id":"percent","label":"Pourcentage des processus"}],
    "realtimeActionBudgetCountLabel": "Nombre fixe d’actions",
    "realtimeActionBudgetPercentLabel": "Tous les processus %",
    "keepSmartRecordHistoryTitle": "Conserver l’historique de la planification intelligente",
    "keepSmartRecordHistoryDescription": "Lorsque cette option est activée, chaque enregistrement de la planification intelligente reste dans l’onglet Rapport des Détails après sa restauration, trié par numéro de séquence, sans limite. Lorsqu’elle est désactivée, les nouveaux enregistrements ne sont pas conservés après leur restauration ; l’historique déjà conservé n’est pas affecté.",
    "keepSmartRecordHistoryOptions": {
      "on": { "label": "Activé", "description": "Conserver les enregistrements dans l’historique après leur restauration." },
      "off": { "label": "Désactivé", "description": "Ne pas conserver les enregistrements après leur restauration." }
    },
    "refreshCadenceTitle": "Fréquence d’actualisation",
    "refreshCadenceDescription": "Définissez la vitesse de mise à jour des mesures et états. Une fréquence élevée consomme un peu plus de ressources.",
    "presetNumericModeOptions": [
      {
        "id": "aotu",
        "label": "Automatique",
        "description": "Utiliser le préréglage automatique intégré."
      },
      {
        "id": "preset",
        "label": "Fixer le préréglage",
        "description": "Toujours utiliser la fréquence du préréglage sélectionné."
      },
      {
        "id": "custom",
        "label": "Personnalisé",
        "description": "Toujours utiliser la valeur saisie en millisecondes."
      }
    ],
    "refreshCadencePresetLabel": "Préréglage",
    "refreshCadenceCustomLabel": "Personnalisé (ms)",
    "refreshCadencePresetOptions": [
      {
        "id": "responsive",
        "label": "Réactif",
        "description": "Privilégie les interactions au premier plan avec des mises à jour rapides."
      },
      {
        "id": "balanced",
        "label": "Équilibré",
        "description": "Une fréquence adaptée à l’utilisation quotidienne."
      },
      {
        "id": "lowPower",
        "label": "Économie d’énergie",
        "description": "Stratégie d’arrière-plan à faible consommation."
      },
      {
        "id": "quiet",
        "label": "Discret",
        "description": "Fréquence minimale tout en conservant les mises à jour nécessaires."
      }
    ],
    "refreshCadenceItems": {
      "monitor": {
        "label": "Mesures et barres de ressources",
        "description": "Actualise les valeurs du tableau de bord et les barres de ressources."
      },
      "resourceTable": {
        "label": "Tableau des ressources",
        "description": "Actualise la liste des ressources des logiciels et processus."
      },
      "localSystem": {
        "label": "Système local",
        "description": "Actualise les états locaux peu variables, comme la durée de fonctionnement."
      }
    }
  },
  "appearance": {
    "cacheDescription": "L\u2019apparence, les intervalles d\u2019actualisation et l\u2019apparence de la superposition sont mis en cache sur cet appareil. Effacer le cache r\u00E9tablit les valeurs par d\u00E9faut.",
    "resetCache": "R\u00E9initialiser les pr\u00E9f\u00E9rences d\u2019interface",
    "uiStyleTitle": "Style de l'interface",
    "uiStyleDescription": "Modifie les coins, les lignes, les ombres et les polices des contrôles ; la disposition ne change pas. Chaque style a ses propres palettes.",
    "themeTitle": "Palette",
    "themeDescription": "Choisissez une palette du style actuel. Système suit le réglage clair ou sombre de Windows.",
    "animationStyleTitle": "Style d’animation",
    "animationStyleDescription": "Choisit la manière dont les transitions se déroulent et dont les commandes se mettent en place. Très hautes performances supprime en outre les ombres, les reflets et le flou.",
    "animationStyleOptions": {
      "smooth": { "label": "Fluide", "description": "Comme macOS et les systèmes mobiles : mouvement à ressort qui conserve la vitesse du geste, peut être rattrapé à tout moment et rebondit en bord de défilement" },
      "desktop": { "label": "Bureau classique", "description": "Comme les logiciels de bureau Windows courants : transitions courtes et décélérées, sans vitesse conservée ni rebond" },
      "terminal": { "label": "Terminal par paliers", "description": "Atteint la fin en un nombre fixe de paliers, sans courbe d’accélération, comme le rafraîchissement d’un terminal en mode texte" },
      "none": { "label": "Aucune animation", "description": "Toutes les transitions et apparitions se terminent dans la même image ; l’apparence reste inchangée" },
      "ultra": { "label": "Très hautes performances", "description": "Aucune animation, sans ombres, reflets ni flou, pour un coût d’affichage minimal" }
    },
    "resourceBarFrameRateTitle": "Fréquence d’images des barres de ressources",
    "resourceBarFrameRateDescription": "Nombre d’images par seconde dessinées pendant les transitions des barres. Suivre l’écran utilise la fréquence de rafraîchissement du moniteur ; Personnalisé limite les images pour réduire la charge. Sans effet avec Aucune animation ou Très hautes performances.",
    "resourceBarFrameRateModeOptions": [
      {
        "id": "display",
        "label": "Suivre l’écran",
        "description": "Dessiner une image à chaque rafraîchissement de l’écran."
      },
      {
        "id": "custom",
        "label": "Personnalisé",
        "description": "Dessiner au plus le nombre d’images par seconde choisi (10–240)."
      }
    ],
    "resourceBarFrameRateCustomLabel": "Images/s (FPS)",
    "barColorTitle": "Couleurs des barres",
    "barColorDescription": "Utilisez des couleurs fixes par type de logiciel ou une couleur différente pour chaque logiciel.",
    "byteUnitTitle": "Unités de capacité",
    "byteUnitDescription": "1 GiB = 1024 MiB, 1 GB = 1000 MB. Le changement convertit aussi les valeurs numériques, pas seulement les unités.",
    "fontSmoothingTitle": "Lissage du texte",
    "fontSmoothingDescription": "Équilibrez automatiquement lisibilité et coût de l’interface, ou fixez un mode de rendu.",
    "languageTitle": "Langue de l’interface",
    "languageDescription": "Choisissez la langue de l’interface de Resource Manager. Système utilise la langue d’affichage de Windows.",
    "settingsLanguageTitle": "Langue de l’interface",
    "settingsLanguageDescription": "Change la langue de toute l’interface, y compris la zone de notification et le titre de fenêtre. Le mode système utilise la langue d’affichage Windows.",
    "languageSelectLabel": "Langue",
    "uiStyleOptions": {
      "standard": "Standard",
      "classic": "Classique",
      "soft": "Doux",
      "outline": "Contour",
      "terminal": "Terminal"
    },
    "themeOptions": {
      "system": "Système",
      "light": "Clair",
      "dark": "Sombre",
      "lowContrast": "Faible contraste",
      "paper": "Papier",
      "graphite": "Graphite",
      "blueprint": "Plan",
      "phosphor": "Phosphore",
      "amber": "Ambre",
      "ice": "Glace"
    },
    "barColorOptions": {
      "type": {
        "label": "Fixes par type",
        "description": "Couleur selon les logiciels système, adaptés et généraux ; séparateurs entre segments voisins"
      },
      "distinct": {
        "label": "Couleurs distinctes",
        "description": "Une couleur par logiciel, sans signification de type ni séparateurs"
      }
    },
    "byteUnitOptions": {
      "native": {
        "label": "Selon le matériel",
        "description": "GiB pour mémoire, VRAM et caches (puissances de deux) ; GB pour disques, fichiers et transferts (comme les fabricants)"
      },
      "binary": {
        "label": "Toujours GiB",
        "description": "Toujours diviser par 1024 ; unités KiB / MiB / GiB"
      },
      "decimal": {
        "label": "Toujours GB",
        "description": "Toujours diviser par 1000 ; unités kB / MB / GB"
      }
    },
    "fontSmoothingOptions": {
      "auto": {
        "label": "Automatique",
        "description": "Utiliser ClearType normalement et réduire le lissage en mode ultra-performance"
      },
      "system": {
        "label": "ClearType système",
        "description": "Rendu sous-pixel Windows natif pour le texte le plus net"
      },
      "grayscale": {
        "label": "Niveaux de gris",
        "description": "Anticrénelage en niveaux de gris, plus doux, adapté à certains écrans haute résolution"
      },
      "disabled": {
        "label": "Désactivé",
        "description": "Désactiver le lissage supplémentaire et rasteriser directement les glyphes"
      }
    },
  },
  "systemIntegration": {
    "crashRecoveryTitle": "Redémarrer après un plantage",
    "crashRecoveryDescription": "Redémarrer automatiquement le service en arrière-plan s’il plante. Un arrêt normal ne déclenche pas de redémarrage.",
    "crashRestartDelayTitle": "Délai avant redémarrage (secondes)",
    "crashRestartCountTitle": "Redémarrages autorisés",
    "crashCounterResetDescription": "Jours sans plantage avant remise à zéro du compteur : {days}. Une fois la limite atteinte, redémarrez le service manuellement.",
    "crashCounterResetTitle": "Période de remise à zéro (jours)",
    "crashRecoveryActualTitle": "Configuration actuelle",
    "crashRecoveryUnavailable": "La configuration de récupération automatique est indisponible.",
    "crashRecoveryApplied": "La configuration de récupération automatique est appliquée.",
    "crashRecoverySkipped": "La récupération automatique ne s’applique pas à ce mode d’exécution.",
    "crashRecoveryFailed": "La configuration de récupération automatique n’a pas pu être appliquée.",
    "crashRecoveryMismatch": "La configuration actuelle diffère de vos réglages.",
    "crashRecoveryNonCrashWarning": "La configuration actuelle redémarre aussi après un arrêt normal signalant une erreur.",
    "crashRecoveryWriteFailed": "Impossible d’enregistrer la configuration de récupération automatique.",
    "crashRecoveryReadFailed": "Impossible de lire la configuration de récupération automatique.",
    "taskManagerTitle": "Remplacement du raccourci du Gestionnaire des tâches",
    "autoStartTitle": "Démarrer le service au démarrage",
    "autoStartDescription": "Démarrer automatiquement le service en arrière-plan au démarrage de Windows.",
    "loginStartTitle": "Ouvrir dans la zone de notification à la connexion",
    "loginStartDescription": "Lancer l’interface à la connexion de cet utilisateur. Le service se configure séparément.",
    "loginStartUnavailable": "Impossible de lire ou d’enregistrer le démarrage à la connexion de l’utilisateur actuel.",
    "loginStartLoading": "Lecture du démarrage à la connexion…",
    "loginStartWriteFailed": "Impossible d’enregistrer le démarrage à la connexion. L’interrupteur affiche l’état relu ensuite.",
    "loginStartRetry": "Réessayer",
    "taskManagerDescription": "Une fois activé, toute ouverture du Gestionnaire des tâches ouvre Resource Manager à la place (il démarre s’il est arrêté, sinon sa fenêtre principale s’affiche) : Ctrl+Maj+Échap, le menu de la barre des tâches, « Gestionnaire des tâches » sur l’écran Ctrl+Alt+Suppr et l’exécution de taskmgr.exe. L’écran Ctrl+Alt+Suppr lui-même n’est pas modifié. Le remplacement doit être enregistré avec des droits d’administrateur ; sans enregistrement, seul Ctrl+Maj+Échap est remplacé, et uniquement pendant l’exécution de Resource Manager. La désactivation supprime uniquement la valeur de registre écrite par Resource Manager.",
    "forceTerminateTitle": "Raccourci d’arrêt forcé",
    "forceTerminateDescription": "Arrêter l’application au premier plan et toutes celles avec une fenêtre visible qui ne répond plus. Désactivé par défaut.",
    "forceTerminateWarning": "Ce raccourci arrête immédiatement les processus et perd les travaux non enregistrés. Il doit contenir Ctrl, Alt ou Win avec une touche non modificatrice.",
    "hotkeyEmpty": "Aucune touche configurée",
    "hotkeyAddKey": "Ajouter une touche",
    "hotkeyKeyLabel": "Touche",
    "hotkeyRemoveKey": "Retirer la touche",
    "hotkeyUnordered": "Appuyer simultanément",
    "hotkeyOrdered": "Touche précédente d’abord",
    "publicServiceTitle": "Service local partagé",
    "publicServiceDescription": "Autoriser les logiciels locaux à utiliser les fonctions partagées de Resource Manager.",
    "publicFileIndexTitle": "Index partagé des fichiers logiciels",
    "publicFileIndexDescription": "Réutiliser l’index existant de Resource Manager sans nouvelle analyse lors des requêtes.",
    "publicDatabaseServiceTitle": "Service de bases de données SQLite",
    "publicDatabaseServiceDescription": "Fournir aux programmes locaux des bases nommées isolées, des requêtes paramétrées et des transactions groupées.",
    "publicAiModelCatalogTitle": "Catalogue unifié de modèles IA",
    "publicAiModelCatalogDescription": "Resource Manager gère le catalogue et la planification des modèles ; LM Studio les exécute.",
    "lmStudioEndpointTitle": "Adresse de LM Studio",
    "lmStudioEndpointDescription": "Seules les adresses de cet ordinateur sont acceptées. Les autres applications passent par Resource Manager.",
    "lmStudioAutoStartTitle": "Lancer LM Studio à la demande",
    "lmStudioAutoStartDescription": "Lancer LM Studio lorsqu’un logiciel demande un modèle et qu’il est arrêté ; aucun modèle n’est chargé automatiquement.",
    "aiGatewayTitle": "Clés de compatibilité IA locale",
    "aiGatewayDescription": "Créer des clés compatibles OpenAI ou Anthropic pour les logiciels sans prise en charge directe de LM Studio. Les requêtes vont uniquement aux modèles ouverts exécutés sur ce PC.",
    "aiGatewayOpenAiProfile": "Compatible OpenAI",
    "aiGatewayAnthropicProfile": "Compatible Anthropic",
    "aiGatewayNamePlaceholder": "Nom d’usage (facultatif)",
    "aiGatewayGenerate": "Créer une clé",
    "aiGatewayGenerating": "Traitement en cours",
    "aiGatewayOneTimeTitle": "Clé créée",
    "aiGatewayOneTimeDescription": "La clé ne s’affiche qu’une fois. Enregistrez-la maintenant, car elle ne pourra plus être consultée.",
    "aiGatewayApiKeyLabel": "Clé API",
    "aiGatewayBaseUrlLabel": "URL de base",
    "aiGatewayCopy": "Copier",
    "aiGatewayRevoke": "Révoquer la clé",
    "aiGatewayEmpty": "Aucune clé de compatibilité n’a été créée.",
    "aiGatewayLoadFailed": "Impossible de charger les clés IA locales",
    "trustedAccess": {
      "title": "Clés d’accès approuvées",
      "description": "Gérez les clés d’accès générales et les demandes d’autorisation à usage unique.",
      "loading": "Chargement des clés d’accès…",
      "loadFailed": "Impossible de charger les clés d’accès approuvées.",
      "actionFailed": "L’opération d’accès approuvé a échoué.",
      "retry": "Réessayer",
      "failureReasons": {
        "unauthorized": "Le service local n’accepte pas la clé de cette interface : la clé par défaut est peut-être désactivée, tout juste renouvelée ou invalide. Vous pouvez réessayer ; si la clé par défaut est désactivée, réactivez-la en tant qu’administrateur.",
        "scope-required": "La clé actuelle n’a pas la portée « Gérer les clés d’accès » ; cette action n’est pas autorisée.",
        "default-scope-denied": "Modifier la clé par défaut exige une clé disposant de toutes les portées par défaut, ou des droits administrateur."
      },
      "defaultTitle": "Fichier de la clé par défaut native",
      "defaultDescription": "La clé par défaut est enregistrée dans le fichier ci-dessous, pour les programmes qui doivent accéder au service local. L’interface de Resource Manager s’exécute en tant qu’administrateur et n’utilise pas la clé par défaut.",
      "defaultEnabled": "Activée",
      "defaultDisabled": "Désactivée",
      "defaultKeyName": "Clé par défaut",
      "defaultFilePath": "Emplacement du fichier",
      "defaultFileLocation": "<dossier d’installation>\\UserData\\Security\\default-key",
      "defaultSecretLocalOnly": "La clé elle-même n’est pas affichée ici.",
      "defaultAccessLossWarning": "Désactiver la clé par défaut la révoque immédiatement, et les programmes qui l’utilisent perdent l’accès. La rotation invalide l’ancienne clé ; les programmes qui l’utilisent doivent relire le fichier de la clé par défaut.",
      "defaultDisabledRecovery": "La clé par défaut est désactivée. Les requêtes des programmes qui l’utilisent sont refusées jusqu’à sa réactivation.",
      "enableDefault": "Réactiver la clé par défaut",
      "disableDefault": "Désactiver et révoquer la clé par défaut",
      "regenerateDefault": "Régénérer la clé par défaut",
      "confirmDisableTitle": "Désactiver et révoquer la clé par défaut ?",
      "confirmDisableMessage": "La désactivation révoque immédiatement la clé par défaut. Les programmes qui l’utilisent perdent l’accès jusqu’à la réactivation de la clé par défaut.",
      "confirmRegenerateTitle": "Régénérer la clé par défaut ?",
      "confirmRegenerateMessage": "L’ancienne clé par défaut cesse immédiatement de fonctionner. Les programmes qui l’utilisent doivent relire le fichier de la clé par défaut ; d’ici là, leurs requêtes sont refusées.",
      "confirmRotateTitle": (name: string) => `Renouveler « ${name} » ?`,
      "confirmRotateMessage": "L’ancienne clé cesse aussitôt de fonctionner et les programmes qui l’utilisent sont refusés jusqu’à ce qu’ils passent à la nouvelle clé. La nouvelle clé n’est affichée qu’une fois.",
      "confirmRevokeTitle": (name: string) => `Révoquer « ${name} » ?`,
      "confirmRevokeMessage": "La clé cesse aussitôt de fonctionner et les programmes qui l’utilisent sont refusés. Cette action est irréversible.",
      "createTitle": "Créer une clé indépendante",
      "createDescription": "Choisissez ses portées. Une nouvelle clé est affichée une seule fois et peut être renouvelée ou révoquée séparément.",
      "nameLabel": "Nom",
      "namePlaceholder": "Par exemple, script local",
      "createScopesLabel": "Portées accordées",
      "noScopesAvailable": "Aucune portée n’est disponible pour créer une clé.",
      "create": "Générer une clé indépendante",
      "working": "Traitement…",
      "oneTimeTitle": "Clé indépendante générée",
      "oneTimeDescription": "Copiez-la et enregistrez-la maintenant. Elle ne pourra plus être affichée.",
      "oneTimeAnnouncement": "Clé générée. Copiez-la et enregistrez-la maintenant.",
      "apiKeyLabel": "Clé",
      "copy": "Copier",
      "copied": "Copié",
      "copyFailed": "Impossible de copier la clé.",
      "dismissSecret": "Masquer la clé à usage unique",
      "createdAt": "Créée le",
      "keysTitle": "Clés indépendantes",
      "keysEmpty": "Aucune clé indépendante n’a été créée.",
      "revoke": "Révoquer",
      "rotate": "Renouveler",
      "rotateKeyLabel": (name: string) => `Renouveler « ${name} »`,
      "revokeKeyLabel": (name: string) => `Révoquer « ${name} »`,
      "requestsTitle": "Demandes d’autorisation à usage unique",
      "requestsDescription": "Les demandes des programmes tiers affichent leur nom choisi et les portées demandées.",
      "reviewRequests": "Examiner les demandes",
      "requestsLoading": "En attente de demandes d’autorisation…",
      "requestsUnavailable": "L’abonnement aux demandes d’autorisation est indisponible ; aucune nouvelle demande ne peut arriver pour l’instant.",
      "requestsLoadFailed": "Impossible de lire les demandes d’autorisation en attente.",
      "requestsStale": "La liste des demandes est peut-être obsolète pendant la reconnexion de l’abonnement.",
      "requestsEmpty": "Aucune demande n’est en attente.",
      "requestNamed": (name: string) => `Nom de la demande : ${name}`,
      "requestCount": (count: number) => `Demandes en attente : ${count}`,
      "expiresAt": "Expire le",
      "observedExecutable": "Chemin d’exécutable observé",
      "observedProcess": "PID",
      "submittedAt": "Envoyée le",
      "observedCallerIsInformational": "Le chemin et le PID observés sont des informations indicatives, pas l’identité autorisée.",
      "requestScopesLabel": "Portées demandées",
      "requestUnavailableScopeHelp": "Les portées hors de vos droits actuels ne sont pas sélectionnables. Seules les portées demandées que vous pouvez accorder seront soumises.",
      "administratorHolderScope": "Les programmes utilisant cette clé doivent s’exécuter en tant qu’administrateur",
      "approve": "Approuver les portées sélectionnées",
      "deny": "Refuser",
      "denyReasonLabel": "Motif du refus",
      "requestActionFailed": "Impossible de transmettre la décision d’autorisation.",
      "requestApproved": "Approuvée. La clé a été remise au programme demandeur.",
      "requestDenied": "Refusée.",
      "requestDeniedWithReason": (reason: string) => `Refusée (motif : ${reason}).`,
      "requestExpired": "La demande a expiré.",
      "requestOutcome": (name: string, result: string) => `${name} : ${result}`,
      "denialReasons": {
        "not-approved": "Non approuvée",
        "unsupported": "Non prise en charge",
        "duplicate": "Demande en double",
        "not-needed": "Non nécessaire"
      },
      "scopeLabels": {
        "read": "Lire l’API",
        "metrics.read": "Lire les métriques",
        "processes.read": "Lire les processus",
        "session": "Actions de session",
        "adapters.register.self": "Enregistrer les adaptateurs de cet utilisateur",
        "desktop.publish": "Publier les observations du bureau",
        "gpu.resolve": "Résoudre le placement GPU",
        "gpu.report": "Signaler les résultats de lancement GPU",
        "keys.manage": "Gérer les clés d’accès",
        "machine.write": "Modifier les paramètres de la machine",
        "injection.configure": "Configurer l’injection au démarrage"
      },
      "unknownScope": "Portée inconnue (cette version ne peut pas la décrire ; elle ne peut pas être accordée ici)"
    }
  },
  "debug": {
    "debugModeTitle": "Mode de débogage",
    "debugModeDescription": "Afficher et activer les diagnostics locaux. N’activez cette option que pour analyser un problème.",
    "debugLogTitle": "Enregistrer les journaux de débogage",
    "debugLogDescription": "Enregistrer les informations utiles au diagnostic. Cela peut utiliser un peu d’espace disque et de temps processeur.",
    "hostManagerSmartCoordinatorScoreOnlyTitle": "Analyser sans appliquer de changements",
    "hostManagerSmartCoordinatorScoreOnlyDescription": "Continuer la surveillance et le calcul de planification sans modifier l’état logiciel ou matériel.",
    "hostManagerSmartCoordinatorPerformanceLogTitle": "Enregistrer les performances de planification",
    "hostManagerSmartCoordinatorPerformanceLogDescription": "Enregistrer les durées et la charge de planification pour analyser le coût propre à Resource Manager."
  },
  "hardwareControl": {
    "keepAliveTitle": "Maintenir les réglages appliqués",
    "keepAliveDescription": "Désactivé, les réglages enregistrés sur la page Contrôle ne sont appliqués qu’une fois, au démarrage de Resource Manager. Activé, ils sont réappliqués selon l’intervalle ci-dessous, afin que Windows, les pilotes ou d’autres programmes ne les laissent pas modifiés.",
    "reassertionTitle": "Intervalle de réapplication",
    "reassertionDescription": "Tant que « Maintenir les réglages appliqués » est activé, les réglages sont réappliqués après chaque intervalle."
  },
  "performanceOverlay": {
    "intro": "Liste les logiciels dont la superposition est activée. Les paramètres enregistrés sont affichés, et non l’état d’affichage actuel de la superposition.",
    "loading": "Lecture des paramètres de superposition",
    "loadFailed": "Échec de la lecture des paramètres de superposition",
    "emptyTitle": "Aucun logiciel n’a de superposition activée",
    "emptyDetail": (tab: string) => `Pour l’activer, ouvrez les détails du logiciel puis l’onglet « ${tab} ».`,
    "metricCount": (count: number) => plural("fr-FR", count, { one: "{count} métrique", many: "{count} métriques", other: "{count} métriques" }),
    "position": (anchor: string) => `Position : ${anchor}`,
    "open": "Ouvrir les paramètres de superposition",
    "openLabel": (name: string) => `Ouvrir les paramètres de superposition de ${name}`,
    "softwareMissing": "Ce logiciel ne figure plus dans la liste des logiciels ; ses détails ne peuvent pas être ouverts."
  },
  "credits": {
    "heroTitle": "Merci à chaque personne et projet qui rend Resource Manager possible",
    "heroBody": "Merci à Microsoft, aux équipes de Windows, .NET et WebView2 et à toute la communauté de développement. Leurs années de travail fournissent des fondations qu’une personne seule ne pourrait construire. Chaque contribution compte, même sans obligation d’attribution. Resource Manager est développé indépendamment ; ces remerciements ne constituent ni signature, ni certification, ni approbation des projets cités.",
    "dependencyListTitle": "Remerciements complets aux dépendances",
    "dependencyListBody": "Merci à tous les auteurs et mainteneurs ci-dessous. La liste couvre le fichier de verrouillage frontal et les paquets .NET résolus, y compris compilation, tests et dépendances facultatives. Leur présence ne signifie pas qu’ils sont tous chargés ou distribués sur cet ordinateur.",
    "groups": {
      "project": "Projet et contribution",
      "runtime": "Runtime et outils de build",
      "windows": "Interfaces Windows et diagnostics",
      "data": "Données hors ligne",
      "build": "Outils de compilation",
      "hardware": "Surveillance matérielle et prise en charge optionnelle"
    },
    "roles": {
      "project": "Projet",
      "frontendFramework": "Framework de l’interface",
      "icons": "Icônes de l’interface",
      "serialization": "Dépendances de sérialisation Solid",
      "charts": "Graphiques des résultats",
      "webView": "Conteneur web de bureau",
      "database": "Base de données locale",
      "nativeCompiler": "Compilateur du cœur natif",
      "softwareCatalog": "Sources de métadonnées logicielles",
      "buildToolchain": "Chaîne de compilation frontale",
      "typeSystem": "Système de types frontal",
      "buildRuntime": "Exécution de compilation et définitions de types",
      "dotnetRuntime": "Environnement d’exécution local",
      "hookLibrary": "Compatibilité Windows",
      "startupInjectionLibrary": "Compatibilité au lancement des logiciels",
      "traceLibrary": "Analyse des performances système",
      "windowsManagement": "Informations système Windows",
      "nvidiaTelemetry": "Télémétrie des GPU NVIDIA",
      "nvidiaExtension": "Interface d’extension GPU NVIDIA",
      "amdTelemetry": "Télémétrie GPU/iGPU AMD",
      "signedHardwareAccess": "Accès matériel signé",
      "hardwareBridge": "Passerelle de capteurs matériels",
      "externalReference": "Référence externe / aide facultative",
      "deviceIdDatabase": "Base de noms d’identifiants matériels"
    },
    "linkLabels": {
      "official": "Site officiel",
      "github": "GitHub",
      "docs": "Documentation",
      "nuget": "NuGet",
      "npm": "npm",
      "license": "Licence"
    },
    "sourceLabel": "Source",
    "dependencyColumns": {
      "name": "Nom",
      "version": "Version",
      "license": "Licence",
      "source": "Source"
    }
  }
});
