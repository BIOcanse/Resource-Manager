import { createSettingsLocale } from "../settingsLocaleFactory.ts";

export default createSettingsLocale("de-DE", {
  "updates": {
    "autoUpdateTitle": "Automatische Updates",
    "autoUpdateDescription": "Speichert die Update-Präferenz. Automatische Installation wartet auf vertrauenswürdige Release-Signaturen; manuelle Updates sind verfügbar.",
    "installedVersionTitle": "Installierte Version",
    "unknownVersion": "Unbekannt",
    "checking": "Wird geprüft…",
    "checkForUpdates": "Nach Updates suchen",
    "historyIncomplete": "Der Versionsverlauf ist unvollständig.",
    "historyStale": "Der vorherige Release-Katalog wird angezeigt; Verbindung herstellen und erneut versuchen.",
    "versionsTitle": "Produktversionen",
    "versionsDescription": "Neueste zuerst; jede Reihe zeigt standardmäßig ihre neueste Version.",
    "lastChecked": "Zuletzt geprüft: ",
    "selectedTarget": "Gewählte Zielversion: ",
    "prepare": "Gewähltes Update vorbereiten und installieren",
    "confirmPrepare": (version: string) => `Version ${version} vorbereiten? Der Updater prüft das Paket, wartet auf das Beenden aller Desktopsitzungen, stoppt den Dienst, erhält und migriert die Daten und prüft die neue Version. Bei einem Fehler stellt er die vorherige Version wieder her.`,
    "prepareFailed": "Update konnte nicht vorbereitet werden.",
    "waitingForExit": "Update bereit. Beenden Sie alle Resource-Manager-Desktop-Sitzungen über den Infobereich, um es anzuwenden.",
    "stageLabel": "Update-Status",
    "unknownStage": "Unbekannter Update-Status",
    "stages": {
      "idle": "Bereit",
      "checking": "Updates werden gesucht",
      "downloading": "Paket wird heruntergeladen",
      "verifying": "Paket wird geprüft",
      "waitingForExit": "Warten auf Ende der Desktop-Sitzungen",
      "completed": "Update abgeschlossen",
      "error": "Update fehlgeschlagen"
    },
    "otherVersions": "Weitere Versionen",
    "installedUnknown": "Installierte Version unbekannt; die Upgrade-Reihenfolge kann nicht geprüft werden.",
    "stable": "Stabil",
    "preview": "Vorschau",
    "noLatest": "Keine neueste Version verfügbar.",
    "noStable": "Keine stabile Version verfügbar.",
    "latest": "Neueste Version",
    "latestStable": "Neueste stabile Version",
    "verified": "Verifizierte Version",
    "unavailableReasons": {
      "无法确认当前安装版本，升级选择已关闭。": "Installierte Version unbekannt; Upgrade-Auswahl deaktiviert.",
      "只能选择高于当前安装版本的发行版。": "Nur neuere Versionen als die installierte sind auswählbar."
    }
  },
  "navigationLabel": "Einstellungsbereiche",
  "sections": {
    "performance": "Leistung",
    "appearance": "Darstellung",
    "systemIntegration": "Systemintegration",
    "updates": "Updates und Versionen",
    "debug": "Debug",
    "credits": "Danksagung"
  },
  "saveState": {
    "saving": "Speichern",
    "saved": "Gespeichert",
    "constrained": "Gespeichert; nicht verfügbare Laufzeitoptionen wurden eingeschränkt",
    "partial": "Gespeichert, aber nicht vollständig angewendet",
    "error": "Speichern fehlgeschlagen",
    "dirty": "Ungespeichert",
    "conflict": "Einstellungen wurden anderswo geändert; vor dem Speichern neu laden"
  },
  "loadState": {
    "loading": "Gespeicherte Einstellungen werden geladen…",
    "errorTitle": "Einstellungen nicht verfügbar",
    "errorDescription": "Standardwerte werden nicht als gespeicherte Einstellungen angezeigt oder verwendet. Stellen Sie den lokalen Dienst wieder her und versuchen Sie es erneut.",
    "staleTitle": "Einstellungen nicht synchronisiert",
    "staleDescription": "Diese Kopie könnte veraltet sein. Änderungen werden erst nach erfolgreichem Neuladen übermittelt."
  },
  "actions": {
    "save": "Speichern",
    "restoreDefaults": "Standardwerte wiederherstellen",
    "retry": "Erneut versuchen",
    "reload": "Neu laden",
    "reapply": "Erneut anwenden"
  },
  "performance": {
    "smartMonitoringTitle": "Überwachung nach Bedarf",
    "smartMonitoringDescription": "Ein: Nur Messwerte, die die Oberfläche gerade anzeigt oder abonniert hat, werden erfasst. Aus: Alle Messwerte des Katalogs werden erfasst, was mehr Last verursacht.",
    "adaptiveBooleanModeOptions": [
      {
        "id": "enabled",
        "label": "Ein"
      },
      {
        "id": "disabled",
        "label": "Aus"
      }
    ],
    "gpuPerformanceUseCasesTitle": "GPU-Einsatzzwecke",
    "gpuPerformanceUseCasesDescription": "Wählen Sie die wichtigsten Aufgaben für einen passenden Vergleich der GPU-Fähigkeiten. Mehrfachauswahl ist möglich.",
    "preciseGpuPlacementTitle": "Genaue GPU-Auswahl",
    "preciseGpuPlacementDescription": "Ermöglicht genaue GPU-Auswahl für unterstützte Renderprozesse. Wechsel während der Laufzeit hängen von der Prozesskompatibilität ab und bleiben pro App konfigurierbar.",
    "preciseGpuPlacementModeOptions": {
      "basic": {
        "label": "Aus",
        "description": "Nur von Windows unterstützte GPU-Präferenzen verwenden."
      },
      "precise": {
        "label": "Ein",
        "description": "Eine bestimmte Ziel-GPU für Software auswählbar machen."
      }
    },
    "automaticMemoryCleanupTitle": "Schwellen für die Notfallbereinigung",
    "automaticMemoryCleanupDescription": "Die Hintergrundbereinigung richtet sich nach der Ressourcenbelastung. Diese Schwellen bestimmen, wann wenig verbleibender physischer Speicher oder Zusicherungskapazität eine Notfallbereinigung auslöst. Sie gelten nur, wenn der Optimierungsmodus die Speichersteuerung einschließt.",
    "physicalMemoryAutomaticCleanupLabel": "Freier physischer Speicher %",
    "virtualMemoryAutomaticCleanupLabel": "Freie Commit-Kapazität %",
    "memoryOptimizationTargetTitle": "Ziel der Speicheroptimierung",
    "memoryOptimizationTargetDescription": "Bei Überschreitung des Ziels wird die langsame Freigabe der ersten Stufe fortgesetzt, ohne Notfallbereinigung auszulösen.",
    "physicalMemoryOptimizationTargetLabel": "Zielauslastung des physischen Speichers %",
    "virtualMemoryOptimizationTargetLabel": "Zielauslastung der Commit-Kapazität %",
    "moveDownDangerTitle": "Gefahrenschwellen beim Auslagern",
    "moveDownDangerDescription": "Sicherheitsreserven für das Auslagern von Daten aus dem VRAM in den physischen Speicher oder aus dem physischen in den virtuellen Speicher: Liegt der danach geschätzte freie Anteil unter diesen Werten, wird das Auslagern für diese Software übersprungen, damit die nächste Ebene nicht überlastet wird. Diese Art des Auslagerns ist in dieser Version noch nicht aktiviert; die Werte werden nur gespeichert.",
    "vramMoveDownPhysicalMemoryDangerLabel": "Aus VRAM: freier physischer Speicher %",
    "physicalMemoryMoveDownVirtualMemoryDangerLabel": "Aus Speicher: freie Commit-Kapazität %",
    "refreshCadenceTitle": "Häufigkeit der Informationsaktualisierung",
    "refreshCadenceDescription": "Legt fest, wie schnell Messwerte und Status aktualisiert werden. Häufigere Aktualisierungen benötigen etwas mehr Ressourcen.",
    "presetNumericModeOptions": [
      {
        "id": "aotu",
        "label": "Automatisch",
        "description": "Aktualisierungsrate anhand des Leistungsmodus und Fensterzustands wählen."
      },
      {
        "id": "preset",
        "label": "Vorgabe festlegen",
        "description": "Immer die Aktualisierungsrate der ausgewählten Vorgabe verwenden."
      },
      {
        "id": "custom",
        "label": "Benutzerdefiniert",
        "description": "Immer den manuell eingegebenen Millisekundenwert verwenden."
      }
    ],
    "refreshCadencePresetLabel": "Vorgabe",
    "refreshCadenceCustomLabel": "Benutzerdefiniert (ms)",
    "refreshCadencePresetOptions": [
      {
        "id": "responsive",
        "label": "Reaktionsschnell",
        "description": "Priorisiert Vordergrundinteraktion durch schnellere Aktualisierungen."
      },
      {
        "id": "balanced",
        "label": "Ausgewogen",
        "description": "Eine alltagstaugliche Aktualisierungsrate."
      },
      {
        "id": "lowPower",
        "label": "Energiesparend",
        "description": "Energiesparende Hintergrundstrategie."
      },
      {
        "id": "quiet",
        "label": "Ruhig",
        "description": "Niedrigste Rate bei Beibehaltung notwendiger Aktualisierungen."
      }
    ],
    "refreshCadenceItems": {
      "monitor": {
        "label": "Messwerte und Ressourcenleisten",
        "description": "Aktualisiert Live-Werte und Ressourcenleisten."
      },
      "resourceTable": {
        "label": "Ressourcentabelle",
        "description": "Aktualisiert die Ressourcenliste für Software und Prozesse."
      },
      "localSystem": {
        "label": "Lokales System",
        "description": "Aktualisiert selten wechselnde lokale Daten wie die Betriebszeit."
      }
    }
  },
  "appearance": {
    "uiStyleTitle": "Oberflächenstil",
    "uiStyleDescription": "Ändert Ecken, Linien, Schatten und Schriften der Bedienelemente; das Layout bleibt gleich. Jeder Stil hat eigene Farbschemata.",
    "themeTitle": "Farbschema",
    "themeDescription": "Wählen Sie ein Farbschema des aktuellen Stils. System folgt der hellen oder dunklen Windows-Einstellung.",
    "animationTitle": "Animationen",
    "animationDescription": "Wählen Sie volle Animationen, keine Animationen oder vereinfachte Effekte für geringere Oberflächenlast.",
    "resourceBarFrameRateTitle": "Bildrate der Ressourcenleisten",
    "resourceBarFrameRateDescription": "Wie viele Bilder pro Sekunde die Übergänge der Ressourcenleisten zeichnen. Bildschirm folgen nutzt die Bildwiederholrate des Monitors; Benutzerdefiniert begrenzt die Bilder und senkt die Last. Gilt nur bei normalen Animationen.",
    "resourceBarFrameRateModeOptions": [
      {
        "id": "display",
        "label": "Bildschirm folgen",
        "description": "Bei jeder Bildschirmaktualisierung ein Bild zeichnen."
      },
      {
        "id": "custom",
        "label": "Benutzerdefiniert",
        "description": "Höchstens die eingestellte Anzahl Bilder pro Sekunde zeichnen (10–240)."
      }
    ],
    "resourceBarFrameRateCustomLabel": "Bildrate (FPS)",
    "barColorTitle": "Leistenfarben",
    "barColorDescription": "Feste Farben je Softwaretyp oder eine eigene Farbe pro Software verwenden.",
    "byteUnitTitle": "Kapazitätseinheiten",
    "byteUnitDescription": "1 GiB = 1024 MiB, 1 GB = 1000 MB. Beim Wechsel werden auch die Zahlen umgerechnet, nicht nur die Bezeichnungen geändert.",
    "readingSourceTitle": "Messwertquellen",
    "readingSourceDescription": "Wie Messwerte mit mehreren Quellen in der Überwachungskonsole und den Gerätedetails aufgeführt werden. Standardmäßig wird nur die genaueste Quelle auf diesem Computer angezeigt; werden alle verfügbaren Quellen angezeigt, ist die genaueste als „Bevorzugt“ markiert. Die Planung verwendet unabhängig von dieser Einstellung immer die genaueste Quelle.",
    "readingSourceModeOptions": [
      {
        "id": "preferred",
        "label": "Nur genaueste",
        "description": "Für jeden Messwert nur die genaueste Quelle auf diesem Computer anzeigen."
      },
      {
        "id": "all",
        "label": "Alle verfügbaren",
        "description": "Alle verfügbaren Quellen jedes Messwerts anzeigen und die genaueste markieren."
      }
    ],
    "fontSmoothingTitle": "Textglättung",
    "fontSmoothingDescription": "Textklarheit und Oberflächenlast automatisch ausgleichen oder einen Darstellungsmodus festlegen.",
    "languageTitle": "Sprache der Benutzeroberfläche",
    "languageDescription": "Wählen Sie die Sprache der Resource-Manager-Oberfläche. System verwendet die Windows-Anzeigesprache.",
    "settingsLanguageTitle": "Oberflächensprache",
    "settingsLanguageDescription": "Ändert die Sprache der gesamten Oberfläche einschließlich Infobereich und Fenstertitel. System verwendet die Windows-Anzeigesprache.",
    "languageSelectLabel": "Sprache",
    "uiStyleOptions": {
      "standard": "Standard",
      "soft": "Weich",
      "outline": "Kontur",
      "terminal": "Terminal"
    },
    "themeOptions": {
      "system": "System",
      "light": "Hell",
      "dark": "Dunkel",
      "lowContrast": "Niedriger Kontrast",
      "paper": "Papier",
      "graphite": "Graphit",
      "blueprint": "Blaupause",
      "phosphor": "Phosphor",
      "amber": "Bernstein",
      "ice": "Eis"
    },
    "animationOptions": {
      "auto": {
        "label": "Automatisch",
        "description": "Im Vordergrund normale Animationen verwenden und im energiesparenden Hintergrund zur besonders sparsamen Darstellung wechseln"
      },
      "normal": {
        "label": "Normal motion",
        "description": "Enable restrained global transitions"
      },
      "none": {
        "label": "No motion",
        "description": "Alle Übergänge und Animationen deaktivieren, das Erscheinungsbild beibehalten"
      },
      "ultra": {
        "label": "Ultra performance",
        "description": "Animationen, Schatten, Glanz und Unschärfe für möglichst geringen Darstellungsaufwand deaktivieren"
      }
    },
    "barColorOptions": {
      "type": {
        "label": "Fest nach Typ",
        "description": "Farben für System-, angepasste und allgemeine Software; Trennlinien zwischen benachbarten Segmenten"
      },
      "distinct": {
        "label": "Unterschiedliche Farben",
        "description": "Eigene Farbe pro Software, ohne Typbedeutung und ohne Trennlinien"
      }
    },
    "byteUnitOptions": {
      "native": {
        "label": "Passend zur Hardware",
        "description": "GiB für Speicher, VRAM und Caches (Zweierpotenzen); GB für Datenträger, Dateien und Übertragungen (wie Herstellerangaben)"
      },
      "binary": {
        "label": "Immer GiB",
        "description": "Immer durch 1024 teilen; Einheiten KiB / MiB / GiB"
      },
      "decimal": {
        "label": "Immer GB",
        "description": "Immer durch 1000 teilen; Einheiten kB / MB / GB"
      }
    },
    "fontSmoothingOptions": {
      "auto": {
        "label": "Automatisch",
        "description": "Normalerweise System-ClearType verwenden und im Höchstleistungsmodus Glättung reduzieren"
      },
      "system": {
        "label": "System-ClearType",
        "description": "Native Windows-Subpixel-Darstellung für besonders scharfen Text"
      },
      "grayscale": {
        "label": "Graustufen",
        "description": "Weichere Graustufen-Kantenglättung, passend für manche hochauflösenden Bildschirme"
      },
      "disabled": {
        "label": "Aus",
        "description": "Zusätzliche Textglättung abschalten und Zeichen direkt rastern"
      }
    },
    "gpuPerformanceUseCaseOptions": {
      "general": {
        "label": "Allgemein",
        "description": "Zum Rasterleistungswert immer einen kleinen Generationsbonus hinzufügen"
      },
      "ai": {
        "label": "AI",
        "description": "Generation stärker gewichten und NVIDIA-Ökosystem, VRAM-Kapazität und Bandbreite hoch bewerten"
      },
      "gaming": {
        "label": "Spiele",
        "description": "Größten Generationsbonus und einen kleinen VRAM-Kapazitätsbonus anwenden"
      }
    }
  },
  "systemIntegration": {
    "taskManagerTitle": "Task-Manager-Tastenkürzel ersetzen",
    "autoStartTitle": "Mit Windows starten",
    "autoStartDescription": "Dienst beim Systemstart ausführen und nach der Anmeldung im Infobereich bleiben.",
    "taskManagerDescription": "Ctrl+Shift+Esc übernehmen: Nach der Registrierung startet Resource Manager, falls nötig, oder zeigt sein bestehendes Hauptfenster im Vordergrund. Ctrl+Alt+Del bleibt unverändert.",
    "forceTerminateTitle": "Tastenkürzel zum sofortigen Beenden",
    "forceTerminateDescription": "Die fokussierte Vordergrund-App und alle Apps mit sichtbaren, nicht reagierenden Fenstern beenden. Standardmäßig deaktiviert.",
    "forceTerminateWarning": "Dieses Kürzel beendet Prozesse sofort; ungespeicherte Arbeit geht verloren. Erforderlich ist Ctrl, Alt oder Win zusammen mit einer normalen Taste.",
    "hotkeyEmpty": "Keine Tasten festgelegt",
    "hotkeyAddKey": "Taste hinzufügen",
    "hotkeyKeyLabel": "Taste",
    "hotkeyRemoveKey": "Taste entfernen",
    "hotkeyUnordered": "Gleichzeitig drücken",
    "hotkeyOrdered": "Vorherige Taste zuerst",
    "publicServiceTitle": "Lokaler gemeinsamer Dienst",
    "publicServiceDescription": "Lokaler Software erlauben, gemeinsame Funktionen von Resource Manager zu nutzen.",
    "publicFileIndexTitle": "Gemeinsamer Software-Dateiindex",
    "publicFileIndexDescription": "Vorhandenen Index von Resource Manager nutzen, ohne bei Abfragen neu zu scannen.",
    "publicDatabaseServiceTitle": "SQLite-Datenbankdienst",
    "publicDatabaseServiceDescription": "Lokalen Programmen getrennte benannte Datenbanken, parametrisierte Abfragen und Sammeltransaktionen bereitstellen.",
    "publicAiModelCatalogTitle": "Einheitlicher KI-Modellkatalog",
    "publicAiModelCatalogDescription": "Resource Manager verwaltet Modellkatalog und Planung; LM Studio führt die Modelle aus.",
    "lmStudioEndpointTitle": "LM-Studio-Endpunkt",
    "lmStudioEndpointDescription": "Nur Adressen dieses Computers sind erlaubt. Andere Apps verbinden sich über Resource Manager.",
    "lmStudioAutoStartTitle": "LM Studio bei Bedarf starten",
    "lmStudioAutoStartDescription": "LM Studio starten, wenn Software ein Modell anfordert und es nicht läuft; Modelle werden nie automatisch geladen.",
    "aiGatewayTitle": "Schlüssel für lokale KI-Kompatibilität",
    "aiGatewayDescription": "OpenAI- oder Anthropic-kompatible Schlüssel für Software ohne direkte LM-Studio-Unterstützung erstellen. Anfragen gehen ausschließlich an offene Modelle auf diesem PC.",
    "aiGatewayOpenAiProfile": "OpenAI-kompatibel",
    "aiGatewayAnthropicProfile": "Anthropic-kompatibel",
    "aiGatewayNamePlaceholder": "Verwendungszweck (optional)",
    "aiGatewayGenerate": "Schlüssel erstellen",
    "aiGatewayGenerating": "In Bearbeitung",
    "aiGatewayOneTimeTitle": "Schlüssel erstellt",
    "aiGatewayOneTimeDescription": "Der Schlüssel wird nur einmal angezeigt. Jetzt speichern, da er später nicht erneut eingesehen werden kann.",
    "aiGatewayApiKeyLabel": "API-Schlüssel",
    "aiGatewayBaseUrlLabel": "Basis-URL",
    "aiGatewayCopy": "Kopieren",
    "aiGatewayRevoke": "Schlüssel widerrufen",
    "aiGatewayEmpty": "Es wurden noch keine Kompatibilitätsschlüssel erstellt.",
    "aiGatewayLoadFailed": "Lokale KI-Schlüssel konnten nicht geladen werden"
  },
  "debug": {
    "debugModeTitle": "Debug-Modus",
    "debugModeDescription": "Lokale Diagnoseoptionen anzeigen und aktivieren. Nur während einer Fehlersuche einschalten.",
    "debugLogTitle": "Debug-Protokolle speichern",
    "debugLogDescription": "Informationen zur Fehlersuche protokollieren. Dies benötigt etwas Speicherplatz und Prozessorzeit.",
    "hostManagerSmartCoordinatorScoreOnlyTitle": "Analysieren ohne Änderungen anzuwenden",
    "hostManagerSmartCoordinatorScoreOnlyDescription": "Überwachung und Planung fortsetzen, ohne Software- oder Hardwarezustände zu ändern.",
    "hostManagerSmartCoordinatorPerformanceLogTitle": "Planungsleistung protokollieren",
    "hostManagerSmartCoordinatorPerformanceLogDescription": "Planungsdauer und Last protokollieren, um den Eigenaufwand von Resource Manager zu untersuchen."
  },
  "credits": {
    "heroTitle": "Danke an alle Menschen und Projekte, die Resource Manager möglich machen",
    "heroBody": "Mein Dank gilt Microsoft, den Menschen hinter Windows, .NET und WebView2 sowie der gesamten Entwicklergemeinschaft. Ihre jahrelange Arbeit schafft Grundlagen, die ein Einzelner nicht aufbauen könnte. Jeder Beitrag zählt, unabhängig von vorgeschriebenen Namensnennungen. Resource Manager wird unabhängig entwickelt; dieser Dank bedeutet keine Signierung, Zertifizierung oder Empfehlung durch die genannten Projekte.",
    "dependencyListTitle": "Vollständige Danksagung für Abhängigkeiten",
    "dependencyListBody": "Danke an alle folgenden Paketautoren und Betreuer. Die Liste umfasst Frontend-Lockdatei und aufgelöste .NET-Pakete einschließlich Build-, Test- und optionaler Abhängigkeiten. Ein Eintrag bedeutet nicht, dass das Paket auf diesem Computer geladen oder verteilt wird.",
    "groups": {
      "project": "Projekt und Beitrag",
      "runtime": "Runtime und Build-Tools",
      "windows": "Windows- und Diagnose-Schnittstellen",
      "data": "Offline-Daten",
      "build": "Build-Werkzeuge",
      "hardware": "Hardwareüberwachung und optionale Unterstützung"
    },
    "roles": {
      "project": "Projekt",
      "frontendFramework": "Frontend-UI-Framework",
      "icons": "Oberflächensymbole",
      "serialization": "Solid-Serialisierungsabhängigkeiten",
      "webView": "Desktop-Web-Container",
      "database": "Lokale Datenbank",
      "nativeCompiler": "Compiler des nativen Kerns",
      "softwareCatalog": "Quellen für Software-Metadaten",
      "buildToolchain": "Frontend-Build-Werkzeuge",
      "typeSystem": "Frontend-Typsystem",
      "buildRuntime": "Build-Laufzeit und Typdefinitionen",
      "dotnetRuntime": "Lokale Anwendungslaufzeit",
      "hookLibrary": "Windows-Kompatibilitätsunterstützung",
      "startupInjectionLibrary": "Kompatibilität beim Softwarestart",
      "traceLibrary": "Analyse der Systemleistung",
      "windowsManagement": "Windows-Systeminformationen",
      "nvidiaTelemetry": "NVIDIA-GPU-Messdaten",
      "nvidiaExtension": "NVIDIA-GPU-Erweiterungsschnittstelle",
      "amdTelemetry": "AMD-GPU/iGPU-Messdaten",
      "signedHardwareAccess": "Signierter Hardwarezugriff",
      "hardwareBridge": "Allgemeine Hardware-Sensorbrücke",
      "externalReference": "Externe Messreferenz / optionale Hilfe",
      "deviceIdDatabase": "Datenbank für Hardware-ID-Namen"
    },
    "notes": {
      "windowsFoundation": "Danke an Microsoft und die Windows-Teams für die vom Betriebssystem bereitgestellten Win32-, Shell-, COM-, Direct3D/DXGI-, PDH-, ETW-, Dienst-, Kryptografie- und Netzwerkfunktionen.",
      "microsoftTools": "Besonderer Dank an Autoren und Betreuer von .NET SDK, MSBuild, NuGet und PowerShell für Kompilierung, Veröffentlichung, Paketverwaltung und Automatisierung.",
      "managedServices": "Danke an Microsoft und .NET-Mitwirkende für Dienstlebenszyklus und Basis-APIs. TypeExtensions ist für dieses Ziel ein aufgelöster Platzhalter, keine zusätzliche DLL.",
      "nativeToolchain": "Danke an GCC- und MinGW-Autoren und Betreuer für native Kompilierung und Laufzeitgrundlagen. Einige Komponenten binden libgcc/libstdc++ statisch; die Weitergabe wird separat geprüft.",
      "buildContributors": "Danke an alle Autoren und Betreuer der Build-Abhängigkeiten, einschließlich Solid-Werkzeuge, Source Maps, Browserdaten und Hilfsbibliotheken. Die vollständige Liste enthält alle fixierten Pakete und Versionen samt optionalen Plattformen.",
      "testContributors": "Danke an Playwright-, xUnit.net-, Coverlet- und Microsoft-Testplattform-Mitwirkende für Browserautomatisierung, Regressionstests und Abdeckung. Dies sind Entwicklungswerkzeuge, keine Laufzeitabhängigkeiten des Basisprodukts.",
      "upstreamContributors": "Unser Dank gilt allen Autoren der ursprünglichen .NET- und WebView2-Hinweise, einschließlich ANTLR, Unicode, Mono, Brotli und LLVM. Vollständige Originale bleiben erhalten; sie sind kein Inventar geladener DLLs.",
      "resourceManager": "Copyright (c) 2026 BIOcanse. Dieses Projekt verwendet Apache-2.0. Drittanbieterkomponenten behalten ihre eigenen Lizenzen.",
      "solid": "Stellt das reaktive Komponentenmodell der aktuellen leichtgewichtigen Web-Oberfläche bereit.",
      "lucide": "Die Oberfläche nutzt lucide-solid und erhält die Lucide-ISC-Lizenz sowie MIT-Lizenz und Urheberangaben der Feather-basierten Symbole.",
      "seroval": "Transitive Solid-Abhängigkeiten; MIT-Lizenz und Alexis-Munsayac-Urheberangabe bleiben erhalten.",
      "webView2": "Der Desktop-Container nutzt das WebView2-SDK. Microsoft verteilt die Systemlaufzeit WebView2 Evergreen separat zu eigenen Bedingungen.",
      "sqlite": "Lokale Datenspeicherung. Microsoft.Data.Sqlite nutzt MIT, SQLitePCLRaw 3.x Apache-2.0. Der von SourceGear verteilte SQLite-Kern wurde vom Ursprungsprojekt gemeinfrei freigegeben.",
      "zig": "Kompiliert den nativen Kern; zum Ausführen der App ist der Compiler nicht nötig.",
      "softwareCatalog": "Der Offline-Katalog nutzt WinGet-Metadaten unter MIT und strukturierte Wikidata-Daten unter CC0. Zusammenfassungen und chinesische Übersetzungen wurden vom Projekt erstellt.",
      "vite": "Übernimmt Entwicklungsbuilds und Paketierung des Solid-Frontends.",
      "typescript": "Verbessert die Zuverlässigkeit des Oberflächencodes und der Einstellungsmodelle.",
      "node": "Unterstützt Frontend-Builds und Entwicklungswerkzeuge.",
      "dotnet": "Stellt die lokale Laufzeitumgebung von Resource Manager bereit.",
      "minHook": "Danke an Tsuda Kageyu, Mitwirkende und HDE-Autor Vyacheslav Patkov für Hooks und Befehlsdekodierung. Originale Lizenzen und Urheberangaben bleiben vollständig erhalten.",
      "detours": "Open-Source-Projekt von Microsoft Research für Startkompatibilität. Ursprüngliche Lizenz und Urheberrechtshinweis bleiben erhalten.",
      "traceEvent": "Ermöglicht Zugriff auf Windows-Systemleistungsdaten.",
      "systemManagement": "Ermöglicht Zugriff auf Windows-System- und Hardwareinformationen.",
      "nvml": "Liefert Takt-, Speicher-, Leistungs- und Temperaturdaten von NVIDIA-GPUs.",
      "nvapi": "Ergänzt Lüfter-, Kühlungs- und elektrische Daten von NVIDIA-GPUs.",
      "adlx": "Liefert Auslastung, Takt, Leistung, Temperatur und Spannung von AMD-GPUs.",
      "pawnIo": "Offiziell signierter Treiber, gemeinsam genutzt von AMD-SMU-Messdaten, Intel-Prozessorsteuerung sowie Mainboard- und Speichersensoren; niemals unbemerkt installiert.",
      "pawnIoModules": "Offiziell signierte Module (LGPL-2.1). RyzenSMU und IntelMSR werden beim Prüfen der Komponente geladen; die LPC-, EC- und SMBus-Zugriffsmodule sind in LibreHardwareMonitorLib eingebettet.",
      "libreHardwareMonitor": "Mitgelieferte offizielle Kernbibliothek 0.9.6 (MPL-2.0), nur mit den Gruppen Mainboard und Arbeitsspeicher; liest über PawnIO Mainboard-Temperaturen, Spannungen, Lüfter und Speichertemperatur. Weder Oberfläche noch Webserver werden gestartet.",
      "blacktempelToolkits": "Von LibreHardwareMonitorLib genutzte Bibliotheken für Laufwerksinformationen, Speicher-SPD und Grundfunktionen (1.1.2, 1.4.2, 1.0.7) von Florian K., unter MPL-2.0.",
      "hidSharp": "Von LibreHardwareMonitorLib genutzte Bibliothek für USB-HID-Gerätezugriff 2.6.4 von James F. Bellinger, unter Apache-2.0.",
      "monoPosix": "Von LibreHardwareMonitorLib genutzte Mono-POSIX-Schnittstelle 1.0.0, veröffentlicht von Microsoft unter der MIT-Lizenz der Mono-Klassenbibliotheken.",
      "usbIds": "Liefert lokale Offline-Namen für USB-VID/PID-Hersteller und Geräte; nur bei angeforderten Gerätedetails geladen.",
      "pciIds": "Liefert lokale Offline-Namen für PCI-VEN/DEV/SUBSYS-Hersteller, Geräte und Subsysteme; nur bei angeforderten Gerätedetails geladen."
    },
    "linkLabels": {
      "official": "Website",
      "github": "GitHub",
      "docs": "Docs",
      "license": "Lizenz",
      "nuget": "NuGet",
      "gpuOpen": "GPUOpen",
      "eula": "EULA",
      "runtime": "Laufzeitumgebung",
      "aspnet": "ASP.NET Core",
      "vite": "Vite",
      "solidPlugin": "Solid plugin"
    }
  }
});
