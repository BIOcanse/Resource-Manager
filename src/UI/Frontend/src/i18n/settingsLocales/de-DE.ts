import { createSettingsLocale } from "../settingsLocaleFactory.ts";
import { plural } from "../plural.ts";

export default createSettingsLocale("de-DE", {
  "updates": {
    "modeTitle": "Update-Modus",
    "modeDescription": "Aus: keine automatischen Prüfungen. Manuelle Prüfung und manuelle Versionswahl funktionieren in jedem Modus.",
    "modeOptions": [{ id: "off", label: "Aus", description: "Keine automatische Prüfung, kein Download, keine Installation." }, { id: "notify", label: "Nur prüfen und melden", description: "Prüft automatisch und meldet eine neue Version, ohne sie herunterzuladen." }, { id: "automatic", label: "Automatisch laden und anwenden", description: "Prüft, lädt und verifiziert automatisch; wendet nach dem Schließen der Fenster an oder über Jetzt anwenden." }],
    "periodicTitle": "Regelmäßige Prüfung",
    "periodicDescription": "Nach der Startprüfung in diesem Abstand erneut prüfen.",
    "internetNote": "Automatische Prüfungen warten, bis das Internet erreichbar ist; der Abstand zählt nur die verbundene Zeit.",
    "offReason": "Der Modus ist Aus: Es laufen keine automatischen Prüfungen, diese beiden Einstellungen wirken nicht; ihre Werte bleiben erhalten.",
    "intervalTitle": "Prüfintervall (Stunden)",
    "intervalLabel": (minimum: number, maximum: number) => `Intervall (Stunden, ${minimum}–${maximum})`,
    "inAppNoticeTitle": "Hinweis auf neue Version in der Oberfläche",
    "inAppNoticeDescription": "Während die Oberfläche läuft, wird eine neue Version einmal gemeldet; dieselbe Version nicht erneut.",
    "accessRequestNoticeTitle": "Systembenachrichtigung bei neuen Zugriffsanfragen",
    "accessRequestNoticeDescription": "Zeigt eine Windows-Benachrichtigung an, wenn bei nicht sichtbarer Oberfläche eine neue vertrauenswürdige Zugriffsanfrage auf Genehmigung wartet.",
    "systemNoticeTitle": "Windows-Systembenachrichtigung",
    "systemNoticeDescription": "Sendet eine Systembenachrichtigung, wenn die App nicht sichtbar ist. Dieser Schalter speichert nur die Einstellung; die Desktop-Shell (Infobereich) führt sie aus.",
    "manualCheckNote": "Eine manuelle Prüfung läuft sofort, ohne auf das Internet zu warten, in jedem Modus.",
    "waitingForInternet": "Warten auf Internet; die Prüfung startet, sobald es erreichbar ist.",
    "availableVersion": "Gefundene neue Version: ",
    "nextCheck": "Nächste Prüfung: ",
    "nextCheckAfterInternet": "Zählung ab Verbindung",
    "nextCheckOff": "nicht aktiviert",
    "connectedElapsed": "Verbundene Zeit seit der letzten Prüfung: ",
    "noticeTitle": "Neue Version verfügbar",
    "noticeMessage": (version: string) => `Resource Manager ${version} ist verfügbar.`,
    "noticeReadyMessage": (version: string) => `Resource Manager ${version} ist heruntergeladen und geprüft und kann jetzt angewendet werden.`,
    "noticeOpenUpdates": "Updates und Versionen öffnen",
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
    "prepare": "Ausgewähltes Update vorbereiten",
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
      "installedVersionUnknown": "Installierte Version unbekannt; Upgrade-Auswahl deaktiviert.",
      "notNewer": "Nur neuere Versionen als die installierte sind auswählbar."
    }
  },
  "navigationLabel": "Einstellungsbereiche",
  "sections": {
    "runtimePolicy": "Laufzeitrichtlinie",
    "memory": "Arbeitsspeicher",
    "memoryProtection": "Speicherschutz des Hosts",
    "hardwareControl": "Hardwaresteuerung",
    "reportRules": "Berichtsregeln",
    "appearance": "Darstellung",
    "performanceOverlay": "Leistungs-Overlay",
    "systemIntegration": "Systemintegration",
    "publicServices": "Gemeinsame Dienste",
    "trustedAccess": "Vertrauenswürdiger Zugriff",
    "updates": "Updates und Versionen",
    "debug": "Debug",
    "credits": "Danksagung"
  },
  "saveState": {
    "saving": "Speichern",
    "saved": "Gespeichert",
    "constrained": "Gespeichert; nicht verfügbare Laufzeitoptionen wurden eingeschränkt",
    "partial": "Gespeichert; Veröffentlichung oder Zustellung des Laufzeitplans ist unvollständig",
    "error": "Speichern fehlgeschlagen",
    "dirty": "Ungespeichert",
    "conflict": "Einstellungen wurden anderswo geändert; vor dem Speichern neu laden",
    "invalidValueType": "Speichern abgelehnt: Ein Einstellungswert hat den falschen Typ"
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
    "reapply": "Laufzeitplan erneut veröffentlichen"
  },
  "performance": {
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
    "gpuOverflowTitle": "Überlaufschwellen der dedizierten GPU",
    "gpuOverflowDescription": "Sobald einer der Schwellenwerte erreicht ist, werden Hintergrundprozesse mit niedriger Bewertung von der dedizierten auf die integrierte GPU verschoben. Erfordert aktive Planung und eine verfügbare integrierte GPU.",
    "gpuOverflowUsageLabel": "Auslastung der dedizierten GPU %",
    "gpuOverflowDedicatedMemoryLabel": "Belegung des dedizierten GPU-Speichers %",
    "realtimeActionBudgetTitle": "Limit für Echtzeitaktionen",
    "realtimeActionBudgetDescription": "Begrenzen Sie Echtzeitaktionen pro Durchlauf auf eine feste Anzahl oder einen aufgerundeten Prozentsatz aller in diesem Durchlauf erkannten Prozesse. Beim Moduswechsel bleiben beide Werte erhalten.",
    "realtimeActionBudgetModeOptions": [{"id":"fixed","label":"Feste Anzahl"},{"id":"percent","label":"Prozessanteil"}],
    "realtimeActionBudgetCountLabel": "Feste Aktionsanzahl",
    "realtimeActionBudgetPercentLabel": "Anteil aller Prozesse %",
    "keepSmartRecordHistoryTitle": "Verlauf der intelligenten Planung aufbewahren",
    "keepSmartRecordHistoryDescription": "Wenn eingeschaltet, bleibt jeder Eintrag der intelligenten Planung nach der Wiederherstellung auf der Registerkarte „Bericht“ unter Details, nach Laufnummer sortiert und ohne Grenze. Wenn ausgeschaltet, werden neue Einträge nach der Wiederherstellung nicht aufbewahrt; bereits aufbewahrter Verlauf bleibt erhalten.",
    "keepSmartRecordHistoryOptions": {
      "on": { "label": "Ein", "description": "Einträge nach der Wiederherstellung als Verlauf aufbewahren." },
      "off": { "label": "Aus", "description": "Einträge nach der Wiederherstellung nicht aufbewahren." }
    },
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
    "cacheDescription": "Darstellung, Aktualisierungsintervalle und Overlay-Darstellung werden auf diesem Ger\u00E4t zwischengespeichert. Beim Leeren des Caches gelten wieder die Standardwerte.",
    "resetCache": "Oberfl\u00E4cheneinstellungen zur\u00FCcksetzen",
    "uiStyleTitle": "Oberflächenstil",
    "uiStyleDescription": "Ändert Ecken, Linien, Schatten und Schriften der Bedienelemente; das Layout bleibt gleich. Jeder Stil hat eigene Farbschemata.",
    "themeTitle": "Farbschema",
    "themeDescription": "Wählen Sie ein Farbschema des aktuellen Stils. System folgt der hellen oder dunklen Windows-Einstellung.",
    "animationStyleTitle": "Animationsstil",
    "animationStyleDescription": "Legt fest, wie Übergänge ablaufen und wie Bedienelemente an ihren Platz gelangen. Höchste Leistung entfernt zusätzlich Schatten, Glanz und Unschärfe.",
    "autoDisableAnimationsTitle": "Animationen durch Selbstplanung automatisch deaktivieren",
    "autoDisableAnimationsDescription": "Wenn aktiviert: Kehrt die Oberfläche ohne kürzliche Eingabe in den Vordergrund zurück und verlangt das Ergebnis der Selbstplanung Einsparungen, wird Keine Animationen verwendet, andernfalls der gewählte Stil. Während der Bedienung im Vordergrund wird nicht gewechselt. Bei Keine Animationen oder Höchste Leistung ohne Wirkung.",
    "animationStyleOptions": {
      "smooth": { "label": "Fließend", "description": "Wie macOS und mobile Systeme: Federbewegung, die die Geschwindigkeit der Geste übernimmt, jederzeit abgefangen werden kann und an Bildlaufrändern zurückfedert" },
      "desktop": { "label": "Desktop-Standard", "description": "Wie übliche Windows-Desktopsoftware: kurze, abbremsende Übergänge ohne übernommene Geschwindigkeit und ohne Zurückfedern" },
      "terminal": { "label": "Terminal-Stufen", "description": "Springt in einer festen Anzahl von Stufen ohne Beschleunigungskurve zum Ziel, wie die Bildschirmaktualisierung eines Textterminals" },
      "none": { "label": "Keine Animationen", "description": "Alle Übergänge und Einblendungen sind im selben Bild abgeschlossen; das Erscheinungsbild bleibt unverändert" },
      "ultra": { "label": "Höchste Leistung", "description": "Keine Animationen und zusätzlich keine Schatten, kein Glanz und keine Unschärfe für den geringsten Darstellungsaufwand" }
    },
    "resourceBarFrameRateTitle": "Bildrate der Ressourcenleisten",
    "resourceBarFrameRateDescription": "Wie viele Bilder pro Sekunde die Übergänge der Ressourcenleisten zeichnen. Bildschirm folgen nutzt die Bildwiederholrate des Monitors; Benutzerdefiniert begrenzt die Bilder und senkt die Last. Bei Keine Animationen oder Höchste Leistung ohne Wirkung.",
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
    "fontSmoothingTitle": "Textglättung",
    "fontSmoothingDescription": "Textklarheit und Oberflächenlast automatisch ausgleichen oder einen Darstellungsmodus festlegen.",
    "languageTitle": "Sprache der Benutzeroberfläche",
    "languageDescription": "Wählen Sie die Sprache der Resource-Manager-Oberfläche. System verwendet die Windows-Anzeigesprache.",
    "settingsLanguageTitle": "Oberflächensprache",
    "settingsLanguageDescription": "Ändert die Sprache der gesamten Oberfläche einschließlich Infobereich und Fenstertitel. System verwendet die Windows-Anzeigesprache.",
    "languageSelectLabel": "Sprache",
    "uiStyleOptions": {
      "standard": "Standard",
      "classic": "Klassisch",
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
  },
  "systemIntegration": {
    "crashRecoveryTitle": "Nach einem Absturz neu starten",
    "crashRecoveryDescription": "Den Hintergrunddienst nach einem Absturz automatisch neu starten. Ein normaler Stopp löst keinen Neustart aus.",
    "crashRestartDelayTitle": "Wartezeit vor Neustart (Sekunden)",
    "crashRestartCountTitle": "Erlaubte Neustarts",
    "crashCounterResetDescription": "Absturzfreie Tage bis zum Zurücksetzen des Zählers: {days}. Starten Sie den Dienst nach Erreichen des Limits manuell.",
    "crashCounterResetTitle": "Rücksetzzeitraum (Tage)",
    "crashRecoveryActualTitle": "Aktuelle Konfiguration",
    "crashRecoveryUnavailable": "Die Konfiguration der automatischen Wiederherstellung ist nicht verfügbar.",
    "crashRecoveryApplied": "Die automatische Wiederherstellung wurde konfiguriert.",
    "crashRecoverySkipped": "In diesem Ausführungsmodus wird die automatische Wiederherstellung nicht konfiguriert.",
    "crashRecoveryFailed": "Die automatische Wiederherstellung konnte nicht konfiguriert werden.",
    "crashRecoveryMismatch": "Die aktuelle Konfiguration weicht von Ihren Einstellungen ab.",
    "crashRecoveryNonCrashWarning": "Die aktuelle Konfiguration startet auch nach einem normalen Stopp mit Fehlermeldung neu.",
    "crashRecoveryWriteFailed": "Die Konfiguration der automatischen Wiederherstellung konnte nicht geschrieben werden.",
    "crashRecoveryReadFailed": "Die Konfiguration der automatischen Wiederherstellung konnte nicht gelesen werden.",
    "taskManagerTitle": "Task-Manager-Tastenkürzel ersetzen",
    "autoStartTitle": "Hintergrunddienst beim Systemstart starten",
    "autoStartDescription": "Den Hintergrunddienst beim Start von Windows automatisch starten.",
    "loginStartTitle": "Nach der Anmeldung im Infobereich öffnen",
    "loginStartDescription": "Die Oberfläche nach der Anmeldung dieses Benutzers starten. Den Hintergrunddienst separat konfigurieren.",
    "loginStartUnavailable": "Die Autostarteinstellung des aktuellen Benutzers konnte nicht gelesen oder gespeichert werden.",
    "loginStartLoading": "Anmeldestart-Einstellung wird gelesen…",
    "loginStartWriteFailed": "Die Anmeldestart-Einstellung konnte nicht gespeichert werden. Der Schalter zeigt den danach gelesenen Zustand.",
    "loginStartRetry": "Erneut versuchen",
    "taskManagerDescription": "Wenn aktiviert, öffnet jeder Aufruf des Task-Managers stattdessen Resource Manager (er startet bei Bedarf oder zeigt sein Hauptfenster): Ctrl+Shift+Esc, das Taskleistenmenü, „Task-Manager“ im Ctrl+Alt+Del-Bildschirm und das Ausführen von taskmgr.exe. Der Ctrl+Alt+Del-Bildschirm selbst bleibt unverändert. Die Ersetzung muss mit Administratorrechten registriert werden; ohne Registrierung wird nur Ctrl+Shift+Esc übernommen, und nur solange Resource Manager läuft. Beim Deaktivieren wird nur der von Resource Manager geschriebene Registrierungswert entfernt.",
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
    "aiGatewayLoadFailed": "Lokale KI-Schlüssel konnten nicht geladen werden",
    "trustedAccess": {
      "title": "Vertrauensschlüssel",
      "description": "Allgemeine Zugriffsschlüssel und einmalige Autorisierungsanfragen verwalten.",
      "loading": "Zugriffsschlüssel werden geladen…",
      "loadFailed": "Vertrauensschlüssel konnten nicht geladen werden.",
      "actionFailed": "Die Aktion für den Vertrauenszugriff ist fehlgeschlagen.",
      "retry": "Erneut versuchen",
      "failureReasons": {
        "unauthorized": "Der lokale Dienst akzeptiert den Schlüssel dieser Oberfläche nicht: Der Standardschlüssel ist möglicherweise deaktiviert, gerade erneuert oder ungültig. Sie können es erneut versuchen; ist der Standardschlüssel deaktiviert, aktivieren Sie ihn als Administrator erneut.",
        "scope-required": "Dem aktuellen Schlüssel fehlt der Bereich „Zugriffsschlüssel verwalten“, daher ist diese Aktion nicht erlaubt.",
        "default-scope-denied": "Zum Ändern des Standardschlüssels ist ein Schlüssel mit allen Standardbereichen oder Administratorrechte erforderlich."
      },
      "defaultTitle": "Datei des nativen Standardschlüssels",
      "defaultDescription": "Der Standardschlüssel liegt in der unten angegebenen Datei und ist für Programme bestimmt, die auf den lokalen Dienst zugreifen müssen. Die Oberfläche von Resource Manager läuft als Administrator und verwendet den Standardschlüssel nicht.",
      "defaultEnabled": "Aktiviert",
      "defaultDisabled": "Deaktiviert",
      "defaultKeyName": "Standardschlüssel",
      "defaultFilePath": "Dateispeicherort",
      "defaultFileLocation": "<Installationsordner>\\UserData\\Security\\default-key",
      "defaultSecretLocalOnly": "Der Schlüssel selbst wird hier nicht angezeigt.",
      "defaultAccessLossWarning": "Wird der Standardschlüssel deaktiviert, wird er sofort widerrufen, und Programme, die ihn verwenden, verlieren den Zugriff. Beim Rotieren wird der alte Schlüssel ungültig; Programme, die ihn verwenden, müssen die Standardschlüsseldatei erneut lesen.",
      "defaultDisabledRecovery": "Der Standardschlüssel ist deaktiviert. Anfragen von Programmen, die ihn verwenden, werden abgelehnt, bis er wieder aktiviert wird.",
      "enableDefault": "Standardschlüssel wieder aktivieren",
      "disableDefault": "Standardschlüssel deaktivieren und widerrufen",
      "regenerateDefault": "Standardschlüssel neu erzeugen",
      "confirmDisableTitle": "Standardschlüssel deaktivieren und widerrufen?",
      "confirmDisableMessage": "Beim Deaktivieren wird der Standardschlüssel sofort widerrufen. Programme, die ihn verwenden, verlieren den Zugriff, bis der Standardschlüssel wieder aktiviert wird.",
      "confirmRegenerateTitle": "Standardschlüssel neu erzeugen?",
      "confirmRegenerateMessage": "Der alte Standardschlüssel wird sofort ungültig. Programme, die ihn verwenden, müssen die Standardschlüsseldatei erneut lesen; bis dahin werden ihre Anfragen abgelehnt.",
      "confirmRotateTitle": (name: string) => `„${name}“ erneuern?`,
      "confirmRotateMessage": "Der alte Schlüssel wird sofort ungültig; Programme, die ihn verwenden, werden abgelehnt, bis sie den neuen Schlüssel nutzen. Der neue Schlüssel wird nur einmal angezeigt.",
      "confirmRevokeTitle": (name: string) => `„${name}“ widerrufen?`,
      "confirmRevokeMessage": "Der Schlüssel wird sofort ungültig; Programme, die ihn verwenden, werden abgelehnt. Dies kann nicht rückgängig gemacht werden.",
      "createTitle": "Unabhängigen Schlüssel erstellen",
      "createDescription": "Geltungsbereiche auswählen. Ein neuer Schlüssel wird einmal angezeigt und kann einzeln erneuert oder widerrufen werden.",
      "nameLabel": "Name",
      "namePlaceholder": "Zum Beispiel lokales Skript",
      "createScopesLabel": "Gewährte Bereiche",
      "noScopesAvailable": "Derzeit sind keine Bereiche für neue Schlüssel verfügbar.",
      "create": "Unabhängigen Schlüssel erzeugen",
      "working": "Wird verarbeitet…",
      "oneTimeTitle": "Unabhängiger Schlüssel erstellt",
      "oneTimeDescription": "Jetzt kopieren und speichern. Später kann er nicht erneut angezeigt werden.",
      "oneTimeAnnouncement": "Schlüssel erzeugt. Jetzt kopieren und speichern.",
      "apiKeyLabel": "Schlüssel",
      "copy": "Kopieren",
      "copied": "Kopiert",
      "copyFailed": "Der Schlüssel konnte nicht kopiert werden.",
      "dismissSecret": "Einmalschlüssel ausblenden",
      "createdAt": "Erstellt",
      "keysTitle": "Unabhängige Schlüssel",
      "keysEmpty": "Es wurden keine unabhängigen Schlüssel erstellt.",
      "revoke": "Widerrufen",
      "rotate": "Erneuern",
      "rotateKeyLabel": (name: string) => `„${name}“ erneuern`,
      "revokeKeyLabel": (name: string) => `„${name}“ widerrufen`,
      "requestsTitle": "Einmalige Autorisierungsanfragen",
      "requestsDescription": "Anfragen von Drittprogrammen zeigen den gewählten Namen und die angeforderten Bereiche.",
      "reviewRequests": "Anfragen prüfen",
      "requestsLoading": "Warte auf Autorisierungsanfragen…",
      "requestsUnavailable": "Das Abonnement für Autorisierungsanfragen ist nicht verfügbar; neue Anfragen können derzeit nicht eintreffen.",
      "requestsLoadFailed": "Ausstehende Autorisierungsanfragen konnten nicht gelesen werden.",
      "requestsStale": "Die Anfrageliste ist möglicherweise veraltet, während das Abonnement neu verbindet.",
      "requestsEmpty": "Es liegen keine Anfragen vor.",
      "requestNamed": (name: string) => `Anfragename: ${name}`,
      "requestCount": (count: number) => `Ausstehende Anfragen: ${count}`,
      "expiresAt": "Läuft ab",
      "observedExecutable": "Beobachteter Programmpfad",
      "observedProcess": "PID",
      "submittedAt": "Eingereicht",
      "observedCallerIsInformational": "Beobachteter Pfad und PID dienen nur zur Information und sind keine Autorisierungsidentität.",
      "requestScopesLabel": "Angeforderte Bereiche",
      "requestUnavailableScopeHelp": "Bereiche außerhalb Ihrer aktuellen Berechtigung können nicht ausgewählt werden. Gesendet werden nur angeforderte Bereiche, die Sie gewähren dürfen.",
      "administratorHolderScope": "Programme mit diesem Schlüssel müssen als Administrator ausgeführt werden",
      "approve": "Ausgewählte Bereiche genehmigen",
      "deny": "Ablehnen",
      "denyReasonLabel": "Ablehnungsgrund",
      "requestActionFailed": "Die Autorisierungsentscheidung konnte nicht gesendet werden.",
      "requestApproved": "Genehmigt. Der Schlüssel wurde dem anfragenden Programm zugestellt.",
      "requestDenied": "Abgelehnt.",
      "requestDeniedWithReason": (reason: string) => `Abgelehnt (Grund: ${reason}).`,
      "requestExpired": "Die Anfrage ist abgelaufen.",
      "requestOutcome": (name: string, result: string) => `${name}: ${result}`,
      "denialReasons": {
        "not-approved": "Nicht genehmigt",
        "unsupported": "Nicht unterstützt",
        "duplicate": "Doppelte Anfrage",
        "not-needed": "Nicht erforderlich"
      },
      "scopeLabels": {
        "read": "API lesen",
        "metrics.read": "Metriken lesen",
        "processes.read": "Prozesse lesen",
        "session": "Sitzungsaktionen",
        "adapters.register.self": "Adapter dieses Benutzers registrieren",
        "desktop.publish": "Desktop-Beobachtungen veröffentlichen",
        "gpu.resolve": "GPU-Platzierung auflösen",
        "gpu.report": "GPU-Start melden",
        "keys.manage": "Zugriffsschlüssel verwalten",
        "machine.write": "Computereinstellungen ändern",
        "injection.configure": "Startinjektion konfigurieren"
      },
      "unknownScope": "Unbekannter Bereich (diese Version kann ihn nicht beschreiben, daher kann er hier nicht gewährt werden)"
    }
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
  "hardwareControl": {
    "keepAliveTitle": "Einstellungen aktiv halten",
    "keepAliveDescription": "Ist dies ausgeschaltet, werden die auf der Seite „Steuerung“ gespeicherten Einstellungen nur einmal beim Start von Resource Manager angewendet. Ist es eingeschaltet, werden sie im unten angegebenen Intervall erneut angewendet, damit Windows, Treiber oder andere Programme sie nicht verändert lassen.",
    "reassertionTitle": "Intervall für erneutes Anwenden",
    "reassertionDescription": "Solange „Einstellungen aktiv halten“ eingeschaltet ist, werden die Einstellungen nach jedem Intervall erneut angewendet."
  },
  "performanceOverlay": {
    "intro": "Listet die Software auf, für die ein Overlay aktiviert ist. Angezeigt werden die gespeicherten Einstellungen, nicht ob ein Overlay gerade sichtbar ist.",
    "loading": "Overlay-Einstellungen werden gelesen",
    "loadFailed": "Die Overlay-Einstellungen konnten nicht gelesen werden",
    "emptyTitle": "Für keine Software ist ein Overlay aktiviert",
    "emptyDetail": (tab: string) => `Zum Aktivieren die Details einer Software öffnen und den Tab „${tab}“ wählen.`,
    "metricCount": (count: number) => plural("de-DE", count, { one: "{count} Metrik", other: "{count} Metriken" }),
    "position": (anchor: string) => `Position: ${anchor}`,
    "open": "Overlay-Einstellungen öffnen",
    "openLabel": (name: string) => `Overlay-Einstellungen für ${name} öffnen`,
    "softwareMissing": "Diese Software ist nicht mehr in der Softwareliste; ihre Details können nicht geöffnet werden."
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
    "linkLabels": {
      "official": "Website",
      "github": "GitHub",
      "docs": "Dokumentation",
      "nuget": "NuGet",
      "npm": "npm",
      "license": "Lizenz"
    },
    "sourceLabel": "Quelle",
    "dependencyColumns": {
      "name": "Name",
      "version": "Version",
      "license": "Lizenz",
      "source": "Quelle"
    }
  }
});
