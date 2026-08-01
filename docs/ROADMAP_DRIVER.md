# Roadmap for a Windows virtual audio endpoint

This file documents a possible future stage. It is not included as a binary
because a kernel driver must be compiled, signed, and tested on real Windows
hardware.

## Proposed architecture

1. Windows Audio Engine
2. SysVAD-based virtual WaveRT endpoint
3. Shared buffer or audio process
4. User-mode service
5. Opus 48 kHz stereo / 10 ms
6. DualSense Bluetooth HID report
7. Controller headset jack

## Stages

1. Build the official SysVAD sample without modifications.
2. Install it only on a dedicated test machine with test signing.
3. Reduce the sample to one 48 kHz stereo endpoint.
4. Implement safe PCM transport to the user-mode service.
5. Reuse this prototype's encoder and HID transport.
6. Implement reconnection, underflow, overflow, and clock-drift handling.
7. Test multiple DualSense firmware versions and hardware revisions.
8. Consider signed distribution only after extensive testing.

## Why it is not included

x64 drivers require a signature to load normally. Driver development should use
Microsoft's documented testing workflow and, ideally, separate development and
test computers. A kernel-mode defect can destabilize Windows or prevent normal
startup.
