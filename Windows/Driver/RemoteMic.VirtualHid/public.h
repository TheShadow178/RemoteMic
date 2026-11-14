#pragma once

#include <initguid.h>

// Keep in sync with VirtualHidKeyboard.cs.
DEFINE_GUID(GUID_DEVINTERFACE_REMOTEMIC_VIRTUAL_HID,
    0xd935fb95, 0x3e8f, 0x44c8, 0xb4, 0x60, 0x1a, 0xab, 0x33, 0x1f, 0xb9, 0xb1);

#define IOCTL_REMOTEMIC_SUBMIT_KEYBOARD_REPORT \
    CTL_CODE(FILE_DEVICE_UNKNOWN, 0x800, METHOD_BUFFERED, FILE_WRITE_DATA)

#pragma pack(push, 1)
typedef struct _REMOTEMIC_KEYBOARD_REPORT {
    UCHAR Modifier;
    UCHAR Reserved;
    UCHAR Keys[6];
} REMOTEMIC_KEYBOARD_REPORT, *PREMOTEMIC_KEYBOARD_REPORT;
#pragma pack(pop)
