#include <ntddk.h>
#include <wdf.h>
#include <vhf.h>
#include "public.h"

DRIVER_INITIALIZE DriverEntry;
EVT_WDF_DRIVER_DEVICE_ADD RemoteMicEvtDeviceAdd;
EVT_WDF_IO_QUEUE_IO_DEVICE_CONTROL RemoteMicEvtIoDeviceControl;
EVT_WDF_OBJECT_CONTEXT_CLEANUP RemoteMicEvtDeviceCleanup;

typedef struct _DEVICE_CONTEXT {
    VHFHANDLE VhfHandle;
} DEVICE_CONTEXT, *PDEVICE_CONTEXT;

WDF_DECLARE_CONTEXT_TYPE_WITH_NAME(DEVICE_CONTEXT, RemoteMicGetDeviceContext);

// Standard boot-keyboard descriptor: 8 modifier bits, LED output, six keys.
static UCHAR g_KeyboardReportDescriptor[] = {
    0x05, 0x01,       // Usage Page (Generic Desktop)
    0x09, 0x06,       // Usage (Keyboard)
    0xA1, 0x01,       // Collection (Application)
    0x05, 0x07,       //   Usage Page (Keyboard)
    0x19, 0xE0,       //   Usage Minimum (Left Control)
    0x29, 0xE7,       //   Usage Maximum (Right GUI)
    0x15, 0x00,       //   Logical Minimum (0)
    0x25, 0x01,       //   Logical Maximum (1)
    0x75, 0x01,       //   Report Size (1)
    0x95, 0x08,       //   Report Count (8)
    0x81, 0x02,       //   Input (Data, Variable, Absolute)
    0x95, 0x01,       //   Report Count (1)
    0x75, 0x08,       //   Report Size (8)
    0x81, 0x01,       //   Input (Constant)
    0x95, 0x05,       //   Report Count (5)
    0x75, 0x01,       //   Report Size (1)
    0x05, 0x08,       //   Usage Page (LEDs)
    0x19, 0x01,       //   Usage Minimum (Num Lock)
    0x29, 0x05,       //   Usage Maximum (Kana)
    0x91, 0x02,       //   Output (Data, Variable, Absolute)
    0x95, 0x01,       //   Report Count (1)
    0x75, 0x03,       //   Report Size (3)
    0x91, 0x01,       //   Output (Constant)
    0x95, 0x06,       //   Report Count (6)
    0x75, 0x08,       //   Report Size (8)
    0x15, 0x00,       //   Logical Minimum (0)
    0x25, 0x65,       //   Logical Maximum (101)
    0x05, 0x07,       //   Usage Page (Keyboard)
    0x19, 0x00,       //   Usage Minimum (Reserved)
    0x29, 0x65,       //   Usage Maximum (Keyboard Application)
    0x81, 0x00,       //   Input (Data, Array)
    0xC0              // End Collection
};

NTSTATUS
DriverEntry(
    _In_ PDRIVER_OBJECT DriverObject,
    _In_ PUNICODE_STRING RegistryPath
    )
{
    WDF_DRIVER_CONFIG config;
    WDF_DRIVER_CONFIG_INIT(&config, RemoteMicEvtDeviceAdd);
    return WdfDriverCreate(
        DriverObject,
        RegistryPath,
        WDF_NO_OBJECT_ATTRIBUTES,
        &config,
        WDF_NO_HANDLE);
}

