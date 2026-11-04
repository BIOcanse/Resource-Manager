import { plural } from "../../plural.ts";
import type { AppCopy } from "../index.ts";

export const deviceActions: AppCopy["deviceActions"] = {
  restart: {
    label: "Gerät neu starten",
    busy: "Wird neu gestartet",
    hint: "Deaktiviert das Gerät, auf das dieser Bericht verweist, und aktiviert es wieder. So kann ein hängender Treiber wieder arbeiten. Sie bestätigen jedes Mal; die Software führt dies nie selbst aus.",
    confirmTitle: "Dieses Gerät neu starten?",
    confirmMessage: (target: string) => `Das Gerät hinter „${target}“ wird deaktiviert und wieder aktiviert.`,
    confirmDetails: [
      "Dies wird nur nach Ihrer Bestätigung einmal ausgeführt und nie automatisch wiederholt.",
      "Administratorrechte sind erforderlich. Das Ergebnis wird in den Geräteaktionen unter „Details“ festgehalten."
    ],
    risks: {
      DeviceRestartNetworkInterrupted: "Bei einem Netzwerkadapter werden Netzwerkverbindungen kurz unterbrochen.",
      DeviceRestartDisplayStateLost: "Bei einem Grafikadapter wird der Bildschirm kurz schwarz; Fensterpositionen und Anzeigeeinstellungen können verloren gehen.",
      DeviceRestartMayRequireReboot: "Manche Geräte funktionieren erst nach einem Neustart des Computers wieder."
    },
    unknownRisk: (code: string) => `Weiteres Risiko (${code}).`,
    confirm: "Gerät neu starten",
    doneTitle: "Gerät neu gestartet",
    refusedTitle: "Gerät nicht neu gestartet",
    failed: "Das Gerät konnte nicht neu gestartet werden.",
    reason: (text: string) => `Ergebnis: ${text}`,
    deviceResult: (text: string) => `Geräteergebnis: ${text}`,
    win32Detail: (code: string) => `Systemfehlercode: ${code}`,
    recorded: (sequence: string) => `In den Geräteaktionen festgehalten (Nummer ${sequence}).`,
    notRecorded: "Diese Anfrage wurde nicht festgehalten."
  },
  reasons: {
    completed: "Abgeschlossen",
    "confirmation-required": "Bestätigung erforderlich",
    "report-not-found": "Der Berichtseintrag wurde nicht gefunden",
    "unsupported-report": "Diese Art von Bericht unterstützt keinen Geräteneustart",
    "device-evidence-unavailable": "Der Bericht enthält keine Angaben, die das Gerät eindeutig bestimmen",
    "invalid-settings": "Die Einstellungen sind ungültig",
    "record-capacity-reached": "Die Geräteaktionen sind voll; es wurde nichts ausgeführt",
    "persistence-failed": "Der Eintrag konnte nicht geschrieben werden; es wurde nichts ausgeführt",
    "device-resolution-failed": "Das neu zu startende Gerät konnte nicht bestimmt werden",
    "device-restart-failed": "Der Neustart des Geräts ist fehlgeschlagen",
    "notification-failed": "Das Gerät wurde behandelt, aber die Benachrichtigung konnte nicht gesendet werden"
  },
  results: {
    success: "Erfolgreich",
    "confirmation-required": "Bestätigung erforderlich",
    "invalid-instance-id": "Ungültige Geräteinstanz-ID",
    "administrator-required": "Administratorrechte erforderlich",
    "device-not-found": "Gerät nicht gefunden",
    "device-unavailable": "Gerät derzeit nicht verfügbar",
    "disable-failed": "Deaktivieren des Geräts fehlgeschlagen",
    "enable-failed": "Erneutes Aktivieren des Geräts fehlgeschlagen",
    "reboot-required": "Der Computer muss neu gestartet werden",
    "verification-failed": "Die Prüfung nach dem Neustart ist fehlgeschlagen",
    "driver-not-found": "Treiber nicht gefunden",
    "ambiguous-device": "Mehrere Geräte passen; es wurde nichts ausgeführt",
    "invalid-module": "Ungültiges Treibermodul",
    "enumeration-failed": "Auflisten der Geräte fehlgeschlagen",
    "buffer-too-small": "Systempuffer zu klein",
    "unsupported-platform": "Auf diesem System nicht unterstützt",
    interrupted: "Während der Ausführung unterbrochen; das Ergebnis ist unbekannt"
  },
  unknownCode: (code: string) => `Unbekannter Code ${code}`,
  records: {
    tab: "Geräteaktionen",
    intro: "Jedes Mal, wenn Sie „Gerät neu starten“ in einem Bericht bestätigen, wird das Ergebnis hier festgehalten. Einträge bleiben erhalten und werden nie automatisch gelöscht; ein laufender Eintrag wird nach Ende der Aktion in den Verlauf verschoben.",
    loading: "Geräteaktionen werden geladen…",
    readFailed: "Die Geräteaktionen konnten nicht geladen werden",
    empty: "Noch keine Geräteaktionen.",
    loadMore: "Ältere Einträge laden",
    loadingMore: "Wird geladen",
    refresh: "Aktualisieren",
    shown: (count: string) => `Angezeigte Einträge: ${count}.`,
    states: { active: "Läuft", history: "Verlauf" },
    device: "Geräteinstanz",
    report: "Berichtseintrag",
    time: "Zeit",
    result: "Ergebnis",
    detail: "Systemfehlercode",
    sequence: "Nummer"
  }
};

