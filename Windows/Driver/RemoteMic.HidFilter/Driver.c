#include <ntddk.h>
#include <wdf.h>
#include <hidport.h>
#include "public.h"

DRIVER_INITIALIZE DriverEntry;
EVT_WDF_DRIVER_DEVICE_ADD RemoteMicFilterEvtDeviceAdd;
EVT_WDF_OBJECT_CONTEXT_CLEANUP RemoteMicFilterEvtCleanup;
EVT_WDF_IO_QUEUE_IO_READ RemoteMicFilterEvtIoRead;
EVT_WDF_IO_QUEUE_IO_DEVICE_CONTROL RemoteMicFilterEvtIoDeviceControl;
EVT_WDF_IO_QUEUE_IO_INTERNAL_DEVICE_CONTROL RemoteMicFilterEvtIoInternalDeviceControl;
EVT_WDF_REQUEST_COMPLETION_ROUTINE RemoteMicFilterReadCompletion;
EVT_WDF_REQUEST_COMPLETION_ROUTINE RemoteMicFilterExpandedReadCompletion;
EVT_WDF_IO_QUEUE_IO_DEVICE_CONTROL RemoteMicControlEvtIoDeviceControl;

typedef struct _DRIVER_CONTEXT {
    WDFSPINLOCK ReportLock;
    WDFWAITLOCK ControlLock;
    WDFDEVICE ControlDevice;
    volatile LONG ActiveDevices;
    ULONG Sequence;
    ULONG Length;
    UCHAR Data[REMOTEMIC_MAX_REPORT_BYTES];
    REMOTEMIC_FILTER_DIAGNOSTICS Diagnostics;
} DRIVER_CONTEXT, *PDRIVER_CONTEXT;

typedef struct _DEVICE_CONTEXT {
    ULONG InstanceIndex;
    BOOLEAN Counted;
} DEVICE_CONTEXT, *PDEVICE_CONTEXT;

typedef struct _REQUEST_CONTEXT {
    WDFDEVICE Device;
    ULONG RequestKind;
    ULONG Ioctl;
} REQUEST_CONTEXT, *PREQUEST_CONTEXT;

typedef struct _EXPANDED_READ_CONTEXT {
    WDFREQUEST OriginalRequest;
    WDFDEVICE Device;
    WDFMEMORY BufferMemory;
} EXPANDED_READ_CONTEXT, *PEXPANDED_READ_CONTEXT;

WDF_DECLARE_CONTEXT_TYPE_WITH_NAME(DRIVER_CONTEXT, RemoteMicGetDriverContext);
WDF_DECLARE_CONTEXT_TYPE_WITH_NAME(DEVICE_CONTEXT, RemoteMicGetDeviceContext);
WDF_DECLARE_CONTEXT_TYPE_WITH_NAME(REQUEST_CONTEXT, RemoteMicGetRequestContext);
WDF_DECLARE_CONTEXT_TYPE_WITH_NAME(
    EXPANDED_READ_CONTEXT,
    RemoteMicGetExpandedReadContext);

static NTSTATUS RemoteMicCreateControlDevice(_In_ WDFDRIVER Driver);
static BOOLEAN RemoteMicTranslateDirectReport(
    _Inout_updates_bytes_(Length) PUCHAR Buffer,
    _In_ size_t Length);
static NTSTATUS RemoteMicTrackRequest(
    _In_ WDFREQUEST Request,
    _In_ WDFDEVICE Device,
    _In_ ULONG RequestKind,
    _In_ ULONG Ioctl);
static NTSTATUS RemoteMicSendExpandedRead(
    _In_ WDFREQUEST OriginalRequest,
    _In_ WDFDEVICE Device);

