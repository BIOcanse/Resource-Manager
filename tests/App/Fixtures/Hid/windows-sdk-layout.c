#include <windows.h>
#include <winternl.h>
#ifdef RESOURCE_MANAGER_SDK_HID_HEADERS
#include <resource_manager_sdk_hidpi.h>
#else
#include <hidsdi.h>
#endif
#include <stdio.h>
#include <stddef.h>

_Static_assert(sizeof(HIDP_CAPS) == 64, "HIDP_CAPS");
_Static_assert(sizeof(HIDP_BUTTON_CAPS) == 72, "HIDP_BUTTON_CAPS");
_Static_assert(sizeof(HIDP_VALUE_CAPS) == 72, "HIDP_VALUE_CAPS");

static void print_hex(const void *data, size_t count) {
    const unsigned char *bytes = data;
    putchar('"');
    for (size_t i = 0; i < count; i++) printf("%02X", bytes[i]);
    putchar('"');
}

int main(void) {
    HIDP_CAPS caps = {0}; HIDP_BUTTON_CAPS buttons = {0}; HIDP_VALUE_CAPS values = {0};
    caps.Usage = 4; caps.UsagePage = 1;
    caps.InputReportByteLength = 65; caps.OutputReportByteLength = 33; caps.FeatureReportByteLength = 17;
    caps.NumberInputButtonCaps = caps.NumberOutputButtonCaps = caps.NumberFeatureButtonCaps = 1;
    caps.NumberInputValueCaps = caps.NumberOutputValueCaps = caps.NumberFeatureValueCaps = 1;
    buttons.UsagePage = 9; buttons.ReportID = 250; buttons.IsAlias = 1; buttons.BitField = 0x8002;
    buttons.LinkCollection = 35; buttons.LinkUsagePage = 1; buttons.LinkUsage = 4;
    buttons.IsRange = buttons.IsStringRange = buttons.IsDesignatorRange = buttons.IsAbsolute = 1;
    buttons.ReportCount = 37;
    buttons.Range.UsageMin = 1; buttons.Range.UsageMax = 16;
    buttons.Range.StringMin = 4; buttons.Range.StringMax = 7;
    buttons.Range.DesignatorMin = 11; buttons.Range.DesignatorMax = 15;
    buttons.Range.DataIndexMin = 255; buttons.Range.DataIndexMax = 270;
    values.UsagePage = 0xFF01; values.ReportID = 255; values.BitField = 0x1234;
    values.LinkCollection = 73; values.LinkUsagePage = 0xFF02; values.LinkUsage = 0xFF03;
    values.HasNull = 1; values.BitSize = 32; values.ReportCount = 5;
    values.UnitsExp = 0xFFFFFFFE; values.Units = 0xFFFEEFFE;
    values.LogicalMin = (-2147483647L - 1); values.LogicalMax = 2147483647L;
    values.PhysicalMin = -120; values.PhysicalMax = 65537;
    values.NotRange.Usage = 48; values.NotRange.Reserved1 = 0xFFFF;
    values.NotRange.StringIndex = 55; values.NotRange.Reserved2 = 0xFFFF;
    values.NotRange.DesignatorIndex = 66; values.NotRange.Reserved3 = 0xFFFF;
    values.NotRange.DataIndex = 77; values.NotRange.Reserved4 = 0xFFFF;
    printf("{\"origin\":\"Windows SDK 10.0.26100.0 hidpi.h; synthetic data, no hardware IO\","
        "\"capsSize\":%zu,\"buttonSize\":%zu,\"valueSize\":%zu,\"success\":%lu,"
        "\"unitsOffset\":%zu,\"selectionOffset\":%zu,\"buttonCountOffset\":%zu,\"capsHex\":",
        sizeof caps, sizeof buttons, sizeof values, (unsigned long)HIDP_STATUS_SUCCESS,
        offsetof(HIDP_VALUE_CAPS, UnitsExp), offsetof(HIDP_VALUE_CAPS, NotRange), offsetof(HIDP_BUTTON_CAPS, ReportCount));
    print_hex(&caps, sizeof caps); printf(",\"buttonHex\":"); print_hex(&buttons, sizeof buttons);
    printf(",\"valueHex\":"); print_hex(&values, sizeof values); puts("}");
    return 0;
}
