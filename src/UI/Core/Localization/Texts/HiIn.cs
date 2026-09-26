namespace ResourceManager.NativeUi.Localization.Texts;

internal static class HiIn
{
    public static readonly NativeText Text = new()
    {
        AppName = "रिसोर्स मैनेजर",
        WindowTitleFormat = "रिसोर्स मैनेजर - {0}",
        TrayTooltipFormat = "रिसोर्स मैनेजर - {0}",

        AvailabilityPanelName = "स्थानीय सेवा की स्थिति",
        ConnectingTitle = "स्थानीय सेवा से कनेक्ट किया जा रहा है",
        ReconnectingTitle = "स्थानीय सेवा से फिर से कनेक्ट किया जा रहा है",
        ConnectingDetail = "सेवा की पहचान सत्यापित की जा रही है और सत्र स्थापित किया जा रहा है।",
        RetryButton = "पुनः प्रयास करें",
        RetryButtonDescription = "स्थानीय सेवा से अभी फिर से कनेक्ट करें",
        DiagnosticsButton = "डायग्नोस्टिक्स फ़ोल्डर खोलें",
        DiagnosticsButtonDescription = "स्थानीय डायग्नोस्टिक फ़ाइलों का फ़ोल्डर खोलें",
        ExitButton = "बाहर निकलें",
        ExitButtonDescription = "रिसोर्स मैनेजर से बाहर निकलें",
        BackendUnavailableTitle = "स्थानीय सेवा अस्थायी रूप से अनुपलब्ध है",
        BackendUnavailableDetail = "रिसोर्स मैनेजर पृष्ठभूमि में कनेक्ट करने का प्रयास जारी रखता है।",
        FrontendLoadingTitle = "इंटरफ़ेस लोड हो रहा है",
        FrontendLoadingDetail = "स्थानीय सेवा सत्यापित है; ऐप्लिकेशन इंटरफ़ेस लोड हो रहा है।",
        FrontendUnavailableTitle = "ऐप्लिकेशन इंटरफ़ेस अस्थायी रूप से अनुपलब्ध है",
        FrontendUnavailableDetail = "स्थानीय सेवा अब भी चल रही है, इसलिए इंटरफ़ेस फिर से लोड किया जा सकता है।",

        StatusConnecting = "स्थानीय सेवा से कनेक्ट किया जा रहा है",
        StatusReconnecting = "स्थानीय सेवा से फिर से कनेक्ट किया जा रहा है",
        StatusBackendUnavailable = "स्थानीय सेवा अनुपलब्ध",
        StatusShuttingDown = "बाहर निकला जा रहा है",
        StatusBackendReady = "स्थानीय सेवा तैयार",
        StatusFrontendLoading = "इंटरफ़ेस लोड हो रहा है",
        StatusReady = "तैयार",
        StatusFrontendUnavailable = "इंटरफ़ेस अनुपलब्ध",
        ActionReconnectBackend = "स्थानीय सेवा से फिर से कनेक्ट करें",
        ActionReloadFrontend = "इंटरफ़ेस फिर से लोड करें",

        BackendProcessExitedFormat = "स्थानीय सेवा की प्रक्रिया {0} कोड के साथ समाप्त हुई।",
        BackendHealthProbeFailedFormat = "स्थानीय सेवा लगातार {0} स्वास्थ्य जाँचों में विफल रही।",
        BackendStartFailedFormat = "स्थानीय सेवा प्रारंभ नहीं हो सकी: {0}",
        BackendLifecycleMonitorFailedFormat = "स्थानीय सेवा के जीवनचक्र की निगरानी विफल रही: {0}",
        BackendHttpErrorFormat = "स्थानीय सेवा ने HTTP {0} लौटाया।",
        BackendEntryMissing = "अंतिम इमेज में स्थानीय बैकएंड सेवा का प्रवेश बिंदु मौजूद नहीं है।",
        BackendNotOwned = "स्थानीय बैकएंड सेवा तैयार नहीं है; नेटिव इंटरफ़ेस के पास सेवा प्रारंभ करने का अधिकार नहीं है। ResourceManager.Service प्रारंभ करें या ठीक करें।",
        BackendPipelineNotReady = "स्थानीय सेवा की अनुरोध पाइपलाइन तैयार नहीं है।",
        NoProcessToTerminate = "समाप्त करने के लिए कोई प्रक्रिया नहीं है।",
        TerminateRequestCompleted = "प्रक्रिया समाप्त करने का अनुरोध पूरा हो गया।",

        WebViewInitializationFailed = "एम्बेडेड इंटरफ़ेस प्रारंभ नहीं हो सका। विवरण के लिए डायग्नोस्टिक्स फ़ोल्डर खोलें।",
        FrontendIdentityVerificationFailed = "फ़्रंटएंड संसाधनों का पहचान सत्यापन विफल रहा। विवरण के लिए डायग्नोस्टिक्स फ़ोल्डर खोलें।",
        FrontendLoadFailed = "सत्यापित सेवा से स्थानीय इंटरफ़ेस लोड नहीं किया जा सका।",
        WebViewRuntimeMissing = "कोई संगत WebView2 रनटाइम नहीं मिला। साझा रनटाइम इंस्टॉल करें और पुनः प्रयास करें।",

        TrayBackendStatusConnecting = "स्थानीय सेवा: कनेक्ट हो रही है",
        TrayStatusFormat = "स्थिति: {0}",
        TrayOpen = "खोलें",
        TrayReconnectBackend = "स्थानीय सेवा से फिर से कनेक्ट करें",
        TrayExit = "बाहर निकलें",
        BackendUnavailableBalloonTitle = "स्थानीय सेवा अनुपलब्ध",
        NoVerifiedSession = "स्थानीय सेवा के साथ अभी तक कोई सत्यापन योग्य सत्र स्थापित नहीं हुआ है।",
        DiagnosticsOpenFailed = "डायग्नोस्टिक्स फ़ोल्डर नहीं खोला जा सका।",

        SelectSoftwareRootFolder = "ऐप्लिकेशन का रूट फ़ोल्डर चुनें",

        ForceTerminateHotkeyBalloonTitle = "बलपूर्वक समाप्ति हॉटकी",
        NoForegroundProcessToTerminate = "समाप्त करने के लिए कोई अग्रभूमि या अनुत्तरदायी ऐप्लिकेशन नहीं है।",
        BackendUnavailableNoTerminate = "स्थानीय सेवा अनुपलब्ध है, इसलिए कोई प्रक्रिया समाप्त नहीं की गई।",
        ForceTerminateFailed = "बलपूर्वक समाप्ति विफल रही। बाद में पुनः प्रयास करें।",
        ForceTerminateHotkeyDisabled = "बलपूर्वक समाप्ति हॉटकी अक्षम है: इसमें Ctrl, Alt या Win के साथ एक ग़ैर-संशोधक कुंजी होनी चाहिए।",
        ForceTerminateHotkeyEnableFailed = "बलपूर्वक समाप्ति हॉटकी सक्षम नहीं की जा सकी। कुंजी संयोजन जाँचें और पुनः प्रयास करें।",
        HotkeyNeedsAtLeastOneKey = "हॉटकी के लिए कम से कम एक कुंजी आवश्यक है।",
        ForceTerminateHotkeyNeedsModifier = "बलपूर्वक समाप्ति हॉटकी में Ctrl, Alt या Win के साथ एक ग़ैर-संशोधक कुंजी होनी चाहिए।",
        KeyboardHookRegisterFailed = "कस्टम ग्लोबल कीबोर्ड हुक पंजीकृत नहीं किया जा सका।",
        MouseHookRegisterFailed = "कस्टम ग्लोबल माउस हुक पंजीकृत नहीं किया जा सका।",

        TaskManagerHotkeyReplacement = "टास्क मैनेजर हॉटकी प्रतिस्थापन",
        TaskManagerHotkeyEnableFailed = "टास्क मैनेजर हॉटकी सक्षम नहीं की जा सकी। बाद में पुनः प्रयास करें।",
        TaskManagerHotkeyDisableFailed = "टास्क मैनेजर हॉटकी अक्षम नहीं की जा सकी। बाद में पुनः प्रयास करें।",
        TaskManagerHotkeyForeignSetting = "टास्क मैनेजर हॉटकी की कोई अन्य सेटिंग मिली और उसे अपरिवर्तित रखा गया।",
        TaskManagerHotkeyNeedsAdminToEnable = "रिसोर्स मैनेजर के न चलने पर टास्क मैनेजर हॉटकी संभालने के लिए व्यवस्थापक अनुमति आवश्यक है।",
        TaskManagerHotkeyNeedsAdminToDisable = "टास्क मैनेजर हॉटकी प्रतिस्थापन को पूरी तरह बंद करने के लिए व्यवस्थापक अनुमति आवश्यक है।",
        TaskManagerHotkeyEnabled = "टास्क मैनेजर हॉटकी सक्षम है।",
        TaskManagerHotkeyDisabled = "टास्क मैनेजर हॉटकी अक्षम है।",
        TaskManagerShortcutHookRegisterFailed = "Ctrl+Shift+Esc हॉटकी हुक पंजीकृत नहीं किया जा सका।",
        TaskManagerShortcutHookRemoveFailed = "Ctrl+Shift+Esc हॉटकी हुक हटाया नहीं जा सका।"
    };
}