NTSTATUS
DriverEntry(
    _In_ PDRIVER_OBJECT DriverObject,
    _In_ PUNICODE_STRING RegistryPath
    )
{
    NTSTATUS status;
    WDFDRIVER driver;
    WDF_DRIVER_CONFIG config;
    WDF_OBJECT_ATTRIBUTES attributes;
    WDF_OBJECT_ATTRIBUTES lockAttributes;
    PDRIVER_CONTEXT context;

    WDF_DRIVER_CONFIG_INIT(&config, RemoteMicFilterEvtDeviceAdd);
    WDF_OBJECT_ATTRIBUTES_INIT_CONTEXT_TYPE(&attributes, DRIVER_CONTEXT);
    status = WdfDriverCreate(
        DriverObject,
        RegistryPath,
        &attributes,
        &config,
        &driver);
    if (!NT_SUCCESS(status)) return status;

    context = RemoteMicGetDriverContext(driver);
    context->ControlDevice = NULL;
    context->ActiveDevices = 0;
    context->Sequence = 0;
    context->Length = 0;
    RtlZeroMemory(context->Data, sizeof(context->Data));
    RtlZeroMemory(&context->Diagnostics, sizeof(context->Diagnostics));
    context->Diagnostics.Version = 1;

    WDF_OBJECT_ATTRIBUTES_INIT(&lockAttributes);
    lockAttributes.ParentObject = driver;
    status = WdfSpinLockCreate(&lockAttributes, &context->ReportLock);
    if (!NT_SUCCESS(status)) return status;

    WDF_OBJECT_ATTRIBUTES_INIT(&lockAttributes);
    lockAttributes.ParentObject = driver;
    return WdfWaitLockCreate(&lockAttributes, &context->ControlLock);
}

NTSTATUS
RemoteMicFilterEvtDeviceAdd(
    _In_ WDFDRIVER Driver,
    _Inout_ PWDFDEVICE_INIT DeviceInit
    )
{
    NTSTATUS status;
    WDFDEVICE device;
    WDF_OBJECT_ATTRIBUTES attributes;
    WDF_IO_QUEUE_CONFIG queueConfig;
    PDRIVER_CONTEXT context = RemoteMicGetDriverContext(Driver);

    WdfFdoInitSetFilter(DeviceInit);
    WDF_OBJECT_ATTRIBUTES_INIT_CONTEXT_TYPE(&attributes, DEVICE_CONTEXT);
    attributes.EvtCleanupCallback = RemoteMicFilterEvtCleanup;
    status = WdfDeviceCreate(&DeviceInit, &attributes, &device);
    if (!NT_SUCCESS(status)) {
        return status;
    }

    WDF_IO_QUEUE_CONFIG_INIT_DEFAULT_QUEUE(
        &queueConfig,
        WdfIoQueueDispatchParallel);
    queueConfig.EvtIoRead = RemoteMicFilterEvtIoRead;
    queueConfig.EvtIoDeviceControl = RemoteMicFilterEvtIoDeviceControl;
    queueConfig.EvtIoInternalDeviceControl =
        RemoteMicFilterEvtIoInternalDeviceControl;
    status = WdfIoQueueCreate(
        device,
        &queueConfig,
        WDF_NO_OBJECT_ATTRIBUTES,
        WDF_NO_HANDLE);
    if (!NT_SUCCESS(status)) {
        return status;
    }

    WdfSpinLockAcquire(context->ReportLock);
    context->Diagnostics.DeviceAdds++;
    RemoteMicGetDeviceContext(device)->InstanceIndex =
        context->Diagnostics.DeviceAdds;
    RemoteMicGetDeviceContext(device)->Counted = TRUE;
    WdfSpinLockRelease(context->ReportLock);
    InterlockedIncrement(&context->ActiveDevices);

    // Sideband setup must never prevent the HID stack from starting.
    (void)RemoteMicCreateControlDevice(Driver);
    return STATUS_SUCCESS;
}