export const crashDumpKeys: AppCopy["crashDumpKeys"] = {
  title: "Tasten für Absturzabbild",
  description: "Wenn aktiviert, hält das System mit einem Bluescreen an und schreibt ein Speicherabbild, sobald Sie die rechte Strg-Taste gedrückt halten und zweimal Rollen drücken. Friert der Computer ein und lässt sich nicht mehr bedienen, bleibt so der Zustand für eine spätere Analyse erhalten. Standardmäßig aus.",
  details: [
    "Es wird nur ausgelöst, wenn Sie diese Tasten drücken; sonst hat es keine Wirkung.",
    "Nach dem Auslösen startet der Computer sofort neu; nicht gespeicherte Arbeit geht verloren.",
    "Änderungen wirken nach einem Neustart des Computers und erfordern zum Schreiben Administratorrechte. Beim Ausschalten werden die Systemeinstellungen von vor dem Einschalten wiederhergestellt."
  ],
  notProvided: "Das Back-End hat diese Einstellung nicht bereitgestellt.",
  statusTitle: "Zustand im System",
  states: {
    takesEffectAfterRestart: "Ins System geschrieben; wirkt nach einem Neustart des Computers.",
    off: "Nicht aktiviert.",
    restorePending: "Die früheren Systemeinstellungen werden wiederhergestellt; abgeschlossen nach einem Neustart des Computers.",
    configurationMismatch: "Die Tasteneinstellungen im System stimmen nicht mit der Auswahl hier überein.",
    failed: "Das Schreiben der Systemeinstellungen ist fehlgeschlagen."
  },
  combinations: {
    rightCtrlScrollLockTwice: "Tasten: rechte Strg + zweimal Rollen",
    custom: "Tasten: eigene Tasten, von einem anderen Programm oder von Hand festgelegt"
  },
  kernelDumpMissing: "Das System ist nicht für Kernelspeicherabbilder eingerichtet; beim Auslösen wird möglicherweise kein Abbild geschrieben.",
  reason: (text: string) => `Grund: ${text}`,
  win32Detail: (code: string) => `Systemfehlercode: ${code}`,
  reasons: {
    "invalid-crash-dump-keys": "Die Tasteneinstellungen sind ungültig",
    "crash-dump-keys-read-failed": "Das Lesen der Systemeinstellungen ist fehlgeschlagen",
    "crash-dump-keys-write-failed": "Das Schreiben der Systemeinstellungen ist fehlgeschlagen",
    "crash-dump-keys-verify-failed": "Die Prüfung nach dem Schreiben ist fehlgeschlagen",
    "crash-dump-keys-restore-pending": "Die früheren Systemeinstellungen sind noch nicht vollständig wiederhergestellt",
    "invalid-crash-dump-key-snapshot": "Die gespeicherten ursprünglichen Einstellungen sind ungültig",
    "crash-dump-keys-access-denied": "Administratorrechte sind erforderlich",
    "crash-dump-key-value-too-large": "Ein ursprünglicher Wert im System ist zu groß zum Speichern",
    "crash-dump-keys-configuration-mismatch": "Ein anderes Programm hat die Einstellungen im System geändert",
    "crash-dump-keys-bridge-failed": "Der lokale Dienst konnte diese Einstellung nicht verarbeiten"
  }
};

