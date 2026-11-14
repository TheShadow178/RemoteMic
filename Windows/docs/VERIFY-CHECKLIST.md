# Windows hardware verification checklist

## Clean-machine setup

- [ ] Windows 10 22H2 x64
- [ ] Windows 11 current x64
- [ ] Self-contained package starts without a preinstalled .NET runtime
- [ ] Simplified Chinese and English both render without clipped text
- [ ] Light/dark themes and 100%, 150%, 200% DPI remain usable
- [ ] Start-at-login and tray restart/quit work without elevation

## Bluetooth and ATVV

- [ ] Pair original Chromecast Voice Remote (`18D1:9450`)
- [ ] Pair X6 Remote (`1D5A:C081`)
- [ ] ATVV 0.4 / 8 kHz fixture or device
- [ ] ATVV 1.0 / 16 kHz original G9N9N
- [ ] capabilities, AUDIO_SYNC-before-start and AUDIO_SYNC-after-start
- [ ] short-press toggle, long-press hold/release and repeated START_SEARCH
- [ ] Chromecast first short tap reopens a persistent stream; second tap closes once
- [ ] X6 reason-zero first tap opens once; its first HID completion does not immediately close
- [ ] sleep/wake, Bluetooth off/on and out-of-range reconnect
- [ ] two remotes connected; stopping one does not stop the other's stream

## Audio

- [ ] `CABLE Input` is detected when VB-CABLE is installed
- [ ] target app receives `CABLE Output`
- [ ] remote audio is decoded at the correct pitch and sample rate
- [ ] remote is +20 dB and does not clip normal speech
- [ ] remote-active path suppresses local mic; stop falls back immediately
- [ ] backlog remains under 250 ms after renderer stalls
- [ ] mute, solo and always-on controls match the UI
- [ ] idle app releases capture and shows no microphone privacy indicator
- [ ] target-recording activity monitor confirms real app start/stop instead of intended state

## HID and mappings

- [ ] REV 011b 3-byte reports: all 14 buttons and release edges
- [ ] REV 0110 direct reports: all available buttons and release edges
- [ ] single-only mappings have no 280 ms delay
- [ ] double/long timing is 280 ms / 600 ms
- [ ] keyboard, media, scroll, fixed text, app launch and sequences
- [ ] fixed text restores the clipboard only if the user did not change it
- [ ] preview clearly reports observe-only mode
- [ ] signed-filter build suppresses only configured mappings and fails open

## Release blockers

- [ ] virtual audio driver attestation/WHQL signature
- [ ] HID filter attestation/WHQL signature
- [ ] clean install, upgrade, rollback and uninstall
- [ ] Defender/SmartScreen scan and Driver Verifier stress run
- [ ] installer and release asset SHA-256 published
- [ ] EXE/installer Authenticode signature and SmartScreen reputation