VOID
RemoteMicFilterEvtIoDeviceControl(
    _In_ WDFQUEUE Queue,
    _In_ WDFREQUEST Request,
    _In_ size_t OutputBufferLength,
    _In_ size_t InputBufferLength,
    _In_ ULONG IoControlCode
    )
{
    BOOLEAN sent;
    WDFDEVICE device = WdfIoQueueGetDevice(Queue);
    PDRIVER_CONTEXT context =
        RemoteMicGetDriverContext(WdfDeviceGetDriver(device));

    UNREFERENCED_PARAMETER(OutputBufferLength);
    UNREFERENCED_PARAMETER(InputBufferLength);

    WdfSpinLockAcquire(context->ReportLock);
    context->Diagnostics.DeviceControls++;
    context->Diagnostics.LastIoctl = IoControlCode;
    WdfSpinLockRelease(context->ReportLock);

    if (IoControlCode == IOCTL_HID_READ_REPORT) {
        NTSTATUS expandedStatus =
            RemoteMicSendExpandedRead(Request, device);
        if (NT_SUCCESS(expandedStatus)) {
            return;
        }
    }

    WdfRequestFormatRequestUsingCurrentType(Request);
    (void)RemoteMicTrackRequest(Request, device, 2, IoControlCode);
    sent = WdfRequestSend(
        Request,
        WdfDeviceGetIoTarget(device),
        WDF_NO_SEND_OPTIONS);
    if (!sent) {
        WdfRequestComplete(Request, WdfRequestGetStatus(Request));
    }
}

static NTSTATUS
RemoteMicSendExpandedRead(
    _In_ WDFREQUEST OriginalRequest,
    _In_ WDFDEVICE Device
    )
{
    NTSTATUS status;
    WDFREQUEST lowerRequest = NULL;
    WDFMEMORY memory = NULL;
    WDF_OBJECT_ATTRIBUTES requestAttributes;
    WDF_OBJECT_ATTRIBUTES memoryAttributes;
    PEXPANDED_READ_CONTEXT expandedContext;
    WDFIOTARGET target = WdfDeviceGetIoTarget(Device);
    BOOLEAN sent;

    WDF_OBJECT_ATTRIBUTES_INIT_CONTEXT_TYPE(
        &requestAttributes,
        EXPANDED_READ_CONTEXT);
    status = WdfRequestCreate(
        &requestAttributes,
        target,
        &lowerRequest);
    if (!NT_SUCCESS(status)) {
        return status;
    }

    WDF_OBJECT_ATTRIBUTES_INIT(&memoryAttributes);
    memoryAttributes.ParentObject = lowerRequest;
    status = WdfMemoryCreate(
        &memoryAttributes,
        NonPagedPoolNx,
        'cMiR',
        REMOTEMIC_MAX_REPORT_BYTES,
        &memory,
        NULL);
    if (!NT_SUCCESS(status)) {
        WdfObjectDelete(lowerRequest);
        return status;
    }

    status = WdfIoTargetFormatRequestForIoctl(
        target,
        lowerRequest,
        IOCTL_HID_READ_REPORT,
        NULL,
        NULL,
        memory,
        NULL);
    if (!NT_SUCCESS(status)) {
        WdfObjectDelete(lowerRequest);
        return status;
    }

    expandedContext = RemoteMicGetExpandedReadContext(lowerRequest);
    expandedContext->OriginalRequest = OriginalRequest;
    expandedContext->Device = Device;
    expandedContext->BufferMemory = memory;
    WdfRequestSetCompletionRoutine(
        lowerRequest,
        RemoteMicFilterExpandedReadCompletion,
        expandedContext);
    sent = WdfRequestSend(lowerRequest, target, WDF_NO_SEND_OPTIONS);
    if (!sent) {
        status = WdfRequestGetStatus(lowerRequest);
        WdfObjectDelete(lowerRequest);
        return status;
    }

    return STATUS_SUCCESS;
}