NTSTATUS
RemoteMicEvtDeviceAdd(
    _In_ WDFDRIVER Driver,
    _Inout_ PWDFDEVICE_INIT DeviceInit
    )
{
    NTSTATUS status;
    WDFDEVICE device;
    WDF_OBJECT_ATTRIBUTES attributes;
    WDF_IO_QUEUE_CONFIG queueConfig;
    VHF_CONFIG vhfConfig;
    PDEVICE_CONTEXT context;

    UNREFERENCED_PARAMETER(Driver);

    WdfDeviceInitSetDeviceType(DeviceInit, FILE_DEVICE_UNKNOWN);
    WdfDeviceInitSetIoType(DeviceInit, WdfDeviceIoBuffered);
    WDF_OBJECT_ATTRIBUTES_INIT_CONTEXT_TYPE(&attributes, DEVICE_CONTEXT);
    attributes.EvtCleanupCallback = RemoteMicEvtDeviceCleanup;

    status = WdfDeviceCreate(&DeviceInit, &attributes, &device);
    if (!NT_SUCCESS(status)) return status;

    context = RemoteMicGetDeviceContext(device);
    context->VhfHandle = NULL;

    status = WdfDeviceCreateDeviceInterface(
        device,
        &GUID_DEVINTERFACE_REMOTEMIC_VIRTUAL_HID,
        NULL);
    if (!NT_SUCCESS(status)) return status;

    WDF_IO_QUEUE_CONFIG_INIT_DEFAULT_QUEUE(
        &queueConfig,
        WdfIoQueueDispatchSequential);
    queueConfig.EvtIoDeviceControl = RemoteMicEvtIoDeviceControl;
    status = WdfIoQueueCreate(
        device,
        &queueConfig,
        WDF_NO_OBJECT_ATTRIBUTES,
        WDF_NO_HANDLE);
    if (!NT_SUCCESS(status)) return status;

    VHF_CONFIG_INIT(
        &vhfConfig,
        WdfDeviceWdmGetDeviceObject(device),
        (USHORT)sizeof(g_KeyboardReportDescriptor),
        g_KeyboardReportDescriptor);
    vhfConfig.VendorID = 0x1209;
    vhfConfig.ProductID = 0x526D;
    vhfConfig.VersionNumber = 0x0100;

    status = VhfCreate(&vhfConfig, &context->VhfHandle);
    if (!NT_SUCCESS(status)) return status;

    status = VhfStart(context->VhfHandle);
    if (!NT_SUCCESS(status)) {
        VhfDelete(context->VhfHandle, TRUE);
        context->VhfHandle = NULL;
    }
    return status;
}

VOID
RemoteMicEvtIoDeviceControl(
    _In_ WDFQUEUE Queue,
    _In_ WDFREQUEST Request,
    _In_ size_t OutputBufferLength,
    _In_ size_t InputBufferLength,
    _In_ ULONG IoControlCode
    )
{
    NTSTATUS status;
    PREMOTEMIC_KEYBOARD_REPORT report;
    size_t length;
    HID_XFER_PACKET packet;
    PDEVICE_CONTEXT context;

    UNREFERENCED_PARAMETER(OutputBufferLength);

    if (IoControlCode != IOCTL_REMOTEMIC_SUBMIT_KEYBOARD_REPORT) {
        WdfRequestComplete(Request, STATUS_INVALID_DEVICE_REQUEST);
        return;
    }
    if (InputBufferLength != sizeof(REMOTEMIC_KEYBOARD_REPORT)) {
        WdfRequestComplete(Request, STATUS_INVALID_BUFFER_SIZE);
        return;
    }

    status = WdfRequestRetrieveInputBuffer(
        Request,
        sizeof(REMOTEMIC_KEYBOARD_REPORT),
        (PVOID*)&report,
        &length);
    if (!NT_SUCCESS(status)) {
        WdfRequestComplete(Request, status);
        return;
    }

    context = RemoteMicGetDeviceContext(WdfIoQueueGetDevice(Queue));
    if (context->VhfHandle == NULL) {
        WdfRequestComplete(Request, STATUS_DEVICE_NOT_READY);
        return;
    }

    packet.reportBuffer = (PUCHAR)report;
    packet.reportBufferLen = (ULONG)sizeof(*report);
    packet.reportId = 0;
    status = VhfReadReportSubmit(context->VhfHandle, &packet);
    WdfRequestCompleteWithInformation(
        Request,
        status,
        NT_SUCCESS(status) ? sizeof(*report) : 0);
}

VOID
RemoteMicEvtDeviceCleanup(_In_ WDFOBJECT DeviceObject)
{
    PDEVICE_CONTEXT context = RemoteMicGetDeviceContext((WDFDEVICE)DeviceObject);
    if (context->VhfHandle != NULL) {
        VhfDelete(context->VhfHandle, TRUE);
        context->VhfHandle = NULL;
    }
}