export const cpuMonopoly: AppCopy["cpuMonopoly"] = {
  intro: "Wenn ein Prozess mit hoher Priorität oder Echtzeitpriorität die CPU dauerhaft voll auslastet, kann das System einfrieren und nicht mehr reagieren. Wenn aktiviert, wird ein solcher Prozess auf normale Priorität zurückgesetzt. Nur die Priorität ändert sich: Der Prozess wird weder beendet noch angehalten, und seine Priorität wird nicht zurückgeändert. Die Prozess-Ausnahmeliste des Speicherschutzes gilt ebenfalls. Standardmäßig aus.",
  notProvided: "Das Back-End hat die Einstellungen des CPU-Monopolschutzes nicht bereitgestellt.",
  unsavedNote: "Es gibt nicht gespeicherte Änderungen; sie wirken nach dem Speichern.",
  rejected: (reason: string) => `Die Einstellungen wurden nicht angenommen: ${reason}`,
  enabledTitle: "CPU-Monopolschutz aktivieren",
  enabledDescription: "Wenn aktiviert, werden CPU-Auslastung und Priorität der Prozesse im Hintergrund geprüft; wenn deaktiviert, wird nichts geprüft.",
  thresholdTitle: "Auslastungsschwelle",
  thresholdDescription: "Die Zeitmessung beginnt, wenn ein Prozess mit hoher Priorität oder Echtzeitpriorität diese CPU-Auslastung erreicht, in Prozent aller Prozessoren.",
  thresholdLabel: "Prozent",
  durationTitle: "Dauer",
  durationDescription: "So lange muss die Auslastung an der Schwelle bleiben, bevor der Prozess auf normale Priorität zurückgesetzt wird.",
  durationLabel: "Sekunden",
  intervalTitle: "Prüfintervall",
  intervalDescription: "Wie oft die CPU-Auslastung der Prozesse gelesen wird.",
  intervalLabel: "Sekunden",
  whitelistNote: "Die Ausnahmeliste wird im Bereich „Speicherschutz des Hosts“ bearbeitet.",
  statusTitle: "Aktueller Zustand",
  statusLoading: "Zustand des CPU-Monopolschutzes wird geladen…",
  statusUnavailable: "Der Zustand des CPU-Monopolschutzes konnte nicht geladen werden",
  statusUnavailableDetail: "Die Einstellungen lassen sich weiter bearbeiten und speichern; der Zustand erscheint hier, sobald er verfügbar ist.",
  stateActive: "Prüft",
  stateInactive: (reason: string) => `Prüft nicht: ${reason}`,
  inactiveReasons: {
    disabled: "Ausgeschaltet",
    "not-running": "Der Schutz wurde noch nicht gestartet",
    "process-data-unavailable": "Prozessdaten können nicht gelesen werden",
    "process-exit-proof-unavailable": "Ob Prozesse beendet wurden, lässt sich nicht bestätigen",
    "process-capacity": "Mehr Prozesse, als verfolgt werden können",
    stopped: "Angehalten"
  },
  currentAction: "Wirksame Aktion",
  exclusionsTitle: "Ausgeschlossene Prozesse",
  exclusionsEmpty: "Derzeit sind keine Prozesse ausgeschlossen.",
  exclusionReasons: {
    "resource-manager": "Ein Prozess dieser Software",
    "resource-manager-process": "Ein Prozess dieser Software",
    "protected-process": "Auf der Ausnahmeliste",
    "critical-process": "Ein kritischer Systemprozess",
    "unsupported-platform": "Auf diesem System nicht unterstützt",
    "identity-changed": "Der Prozess hat sich geändert",
    "cannot-open": "Auf den Prozess kann nicht zugegriffen werden",
    "criticality-unavailable": "Ob es ein kritischer Systemprozess ist, lässt sich nicht bestätigen"
  },
  process: (name: string, processId: number) => `${name} (PID ${processId})`,
  percent: (value: string) => `${value} %`,
  seconds: (value: string) => `${value} s`,
  priorities: { High: "Hoch", RealTime: "Echtzeit" },
  records: {
    tab: "CPU-Monopolschutz",
    intro: "Der CPU-Monopolschutz legt für jeden behandelten Prozess einen Eintrag an. Nachdem ein Prozess auf normale Priorität zurückgesetzt wurde, wandert sein Eintrag in den Verlauf, sobald der Prozess endet oder Sie seine Priorität selbst festlegen; eine fehlgeschlagene Aktion steht direkt im Verlauf. Einträge bleiben erhalten und werden nie automatisch gelöscht.",
    loading: "Einträge des CPU-Monopolschutzes werden geladen…",
    unavailable: "Die Einträge des CPU-Monopolschutzes konnten nicht geladen werden",
    empty: "Noch keine Aktionen festgehalten.",
    count: (count: string, capacity: string) => `Einträge: ${count}; höchstens ${capacity}.`,
    unrecorded: (count: number) => plural("de-DE", count, {
      one: "Die Liste ist voll; {count} spätere Aktion wurde nicht festgehalten. Nach dem Leeren des Verlaufs wird wieder festgehalten.",
      other: "Die Liste ist voll; {count} spätere Aktionen wurden nicht festgehalten. Nach dem Leeren des Verlaufs wird wieder festgehalten."
    }),
    outcomes: { normalized: "Auf normale Priorität zurückgesetzt", failed: "Aktion fehlgeschlagen" },
    states: { active: "Noch wirksam", history: "Verlauf" },
    time: "Zeit",
    cpu: "CPU-Auslastung",
    previousPriority: "Vorherige Priorität",
    threshold: "Damalige Schwelle",
    duration: "Damalige Dauer",
    reason: "Grund",
    detail: "Details",
    endedAt: "Beendet",
    endReason: "Grund für das Ende"
  },
  actionReasons: {
    "process-exited": "Der Prozess wurde beendet",
    "user-priority-set": "Seine Priorität wurde gesondert festgelegt",
    "write-failed": "Das Schreiben der Priorität ist fehlgeschlagen",
    "identity-changed": "Der Prozess hat sich geändert",
    "priority-changed": "Ein anderes Programm hat seine Priorität geändert",
    "priority-unavailable": "Seine Priorität kann nicht gelesen werden",
    "priority-write-failed": "Das Schreiben der Priorität ist fehlgeschlagen",
    "priority-readback-unavailable": "Die Priorität konnte nach dem Schreiben nicht zurückgelesen werden",
    "priority-readback-mismatch": "Die nach dem Schreiben gelesene Priorität stimmt nicht überein"
  },
  notice: {
    title: "CPU-Monopolschutz",
    normalized: (target: string) => `${target} wurde auf normale Priorität zurückgesetzt.`,
    failed: (target: string, reason: string) => `${target} konnte nicht angepasst werden: ${reason}`,
    openRecords: "Einträge anzeigen"
  }
};