VOID
RemoteMicFilterExpandedReadCompletion(
    _In_ WDFREQUEST Request,
    _In_ WDFIOTARGET Target,
    _In_ PWDF_REQUEST_COMPLETION_PARAMS CompletionParams,
    _In_ WDFCONTEXT Context
    )
{
    PEXPANDED_READ_CONTEXT expandedContext =
        (PEXPANDED_READ_CONTEXT)Context;
    WDFREQUEST originalRequest = expandedContext->OriginalRequest;
    WDFDEVICE device = expandedContext->Device;
    WDFDRIVER driver = WdfDeviceGetDriver(device);
    PDRIVER_CONTEXT driverContext = RemoteMicGetDriverContext(driver);
    PDEVICE_CONTEXT deviceContext = RemoteMicGetDeviceContext(device);
    NTSTATUS lowerStatus = CompletionParams->IoStatus.Status;
    ULONG_PTR lowerInformation = CompletionParams->IoStatus.Information;
    PUCHAR expandedBuffer = NULL;
    size_t expandedLength = 0;
    PUCHAR originalBuffer = NULL;
    size_t originalLength = 0;
    NTSTATUS retrieveStatus;
    NTSTATUS completionStatus = lowerStatus;
    ULONG_PTR completionInformation = lowerInformation;
    size_t capturedLength = 0;
    BOOLEAN translated = FALSE;

    UNREFERENCED_PARAMETER(Target);

    expandedBuffer = (PUCHAR)WdfMemoryGetBuffer(
        expandedContext->BufferMemory,
        &expandedLength);
    if (lowerInformation > 0 &&
        (size_t)lowerInformation < expandedLength) {
        capturedLength = (size_t)lowerInformation;
    }
    else {
        capturedLength = expandedLength;
    }

    retrieveStatus = WdfRequestRetrieveOutputBuffer(
        originalRequest,
        3,
        (PVOID*)&originalBuffer,
        &originalLength);
    if (NT_SUCCESS(lowerStatus) &&
        NT_SUCCESS(retrieveStatus) &&
        originalLength >= 3 &&
        expandedBuffer != NULL &&
        capturedLength >= 3) {
        translated = RemoteMicTranslateDirectReport(
            expandedBuffer,
            capturedLength);
        if (translated) {
            RtlCopyMemory(originalBuffer, expandedBuffer, 3);
            completionStatus = STATUS_SUCCESS;
            completionInformation = 3;
        }
    }

    WdfSpinLockAcquire(driverContext->ReportLock);
    driverContext->Diagnostics.Completions++;
    driverContext->Diagnostics.LastRequestType = 4;
    driverContext->Diagnostics.LastDeviceIndex =
        deviceContext->InstanceIndex;
    driverContext->Diagnostics.LastIoctl = IOCTL_HID_READ_REPORT;
    driverContext->Diagnostics.LastStatus = (ULONG)lowerStatus;
    driverContext->Diagnostics.LastInformation =
        (ULONG)lowerInformation;
    driverContext->Diagnostics.LastRetrieveStatus =
        (ULONG)retrieveStatus;
    driverContext->Diagnostics.LastBufferLength =
        (ULONG)capturedLength;
    RtlZeroMemory(
        driverContext->Diagnostics.LastData,
        sizeof(driverContext->Diagnostics.LastData));
    if (expandedBuffer != NULL && capturedLength > 0) {
        size_t copyLength = capturedLength;
        if (copyLength > sizeof(driverContext->Diagnostics.LastData)) {
            copyLength = sizeof(driverContext->Diagnostics.LastData);
        }
        RtlCopyMemory(
            driverContext->Diagnostics.LastData,
            expandedBuffer,
            copyLength);
        RtlCopyMemory(driverContext->Data, expandedBuffer, copyLength);
        driverContext->Length = (ULONG)copyLength;
        driverContext->Sequence++;
        if (driverContext->Sequence == 0) {
            driverContext->Sequence = 1;
        }
    }
    WdfSpinLockRelease(driverContext->ReportLock);

    if (!translated && NT_SUCCESS(lowerStatus)) {
        completionStatus = STATUS_INVALID_DEVICE_REQUEST;
        completionInformation = 0;
    }
    WdfRequestCompleteWithInformation(
        originalRequest,
        completionStatus,
        completionInformation);
    WdfObjectDelete(Request);
}

