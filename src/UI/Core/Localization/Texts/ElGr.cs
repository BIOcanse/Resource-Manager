namespace ResourceManager.NativeUi.Localization.Texts;

internal static class ElGr
{
    public static readonly NativeText Text = new()
    {
        AppName = "Διαχείριση πόρων",
        WindowTitleFormat = "Διαχείριση πόρων - {0}",
        TrayTooltipFormat = "Διαχείριση πόρων - {0}",

        AvailabilityPanelName = "Κατάσταση τοπικής υπηρεσίας",
        ConnectingTitle = "Σύνδεση με την τοπική υπηρεσία",
        ReconnectingTitle = "Επανασύνδεση με την τοπική υπηρεσία",
        ConnectingDetail = "Γίνεται επαλήθευση της ταυτότητας της υπηρεσίας και δημιουργία περιόδου λειτουργίας.",
        RetryButton = "Δοκιμή ξανά",
        RetryButtonDescription = "Άμεση επανασύνδεση με την τοπική υπηρεσία",
        DiagnosticsButton = "Άνοιγμα φακέλου διαγνωστικών",
        DiagnosticsButtonDescription = "Άνοιγμα του φακέλου με τα τοπικά αρχεία διαγνωστικών",
        ExitButton = "Έξοδος",
        ExitButtonDescription = "Έξοδος από τη Διαχείριση πόρων",
        BackendUnavailableTitle = "Η τοπική υπηρεσία δεν είναι προσωρινά διαθέσιμη",
        BackendUnavailableDetail = "Η Διαχείριση πόρων συνεχίζει να προσπαθεί να συνδεθεί στο παρασκήνιο.",
        FrontendLoadingTitle = "Φόρτωση της διεπαφής",
        FrontendLoadingDetail = "Η τοπική υπηρεσία επαληθεύτηκε. Φορτώνεται η διεπαφή της εφαρμογής.",
        FrontendUnavailableTitle = "Η διεπαφή της εφαρμογής δεν είναι προσωρινά διαθέσιμη",
        FrontendUnavailableDetail = "Η τοπική υπηρεσία εξακολουθεί να εκτελείται, οπότε η διεπαφή μπορεί να φορτωθεί ξανά.",

        StatusConnecting = "Σύνδεση με την τοπική υπηρεσία",
        StatusReconnecting = "Επανασύνδεση με την τοπική υπηρεσία",
        StatusBackendUnavailable = "Η τοπική υπηρεσία δεν είναι διαθέσιμη",
        StatusShuttingDown = "Γίνεται έξοδος",
        StatusBackendReady = "Η τοπική υπηρεσία είναι έτοιμη",
        StatusFrontendLoading = "Φόρτωση της διεπαφής",
        StatusReady = "Έτοιμο",
        StatusFrontendUnavailable = "Η διεπαφή δεν είναι διαθέσιμη",
        ActionReconnectBackend = "Επανασύνδεση με την τοπική υπηρεσία",
        ActionReloadFrontend = "Επαναφόρτωση της διεπαφής",

        BackendProcessExitedFormat = "Η διεργασία της τοπικής υπηρεσίας τερματίστηκε με κωδικό {0}.",
        BackendHealthProbeFailedFormat = "Η τοπική υπηρεσία απέτυχε σε {0} διαδοχικούς ελέγχους κατάστασης.",
        BackendStartFailedFormat = "Η εκκίνηση της τοπικής υπηρεσίας απέτυχε: {0}",
        BackendLifecycleMonitorFailedFormat = "Η παρακολούθηση του κύκλου ζωής της τοπικής υπηρεσίας απέτυχε: {0}",
        BackendHttpErrorFormat = "Η τοπική υπηρεσία επέστρεψε HTTP {0}.",
        BackendEntryMissing = "Από την τελική εικόνα λείπει το σημείο εισόδου της τοπικής υπηρεσίας παρασκηνίου.",
        BackendNotOwned = "Η τοπική υπηρεσία παρασκηνίου δεν είναι έτοιμη. Η εγγενής διεπαφή δεν έχει δικαίωμα εκκίνησης της υπηρεσίας. Εκκινήστε ή επιδιορθώστε την υπηρεσία ResourceManager.Service.",
        BackendPipelineNotReady = "Η διοχέτευση αιτημάτων της τοπικής υπηρεσίας δεν είναι έτοιμη.",
        NoProcessToTerminate = "Δεν υπάρχει διεργασία προς τερματισμό.",
        TerminateRequestCompleted = "Το αίτημα τερματισμού της διεργασίας ολοκληρώθηκε.",

        WebViewInitializationFailed = "Η προετοιμασία της ενσωματωμένης διεπαφής απέτυχε. Ανοίξτε τον φάκελο διαγνωστικών για λεπτομέρειες.",
        FrontendIdentityVerificationFailed = "Η επαλήθευση ταυτότητας των πόρων της διεπαφής απέτυχε. Ανοίξτε τον φάκελο διαγνωστικών για λεπτομέρειες.",
        FrontendLoadFailed = "Δεν ήταν δυνατή η φόρτωση της τοπικής διεπαφής από την επαληθευμένη υπηρεσία.",
        WebViewRuntimeMissing = "Δεν βρέθηκε συμβατός χρόνος εκτέλεσης WebView2. Εγκαταστήστε τον κοινόχρηστο χρόνο εκτέλεσης και δοκιμάστε ξανά.",

        TrayBackendStatusConnecting = "Τοπική υπηρεσία: σύνδεση",
        TrayStatusFormat = "Κατάσταση: {0}",
        TrayOpen = "Άνοιγμα",
        TrayReconnectBackend = "Επανασύνδεση με την τοπική υπηρεσία",
        TrayExit = "Έξοδος",
        BackendUnavailableBalloonTitle = "Η τοπική υπηρεσία δεν είναι διαθέσιμη",
        NoVerifiedSession = "Δεν έχει δημιουργηθεί ακόμη επαληθεύσιμη περίοδος λειτουργίας με την τοπική υπηρεσία.",
        DiagnosticsOpenFailed = "Δεν ήταν δυνατό το άνοιγμα του φακέλου διαγνωστικών.",

        SelectSoftwareRootFolder = "Επιλέξτε τον ριζικό φάκελο της εφαρμογής",

        ForceTerminateHotkeyBalloonTitle = "Συντόμευση αναγκαστικού τερματισμού",
        NoForegroundProcessToTerminate = "Δεν υπάρχει εφαρμογή στο προσκήνιο ή εφαρμογή που δεν αποκρίνεται για τερματισμό.",
        BackendUnavailableNoTerminate = "Η τοπική υπηρεσία δεν είναι διαθέσιμη, επομένως δεν τερματίστηκε καμία διεργασία.",
        ForceTerminateFailed = "Ο αναγκαστικός τερματισμός απέτυχε. Δοκιμάστε ξανά αργότερα.",
        ForceTerminateHotkeyDisabled = "Η συντόμευση αναγκαστικού τερματισμού είναι απενεργοποιημένη: πρέπει να συνδυάζει Ctrl, Alt ή Win με ένα πλήκτρο που δεν είναι τροποποιητής.",
        ForceTerminateHotkeyEnableFailed = "Δεν ήταν δυνατή η ενεργοποίηση της συντόμευσης αναγκαστικού τερματισμού. Ελέγξτε τον συνδυασμό πλήκτρων και δοκιμάστε ξανά.",
        HotkeyNeedsAtLeastOneKey = "Μια συντόμευση χρειάζεται τουλάχιστον ένα πλήκτρο.",
        ForceTerminateHotkeyNeedsModifier = "Η συντόμευση αναγκαστικού τερματισμού πρέπει να συνδυάζει Ctrl, Alt ή Win με ένα πλήκτρο που δεν είναι τροποποιητής.",
        KeyboardHookRegisterFailed = "Δεν ήταν δυνατή η καταχώριση της προσαρμοσμένης καθολικής αγκύλωσης πληκτρολογίου.",
        MouseHookRegisterFailed = "Δεν ήταν δυνατή η καταχώριση της προσαρμοσμένης καθολικής αγκύλωσης ποντικιού.",

        TaskManagerHotkeyReplacement = "Αντικατάσταση συντόμευσης Διαχείρισης εργασιών",
        TaskManagerHotkeyEnableFailed = "Δεν ήταν δυνατή η ενεργοποίηση της συντόμευσης Διαχείρισης εργασιών. Δοκιμάστε ξανά αργότερα.",
        TaskManagerHotkeyDisableFailed = "Δεν ήταν δυνατή η απενεργοποίηση της συντόμευσης Διαχείρισης εργασιών. Δοκιμάστε ξανά αργότερα.",
        TaskManagerHotkeyForeignSetting = "Εντοπίστηκε άλλη ρύθμιση συντόμευσης Διαχείρισης εργασιών και διατηρήθηκε αμετάβλητη.",
        TaskManagerHotkeyNeedsAdminToEnable = "Απαιτούνται δικαιώματα διαχειριστή για την ανάληψη της συντόμευσης Διαχείρισης εργασιών όταν η Διαχείριση πόρων δεν εκτελείται.",
        TaskManagerHotkeyNeedsAdminToDisable = "Απαιτούνται δικαιώματα διαχειριστή για την πλήρη απενεργοποίηση της αντικατάστασης της συντόμευσης Διαχείρισης εργασιών.",
        TaskManagerHotkeyEnabled = "Η συντόμευση Διαχείρισης εργασιών είναι ενεργοποιημένη.",
        TaskManagerHotkeyDisabled = "Η συντόμευση Διαχείρισης εργασιών είναι απενεργοποιημένη.",
        TaskManagerShortcutHookRegisterFailed = "Δεν ήταν δυνατή η καταχώριση της αγκύλωσης για τη συντόμευση Ctrl+Shift+Esc.",
        TaskManagerShortcutHookRemoveFailed = "Δεν ήταν δυνατή η κατάργηση της αγκύλωσης για τη συντόμευση Ctrl+Shift+Esc."
    };
}
