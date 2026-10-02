import { createSettingsLocale } from "../settingsLocaleFactory.ts";

export default createSettingsLocale("fr-FR", {
  "updates": {
    "autoUpdateTitle": "Mises à jour automatiques",
    "autoUpdateDescription": "Enregistre la préférence de mise à jour. L’installation automatique attend une signature de publication fiable ; les mises à jour manuelles sont disponibles.",
    "installedVersionTitle": "Version installée",
    "unknownVersion": "Inconnu",
    "checking": "Vérification…",
    "checkForUpdates": "Rechercher des mises à jour",
    "catalogFailed": "Impossible de lire le catalogue : ",
    "historyIncomplete": "L’historique des versions est incomplet.",
    "historyStale": "Le catalogue précédent est affiché ; reconnectez-vous puis réessayez.",
    "versionsTitle": "Versions du produit",
    "versionsDescription": "Les plus récentes d’abord ; chaque série affiche sa dernière version par défaut.",
    "lastChecked": "Dernière vérification : ",
    "selectedTarget": "Version cible : ",
    "prepare": "Préparer et installer la mise à jour choisie",
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
      "无法确认当前安装版本，升级选择已关闭。": "Version installée inconnue ; sélection de mise à niveau désactivée.",
      "只能选择高于当前安装版本的发行版。": "Seules les versions plus récentes que celle installée peuvent être sélectionnées."
    }
  },
  "navigationLabel": "Rubriques des paramètres",
  "sections": {
    "performance": "Performances",
    "appearance": "Apparence",
    "systemIntegration": "Intégration système",
    "updates": "Mises à jour et versions",
    "debug": "Débogage",
    "credits": "Crédits"
  },
  "saveState": {
    "saving": "Enregistrement",
    "saved": "Enregistré",
    "constrained": "Enregistré ; les options indisponibles ont été limitées",
    "partial": "Enregistré, mais pas entièrement appliqué",
    "error": "Échec de l'enregistrement",
    "dirty": "Non enregistré",
    "conflict": "Paramètres modifiés ailleurs ; rechargez avant d’enregistrer"
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
    "reapply": "Appliquer à nouveau"
  },
  "performance": {
    "smartMonitoringTitle": "Surveillance à la demande",
    "smartMonitoringDescription": (seconds: number) => `Surveille uniquement quand les données sont utilisées, puis s’arrête après ${seconds} secondes d’inactivité.`,
    "adaptiveBooleanModeOptions": [
      {
        "id": "auto",
        "label": "Automatique",
        "description": "Choisir automatiquement selon le mode de performance actuel."
      },
      {
        "id": "enabled",
        "label": "Toujours activé",
        "description": "Toujours activer ce paramètre."
      },
      {
        "id": "disabled",
        "label": "Toujours désactivé",
        "description": "Toujours désactiver ce paramètre."
      }
    ],
    "gpuPerformanceUseCasesTitle": "Usages du GPU",
    "gpuPerformanceUseCasesDescription": "Sélectionnez les usages principaux pour mieux comparer les capacités des GPU. Plusieurs choix sont possibles.",
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
    "automaticMemoryCleanupTitle": "Seuil de nettoyage automatique",
    "automaticMemoryCleanupDescription": "Démarrer le nettoyage en arrière-plan lorsque les ressources sont faibles et qu’un déplacement sûr ne peut continuer.",
    "physicalMemoryAutomaticCleanupLabel": "Mémoire physique libre %",
    "virtualMemoryAutomaticCleanupLabel": "Marge d’engagement mémoire %",
    "memoryOptimizationTargetTitle": "Cible d’optimisation mémoire",
    "memoryOptimizationTargetDescription": "Au-dessus de la cible, la libération lente de niveau un continue sans déclencher de nettoyage d’urgence.",
    "physicalMemoryOptimizationTargetLabel": "Utilisation cible de mémoire physique %",
    "virtualMemoryOptimizationTargetLabel": "Utilisation cible de mémoire engagée %",
    "pauseHiddenTitle": "Actualisation lorsque la fenêtre est masquée",
    "pauseHiddenDescription": "En mode normal, les mises à jour de l’interface peuvent s’arrêter lorsque la fenêtre est masquée et reprendre dès son affichage.",
    "frontendHiddenRefreshModeOptions": [
      {
        "id": "auto",
        "label": "Automatique",
        "description": "Décider automatiquement selon le mode de performance actuel."
      },
      {
        "id": "pauseWhenHidden",
        "label": "Suspendre si masquée",
        "description": "Suspendre les mises à jour de l’interface lorsque la fenêtre est masquée."
      },
      {
        "id": "continueWhenHidden",
        "label": "Continuer l’actualisation",
        "description": "Continuer les mises à jour de l’interface lorsque la fenêtre est masquée."
      }
    ],
    "refreshCadenceTitle": "Fréquence d’actualisation",
    "refreshCadenceDescription": "Définissez la vitesse de mise à jour des mesures et états. Une fréquence élevée consomme un peu plus de ressources.",
    "presetNumericModeOptions": [
      {
        "id": "aotu",
        "label": "Automatique",
        "description": "Choisir une fréquence selon le mode de performance et l’état de la fenêtre."
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
      "management": {
        "label": "Composants et logiciels",
        "description": "Actualise l’état des composants, les listes de logiciels et la progression des opérations."
      },
      "discovery": {
        "label": "Migration des logiciels",
        "description": "Actualise la progression des migrations et le contenu disponible."
      },
      "optimization": {
        "label": "Optimisation des performances",
        "description": "Actualise les rapports d’optimisation et l’état de l’ordonnancement."
      },
      "localSystem": {
        "label": "Système local",
        "description": "Actualise les états locaux peu variables, comme la durée de fonctionnement."
      }
    }
  },
  "appearance": {
    "themeTitle": "Mode de couleur",
    "themeDescription": "Choisissez une apparence système, claire, sombre ou à faible contraste.",
    "animationTitle": "Animations",
    "animationDescription": "Choisissez des animations complètes, aucune animation ou des effets simplifiés pour réduire le coût de l’interface.",
    "resourceBarHardwareAccelerationTitle": "Accélération matérielle des barres",
    "resourceBarHardwareAccelerationDescription": "Utilisez la carte graphique pour améliorer l’animation des barres. Désactivez cette option en cas de problème d’affichage.",
    "resourceBarHardwareAccelerationModeOptions": [
      {
        "id": "auto",
        "label": "Automatique",
        "description": "Conserver la fluidité au premier plan et réduire le coût en arrière-plan à faible consommation."
      },
      {
        "id": "enabled",
        "label": "Toujours activé",
        "description": "Toujours utiliser l’accélération matérielle pour les barres."
      },
      {
        "id": "disabled",
        "label": "Toujours désactivé",
        "description": "Toujours utiliser le rendu de compatibilité pour les barres."
      }
    ],
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
    "themeOptions": {
      "system": "Système",
      "light": "Clair",
      "dark": "Sombre",
      "lowContrast": "Faible contraste"
    },
    "animationOptions": {
      "auto": {
        "label": "Automatique",
        "description": "Utiliser les animations normales au premier plan et l’apparence à très hautes performances en arrière-plan à faible consommation"
      },
      "normal": {
        "label": "Normal motion",
        "description": "Enable restrained global transitions"
      },
      "none": {
        "label": "No motion",
        "description": "Désactiver les transitions et animations tout en conservant l’aspect soigné"
      },
      "ultra": {
        "label": "Ultra performance",
        "description": "Désactiver les animations, ombres, reflets et flous pour réduire au minimum le coût d’affichage"
      }
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
    "gpuPerformanceUseCaseOptions": {
      "general": {
        "label": "Général",
        "description": "Toujours ajouter un petit bonus de génération au score de rastérisation"
      },
      "ai": {
        "label": "AI",
        "description": "Renforcer la pondération de génération, de l’écosystème NVIDIA, de la capacité VRAM et de la bande passante"
      },
      "gaming": {
        "label": "Jeux",
        "description": "Appliquer le plus grand bonus de génération et un petit bonus de capacité VRAM"
      }
    }
  },
  "systemIntegration": {
    "taskManagerTitle": "Remplacement du raccourci du Gestionnaire des tâches",
    "autoStartTitle": "Démarrer avec Windows",
    "autoStartDescription": "Lancer le service au démarrage et rester dans la zone de notification après la connexion.",
    "taskManagerDescription": "Remplacer Ctrl+Maj+Échap : après enregistrement, Resource Manager démarre s’il est arrêté, sinon sa fenêtre apparaît au premier plan. Ctrl+Alt+Suppr n’est pas modifié.",
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
    "aiGatewayLoadFailed": "Impossible de charger les clés IA locales"
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
      "etwToolkit": "Outils d’analyse des performances Windows",
      "nvidiaTelemetry": "Télémétrie des GPU NVIDIA",
      "nvidiaExtension": "Interface d’extension GPU NVIDIA",
      "amdTelemetry": "Télémétrie GPU/iGPU AMD",
      "amdCpuSdk": "SDK de capteurs CPU AMD",
      "intelTelemetry": "Télémétrie CPU / MSR Intel",
      "amdSmuBoundary": "Limite d’accès au SMU AMD",
      "hardwareBridge": "Passerelle de capteurs matériels",
      "notebookEc": "Ventilateurs de portables",
      "externalReference": "Référence externe / aide facultative",
      "latencyDiagnostics": "Aide au diagnostic de latence",
      "deviceIdDatabase": "Base de noms d’identifiants matériels"
    },
    "notes": {
      "windowsFoundation": "Merci à Microsoft et aux équipes Windows pour Win32, Shell, COM, Direct3D/DXGI, PDH, ETW, services, cryptographie et réseau fournis par le système.",
      "microsoftTools": "Merci aux auteurs et mainteneurs de .NET SDK, MSBuild, NuGet et PowerShell pour compilation, publication, gestion des paquets et automatisation.",
      "managedServices": "Merci à Microsoft et aux contributeurs .NET pour les API de base et l’intégration des services. TypeExtensions est un paquet de substitution résolu pour cette cible, pas une DLL supplémentaire.",
      "nativeToolchain": "Merci aux auteurs et mainteneurs GCC et MinGW pour la compilation et l’exécution natives. Certains composants lient statiquement libgcc/libstdc++ ; la redistribution fait l’objet d’un examen distinct.",
      "buildContributors": "Merci aux auteurs et mainteneurs des dépendances de compilation : outils Solid, cartes de sources, données de navigateurs et petites bibliothèques. La liste complète conserve chaque paquet et version verrouillés, y compris les plateformes facultatives.",
      "testContributors": "Merci aux contributeurs Playwright, xUnit.net, Coverlet et Microsoft pour l’automatisation des navigateurs, les tests de régression et la couverture. Ce sont des outils de développement, pas des dépendances d’exécution du produit.",
      "openHardwareMonitor": "Merci aux auteurs et contributeurs OpenHardwareMonitor pour les interfaces de capteurs. Un fournisseur externe installé peut fournir des données WMI ; cela n’implique pas son inclusion.",
      "upstreamContributors": "Merci à tous les auteurs des notices originales .NET et WebView2, notamment ANTLR, Unicode, Mono, Brotli et LLVM. Les originaux complets sont conservés ; ils ne constituent pas un inventaire des DLL en cours d’exécution.",
      "resourceManager": "Copyright (c) 2026 BIOcanse. Ce projet utilise Apache-2.0. Les composants tiers conservent leurs propres licences.",
      "solid": "Fournit le modèle de composants réactifs de l’interface web légère actuelle.",
      "lucide": "L’interface utilise lucide-solid et conserve la licence ISC de Lucide ainsi que la licence MIT et les mentions des icônes dérivées de Feather.",
      "seroval": "Dépendances transitives de Solid ; licence MIT et attribution Alexis Munsayac conservées.",
      "webView2": "Le conteneur de bureau utilise le SDK WebView2. Microsoft distribue séparément le runtime système WebView2 Evergreen selon ses propres conditions.",
      "sqlite": "Stockage local. Microsoft.Data.Sqlite utilise MIT et SQLitePCLRaw 3.x Apache-2.0. Le cœur SQLite distribué par SourceGear est placé dans le domaine public par le projet amont.",
      "zig": "Compile le cœur natif ; le compilateur n’est pas nécessaire pour exécuter l’application.",
      "softwareCatalog": "Le catalogue hors ligne utilise les métadonnées WinGet sous MIT et les données structurées Wikidata sous CC0. Les résumés et traductions chinoises sont rédigés par le projet.",
      "vite": "Gère les compilations de développement et le conditionnement du frontend Solid.",
      "typescript": "Améliore la fiabilité du code d’interface et des modèles de paramètres.",
      "node": "Prend en charge les compilations frontales et les outils de développement.",
      "dotnet": "Fournit l’environnement d’exécution local de Resource Manager.",
      "minHook": "Merci à Tsuda Kageyu, aux contributeurs et à l’auteur HDE Vyacheslav Patkov pour les hooks et le décodage d’instructions. Licences et attributions originales conservées intégralement.",
      "detours": "Projet libre de Microsoft Research utilisé pour la compatibilité au lancement. Licence et avis de droit d’auteur originaux conservés.",
      "traceEvent": "Donne accès aux informations de performances Windows.",
      "systemManagement": "Donne accès aux informations système et matérielles Windows.",
      "wpt": "Outils Microsoft de diagnostic des performances Windows installés séparément ; non inclus dans l’application de base.",
      "nvml": "Fournit fréquence, mémoire, puissance et température des GPU NVIDIA.",
      "nvapi": "Ajoute les informations de ventilation, refroidissement et alimentation des GPU NVIDIA.",
      "adlx": "Fournit utilisation, fréquence, puissance, température et tension des GPU AMD.",
      "ryzenMaster": "Ajoute puissance, tension, courant et température des processeurs AMD ; installation et licence nécessitent une confirmation explicite.",
      "intelPcm": "Ajoute puissance, fréquence et température des processeurs Intel.",
      "pawnIo": "Pilote officiellement signé pour lire uniquement la télémétrie de la table AMD SMU PM ; jamais installé sans avertissement.",
      "libreHardwareMonitor": "Fournit en option ventilateurs CPU/système, températures mémoire, carte mère, VRM, chipset et mesures de tension.",
      "notebookFanControl": "Voie facultative lorsque les moniteurs matériels et pilotes GPU ne fournissent pas les mesures des ventilateurs du portable.",
      "afterburner": "Utilisé comme référence externe et aide facultative, pas comme dépendance d’exécution de base.",
      "latencyMon": "Aide au diagnostic ISR, DPC, défauts de page matériels et latence des pilotes comme outil de comparaison.",
      "usbIds": "Fournit hors ligne les noms de fabricants et appareils USB VID/PID ; chargé seulement à la demande des détails.",
      "pciIds": "Fournit hors ligne les noms de fabricants, appareils et sous-systèmes PCI VEN/DEV/SUBSYS ; chargé seulement à la demande des détails."
    },
    "linkLabels": {
      "official": "Website",
      "github": "GitHub",
      "docs": "Docs",
      "license": "Licence",
      "nuget": "NuGet",
      "gpuOpen": "GPUOpen",
      "eula": "EULA",
      "runtime": "Environnement d’exécution",
      "aspnet": "ASP.NET Core",
      "vite": "Vite",
      "solidPlugin": "Solid plugin"
    }
  }
});