VOID
RemoteMicFilterEvtIoInternalDeviceControl(
    _In_ WDFQUEUE Queue,
    _In_ WDFREQUEST Request,
    _In_ size_t OutputBufferLength,
    _In_ size_t InputBufferLength,
    _In_ ULONG IoControlCode
    )
{
    BOOLEAN sent;
    WDFDEVICE device = WdfIoQueueGetDevice(Queue);
    PDRIVER_CONTEXT context =
        RemoteMicGetDriverContext(WdfDeviceGetDriver(device));

    UNREFERENCED_PARAMETER(OutputBufferLength);
    UNREFERENCED_PARAMETER(InputBufferLength);

    WdfSpinLockAcquire(context->ReportLock);
    context->Diagnostics.InternalControls++;
    context->Diagnostics.LastIoctl = IoControlCode;
    WdfSpinLockRelease(context->ReportLock);

    WdfRequestFormatRequestUsingCurrentType(Request);
    (void)RemoteMicTrackRequest(Request, device, 3, IoControlCode);
    sent = WdfRequestSend(
        Request,
        WdfDeviceGetIoTarget(device),
        WDF_NO_SEND_OPTIONS);
    if (!sent) {
        WdfRequestComplete(Request, WdfRequestGetStatus(Request));
    }
}

VOID
RemoteMicFilterEvtCleanup(_In_ WDFOBJECT DeviceObject)
{
    WDFDRIVER driver = WdfDeviceGetDriver((WDFDEVICE)DeviceObject);
    PDRIVER_CONTEXT context = RemoteMicGetDriverContext(driver);
    PDEVICE_CONTEXT deviceContext =
        RemoteMicGetDeviceContext((WDFDEVICE)DeviceObject);
    WDFDEVICE controlDevice = NULL;

    if (!deviceContext->Counted) {
        return;
    }
    deviceContext->Counted = FALSE;
    if (InterlockedDecrement(&context->ActiveDevices) != 0) {
        return;
    }

    WdfWaitLockAcquire(context->ControlLock, NULL);
    controlDevice = context->ControlDevice;
    context->ControlDevice = NULL;
    WdfWaitLockRelease(context->ControlLock);

    if (controlDevice != NULL) {
        WdfObjectDelete(controlDevice);
    }
}

VOID
RemoteMicFilterEvtIoRead(
    _In_ WDFQUEUE Queue,
    _In_ WDFREQUEST Request,
    _In_ size_t Length
    )
{
    BOOLEAN sent;
    WDFDEVICE device = WdfIoQueueGetDevice(Queue);
    PDRIVER_CONTEXT context =
        RemoteMicGetDriverContext(WdfDeviceGetDriver(device));

    UNREFERENCED_PARAMETER(Length);

    WdfSpinLockAcquire(context->ReportLock);
    context->Diagnostics.Reads++;
    WdfSpinLockRelease(context->ReportLock);

    WdfRequestFormatRequestUsingCurrentType(Request);
    (void)RemoteMicTrackRequest(Request, device, 1, 0);
    sent = WdfRequestSend(
        Request,
        WdfDeviceGetIoTarget(device),
        WDF_NO_SEND_OPTIONS);
    if (!sent) {
        WdfRequestComplete(Request, WdfRequestGetStatus(Request));
    }
}

