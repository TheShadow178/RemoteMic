# Windows driver boundary

RemoteMic deliberately keeps kernel code outside the desktop application. The
application is usable with an external virtual cable and Raw Input where Windows
exposes the remote, but complete feature parity needs Microsoft-signed drivers.

## Virtual audio capture driver

Required production behavior:

- capture endpoint display name: `RemoteMic 2ch`;
- one 48 kHz, 16-bit, stereo PCM format at minimum;
- a private, ACL-restricted device interface for the RemoteMic desktop app to
  write bounded PCM packets;
- silence on underrun, drop-oldest on overflow and no unbounded kernel buffer;
- cleanup when the client exits and no always-on microphone indicator while
  the endpoint is idle.

The implementation should start from Microsoft's current SysVAD or Simple Audio
Sample, keep third-party notices, undergo Driver Verifier/HLK stress testing and
be submitted for Microsoft attestation or WHQL signing. A test-signed build must
never be distributed to normal users.

## Chromecast HID filter

The Windows tree now contains two x64 driver projects:

- `RemoteMic.HidFilter`: a device-specific upper filter extension for the
  Chromecast Consumer Control collection. It copies completed reports to a
  sideband raw PDO and forwards the original read unchanged.
- `RemoteMic.VirtualHid`: a root-enumerated VHF boot keyboard used for target
  applications, such as input methods, that reject `SendInput` events.

Build both with `Windows/build-drivers.ps1 -CodeAnalysis`. Package unsigned
test outputs with `Windows/package-test-drivers.ps1`; create a local development
certificate and sign them with `Windows/sign-test-drivers.ps1`.

For an explicitly designated development machine only, the staged sequence is:

1. Run `Windows/enable-driver-test-mode.ps1 -ConfirmRisk` from elevated
   PowerShell, then reboot. The script refuses to alter Secure Boot.
2. Run `Windows/install-test-drivers.ps1 -ConfirmRisk` from elevated PowerShell,
   then reboot again.
3. Verify the remote and run Driver Verifier against only
   `RemoteMicVirtualHid.sys` and `RemoteMicHidFilter.sys`—never `/all`.

These packages are development artifacts only. Do not distribute or install
them on normal user machines until they pass Driver Verifier/HLK and Microsoft
attestation or WHQL signing. Enabling Windows test-signing changes the machine's
boot security posture and must be an explicit tester action.

Required production behavior:

- bind only to `VID_18D1&PID_9450` and the intended HID collections;
- parse report descriptors instead of assuming one fixed report length;
- normalize both known layouts:
  - REV 011b: `[02][usageLo][usageHi]` Consumer Control;
  - REV 0110: direct usage followed by zero padding;
- forward reports unchanged while mapping is disabled;
- when enabled, copy the normalized edge to a least-privilege user-mode queue
  and clear only configured mapped usages before forwarding;
- fail open: app/driver failure restores the original HID behavior;
- optionally expose a VHF virtual keyboard for input methods that reject
  `SendInput` events.

The preview's `RawInputRemoteSource` implements the same logical event boundary
without suppressing reports, allowing the UI, mappings and protocol layer to be
reviewed and tested before signed-driver delivery.
