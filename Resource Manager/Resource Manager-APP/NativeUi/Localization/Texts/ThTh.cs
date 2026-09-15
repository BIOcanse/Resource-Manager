namespace ResourceManager.NativeUi.Localization.Texts;

internal static class ThTh
{
    public static readonly NativeText Text = new()
    {
        AppName = "ตัวจัดการทรัพยากร",
        WindowTitleFormat = "ตัวจัดการทรัพยากร - {0}",
        TrayTooltipFormat = "ตัวจัดการทรัพยากร - {0}",

        AvailabilityPanelName = "สถานะบริการภายในเครื่อง",
        ConnectingTitle = "กำลังเชื่อมต่อกับบริการภายในเครื่อง",
        ReconnectingTitle = "กำลังเชื่อมต่อกับบริการภายในเครื่องอีกครั้ง",
        ConnectingDetail = "กำลังตรวจสอบตัวตนของบริการและสร้างเซสชัน",
        RetryButton = "ลองใหม่",
        RetryButtonDescription = "เชื่อมต่อกับบริการภายในเครื่องอีกครั้งทันที",
        DiagnosticsButton = "เปิดโฟลเดอร์การวินิจฉัย",
        DiagnosticsButtonDescription = "เปิดโฟลเดอร์ไฟล์การวินิจฉัยภายในเครื่อง",
        ExitButton = "ออก",
        ExitButtonDescription = "ออกจากตัวจัดการทรัพยากร",
        BackendUnavailableTitle = "บริการภายในเครื่องไม่พร้อมใช้งานชั่วคราว",
        BackendUnavailableDetail = "ตัวจัดการทรัพยากรจะพยายามเชื่อมต่อต่อไปในเบื้องหลัง",
        FrontendLoadingTitle = "กำลังโหลดส่วนติดต่อผู้ใช้",
        FrontendLoadingDetail = "ตรวจสอบบริการภายในเครื่องแล้ว กำลังโหลดส่วนติดต่อผู้ใช้ของแอป",
        FrontendUnavailableTitle = "ส่วนติดต่อผู้ใช้ของแอปไม่พร้อมใช้งานชั่วคราว",
        FrontendUnavailableDetail = "บริการภายในเครื่องยังทำงานอยู่ จึงสามารถโหลดส่วนติดต่อผู้ใช้ใหม่ได้",

        StatusConnecting = "กำลังเชื่อมต่อกับบริการภายในเครื่อง",
        StatusReconnecting = "กำลังเชื่อมต่อกับบริการภายในเครื่องอีกครั้ง",
        StatusBackendUnavailable = "บริการภายในเครื่องไม่พร้อมใช้งาน",
        StatusShuttingDown = "กำลังออก",
        StatusBackendReady = "บริการภายในเครื่องพร้อมแล้ว",
        StatusFrontendLoading = "กำลังโหลดส่วนติดต่อผู้ใช้",
        StatusReady = "พร้อม",
        StatusFrontendUnavailable = "ส่วนติดต่อผู้ใช้ไม่พร้อมใช้งาน",
        ActionReconnectBackend = "เชื่อมต่อกับบริการภายในเครื่องอีกครั้ง",
        ActionReloadFrontend = "โหลดส่วนติดต่อผู้ใช้ใหม่",

        BackendProcessExitedFormat = "กระบวนการของบริการภายในเครื่องสิ้นสุดด้วยรหัส {0}",
        BackendHealthProbeFailedFormat = "บริการภายในเครื่องล้มเหลวในการตรวจสอบสถานะ {0} ครั้งติดต่อกัน",
        BackendStartFailedFormat = "เริ่มบริการภายในเครื่องไม่สำเร็จ: {0}",
        BackendLifecycleMonitorFailedFormat = "การตรวจสอบวงจรชีวิตของบริการภายในเครื่องล้มเหลว: {0}",
        BackendHttpErrorFormat = "บริการภายในเครื่องส่งคืน HTTP {0}",
        BackendEntryMissing = "อิมเมจสุดท้ายไม่มีจุดเริ่มต้นของบริการเบื้องหลังภายในเครื่อง",
        BackendNotOwned = "บริการเบื้องหลังภายในเครื่องยังไม่พร้อม ส่วนติดต่อผู้ใช้แบบเนทีฟไม่มีสิทธิ์เริ่มบริการ โปรดเริ่มหรือซ่อมแซม ResourceManager.Service",
        BackendPipelineNotReady = "ไปป์ไลน์คำขอของบริการภายในเครื่องยังไม่พร้อม",
        NoProcessToTerminate = "ไม่มีกระบวนการที่จะสิ้นสุด",
        TerminateRequestCompleted = "คำขอสิ้นสุดกระบวนการเสร็จสมบูรณ์แล้ว",

        WebViewInitializationFailed = "เริ่มต้นส่วนติดต่อผู้ใช้แบบฝังไม่สำเร็จ เปิดโฟลเดอร์การวินิจฉัยเพื่อดูรายละเอียด",
        FrontendIdentityVerificationFailed = "การตรวจสอบตัวตนของทรัพยากรส่วนหน้าล้มเหลว เปิดโฟลเดอร์การวินิจฉัยเพื่อดูรายละเอียด",
        FrontendLoadFailed = "ไม่สามารถโหลดส่วนติดต่อผู้ใช้ภายในเครื่องจากบริการที่ตรวจสอบแล้ว",
        WebViewRuntimeMissing = "ไม่พบ WebView2 Runtime ที่เข้ากันได้ โปรดติดตั้งรันไทม์แบบใช้ร่วมกันแล้วลองใหม่",

        TrayBackendStatusConnecting = "บริการภายในเครื่อง: กำลังเชื่อมต่อ",
        TrayStatusFormat = "สถานะ: {0}",
        TrayOpen = "เปิด",
        TrayReconnectBackend = "เชื่อมต่อกับบริการภายในเครื่องอีกครั้ง",
        TrayExit = "ออก",
        BackendUnavailableBalloonTitle = "บริการภายในเครื่องไม่พร้อมใช้งาน",
        NoVerifiedSession = "ยังไม่ได้สร้างเซสชันกับบริการภายในเครื่องที่ตรวจสอบได้",
        DiagnosticsOpenFailed = "ไม่สามารถเปิดโฟลเดอร์การวินิจฉัยได้",

        SelectSoftwareRootFolder = "เลือกโฟลเดอร์รากของแอป",

        ForceTerminateHotkeyBalloonTitle = "แป้นลัดบังคับสิ้นสุด",
        NoForegroundProcessToTerminate = "ไม่มีแอปที่อยู่เบื้องหน้าหรือแอปที่ไม่ตอบสนองให้สิ้นสุด",
        BackendUnavailableNoTerminate = "บริการภายในเครื่องไม่พร้อมใช้งาน จึงไม่มีกระบวนการใดถูกสิ้นสุด",
        ForceTerminateFailed = "การบังคับสิ้นสุดล้มเหลว โปรดลองใหม่ภายหลัง",
        ForceTerminateHotkeyDisabled = "แป้นลัดบังคับสิ้นสุดถูกปิดใช้งาน: ต้องใช้ Ctrl, Alt หรือ Win ร่วมกับแป้นที่ไม่ใช่แป้นปรับแต่ง",
        ForceTerminateHotkeyEnableFailed = "ไม่สามารถเปิดใช้งานแป้นลัดบังคับสิ้นสุดได้ โปรดตรวจสอบชุดแป้นแล้วลองใหม่",
        HotkeyNeedsAtLeastOneKey = "แป้นลัดต้องมีอย่างน้อยหนึ่งแป้น",
        ForceTerminateHotkeyNeedsModifier = "แป้นลัดบังคับสิ้นสุดต้องใช้ Ctrl, Alt หรือ Win ร่วมกับแป้นที่ไม่ใช่แป้นปรับแต่ง",
        KeyboardHookRegisterFailed = "ไม่สามารถลงทะเบียนฮุกแป้นพิมพ์ส่วนกลางแบบกำหนดเองได้",
        MouseHookRegisterFailed = "ไม่สามารถลงทะเบียนฮุกเมาส์ส่วนกลางแบบกำหนดเองได้",

        TaskManagerHotkeyReplacement = "การแทนที่แป้นลัดตัวจัดการงาน",
        TaskManagerHotkeyEnableFailed = "ไม่สามารถเปิดใช้งานแป้นลัดตัวจัดการงานได้ โปรดลองใหม่ภายหลัง",
        TaskManagerHotkeyDisableFailed = "ไม่สามารถปิดใช้งานแป้นลัดตัวจัดการงานได้ โปรดลองใหม่ภายหลัง",
        TaskManagerHotkeyForeignSetting = "ตรวจพบการตั้งค่าแป้นลัดตัวจัดการงานอื่น และคงการตั้งค่าเดิมไว้",
        TaskManagerHotkeyNeedsAdminToEnable = "ต้องมีสิทธิ์ผู้ดูแลระบบเพื่อรับช่วงแป้นลัดตัวจัดการงานขณะที่ตัวจัดการทรัพยากรไม่ได้ทำงาน",
        TaskManagerHotkeyNeedsAdminToDisable = "ต้องมีสิทธิ์ผู้ดูแลระบบเพื่อปิดการแทนที่แป้นลัดตัวจัดการงานทั้งหมด",
        TaskManagerHotkeyEnabled = "เปิดใช้งานแป้นลัดตัวจัดการงานแล้ว",
        TaskManagerHotkeyDisabled = "ปิดใช้งานแป้นลัดตัวจัดการงานแล้ว",
        TaskManagerShortcutHookRegisterFailed = "ไม่สามารถลงทะเบียนฮุกแป้นลัด Ctrl+Shift+Esc ได้",
        TaskManagerShortcutHookRemoveFailed = "ไม่สามารถลบฮุกแป้นลัด Ctrl+Shift+Esc ได้"
    };
}