VOID
RemoteMicFilterReadCompletion(
    _In_ WDFREQUEST Request,
    _In_ WDFIOTARGET Target,
    _In_ PWDF_REQUEST_COMPLETION_PARAMS CompletionParams,
    _In_ WDFCONTEXT Context
    )
{
    NTSTATUS status = CompletionParams->IoStatus.Status;
    ULONG_PTR information = CompletionParams->IoStatus.Information;
    PREQUEST_CONTEXT requestContext = (PREQUEST_CONTEXT)Context;
    WDFDEVICE device = requestContext->Device;
    WDFDRIVER driver = WdfDeviceGetDriver(device);
    PDRIVER_CONTEXT context = RemoteMicGetDriverContext(driver);
    PDEVICE_CONTEXT deviceContext = RemoteMicGetDeviceContext(device);
    PUCHAR buffer = NULL;
    size_t bufferLength = 0;
    size_t capturedLength = 0;
    PHID_XFER_PACKET transferPacket = NULL;
    NTSTATUS retrieveStatus = STATUS_UNSUCCESSFUL;
    ULONG ioctl = requestContext->Ioctl;
    BOOLEAN isInputReport = FALSE;

    UNREFERENCED_PARAMETER(Target);

    isInputReport = requestContext->RequestKind == 1 ||
        ioctl == IOCTL_HID_READ_REPORT ||
        ioctl == IOCTL_HID_GET_INPUT_REPORT;

    if (NT_SUCCESS(status) && ioctl == IOCTL_HID_GET_INPUT_REPORT) {
        retrieveStatus = WdfRequestRetrieveOutputBuffer(
            Request,
            sizeof(HID_XFER_PACKET),
            (PVOID*)&transferPacket,
            &bufferLength);
        if (NT_SUCCESS(retrieveStatus) &&
            transferPacket->reportBuffer != NULL &&
            transferPacket->reportBufferLen > 0) {
            buffer = transferPacket->reportBuffer;
            capturedLength = transferPacket->reportBufferLen;
            bufferLength = capturedLength;
        }
    }
    else if (isInputReport || (NT_SUCCESS(status) && information > 0)) {
        retrieveStatus = WdfRequestRetrieveOutputBuffer(
            Request,
            1,
            (PVOID*)&buffer,
            &bufferLength);
        if (NT_SUCCESS(retrieveStatus)) {
            capturedLength = bufferLength;
            if (information > 0 && capturedLength > (size_t)information) {
                capturedLength = (size_t)information;
            }
        }
    }

    if (status == STATUS_BUFFER_TOO_SMALL &&
        ioctl == IOCTL_HID_READ_REPORT &&
        NT_SUCCESS(retrieveStatus) &&
        RemoteMicTranslateDirectReport(buffer, bufferLength)) {
        status = STATUS_SUCCESS;
        information = 3;
        capturedLength = 3;
    }

    WdfSpinLockAcquire(context->ReportLock);
    context->Diagnostics.Completions++;
    context->Diagnostics.LastRequestType = requestContext->RequestKind;
    context->Diagnostics.LastDeviceIndex = deviceContext->InstanceIndex;
    context->Diagnostics.LastIoctl = ioctl;
    context->Diagnostics.LastStatus = (ULONG)status;
    context->Diagnostics.LastInformation = (ULONG)information;
    context->Diagnostics.LastRetrieveStatus = (ULONG)retrieveStatus;
    context->Diagnostics.LastBufferLength = (ULONG)capturedLength;
    RtlZeroMemory(
        context->Diagnostics.LastData,
        sizeof(context->Diagnostics.LastData));

    if (NT_SUCCESS(retrieveStatus) && buffer != NULL && capturedLength > 0) {
        size_t copyLength = capturedLength;
        if (copyLength > sizeof(context->Data)) {
            copyLength = sizeof(context->Data);
        }
        __analysis_assume(copyLength <= bufferLength);
        __analysis_assume(copyLength <= sizeof(context->Data));
        RtlCopyMemory(context->Diagnostics.LastData, buffer, copyLength);
        if (isInputReport) {
            RtlCopyMemory(context->Data, buffer, copyLength);
            context->Length = (ULONG)copyLength;
            context->Sequence++;
            if (context->Sequence == 0) context->Sequence = 1;
        }
    }
    WdfSpinLockRelease(context->ReportLock);

    WdfRequestCompleteWithInformation(Request, status, information);
}

static NTSTATUS
RemoteMicTrackRequest(
    _In_ WDFREQUEST Request,
    _In_ WDFDEVICE Device,
    _In_ ULONG RequestKind,
    _In_ ULONG Ioctl
    )
{
    NTSTATUS status;
    PREQUEST_CONTEXT requestContext = NULL;
    WDF_OBJECT_ATTRIBUTES attributes;

    WDF_OBJECT_ATTRIBUTES_INIT_CONTEXT_TYPE(&attributes, REQUEST_CONTEXT);
    status = WdfObjectAllocateContext(
        Request,
        &attributes,
        (PVOID*)&requestContext);
    if (status == STATUS_OBJECT_NAME_EXISTS) {
        requestContext = RemoteMicGetRequestContext(Request);
        status = STATUS_SUCCESS;
    }
    if (!NT_SUCCESS(status)) {
        return status;
    }

    requestContext->Device = Device;
    requestContext->RequestKind = RequestKind;
    requestContext->Ioctl = Ioctl;
    WdfRequestSetCompletionRoutine(
        Request,
        RemoteMicFilterReadCompletion,
        requestContext);
    return STATUS_SUCCESS;
}

static BOOLEAN
RemoteMicTranslateDirectReport(
    _Inout_updates_bytes_(Length) PUCHAR Buffer,
    _In_ size_t Length
    )
{
    USHORT usage;

    if (Buffer == NULL || Length < 3 || Buffer[0] != 0x01) {
        return FALSE;
    }

    switch (Buffer[1]) {
    case 0x00: usage = 0x0000; break;
    case 0x01: usage = 0x019E; break;
    case 0x03: usage = 0x0042; break;
    case 0x04: usage = 0x0043; break;
    case 0x05: usage = 0x0044; break;
    case 0x06: usage = 0x0045; break;
    case 0x07: usage = 0x0041; break;
    case 0x08: usage = 0x00E2; break;
    case 0x0A: usage = 0x0223; break;
    case 0x0B: usage = 0x0224; break;
    case 0x0C: usage = 0x00E9; break;
    case 0x0D: usage = 0x00EA; break;
    case 0x0E: usage = 0x0077; break;
    case 0x0F: usage = 0x0078; break;
    case 0x11: usage = 0x0089; break;
    default: return FALSE;
    }

    Buffer[1] = (UCHAR)(usage & 0xFF);
    Buffer[2] = (UCHAR)(usage >> 8);
    return TRUE;
}

VOID
RemoteMicControlEvtIoDeviceControl(
    _In_ WDFQUEUE Queue,
    _In_ WDFREQUEST Request,
    _In_ size_t OutputBufferLength,
    _In_ size_t InputBufferLength,
    _In_ ULONG IoControlCode
    )
{
    NTSTATUS status;
    PREMOTEMIC_FILTER_REPORT report;
    size_t length;
    WDFDRIVER driver = WdfDeviceGetDriver(WdfIoQueueGetDevice(Queue));
    PDRIVER_CONTEXT context = RemoteMicGetDriverContext(driver);

    UNREFERENCED_PARAMETER(InputBufferLength);

    if (IoControlCode == IOCTL_REMOTEMIC_GET_DIAGNOSTICS) {
        PREMOTEMIC_FILTER_DIAGNOSTICS diagnostics;
        if (OutputBufferLength < sizeof(REMOTEMIC_FILTER_DIAGNOSTICS)) {
            WdfRequestComplete(Request, STATUS_BUFFER_TOO_SMALL);
            return;
        }
        status = WdfRequestRetrieveOutputBuffer(
            Request,
            sizeof(REMOTEMIC_FILTER_DIAGNOSTICS),
            (PVOID*)&diagnostics,
            &length);
        if (!NT_SUCCESS(status)) {
            WdfRequestComplete(Request, status);
            return;
        }
        __analysis_assume(length >= sizeof(REMOTEMIC_FILTER_DIAGNOSTICS));
        WdfSpinLockAcquire(context->ReportLock);
        RtlCopyMemory(
            diagnostics,
            &context->Diagnostics,
            sizeof(context->Diagnostics));
        WdfSpinLockRelease(context->ReportLock);
        WdfRequestCompleteWithInformation(
            Request,
            STATUS_SUCCESS,
            sizeof(REMOTEMIC_FILTER_DIAGNOSTICS));
        return;
    }

    if (IoControlCode != IOCTL_REMOTEMIC_GET_LATEST_REPORT) {
        WdfRequestComplete(Request, STATUS_INVALID_DEVICE_REQUEST);
        return;
    }
    if (OutputBufferLength < sizeof(REMOTEMIC_FILTER_REPORT)) {
        WdfRequestComplete(Request, STATUS_BUFFER_TOO_SMALL);
        return;
    }

    status = WdfRequestRetrieveOutputBuffer(
        Request,
        sizeof(REMOTEMIC_FILTER_REPORT),
        (PVOID*)&report,
        &length);
    if (!NT_SUCCESS(status)) {
        WdfRequestComplete(Request, status);
        return;
    }

    WdfSpinLockAcquire(context->ReportLock);
    report->Sequence = context->Sequence;
    report->Length = context->Length;
    RtlZeroMemory(report->Data, sizeof(report->Data));
    if (context->Length > 0) {
        RtlCopyMemory(report->Data, context->Data, context->Length);
    }
    WdfSpinLockRelease(context->ReportLock);

    WdfRequestCompleteWithInformation(
        Request,
        STATUS_SUCCESS,
        sizeof(REMOTEMIC_FILTER_REPORT));
}

static NTSTATUS
RemoteMicCreateControlDevice(_In_ WDFDRIVER Driver)
{
    NTSTATUS status = STATUS_SUCCESS;
    PWDFDEVICE_INIT deviceInit = NULL;
    WDFDEVICE controlDevice = NULL;
    WDF_IO_QUEUE_CONFIG queueConfig;
    PDRIVER_CONTEXT context = RemoteMicGetDriverContext(Driver);
    DECLARE_CONST_UNICODE_STRING(
        sddl,
        L"D:P(A;;GA;;;SY)(A;;GA;;;BA)(A;;GR;;;IU)");
    DECLARE_CONST_UNICODE_STRING(
        deviceName,
        L"\\Device\\RemoteMicHidFilter");
    DECLARE_CONST_UNICODE_STRING(
        symbolicLink,
        L"\\DosDevices\\RemoteMicHidFilter");

    WdfWaitLockAcquire(context->ControlLock, NULL);
    if (context->ControlDevice != NULL) {
        WdfWaitLockRelease(context->ControlLock);
        return STATUS_SUCCESS;
    }

    deviceInit = WdfControlDeviceInitAllocate(Driver, &sddl);
    if (deviceInit == NULL) {
        status = STATUS_INSUFFICIENT_RESOURCES;
        goto Exit;
    }

    WdfDeviceInitSetExclusive(deviceInit, FALSE);
    WdfDeviceInitSetDeviceType(deviceInit, FILE_DEVICE_UNKNOWN);
    WdfDeviceInitSetIoType(deviceInit, WdfDeviceIoBuffered);
    status = WdfDeviceInitAssignName(deviceInit, &deviceName);
    if (!NT_SUCCESS(status)) goto Exit;

    status = WdfDeviceCreate(
        &deviceInit,
        WDF_NO_OBJECT_ATTRIBUTES,
        &controlDevice);
    if (!NT_SUCCESS(status)) goto Exit;

    status = WdfDeviceCreateSymbolicLink(controlDevice, &symbolicLink);
    if (!NT_SUCCESS(status)) goto Exit;

    WDF_IO_QUEUE_CONFIG_INIT_DEFAULT_QUEUE(
        &queueConfig,
        WdfIoQueueDispatchSequential);
    queueConfig.EvtIoDeviceControl = RemoteMicControlEvtIoDeviceControl;
    status = WdfIoQueueCreate(
        controlDevice,
        &queueConfig,
        WDF_NO_OBJECT_ATTRIBUTES,
        WDF_NO_HANDLE);
    if (!NT_SUCCESS(status)) goto Exit;

    WdfControlFinishInitializing(controlDevice);
    context->ControlDevice = controlDevice;
    controlDevice = NULL;

Exit:
    if (deviceInit != NULL) WdfDeviceInitFree(deviceInit);
    if (controlDevice != NULL) WdfObjectDelete(controlDevice);
    WdfWaitLockRelease(context->ControlLock);
    return status;
}
